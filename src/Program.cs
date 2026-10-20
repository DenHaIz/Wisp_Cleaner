using System;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Wisp
{
    static class Program
    {
        /// <summary>
        /// Name of the single-instance lock and of the "show" message. The test build
        /// (WispCleaner.dev.exe) has its own, so it never touches a running release copy.
        /// </summary>
        public static readonly string Instance =
            System.IO.Path.GetFileNameWithoutExtension(Application.ExecutablePath).EndsWith(".dev", StringComparison.OrdinalIgnoreCase)
                ? "WispCleaner.Dev" : "WispCleaner";

        [DllImport("user32.dll")]
        static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// WispCleaner.exe          - open the window
        /// WispCleaner.exe /tray    - start hidden in the tray (used by start-with-Windows)
        /// WispCleaner.exe /clean   - clean the checked categories silently and exit
        /// WispCleaner.exe /tab:N   - open on tab N (1..4); brings up a running instance on that tab
        /// </summary>
        [STAThread]
        static int Main(string[] args)
        {
            bool tray = args.Any(a => a.Equals("/tray", StringComparison.OrdinalIgnoreCase));
            bool clean = args.Any(a => a.Equals("/clean", StringComparison.OrdinalIgnoreCase));
            int tab = 0;
            foreach (string a in args)
                if (a.StartsWith("/tab:", StringComparison.OrdinalIgnoreCase)) int.TryParse(a.Substring(5), out tab);

            Settings.Load();
            L.Init();

            if (clean)
            {
                var r = Cleaner.RunUnattended();
                History.Add(HistoryKind.Auto, r.Freed, string.Join(", ", r.Items));
                return 0;
            }

            bool first;
            using (var mutex = new Mutex(true, @"Local\" + Instance + ".SingleInstance", out first))
            {
                if (!first)
                {
                    // Already running (perhaps hidden in the tray): bring that window up instead.
                    if (!tray) PostMessage((IntPtr)0xFFFF /* HWND_BROADCAST */, MainForm.ShowMessage, (IntPtr)tab, IntPtr.Zero);
                    return 0;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) => ThemedDialog.Info(null, L.T("Ошибка", "Error"), e.Exception.Message, true);

                float scale;
                using (var g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
                Theme.Init(scale);
                Settings.Saved = () => { if (Settings.Autostart) Autostart.CopySettings(); };
                if (Settings.Autostart) new Thread(Autostart.Refresh) { IsBackground = true }.Start();

                // Started from the tray task while auto-clean is off: nothing to do in the background.
                if (tray && !Settings.AutoClean) return 0;
                var form = new MainForm(tray);
                if (tab >= 1 && tab <= 4) form.StartTab = tab - 1;
                Application.Run(form);
            }
            return 0;
        }
    }
}
