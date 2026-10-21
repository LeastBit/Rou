using System.ComponentModel;
using System.Runtime.CompilerServices;
using Rou.Services;

namespace Rou.Models;

/// <summary>列表行视图模型（轻量包装，无第三方依赖）。</summary>
public sealed class TaskRow : INotifyPropertyChanged
{
    public TaskItem Model { get; }

    public TaskRow(TaskItem model)
    {
        Model = model;
        Refresh();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public string Id => Model.Id;

    public string Title
    {
        get => Model.Title;
        set
        {
            if (Model.Title == value) return;
            Model.Title = value;
            Raise();
        }
    }

    public string TimeText { get; private set; } = "";
    public string Meta { get; private set; } = "";
    public bool HasMeta { get; private set; }
    public string Group { get; private set; } = "";
    public int Rank { get; private set; }
    public bool IsOverdue { get; private set; }
    public int Priority { get; private set; }
    public bool HasPriority => Priority > 0;

    bool _editing;
    public bool IsEditing
    {
        get => _editing;
        set { if (_editing == value) return; _editing = value; Raise(); }
    }

    public void Refresh()
    {
        var t = Model;
        var today = DateTime.Today;
        var due = t.Due;
        var reference = due is null ? DateTime.MaxValue
            : t.HasTime ? due.Value : due.Value.Date.AddDays(1);
        IsOverdue = reference < DateTime.Now;
        Priority = t.Priority;

        TimeText = due is null ? "待定" : t.HasTime ? due.Value.ToString("HH:mm") : "全天";

        var bits = new List<string>(2);
        if (t.Tags.Count > 0) bits.Add(string.Join("  ", t.Tags.Select(x => "#" + x)));
        if (!string.IsNullOrEmpty(t.Repeat)) bits.Add(RepeatLabel(t.Repeat!));
        Meta = string.Join("   ", bits);
        HasMeta = Meta.Length > 0;

        if (due is null) { Rank = 5; Group = "待安排"; }
        else if (IsOverdue && due.Value.Date <= today) { Rank = 0; Group = "已逾期"; }
        else if (due.Value.Date == today) { Rank = 1; Group = "今天 · " + Nlp.WeekCn[(int)due.Value.DayOfWeek]; }
        else if (due.Value.Date == today.AddDays(1)) { Rank = 2; Group = "明天 · " + Nlp.WeekCn[(int)due.Value.DayOfWeek]; }
        else if (due.Value.Date < today.AddDays(8)) { Rank = 3; Group = Nlp.DayLabel(due.Value); }
        else { Rank = 4; Group = "以后"; }
    }

    public static string RepeatLabel(string r)
    {
        if (r == "daily") return "每天";
        if (r == "weekdays") return "工作日";
        if (r.StartsWith("weekly:")) return "每周" + Nlp.WeekCn[int.Parse(r[7..])][1];
        if (r.StartsWith("monthly:")) return "每月" + r[8..] + "日";
        return "";
    }

    public string EditText
    {
        get => Model.Title;
        set => Model.Title = value;
    }
}

public sealed class ArchiveRow
{
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Day { get; init; } = "";
}
