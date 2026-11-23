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

        static double lastFrame;

        /// <summary>
        /// The time continuous animations draw for (ms): the display frame being painted, so motion advances by
        /// exactly one refresh per frame however late the paint itself runs. Between animations, the clock.
        /// </summary>
        public static double FrameNow
        {
            get
            {
                double now = Now;
                return now - lastFrame < 100 ? lastFrame : now;
            }
        }

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

        /// <summary>
        /// Moves a value to a target that may keep changing on the way (wheel scrolling), as a critically damped
        /// spring: the speed carries over when the target moves, so fast wheel turns glide instead of surging and
        /// stopping on every notch as a restarted tween did. Called again while running, it just follows the new target.
        /// </summary>
        public static void Follow(object key, Func<float> value, Action<float> set, Func<float> target, float omega = 24f)
        {
            Tween running;
            if (byKey.TryGetValue(key, out running) && running.Frame != null && !running.Dead) return;
            double last = -1;
            float v = 0;
            Frame(key, () =>
            {
                double now = FrameNow;
                float dt = last < 0 ? (float)(FramePeriod / 1000) : (float)Math.Max(0, Math.Min(0.05, (now - last) / 1000));
                last = now;
                // Exact step of x'' = -w²x - 2wx' over dt.
                float x = value() - target(), tmp = (v + omega * x) * dt, e = (float)Math.Exp(-omega * dt);
                float nx = (x + tmp) * e;
                v = (v - omega * tmp) * e;
                if (Math.Abs(nx) < 0.5f && Math.Abs(v) < 20) { set(target()); return false; }
                set(target() + nx);
                return true;
            });
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
                // Whether anything is on screen is known from the last frame, which may be long ago (the
                // window was in the tray): asked again, so a fade-in does not wait for a slow tick first.
                hidden = NothingOnScreen();
                active = true;
                wake.Set();
            }
            else if (hidden && !NothingOnScreen())
            {
                // A window came back while the clock ticks slowly: back to the display rate at once.
                hidden = false;
                wake.Set();
            }
        }

        static void OnFrame(object state)
        {
            double now = Interlocked.Read(ref frameStamp) / TicksPerMs;
            double began = FrameLog != null ? Now : 0;
            Interlocked.Exchange(ref posted, 0);
            lastFrame = now;
            RunTweens(now);
            double painted = FrameLog != null ? Now : 0;
            PaintNow();
            hidden = NothingOnScreen();
            if (FrameLog != null) Log(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "F\t{0:F1}\t{1:F1}\t{2:F1}\t{3}\t{4:F1}", now, began - now, Now - began, tweens.Count, Now - painted));
        }

        // ---------- frame log (diagnostics: set WISP_FRAMELOG to a file path) ----------

        /// <summary>Lines: "F stamp lateness work tweens paint" per frame (paint: the part of work spent in PaintNow), plus free-form notes. Null when off.</summary>
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

        [DllImport("user32.dll")] static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr region, uint flags);

        /// <summary>Every window minimized or hidden: the clock then ticks a few times a second, not at the display rate.</summary>
        static volatile bool hidden;

        /// <summary>The windows that could come back (minimized ones), watched by the clock while it ticks slowly.</summary>
        static volatile IntPtr[] watched = new IntPtr[0];

        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);

        static bool NothingOnScreen()
        {
            foreach (Form f in Application.OpenForms)
                if (f.Visible && f.WindowState != FormWindowState.Minimized) return false;
            var handles = new List<IntPtr>();
            foreach (Form f in Application.OpenForms)
                if (f.IsHandleCreated && !f.IsDisposed) handles.Add(f.Handle);
            watched = handles.ToArray();
            return true;
        }

        /// <summary>A minimized window was restored (asked from the clock thread: plain Win32 calls only).</summary>
        static bool CameBack()
        {
            foreach (var h in watched)
                if (IsWindowVisible(h) && !IsIconic(h)) return true;
            return false;
        }

        /// <summary>
        /// Paints whatever this frame's tweens invalidated, now. WM_PAINT is the lowest-priority message: when a
        /// frame's work (a transition's synchronous overlay paint) takes most of the refresh interval, the next
        /// frame is already queued before Windows gets to it, so a control that only invalidates (the tab
        /// underline) stood still for several frames and then jumped. Painting here keeps every animation in step.
        /// </summary>
        static void PaintNow()
        {
            if (Application.OpenForms.Count == 0) return;
            foreach (Form f in new System.Collections.ArrayList(Application.OpenForms))
                // RDW_UPDATENOW | RDW_ALLCHILDREN: only what is invalid is painted.
                if (f.IsHandleCreated && f.Visible && !f.IsDisposed) RedrawWindow(f.Handle, IntPtr.Zero, IntPtr.Zero, 0x0100 | 0x0080);
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
                catch (Exception ex)
                {
                    // Dropped, reported once, and the other animations go on: left in the list it would throw
                    // again every frame, and the error dialog itself needs the clock to appear and close.
                    Finish(tw);
                    var report = ex;
                    if (ui != null) ui.Post(_ => Application.OnThreadException(report), null);
                }
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
                if (hidden)
                {
                    // Nothing to see (minimized, in the tray): spinners and loaders only need to stay in time,
                    // ~8 ticks a second. A window restored, or a new animation (EnsureClock), ends the wait at once.
                    period = Stopwatch.Frequency / 8;
                    if (next <= now) next = now + period;
                    while (true)
                    {
                        long wait = (next - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency;
                        if (wait <= 0) break;
                        if (wake.WaitOne((int)Math.Min(16, wait))) { next = Stopwatch.GetTimestamp(); break; }
                        if (CameBack()) { hidden = false; next = Stopwatch.GetTimestamp(); break; }
                    }
                    Interlocked.Exchange(ref frameStamp, next);
                    if (Interlocked.CompareExchange(ref posted, 1, 0) == 0) ui.Post(OnFrame, null);
                    // Back at the display rate the next frame is placed from the vblank again.
                    next = 0;
                    continue;
                }
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
            // Sleeps only (1 ms timer resolution): the frame is drawn for its scheduled time anyway, so waking up to
            // a millisecond early costs nothing, while spinning out the last 1.5 ms took ~9 % of a core all along.
            while (true)
            {
                double ms = (stamp - Stopwatch.GetTimestamp()) / TicksPerMs;
                if (ms <= 1) return;
                Thread.Sleep(Math.Max(1, (int)(ms - 0.5)));
            }
        }

        // ---------- helpers ----------

        public static float EaseOut(float t)
        {
            float u = 1 - t;
            return 1 - u * u * u;
        }

        /// <summary>Sine ease in-out: at its fastest only ~1.6× the average speed (cubic: 3×), so the steps stay small.</summary>
        public static float SineInOut(float t)
        {
            return (float)(0.5 - 0.5 * Math.Cos(Math.PI * Math.Max(0, Math.Min(1, t))));
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

        /// <summary>
        /// Whether a control is on screen now: visible, with its window not minimized. Continuous animations
        /// (spinners, loaders) repaint only then — a minimized window kept repainting them for a whole scan.
        /// </summary>
        public static bool Shown(Control c)
        {
            if (c == null || c.IsDisposed || !c.Visible || !c.IsHandleCreated) return false;
            var f = c.FindForm();
            return f == null || f.WindowState != FormWindowState.Minimized;
        }

        /// <summary>
        /// Two texts that differ only in their numbers ("Install (3)" / "Install (4)", "1 of 5" / "2 of 5"):
        /// a counter, which just changes — fading it out and back in on every tick read as blinking.
        /// </summary>
        public static bool OnlyNumbersDiffer(string a, string b)
        {
            if (a == null || b == null || a.Length == 0 || b.Length == 0) return false;
            return System.Text.RegularExpressions.Regex.Replace(a, @"[\d\s.,]+", "#") == System.Text.RegularExpressions.Regex.Replace(b, @"[\d\s.,]+", "#");
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
