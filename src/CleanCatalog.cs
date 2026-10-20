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

        public IEnumerable<string> ExistingRoots()
        {
            if (Roots == null) yield break;
            foreach (string r in Roots())
            {
                if (string.IsNullOrEmpty(r)) continue;
                bool exists;
                try { exists = Directory.Exists(r) || File.Exists(r); } catch { exists = false; }
                if (exists) yield return r;
            }
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
        static string Local { get { return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); } }
        static string Roaming { get { return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); } }
        static string ProgData { get { return Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData); } }
        static string Win { get { return Environment.GetFolderPath(Environment.SpecialFolder.Windows); } }

        static Func<IEnumerable<string>> P(params string[] paths) { return () => paths; }

        public static List<CleanGroup> Build()
        {
            var groups = new List<CleanGroup>();

            var sys = Group(groups, "sys", "Система", "System", "Временные файлы Windows и программ", "Temporary files of Windows and programs", "\uE7F8");
            Add(sys, new CleanItem
            {
                Id = "sys.temp", Ru = "Временные файлы пользователя", En = "User temporary files",
                NoteRu = "%TEMP%, старше суток", NoteEn = "%TEMP%, older than a day",
                Roots = P(Path.Combine(Local, "Temp")), MinAgeHours = 24,
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
                Roots = P(Path.Combine(Local, @"Microsoft\Windows\INetCache")),
            });
            Add(sys, new CleanItem
            {
                Id = "sys.thumbs", Ru = "Кэш эскизов", En = "Thumbnail cache",
                NoteRu = "Пересоздаётся проводником", NoteEn = "Rebuilt by Explorer",
                Roots = P(Path.Combine(Local, @"Microsoft\Windows\Explorer")), Patterns = new[] { "thumbcache_*.db" }, Recursive = false,
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
            Add(apps, App("app.discord", "Discord", Roaming, @"discord\Cache", @"discord\Code Cache", @"discord\GPUCache"));
            Add(apps, App("app.teams", "Microsoft Teams", Roaming, @"Microsoft\Teams\Cache", @"Microsoft\Teams\Code Cache", @"Microsoft\Teams\GPUCache",
                @"Microsoft\Teams\Service Worker\CacheStorage", @"Microsoft\Teams\tmp"));
            Add(apps, App("app.slack", "Slack", Roaming, @"Slack\Cache", @"Slack\Code Cache", @"Slack\GPUCache", @"Slack\Service Worker\CacheStorage"));
            Add(apps, App("app.spotify", "Spotify", Local, @"Spotify\Storage", @"Spotify\Data"));
            Add(apps, App("app.steam", "Steam", Local, @"Steam\htmlcache"));
            Add(apps, App("app.vscode", "Visual Studio Code", Roaming, @"Code\Cache", @"Code\CachedData", @"Code\Code Cache", @"Code\GPUCache", @"Code\CachedExtensionVSIXs"));
            Add(apps, App("app.zoom", "Zoom", Roaming, @"Zoom\data\Cache", @"Zoom\logs"));
            Add(apps, App("app.epic", "Epic Games Launcher", Local, @"EpicGamesLauncher\Saved\webcache", @"EpicGamesLauncher\Saved\webcache_4147", @"EpicGamesLauncher\Saved\Logs"));

            var dev = Group(groups, "dev", "Кэш разработчика", "Developer caches", "Пакеты скачаются заново при следующей сборке", "Packages are downloaded again on the next build", "\uE943");
            Add(dev, Dev("dev.npm", "npm", Path.Combine(Local, "npm-cache")));
            Add(dev, Dev("dev.yarn", "Yarn", Path.Combine(Local, @"Yarn\Cache")));
            Add(dev, Dev("dev.pip", "pip", Path.Combine(Local, @"pip\cache")));
            Add(dev, Dev("dev.nuget", "NuGet (HTTP)", Path.Combine(Local, @"NuGet\v3-cache"), Path.Combine(Local, @"NuGet\http-cache")));
            Add(dev, Dev("dev.gradle", "Gradle", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".gradle\caches")));

            var gpu = Group(groups, "gpu", "Шейдеры и драйверы", "Shaders and drivers", "Пересоздаются, первые запуски игр могут быть медленнее", "Rebuilt; the first game launches may be slower", "\uE7F4");
            Add(gpu, new CleanItem
            {
                Id = "gpu.d3d", Ru = "Кэш шейдеров DirectX", En = "DirectX shader cache", DefaultOn = false,
                Roots = P(Path.Combine(Local, "D3DSCache")),
            });
            Add(gpu, new CleanItem
            {
                Id = "gpu.nvidia", Ru = "Кэш шейдеров NVIDIA", En = "NVIDIA shader cache", DefaultOn = false,
                Roots = P(Path.Combine(Local, @"NVIDIA\DXCache"), Path.Combine(Local, @"NVIDIA\GLCache"), Path.Combine(ProgData, @"NVIDIA Corporation\NV_Cache")),
            });
            Add(gpu, new CleanItem
            {
                Id = "gpu.amd", Ru = "Кэш шейдеров AMD", En = "AMD shader cache", DefaultOn = false,
                Roots = P(Path.Combine(Local, @"AMD\DxCache"), Path.Combine(Local, @"AMD\DxcCache"), Path.Combine(Local, @"AMD\GLCache"), Path.Combine(Local, @"AMD\VkCache")),
            });
            Add(gpu, new CleanItem
            {
                Id = "gpu.intel", Ru = "Кэш шейдеров Intel", En = "Intel shader cache", DefaultOn = false,
                Roots = P(Path.Combine(Local, @"Intel\ShaderCache")),
            });

            var logs = Group(groups, "log", "Логи и дампы", "Logs and dumps", "Отчёты об ошибках и аварийные дампы памяти", "Error reports and crash memory dumps", "\uE9F9");
            Add(logs, new CleanItem
            {
                Id = "log.wer", Ru = "Отчёты об ошибках Windows", En = "Windows error reports",
                Roots = P(Path.Combine(ProgData, @"Microsoft\Windows\WER\ReportArchive"), Path.Combine(ProgData, @"Microsoft\Windows\WER\ReportQueue"),
                          Path.Combine(ProgData, @"Microsoft\Windows\WER\Temp"), Path.Combine(Local, @"Microsoft\Windows\WER")),
            });
            Add(logs, new CleanItem
            {
                Id = "log.crashdumps", Ru = "Дампы сбоев программ", En = "App crash dumps",
                Roots = P(Path.Combine(Local, "CrashDumps")),
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

        static CleanItem App(string id, string name, string baseDir, params string[] rel)
        {
            return new CleanItem
            {
                Id = id, Ru = "Кэш " + name, En = name + " cache",
                Roots = P(rel.Select(r => Path.Combine(baseDir, r)).ToArray()),
            };
        }

        static CleanItem Dev(string id, string name, params string[] roots)
        {
            return new CleanItem { Id = id, Ru = "Кэш " + name, En = name + " cache", DefaultOn = false, Roots = P(roots) };
        }
    }
}
