using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace Wisp
{
    /// <summary>
    /// Writes app.ico (the .exe icon) from the same drawing code the app uses at runtime.
    /// "MakeIcon.exe app.ico --png dir" also saves PNG previews of every size into dir.
    /// </summary>
    static class MakeIcon
    {
        static int Main(string[] args)
        {
            string path = args.Length > 0 ? args[0] : "app.ico";
            var sizes = Logo.IconSizes;
            File.WriteAllBytes(path, Logo.BuildIco(sizes));
            Console.WriteLine("Icon written: " + Path.GetFullPath(path));
            if (args.Length > 2 && args[1] == "--png")
            {
                Directory.CreateDirectory(args[2]);
                foreach (int s in sizes)
                    using (var bmp = Logo.Render(s))
                        bmp.Save(Path.Combine(args[2], "logo" + s + ".png"), ImageFormat.Png);
            }
            return 0;
        }
    }
}
