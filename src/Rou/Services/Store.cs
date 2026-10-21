using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rou.Models;

namespace Rou.Services;

/// <summary>
/// 本地数据仓库：%APPDATA%\Rou
///  · tasks.json    未完成
///  · archive.json  已归档（完成记录）
///  · settings.json 偏好
///  · inbox\        外部（Agent / 脚本）投递任务
/// 写入采用「临时文件 + 替换」，并以命名互斥量保护跨进程并发。
/// </summary>
public sealed class Store : IDisposable
{
    public static readonly string Root =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Rou");
    public static readonly string InboxDir = Path.Combine(Root, "inbox");
    public static readonly string InboxDoneDir = Path.Combine(InboxDir, "_done");
    public static readonly string LogPath = Path.Combine(Root, "rou.log");

    static readonly string TasksPath = Path.Combine(Root, "tasks.json");
    static readonly string ArchivePath = Path.Combine(Root, "archive.json");
    static readonly string SettingsPath = Path.Combine(Root, "settings.json");

    static readonly Mutex Gate = new(false, @"Local\Rou.Data.Gate");

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public List<TaskItem> Tasks { get; private set; } = new();
    public List<TaskItem> Archive { get; private set; } = new();
    public AppSettings Settings { get; private set; } = new();

    FileSystemWatcher? _watcher;
    DateTime _selfStamp = DateTime.MinValue;

    /// <summary>外部（其它进程）改动了数据文件。</summary>
    public event Action? ExternalChange;

    /// <summary>首次以 Rou 启动时，把旧版 Pebble 的数据接过来。</summary>
    static void MigrateLegacy()
    {
        try
        {
            if (Directory.Exists(Root)) return;
            var legacy = Path.Combine(Path.GetDirectoryName(Root)!, "Pebble");
            if (!Directory.Exists(legacy)) return;
            Directory.CreateDirectory(Root);
            foreach (var file in Directory.GetFiles(legacy, "*.json"))
                File.Copy(file, Path.Combine(Root, Path.GetFileName(file)), true);
            var inbox = Path.Combine(legacy, "inbox");
            if (Directory.Exists(inbox))
            {
                Directory.CreateDirectory(InboxDir);
                foreach (var file in Directory.GetFiles(inbox, "*.json"))
                    File.Copy(file, Path.Combine(InboxDir, Path.GetFileName(file)), true);
            }
            Log("migrated legacy data from " + legacy);
        }
        catch (Exception ex) { Log("migrate failed: " + ex.Message); }
    }

    /// <summary>最近一次读取是否全部成功；失败时禁止回写，避免把空数据覆盖上去。</summary>
    public bool LoadOk { get; private set; } = true;

    public void Load()
    {
        MigrateLegacy();
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(InboxDir);
        Directory.CreateDirectory(InboxDoneDir);

        LoadOk = true;
        if (TryRead(TasksPath, out List<TaskItem>? tasks)) { if (tasks != null) Tasks = tasks; }
        else LoadOk = false;
        if (TryRead(ArchivePath, out List<TaskItem>? archive)) { if (archive != null) Archive = archive; }
        else LoadOk = false;
        if (TryRead(SettingsPath, out AppSettings? settings)) { if (settings != null) Settings = settings; }
        else LoadOk = false;
    }

    public static List<TaskItem> ReadList(string path)
        => TryRead(path, out List<TaskItem>? list) && list != null ? list : new List<TaskItem>();

    public static T? ReadObject<T>(string path) where T : class
        => TryRead<T>(path, out var value) ? value : null;

    /// <summary>true = 读取成功（文件不存在也算成功，value 为 null）；false = 暂时读不到（被占用等）。</summary>
    static bool TryRead<T>(string path, out T? value) where T : class
    {
        value = null;
        string? text = ReadTextShared(path);
        if (text == null)
        {
            if (!File.Exists(path)) return true;
            Log("read failed (locked): " + path);
            return false;
        }
        if (string.IsNullOrWhiteSpace(text)) return true;
        try
        {
            value = JsonSerializer.Deserialize<T>(text, Json);
            return true;
        }
        catch (Exception ex)
        {
            // 内容损坏：留档后按空处理，避免反复失败
            try { File.Copy(path, path + ".bad-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true); } catch { }
            Log("read failed (corrupt): " + path + " :: " + ex.Message);
            return true;
        }
    }

    static string? ReadTextShared(string path)
    {
        for (int attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return "";
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs, System.Text.Encoding.UTF8);
                return reader.ReadToEnd();
            }
            catch (IOException) { Thread.Sleep(30); }
            catch (UnauthorizedAccessException) { Thread.Sleep(30); }
        }
        return null;
    }

    public void SaveTasks() => SaveList(TasksPath, Tasks);
    public void SaveArchive() => SaveList(ArchivePath, Archive);
    public void SaveSettings() => SaveObject(SettingsPath, Settings);

    void SaveList(string path, List<TaskItem> data) => SaveObject(path, data);

    void SaveObject<T>(string path, T data)
    {
        try
        {
            Directory.CreateDirectory(Root);
            var json = JsonSerializer.Serialize(data, Json);
            WriteAtomic(path, json);
            _selfStamp = DateTime.Now;
        }
        catch (Exception ex) { Log("save failed: " + path + " :: " + ex.Message); }
    }

    public static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        if (File.Exists(path))
        {
            // 允许其它进程短暂持有读句柄
            for (int i = 0; i < 5; i++)
            {
                try { File.Replace(tmp, path, null, true); return; }
                catch (IOException) { Thread.Sleep(30); }
                catch (UnauthorizedAccessException) { Thread.Sleep(30); }
            }
            File.Copy(tmp, path, true);
            try { File.Delete(tmp); } catch { }
        }
        else
        {
            File.Move(tmp, path);
        }
    }

    /// <summary>跨进程安全地修改任务列表（供 CLI 使用）。</summary>
    public static T Mutate<T>(Func<Store, T> action, bool saveTasks = true, bool saveArchive = false)
    {
        bool owned = false;
        try { owned = Gate.WaitOne(3000); } catch { }
        try
        {
            var s = new Store();
            s.Load();
            if (!s.LoadOk)
            {
                Log("mutate aborted: data file locked");
                throw new IOException("data file is locked, please retry");
            }
            var result = action(s);
            if (saveTasks) s.SaveTasks();
            if (saveArchive) s.SaveArchive();
            return result;
        }
        finally { if (owned) { try { Gate.ReleaseMutex(); } catch { } } }
    }

    public void Watch()
    {
        try
        {
            _watcher = new FileSystemWatcher(Root, "*.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
                IncludeSubdirectories = false
            };
            FileSystemWatcher? w = _watcher;
            w.Changed += OnFs;
            w.Created += OnFs;
            w.Renamed += OnFs;
        }
        catch (Exception ex) { Log("watch failed: " + ex.Message); }
    }

    void OnFs(object sender, FileSystemEventArgs e)
    {
        if (DateTime.Now - _selfStamp < TimeSpan.FromMilliseconds(600)) return;
        Task.Delay(320).ContinueWith(_ => ExternalChange?.Invoke());
    }

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Root);
            var line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
            var fi = new FileInfo(LogPath);
            if (fi.Exists && fi.Length > 256 * 1024) fi.Delete();
            File.AppendAllText(LogPath, line);
        }
        catch { }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
    }
}
