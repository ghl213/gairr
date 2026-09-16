using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace GAIRR.Core;

/// <summary>代码切片提取器（MapSlice 工具支撑）：按"文件:起始行"或"文件:起始行-结束行"
/// 提取目标方法/类的完整代码块（自动向上吸附前置注释、向下匹配大括号边界），
/// 让模型一次调用即可拿到目标符号完整源码，无需 Read 猜行号二次往返。</summary>
public static class SliceExtractor
{
    const int MaxOutChars = 12000;     // 单次输出预算（含 chain 引用段；chain=1 时主切片与引用共享）
    const int MaxBlockLines = 300;     // 单个代码块最大行数（防无边界块把预算吃光）
    const int MaxFiles = 5;            // 单次调用最多切片文件数
    const int CommentLookback = 12;    // 向上吸附注释/空行窗口
    const int ChainCtx = 3;            // chain=1 时引用行上下各取 N 行上下文
    const int ChainMaxRefs = 6;        // chain=1 时最多展示引用处数

    static readonly char[] Braces = { '{', '}' };
    static readonly string[] CommentStarts = { "//", "/*", "///", "#", ";", "--" };

    /// <summary>MapSlice 入口：spec 支持多段（逗号/分号分隔），形如
    /// "Core/Tools.cs:165"、"Tools.cs:165-185"、"Tools.cs:Read"（符号名回退 MapTrace 定位）。
    /// chain=true 时额外附带主块符号的被引用处上下文（跨文件调用链一次拿全）。</summary>
    public static string Run(AppConfig cfg, string spec, bool chain = false)
    {
        if (string.IsNullOrWhiteSpace(spec)) return "错误：spec 为空（格式：文件:起始行 或 文件:起始行-结束行，多段用逗号分隔）";
        var parts = spec.Split(',', ';', '，', '；')
            .Select(s => s.Trim()).Where(s => s.Length > 0).Take(MaxFiles + 1).ToList();
        if (parts.Count == 0) return "错误：spec 解析为空";
        if (parts.Count > MaxFiles) return $"错误：一次最多 {MaxFiles} 个切片，当前 {parts.Count} 段";

        var sb = new StringBuilder();
        var totalChars = 0;
        var chainSyms = new List<string>();   // 收集主块符号，供 chain 段统一追加
        foreach (var p in parts)
        {
            var text = ExtractOne(cfg, p, MaxOutChars - totalChars, out var sym);
            sb.Append(text).Append('\n');
            totalChars += text.Length + 1;
            if (sym.Length > 0 && !chainSyms.Contains(sym, StringComparer.OrdinalIgnoreCase)) chainSyms.Add(sym);
            if (totalChars >= MaxOutChars) { sb.Append("（已达输出预算上限，剩余切片省略；请分批或缩小范围）"); return sb.ToString().TrimEnd('\n'); }
        }

        // 调用链段：主块符号的被引用处（引用行±N 行 + 所属方法），共享剩余预算
        if (chain)
        {
            foreach (var sym in chainSyms)
            {
                if (totalChars >= MaxOutChars) { sb.Append("（调用链引用段因预算省略）"); break; }
                var c = AppendChain(cfg, sym, MaxOutChars - totalChars);
                if (c.Length > 0) { sb.Append('\n').Append(c).Append('\n'); totalChars += c.Length + 2; }
            }
        }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>chain 段：符号被引用处列表，每处给所属方法名 + 引用行±ChainCtx 行上下文</summary>
    static string AppendChain(AppConfig cfg, string sym, int budget)
    {
        if (budget <= 80) return "";
        var refs = SymbolIndex.GetRefs(cfg, sym, ChainMaxRefs);
        if (refs.Count == 0) return "";
        var sb = new StringBuilder();
        sb.Append("【调用链 · 符号 ").Append(sym).Append(" 被引用 ").Append(refs.Count).Append(" 处】\n");
        foreach (var (rel, line) in refs)
        {
            var ctx = RefContext(cfg, rel, line, budget - sb.Length);
            if (ctx.Length == 0) { sb.Append("▸ ").Append(rel).Append(':').Append(line).Append("（超预算省略）\n"); continue; }
            sb.Append("▸ ").Append(rel).Append(':').Append(line).Append('\n').Append(ctx);
            if (sb.Length >= budget) break;
        }
        return sb.ToString();
    }

    /// <summary>单处引用上下文：向上找所属方法声明，输出方法名 + 引用行±ChainCtx 行</summary>
    static string RefContext(AppConfig cfg, string rel, int line, int budget)
    {
        try
        {
            var path = Phase1Tools.Locate(cfg, rel, out _);
            if (!File.Exists(path)) return "";
            var (text, _) = Phase1Tools.Decode(File.ReadAllBytes(path));
            var lines = text.Replace("\r\n", "\n").Split('\n');
            if (line < 1 || line > lines.Length) return "";

            // 向上找所属声明（最近一条像声明的行）
            var owner = "";
            for (var r = line - 1; r >= 0 && r >= line - 60; r--)
            {
                var t = lines[r].Trim();
                if (t.Length == 0 || IsComment(t)) continue;
                if (LooksLikeDecl(t) || Regex.IsMatch(t, @"^[\w<>\[\]\?]+\s+[\w]+\s*\("))
                { owner = t.Length > 80 ? t[..80] : t; break; }
            }

            var sb = new StringBuilder();
            if (owner.Length > 0) sb.Append("  所属: ").Append(owner).Append('\n');
            var lo = Math.Max(1, line - ChainCtx);
            var hi = Math.Min(lines.Length, line + ChainCtx);
            for (var i = lo; i <= hi; i++)
            {
                sb.Append("  ").Append(i == line ? "▶" : " ").Append(i).Append('→')
                  .Append(LLMClient.Trunc(lines[i - 1], 120)).Append('\n');
                if (sb.Length > budget) break;
            }
            return sb.ToString();
        }
        catch { return ""; }
    }

    /// <summary>从切片声明行提取符号名（方法名/类名），供 chain 引用检索；提取失败返回空；
    /// 元组返回类型先归一化为 Tuple，否则方法名匹配不到</summary>
    static string SymOfDecl(string declLine)
    {
        if (declLine.Length == 0) return "";
        var norm = Regex.Replace(declLine, @"\([^()]*\)(?=\s*(?:>|[A-Za-z_]))", "Tuple");   // (int a, int b) Foo( → Tuple Foo(
        var mc = Regex.Match(norm, @"\b(class|interface|enum|struct|record)\s+([A-Za-z_][A-Za-z0-9_]*)");
        if (mc.Success) return mc.Groups[2].Value;
        var mm = Regex.Match(norm, @"\b([A-Za-z_][A-Za-z0-9_]*)\s*\([^;{}=]*\)\s*(\{|=>|$|where|:)");
        if (mm.Success)
        {
            var name = mm.Groups[1].Value;
            if (name is not ("if" or "for" or "foreach" or "while" or "switch" or "catch" or "return" or "new")) return name;
        }
        return "";
    }

    /// <summary>解析单段 spec 并提取切片；找不到行号时回退 MapTrace 按符号名定位。
    /// symbol 输出主块声明行的符号名（供 chain 引用检索，提取失败为空）。</summary>
    static string ExtractOne(AppConfig cfg, string part, int budget, out string symbol)
    {
        symbol = "";
        if (budget <= 60) return "（输出预算耗尽）";
        var (rel, start, end, sym) = ParseSpec(part);
        if (rel.Length == 0) return $"✗ 无法解析片段 \"{part}\"（应为 文件:行 或 文件:起-止）";

        var path = Phase1Tools.Locate(cfg, rel, out var note);
        if (path.Length == 0) return $"✗ {note}";
        if (!File.Exists(path)) return $"✗ 文件不存在 {rel}（{note}）";

        // 行号缺失但有符号名（如 Tools.cs:Read）：回退符号索引定位首处定义
        if (start <= 0)
        {
            if (sym.Length == 0)
            {
                // 只给了文件名（无行号也无符号名）：回退返回文件头部声明概览，引导模型先定位再切片，避免报错后反复重试烧轮次
                var head = FileHead(cfg, path, budget);
                if (head.Length > 0) return head;
                return $"✗ 片段 \"{part}\" 未提供行号也未带符号名（格式：文件:行号 或 文件:符号名；行号可先用 MapTrace/Grep 定位）";
            }
            var loc = SymbolIndex.LocateDef(cfg, sym, Path.GetFileName(path));
            if (loc.start <= 0) return $"✗ 符号 \"{sym}\" 未在 {rel} 中命中定义（可改用 MapTrace q={sym} 确认符号名与所在文件，或用行号格式 文件:行号）";
            (start, end) = (loc.start, loc.end);
            symbol = sym;   // spec 已带符号名，直接用
        }

        try
        {
            var (text, enc) = Phase1Tools.Decode(File.ReadAllBytes(path));
            var lines = text.Replace("\r\n", "\n").Split('\n');
            if (start < 1 || start > lines.Length) return $"✗ {Rel(cfg, path)} 只有 {lines.Length} 行，起始行 {start} 越界";

            var (s, e) = ExpandBlock(lines, start, end);
            if (symbol.Length == 0) symbol = SymOfDecl(lines[start - 1]);   // 行号模式下从声明行提取符号

            // 行号漂移警示：模型常拿历史回合的旧行号来切片（文件已改过），旧行号落进注释/间隙时
            // ExpandBlock 会静默返回相邻方法块——这是幻觉主通道。符号索引永远新鲜（按 mtime 重建），
            // 无法"重定位到索引行号"（索引行=当前文件行）；有效信号是"请求行是否指向返回块的真实声明行"。
            // 请求行是注释行且与块内实际声明行相距超容差 → 显式警示（不重定位：工具不知道模型想要哪个符号），
            // 让模型核对内容而非盲信。
            var driftNote = "";
            if (sym.Length == 0)
                driftNote = DriftWarning(lines, start, e);

            var sb = new StringBuilder();
            var relOut = Rel(cfg, path);
            sb.Append("【").Append(relOut).Append(" · ").Append(enc)
              .Append(" · 行 ").Append(s).Append('-').Append(e)
              .Append(" / 共 ").Append(lines.Length).Append("】\n");
            if (driftNote.Length > 0) sb.Append(driftNote);
            for (var i = s; i <= e; i++)
            {
                sb.Append(i).Append('→').Append(lines[i - 1]).Append('\n');
                if (sb.Length > budget) { sb.Append("（切片超预算被截断，可指定更小的 起-止 范围）"); break; }
            }
            return sb.ToString();
        }
        catch (Exception ex) { return $"✗ 读取 {rel} 失败：{ex.Message}"; }
    }

    /// <summary>解析 "文件:起始行" / "文件:起始行-结束行" / "文件:符号名"；
    /// 无行号时 start=0 表示符号名模式，sym 为冒号后的符号名（其余模式为空）；
    /// 尾部为空/带引号空串时视为无符号（如 "Tools.cs:"），按纯文件名处理</summary>
    static (string rel, int start, int end, string sym) ParseSpec(string part)
    {
        var ci = part.LastIndexOf(':');
        if (ci <= 0) return (part, 0, 0, "");
        var rel = part[..ci].Trim().Trim('"', '\'');
        var tail = part[(ci + 1)..].Trim().Trim('"', '\'');
        if (tail.Length == 0) return (rel, 0, 0, "");
        if (int.TryParse(tail, out var line)) return (rel, line, 0, "");
        var m = Regex.Match(tail, @"^(\d+)\s*[-~–]\s*(\d+)$");
        if (m.Success) return (rel, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), "");
        return (rel, 0, 0, tail);   // 符号名模式：tail 即符号名，交由回退定位
    }

