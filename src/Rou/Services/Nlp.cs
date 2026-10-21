using System.Globalization;
using System.Text.RegularExpressions;
using Rou.Models;

namespace Rou.Services;

public sealed class ParseResult
{
    public string Title = "";
    public DateTime? Due;
    public bool HasTime = true;
    public int Priority;
    public List<string> Tags = new();
    public string? Repeat;
    public bool ExplicitDate;
    public bool ExplicitTime;

    public TaskItem ToTask(string source = "manual") => new()
    {
        Title = Title,
        Due = Due,
        HasTime = HasTime,
        Priority = Priority,
        Tags = Tags,
        Repeat = Repeat,
        Source = source,
        CreatedAt = DateTime.Now
    };
}

/// <summary>
/// 纯本地中文自然语言解析：不联网、不依赖模型，单条耗时 &lt; 1ms。
/// 支持：绝对日期、相对日期、星期、时段、重复规则、优先级、标签。
/// </summary>
public static class Nlp
{
    const RegexOptions O = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    static readonly Regex RxRange = new(@"(?<h1>\d{1,2})\s*[:：]\s*(?<m1>\d{2})\s*[-~～到至]\s*(?<h2>\d{1,2})\s*[:：]?\s*(?<m2>\d{2})?", O);
    static readonly Regex RxClock = new(@"(?<h>\d{1,2})\s*[:：]\s*(?<m>\d{2})", O);
    static readonly Regex RxDian = new(@"(?<h>\d{1,2})\s*[点點时時](?<m>半|\d{1,2}\s*分?)?", O);
    static readonly Regex RxAmPm = new(@"(凌晨|清晨|早上|早晨|上午|中午|下午|傍晚|晚上|夜间|夜里|今晚|今夜|明晚|明早|今早)", O);
    static readonly Regex RxTag = new(@"[#＃](?<t>[^\s#＃,，;；、]+)", O);
    static readonly Regex RxBang = new(@"!{1,3}|！{1,3}", O);
    static readonly Regex RxPrioWord = new(@"(紧急|加急|特急|重要|优先|一般)(任务)?", O);
    static readonly Regex RxRepeatDay = new(@"(每天|每日|天天)", O);
    static readonly Regex RxRepeatWeekday = new(@"工作日", O);
    static readonly Regex RxRepeatWeek = new(@"每(个)?(周|星期|礼拜)\s*(?<w>[一二三四五六日天1-7])?", O);
    static readonly Regex RxRepeatMonth = new(@"每(个)?月\s*(?<d>\d{1,2})\s*[日号]?", O);
    static readonly Regex RxYmd = new(@"(?<y>\d{4})\s*[-/.年]\s*(?<mo>\d{1,2})\s*[-/.月]\s*(?<d>\d{1,2})\s*[日号]?", O);
    static readonly Regex RxMd = new(@"(?<mo>\d{1,2})\s*[月/]\s*(?<d>\d{1,2})\s*[日号]?", O);
    static readonly Regex RxRelDay = new(@"(大后天|后天|明天|明日|明儿|今天|今日|今儿|昨天|前天)", O);
    static readonly Regex RxWeekday = new(@"(?<pre>下下|下个|下|这个|这|本)?\s*(?:周|星期|礼拜)\s*(?<w>[一二三四五六日天1-7])", O);
    static readonly Regex RxOffset = new(@"(?<n>\d{1,3})\s*(?<u>天后|天以后|天之后|日后|周后|周以后|星期后|个?月后|个?月以后)", O);
    static readonly Regex RxMonthEnd = new(@"(月底|月末)", O);
    static readonly Regex RxAllDay = new(@"(全天|整日)", O);

