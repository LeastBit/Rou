using System.Globalization;
using System.IO;
using System.Text.Json;
using Rou.Models;

namespace Rou.Services;

/// <summary>
/// Agent / 脚本投递通道：往 %APPDATA%\Rou\inbox 放一个 .json 文件即可，
/// 组件运行时会自动摄取、入库，并把文件移到 inbox\_done。
/// 支持三种写法：
///   {"tasks":[{"title":"写周报","due":"2026-10-23 14:00","priority":2,"tags":["工作"]}]}
///   {"raw":["明天下午3点 评审会 #工作"]}
///   ["明天9点 交报告","买咖啡豆"]
/// </summary>
public static class AgentInbox
{
    public static List<TaskItem> Drain()
    {
        var added = new List<TaskItem>();
        try
        {
            if (!Directory.Exists(Store.InboxDir)) return added;
            foreach (var file in Directory.GetFiles(Store.InboxDir, "*.json").OrderBy(f => f))
            {
                try
                {
                    var text = File.ReadAllText(file);
                    var items = ParseJson(text);
                    if (items.Count > 0) added.AddRange(Store.Mutate(s =>
                    {
                        s.Tasks.AddRange(items);
                        return items;
                    }));
                    Move(file, Store.InboxDoneDir, null);
                }
                catch (Exception ex)
                {
                    Store.Log("inbox failed: " + file + " :: " + ex.Message);
                    Move(file, Store.InboxDoneDir, ".error");
                }
            }
        }
        catch (Exception ex) { Store.Log("inbox drain: " + ex.Message); }
        return added;
    }

    /// <summary>供 CLI --json 使用：解析并写入任务库，返回实际新增的条目。</summary>
    public static List<TaskItem> Append(List<TaskItem> items, string source)
    {
        foreach (var it in items) it.Source = source;
        if (items.Count == 0) return items;
        return Store.Mutate(s => { s.Tasks.AddRange(items); return items; });
    }

    static void Move(string file, string dir, string? suffix)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var name = Path.GetFileNameWithoutExtension(file);
            var ext = suffix == null ? ".json" : suffix + ".json";
            var target = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd-HHmmss-") + name + ext);
            if (File.Exists(target)) target = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff-") + name + ext);
            File.Move(file, target, true);
        }
        catch (Exception ex) { Store.Log("inbox move: " + ex.Message); }
    }

    public static List<TaskItem> ParseJson(string json)
    {
        var list = new List<TaskItem>();
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in root.EnumerateArray()) AddNode(list, el);
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("tasks", out var tasks) && tasks.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in tasks.EnumerateArray()) AddNode(list, el);
            }
            else if (root.TryGetProperty("raw", out var raw))
            {
                if (raw.ValueKind == JsonValueKind.Array)
                    foreach (var el in raw.EnumerateArray()) AddNode(list, el);
                else AddNode(list, raw);
            }
            else if (root.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                AddNode(list, text);
            }
            else AddNode(list, root);
        }
        else if (root.ValueKind == JsonValueKind.String)
        {
            AddNode(list, root);
        }
        return list;
    }

    static void AddNode(List<TaskItem> list, JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.String)
        {
            var s = el.GetString();
            if (!string.IsNullOrWhiteSpace(s)) list.Add(Nlp.Parse(s).ToTask("agent"));
            return;
        }
        if (el.ValueKind != JsonValueKind.Object) return;

        var raw = GetString(el, "raw") ?? GetString(el, "text");
        var title = GetString(el, "title") ?? GetString(el, "task") ?? GetString(el, "name");
        var parsed = Nlp.Parse(!string.IsNullOrWhiteSpace(raw) ? raw : title ?? "");
        var item = parsed.ToTask("agent");

        // 结构化字段覆盖自然语言结果
        var dueText = GetString(el, "due") ?? GetString(el, "when") ?? GetString(el, "time");
        if (!string.IsNullOrWhiteSpace(dueText))
        {
            var due = ParseDue(dueText, out bool hasTime);
            if (due != null) { item.Due = due; item.HasTime = hasTime; }
        }
        if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(raw) == false)
            item.Title = title.Trim();

        if (el.TryGetProperty("priority", out var pri))
        {
            item.Priority = pri.ValueKind switch
            {
                JsonValueKind.Number => Math.Clamp(pri.GetInt32(), 0, 3),
                JsonValueKind.String => pri.GetString() switch
                {
                    "高" or "紧急" or "high" or "h" or "p1" => 3,
                    "中" or "medium" or "m" or "p2" => 2,
                    "低" or "low" or "l" or "p3" => 1,
                    _ => 0
                },
                _ => item.Priority
            };
        }
        if (el.TryGetProperty("tags", out var tags))
        {
            var list2 = new List<string>();
            if (tags.ValueKind == JsonValueKind.Array)
                foreach (var t in tags.EnumerateArray())
                    if (t.ValueKind == JsonValueKind.String && t.GetString() is { Length: > 0 } sv) list2.Add(sv.Trim().TrimStart('#', '＃'));
            else if (tags.ValueKind == JsonValueKind.String)
                list2.AddRange((tags.GetString() ?? "").Split(new[] { ',', '，', ' ' }, StringSplitOptions.RemoveEmptyEntries));
            if (list2.Count > 0) item.Tags = list2;
        }
        if (el.TryGetProperty("repeat", out var rep) && rep.ValueKind == JsonValueKind.String)
        {
            var r = rep.GetString();
            item.Repeat = string.IsNullOrWhiteSpace(r) || r == "none" ? null : r.Trim();
        }
        if (el.TryGetProperty("hasTime", out var ht) && (ht.ValueKind == JsonValueKind.True || ht.ValueKind == JsonValueKind.False))
            item.HasTime = ht.GetBoolean();
        var notes = GetString(el, "notes") ?? GetString(el, "note") ?? GetString(el, "desc");
        if (!string.IsNullOrWhiteSpace(notes)) item.Notes = notes;

        if (string.IsNullOrWhiteSpace(item.Title)) item.Title = "新任务";
        list.Add(item);
    }

    static string? GetString(JsonElement el, string name)
        => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    public static DateTime? ParseDue(string text, out bool hasTime)
    {
        hasTime = true;
        text = text.Trim();
        if (text.Length == 0) return null;

        if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var ts) && text.Length <= 5)
            return DateTime.Today.Add(ts);

        var formats = new[] { "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd", "yyyy/M/d H:mm", "yyyy/M/d", "M/d H:mm", "M/d" };
        if (DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
        {
            hasTime = text.Contains(':');
            if (exact.Year == 1) exact = new DateTime(DateTime.Today.Year, exact.Month, exact.Day, exact.Hour, exact.Minute, 0);
            return DateTime.SpecifyKind(exact, DateTimeKind.Local);
        }
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            hasTime = text.Contains(':');
            return DateTime.SpecifyKind(dt, DateTimeKind.Local);
        }

        var p = Nlp.Parse(text);
        hasTime = p.ExplicitTime;
        return p.Due;
    }
}
