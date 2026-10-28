using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Wisp
{
    enum Special { None, RecycleBin }

    /// <summary>One thing the cleaner can remove, e.g. "temporary files" or "Discord cache".</summary>
    class CleanItem
    {
        public string Id, Ru, En, NoteRu, NoteEn;
        public bool DefaultOn = true, Risky;
        public Func<IEnumerable<string>> Roots;
        public string[] Patterns;
        public bool Recursive = true;
        public int MinAgeHours;
        public Special Special;

        // results of the last scan
        public long Size;
        public long Count;
        public bool Scanned;
        public List<FileEntry> Files = new List<FileEntry>();

        public string Name { get { return L.T(Ru, En); } }
        public string Note { get { return L.T(NoteRu, NoteEn); } }

        public bool Checked
        {
            get
            {
                bool v;
                return Settings.Items.TryGetValue(Id, out v) ? v : DefaultOn;
            }
            set
            {
                if (value == DefaultOn) Settings.Items.Remove(Id);
                else Settings.Items[Id] = value;
            }
        }

        /// <summary>The roots present on this PC; one lying inside another is dropped, so no file is counted twice.</summary>
        public IEnumerable<string> ExistingRoots()
        {
            if (Roots == null) return new string[0];
            var found = new List<string>();
            foreach (string r in Roots())
            {
                if (string.IsNullOrEmpty(r)) continue;
                bool exists;
                try { exists = Directory.Exists(r) || File.Exists(r); } catch { exists = false; }
                if (exists && !found.Any(f => FileOps.SamePath(f, r))) found.Add(r);
            }
            return found.Where(r => !found.Any(o => !ReferenceEquals(o, r) && FileOps.IsUnder(r, o))).ToList();
        }
    }

    class CleanGroup
    {
        public string Id, Ru, En, DescRu, DescEn, Glyph;
        public readonly List<CleanItem> Items = new List<CleanItem>();
        public string Name { get { return L.T(Ru, En); } }
        public string Desc { get { return L.T(DescRu, DescEn); } }
    }

    /// <summary>The list of everything the one-button clean knows about. Only targets present on this PC are listed.</summary>
    static class CleanCatalog
    {
        static string ProgData { get { return Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData); } }
        static string Win { get { return Environment.GetFolderPath(Environment.SpecialFolder.Windows); } }

        static Func<IEnumerable<string>> P(params string[] paths) { return () => paths; }

        public static List<CleanGroup> Build()
        {
            var groups = new List<CleanGroup>();

            var sys = Group(groups, "sys", "Система", "System", "Временные файлы Windows и программ", "Temporary files of Windows and programs", "\uE7F8");
            Add(sys, new CleanItem
            {
                Id = "sys.temp", Ru = UserProfiles.Many ? "Временные файлы пользователей" : "Временные файлы пользователя",
                En = UserProfiles.Many ? "Users' temporary files" : "User temporary files",
                NoteRu = UserProfiles.Many ? "%TEMP% всех пользователей (" + UserProfiles.All.Count + "), старше суток" : "%TEMP%, старше суток",
                NoteEn = UserProfiles.Many ? "%TEMP% of all users (" + UserProfiles.All.Count + "), older than a day" : "%TEMP%, older than a day",
                Roots = UserTemp, MinAgeHours = 24,
            });
            Add(sys, new CleanItem
            {
                Id = "sys.wintemp", Ru = "Временные файлы Windows", En = "Windows temporary files",
                NoteRu = @"Windows\Temp, старше суток", NoteEn = @"Windows\Temp, older than a day",
                Roots = P(Path.Combine(Win, "Temp")), MinAgeHours = 24,
            });
            Add(sys, new CleanItem
            {
                Id = "sys.inetcache", Ru = "Кэш интернета Windows", En = "Windows internet cache",
                NoteRu = "INetCache", NoteEn = "INetCache",
                Roots = UserProfiles.Local(@"Microsoft\Windows\INetCache"),
            });
            Add(sys, new CleanItem
            {
                Id = "sys.thumbs", Ru = "Кэш эскизов", En = "Thumbnail cache",
                NoteRu = "Пересоздаётся проводником", NoteEn = "Rebuilt by Explorer",
                Roots = UserProfiles.Local(@"Microsoft\Windows\Explorer"), Patterns = new[] { "thumbcache_*.db" }, Recursive = false,
            });

            var upd = Group(groups, "upd", "Обновления Windows", "Windows Update", "Уже установленные загрузки обновлений", "Downloads of already installed updates", "\uE895");
            Add(upd, new CleanItem
            {
                Id = "upd.download", Ru = "Загрузки Центра обновления", En = "Windows Update downloads",
                NoteRu = @"SoftwareDistribution\Download", NoteEn = @"SoftwareDistribution\Download",
                Roots = P(Path.Combine(Win, @"SoftwareDistribution\Download")),
            });
            Add(upd, new CleanItem
            {
                Id = "upd.do", Ru = "Кэш оптимизации доставки", En = "Delivery Optimization cache",
                NoteRu = "Файлы обновлений для раздачи", NoteEn = "Update files shared with other PCs",
                Roots = P(Path.Combine(Win, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"),
                          Path.Combine(Win, @"SoftwareDistribution\DeliveryOptimization")),
            });

            var bin = Group(groups, "bin", "Корзина", "Recycle Bin", "Удалённые файлы на всех дисках", "Deleted files on all drives", "\uE74D");
            bin.Items.Add(new CleanItem
            {
                Id = "bin.recycle", Ru = "Корзина", En = "Recycle Bin", NoteRu = "Восстановить будет нельзя", NoteEn = "Cannot be restored afterwards",
                Special = Special.RecycleBin,
            });

            var br = Group(groups, "br", "Браузеры", "Browsers", "Только кэш: пароли, вкладки и история не затрагиваются", "Cache only: passwords, tabs and history stay", "\uE774");
            foreach (var b in Browsers.Detect())
            {
                var browser = b;
                Add(br, new CleanItem
                {
                    Id = "br." + b.Id, Ru = "Кэш " + b.Name, En = b.Name + " cache",
                    NoteRu = b.Profiles.Count > 1 ? "Профилей: " + b.Profiles.Count : "", NoteEn = b.Profiles.Count > 1 ? "Profiles: " + b.Profiles.Count : "",
                    Roots = () => browser.Profiles.SelectMany(p => p.Paths(BrowserData.Cache)),
                });
            }

            var apps = Group(groups, "app", "Приложения", "Apps", "Кэш мессенджеров, плееров и лаунчеров", "Cache of messengers, players and launchers", "\uE71D");
            Add(apps, App("app.discord", "Discord", UserProfiles.Roaming(@"discord\Cache", @"discord\Code Cache", @"discord\GPUCache")));
            Add(apps, App("app.teams", "Microsoft Teams", UserProfiles.Roaming(@"Microsoft\Teams\Cache", @"Microsoft\Teams\Code Cache", @"Microsoft\Teams\GPUCache",
                @"Microsoft\Teams\Service Worker\CacheStorage", @"Microsoft\Teams\tmp")));
            Add(apps, App("app.slack", "Slack", UserProfiles.Roaming(@"Slack\Cache", @"Slack\Code Cache", @"Slack\GPUCache", @"Slack\Service Worker\CacheStorage")));
            Add(apps, App("app.spotify", "Spotify", UserProfiles.Local(@"Spotify\Storage", @"Spotify\Data")));
            Add(apps, App("app.steam", "Steam", UserProfiles.Local(@"Steam\htmlcache")));
            Add(apps, App("app.vscode", "Visual Studio Code", UserProfiles.Roaming(@"Code\Cache", @"Code\CachedData", @"Code\Code Cache", @"Code\GPUCache", @"Code\CachedExtensionVSIXs")));
            Add(apps, App("app.zoom", "Zoom", UserProfiles.Roaming(@"Zoom\data\Cache", @"Zoom\logs")));
            Add(apps, App("app.epic", "Epic Games Launcher", UserProfiles.Local(@"EpicGamesLauncher\Saved\webcache", @"EpicGamesLauncher\Saved\webcache_4147", @"EpicGamesLauncher\Saved\Logs")));

            var dev = Group(groups, "dev", "Кэш разработчика", "Developer caches", "Пакеты скачаются заново при следующей сборке", "Packages are downloaded again on the next build", "\uE943");
            Add(dev, Dev("dev.npm", "npm", UserProfiles.Local("npm-cache")));
            Add(dev, Dev("dev.yarn", "Yarn", UserProfiles.Local(@"Yarn\Cache")));
            Add(dev, Dev("dev.pip", "pip", UserProfiles.Local(@"pip\cache")));
            Add(dev, Dev("dev.nuget", "NuGet (HTTP)", UserProfiles.Local(@"NuGet\v3-cache", @"NuGet\http-cache")));
            Add(dev, Dev("dev.gradle", "Gradle", UserProfiles.Home(@".gradle\caches")));

            var gpu = Group(groups, "gpu", "Шейдеры и драйверы", "Shaders and drivers", "Пересоздаются, первые запуски игр могут быть медленнее", "Rebuilt; the first game launches may be slower", "\uE7F4");
            Add(gpu, new CleanItem
            {
                Id = "gpu.d3d", Ru = "Кэш шейдеров DirectX", En = "DirectX shader cache", DefaultOn = false,
                Roots = UserProfiles.Local("D3DSCache"),
            });
            Add(gpu, new CleanItem
            {
                Id = "gpu.nvidia", Ru = "Кэш шейдеров NVIDIA", En = "NVIDIA shader cache", DefaultOn = false,
                Roots = Both(UserProfiles.Local(@"NVIDIA\DXCache", @"NVIDIA\GLCache"), Path.Combine(ProgData, @"NVIDIA Corporation\NV_Cache")),
            });
            Add(gpu, new CleanItem
            {
                Id = "gpu.amd", Ru = "Кэш шейдеров AMD", En = "AMD shader cache", DefaultOn = false,
                Roots = UserProfiles.Local(@"AMD\DxCache", @"AMD\DxcCache", @"AMD\GLCache", @"AMD\VkCache"),
            });
            Add(gpu, new CleanItem
            {
                Id = "gpu.intel", Ru = "Кэш шейдеров Intel", En = "Intel shader cache", DefaultOn = false,
                Roots = UserProfiles.Local(@"Intel\ShaderCache"),
            });

            var logs = Group(groups, "log", "Логи и дампы", "Logs and dumps", "Отчёты об ошибках и аварийные дампы памяти", "Error reports and crash memory dumps", "\uE9F9");
            Add(logs, new CleanItem
            {
                Id = "log.wer", Ru = "Отчёты об ошибках Windows", En = "Windows error reports",
                Roots = Both(UserProfiles.Local(@"Microsoft\Windows\WER"), Path.Combine(ProgData, @"Microsoft\Windows\WER\ReportArchive"),
                          Path.Combine(ProgData, @"Microsoft\Windows\WER\ReportQueue"), Path.Combine(ProgData, @"Microsoft\Windows\WER\Temp")),
            });
            Add(logs, new CleanItem
            {
                Id = "log.crashdumps", Ru = "Дампы сбоев программ", En = "App crash dumps",
                Roots = UserProfiles.Local("CrashDumps"),
            });
            Add(logs, new CleanItem
            {
                Id = "log.minidump", Ru = "Дампы памяти Windows", En = "Windows memory dumps",
                NoteRu = "Нужны только для разбора «синих экранов»", NoteEn = "Only needed to analyze blue screens",
                Roots = P(Path.Combine(Win, "Minidump"), Path.Combine(Win, "MEMORY.DMP"), Path.Combine(Win, "LiveKernelReports")),
            });
            Add(logs, new CleanItem
            {
                Id = "log.winlogs", Ru = "Журналы установки Windows", En = "Windows setup logs", DefaultOn = false, Risky = true,
                NoteRu = "Старше 30 дней; могут понадобиться для диагностики", NoteEn = "Older than 30 days; may be needed for troubleshooting",
                Roots = P(Path.Combine(Win, "Logs"), Path.Combine(Win, @"Panther")), Patterns = new[] { "*.log", "*.etl", "*.cab" }, MinAgeHours = 24 * 30,
            });

            groups.RemoveAll(g => g.Items.Count == 0);
            return groups;
        }

        static CleanGroup Group(List<CleanGroup> groups, string id, string ru, string en, string dru, string den, string glyph)
        {
            var g = new CleanGroup { Id = id, Ru = ru, En = en, DescRu = dru, DescEn = den, Glyph = glyph };
            groups.Add(g);
            return g;
        }

        /// <summary>Adds the item only when something of it exists on this PC.</summary>
        static void Add(CleanGroup g, CleanItem item)
        {
            if (item.Special != Special.None || item.ExistingRoots().Any()) g.Items.Add(item);
        }

        static CleanItem App(string id, string name, Func<IEnumerable<string>> roots)
        {
            return new CleanItem { Id = id, Ru = "Кэш " + name, En = name + " cache", Roots = roots };
        }

        static CleanItem Dev(string id, string name, Func<IEnumerable<string>> roots)
        {
            return new CleanItem { Id = id, Ru = "Кэш " + name, En = name + " cache", DefaultOn = false, Roots = roots };
        }

        static Func<IEnumerable<string>> Both(Func<IEnumerable<string>> perUser, params string[] shared)
        {
            return () => perUser().Concat(shared);
        }

        /// <summary>
        /// Temp of every profile. The app's own %TEMP% is added when it lies elsewhere (usually it is
        /// AppData\Local\Temp or, on terminal servers, a per-session folder inside it).
        /// </summary>
        static IEnumerable<string> UserTemp()
        {
            var roots = UserProfiles.Local("Temp")().ToList();
            string own = Path.GetTempPath();
            if (!roots.Any(r => FileOps.IsUnder(own, r))) roots.Add(own);
            return roots;
        }
    }
}