    /// <summary>行号漂移警示（行号模式）：请求行不是代码声明行（注释/空行/方法体内）时，
    /// 检查返回块内第一条声明行与请求行的距离，超过容差（注释吸附窗口）→ 文件大概率已变更、
    /// 旧行号失效，显式提醒模型核对内容、改用 文件:符号名 定位（工具不知道模型想要哪个符号，不自动重定位）。
    /// 请求行本身是声明行、距块内声明行在容差内、或块内无声明行时返回空（不警示）。</summary>
    static string DriftWarning(string[] lines, int requestLine, int blockEnd)
    {
        const int DriftTol = 12;   // 容差窗口（= CommentLookback，覆盖向上吸附注释的最大行数）
        if (SymOfDecl(lines[requestLine - 1]).Length > 0) return "";   // 请求行是声明行，可信
        var end = Math.Min(blockEnd, lines.Length);
        for (var r = requestLine - 1; r <= end; r++)
        {
            var t = lines[r].Trim();
            if (t.Length == 0 || IsComment(t)) continue;
            var sym = SymOfDecl(t);
            if (sym.Length > 0)
            {
                var dist = r - (requestLine - 1);
                return dist > DriftTol
                    ? $"（⚠ 行号漂移：请求行 {requestLine} 处非代码声明，块内实际声明在行 {r + 1}（{sym}），相距 {dist} 行——文件可能已变更，旧行号失效，请核对返回内容是否为目标符号，建议改用 文件:符号名 定位）\n"
                    : "";   // 容差内（注释吸附等），不警示
            }
            break;   // 块内无声明行（纯数据/配置块），无法判断，不警示
        }
        return "";
    }