    public static ParseResult Parse(string? raw)
    {
        var r = new ParseResult();
        var s = (raw ?? "").Trim();
        if (s.Length == 0) { r.Title = "新任务"; return r; }

        var today = DateTime.Today;
        DateTime? date = null;
        int? hour = null;
        int minute = 0;
        string? ampm = null;

        // ---------- 标签 ----------
        s = Take(s, RxTag, m => { r.Tags.Add(m.Groups["t"].Value.Trim()); return true; });

        // ---------- 优先级 ----------
        s = Take(s, RxBang, m => { r.Priority = Math.Clamp(m.Value.Length, 1, 3); return true; });
        s = Take(s, RxPrioWord, m =>
        {
            r.Priority = m.Groups[1].Value switch
            {
                "紧急" or "加急" or "特急" => 3,
                "重要" or "优先" => 2,
                _ => 1
            };
            return true;
        });

        // ---------- 重复 ----------
        s = Take(s, RxRepeatDay, _ => { r.Repeat = "daily"; return true; });
        s = Take(s, RxRepeatWeekday, _ => { r.Repeat = "weekdays"; return true; });
        s = Take(s, RxRepeatWeek, m =>
        {
            var w = m.Groups["w"].Value;
            if (w.Length > 0) r.Repeat = "weekly:" + (int)Dow(w);
            else r.Repeat = "weekly:" + (int)today.DayOfWeek;
            if (r.Repeat.StartsWith("weekly:") && w.Length > 0) date = NextWeekday(today, Dow(w), true);
            else if (w.Length == 0) date = today.AddDays(7);
            return true;
        });
        s = Take(s, RxRepeatMonth, m =>
        {
            if (int.TryParse(m.Groups["d"].Value, out var d) && d is >= 1 and <= 31)
            {
                r.Repeat = "monthly:" + d;
                date = SafeDate(today.Year, today.Month, d) ?? today;
                if (date < today) date = SafeDate(today.Year, today.Month + 1, d) ?? date;
            }
            return true;
        });

        // ---------- 绝对日期 ----------
        s = Take(s, RxYmd, m =>
        {
            if (int.TryParse(m.Groups["y"].Value, out var y) &&
                int.TryParse(m.Groups["mo"].Value, out var mo) &&
                int.TryParse(m.Groups["d"].Value, out var d) && mo is >= 1 and <= 12 && d is >= 1 and <= 31)
            {
                date = SafeDate(y, mo, d);
                if (date != null) r.ExplicitDate = true;
            }
            return true;
        });
        if (date == null)
        {
            s = Take(s, RxMd, m =>
            {
                if (int.TryParse(m.Groups["mo"].Value, out var mo) &&
                    int.TryParse(m.Groups["d"].Value, out var d) && mo is >= 1 and <= 12 && d is >= 1 and <= 31)
                {
                    var cand = SafeDate(today.Year, mo, d) ?? SafeDate(today.Year + 1, mo, d);
                    if (cand != null)
                    {
                        if (cand.Value < today.AddDays(-60)) cand = SafeDate(today.Year + 1, mo, d) ?? cand;
                        date = cand; r.ExplicitDate = true;
                    }
                }
                return true;
            });
        }

        // ---------- 相对日期 ----------
        s = Take(s, RxMonthEnd, _ => { date = new DateTime(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month)); r.ExplicitDate = true; return true; });
        s = Take(s, RxOffset, m =>
        {
            if (int.TryParse(m.Groups["n"].Value, out var n))
            {
                var u = m.Groups["u"].Value;
                date = u.StartsWith("周") || u.StartsWith("星期") ? today.AddDays(7 * n)
                     : u.Contains("月") ? today.AddMonths(n)
                     : today.AddDays(n);
                r.ExplicitDate = true;
            }
            return true;
        });
        s = Take(s, RxRelDay, m =>
        {
            date = m.Groups[1].Value switch
            {
                "大后天" => today.AddDays(3),
                "后天" => today.AddDays(2),
                "明天" or "明日" or "明儿" => today.AddDays(1),
                "昨天" => today.AddDays(-1),
                "前天" => today.AddDays(-2),
                _ => today
            };
            r.ExplicitDate = true;
            return true;
        });
        s = Take(s, RxWeekday, m =>
        {
            var w = Dow(m.Groups["w"].Value);
            var pre = m.Groups["pre"].Value;
            bool next = pre is "下" or "下个" or "下下";
            date = NextWeekday(today, w, next);
            r.ExplicitDate = true;
            return true;
        });

        // ---------- 时间 ----------
        s = Take(s, RxAmPm, m =>
        {
            ampm = m.Groups[1].Value;
            if (ampm is "今晚" or "今夜") { date = today; r.ExplicitDate = true; }
            else if (ampm is "明晚" or "明早") { date = today.AddDays(1); r.ExplicitDate = true; }
            else if (ampm is "今早") { date = today; r.ExplicitDate = true; }
            return true;
        });
        s = Take(s, RxRange, m =>
        {
            if (int.TryParse(m.Groups["h1"].Value, out var h1))
            {
                hour = h1;
                minute = int.TryParse(m.Groups["m1"].Value, out var m1) ? m1 : 0;
                r.ExplicitTime = true;
            }
            return true;
        });
        if (hour == null)
        {
            s = Take(s, RxClock, m =>
            {
                if (int.TryParse(m.Groups["h"].Value, out var h))
                {
                    hour = h;
                    minute = int.TryParse(m.Groups["m"].Value, out var mi) ? mi : 0;
                    r.ExplicitTime = true;
                }
                return true;
            });
        }
        if (hour == null)
        {
            s = Take(s, RxDian, m =>
            {
                if (int.TryParse(m.Groups["h"].Value, out var h))
                {
                    hour = h;
                    var mm = m.Groups["m"].Value;
                    minute = mm.Contains('半') ? 30 : int.TryParse(mm.Replace("分", "").Trim(), out var mi) ? mi : 0;
                    r.ExplicitTime = true;
                }
                return true;
            });
        }
        s = Take(s, RxAllDay, _ => { r.HasTime = false; r.ExplicitTime = true; return true; });

