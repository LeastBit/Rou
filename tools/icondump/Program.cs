using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

// icondump <exeOrIco> <out.png> [size]
var src = args[0];
var outPath = args[1];
int size = args.Length > 2 ? int.Parse(args[2]) : 256;

IntPtr h = IntPtr.Zero;
if (src.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
{
    // 直接从 ico 取最大帧
    h = LoadImage(IntPtr.Zero, src, 1 /*IMAGE_ICON*/, size, size, 0x10 /*LR_LOADFROMFILE*/);
}
if (h == IntPtr.Zero)
{
    var small = new IntPtr[1];
    ExtractIconEx(src, 0, null, small, 1);
    h = small[0];
}
if (h == IntPtr.Zero)
{
    Console.WriteLine("no icon found");
    return 1;
}

using var icon = Icon.FromHandle(h);
using var bmp = icon.ToBitmap();
using var scaled = new Bitmap(size, size, PixelFormat.Format32bppArgb);
using (var g = Graphics.FromImage(scaled))
{
    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
    g.Clear(Color.White);
    g.DrawImage(bmp, 0, 0, size, size);
}
scaled.Save(outPath, ImageFormat.Png);
Console.WriteLine($"{src} -> {outPath} ({bmp.Width}x{bmp.Height})");
return 0;

[DllImport("user32.dll", CharSet = CharSet.Unicode)]
static extern IntPtr LoadImage(IntPtr hinst, string name, uint type, int cx, int cy, uint load);

[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
static extern int ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, int n);
