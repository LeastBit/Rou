using Rou.Models;

namespace Rou.Services;

public static class Recurrence
{
    /// <summary>完成一条重复任务后，生成下一次实例；无重复则返回 null。</summary>
    public static TaskItem? Next(TaskItem t)
    {
        if (string.IsNullOrEmpty(t.Repeat) || t.Due == null) return null;
        var due = t.Due.Value;
        DateTime next = t.Repeat switch
        {
            "daily" => due.AddDays(1),
            "weekdays" => NextWorkday(due),
            var r when r.StartsWith("weekly:") => due.AddDays(7),
            var r when r.StartsWith("monthly:") => due.AddMonths(1),
            _ => due.AddDays(1)
        };
        var clone = t.Clone();
        clone.Due = next;
        clone.Repeat = t.Repeat;
        clone.CompletedAt = null;
        return clone;
    }

    static DateTime NextWorkday(DateTime d)
    {
        var n = d.AddDays(1);
        while (n.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) n = n.AddDays(1);
        return n;
    }
}
