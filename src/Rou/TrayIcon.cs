using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Rou.Interop;
using Rou.Services;

namespace Rou;

/// <summary>原生托盘图标（Shell_NotifyIcon），不引入 WinForms。</summary>
internal sealed class TrayIcon : IDisposable
{
    public const uint CallbackMessage = 0x8000 + 1;
    // 换过图标后递增此 ID，可让资源管理器丢弃旧的托盘缓存
    const uint IconId = 3;
    const int WM_LBUTTONUP = 0x0202;
    const int WM_RBUTTONUP = 0x0205;
    const int WM_LBUTTONDBLCLK = 0x0203;

    readonly IntPtr _hwnd;
    IntPtr _icon;
    bool _added;

    public event Action? Click;
    public event Action? RightClick;
    public event Action? DoubleClick;

    public TrayIcon(IntPtr hwnd, string tip)
    {
        _hwnd = hwnd;
        _icon = LoadAppIcon();
        var data = new Native.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<Native.NOTIFYICONDATA>(),
            hWnd = hwnd,
            uID = IconId,
            uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
            szTip = tip,
            szInfo = "",
            szInfoTitle = ""
        };
        _added = Native.Shell_NotifyIcon(Native.NIM_ADD, ref data);
        if (_added)
        {
            // 立刻 MODIFY 一次，强制刷新已缓存的图标位图
            data.uFlags = Native.NIF_ICON | Native.NIF_TIP;
            Native.Shell_NotifyIcon(Native.NIM_MODIFY, ref data);
        }
        Store.Log("tray icon added=" + _added);
    }

    static IntPtr LoadAppIcon()
    {
        // 优先用专用的高对比度托盘图标（内嵌资源），不依赖 exe 图标缓存
        try
        {
            var path = MaterializeTrayIcon();
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                var hIcon = Native.LoadImage(IntPtr.Zero, path, 1 /*IMAGE_ICON*/, 32, 32, 0x10 /*LR_LOADFROMFILE*/);
                if (hIcon != IntPtr.Zero) return hIcon;
            }
            else
            {
                Store.Log("tray icon file missing: " + path);
            }
        }
        catch (Exception ex) { Store.Log("tray icon resource: " + ex.Message); }

        // 兜底：从 exe 提取
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var large = new IntPtr[1];
                var small = new IntPtr[1];
                if (Native.ExtractIconEx(exe, 0, large, small, 1) > 0)
                {
                    if (large[0] != IntPtr.Zero) return large[0];
                    if (small[0] != IntPtr.Zero) return small[0];
                }
            }
        }
        catch { }
        return Native.LoadIcon(IntPtr.Zero, new IntPtr(32512)); // IDI_APPLICATION
    }

    /// <summary>把内嵌的 rou-tray.ico 释放到数据目录，供 LoadImage 使用。</summary>
    static string MaterializeTrayIcon()
    {
        var target = Path.Combine(Store.Root, "tray.ico");
        Directory.CreateDirectory(Store.Root);
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Rou.tray.ico");
        if (stream == null) return target;
        using var fs = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.Read);
        stream.CopyTo(fs);
        return target;
    }

    public void Handle(IntPtr lParam)
    {
        int msg = (int)(lParam.ToInt64() & 0xFFFF);
        switch (msg)
        {
            case WM_LBUTTONUP: Click?.Invoke(); break;
            case WM_LBUTTONDBLCLK: DoubleClick?.Invoke(); break;
            case WM_RBUTTONUP: RightClick?.Invoke(); break;
        }
    }

    public void Dispose()
    {
        if (!_added) return;
        var data = new Native.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<Native.NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = IconId,
            szTip = "",
            szInfo = "",
            szInfoTitle = ""
        };
        Native.Shell_NotifyIcon(Native.NIM_DELETE, ref data);
        _added = false;
        if (_icon != IntPtr.Zero) Native.DestroyIcon(_icon);
        _icon = IntPtr.Zero;
    }
}
