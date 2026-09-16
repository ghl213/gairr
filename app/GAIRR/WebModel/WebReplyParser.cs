using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using GAIRR.Core;

namespace GAIRR;

/// <summary>网页模型通道的「返回大模型接口格式」协议：约定网页版模型把每轮回复写成 OpenAI 兼容的响应结构
/// （一个 JSON 对象：role / content / tool_calls），本类负责三件事——
/// ① 生成写入【系统设定】的协议文本与可用工具清单；② 把模型回复解析回 content + 工具调用
/// （含 Markdown 围栏、前后夹带说明文字的容错：JSON 之前的说明文字并入正文，JSON 照常执行工具）；③ 流式增量闸门（协议模式下抑制 JSON 原文，只放行正文）。
/// 容错原则：解析不出协议结构时**按纯文本原样返回**——网页模型不听话也不能让整轮失败，
/// 原有"网页版按纯文本模型工作"的路径必须保持不变。
/// 另一形态（XML 工具调用）的兜底解析见 WebReplyParser.Xml.cs。</summary>
static partial class WebReplyParser
{
    /// <summary>工具清单文本上限（字符）：超出只列前若干个，避免把网页输入框和上下文撑爆</summary>
    const int ToolsBudget = 20000;
    const int DescLimit = 300;          // 单个工具说明截断
    const int ParamDescLimit = 100;     // 单个参数说明截断

    /* ==================== ① 协议文本 ==================== */

    /// <summary>完整协议段（首次投递/重置网页上下文时并入【系统设定】之后；tools 为空返回空串 = 不启用协议）</summary>
    public static string ProtocolSection(JsonArray tools)
    {
        if (tools is not { Count: > 0 }) return "";
        var names = new List<string>();
        var sb = new StringBuilder();
        var truncated = false;
        foreach (var node in tools)
        {
            if (node is not JsonObject t) continue;
            var fn = t["function"] as JsonObject;
            var name = Str(fn?["name"]);
            if (name.Length == 0) continue;
            var line = $"- {name}：{Trunc(Str(fn?["description"]), DescLimit)}\n  参数：{DescribeParams(fn?["parameters"] as JsonObject)}\n";
            if (sb.Length + line.Length > ToolsBudget) { truncated = true; break; }
            sb.Append(line);
            names.Add(name);
        }
        if (names.Count == 0) return "";

        var head = new StringBuilder();
        head.Append("【输出格式约定】你的每轮回复必须是**一个 JSON 对象**，除此之外不要输出任何文字")
            .Append("（不要解释、不要寒暄、不要用 Markdown 代码块包裹）：\n\n")
            .Append("{\"role\":\"assistant\",\"content\":\"给用户看的正文\",")
            .Append("\"tool_calls\":[{\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"工具名\",\"arguments\":\"{\\\"参数名\\\":\\\"参数值\\\"}\"}}]}\n\n")
            .Append("规则：\n")
            .Append("1. 顶层只有 role / content / tool_calls 三个字段，role 固定为 \"assistant\"。\n")
            .Append("2. 需要调用工具时把调用放进 tool_calls；arguments 必须是**一个 JSON 字符串**（把入参对象序列化成字符串），不是嵌套对象。\n")
            .Append("3. 一轮可以返回多个 tool_calls（会被并行执行，执行结果下一轮以【工具执行结果】给你）；不需要调用工具时写 \"tool_calls\":[] 并在 content 里写完整答复。\n")
            .Append("4. content 只写要对用户说的话，纯工具轮写 \"\"；不要把 JSON 复述进 content。\n")
            .Append("5. 工具名与参数名必须取自下方清单，不要虚构；参数不确定时先用只读工具（Read/Grep/Glob 等）确认。\n")
            .Append("6. 看到【工具执行结果】后继续下一步；任务完成时返回 tool_calls 为 [] 的最终答复。\n")
            .Append("7. 若需要保留推理过程，可加一个可选字段 reasoning（字符串，与 content 同级）；它只用于展示思考，不要写进 content。\n\n")
            .Append("【可用工具清单】\n");
        if (truncated) head.Append($"(清单过长仅列出前 {names.Count} 个工具；其余工具本轮不可用)\n");
        return head.Append(sb).ToString();
    }