    /// <summary>只给文件名时的回退：返回文件头部概览（顶层声明行），引导模型定位行号后再切片</summary>
    static string FileHead(AppConfig cfg, string path, int budget)
    {
        try
        {
            var (text, enc) = Phase1Tools.Decode(File.ReadAllBytes(path));
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var sb = new StringBuilder();
            sb.Append("【").Append(Rel(cfg, path)).Append(" · ").Append(enc)
              .Append(" · 共 ").Append(lines.Length).Append(" 行 · 未指定行号，以下为顶层声明概览】\n");
            var shown = 0;
            for (var i = 0; i < lines.Length && shown < 25; i++)
            {
                var t = lines[i].Trim();
                if (t.Length == 0 || IsComment(t)) continue;
                if (LooksLikeDecl(t) || Regex.IsMatch(t, @"^\s*(public|private|protected|internal|static|class|interface|enum|struct|record|def |func )"))
                {
                    sb.Append(i + 1).Append('→').Append(LLMClient.Trunc(lines[i], 120)).Append('\n');
                    shown++;
                    if (sb.Length > budget) break;
                }
            }
            sb.Append("（需要完整代码块请用 文件:行号 重试，行号见上方概览；或用 MapTrace 按符号名定位）");
            return sb.ToString();
        }
        catch { return ""; }
    }

