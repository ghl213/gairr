using System.Text.RegularExpressions;

namespace GAIRR.Core.Cron;

/// <summary>
/// 轻量级 Cron 表达式解析器：支持标准 5 字段（分 时 日 月 周）。
/// 仅覆盖自动任务常用能力：*、数字、范围、列表、步长（/）。
/// 不处理 L/#/W 等高级语法；遇高级语法回退到整点触发，避免崩溃。
/// </summary>
public sealed class CronExpression
{
    readonly int[] minutes;
    readonly int[] hours;
    readonly int[] daysOfMonth;
    readonly int[] months;
    readonly DayOfWeek[] daysOfWeek;

    public string Expression { get; }

    CronExpression(string expr, int[] m, int[] h, int[] dom, int[] mo, DayOfWeek[] dow)
    {
        Expression = expr;
        minutes = m;
        hours = h;
        daysOfMonth = dom;
        months = mo;
        daysOfWeek = dow;
    }

    /// <summary>尝试解析 5 字段 Cron 表达式；失败返回 null。</summary>
    public static CronExpression? TryParse(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return null;
        var parts = expression.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5) return null;
        try
        {
            var m = ParseField(parts[0], 0, 59);
            var h = ParseField(parts[1], 0, 23);
            var dom = ParseField(parts[2], 1, 31);
            var mo = ParseField(parts[3], 1, 12);
            var dow = ParseDayOfWeek(parts[4]);
            if (m.Length == 0 || h.Length == 0 || dom.Length == 0 || mo.Length == 0 || dow.Length == 0) return null;
            return new CronExpression(expression.Trim(), m, h, dom, mo, dow);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>计算从 start 开始的下一次命中时刻；若当前正好命中则返回下一命中，避免立即重复执行。</summary>
    public DateTime GetNextOccurrence(DateTime start)
    {
        var t = new DateTime(start.Year, start.Month, start.Day, start.Hour, start.Minute, 0).AddMinutes(1);
        // 安全兜底：最多向前搜索 4 年
        var limit = start.AddYears(4);
        while (t <= limit)
        {
            if (months.Contains(t.Month) &&
                daysOfMonth.Contains(t.Day) &&
                daysOfWeek.Contains(t.DayOfWeek) &&
                hours.Contains(t.Hour) &&
                minutes.Contains(t.Minute))
            {
                return t;
            }
            t = t.AddMinutes(1);
        }
        return start; // 兜底：找不到时返回原值
    }

    static int[] ParseField(string field, int min, int max)
    {
        var list = new List<int>();
        foreach (var part in field.Split(','))
        {
            var step = 1;
            var rangePart = part;
            if (rangePart.Contains('/'))
            {
                var stepSplit = rangePart.Split('/');
                if (stepSplit.Length != 2 || !int.TryParse(stepSplit[1], out step) || step < 1) step = 1;
                rangePart = stepSplit[0];
            }
            var (lo, hi) = rangePart == "*" ? (min, max) : ParseRange(rangePart, min, max);
            for (var i = lo; i <= hi; i += step) list.Add(i);
        }
        return list.Distinct().OrderBy(x => x).ToArray();
    }

    static (int lo, int hi) ParseRange(string part, int min, int max)
    {
        if (part == "*") return (min, max);
        if (part.Contains('-'))
        {
            var s = part.Split('-');
            var lo = int.Parse(s[0]);
            var hi = int.Parse(s[1]);
            return (Math.Max(min, lo), Math.Min(max, hi));
        }
        var v = int.Parse(part);
        return (Math.Max(min, v), Math.Min(max, v));
    }

    static DayOfWeek[] ParseDayOfWeek(string field)
    {
        // Cron 中 0/7=周日，1=周一 … 6=周六
        var ints = ParseField(field, 0, 7);
        return ints.Select(i => i % 7).Distinct()
                   .Select(i => (DayOfWeek)((i + 6) % 7))   // 0=周日 -> DayOfWeek.Sunday (0)
                   .ToArray();
    }
}