    /// <summary>把工具入参 schema 压成一行可读清单（属性名:类型(必填)，说明截断）</summary>
    static string DescribeParams(JsonObject? schema)
    {
        var props = schema?["properties"] as JsonObject;
        if (props == null || props.Count == 0) return "无参数";
        var required = new HashSet<string>(StringComparer.Ordinal);
        if (schema?["required"] is JsonArray req)
            foreach (var r in req) { var s = Str(r); if (s.Length > 0) required.Add(s); }
        var parts = new List<string>();
        foreach (var kv in props)
        {
            var p = kv.Value as JsonObject;
            var type = Str(p?["type"]);
            if (type.Length == 0) type = "any";
            if (type == "array" && p?["items"] is JsonObject items)
                type = "array<" + (Str(items["type"]).Length > 0 ? Str(items["type"]) : "any") + ">";
            var note = Trunc(Str(p?["description"]), ParamDescLimit);
            parts.Add($"{kv.Key}:{type}{(required.Contains(kv.Key) ? "(必填)" : "")}{(note.Length > 0 ? "，" + note : "")}");
        }
        return string.Join("；", parts);
    }

    /* ==================== ② 解析：回复文本 → content + 工具调用 ==================== */

    /// <summary>解析一段回复：成功识别协议结构时 Parsed=true（Content 为正文，可为空串；Calls 为工具调用；
    /// Reasoning 为可选字段 reasoning/reasoning_content 里的思考文本）；否则 Parsed=false 且 Content 为原文
    /// （调用方按纯文本处理，保持原行为）。</summary>
    public static (string? Content, List<LlmToolCall> Calls, string? Reasoning, bool Parsed) Parse(string? text)
    {
        var calls = new List<LlmToolCall>();
        var raw = text ?? "";
        if (string.IsNullOrWhiteSpace(raw)) return (raw, calls, null, false);
        var (obj, prefix) = TryExtractObject(raw);
        if (obj == null)
        {
            // 协议 JSON 认不出时再兜一次：模型改按 XML 工具调用格式回（tool_calls 标记 + <invoke>/<parameter>），
            // 硬编码剥离标记、把 XML 转成等价的 content + tool_calls；都没有才按纯文本回退。
            if (TryExtractXml(raw) is { } xml) return (xml.Content, xml.Calls, null, true);
            return (raw, calls, null, false);
        }

 var content = ExtractText(obj["content"]);
 var reasoning = ExtractText(obj["reasoning"] ?? obj["reasoning_content"]);
 // 模型不守协议、在协议 JSON 之外先写了一段说明文字（"核实上一轮改动…"这类）：JSON 照常拿去执行工具，
 // 多出来的文字按内容分流——含 question-card 块的部分留在正文（渲染成询问卡片），其余普通说明
 // 文字并入思考（reasoning）——既不丢信息，也不让原始 JSON 或多余文字刷进气泡。
 if (prefix.Length > 0)
 {
 var (card, rest) = SplitQuestionCards(prefix);
 if (card.Length > 0 && !content.Contains(card, StringComparison.Ordinal))
 content = content.Length == 0 ? card : card + "\n\n" + content;
 if (rest.Length > 0 && !reasoning.Contains(rest, StringComparison.Ordinal))
 reasoning = reasoning.Length == 0 ? rest : reasoning + "\n\n" + rest;
 }
        if (obj["tool_calls"] is JsonArray arr)
            foreach (var n in arr)
            {
                if (ToCall(n, calls.Count) is { } c) calls.Add(c);
            }
        // 协议字段在了但正文/工具/思考全空（模型返回空壳）：按原文回退，避免整轮空答复
        if (string.IsNullOrWhiteSpace(content) && string.IsNullOrWhiteSpace(reasoning) && calls.Count == 0)
        {
            // 有一类"空壳"其实是 XML 形态被扫描配平扫出来的内层 JSON（<tool_call>{…}</tool_call>）：
            // 再让 XML 兜底认一次，认得出来就当协议回复用，认不出才真正按纯文本回退。
            if (TryExtractXml(raw) is { } alt) return (alt.Content, alt.Calls, null, true);
            return (raw, calls, null, false);
        }
        return (content, calls, string.IsNullOrWhiteSpace(reasoning) ? null : reasoning, true);
    }

