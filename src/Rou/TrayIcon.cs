using System.Runtime.InteropServices;
using Rou.Interop;
using Rou.Services;

namespace Rou;

/// <summary>原生托盘图标（Shell_NotifyIcon），不引入 WinForms。</summary>
internal sealed class TrayIcon : IDisposable
{
    public const uint CallbackMessage = 0x8000 + 1;
    // 换过图标后递增此 ID，可让资源管理器丢弃旧的托盘缓存
    const uint IconId = 2;
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
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                // 取 32px 大图标，高 DPI 下由系统缩小，比 16px 放大更清晰
                var large = new IntPtr[1];
                var small = new IntPtr[1];
                if (Native.ExtractIconEx(path, 0, large, small, 1) > 0)
                {
                    if (large[0] != IntPtr.Zero) return large[0];
                    if (small[0] != IntPtr.Zero) return small[0];
                }
            }
        }
        catch { }
        return Native.LoadIcon(IntPtr.Zero, new IntPtr(32512)); // IDI_APPLICATION
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