    /// <summary>扩展为完整代码块：向上吸附前置注释（含 XML 文档注释），向下按大括号配对找块尾；
    /// 已给定 end 时仅向上吸附注释，不再扩块。</summary>
    static (int start, int end) ExpandBlock(string[] lines, int start, int end)
    {
        var s = start;
        // 向上吸附：连续注释行与紧邻注释的空行（注释与声明之间允许 1 空行）
        var i = s - 2;
        var blanks = 0;
        while (i >= 0 && i >= start - 1 - CommentLookback)
        {
            var t = lines[i].Trim();
            if (t.Length == 0) { blanks++; if (blanks > 1) break; i--; continue; }
            if (!IsComment(t)) break;
            blanks = 0;
            s = i + 1;
            i--;
        }

        if (end > 0) return (s, Math.Min(end, lines.Length));

        // 向下扩块：从 start 行起找首个 '{'，按配对找 '}'；无大括号语言（py）退化为缩进块
        var e = start - 1;
        var depth = 0;
        var opened = false;
        var max = Math.Min(lines.Length - 1, start - 1 + MaxBlockLines);
        for (var r = start - 1; r <= max; r++)
        {
            var line = lines[r];
            // 去掉字符串与注释里的大括号，避免配对误判（简化处理：只去行尾 // 注释）
            var code = StripTrailingComment(line);
            foreach (var ch in code)
            {
                if (ch == '{') { depth++; opened = true; }
                else if (ch == '}') depth--;
            }
            e = r + 1;
            if (opened && depth <= 0) break;
            // 无 '{' 的单行声明（如表达式属性/接口方法）：到下一空行或下一声明为止
            if (!opened && r > start && LooksLikeDecl(lines[r].Trim())) break;
        }
        if (e < start) e = start;
        return (s, e);
    }

    /// <summary>行是否为注释行（//、/*、#、;、-- 及 XML 文档标签行）</summary>
    static bool IsComment(string t) =>
        CommentStarts.Any(p => t.StartsWith(p, StringComparison.Ordinal)) ||
        t.StartsWith("*") || t.StartsWith("'");

    /// <summary>去掉行尾 // 注释（不处理字符串内的转义，仅用于大括号配对的保守估计）</summary>
    static string StripTrailingComment(string line)
    {
        var idx = line.IndexOf("//", StringComparison.Ordinal);
        return idx >= 0 ? line[..idx] : line;
    }

    /// <summary>粗略判断一行像新声明（顶格修饰符/类型开头），用于无大括号块的终止</summary>
    static bool LooksLikeDecl(string t) =>
        t.Length > 0 && (char.IsLetter(t[0]) || t[0] == '[') &&
        (t.StartsWith("public ") || t.StartsWith("private ") || t.StartsWith("protected ") ||
         t.StartsWith("internal ") || t.StartsWith("static ") || t.StartsWith("void ") ||
         t.StartsWith("class ") || t.StartsWith("def ") || t.StartsWith("func ") ||
         Regex.IsMatch(t, @"^[\w<>\[\]\?]+\s+[\w]+\s*[\(=]"));

    /// <summary>输出用相对路径（项目根之外则原样返回全路径）</summary>
    static string Rel(AppConfig cfg, string fullPath)
    {
        try
        {
            var r = Path.GetRelativePath(cfg.ProjectRoot, fullPath).Replace('\\', '/');
            return r.StartsWith("..", StringComparison.Ordinal) ? fullPath.Replace('\\', '/') : r;
        }
        catch { return fullPath.Replace('\\', '/'); }
    }
}