    /// <summary>把解析结果套用回 LlmResponse，并收尾增量闸门（协议模式下把正文补推给打字机）。
    /// 思考来源二选一：页面"深度思考"块已抓到（resp.ReasoningContent）时优先用它；页面没抓到、
    /// 而模型在 JSON 里写了 reasoning 字段时，用它补一次思考直播并留存（两条来源都无则思考为空）。</summary>
    public static (LlmResponse Response, bool Parsed) Apply(LlmResponse resp, DeltaGate gate, Action<string>? onReasoning = null)
    {
        var (content, calls, reasoning, parsed) = Parse(resp.Content);
        // 围栏自愈：网页把 ```question-card … ``` 渲染成代码块样式后，抓回的文本只剩下裸标记
        // "question-card" + JSON（围栏字符消失），平台级卡片解析只认带围栏的块 → 卡片不显示、
        // JSON 原文被当正文刷进气泡。这里在网页通道内把裸块补回围栏（详见 RestoreBareQuestionCards）。
        if (parsed) content = RestoreBareQuestionCards(content);
        else resp = resp with { Content = RestoreBareQuestionCards(resp.Content) };
        gate.Finish(parsed ? content ?? "" : resp.Content ?? "");
        if (!parsed) return (resp, false);
        var pageThink = resp.ReasoningContent;
        if (string.IsNullOrWhiteSpace(pageThink) && !string.IsNullOrWhiteSpace(reasoning))
            try { onReasoning?.Invoke(reasoning); } catch { }
        var finalReasoning = string.IsNullOrWhiteSpace(pageThink) ? reasoning : pageThink;
        return (resp with
        {
            Content = string.IsNullOrWhiteSpace(content) ? null : content,
            ToolCalls = calls,
            ReasoningContent = finalReasoning,
        }, true);
    }

    /// <summary>围栏自愈：网页版把模型回复当 Markdown 渲染，回复里写的 ```question-card … ``` 先被页面
    /// 渲染成代码块样式，抓回的 innerText 里三个反引号消失，只剩裸标记 "question-card" 与其后的 JSON
    /// 对象。平台级卡片解析只认带围栏的块，于是卡片不显示、"question-card{…}" 源码被当正文刷进气泡。
    /// 这里把这种"围栏被页面吞掉"的裸块补回标准围栏——硬条件：标记独占一行且其后紧跟一个可解析的
    /// JSON 对象；已带围栏的块、正文里行内提及的 "question-card" 一律原样保留（返回原文）。</summary>
    public static string RestoreBareQuestionCards(string? content)
    {
        var s = content ?? "";
        const string mark = "question-card";
        if (s.IndexOf(mark, StringComparison.OrdinalIgnoreCase) < 0) return s;

        StringBuilder? sb = null;
        var cursor = 0;        // 已写入 sb 的原文位置
        var searchFrom = 0;
        while (true)
        {
            var idx = s.IndexOf(mark, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) break;
            searchFrom = idx + mark.Length;

            // 标记须独占一行：行首到标记间只允许空白（已带 ``` 围栏时此处为 "```" 非空 → 自动跳过）
            var lineStart = s.LastIndexOf('\n', Math.Max(idx - 1, 0)) + 1;
            if (s.Substring(lineStart, idx - lineStart).Trim().Length != 0) continue;
            // 标记行尾也须只剩空白（排除正文里"question-card 是什么"这类行内提及）
            var nl = s.IndexOf('\n', idx + mark.Length);
            var tail = nl < 0 ? s.Substring(idx + mark.Length) : s.Substring(idx + mark.Length, nl - idx - mark.Length);
            if (tail.Trim().Length != 0) continue;

            // 标记行之后跳过空白（含换行），须紧跟一个能解析的 JSON 对象（卡片正文）
            var p = idx + mark.Length;
            while (p < s.Length && char.IsWhiteSpace(s[p])) p++;
            if (p >= s.Length || s[p] != '{') continue;
            if (BalancedFragment(s, p) is not { } frag) continue;
            if (ParseObjLoose(frag) is not JsonObject) continue;

            sb ??= new StringBuilder(s.Length + 32);
            sb.Append(s, cursor, lineStart - cursor)
              .Append("```question-card\n").Append(frag).Append("\n```");
            cursor = p + frag.Length;
            searchFrom = cursor;
        }
 return sb == null ? s : sb.Append(s, cursor, s.Length - cursor).ToString();
 }

