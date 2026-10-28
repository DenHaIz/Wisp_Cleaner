using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Wisp
{
    /// <summary>
    /// Operations that must stay safe while the app runs as administrator:
    /// deleting through a handle whose real path is verified (no junction/symlink tricks between
    /// scan and delete), and starting user programs without administrator rights.
    /// </summary>
    static class Secure
    {
        const uint DELETE = 0x00010000, FILE_READ_ATTRIBUTES = 0x0080, FILE_WRITE_ATTRIBUTES = 0x0100;
        const uint FILE_SHARE_ALL = 0x1 | 0x2 | 0x4;
        const uint OPEN_EXISTING = 3;
        const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000, FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        const int FileBasicInfo = 0, FileDispositionInfo = 4, FileDispositionInfoEx = 21;

        [StructLayout(LayoutKind.Sequential)]
        struct BY_HANDLE_FILE_INFORMATION
        {
            public uint dwFileAttributes;
            public long ftCreationTime, ftLastAccessTime, ftLastWriteTime;
            public uint dwVolumeSerialNumber, nFileSizeHigh, nFileSizeLow, nNumberOfLinks, nFileIndexHigh, nFileIndexLow;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct FILE_BASIC_INFO
        {
            public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime;
            public uint FileAttributes;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern uint GetFinalPathNameByHandle(SafeFileHandle h, StringBuilder path, uint size, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetFileInformationByHandle(SafeFileHandle h, out BY_HANDLE_FILE_INFORMATION info);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetFileInformationByHandle(SafeFileHandle h, int cls, ref uint info, uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetFileInformationByHandle(SafeFileHandle h, int cls, ref FILE_BASIC_INFO info, uint size);

        static string Final(SafeFileHandle h)
        {
            var sb = new StringBuilder(1024);
            uint n = GetFinalPathNameByHandle(h, sb, (uint)sb.Capacity, 0);
            if (n == 0 || n >= sb.Capacity) return null;
            string p = sb.ToString();
            if (p.StartsWith(@"\\?\UNC\")) p = @"\\" + p.Substring(8);
            else if (p.StartsWith(@"\\?\")) p = p.Substring(4);
            return p.TrimEnd('\\') + "\\";
        }

        /// <summary>The real location of a folder (junctions resolved), with a trailing backslash; null if it cannot be opened.</summary>
        public static string FinalPath(string dir)
        {
            using (var h = CreateFile(dir, FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero))
                return h.IsInvalid ? null : Final(h);
        }

        /// <summary>
        /// Deletes a file or an empty folder only if, at the moment of deletion, the object itself is
        /// not a reparse point and its real path lies inside rootFinal (from FinalPath). Read-only
        /// files are deleted too. False when it is locked, protected or outside the root.
        /// </summary>
        public static bool Delete(string path, string rootFinal)
        {
            bool inUse;
            return Delete(path, rootFinal, out inUse);
        }

        /// <summary>
        /// As <see cref="Delete(string, string)"/>; inUse is true only when the file was not deleted because
        /// it is open or running (sharing/lock violation, a mapped program image) and it surely lies inside
        /// rootFinal — the one case where deleting it at the next restart is right.
        /// </summary>
        public static bool Delete(string path, string rootFinal, out bool inUse)
        {
            inUse = false;
            if (rootFinal == null) return false;
            using (var h = CreateFile(path, DELETE | FILE_READ_ATTRIBUTES | FILE_WRITE_ATTRIBUTES, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING,
                       FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero))
            {
                if (h.IsInvalid)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == 32 || err == 33) inUse = InsideByParent(path, rootFinal);   // sharing / lock violation
                    return false;
                }
                BY_HANDLE_FILE_INFORMATION info;
                if (!GetFileInformationByHandle(h, out info) || (info.dwFileAttributes & 0x400) != 0) return false;
                string real = Final(h);
                if (real == null || real.Length <= rootFinal.Length || !real.StartsWith(rootFinal, StringComparison.OrdinalIgnoreCase)) return false;

                // DELETE | POSIX_SEMANTICS | IGNORE_READONLY_ATTRIBUTE (Windows 10 1809+)
                uint flags = 0x1 | 0x2 | 0x10;
                if (SetFileInformationByHandle(h, FileDispositionInfoEx, ref flags, 4)) return true;
                // Older systems: clear read-only, then the classic disposition.
                if ((info.dwFileAttributes & 0x1) != 0)
                {
                    var basic = new FILE_BASIC_INFO { FileAttributes = (info.dwFileAttributes & ~0x1u & ~0x4u & ~0x2u) | 0 };
                    if (basic.FileAttributes == 0) basic.FileAttributes = (info.dwFileAttributes & 0x10) != 0 ? 0x10u : 0x80u;
                    SetFileInformationByHandle(h, FileBasicInfo, ref basic, (uint)Marshal.SizeOf(typeof(FILE_BASIC_INFO)));
                }
                uint del = 1;
                if (SetFileInformationByHandle(h, FileDispositionInfo, ref del, 4)) return true;
                // Opened for deletion and checked, but Windows refuses: a running program's image, mostly.
                // A folder that is not empty (145) is not "in use".
                inUse = Marshal.GetLastWin32Error() != 145;
                return false;
            }
        }

        /// <summary>The object is no reparse point and its folder's real path lies inside rootFinal.</summary>
        static bool InsideByParent(string path, string rootFinal)
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
                string parent = FinalPath(Path.GetDirectoryName(path.TrimEnd('\\')));
                return parent != null && parent.StartsWith(rootFinal, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>True when the path or any folder above it is a junction or symbolic link.</summary>
        public static bool HasReparsePoint(string path)
        {
            try
            {
                string p = Path.GetFullPath(path).TrimEnd('\\');
                while (!string.IsNullOrEmpty(p) && p.Length > 3)
                {
                    if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) return true;
                    p = Path.GetDirectoryName(p);
                }
                return false;
            }
            catch { return true; }
        }

        // ---------- starting programs without administrator rights ----------

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct STARTUPINFO
        {
            public int cb;
            public string lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attrs, int level, int type, out IntPtr newToken);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CreateProcessWithTokenW(IntPtr token, int logonFlags, string app, StringBuilder cmd, int flags,
            IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

        public static bool IsElevated
        {
            get
            {
                try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
                catch { return false; }
            }
        }

        /// <summary>
        /// Starts a program with the signed-in user's normal rights (the shell's token), so browsers
        /// and per-user uninstallers never run as administrator. When this app is not elevated it is a
        /// plain start. Returns a process handle the caller must close (IntPtr.Zero if not needed / failed).
        /// </summary>
        public static IntPtr StartAsUser(string exe, string args, bool wantHandle)
        {
            if (!IsElevated)
            {
                var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false });
                if (!wantHandle || p == null) return IntPtr.Zero;
                return OpenProcess(0x00100000 /* SYNCHRONIZE */, false, p.Id);
            }
            IntPtr shell = GetShellWindow();
            if (shell == IntPtr.Zero) throw new InvalidOperationException(L.T("Проводник Windows не запущен", "Windows Explorer is not running"));
            int pid;
            GetWindowThreadProcessId(shell, out pid);
            IntPtr proc = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            if (proc == IntPtr.Zero) throw new Win32Exception();
            IntPtr token = IntPtr.Zero, primary = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(proc, 0x0002 /* TOKEN_DUPLICATE */, out token)) throw new Win32Exception();
                // TOKEN_QUERY | TOKEN_ASSIGN_PRIMARY | TOKEN_DUPLICATE | TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_SESSIONID
                if (!DuplicateTokenEx(token, 0x0008 | 0x0001 | 0x0002 | 0x0080 | 0x0100, IntPtr.Zero, 2, 1, out primary)) throw new Win32Exception();
                var si = new STARTUPINFO { cb = Marshal.SizeOf(typeof(STARTUPINFO)) };
                PROCESS_INFORMATION pi;
                var cmd = new StringBuilder("\"" + exe + "\"" + (string.IsNullOrEmpty(args) ? "" : " " + args));
                string dir = Path.IsPathRooted(exe) ? Path.GetDirectoryName(exe) : null;
                if (!CreateProcessWithTokenW(primary, 0, null, cmd, 0, IntPtr.Zero, dir, ref si, out pi))
                    throw new Win32Exception();
                CloseHandle(pi.hThread);
                if (wantHandle) return pi.hProcess;
                CloseHandle(pi.hProcess);
                return IntPtr.Zero;
            }
            finally
            {
                if (primary != IntPtr.Zero) CloseHandle(primary);
                if (token != IntPtr.Zero) CloseHandle(token);
                CloseHandle(proc);
            }
        }

        public static void WaitAndClose(IntPtr process)
        {
            if (process == IntPtr.Zero) return;
            WaitForSingleObject(process, 0xFFFFFFFF);
            CloseHandle(process);
        }

        // ---------- who is who ----------

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

        static string selfSid, shellSid;
        static bool shellKnown;

        /// <summary>The account this app runs as.</summary>
        public static string SelfSid
        {
            get
            {
                if (selfSid == null)
                {
                    try { selfSid = WindowsIdentity.GetCurrent().User.Value; } catch { selfSid = ""; }
                }
                return selfSid;
            }
        }

        /// <summary>
        /// The signed-in user whose desktop this is (owner of Explorer). It differs from SelfSid when
        /// the UAC prompt was answered with another account's password. Null if Explorer is not running.
        /// </summary>
        public static string ShellSid
        {
            get
            {
                if (!shellKnown)
                {
                    int pid;
                    IntPtr shell = GetShellWindow();
                    if (shell != IntPtr.Zero && GetWindowThreadProcessId(shell, out pid) != 0) shellSid = OwnerSid(pid);
                    // Explorer not running (yet): ask again next time instead of remembering "nobody".
                    shellKnown = shellSid != null;
                }
                return shellSid;
            }
        }

        /// <summary>SID of the account a process runs as; null when it cannot be read.</summary>
        public static string OwnerSid(int pid)
        {
            IntPtr proc = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            if (proc == IntPtr.Zero) return null;
            IntPtr token = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(proc, 0x0008 /* TOKEN_QUERY */, out token)) return null;
                using (var id = new WindowsIdentity(token)) return id.User != null ? id.User.Value : null;
            }
            catch { return null; }
            finally
            {
                if (token != IntPtr.Zero) CloseHandle(token);
                CloseHandle(proc);
            }
        }

        /// <summary>Full path of a process's executable; null when it cannot be read.</summary>
        public static string ImagePath(int pid)
        {
            IntPtr proc = OpenProcess(0x1000, false, pid);
            if (proc == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(proc, 0, sb, ref size) ? sb.ToString(0, size) : null;
            }
            finally { CloseHandle(proc); }
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool MoveFileEx(string from, string to, int flags);

        /// <summary>Schedules a locked file or an empty folder for deletion at the next restart (administrator only).</summary>
        public static bool DeleteOnReboot(string path)
        {
            return MoveFileEx(path, null, 0x4 /* MOVEFILE_DELAY_UNTIL_REBOOT */);
        }
    }
}
