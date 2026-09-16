using System.Text.Json;
using System.Text.Json.Nodes;

namespace GAIRR.Core;

/// <summary>某模型思考模式规则（来自 config.ini [ModelThinking]，一行一条，未配置=该模型不发思考参数）
/// 配置格式（按模型名定位，模型名全局唯一，不区分大小写）：
///   模型名=参数名=on:开时值[,off:关时值][@写入路径][|levels=档位1,档位2][|label=显示名]
/// 例：
///   qwen3.8-max=enable_thinking=on:true,off:false                              顶层布尔（百炼 qwen3）
///   Qwen3.8-27B-UDQ4KXL=enable_thinking=on:true,off:false@chat_template_kwargs llama.cpp 嵌套字段
///   k3=reasoning_effort=on:high|levels=low,high,max                            顶层字符串等级（默认 high）
///   kimi-for-coding=thinking=on:{"type":"enabled"}                             顶层 JSON 对象，无 off=不支持关（恒开）
/// 字段：参数名=API 参数键（enable_thinking/reasoning_effort/thinking…，随厂商模型不同而不同）。
/// 值语法：true/false→布尔；{...}→JSON 对象/数组；其他→字符串字面值。
/// 路径：@ 后点号分隔（如 @a.b）写入嵌套位置，缺省=请求体顶层。
/// levels：等级列表（配置驱动，随厂商模型不同而不同）；>1 档=等级模式（UI 下拉），on 值=默认档。</summary>
public sealed class ThinkingRule
{
    public required string Model { get; init; }
    public required string Param { get; init; }          // API 参数名（enable_thinking / reasoning_effort / thinking …）
    public string On { get; init; } = "";                // 开时值；等级模式=默认档；空=不发（无思考）
    public string Off { get; init; } = "";               // 关时值；空=不支持关（恒发开值，如 Thinking:ON 模型）
    public string[] Levels { get; init; } = Array.Empty<string>();
    public string Label { get; init; } = "";             // UI 显示名（如"思考"），空=用默认
    public string Path { get; init; } = "";              // @ 写入路径（点号分隔），空=顶层

    /// <summary>是否等级模式：levels 多于 1 档即渲染下拉（≤1 档=开关，按 on/off 值形态）</summary>
    public bool IsLevel => Levels.Length > 1;

    /// <summary>是否支持关闭：off 为空即只能开（开关模式 UI 锁定常亮）</summary>
    public bool CanOff => Off.Length > 0;

    /// <summary>默认值：开/关与等级模式统一取 on 值（等级模式 on=默认档；on 不在 levels 时回退首档）</summary>
    public string Default =>
        IsLevel && !Levels.Contains(On, StringComparer.OrdinalIgnoreCase) && Levels.Length > 0
            ? Levels[0] : On;

    /// <summary>按当前 UI 值（开/关 或 等级档）解析出要注入请求体的值；空值→默认档/开值；返回 null=不注入。
    /// 等级模式下 UI"关闭"项（值 off，仅配置了 off 值时出现）显式按 off 值发送</summary>
    public JsonNode? Resolve(string uiValue)
    {
        var raw = IsLevel
            ? (uiValue == "off" && Off.Length > 0 ? Off
              : Levels.Contains(uiValue, StringComparer.OrdinalIgnoreCase) ? uiValue : On)
            : (uiValue.Length > 0 && uiValue == "off") ? Off : On;   // 空/on=开（与 UI 显示一致），仅显式 off=关
        return ParseValue(raw);
    }

    /// <summary>值解析：true/false→bool；以 { 或 [ 开头→内嵌 JSON；其余→字符串</summary>
    public static JsonNode? ParseValue(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (raw == "true") return true;
        if (raw == "false") return false;
        if (raw.StartsWith('{') || raw.StartsWith('['))
        {
            try { return JsonNode.Parse(raw); }
            catch { return raw; }   // 非法 JSON 降级为字符串字面值
        }
        return raw;
    }