 /// <summary>把协议 JSON 之外的多余文字按内容分流：含 question-card … 围栏块的部分归 Card
 /// （留在正文渲染成询问卡片），其余普通说明文字归 Rest（并入思考）。裸卡片块先经
 /// RestoreBareQuestionCards 补回围栏再切分；两段都做 Trim，无卡片时 Card 为空串、Rest 为全文。</summary>
 static (string Cards, string Other) SplitQuestionCards(string text)
 {
 if (text.IndexOf("question-card", StringComparison.OrdinalIgnoreCase) < 0)
 return ("", text.Trim());
 var s = RestoreBareQuestionCards(text);
 var card = new StringBuilder();
 var rest = new StringBuilder();
 var i = 0;
 while (i < s.Length)
 {
  var mark = "question-card"; var at = s.IndexOf(mark, i, StringComparison.OrdinalIgnoreCase); if (at < 0) { rest.Append(s, i, s.Length - i); break; } var open = s.LastIndexOf((char)10, Math.Max(at - 1, 0)) + 1; rest.Append(s, i, open - i); var nl = s.IndexOf((char)10, at); var close = nl < 0 ? -1 : s.IndexOf(new string((char)96, 3), nl, StringComparison.Ordinal);
 if (close < 0) { card.Append(s, open, s.Length - open); break; }
 var end = s.IndexOf('\n', close);
 var blockEnd = end < 0 ? s.Length : end + 1;
 card.Append(s, open, blockEnd - open);
 i = blockEnd;
 }
 return (card.ToString().Trim(), rest.ToString().Trim());
 }

 /// <summary>从回复文本里尽力取出那个协议 JSON 对象：整体／围栏内／"说明文字 + 尾部 JSON"／夹带说明文字时的配平片段。
    /// 第二项返回协议 JSON 之前的那段文字（Prefix，无则空串），供调用方并入正文。</summary>
    static (JsonObject? Obj, string Prefix) TryExtractObject(string text)
    {
        var raw = text.Trim();
        if (ParseObjLoose(raw) is { } direct) return (direct, "");

        var fenced = FenceContent(raw);
        if (fenced != null && ParseObjLoose(fenced.Trim()) is { } inner) return (inner, "");

        // 优先认"说明文字 + 尾部协议 JSON"（网页模型最常见的"不守协议"形态）：整段配平会因 arguments
        // 内层引号未转义而失败，这里改为从协议字段处起到末尾整体宽松解析，成功即把前面那段文字带回。
        if (TryTailProtocol(raw) is { } tail) return tail;

        // 扫描各 '{' 起点取配平片段：优先"含协议字段"的，其次第一个能解析成功的
        JsonObject? firstOk = null;
        var from = 0;
        for (var i = 0; i < 30; i++)
        {
            var start = raw.IndexOf('{', from);
            if (start < 0) break;
            from = start + 1;
            var frag = BalancedFragment(raw, start);
            if (frag == null) continue;
            if (ParseObjLoose(frag) is not { } o) continue;
            if (IsProtocolObject(o)) return (o, "");
            firstOk ??= o;
        }
        return (firstOk, "");
    }

    /// <summary>容错第三式（"说明文字 + 尾部协议 JSON"）：模型先写一段话、再把协议 JSON 附在末尾。
    /// 从每个"协议开头"的 '{'（后随 role/content/tool_calls 字段名）起，把其后整段（或到最后一个 '}' 为止）
    /// 交给宽松解析（含 arguments 内层引号未转义、Windows 路径单反斜杠两类修复）；成功则返回该对象与
    /// JSON 之前那段文字。都取不到返回 null，调用方按原路径回退（不改变纯文本行为）。</summary>
    static (JsonObject? Obj, string Prefix)? TryTailProtocol(string text)
    {
        var end = text.TrimEnd().Length;
        var lastBrace = text.LastIndexOf('}');
        var from = 0;
        for (var n = 0; n < 30; n++)
        {
            var start = text.IndexOf('{', from);
            if (start < 0 || start >= end) break;
            from = start + 1;
            var p = start + 1;
            while (p < text.Length && char.IsWhiteSpace(text[p])) p++;
            if (!IsProtocolHeadAt(text, p)) continue;   // 只认协议对象的开头，避免误把内层参数对象当协议
            foreach (var stop in new[] { end, lastBrace + 1 })   // 先整段，再退一步"到最后一个 '}' 为止"
            {
                if (stop <= start) continue;
                var frag = text.Substring(start, stop - start);
                if (ParseObjLoose(frag) is { } o && IsProtocolObject(o))
                    return (o, text.Substring(0, start).Trim());
            }
        }
        return null;
    }

