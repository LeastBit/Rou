using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

var list = args.ToList();
string? procName = null;
int wi = list.IndexOf("--window");
if (wi >= 0 && wi + 1 < list.Count)
{
    procName = list[wi + 1];
    list.RemoveRange(wi, 2);
}
var path = list.Count > 0 ? list[0] : "shot.png";

int comboIndex = list.IndexOf("--combo");
if (comboIndex >= 0 && comboIndex + 1 < list.Count)
{
    var parts = list[comboIndex + 1].ToLowerInvariant().Split('+');
    var down = new List<byte>();
    byte key = 0;
    foreach (var raw in parts)
    {
        var t = raw.Trim();
        byte vk = t switch
        {
            "ctrl" => 0x11,
            "shift" => 0x10,
            "alt" => 0x12,
            "win" => 0x5B,
            _ => (byte)char.ToUpperInvariant(t[0])
        };
        if (t is "ctrl" or "shift" or "alt" or "win") down.Add(vk); else key = vk;
    }
    foreach (var v in down) { W.keybd_event(v, 0, 0, UIntPtr.Zero); Thread.Sleep(25); }
    W.keybd_event(key, 0, 0, UIntPtr.Zero);
    Thread.Sleep(45);
    W.keybd_event(key, 0, 2, UIntPtr.Zero);
    Thread.Sleep(25);
    for (int i = down.Count - 1; i >= 0; i--) { W.keybd_event(down[i], 0, 2, UIntPtr.Zero); Thread.Sleep(25); }
    Thread.Sleep(420);
    list.RemoveRange(comboIndex, 2);
}

int keyIndex = list.IndexOf("--key");
if (keyIndex >= 0 && keyIndex + 1 < list.Count)
{
    byte vk = byte.Parse(list[keyIndex + 1], System.Globalization.NumberStyles.HexNumber);
    W.keybd_event(vk, 0, 0, UIntPtr.Zero);
    Thread.Sleep(40);
    W.keybd_event(vk, 0, 2, UIntPtr.Zero);
    Thread.Sleep(320);
    list.RemoveRange(keyIndex, 2);
}

int dragIndex = list.IndexOf("--drag");
if (dragIndex >= 0 && dragIndex + 1 < list.Count)
{
    var pp = list[dragIndex + 1].Split(',');
    int x1 = int.Parse(pp[0]), y1 = int.Parse(pp[1]), x2 = int.Parse(pp[2]), y2 = int.Parse(pp[3]);
    W.SetCursorPos(x1, y1);
    Thread.Sleep(150);
    W.mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
    Thread.Sleep(150);
    for (int i = 1; i <= 14; i++)
    {
        W.SetCursorPos(x1 + (x2 - x1) * i / 14, y1 + (y2 - y1) * i / 14);
        Thread.Sleep(22);
    }
    Thread.Sleep(180);
    W.mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
    Thread.Sleep(500);
    list.RemoveRange(dragIndex, 2);
}

int clickIndex = list.IndexOf("--click");
if (clickIndex >= 0 && clickIndex + 1 < list.Count)
{
    var xy = list[clickIndex + 1].Split(',');
    W.SetCursorPos(int.Parse(xy[0]), int.Parse(xy[1]));
    Thread.Sleep(120);
    W.mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
    Thread.Sleep(70);
    W.mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
    Thread.Sleep(420);
    list.RemoveRange(clickIndex, 2);
}

int ri = list.IndexOf("--rect");
Rectangle? forced = null;
if (ri >= 0 && ri + 1 < list.Count)
{
    var p4 = list[ri + 1].Split(',');
    forced = new Rectangle(int.Parse(p4[0]), int.Parse(p4[1]), int.Parse(p4[2]), int.Parse(p4[3]));
    list.RemoveRange(ri, 2);
}

Rectangle bounds;
if (forced != null)
{
    bounds = forced.Value;
}
else if (procName != null)
{
    var hwnd = FindWindow(procName);
    if (hwnd == IntPtr.Zero)
    {
        Console.WriteLine("window not found: " + procName);
        return 1;
    }
    W.GetWindowRect(hwnd, out var r);
    bounds = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
}
else
{
    bounds = Screen.PrimaryScreen!.Bounds;
}

using var bmp = new Bitmap(bounds.Width, bounds.Height);
using var g = Graphics.FromImage(bmp);
g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
bmp.Save(path, ImageFormat.Png);
Console.WriteLine($"{bounds.Width}x{bounds.Height} at {bounds.X},{bounds.Y} -> {path}");
return 0;

static IntPtr FindWindow(string procName)
{
    IntPtr found = IntPtr.Zero;
    W.EnumWindows((h, _) =>
    {
        if (!W.IsWindowVisible(h)) return true;
        W.GetWindowThreadProcessId(h, out uint pid);
        try
        {
            var p = Process.GetProcessById((int)pid);
            if (p.ProcessName.Equals(procName, StringComparison.OrdinalIgnoreCase))
            {
                W.GetWindowRect(h, out var r);
                if (r.Right - r.Left > 60)
                {
                    found = h;
                    return false;
                }
            }
        }
        catch { }
        return true;
    }, IntPtr.Zero);
    return found;
}

static class W
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);

    [DllImport("user32.dll")]
    public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
}
