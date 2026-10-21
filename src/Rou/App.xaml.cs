using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Rou.Services;

namespace Rou;

public partial class App : Application
{
    const string InstanceName = @"Local\Rou.Instance";
    const string WakeName = @"Local\Rou.Wake";

    Mutex? _instance;
    EventWaitHandle? _wake;
    Store? _store;
    internal MainWindow? Widget { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // 小尺寸浮窗强制使用软件渲染：避免在无独显/虚拟机上创建 D3D/WARP 设备（内存占用可降数十 MB）
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            Store.Log("unhandled: " + args.Exception);
            args.Handled = true;
        };

        // 命令行模式：不创建界面，处理完立即退出
        if (e.Args.Length > 0 && Cli.TryRun(e.Args))
        {
            Shutdown(0);
            return;
        }

        _instance = new Mutex(true, InstanceName, out bool first);
        if (!first)
        {
            Wake();
            Shutdown(0);
            return;
        }

        _store = new Store();
        bool firstRun = !File.Exists(Path.Combine(Store.Root, "tasks.json"));
        _store.Load();
        if (firstRun) Seed(_store);

        ApplyTheme(_store.Settings.Theme);

        Widget = new MainWindow(_store);
        MainWindow = Widget;
        Widget.Show();

        _store.Watch();
        _store.ExternalChange += () => Dispatcher.BeginInvoke(() => Widget?.ReloadFromDisk());

        // 第二个实例启动时唤醒本窗口
        _wake = new EventWaitHandle(false, EventResetMode.AutoReset, WakeName);
        var t = new Thread(() =>
        {
            while (true)
            {
                if (!_wake.WaitOne()) continue;
                Dispatcher.BeginInvoke(new Action(() => Widget?.WakeUp()));
            }
        }) { IsBackground = true, Name = "rou-wake" };
        t.Start();
    }

    static void Wake()
    {
        try { EventWaitHandle.OpenExisting(WakeName).Set(); } catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Widget?.Shutdown();
        _store?.Dispose();
        try { _instance?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }

    static void Seed(Store store)
    {
        var today = DateTime.Today;
        store.Tasks.Add(new Models.TaskItem
        {
            Title = "晨会 · 同步本周进度",
            Due = today.AddHours(9).AddMinutes(30),
            Tags = { "工作" },
            Priority = 2,
            Source = "seed"
        });
        store.Tasks.Add(new Models.TaskItem
        {
            Title = "写周报",
            Due = today.AddHours(16),
            Tags = { "工作" },
            Priority = 1,
            Source = "seed"
        });
        store.Tasks.Add(new Models.TaskItem
        {
            Title = "买咖啡豆",
            Due = null,
            HasTime = false,
            Tags = { "生活" },
            Source = "seed"
        });
        store.Settings.Seeded = true;
        store.SaveTasks();
        store.SaveSettings();
    }

    public static void ApplyTheme(string theme)
    {
        bool dark = theme == "dark" || (theme == "auto" && IsSystemDark());
        var uri = new Uri(dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative);
        var app = (App)Current;
        app.Resources.MergedDictionaries[0] = new ResourceDictionary { Source = uri };
    }

    static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }
}
