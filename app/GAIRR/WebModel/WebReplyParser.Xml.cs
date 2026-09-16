using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using GAIRR.Core;

namespace GAIRR;

/// <summary>WebReplyParser 的 XML 兜底分支（协议 JSON 解析不出来时才走这里）：
/// 网页版模型偶尔无视"每轮回复必须是一个 JSON 对象"的约定，改按 XML 工具调用格式回复，例如
/// <c>tool_calls</c> + &lt;tool_calls&gt;&lt;invoke name="Grep"&gt;&lt;parameter name="pattern"&gt;deepseek&lt;/parameter&gt;&lt;/invoke&gt;&lt;/tool_calls&gt;，
/// 或 Qwen 风格的 &lt;tool_call&gt;{"name":"Grep","arguments":{…}}&lt;/tool_call&gt;。
/// 这里硬编码地剥掉 tool_calls 标记与标签，把 XML 转成与协议等价的 JSON 结构（正文 + tool_calls 列表，
/// arguments 仍按协议序列化成 JSON 字符串），使这类回复也能正常发起工具调用而不是把 XML 原文刷进气泡。
/// 认不出该形态（或认出来了但一个工具调用都没解析出）一律返回 null，调用方保持原有"按纯文本回退"的行为。</summary>
static partial class WebReplyParser
{
    /// <summary>XML 兜底入口：识别出 XML 工具调用时返回（正文, 调用列表），否则 null。
    /// 正文 = 原文剔除工具调用标签块、并丢掉仅剩 tool_calls 字样的残留行后的文本。
    /// 入口先做一次 DeepSeek DSML 归一化（见 NormalizeDsml），再走下方标准 XML 解析。</summary>
    internal static (string Content, List<LlmToolCall> Calls)? TryExtractXml(string text)
    {
        var s = NormalizeDsml(text);
        var fenced = FenceContent(s);
        if (fenced != null && fenced.IndexOf("<tool_call", StringComparison.OrdinalIgnoreCase) >= 0)
            s = fenced.Trim();      // 块被 Markdown 围栏包住：只认围栏内的那份
        var block = TagBlock(s, "tool_calls") ?? TagBlock(s, "tool_call");
        if (block is not { } b) return null;

        var calls = ParseXmlCalls(s.Substring(b.OpenEnd, b.BodyEnd - b.OpenEnd));
        if (calls.Count == 0) return null;
        return (StripMarkers(s.Remove(b.Start, b.End - b.Start)), calls);
    }

    /// <summary>DeepSeek 私有工具调用标记（DSML）归一化：网页版 DeepSeek 偶尔无视 JSON 协议，
    /// 改用训练期的特殊 token 形态回工具调用——标签被成对全角竖线（U+FF5C）与 "DSML" 字样包裹，
    /// 形如 "&lt;｜｜DSML｜｜ calls&gt;"、"&lt;｜｜DSML｜｜ invoke name=...&gt;"、闭标签 "&lt;/｜｜DSML｜｜ parameter&gt;"。
    /// 这里以 "DSML" 字样为锚点做定向折回：把 "&lt;[竖线空白]*DSML[竖线空白]*" 折成 "&lt;"（闭标签同理折成 "&lt;/"），
    /// 并把标签名 "calls" 还原为 "tool_calls" 以匹配下方标准解析；不出现 DSML 字样时原样返回、零开销，
    /// 正文里合法出现的全角竖线（无 DSML 锚点）不受影响。
    /// 竖线字符用 \u 转义书写，避免源码里出现全角字符。</summary>
    static string NormalizeDsml(string text)
    {
        var s = (text ?? "").Trim();
        if (s.IndexOf("DSML", StringComparison.OrdinalIgnoreCase) < 0) return s;   // 无 DSML 锚点：原样返回
        s = DsmlCloseCalls.Replace(s, "</tool_calls");
        s = DsmlOpenCalls.Replace(s, "<tool_calls");
        s = DsmlClose.Replace(s, "</");
        s = DsmlOpen.Replace(s, "<");
        return s;
    }

    /// <summary>DSML 特殊 token 的分隔竖线族：DeepSeek 网页版常见全角 U+FF5C，另兼容半角与其它竖线变体
    /// （抓取/转义后可能变形）。仅作 "DSML" 锚点折回中的可选分隔符（见 NormalizeDsml）。</summary>
    const string Bar = @"[|\uFF5C\uFFE8\u2502\u2503\u2016\u01C0]";

