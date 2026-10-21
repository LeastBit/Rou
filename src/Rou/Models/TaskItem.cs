namespace Rou.Models;

/// <summary>一条日程/任务。JSON 字段名保持简短，方便 Agent 直接读写。</summary>
public sealed class TaskItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Title { get; set; } = "";
    public DateTime? Due { get; set; }
    /// <summary>false 表示「全天」，不显示具体时刻。</summary>
    public bool HasTime { get; set; } = true;
    /// <summary>0 普通 / 1 低 / 2 中 / 3 高</summary>
    public int Priority { get; set; }
    public List<string> Tags { get; set; } = new();
    /// <summary>null | daily | weekdays | weekly:0-6 | monthly:1-31</summary>
    public string? Repeat { get; set; }
    public string? Notes { get; set; }
    /// <summary>manual | agent | cli | inbox</summary>
    public string Source { get; set; } = "manual";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? CompletedAt { get; set; }

    public TaskItem Clone() => new()
    {
        Id = Guid.NewGuid().ToString("N")[..12],
        Title = Title,
        Due = Due,
        HasTime = HasTime,
        Priority = Priority,
        Tags = new List<string>(Tags),
        Repeat = Repeat,
        Notes = Notes,
        Source = Source,
        CreatedAt = DateTime.Now
    };
}

public sealed class AppSettings
{
    public string Theme { get; set; } = "light";          // light | dark | auto
    public bool Topmost { get; set; } = true;
    public bool DesktopLayer { get; set; } = false;        // 失焦时沉到桌面层
    public bool Autostart { get; set; } = false;
    public bool Compact { get; set; } = false;
    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool Seeded { get; set; }
    /// <summary>贴边吸附状态：none | left | right | top</summary>
    public string DockEdge { get; set; } = "none";
}
