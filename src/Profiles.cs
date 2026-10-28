using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace Wisp
{
    /// <summary>One Windows user profile on this PC.</summary>
    class UserProfile
    {
        public string Sid, Dir, Name, Local, Roaming;
        public bool IsSelf;   // the account this app runs as
        public bool IsShell;  // the signed-in user whose desktop is shown
    }

    /// <summary>
    /// The profiles whose files are cleaned. As administrator — often another account typed in at the
    /// UAC prompt — the app's own %LOCALAPPDATA% is not the signed-in user's, so every real profile
    /// from ProfileList is covered. Without administrator rights other profiles cannot be read: only one's own.
    /// </summary>
    static class UserProfiles
    {
        static List<UserProfile> cache;
        static readonly object sync = new object();

        /// <summary>The signed-in user first, then this app's account, then the rest by name.</summary>
        public static List<UserProfile> All
        {
            get
            {
                lock (sync)
                {
                    if (cache == null) cache = Load();
                    return cache;
                }
            }
        }

        public static bool Many { get { return All.Count > 1; } }

        public static UserProfile BySid(string sid)
        {
            return sid == null ? null : All.FirstOrDefault(p => string.Equals(p.Sid, sid, StringComparison.OrdinalIgnoreCase));
        }

        static List<UserProfile> Load()
        {
            var list = new List<UserProfile>();
            string self = Secure.SelfSid, shell = Secure.ShellSid;
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            list.Add(new UserProfile
            {
                Sid = self, Dir = home, Name = Path.GetFileName(home.TrimEnd('\\')), IsSelf = true,
                IsShell = shell == null || string.Equals(shell, self, StringComparison.OrdinalIgnoreCase),
                Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            });
            if (!Secure.IsElevated) return list;
            try
            {
                using (var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var k = b.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList"))
                {
                    if (k == null) return list;
                    foreach (string sid in k.GetSubKeyNames())
                    {
                        // Real accounts only (local/domain and Azure AD); not SYSTEM or service profiles.
                        if (!sid.StartsWith("S-1-5-21-") && !sid.StartsWith("S-1-12-1-")) continue;
                        if (sid.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) || string.Equals(sid, self, StringComparison.OrdinalIgnoreCase)) continue;
                        string dir;
                        using (var pk = k.OpenSubKey(sid))
                            dir = pk != null ? Environment.ExpandEnvironmentVariables((pk.GetValue("ProfileImagePath") as string) ?? "") : "";
                        if (dir.Length < 4 || !Directory.Exists(dir) || list.Any(p => string.Equals(p.Dir.TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) continue;
                        list.Add(new UserProfile
                        {
                            Sid = sid, Dir = dir, Name = Path.GetFileName(dir.TrimEnd('\\')),
                            IsShell = string.Equals(sid, shell, StringComparison.OrdinalIgnoreCase),
                            Local = Path.Combine(dir, @"AppData\Local"), Roaming = Path.Combine(dir, @"AppData\Roaming"),
                        });
                    }
                }
            }
            catch { }
            return list.OrderBy(p => p.IsShell ? 0 : p.IsSelf ? 1 : 2).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        static readonly string[] userVars = { "USERPROFILE", "APPDATA", "LOCALAPPDATA", "TEMP", "TMP", "HOMEPATH", "USERNAME", "ONEDRIVE" };

        /// <summary>
        /// Expands %VARS% of a value from the registry of the user with this SID (null: the machine's own
        /// values): %LOCALAPPDATA% of Alice's registry is in Alice's profile, not in this app's. A per-user
        /// variable of a user whose profile is unknown stays unexpanded (callers treat "%" as "cannot tell").
        /// </summary>
        public static string Expand(string text, string sid)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('%') < 0) return text ?? "";
            if (sid != null && !string.Equals(sid, Secure.SelfSid, StringComparison.OrdinalIgnoreCase))
            {
                var u = BySid(sid);
                foreach (string v in userVars)
                {
                    int i = text.IndexOf("%" + v + "%", StringComparison.OrdinalIgnoreCase);
                    if (i < 0) continue;
                    if (u == null) return text;
                    string value;
                    switch (v)
                    {
                        case "USERPROFILE": value = u.Dir; break;
                        case "APPDATA": value = u.Roaming; break;
                        case "LOCALAPPDATA": value = u.Local; break;
                        case "TEMP": case "TMP": value = Path.Combine(u.Local, "Temp"); break;
                        case "HOMEPATH": value = u.Dir.Length > 2 && u.Dir[1] == ':' ? u.Dir.Substring(2) : u.Dir; break;
                        case "USERNAME": value = u.Name; break;
                        default: return text;   // OneDrive: wherever that user keeps it — cannot tell
                    }
                    while (i >= 0)
                    {
                        text = text.Substring(0, i) + value + text.Substring(i + v.Length + 2);
                        i = text.IndexOf("%" + v + "%", i + value.Length, StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
            return Environment.ExpandEnvironmentVariables(text);
        }

        /// <summary>The same folder under %LOCALAPPDATA% of every profile.</summary>
        public static Func<IEnumerable<string>> Local(params string[] rel)
        {
            return () => All.SelectMany(p => rel.Select(r => Path.Combine(p.Local, r)));
        }

        /// <summary>The same folder under %APPDATA% of every profile.</summary>
        public static Func<IEnumerable<string>> Roaming(params string[] rel)
        {
            return () => All.SelectMany(p => rel.Select(r => Path.Combine(p.Roaming, r)));
        }

        /// <summary>The same folder under the home folder of every profile.</summary>
        public static Func<IEnumerable<string>> Home(params string[] rel)
        {
            return () => All.SelectMany(p => rel.Select(r => Path.Combine(p.Dir, r)));
        }
    }
}
