using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Wisp
{
    /// <summary>
    /// Tween engine. A background clock paces frames to the display refresh (DWM vblank timing,
    /// 1 ms timer resolution) and posts one frame at a time to the UI thread, so animations run at
    /// an even 60+ fps instead of the jittery 15.6 ms WinForms timer. Progress is computed from the
    /// frame's scheduled time, so speed stays constant even when a frame is late.
    /// </summary>
    static class Anim
    {
        class Tween
        {
            public object Key;
            public double Start;
            public int Duration;
            public Action<float> Step;
            public Action Done;
            public Func<float, float> Ease;
            public Func<bool> Frame;   // continuous callback; returns false to stop
            public bool Dead;
            public bool Started;
        }

        static readonly List<Tween> tweens = new List<Tween>();
        static readonly Dictionary<object, Tween> byKey = new Dictionary<object, Tween>();
        static readonly double TicksPerMs = Stopwatch.Frequency / 1000.0;

        static SynchronizationContext ui;
        static Thread clockThread;
        static readonly AutoResetEvent wake = new AutoResetEvent(false);
        static volatile bool active;
        static int posted;
        static long frameStamp;

        /// <summary>Current time in milliseconds (QPC based).</summary>
        public static double Now { get { return Stopwatch.GetTimestamp() / TicksPerMs; } }

        /// <summary>Runs step(t) with t going 0..1 over ms milliseconds. A new tween with the same key replaces the old one.</summary>
        public static void Run(object key, int ms, Action<float> step, Action done = null, Func<float, float> ease = null)
        {
            Add(new Tween { Key = key, Duration = Math.Max(1, ms), Step = step, Done = done, Ease = ease ?? EaseOut });
        }

        /// <summary>Calls frame() once per display frame until it returns false.</summary>
        public static void Frame(object key, Func<bool> frame)
        {
            Add(new Tween { Key = key, Frame = frame });
        }

        /// <summary>Runs action after ms milliseconds, on the animation clock.</summary>
        public static void Delay(int ms, Action action)
        {
            Run(null, ms, t => { }, action, t => t);
        }

        public static void Cancel(object key)
        {
            Tween old;
            if (byKey.TryGetValue(key, out old))
            {
                old.Dead = true;
                byKey.Remove(key);
            }
        }

        static void Add(Tween tw)
        {
            if (tw.Key != null)
            {
                Cancel(tw.Key);
                byKey[tw.Key] = tw;
            }
            // The clock starts at the tween's first frame, not now: work done between starting an
            // animation and the next frame (snapshots, layout) must not eat into it and make it jump.
            tw.Start = Now;
            tweens.Add(tw);
            EnsureClock();
        }

        static void EnsureClock()
        {
            if (ui == null) ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            if (clockThread == null)
            {
                clockThread = new Thread(ClockLoop) { IsBackground = true, Name = "Wisp animation clock", Priority = ThreadPriority.AboveNormal };
                clockThread.Start();
            }
            if (!active)
            {
                active = true;
                wake.Set();
            }
        }

        static void OnFrame(object state)
        {
            double now = Interlocked.Read(ref frameStamp) / TicksPerMs;
            double began = FrameLog != null ? Now : 0;
            Interlocked.Exchange(ref posted, 0);
            RunTweens(now);
            if (FrameLog != null) Log(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "F\t{0:F1}\t{1:F1}\t{2:F1}\t{3}", now, began - now, Now - began, tweens.Count));
        }

        // ---------- frame log (diagnostics: set WISP_FRAMELOG to a file path) ----------

        /// <summary>Lines: "F stamp lateness work tweens" per frame, plus free-form notes. Null when off.</summary>
        public static readonly string FrameLog = Environment.GetEnvironmentVariable("WISP_FRAMELOG");
        static readonly System.Text.StringBuilder logBuf = new System.Text.StringBuilder();
        static double logFlushed;

        public static void Log(string line)
        {
            if (FrameLog == null) return;
            logBuf.Append(line).Append('\n');
            if (logBuf.Length > 8192 || Now - logFlushed > 1000)
            {
                logFlushed = Now;
                try { System.IO.File.AppendAllText(FrameLog, logBuf.ToString()); } catch { }
                logBuf.Clear();
            }
        }

        static void RunTweens(double now)
        {
            foreach (var tw in tweens.ToArray())
            {
                if (tw.Dead) { tweens.Remove(tw); continue; }
                try
                {
                    if (tw.Frame != null)
                    {
                        if (!tw.Frame()) Finish(tw);
                        continue;
                    }
                    if (!tw.Started)
                    {
                        tw.Started = true;
                        tw.Start = Math.Max(tw.Start, now - FramePeriod);
                    }
                    float t = (float)Math.Max(0, Math.Min(1, (now - tw.Start) / tw.Duration));
                    bool finished = t >= 1f;
                    if (finished) Finish(tw);
                    tw.Step(tw.Ease(t));
                    if (finished && tw.Done != null) tw.Done();
                }
                catch (ObjectDisposedException) { Finish(tw); }
            }
            if (tweens.Count == 0) active = false;
        }

        /// <summary>Last measured display period (ms).</summary>
        static double FramePeriod = 1000.0 / 60;

        static void Finish(Tween tw)
        {
            tw.Dead = true;
            tweens.Remove(tw);
            Tween current;
            if (tw.Key != null && byKey.TryGetValue(tw.Key, out current) && current == tw) byKey.Remove(tw.Key);
        }

        // ---------- frame clock ----------

        [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint period);
        [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint period);
        [DllImport("dwmapi.dll")] static extern int DwmGetCompositionTimingInfo(IntPtr hwnd, byte[] info);

        const int TimingInfoSize = 292; // packed DWM_TIMING_INFO
        static readonly byte[] timing = new byte[TimingInfoSize];

        /// <summary>Last vblank (QPC ticks) and refresh period from DWM; false over RDP or without composition.</summary>
        static bool TryVBlank(out long vblank, out long period)
        {
            vblank = period = 0;
            try
            {
                BitConverter.GetBytes(TimingInfoSize).CopyTo(timing, 0);
                if (DwmGetCompositionTimingInfo(IntPtr.Zero, timing) != 0) return false;
                period = BitConverter.ToInt64(timing, 12);  // qpcRefreshPeriod
                vblank = BitConverter.ToInt64(timing, 28);  // qpcVBlank
                // Sanity: 30..300 Hz.
                return period > Stopwatch.Frequency / 300 && period < Stopwatch.Frequency / 30 && vblank > 0;
            }
            catch { return false; }
        }

        static void ClockLoop()
        {
            bool remote = SystemInformation.TerminalServerSession;
            bool hiRes = false;
            long fallbackPeriod = Stopwatch.Frequency / 60;
            long next = 0;
            while (true)
            {
                if (!active)
                {
                    if (hiRes) { timeEndPeriod(1); hiRes = false; }
                    wake.WaitOne();
                    next = 0;
                    continue;
                }
                if (!hiRes) { timeBeginPeriod(1); hiRes = true; }

                long now = Stopwatch.GetTimestamp();
                long vblank, period;
                // Remote sessions report a ~30 Hz composition clock that RDP re-samples anyway;
                // there an even 60 Hz cadence looks smoother than following DWM.
                if (!remote && TryVBlank(out vblank, out period) && period <= Stopwatch.Frequency / 50)
                {
                    // Fire just after each vblank: the frame then has a whole refresh interval to paint.
                    long offset = (long)TicksPerMs;
                    long k = (now - vblank - offset) / period + 1;
                    next = vblank + offset + k * period;
                }
                else
                {
                    period = fallbackPeriod;
                    if (next <= now) next = now + period;
                }

                FramePeriod = period / TicksPerMs;
                WaitUntil(next);
                Interlocked.Exchange(ref frameStamp, next);
                if (Interlocked.CompareExchange(ref posted, 1, 0) == 0) ui.Post(OnFrame, null);
                next += period;
            }
        }

        static void WaitUntil(long stamp)
        {
            while (true)
            {
                double ms = (stamp - Stopwatch.GetTimestamp()) / TicksPerMs;
                if (ms <= 0) return;
                if (ms > 2) Thread.Sleep((int)(ms - 1.5));
                else Thread.Yield();
            }
        }

        // ---------- helpers ----------

        public static float EaseOut(float t)
        {
            float u = 1 - t;
            return 1 - u * u * u;
        }

        public static float EaseInOut(float t)
        {
            return t < 0.5f ? 4 * t * t * t : 1 - (float)Math.Pow(-2 * t + 2, 3) / 2;
        }

        public static Color Blend(Color a, Color b, float t)
        {
            if (t <= 0) return a;
            if (t >= 1) return b;
            return Color.FromArgb(
                (int)(a.A + (b.A - a.A) * t),
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        public static float Lerp(float a, float b, float t)
        {
            return a + (b - a) * t;
        }
    }

    /// <summary>A float that animates toward a target and calls back on every change (hover, press, focus...).</summary>
    class Tweened
    {
        public float Value;
        readonly Action changed;
        readonly object key = new object();

        public Tweened(Action changed, float initial = 0f)
        {
            this.changed = changed;
            Value = initial;
        }

        public void To(float target, int ms, Action done = null)
        {
            float from = Value;
            if (Math.Abs(from - target) < 0.001f)
            {
                Anim.Cancel(key);
                Value = target;
                if (done != null) done();
                return;
            }
            Anim.Run(key, ms, t => { Value = from + (target - from) * t; changed(); }, done);
        }

        public void Set(float v)
        {
            Anim.Cancel(key);
            Value = v;
            changed();
        }
    }
}