    /// <summary>'{' 之后紧跟的是不是协议字段名（role / content / tool_calls）——判定"这里是协议 JSON 的开头"</summary>
    static bool IsProtocolHeadAt(string s, int p)
        => AtKey(s, p, "role") || AtKey(s, p, "content") || AtKey(s, p, "tool_calls");

    static bool AtKey(string s, int p, string key)
        => p + key.Length + 1 < s.Length && s[p] == '"'
           && string.CompareOrdinal(s, p + 1, key, 0, key.Length) == 0
           && s[p + 1 + key.Length] == '"';

    /// <summary>是不是协议外壳对象（顶层带 role / content / tool_calls 之一）</summary>
    static bool IsProtocolObject(JsonObject o)
        => o.ContainsKey("tool_calls") || o.ContainsKey("role") || o.ContainsKey("content");

    static JsonObject? ParseObj(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        try { return JsonNode.Parse(s) as JsonObject; }
        catch { return null; }
    }

    /// <summary>解析的第二道尝试：严格 JSON 失败时，按序叠加定向修复再解析——每步都在上一步的结果上继续修，
    /// 因此多种畸形同现（用户实测过：缺 "function" 键 + arguments 内层引号未转义 + Windows 路径单反斜杠三者叠加）
    /// 也能救回；每步修复前先直接解析一次（修好的中间形态本身可能就是合法 JSON）。全部修完仍失败返回 null
    /// （调用方按纯文本回退）。修复顺序按依赖关系排：arguments 修复要求内层片段是严格合法 JSON，
    /// 故必须放在反斜杠修复之后；缺键修复与两者无关，放最前。</summary>
    static JsonObject? ParseObjLoose(string s)
    {
        if (ParseObj(s) is { } direct) return direct;
        var cur = s;
        foreach (var repair in new Func<string, string?>[]
                 { RepairMissingFunctionKey, RepairLooseBackslash, RepairUnescapedArguments })
        {
            if (ParseObj(cur) is { } ok) return ok;
            if (repair(cur) is { } next) cur = next;
        }
        return ParseObj(cur);
    }

    /// <summary>容错第三式：模型写 tool_calls 元素时漏掉 "function" 键（"type":"function":{...}——
    /// 值 "function" 之后又冒出 ':{'，严格 JSON 非法，整条回复因此解析失败，该轮全部工具调用丢失）。
    /// 形态特征无歧义：合法 JSON 里值之后只能跟 ',' 或 '}'，绝不可能是 ':'，因此 ':"function":{' 只可能是缺键。
    /// 修复：补成 ':"function","function":{'。未发现该形态时返回 null。仅在严格解析已失败、
    /// 叠加修复链中被调用，误伤合规文本的风险可忽略（合规文本里该片段只会以转义形态出现）。</summary>
    static string? RepairMissingFunctionKey(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        const string bad = ":\"function\":{";
        if (text.IndexOf(bad, StringComparison.Ordinal) < 0) return null;
        return text.Replace(bad, ":\"function\",\"function\":{");
    }

