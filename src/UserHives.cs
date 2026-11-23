using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Wisp
{
    /// <summary>
    /// The registry of users who are not signed in. Their programs installed "for me" live in their own
    /// registry, which is a closed file (NTUSER.DAT) while they are away — on a shared or domain PC that is most
    /// users. As administrator such a file is loaded under HKEY_USERS\WispUser_&lt;SID&gt; only for as long as a
    /// list is read or a removal runs, and unloaded right after: a user signing in while their file is loaded
    /// elsewhere would get a temporary profile. Mounts are counted (nested scopes share one), never made for a
    /// user who is signed in, released at exit and on a crash, and stale ones from an earlier run are unloaded
    /// at the first use.
    /// </summary>
    static class UserHives
    {
        public const string MountPrefix = "WispUser_";

        class Mount
        {
            public string Sid;
            public int Count;
            public bool Classes;
        }

        static readonly Dictionary<string, Mount> mounts = new Dictionary<string, Mount>(StringComparer.OrdinalIgnoreCase);
        static readonly object sync = new object();
        static bool ready;
        static readonly List<string> failed = new List<string>();

        /// <summary>Users whose registry could not be loaded the last time all were (their file is locked or damaged).</summary>
        public static List<string> Failed { get { lock (sync) return failed.ToList(); } }

        static readonly IntPtr HKEY_USERS = new IntPtr(unchecked((int)0x80000003));

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RegLoadKey(IntPtr key, string subKey, string file);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RegUnLoadKey(IntPtr key, string subKey);

        [StructLayout(LayoutKind.Sequential)]
        struct LUID { public uint Low; public int High; }

        [StructLayout(LayoutKind.Sequential)]
        struct TOKEN_PRIVILEGES { public uint Count; public LUID Luid; public uint Attributes; }

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool LookupPrivilegeValue(string system, string name, out LUID luid);
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES state, uint length, IntPtr previous, IntPtr returned);
        [DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);

        /// <summary>Loading and unloading a registry file needs these two privileges, which administrators have but are off by default.</summary>
        static void EnablePrivileges()
        {
            IntPtr token;
            if (!OpenProcessToken(GetCurrentProcess(), 0x0020 | 0x0008 /* ADJUST_PRIVILEGES | QUERY */, out token)) return;
            try
            {
                foreach (string name in new[] { "SeRestorePrivilege", "SeBackupPrivilege" })
                {
                    var tp = new TOKEN_PRIVILEGES { Count = 1, Attributes = 0x2 /* SE_PRIVILEGE_ENABLED */ };
                    if (LookupPrivilegeValue(null, name, out tp.Luid)) AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
                }
            }
            finally { CloseHandle(token); }
        }

        static void Prepare()
        {
            if (ready) return;
            ready = true;
            EnablePrivileges();
            // Whatever an earlier run left loaded (it was killed in the middle) goes first.
            try
            {
                foreach (string name in Registry.Users.GetSubKeyNames())
                    if (name.StartsWith(MountPrefix, StringComparison.OrdinalIgnoreCase)) Unload(name);
            }
            catch { }
            AppDomain.CurrentDomain.ProcessExit += (s, e) => ReleaseAll();
            AppDomain.CurrentDomain.UnhandledException += (s, e) => ReleaseAll();
        }

        static bool Unload(string name)
        {
            for (int i = 0; i < 10; i++)
            {
                // Registry handles the program dropped without closing keep a hive loaded.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                if (RegUnLoadKey(HKEY_USERS, name) == 0) return true;
                System.Threading.Thread.Sleep(100);
            }
            return false;
        }

        static HashSet<string> SignedIn()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try { foreach (string n in Registry.Users.GetSubKeyNames()) set.Add(n); }
            catch { }
            return set;
        }

        /// <summary>Profiles whose registry is closed now: not signed in, with a registry file on the disk.</summary>
        public static List<UserProfile> Closed()
        {
            if (!Secure.IsElevated) return new List<UserProfile>();
            var online = SignedIn();
            return UserProfiles.All.Where(u => u.Sid != null && !online.Contains(u.Sid) && !u.IsSelf &&
                                               File.Exists(Path.Combine(u.Dir, "NTUSER.DAT"))).ToList();
        }

        /// <summary>The users whose registry this app has loaded at the moment, as roots for the registry code.</summary>
        public static List<UserRoot> Roots()
        {
            lock (sync)
                return mounts.Values.Select(m => new UserRoot
                {
                    Hive = RegistryHive.Users, Prefix = MountPrefix + m.Sid + "\\", Sid = m.Sid, Offline = true,
                    Classes = m.Classes ? MountPrefix + m.Sid + "_Classes\\" : MountPrefix + m.Sid + @"\Software\Classes\",
                }).ToList();
        }

        /// <summary>The registry of every user who is not signed in, loaded until the result is disposed.</summary>
        public static IDisposable LoadAll()
        {
            var closed = Closed();
            var scope = Load(closed.Select(u => u.Sid));
            lock (sync)
            {
                failed.Clear();
                failed.AddRange(closed.Where(u => !mounts.ContainsKey(u.Sid)).Select(u => u.Name));
            }
            return scope;
        }

        /// <summary>The registry of these users (those not signed in), loaded until the result is disposed.</summary>
        public static IDisposable Load(IEnumerable<string> sids)
        {
            var taken = new List<string>();
            if (!Secure.IsElevated) return new Scope(taken);
            lock (sync)
            {
                Prepare();
                var online = SignedIn();
                foreach (string sid in sids.Where(s => s != null).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    Mount m;
                    if (mounts.TryGetValue(sid, out m)) { m.Count++; taken.Add(sid); continue; }
                    if (online.Contains(sid)) continue;   // signed in: their registry is under HKEY_USERS already
                    var u = UserProfiles.BySid(sid);
                    if (u == null) continue;
                    string file = Path.Combine(u.Dir, "NTUSER.DAT");
                    if (!File.Exists(file) || RegLoadKey(HKEY_USERS, MountPrefix + sid, file) != 0) continue;
                    m = new Mount { Sid = sid, Count = 1 };
                    string classes = Path.Combine(u.Local, @"Microsoft\Windows\UsrClass.dat");
                    if (File.Exists(classes) && RegLoadKey(HKEY_USERS, MountPrefix + sid + "_Classes", classes) == 0) m.Classes = true;
                    mounts[sid] = m;
                    taken.Add(sid);
                }
            }
            return new Scope(taken);
        }

        /// <summary>The users whose loaded registry these registry paths (HKU\WispUser_&lt;SID&gt;\…) are in.</summary>
        public static IEnumerable<string> SidsIn(IEnumerable<string> subKeys)
        {
            foreach (string s in subKeys)
            {
                if (s == null || !s.StartsWith(MountPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                int end = s.IndexOf('\\');
                string sid = (end > 0 ? s.Substring(0, end) : s).Substring(MountPrefix.Length);
                if (sid.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase)) sid = sid.Substring(0, sid.Length - 8);
                yield return sid;
            }
        }

        static void Release(IEnumerable<string> sids)
        {
            lock (sync)
                foreach (string sid in sids)
                {
                    Mount m;
                    if (!mounts.TryGetValue(sid, out m) || --m.Count > 0) continue;
                    mounts.Remove(sid);
                    if (m.Classes) Unload(MountPrefix + sid + "_Classes");
                    Unload(MountPrefix + sid);
                }
        }

        static void ReleaseAll()
        {
            try
            {
                lock (sync)
                {
                    foreach (var m in mounts.Values.ToList())
                    {
                        if (m.Classes) Unload(MountPrefix + m.Sid + "_Classes");
                        Unload(MountPrefix + m.Sid);
                    }
                    mounts.Clear();
                }
            }
            catch { }
        }

        class Scope : IDisposable
        {
            List<string> sids;
            public Scope(List<string> sids) { this.sids = sids; }
            public void Dispose()
            {
                var s = sids;
                sids = null;
                if (s != null && s.Count > 0) Release(s);
            }
        }
    }
}