    static readonly System.Text.RegularExpressions.Regex DsmlOpenCalls =
        new("<" + Bar + "*DSML" + Bar + @"*\s*calls\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    static readonly System.Text.RegularExpressions.Regex DsmlCloseCalls =
        new("</" + Bar + "*DSML" + Bar + @"*\s*calls\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    static readonly System.Text.RegularExpressions.Regex DsmlOpen =
        new("<" + Bar + "*DSML" + Bar + @"*\s*", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    static readonly System.Text.RegularExpressions.Regex DsmlClose =
        new("</" + Bar + "*DSML" + Bar + @"*\s*", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>把工具调用标签块的内部文本解析成调用列表：先认 &lt;invoke name="…"&gt;…&lt;/invoke&gt;（Claude 风格，
    /// 参数在 &lt;parameter name="…"&gt;值&lt;/parameter&gt; 里），再认标签内直接放 JSON 的 &lt;tool_call&gt;{…}&lt;/tool_call&gt;（Qwen 风格）。</summary>
    static List<LlmToolCall> ParseXmlCalls(string body)
    {
        var calls = new List<LlmToolCall>();
        var from = 0;
        while (true)
        {
            var blk = TagBlock(body, "invoke", from);
            if (blk is not { } b) break;
            from = b.End;
            var name = XmlAttr(b.OpenTag, "name");
            if (name.Length == 0) continue;
            var args = ParseXmlParams(body.Substring(b.OpenEnd, b.BodyEnd - b.OpenEnd));
            calls.Add(new LlmToolCall($"call_web_x{calls.Count + 1}", name.Trim(), args));
        }
        if (calls.Count > 0) return calls;

        from = 0;
        while (true)
        {
            var blk = TagBlock(body, "tool_call", from);
            if (blk is not { } b) break;
            from = b.End;
            if (TryJsonCall(body.Substring(b.OpenEnd, b.BodyEnd - b.OpenEnd), calls.Count) is { } c) calls.Add(c);
        }
        if (calls.Count == 0 && TryJsonCall(body, 0) is { } direct) calls.Add(direct);
        return calls;
    }

    /// <summary>把 &lt;parameter name="参数名"&gt;值&lt;/parameter&gt; 收成一个 arguments JSON 字符串；
    /// 若只有一个名为 arguments/parameters 的参数且其值是 JSON 对象，则原样采用该对象
    /// （等价于协议要求的"入参对象序列化成字符串"）。</summary>
    static string ParseXmlParams(string body)
    {
        var obj = new JsonObject();
        var from = 0;
        while (true)
        {
            var blk = TagBlock(body, "parameter", from) ?? TagBlock(body, "param", from);
            if (blk is not { } b) break;
            from = b.End;
            var name = XmlAttr(b.OpenTag, "name");
            if (name.Length == 0) continue;
            obj[name] = CoerceValue(Unescape(body.Substring(b.OpenEnd, b.BodyEnd - b.OpenEnd).Trim()));
        }
        if (obj.Count == 0) return "{}";
        if (obj.Count == 1)
        {
            var only = obj.First();
            if (only.Key is "arguments" or "parameters")
            {
                var raw = Str(only.Value);
                if (raw.Trim().Length > 0)
                    return ParseObjLoose(raw) is { } innerObj ? innerObj.ToJsonString() : obj.ToJsonString();
            }
        }
        return obj.ToJsonString();
    }

    /// <summary>标签里直接放 JSON 的形态：{…"name":"工具名"…"arguments":{…}} → LlmToolCall；不是 JSON 对象则 null</summary>
    static LlmToolCall? TryJsonCall(string s, int index)
    {
        var t = s.Trim();
        if (t.Length == 0 || t[0] != '{') return null;
        var frag = BalancedFragment(t, 0) ?? "";
        return frag.Length > 0 && ParseObjLoose(frag) is { } o ? ToCall(o, index) : null;
    }

    /// <summary>取 &lt;tag …&gt;…&lt;/tag&gt; 的跨度（from 起首个匹配）。未闭合时按"一直到文末"处理，
    /// 自闭合标签的体为空；找不到返回 null。</summary>
    static TagSpan? TagBlock(string s, string tag, int from = 0)
    {
        var open = FindTag(s, tag, from);
        if (open < 0) return null;
        var openEnd = s.IndexOf('>', open);
        if (openEnd < 0) return null;
        openEnd++;
        var openTag = s.Substring(open, openEnd - open);
        if (openTag.EndsWith("/>", StringComparison.Ordinal))
            return new TagSpan(open, openEnd, openEnd, openEnd, openTag);
        var close = FindTag(s, "/" + tag, openEnd);
        if (close < 0) return new TagSpan(open, s.Length, openEnd, s.Length, openTag);
        var closeEnd = s.IndexOf('>', close);
        closeEnd = closeEnd < 0 ? s.Length : closeEnd + 1;
        return new TagSpan(open, closeEnd, openEnd, close, openTag);
    }

    /// <summary>找 &lt;tag 的位置：标签名之后只允许空白、'&gt;' 或 '/'，避免 tool_call 误命中 tool_calls</summary>
    static int FindTag(string s, string tag, int from)
    {
        var probe = "<" + tag;
        var i = from;
        while (i < s.Length)
        {
            var at = s.IndexOf(probe, i, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return -1;
            var j = at + probe.Length;
            if (j >= s.Length || char.IsWhiteSpace(s[j]) || s[j] == '>' || s[j] == '/') return at;
            i = at + probe.Length;
        }
        return -1;
    }

    /// <summary>取标签里的属性值（支持 name="x" / name='x' / name=x）；没有该属性返回空串</summary>
    static string XmlAttr(string tag, string attr)
    {
        var i = tag.IndexOf(attr, StringComparison.OrdinalIgnoreCase);
        while (i >= 0)
        {
            var j = i + attr.Length;
            var boundaryOk = i == 0 || (!char.IsLetterOrDigit(tag[i - 1]) && tag[i - 1] != '_');
            while (boundaryOk && j < tag.Length && char.IsWhiteSpace(tag[j])) j++;
            if (boundaryOk && j < tag.Length && tag[j] == '=')
            {
                j++;
                while (j < tag.Length && char.IsWhiteSpace(tag[j])) j++;
                if (j >= tag.Length) return "";
                var q = tag[j];
                if (q is '"' or '\'')
                {
                    var end = tag.IndexOf(q, j + 1);
                    return end < 0 ? tag.Substring(j + 1) : tag.Substring(j + 1, end - j - 1);
                }
                var e = j;
                while (e < tag.Length && !char.IsWhiteSpace(tag[e]) && tag[e] != '>') e++;
                return tag.Substring(j, e - j);
            }
            i = tag.IndexOf(attr, i + attr.Length, StringComparison.OrdinalIgnoreCase);
        }
        return "";
    }

    /// <summary>XML 文本值转 JSON 节点：看起来是 JSON（true/false/null/数字/对象/数组/带引号字符串）就按 JSON 收，
    /// 否则按原文收成字符串——否则 <c>&lt;parameter name="ignore_case"&gt;true&lt;/parameter&gt;</c> 会把布尔值传成 "true"。</summary>
    static JsonNode CoerceValue(string v)
    {
        var t = v.Trim();
        if (t.Length == 0) return JsonValue.Create("")!;
        var c = t[0];
        if (c is '{' or '[' or '"' || char.IsDigit(c) || c == '-' || t is "true" or "false" or "null")
        {
            try { if (JsonNode.Parse(t) is { } n) return n; } catch { }
        }
        return JsonValue.Create(v)!;
    }

    /// <summary>XML 实体反转义（顺序敏感：&amp;amp; 必须最后处理）</summary>
    static string Unescape(string s)
    {
        if (s.IndexOf('&') < 0) return s;
        return s.Replace("&lt;", "<").Replace("&gt;", ">")
                .Replace("&quot;", "\"").Replace("&apos;", "'").Replace("&amp;", "&");
    }

    /// <summary>剥掉标签块之外残留的印记：整行只剩 tool_calls 字样的行（模型常写成 "tool_calls:"、"**tool_calls**" 这类标题）</summary>
    static string StripMarkers(string s)
    {
        var sb = new StringBuilder();
        foreach (var line in s.Replace("\r\n", "\n").Split('\n'))
        {
            var t = line.Trim().Trim('*', '`', '#', '-', '_', ':', '：', '[', ']', '【', '】', '"', '\'').Trim();
            if (t.Equals("tool_calls", StringComparison.OrdinalIgnoreCase) || t.Equals("tool call", StringComparison.OrdinalIgnoreCase))
                continue;
            sb.Append(line).Append('\n');
        }
        return sb.ToString().Trim();
    }

    /// <summary>XML 标签块跨度：Start/End 含标签本身；OpenEnd = 开标签结束位置，BodyEnd = 闭标签起始（未闭合与 End 相同）</summary>
    readonly record struct TagSpan(int Start, int End, int OpenEnd, int BodyEnd, string OpenTag);
}