    /// <summary>容错第二式：模型手写 JSON 时常把 Windows 路径的反斜杠只写一个（"d:\work\gairr"），
    /// 而 \w \g 不是合法 JSON 转义，整条回复因此解析失败。这里只把"非法转义"补成字面反斜杠（\\），
    /// 合法转义（\" \\ \/ \b \f \n \r \t \u）一律原样保留；没改动就返回 null。
    /// 注意：合法转义对必须整体跳过（i 前进一步）——否则文本里本来就合规的 \\ 会被拆成"半个转义"，
    /// 第二个反斜杠被误判为非法转义起点再补一个，d:\work 级别的合规写法反而被改坏（回归实测抓到）。</summary>
    static string? RepairLooseBackslash(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('\\') < 0) return null;
        var sb = new StringBuilder(text.Length + 8);
        var changed = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            sb.Append(c);
            if (c != '\\') continue;
            if (i + 1 >= text.Length) break;
            if (text[i + 1] is '"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't' or 'u')
            {
                sb.Append(text[i + 1]);   // 合法转义对整体保留（\n 不能丢 n，\\ 不能拆散）
                i++;
                continue;
            }
            sb.Append('\\');   // 非法转义 → 补一个反斜杠，使其成为字面反斜杠
            changed = true;
        }
        return changed ? sb.ToString() : null;
    }

    /// <summary>修复模型的一种常见畸形写法：协议要求 arguments 是 JSON 字符串，模型却把入参对象
    /// 直接塞进引号里而没转义内层引号（形如 "arguments":"{"pattern":"x"}"，严格 JSON 非法，
    /// 整条回复因此解析失败、JSON 原文被当正文刷进气泡）。做法：定位该形态的值片段——
    /// 开引号后紧跟 '{'，用括号配平取出内层对象文本（内层本身是合法 JSON），确认其后确实还有一个
    /// 闭引号——把整段（含外层引号）换成转义后的合法 JSON 字符串。未发现该形态时返回 null，
    /// 调用方保持原有回退行为（按纯文本处理）。</summary>
    static string? RepairUnescapedArguments(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var s = text;
        var keys = new[] { "\"arguments\"", "\"parameters\"" };
        StringBuilder? sb = null;
        var cursor = 0;        // 已写入 sb 的原文位置
        var searchFrom = 0;    // 键的搜索起点
        while (true)
        {
            var keyAt = -1;
            var keyLen = 0;
            foreach (var k in keys)
            {
                var i = s.IndexOf(k, searchFrom, StringComparison.Ordinal);
                if (i >= 0 && (keyAt < 0 || i < keyAt)) { keyAt = i; keyLen = k.Length; }
            }
            if (keyAt < 0) break;
            searchFrom = keyAt + keyLen;

            var p = searchFrom;
            while (p < s.Length && char.IsWhiteSpace(s[p])) p++;
            if (p >= s.Length || s[p] != ':') continue;
            p++;
            while (p < s.Length && char.IsWhiteSpace(s[p])) p++;
            if (p >= s.Length || s[p] != '"') continue;   // 值不是字符串（"arguments":{...} 本就合法）→ 不动
            var quoteAt = p;
            var b = quoteAt + 1;
            while (b < s.Length && char.IsWhiteSpace(s[b])) b++;
            if (b >= s.Length || s[b] != '{') continue;   // 不是"对象文本被裹进字符串"的形态 → 不动
            if (BalancedFragment(s, b) is not { } frag) continue;
            if (ParseObj(frag) is not JsonObject) continue;   // 内层不是合法对象（另有破损）→ 不动
            var q = b + frag.Length;
            while (q < s.Length && char.IsWhiteSpace(s[q])) q++;
            if (q >= s.Length || s[q] != '"') continue;   // 缺外层闭引号，不是该形态 → 不动

            sb ??= new StringBuilder();
            sb.Append(s, cursor, quoteAt - cursor).Append(Quote(frag));
            cursor = q + 1;          // 跳过被替换掉的闭引号
            searchFrom = cursor;
        }
        return sb?.Append(s, cursor, s.Length - cursor).ToString();
    }

    /// <summary>把一段原文包成合法的 JSON 字符串字面量（转义反斜杠、引号与控制字符）</summary>
    static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2).Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    /// <summary>取第一个 Markdown 围栏（```json … ```）内的内容；没有则 null</summary>
    static string? FenceContent(string s)
    {
        var i = s.IndexOf("```", StringComparison.Ordinal);
        if (i < 0) return null;
        var j = s.IndexOf('\n', i);
        if (j < 0) return null;
        var end = s.IndexOf("```", j, StringComparison.Ordinal);
        return end < 0 ? null : s.Substring(j + 1, end - j - 1);
    }

    /// <summary>从 start（'{'）起做括号配平扫描（跳过字符串内的括号与转义），返回完整片段；未闭合返回 null</summary>
    static string? BalancedFragment(string s, int start)
    {
        var depth = 0;
        var inStr = false;
        var esc = false;
        for (var i = start; i < s.Length; i++)
        {
            var c = s[i];
            if (inStr)
            {
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
                continue;
            }
            if (c == '"') inStr = true;
            else if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return s.Substring(start, i - start + 1);
            }
        }
        return null;
    }

    /// <summary>把单个 tool_calls 元素转成 LlmToolCall（兼容 {"function":{…}} 与扁平 {name,arguments} 两种写法）</summary>
    static LlmToolCall? ToCall(JsonNode? node, int index)
    {
        if (node is not JsonObject o) return null;
        var fn = o["function"] as JsonObject ?? o;
        var name = Str(fn["name"]);
        if (name.Length == 0) name = Str(o["name"]);
        if (name.Length == 0) return null;

        var argsNode = fn["arguments"] ?? o["arguments"] ?? o["parameters"];
        var args = argsNode switch
        {
            null => "{}",
            JsonValue v when v.TryGetValue<string>(out var s) => string.IsNullOrWhiteSpace(s) ? "{}" : s.Trim(),
            _ => argsNode!.ToJsonString(),
        };
        var id = Str(o["id"]);
        if (id.Length == 0) id = $"call_web_{index + 1}";
        return new LlmToolCall(id, name.Trim(), args);
    }

    /// <summary>取节点的文本内容（兼容字符串与多模态数组两种形态）</summary>
    static string ExtractText(JsonNode? node)
    {
        if (node == null) return "";
        if (node is JsonValue v && v.TryGetValue<string>(out var s)) return s ?? "";
        if (node is JsonArray arr)
        {
            var sb = new StringBuilder();
            foreach (var item in arr)
                sb.Append(item is JsonObject o ? ExtractText(o["text"]) : ExtractText(item));
            return sb.ToString();
        }
        return "";
    }

    /// <summary>安全取字符串（非字符串值退化为其 JSON 文本），避免 GetValue&lt;string&gt; 抛类型异常</summary>
    internal static string Str(JsonNode? n)
    {
        if (n == null) return "";
        if (n is JsonValue v && v.TryGetValue<string>(out var s)) return s ?? "";
        return n.ToJsonString().Trim('"');
    }

    static string Trunc(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "…";

    /* ==================== ③ 流式增量闸门 ==================== */

    /// <summary>增量闸门：启用协议后模型每轮正文是一整段 JSON，直接透传会把 JSON 刷进气泡。
    /// 这里只缓冲开头几字符定性——判定为 JSON 就静默（解析完成后仅把正文推一次），
    /// 判定为纯文本则原样透传，保证不听话的模型也能正常显示。</summary>
    public sealed class DeltaGate(Action<string>? sink)
    {
        const int Window = 32;   // 开头全是空白时的死等上限
        readonly StringBuilder buf = new();
        bool decided, jsonMode;

        /// <summary>收增量：未定性前缓冲，定性后按模式放行</summary>
        public void Feed(string delta)
        {
            if (sink == null || string.IsNullOrEmpty(delta)) return;
            if (decided) { if (!jsonMode) sink(delta); return; }
            buf.Append(delta);
            var head = buf.ToString().TrimStart(' ', '\t', '\r', '\n');
            if (head.Length == 0) { if (buf.Length >= Window) Decide(false); return; }
            Decide(IsProtocolHead(head));   // JSON 对象 / Markdown 围栏 / XML 工具调用标记 → 协议模式
            if (!jsonMode) sink(buf.ToString());
        }

        /// <summary>协议回复的开头判定：JSON 对象、Markdown 围栏，或 XML 工具调用形态
        /// （&lt;tool_call… 标签、或 tool_calls 字样打头）。判成协议模式只影响"是否边收边显示"——
        /// 收尾时若仍没解析成功，原文会整段补推，不会丢内容。</summary>
        static bool IsProtocolHead(string head)
        {
            if (head.Length == 0) return false;
            if (head[0] is '{' or '`' or '<') return true;
            return head.StartsWith("tool_calls", StringComparison.OrdinalIgnoreCase)
                || head.StartsWith("tool call", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>本轮收尾：协议模式补推解析后的正文；未定性（输出太短）补推全文</summary>
        public void Finish(string shown)
        {
            if (sink == null || shown.Length == 0) return;
            if (decided) { if (jsonMode) sink(shown); return; }
            sink(shown);
        }

        void Decide(bool json) { decided = true; jsonMode = json; }
    }
}
