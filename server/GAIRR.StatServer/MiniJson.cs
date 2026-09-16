// MiniJson.cs — GAIRR.StatServer 迷你 JSON 库（零外部依赖，替代 System.Text.Json）
// 支撑：上报 body 解析（非法时抛 JsonParseException，HTTP 层捕获返回 400）、
//       stats.json / latest.json 文件读写、/api/overview 看板输出。
// 覆盖类型：object / array / string / number / bool / null，字符串含全转义与 \uXXXX（含代理对）。
// 值模型：object -> Dictionary<string, object?>；array -> List<object?>；
//         string -> string；number -> double（序列化时整数不带小数点）；bool -> bool；null -> null。

using System.Globalization;
using System.Text;

namespace GAIRR.StatServer;

/// <summary>JSON 解析失败异常。带字符位置，HTTP 层捕获后返回 400。</summary>
public sealed class JsonParseException : Exception
{
    /// <summary>出错位置（0 起字符索引）。</summary>
    public int Position { get; }

    public JsonParseException(string message, int position) : base($"{message}（位置 {position}）")
        => Position = position;
}

/// <summary>迷你 JSON 静态入口：Parse 解析、Serialize 序列化（解析器见 MiniJson.Parser.cs）。</summary>
public static partial class MiniJson
{
    // ─────────────────────────── 解析 ───────────────────────────

    /// <summary>解析 JSON 文本为通用值模型（Dictionary/List/string/double/bool/null）。</summary>
    public static object? Parse(string json)
    {
        var p = new Parser(json);
        p.SkipWs();
        var v = p.ParseValue();
        p.SkipWs();
        if (!p.AtEnd) throw p.Error("JSON 尾部存在多余内容");
        return v;
    }

    /// <summary>便捷取值：把解析结果当 object 字典取键值。</summary>
    public static Dictionary<string, object?>? AsObject(object? v)
        => v as Dictionary<string, object?>;

    public static List<object?>? AsArray(object? v)
        => v as List<object?>;

    public static string? AsString(object? v)
        => v as string;

    /// <summary>取数字字段：double 直接转，long 兼容，缺省返回 defaultValue。</summary>
    public static double AsNumber(object? v, double defaultValue = 0)
        => v switch
        {
            double d => d,
            long l => l,
            int i => i,
            _ => defaultValue,
        };

    public static bool AsBool(object? v, bool defaultValue = false)
        => v is bool b ? b : defaultValue;

    // ─────────────────────────── 序列化 ───────────────────────────

    /// <summary>序列化为紧凑 JSON 文本。value 支持 值模型/Dictionary/List/字符串/数字/bool/null。</summary>
    public static string Serialize(object? value, bool pretty = false)
    {
        var sb = new StringBuilder();
        WriteValue(sb, value, pretty, 0);
        return sb.ToString();
    }

    // ─────────────────────────── 序列化内部 ───────────────────────────

    private static void WriteValue(StringBuilder sb, object? v, bool pretty, int depth)
    {
        switch (v)
        {
            case null: sb.Append("null"); return;
            case bool b: sb.Append(b ? "true" : "false"); return;
            case string s: WriteString(sb, s); return;
            case double d: sb.Append(FormatNumber(d)); return;
            case float f: sb.Append(FormatNumber(f)); return;
            case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); return;
            case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); return;
            case Dictionary<string, object?> obj:
                WriteObject(sb, obj, pretty, depth); return;
            case List<object?> arr:
                WriteArray(sb, arr, pretty, depth); return;
            // 反射兜底：兼容匿名对象（看板输出用），无属性则按 null
            default:
                var wrote = false;
                sb.Append('{');
                foreach (var prop in v.GetType().GetProperties())
                {
                    if (!prop.CanRead) continue;
                    if (wrote) sb.Append(',');
                    WriteString(sb, prop.Name);
                    sb.Append(':');
                    WriteValue(sb, prop.GetValue(v), pretty, depth + 1);
                    wrote = true;
                }
                sb.Append('}');
                return;
        }
    }

    private static void WriteObject(StringBuilder sb, Dictionary<string, object?> obj, bool pretty, int depth)
    {
        if (obj.Count == 0) { sb.Append("{}"); return; }
        sb.Append('{');
        var first = true;
        foreach (var (k, val) in obj)
        {
            if (!first) sb.Append(',');
            first = false;
            if (pretty) sb.Append('\n').Append(' ', (depth + 1) * 2);
            WriteString(sb, k);
            sb.Append(':');
            if (pretty) sb.Append(' ');
            WriteValue(sb, val, pretty, depth + 1);
        }
        if (pretty) sb.Append('\n').Append(' ', depth * 2);
        sb.Append('}');
    }

    private static void WriteArray(StringBuilder sb, List<object?> arr, bool pretty, int depth)
    {
        if (arr.Count == 0) { sb.Append("[]"); return; }
        sb.Append('[');
        for (var i = 0; i < arr.Count; i++)
        {
            if (i > 0) sb.Append(',');
            if (pretty) sb.Append('\n').Append(' ', (depth + 1) * 2);
            WriteValue(sb, arr[i], pretty, depth + 1);
        }
        if (pretty) sb.Append('\n').Append(' ', depth * 2);
        sb.Append(']');
    }

    /// <summary>JSON 字符串转义：控制字符与 " \ 必转，/ 不转，非 ASCII 原样输出（UTF-8）。</summary>
    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    /// <summary>数字格式化：整数不带小数点；小数用不变区域 round-trip 格式；非法值输出 null。</summary>
    private static string FormatNumber(double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d)) return "null";
        if (d == Math.Floor(d) && Math.Abs(d) < 1e15)
            return ((long)d).ToString(CultureInfo.InvariantCulture);
        return d.ToString("r", CultureInfo.InvariantCulture);
    }
}
