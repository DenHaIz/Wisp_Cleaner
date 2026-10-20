using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace Wisp
{
    /// <summary>
    /// "Start with Windows" as a Task Scheduler task: a requireAdministrator exe in the Run key is
    /// blocked at sign-in, a task with highest privileges is not. The task points at wherever the
    /// portable exe currently is; turning the option off deletes the task and leaves nothing behind.
    /// </summary>
    static class Autostart
    {
        const string TaskName = "Wisp Cleaner";

        /// <summary>Protected copy used by the task: %ProgramFiles%\Wisp Cleaner.</summary>
        public static string InstallDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Wisp Cleaner"); }
        }

        public static string InstalledExe { get { return Path.Combine(InstallDir, "WispCleaner.exe"); } }

        public static bool RunningInstalled
        {
            get { return string.Equals(Path.GetFullPath(Settings.ExePath), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase); }
        }

        /// <summary>Copies this exe (and the settings) to Program Files unless it is already running from there.</summary>
        static void Install()
        {
            if (RunningInstalled) return;
            Directory.CreateDirectory(InstallDir);
            try { File.Copy(Settings.ExePath, InstalledExe, true); }
            catch (IOException)
            {
                // The installed copy is running (in the tray): keep it, it is the same program.
                if (!File.Exists(InstalledExe)) throw;
            }
            CopySettings();
        }

        /// <summary>Keeps the installed copy's settings in step with this one.</summary>
        public static void CopySettings()
        {
            try
            {
                if (RunningInstalled || !Directory.Exists(InstallDir)) return;
                if (File.Exists(Settings.IniPath)) File.Copy(Settings.IniPath, Path.Combine(InstallDir, "WispCleaner.ini"), true);
            }
            catch { }
        }

        public static void Set(bool on)
        {
            if (on) Create();
            else Delete();
        }

        /// <summary>Re-points the task at this exe if it was moved; called on start when the option is on.</summary>
        public static void Refresh()
        {
            try { if (Settings.Autostart) Create(); }
            catch { }
        }

        static void Create()
        {
            Install();
            string user = WindowsIdentity.GetCurrent().Name;
            string xml =
                "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\n" +
                "  <RegistrationInfo><Description>Wisp Cleaner: starts in the tray for scheduled auto-clean.</Description></RegistrationInfo>\n" +
                "  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + SecurityElement.Escape(user) + "</UserId><Delay>PT30S</Delay></LogonTrigger></Triggers>\n" +
                "  <Principals><Principal id=\"Author\"><UserId>" + SecurityElement.Escape(user) + "</UserId>" +
                "<LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\n" +
                "  <Settings>\n" +
                "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\n" +
                "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\n" +
                "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\n" +
                "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\n" +
                "    <Priority>7</Priority>\n" +
                "  </Settings>\n" +
                "  <Actions Context=\"Author\"><Exec><Command>\"" + SecurityElement.Escape(InstalledExe) + "\"</Command><Arguments>/tray</Arguments></Exec></Actions>\n" +
                "</Task>\n";
            string tmp = Path.Combine(Path.GetTempPath(), "WispCleaner-task-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                File.WriteAllText(tmp, xml, Encoding.Unicode);
                Schtasks("/Create /TN \"" + TaskName + "\" /XML \"" + tmp + "\" /F");
            }
            finally
            {
                try { File.Delete(tmp); } catch { }
            }
        }

        static void Delete()
        {
            if (Exists()) Schtasks("/Delete /TN \"" + TaskName + "\" /F");
            // Remove the protected copy too, so nothing is left behind (unless it is the one running).
            if (!RunningInstalled && Directory.Exists(InstallDir))
            {
                try { Directory.Delete(InstallDir, true); } catch { }
            }
        }

        public static bool Exists()
        {
            try { return Run("/Query /TN \"" + TaskName + "\"").ExitCode == 0; }
            catch { return false; }
        }

        static void Schtasks(string args)
        {
            var r = Run(args);
            if (r.ExitCode != 0) throw new InvalidOperationException(r.Output.Trim().Length > 0 ? r.Output.Trim() : "schtasks: " + r.ExitCode);
        }

        class Result
        {
            public int ExitCode;
            public string Output;
        }

        static Result Run(string args)
        {
            var psi = new ProcessStartInfo("schtasks.exe", args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage),
                StandardErrorEncoding = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage),
            };
            using (var p = Process.Start(psi))
            {
                string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(15000);
                return new Result { ExitCode = p.ExitCode, Output = output };
            }
        }
    }
}
