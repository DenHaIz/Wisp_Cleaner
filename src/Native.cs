using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace Wisp
{
    static class Native
    {
        const uint SHGFI_ICON = 0x100;
        const uint SHGFI_SMALLICON = 0x1;
        const uint SHGFI_LARGEICON = 0x0;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct SHQUERYRBINFO
        {
            public int cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SHGetFileInfo(string path, uint fileAttributes, ref SHFILEINFO info, uint size, uint flags);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, int count);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool DestroyIcon(IntPtr icon);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        public static extern int SetWindowTheme(IntPtr hwnd, string subAppName, string subIdList);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHQueryRecycleBin(string rootPath, ref SHQUERYRBINFO info);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHEmptyRecycleBin(IntPtr hwnd, string rootPath, uint flags);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        static extern int SHLoadIndirectString(string source, StringBuilder outBuf, int outSize, IntPtr reserved);

        /// <summary>Returns the shell's small icon for a file, or null.</summary>
        public static Bitmap GetSmallIcon(string path)
        {
            var info = new SHFILEINFO();
            IntPtr r = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(info), SHGFI_ICON | SHGFI_SMALLICON);
            if (r == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
            try
            {
                using (var icon = Icon.FromHandle(info.hIcon))
                    return icon.ToBitmap();
            }
            finally { DestroyIcon(info.hIcon); }
        }

        /// <summary>The shell's small icon for a file type (".mp4"), without touching any file.</summary>
        public static Bitmap GetTypeIcon(string extension)
        {
            var info = new SHFILEINFO();
            // SHGFI_USEFILEATTRIBUTES with FILE_ATTRIBUTE_NORMAL
            IntPtr r = SHGetFileInfo("x" + extension, 0x80, ref info, (uint)Marshal.SizeOf(info), SHGFI_ICON | SHGFI_SMALLICON | 0x10);
            if (r == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
            try
            {
                using (var icon = Icon.FromHandle(info.hIcon))
                    return icon.ToBitmap();
            }
            finally { DestroyIcon(info.hIcon); }
        }

        /// <summary>Icon by "file,index" (DisplayIcon of an installed program); 32 px so it stays sharp when scaled.</summary>
        public static Bitmap GetIcon(string file, int index)
        {
            var large = new IntPtr[1];
            try
            {
                if (ExtractIconEx(file, index, large, null, 1) <= 0 || large[0] == IntPtr.Zero) return null;
                using (var icon = Icon.FromHandle(large[0]))
                    return icon.ToBitmap();
            }
            catch { return null; }
            finally { if (large[0] != IntPtr.Zero) DestroyIcon(large[0]); }
        }

        /// <summary>Total size and item count of the Recycle Bin on all drives.</summary>
        public static bool QueryRecycleBin(out long size, out long count)
        {
            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf(typeof(SHQUERYRBINFO)) };
            int hr = SHQueryRecycleBin(null, ref info);
            size = info.i64Size;
            count = info.i64NumItems;
            return hr == 0;
        }

        public static bool EmptyRecycleBin()
        {
            // SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND
            int hr = SHEmptyRecycleBin(IntPtr.Zero, null, 0x1 | 0x2 | 0x4);
            return hr == 0 || hr == unchecked((int)0x8000FFFF); // E_UNEXPECTED: the bin was already empty
        }

        /// <summary>Moves files or folders to the Recycle Bin without any shell UI. False if anything failed.</summary>
        public static bool MoveToRecycleBin(IEnumerable<string> paths)
        {
            var sb = new StringBuilder();
            foreach (string p in paths) sb.Append(p).Append('\0');
            if (sb.Length == 0) return true;
            sb.Append('\0');
            var op = new SHFILEOPSTRUCT
            {
                wFunc = 0x0003, // FO_DELETE
                pFrom = sb.ToString(),
                // FOF_SILENT | FOF_NOCONFIRMATION | FOF_ALLOWUNDO | FOF_NOERRORUI
                fFlags = 0x0004 | 0x0010 | 0x0040 | 0x0400,
            };
            return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
        }

        /// <summary>Resolves "@{Package?ms-resource://...}" style strings; null when it cannot.</summary>
        public static string LoadIndirectString(string source)
        {
            var sb = new StringBuilder(1024);
            try
            {
                return SHLoadIndirectString(source, sb, sb.Capacity, IntPtr.Zero) == 0 ? sb.ToString() : null;
            }
            catch { return null; }
        }
    }
}