        // ---------- 组装 ----------
        if (hour is >= 0 and <= 23)
        {
            int h = hour.Value;
            if (!string.IsNullOrEmpty(ampm))
            {
                switch (ampm)
                {
                    case "下午" or "傍晚" or "晚上" or "夜间" or "夜里" or "今晚" or "今夜" or "明晚":
                        if (h < 12) h += 12;
                        break;
                    case "中午":
                        if (h <= 6) h += 12;
                        break;
                    case "凌晨":
                    case "明早":
                    case "今早":
                        if (h == 12) h = 0;
                        break;
                }
            }
            else if (h is >= 1 and <= 6)
            {
                h += 12; // 「3点」默认按下午理解
            }
            minute = Math.Clamp(minute, 0, 59);
            var day = date ?? today;
            var due = day.AddHours(h).AddMinutes(minute);
            if (date == null && due < DateTime.Now)
            {
                // 只说时刻且已过点 → 顺延到明天
                due = due.AddDays(1);
            }
            r.Due = due;
            r.HasTime = true;
        }
        else if (date != null)
        {
            r.Due = date.Value.AddHours(9);
            r.HasTime = false;
        }

        r.Title = CleanTitle(s, raw);
        return r;
    }

    static string CleanTitle(string s, string original)
    {
        s = Regex.Replace(s, @"[[(（【]\s*[)）】\]]", " ");
        s = Regex.Replace(s, @"\s{2,}", " ").Trim();
        s = s.Trim(' ', ',', '，', '。', '.', '-', '·', '、', ':', '：', ';', '；');
        s = Regex.Replace(s, @"^(提醒我|提醒一下|记得|帮我|帮忙|我要|添加任务|新建任务|添加|新建|todo[:：]?)\s*", "", RegexOptions.IgnoreCase);
        s = s.Trim(' ', ',', '，', '。', '.', '-', '·', '、');
        if (s.Length == 0) s = Regex.Replace(original, @"[#＃!！]", " ").Trim();
        return s.Length == 0 ? "新任务" : s;
    }

    static string Take(string s, Regex rx, Func<Match, bool> apply)
    {
        for (int guard = 0; guard < 12; guard++)
        {
            var m = rx.Match(s);
            if (!m.Success) break;
            if (!apply(m)) break;
            s = s.Remove(m.Index, m.Length);
        }
        return s;
    }

    static DayOfWeek Dow(string w) => w switch
    {
        "一" or "1" => DayOfWeek.Monday,
        "二" or "2" => DayOfWeek.Tuesday,
        "三" or "3" => DayOfWeek.Wednesday,
        "四" or "4" => DayOfWeek.Thursday,
        "五" or "5" => DayOfWeek.Friday,
        "六" or "6" => DayOfWeek.Saturday,
        _ => DayOfWeek.Sunday
    };

    static DateTime NextWeekday(DateTime from, DayOfWeek target, bool forceNext)
    {
        int delta = ((int)target - (int)from.DayOfWeek + 7) % 7;
        if (delta == 0 && forceNext) delta = 7;
        return from.AddDays(delta);
    }

    static DateTime? SafeDate(int y, int m, int d)
    {
        try
        {
            if (m > 12) { y += (m - 1) / 12; m = (m - 1) % 12 + 1; }
            if (m < 1) { y -= 1; m = 12; }
            var dim = DateTime.DaysInMonth(y, m);
            return new DateTime(y, m, Math.Min(d, dim));
        }
        catch { return null; }
    }

    // ---------- 展示 ----------
    public static readonly string[] WeekCn = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };

    public static string DayLabel(DateTime d)
    {
        var today = DateTime.Today;
        if (d.Date == today) return "今天 · " + WeekCn[(int)d.DayOfWeek];
        if (d.Date == today.AddDays(1)) return "明天 · " + WeekCn[(int)d.DayOfWeek];
        if (d.Date == today.AddDays(-1)) return "昨天 · " + WeekCn[(int)d.DayOfWeek];
        if (d.Year != today.Year) return d.ToString("yyyy年M月d日");
        return d.ToString("M月d日") + " · " + WeekCn[(int)d.DayOfWeek];
    }

    public static string Describe(TaskItem t)
    {
        if (t.Due == null) return "待安排";
        var d = t.Due.Value;
        var day = d.Date == DateTime.Today ? "今天"
                : d.Date == DateTime.Today.AddDays(1) ? "明天"
                : d.Date == DateTime.Today.AddDays(2) ? "后天"
                : d.ToString("M月d日");
        return t.HasTime ? $"{day} {d:HH:mm}" : day;
    }
}
