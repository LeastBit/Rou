using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Rou.Models;
using Rou.Interop;

namespace Rou.Services;

/// <summary>
/// 命令行 / Agent 入口，例如：
///   Rou.exe --add "明天下午3点 评审会 #工作 !!"
///   Rou.exe --list
///   Rou.exe --done 3f2a1b
/// 输出为单行 JSON，便于脚本或 Agent 解析。
/// </summary>
public static class Cli
{
    static readonly Lazy<TextWriter> Out = new(OpenStdout);

    public static bool TryRun(string[] args)
    {
        if (args.Length == 0) return false;
        var verb = args[0].TrimStart('-', '/', '—').ToLowerInvariant();
        try
        {
            switch (verb)
            {
                case "add" or "a":
                    return Add(args.Skip(1).ToArray());
                case "list" or "ls":
                    List();
                    return true;
                case "done" or "d":
                    Done(args.Skip(1).ToArray());
                    return true;
                case "archive" or "his":
                    Archive(args.Length > 1 ? args[1] : "20");
                    return true;
                case "help" or "h" or "?":
                    Help();
                    return true;
                default:
                    return false;
            }
        }
        catch (Exception ex)
        {
            Store.Log("cli error: " + ex);
            Emit(new { ok = false, error = ex.Message });
            return true;
        }
    }

    static bool Add(string[] args)
    {
        var source = "cli";
        string text = "";
        var json = "";
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--json" or "-j") { json = i + 1 < args.Length ? args[i + 1] : ""; i++; }
            else if (args[i] is "--agent") { source = "agent"; }
            else text += (text.Length > 0 ? " " : "") + args[i];
        }

        if (!string.IsNullOrWhiteSpace(json))
        {
            var dtos = AgentInbox.ParseJson(json);
            var added = AgentInbox.Append(dtos, source);
            Emit(new { ok = true, added = added.Select(Describe) });
            return true;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            Emit(new { ok = false, error = "empty text" });
            return true;
        }

        var tasks = SplitTasks(text).Select(s => Nlp.Parse(s).ToTask(source)).ToList();
        var result = Store.Mutate(s => { s.Tasks.AddRange(tasks); return tasks; });
        Emit(new { ok = true, added = result.Select(Describe) });
        return true;
    }

    static void List()
    {
        var s = new Store();
        s.Load();
        var items = s.Tasks
            .OrderBy(t => t.Due ?? DateTime.MaxValue)
            .Select(t => new
            {
                t.Id,
                t.Title,
                due = t.Due?.ToString("yyyy-MM-dd HH:mm"),
                t.HasTime,
                t.Priority,
                t.Tags,
                t.Repeat,
                t.Source
            });
        Emit(new { ok = true, count = s.Tasks.Count, tasks = items });
    }

    static void Done(string[] args)
    {
        var key = string.Join(" ", args).Trim();
        if (key.Length == 0) { Emit(new { ok = false, error = "usage: --done <id>" }); return; }

        var outcome = Store.Mutate(s =>
        {
            var target = s.Tasks.FirstOrDefault(t => t.Id.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                      ?? s.Tasks.OrderBy(t => t.Due ?? DateTime.MaxValue)
                                .ElementAtOrDefault(int.TryParse(key, out var idx) ? idx - 1 : -1);
            if (target == null) return null;
            s.Tasks.Remove(target);
            target.CompletedAt = DateTime.Now;
            s.Archive.Insert(0, target);
            if (s.Archive.Count > 3000) s.Archive.RemoveRange(3000, s.Archive.Count - 3000);
            return target;
        }, saveTasks: true, saveArchive: true);

        Emit(outcome == null
            ? new { ok = false, error = "not found" }
            : new { ok = true, done = Describe(outcome) });
    }

    static void Archive(string count)
    {
        var s = new Store();
        s.Load();
        int n = int.TryParse(count, out var k) ? k : 20;
        Emit(new
        {
            ok = true,
            total = s.Archive.Count,
            items = s.Archive.Take(Math.Max(1, n)).Select(t => new
            {
                t.Id,
                t.Title,
                due = t.Due?.ToString("yyyy-MM-dd HH:mm"),
                completedAt = t.CompletedAt?.ToString("yyyy-MM-dd HH:mm"),
                t.Tags
            })
        });
    }

    static void Help()
    {
        Emit(new
        {
            ok = true,
            usage = new[]
            {
                "Rou.exe --add \"明天下午3点 评审会 #工作 !!\"",
                "Rou.exe --add --agent \"周五 10:00 复盘\"",
                "Rou.exe --list",
                "Rou.exe --done <id|序号>",
                "Rou.exe --archive 20"
            },
            dataDir = Store.Root,
            inbox = Store.InboxDir
        });
    }

    internal static object Describe(TaskItem t) => new
    {
        t.Id,
        t.Title,
        due = t.Due?.ToString("yyyy-MM-dd HH:mm"),
        t.HasTime,
        t.Priority,
        t.Tags,
        t.Repeat
    };

    internal static IEnumerable<string> SplitTasks(string text)
    {
        foreach (var part in text.Split(new[] { '；', ';', '\n', '|' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = part.Trim();
            if (p.Length == 0) continue;
            foreach (var sub in System.Text.RegularExpressions.Regex.Split(p, @"\s+然后\s*"))
                if (sub.Trim().Length > 0) yield return sub.Trim();
        }
    }

    static void Emit(object payload)
    {
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        try
        {
            Out.Value.WriteLine(json);
            Out.Value.Flush();
        }
        catch { }
        Store.Log("cli> " + json);
    }

    /// <summary>GUI 子系统程序需要手动接管父进程的控制台或重定向管道。</summary>
    static TextWriter OpenStdout()
    {
        try
        {
            var handle = Native.GetStdHandle(-11);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            {
                if (Native.AttachConsole(-1)) handle = Native.GetStdHandle(-11);
            }
            if (handle != IntPtr.Zero && handle != new IntPtr(-1))
            {
                var stream = new FileStream(new SafeFileHandle(handle, ownsHandle: false), FileAccess.Write, 4096);
                return new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            }
        }
        catch { }
        return TextWriter.Null;
    }
}