    /// <summary>解析一条配置行（"模型名=参数名=on:V[,off:V][@路径][|levels=..][|label=..]"）；格式非法返回 null</summary>
    public static ThinkingRule? Parse(string line)
    {
        // 三段拆分：模型名 / 参数名 / 规格（规格本身可含 =，故只按前两个 = 切分）
        var eq1 = line.IndexOf('=');
        if (eq1 <= 0) return null;
        var model = line[..eq1].Trim();
        var rest1 = line[(eq1 + 1)..];
        var eq2 = rest1.IndexOf('=');
        if (eq2 <= 0) return null;
        var param = rest1[..eq2].Trim();
        var spec = rest1[(eq2 + 1)..].Trim();
        if (model.Length == 0 || param.Length == 0 || spec.Length == 0) return null;

        // 摘出 | 分隔的可选项（levels/label）
        var pipes = spec.Split('|');
        var main = pipes[0].Trim();
        var levelsRaw = "";
        var label = "";
        for (var i = 1; i < pipes.Length; i++)
        {
            var opt = pipes[i].Trim();
            var pi = opt.IndexOf('=');
            if (pi <= 0) continue;
            var k = opt[..pi].Trim().ToLowerInvariant();
            var v = opt[(pi + 1)..].Trim();
            if (k == "levels") levelsRaw = v;
            else if (k == "label") label = v;
        }

        // 从 main 尾部摘 @路径（点号分隔；@ 后不得含 } 或 ]，避免误伤 JSON 值内的 @）
        var path = "";
        var at = main.IndexOf('@');
        while (at > 0 && (main[at..].Contains('}') || main[at..].Contains(']'))) at = main.IndexOf('@', at + 1);
        if (at > 0)
        {
            path = main[(at + 1)..].Trim();
            main = main[..at].Trim();
        }

        // main 形如 "on:V" 或 "on:V,off:V"（on 值必填，off 值可选）
        string on = "", off = "";
        if (!main.StartsWith("on:", StringComparison.Ordinal)) return null;
        var r = main[3..].Trim();
        var ci = r.IndexOf(',');
        if (ci >= 0 && r[(ci + 1)..].TrimStart().StartsWith("off:", StringComparison.Ordinal))
        {
            on = r[..ci].Trim();
            off = r[(ci + 1)..].TrimStart()[4..].Trim();
        }
        else on = r.Trim();
        if (on.Length == 0) return null;

        var levels = levelsRaw.Length > 0
            ? levelsRaw.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray()
            : Array.Empty<string>();
        return new ThinkingRule { Model = model, Param = param, On = on, Off = off, Levels = levels, Label = label, Path = path };
    }

    /// <summary>把规则值写入请求体：按 Path 点号分隔逐层下钻（缺省顶层），最终 node[Param]=value</summary>
    public void Apply(JsonObject body, JsonNode? value)
    {
        if (value == null) return;
        var node = body;
        var segs = Path.Length > 0 ? Path.Split('.') : Array.Empty<string>();
        foreach (var seg in segs)
        {
            if (node[seg] is not JsonObject sub)
            {
                sub = new JsonObject();
                node[seg] = sub;
            }
            node = sub;
        }
        node[Param] = value;
    }
}

/// <summary>[ModelThinking] 配置表：按模型名索引（忽略大小写），供 AgentLoop/UI/客户端共用</summary>
public sealed class ThinkingRules
{
    readonly Dictionary<string, ThinkingRule> byModel = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>从 config.ini [ModelThinking] 节加载全部规则（行格式见 ThinkingRule 头注释；非法行跳过）</summary>
    public static ThinkingRules Load(AppConfig cfg)
    {
        var t = new ThinkingRules();
        foreach (var line in cfg.GetLines("ModelThinking"))
        {
            var r = ThinkingRule.Parse(line);
            if (r != null) t.byModel[r.Model] = r;
            else System.Diagnostics.Debug.WriteLine($"[ModelThinking] 非法配置行，已跳过：{line}");
        }
        return t;
    }

    public ThinkingRule? For(string model) =>
        byModel.TryGetValue(model, out var r) ? r : null;

    public IEnumerable<ThinkingRule> All => byModel.Values;
}
