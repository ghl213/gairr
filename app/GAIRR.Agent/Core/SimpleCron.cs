using System.Text.RegularExpressions;

namespace GAIRR.Core;

/// <summary>轻量级 Cron 解析器：支持 5 字段格式（分 时 日 月 周）。</summary>
public class SimpleCron
{
    readonly string expression;
    readonly int[] minutes;
    readonly int[] hours;
    readonly int[] days;
    readonly int[] months;
    readonly int[] weekdays;

    /// <summary>解析 Cron 表达式；解析失败抛出 FormatException。</summary>
    public SimpleCron(string expr)
    {
        expression = expr.Trim();
        var parts = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5) throw new FormatException("Cron 表达式必须是 5 个字段：分 时 日 月 周");

        minutes = ParseField(parts[0], 0, 59);
        hours = ParseField(parts[1], 0, 23);
        days = ParseField(parts[2], 1, 31);
        months = ParseField(parts[3], 1, 12);
        weekdays = ParseField(parts[4], 0, 6);
    }

    /// <summary>计算给定时间之后的下一次执行时间。</summary>
    public DateTime GetNextOccurrence(DateTime after)
    {
        var dt = after.AddMinutes(1).TruncateToMinute();
        // 安全上限：一年内找不到则返回 MaxValue
        var limit = after.AddYears(1);
        while (dt <= limit)
        {
            if (months.Contains(dt.Month) && days.Contains(dt.Day) && weekdays.Contains((int)dt.DayOfWeek)
                && hours.Contains(dt.Hour) && minutes.Contains(dt.Minute))
                return dt;
            dt = dt.AddMinutes(1);
        }
        return DateTime.MaxValue;
    }

    static int[] ParseField(string field, int min, int max)
    {
        if (field == "*") return Enumerable.Range(min, max - min + 1).ToArray();

        var list = new List<int>();
        foreach (var segment in field.Split(','))
        {
            var s = segment.Trim();
            var step = 1;
            var rangeStr = s;
            if (s.Contains('/'))
            {
                var sp = s.Split('/');
                rangeStr = sp[0];
                if (!int.TryParse(sp[1], out step) || step < 1) throw new FormatException($"Cron 步长无效：{s}");
            }

            var start = min;
            var end = max;
            if (rangeStr != "*")
            {
                if (rangeStr.Contains('-'))
                {
                    var r = rangeStr.Split('-');
                    if (!int.TryParse(r[0], out start) || !int.TryParse(r[1], out end))
                        throw new FormatException($"Cron 范围无效：{s}");
                }
                else if (!int.TryParse(rangeStr, out start))
                {
                    throw new FormatException($"Cron 字段无效：{s}");
                }
                else
                {
                    end = start;
                }
            }

            for (var i = start; i <= end; i += step)
            {
                if (i < min || i > max) throw new FormatException($"Cron 值越界：{i}（字段 {field}）");
                list.Add(i);
            }
        }
        return list.Distinct().OrderBy(x => x).ToArray();
    }
}

static class DateTimeExtensions
{
    public static DateTime TruncateToMinute(this DateTime dt) => new(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, 0, dt.Kind);
}
