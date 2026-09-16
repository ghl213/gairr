using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Text.Encodings.Web;

namespace GAIRR.Core;

/// <summary>符号倒排索引（MapTrace 工具支撑）：把"类名/接口名/方法名/文件名主干"映射到
/// 定义位置（文件:行号 + 所属命名空间/类 + 签名 + 中文注释标签）与引用位置（文件:行号），写入 .gairr/symbols.json。
/// 中文标签自动收录：文件头注释进 file 符号、声明上方中文注释进方法/类符号（note 字段），支持按中文搜符号。
/// 纯程序提取（正则），零 token；文件 mtime 快照判断是否需要重建，工具调用时懒刷新。</summary>
public static class SymbolIndex
{
    const string DirName = ".gairr";
    const string IndexName = "symbols.json";
    const int MaxScanBytes = 512 * 1024;      // 单文件上限（超大文件跳过）
    const int MaxTotalBytes = 16 * 1024 * 1024; // 总量上限（超限停止收录）
    const int MaxFiles = 1500;                // 索引文件数上限
    const int MaxSymbols = 20000;             // 符号总数上限
    const int MaxRefPos = 400;                // 单符号引用位置总数上限
    const int MaxRefPerFile = 8;              // 单文件内引用位置上限（防样板重复）
    const int MaxRefShow = 6;                 // 检索输出引用位置条数

    static readonly string[] CodeExts =
    {
        ".cs", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".py", ".go", ".java", ".kt",
        ".dart", ".rb", ".php", ".vue", ".cpp", ".h", ".c", ".xaml", ".csproj",
    };
    static readonly Regex ClassSymRe = new(@"\b(class|interface|enum|struct|record)\s+([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex NsRe = new(@"^\s*namespace\s+([\w\.]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // 属性/字段：`修饰符* 类型 名字 => 表达式` 或 `修饰符* 类型 名字 { get`（避免 var x = 误报）
    static readonly Regex PropRe = new(
        @"^\s*(?:(?:public|private|protected|internal|static|virtual|override|abstract|readonly|async)\s+)*(?:[\w<>\[\]\.\?]+\s+)+([A-Za-z_][A-Za-z0-9_]*)\s*(=>|\{\s*get\b)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex MethodRe = new(
        @"^\s*(?:(?:public|private|protected|internal|static|virtual|override|sealed|partial|abstract|extern|readonly|async|export|declare|final|default|unsafe|volatile)\s+)*(?:[\w<>\[\]\.\?]+(?:\s*\[\s*\])?\s+)*([A-Za-z_][A-Za-z0-9_]*)\s*\([^;{}]*\)(?:\s*\{|\s*=>|\s*$|\s*where\b|\s*:\s*(?:#.*)?$|\s*;\s*(?://.*)?$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly string[] CtrlWords =
    {
        "if", "for", "foreach", "while", "switch", "catch", "using", "return", "new", "typeof",
        "nameof", "lock", "case", "when", "else", "elif", "try", "do", "throw", "yield",
        "var", "let", "const", "with", "except", "import", "from", "print", "assert", "del",
        "global", "nonlocal", "base", "this", "synchronized",
        // 修饰符/内建类型：表达式属性等误匹配防护
        "public", "private", "protected", "internal", "static", "abstract", "sealed", "virtual",
        "override", "readonly", "volatile", "extern", "partial", "async", "void", "int", "string",
        "bool", "long", "double", "float", "decimal", "char", "byte", "short", "object", "class",
        "interface", "enum", "struct", "record", "get", "set", "init",
    };

    // ---- Java/Kotlin/Dart 强化 ----
    static readonly Regex AnnotRe = new(@"^@([A-Za-z_]\w*)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // 注解上的 HTTP 路径：@GetMapping("/user/list") / @RequestMapping(value="...") / @Path("...")
    static readonly Regex EndpointRe = new(@"@\w+(?:Mapping|Path)\s*\(\s*(?:value\s*=\s*)?[""']([^""']+)[""']",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // Java 字段声明（lombok 类里生成 get/set 伪符号用）：private String name;  形式
    static readonly Regex FieldRe = new(@"^\s*(?:public|private|protected)\s+(?:final\s+|static\s+)*[\w<>,.\[\]\?]+\s+([a-z_]\w*)\s*(?:=|;|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // 泛型方法头：`<T> T foo(` / `<T extends X> T foo(`（去掉后 MethodRe 才能命中）
    static readonly Regex GenericMethodRe = new(@"^<[A-Za-z_][^>]{0,60}>\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // 泛型头解开后的剩余部分必须是"类型 名("形态，避免误伤表达式
    static readonly Regex MethodLikeRe = new(@"^\s*[\w<>\[\]\.\?]+(?:\s*\[\s*\])?\s+[A-Za-z_]\w*\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly HashSet<string> LombokAnns = new(StringComparer.Ordinal)
    {
        "Data", "Getter", "Setter", "AllArgsConstructor", "NoArgsConstructor", "Value", "Builder",
    };

    // ---- Vue SFC 强化 ----
    // 组件标签：<UserCard/> 或 kebab-case <el-button>（HTML 内建标签排除）
    static readonly Regex VueCompTagRe = new(@"<([A-Z][A-Za-z0-9]*|[a-z][a-z0-9]*-[a-z][a-z0-9-]*)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // 行内连续中文（按钮文案/标签文字，2 字起）
    static readonly Regex CjkRunRe = new(@"[\u4e00-\u9fff]{2,}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly HashSet<string> HtmlTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "div", "span", "p", "a", "ul", "ol", "li", "table", "tr", "td", "th", "thead", "tbody",
        "form", "input", "button", "select", "option", "img", "video", "audio", "header", "footer",
        "nav", "main", "section", "article", "aside", "h1", "h2", "h3", "h4", "h5", "h6",
        "label", "textarea", "em", "strong", "b", "i", "u", "br", "hr", "svg", "canvas", "iframe",
        "link", "meta", "title", "style", "script", "template", "slot", "transition", "keep-alive",
        "component", "teleport", "suspense", "transition-group", "source", "picture", "dialog",
    };

    // .NET 8 JsonNode 序列化要求显式 TypeInfoResolver；符号索引开放中文，用宽松转义减小体积
    static readonly System.Text.Json.JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    sealed class Def
    {
        public string Rel = "";
        public int Line;
        public string Kind = "";
        public string Ns = "";          // 所属命名空间
        public string Container = "";   // 所属类/接口（方法/属性归属）
        public string Sig = "";         // 声明签名摘要（原行规整后截断）
        public string Note = "";        // 声明前的中文注释标签（文件头/方法注释，支持中文检索）
    }

    sealed class RefPos { public string Rel = ""; public int Line; }

    sealed class Index
    {
        public string Root = "";
        public Dictionary<string, string> Snap = new(StringComparer.OrdinalIgnoreCase); // rel → mtime
        public Dictionary<string, List<Def>> Defs = new(StringComparer.OrdinalIgnoreCase); // 符号 → 定义
        public Dictionary<string, List<RefPos>> Refs = new(StringComparer.OrdinalIgnoreCase); // 符号 → 引用位置
        public readonly Dictionary<string, string> Heads = new(StringComparer.OrdinalIgnoreCase); // rel → 文件头中文注释（文件级标签，不受同名类顶替影响）
        public Dictionary<string, HashSet<string>> NoteGrams = new(StringComparer.Ordinal); // 注释 2-gram → 符号名集合（倒排，加速中文查询）
        public Dictionary<string, HashSet<string>> SymGrams = new(StringComparer.Ordinal); // 符号名 2-gram(小写) → 符号名集合（倒排，加速符号名包含查询）
    }

    static readonly object Sync = new();
    static Index? cache;          // 内存缓存（按项目根区分）

    /// <summary>构建注释 2-gram 倒排：对每个符号的全部中文注释拆 2-gram，映射到符号名集合。</summary>
    static void BuildGrams(Index idx)
    {
        idx.NoteGrams = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var kv in idx.Defs)
        {
            var name = kv.Key;
            foreach (var d in kv.Value)
            {
                if (d.Note.Length < 2) continue;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (var i = 0; i < d.Note.Length - 1; i++)
                {
                    var g = d.Note.Substring(i, 2);
                    if (g[0] < ' ' || g[1] < ' ') continue;   // 跳过含空白的 gram
                    if (!seen.Add(g)) continue;
                    if (!idx.NoteGrams.TryGetValue(g, out var set)) idx.NoteGrams[g] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    set.Add(name);
                }
            }
        }
    }

    /// <summary>构建符号名 2-gram 倒排：对每个符号名拆小写 2-gram，映射到符号名集合（加速符号名 Contains 查询）。</summary>
    static void BuildSymGrams(Index idx)
    {
        idx.SymGrams = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var name in idx.Defs.Keys)
        {
            if (name.Length < 2) continue;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < name.Length - 1; i++)
            {
                var g = name.Substring(i, 2).ToLowerInvariant();
                if (!seen.Add(g)) continue;
                if (!idx.SymGrams.TryGetValue(g, out var set)) idx.SymGrams[g] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(name);
            }
        }
    }

    /// <summary>按查询词拆 2-gram，取倒排交集得候选符号名集合（任一 gram 未命中则候选为空）。</summary>
    static HashSet<string>? GramCandidates(Index idx, List<string> kwGrams)
    {
        if (kwGrams.Count == 0) return null;   // 无 gram（短查询/英文）→ 不启用倒排，走全量
        return IntersectGrams(idx.NoteGrams, kwGrams);
    }

    /// <summary>通用倒排交集：对每个 gram 取倒排集合求交；任一 gram 未命中返回空集（=无候选）。
    /// 返回 null 表示 gram 列表为空（不启用倒排，调用方走全量）。</summary>
    static HashSet<string>? IntersectGrams(Dictionary<string, HashSet<string>> inv, List<string> grams)
    {
        if (grams.Count == 0) return null;
        HashSet<string>? cand = null;
        foreach (var g in grams)
        {
            if (!inv.TryGetValue(g, out var set)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            cand ??= new HashSet<string>(set, StringComparer.OrdinalIgnoreCase);
            cand.IntersectWith(set);
        }
        return cand ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>MapTrace 入口：确保索引新鲜后按符号检索。q 为空时只返回索引状态。</summary>
    public static string Run(AppConfig cfg, string q, int max, bool defOnly)
    {
        var idx = Fresh(cfg);
        if (idx == null) return "错误：符号索引构建失败（项目根无效）";
        if (string.IsNullOrWhiteSpace(q))
            return $"符号索引：{idx.Defs.Count} 个符号，{idx.Snap.Count} 个文件（.gairr/symbols.json），累 {idx.Defs.Values.Sum(d => d.Count)} 处定义";

        var kw = q.Trim();
        // 中文查询拆 2-gram：注释措辞与查询不完全一致（如“智能搜索”vs“智能检索”）时仍能召回；
        // 命中分级：符号名前缀 > 符号名包含 > 标签整串 > 仅标签 gram；同档内按定义数降序（核心符号优先）
        // 仅 gram 命中需 ≥2 个 gram（单 gram 擦边不算）：否则“文件/确认/删除”等通用词会让高频符号（Program/Id）霸榜
        var kwGrams = new List<string>();
        if (kw.Length > 2 && kw[0] >= '\u4e00' && kw[0] <= '\u9fff')
        {
            for (int i = 0; i < kw.Length - 1; i++) kwGrams.Add(kw.Substring(i, 2));
        }
        bool NoteHit(List<Def> defs) =>
            defs.Any(d => d.Note.Length > 0 && (d.Note.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                (kwGrams.Count > 0 && kwGrams.Count(g => d.Note.Contains(g, StringComparison.OrdinalIgnoreCase)) >= 2)));
        bool NoteExactHit(List<Def> defs) =>
            defs.Any(d => d.Note.Contains(kw, StringComparison.OrdinalIgnoreCase));
        // 倒排缩小候选：符号名查询用 SymGrams（符号名含 kw 全部 2-gram），中文查询用 NoteGrams；
        // 两类候选取并集，只遍历候选符号（避免全量线性 Contains 扫描）
        HashSet<string>? cand = null;
        if (kw.Length >= 2)
        {
            // 符号名 2-gram 倒排交集
            var symGrams = new List<string>();
            for (int i = 0; i < kw.Length - 1; i++) symGrams.Add(kw.Substring(i, 2).ToLowerInvariant());
            cand = IntersectGrams(idx.SymGrams, symGrams);
        }
        var noteCand = GramCandidates(idx, kwGrams);
        if (cand != null && noteCand != null) cand.UnionWith(noteCand);
        else if (noteCand != null) cand = noteCand;
        var hits = idx.Defs
            .Where(kv => (cand == null || cand.Contains(kv.Key)) &&
                         (kv.Key.Contains(kw, StringComparison.OrdinalIgnoreCase) || NoteHit(kv.Value)))
            .Select(kv => new
            {
                Name = kv.Key,
                Count = kv.Value.Count,
                Tier = kv.Key.StartsWith(kw, StringComparison.OrdinalIgnoreCase) ? 0
                     : kv.Key.Contains(kw, StringComparison.OrdinalIgnoreCase) ? 1
                     : NoteExactHit(kv.Value) ? 2 : 3
            })
            .OrderBy(h => h.Tier).ThenByDescending(h => h.Count)
            .Take(Math.Max(1, Math.Min(max, 50)))
            .Select(h => h.Name).ToList();
        if (hits.Count == 0) return $"符号索引：未找到包含 \"{q}\" 的符号或中文标签（试试更短片段；中文查询依赖代码注释）";

        var sb = new StringBuilder();
        sb.Append($"符号索引：\"{q}\" 命中 {hits.Count} 个符号");
        if (idx.Defs.Count > MaxSymbols) sb.Append("（注：符号量已达上限，可能未收录全部）");
        sb.Append('\n');
        foreach (var name in hits)
        {
            var defs = idx.Defs[name];
            var first = defs[0];
            sb.Append("▸ ").Append(name);
            if (first.Kind.Length > 0) sb.Append('（').Append(first.Kind).Append('）');
            sb.Append('\n');
            foreach (var d in defs.Take(5))
            {
                sb.Append("  定义: ").Append(d.Rel).Append(':').Append(d.Line);
                if (d.Ns.Length > 0 || d.Container.Length > 0)
                    sb.Append('（').Append(d.Ns.Length > 0 ? d.Ns : "?").Append("::")
                      .Append(d.Container.Length > 0 ? d.Container : "?").Append('）');
                if (d.Sig.Length > 0) sb.Append(' ').Append(d.Sig);
                sb.Append('\n');
                if (d.Note.Length > 0) sb.Append("    标签: ").Append(d.Note).Append('\n');
            }
            if (defs.Count > 5) sb.Append("（等 ").Append(defs.Count).Append(" 处）\n");
            if (!defOnly && idx.Refs.TryGetValue(name, out var refs) && refs.Count > 0)
            {
                sb.Append("  引用: ").Append(refs.Count).Append(" 处：")
                  .Append(string.Join("、", refs.Take(MaxRefShow).Select(r => r.Rel + ":" + r.Line)))
                  .Append(refs.Count > MaxRefShow ? "…" : "").Append('\n');
            }
        }
        sb.Append("（行号为定义声明所在行；取完整代码块用 MapSlice spec=文件:行号，全文搜索配合 Grep）");
        return sb.ToString();
    }

    /// <summary>供 MapSlice 回退定位：按符号名（精确优先、部分匹配兜底）找首处定义，
    /// fileHint 非空时优先同名文件内的定义。未命中返回 (0,0)。</summary>
    public static (int start, int end) LocateDef(AppConfig cfg, string sym, string? fileHint = null)
    {
        if (string.IsNullOrWhiteSpace(sym)) return (0, 0);
        var idx = Fresh(cfg);
        if (idx == null) return (0, 0);
        var q = sym.Trim();
        // 精确命中优先；否则取包含 q 的最短符号名（更接近用户意图）
        var key = idx.Defs.Keys.FirstOrDefault(k => k.Equals(q, StringComparison.OrdinalIgnoreCase))
               ?? idx.Defs.Keys.Where(k => k.Contains(q, StringComparison.OrdinalIgnoreCase))
                               .OrderBy(k => k.Length).FirstOrDefault();
        if (key == null || !idx.Defs.TryGetValue(key, out var defs) || defs.Count == 0) return (0, 0);
        var d = defs[0];
        if (fileHint != null && fileHint.Length > 0)
        {
            var hint = defs.FirstOrDefault(x =>
                Path.GetFileName(x.Rel).Equals(fileHint, StringComparison.OrdinalIgnoreCase));
            if (hint != null) d = hint;
        }
        return (d.Line, 0);   // end=0 由 SliceExtractor 自动扩块
    }

    /// <summary>供 MapSlice chain=1：返回符号的引用位置列表（相对路径+行号，最多 max 条），无则空列表</summary>
    public static List<(string rel, int line)> GetRefs(AppConfig cfg, string sym, int max = 6)
    {
        var list = new List<(string, int)>();
        if (string.IsNullOrWhiteSpace(sym)) return list;
        var idx = Fresh(cfg);
        if (idx == null) return list;
        var q = sym.Trim();
        var key = idx.Defs.Keys.FirstOrDefault(k => k.Equals(q, StringComparison.OrdinalIgnoreCase))
               ?? idx.Defs.Keys.Where(k => k.Contains(q, StringComparison.OrdinalIgnoreCase))
                               .OrderBy(k => k.Length).FirstOrDefault();
        if (key == null || !idx.Refs.TryGetValue(key, out var refs)) return list;
        foreach (var r in refs.Take(max)) list.Add((r.Rel, r.Line));
        return list;
    }

    // 新鲜度：写后由 IndexGuardian per-file 增量维护；Fresh 命中即返回，仅 30s 低频抽样兜底
    //（SnapAlive 已降为兜底路径，防钩子/watcher 漏报的外部修改）
    static DateTime LastCheckUtc = DateTime.MinValue;
    static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>取新鲜索引：同根缓存命中且距上次新鲜度检查不足 30s 时直接返回（零遍历）；
    /// 否则抽样检查（mtime+文件数），不新鲜则全量重建并落盘（锁内串行）</summary>
    static Index? Fresh(AppConfig cfg)
    {
        var root = cfg.ProjectRoot;
        if (root.Length == 0 || !Directory.Exists(root)) return null;
        lock (Sync)
        {
            if (cache != null && string.Equals(cache.Root, root, StringComparison.OrdinalIgnoreCase))
            {
                var now = DateTime.UtcNow;
                if (now - LastCheckUtc < CheckInterval) return cache;   // 高频命中：免遍历税
                LastCheckUtc = now;
                if (SnapAlive(root, cache.Snap)) return cache;          // 低频抽样兜底
                cache = Build(root);
            }
            else
            {
                LastCheckUtc = DateTime.UtcNow;
                // P1 懒加载：磁盘既有索引（含已落盘 refs）命中快照校验则直接复用（~百ms 级），
                // 磁盘缺失/损坏/版本不符/快照失效才走全量 Build（仅首次或"重建地图"）
                cache = Load(root);
                if (cache == null || !SnapAlive(root, cache.Snap)) cache = Build(root);
            }
            return cache;
        }
    }

    /// <summary>标签合规检查（"重建地图"按钮全量重建后调用）：统计缺文件头中文注释的代码文件与无中文注释的方法/类定义数。
    /// notes.json 中 LLM 补充说明同样计入合规：head 冲抵文件头缺口，methods 按条数冲抵无注释定义。</summary>
    public static string ComplianceReport(AppConfig cfg)
    {
        var idx = Fresh(cfg);
        if (idx == null) return "标签合规：符号索引不可用";
        var notes = LoadNotesNormalized(cfg);   // LLM 补充说明（.gairr/notes.json，键归一化为 '/' 分隔）

        var totalDefs = idx.Defs.Values.Sum(d => d.Count);
        // 无注释定义按文件分组，逐文件用 notes.Methods 条数冲抵（上限=该文件缺口数，methods 键仅为序号不定位）
        var noNoteByFile = idx.Defs.Values.SelectMany(d => d)
            .Where(x => x.Kind != "file" && x.Note.Length == 0)
            .GroupBy(x => x.Rel, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var defsCovered = 0;
        foreach (var kv in noNoteByFile)
        {
            if (!notes.TryGetValue(kv.Key, out var n) || n.Methods.Count == 0) continue;
            defsCovered += Math.Min(kv.Value, n.Methods.Count);
        }
        var noNoteDefs = noNoteByFile.Values.Sum() - defsCovered;

        var codeFiles = idx.Snap.Keys.Count(IsCodeFile);
        var noHead = new List<string>();
        foreach (var rel in idx.Snap.Keys.OrderBy(r => r, StringComparer.OrdinalIgnoreCase))
        {
            if (!IsCodeFile(rel)) continue;   // xaml/csproj 等非纯代码文件不要求中文注释头
            var hasHead = idx.Heads.TryGetValue(rel, out var h) && h.Length > 0;
            if (!hasHead && notes.TryGetValue(rel, out var n) && n.Head.Length > 0) hasHead = true;   // LLM 补充说明视为合规
            if (!hasHead) noHead.Add(rel);
        }
        var headInfo = noHead.Count == 0 ? "全部合规" :
            string.Join("、", noHead.Take(10)) + (noHead.Count > 10 ? $" 等 {noHead.Count} 个" : "");
        var coveredInfo = defsCovered > 0 ? $"（LLM 补充说明已冲抵 {defsCovered} 处）" : "";
        return $"标签合规：代码文件 {codeFiles} 个，缺文件头中文注释 {noHead.Count} 个（{headInfo}）；" +
               $"方法/类定义 {totalDefs} 处，无中文注释 {noNoteDefs} 处{coveredInfo}";
    }

    /// <summary>读取 .gairr/notes.json（LLM 补充说明）并把键归一化为 '/' 分隔，与符号索引 rel 口径一致。</summary>
    static Dictionary<string, NoteEntry> LoadNotesNormalized(AppConfig cfg)
    {
        var raw = ProjectMap.LoadNotes(Path.Combine(cfg.ProjectRoot, DirName, "notes.json"));
        var dict = new Dictionary<string, NoteEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in raw) dict[kv.Key.Replace('\\', '/')] = kv.Value;
        return dict;
    }

    /// <summary>是否纯代码文件（文件头注释规范适用的类型；xaml/csproj/json 等除外）</summary>
    static bool IsCodeFile(string rel)
    {
        var ext = Path.GetExtension(rel).ToLowerInvariant();
        return ext is ".cs" or ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".py" or ".go" or ".java" or ".kt"
            or ".dart" or ".rb" or ".php" or ".vue" or ".cpp" or ".h" or ".c";
    }

    /// <summary>符号级补注缺口清单：中文标签为空的非 file 符号（供 ProjectMapAuto 补注任务消费；与索引同一内存快照，零重建）。</summary>
    public static List<SymbolGap> ListNoteGaps(AppConfig cfg)
    {
        var idx = Fresh(cfg);
        var gaps = new List<SymbolGap>();
        if (idx == null) return gaps;
        foreach (var kv in idx.Defs)
            foreach (var d in kv.Value)
                if (d.Kind != "file" && d.Note.Length == 0)
                    gaps.Add(new SymbolGap { Rel = d.Rel, Name = kv.Key, Sig = d.Sig, Container = d.Container, Line = d.Line });
        return gaps;
    }

    /// <summary>符号级补注缺口条目：位置 + 签名 + 归属容器。</summary>
    public sealed class SymbolGap
    {
        public string Rel = "";
        public string Name = "";
        public string Sig = "";
        public string Container = "";
        public int Line;
    }

    /// <summary>强制重建符号索引（"重建地图"按钮用）：清空内存与磁盘缓存后全量构建，剔除已删符号与过时中文标签。</summary>
    public static void Refresh(AppConfig cfg)
    {
        var root = cfg.ProjectRoot;
        if (root.Length == 0 || !Directory.Exists(root)) return;
        lock (Sync)
        {
            cache = null;
            // 改名而非删除：Build 内 CleanupAfterBuild 需要读旧符号集做记忆库失效对比（读后清掉）
            try { File.Move(Path.Combine(root, DirName, IndexName), Path.Combine(root, DirName, IndexName + ".bak"), true); } catch { }
            cache = Build(root);
        }
    }

    /// <summary>快照新鲜度：只对快照自身的键比对 mtime（口径必须与 Build 一致）。
    /// 快照是"扩展名过滤 + 总量上限截断"后的子集（本机实测 794/20307），
    /// 用全项目文件数或全项目抽样去核对必然不等 —— 那会让每次距上次检查超 30s 的查询都触发全量重建。
    /// 新增/删除文件由 IndexGuardian（写后钩子 + FileSystemWatcher + 30s 文件数兜底）负责。</summary>
    static bool SnapAlive(string root, Dictionary<string, string> snap)
    {
        if (snap.Count == 0) return true;
        var step = Math.Max(1, snap.Count / 1000);   // 键多时均匀抽样：stat 次数上限约 1000
        var checkedCount = 0;
        var hit = 0;
        var i = 0;
        foreach (var kv in snap)
        {
            if (i++ % step != 0) continue;
            checkedCount++;
            try
            {
                var abs = Path.Combine(root, kv.Key.Replace('/', Path.DirectorySeparatorChar));
                var fi = new FileInfo(abs);
                if (fi.Exists && string.Equals(kv.Value, fi.LastWriteTimeUtc.ToString("o"), StringComparison.Ordinal))
                    hit++;
            }
            catch { }
        }
        return checkedCount == 0 || hit == checkedCount;   // 任一被收录文件改动/删除 → 判定需重建
    }

    /// <summary>单文件增量更新结果。</summary>
    public enum UpsertResult { Ok, Changed, RebuildRequested, Unavailable }

    /// <summary>单文件增量 upsert：移除该文件旧符号/头注/旧引用 → 重新解析该文件 →
    /// 只重算本文件的引用贡献（PropagateRefsInFile）→ Save。不再触发全量引用传播。
    /// 缓存未就绪时返回 RebuildRequested（交由检查官/低频兜底走全量构建）；
    /// 符号量已达上限时同样返回 RebuildRequested。</summary>
    public static UpsertResult UpsertFile(string rel)
    {
        var relN = rel.Replace('\\', '/');
        lock (Sync)
        {
            if (cache == null) { /* 由检查官侧先 Refresh 兜底，这里返回 RebuildRequested */ return UpsertResult.RebuildRequested; }
            var root = cache.Root;
            if (cache.Defs.Count >= MaxSymbols) return UpsertResult.RebuildRequested;
            RemoveFileFromIndex(cache, relN);
            var abs = Path.Combine(root, relN.Replace('/', Path.DirectorySeparatorChar));
            var ext = Path.GetExtension(relN).ToLowerInvariant();
            if (!CodeExts.Contains(ext) || IsAutoRel(relN))
            {
                UpdateSnap(cache, relN, abs);
                PropagateRefsInFile(cache, root, relN);   // 非代码文件也贡献引用（与全量口径一致）
                return UpsertResult.Changed;
            }
            try
            {
                var fi = new FileInfo(abs);
                if (!fi.Exists || fi.Length == 0 || fi.Length > MaxScanBytes)
                {
                    UpdateSnap(cache, relN, abs);
                    PropagateRefsInFile(cache, root, relN);
                    return UpsertResult.Changed;
                }
                var (text, _) = Phase1Tools.Decode(File.ReadAllBytes(abs));
                if (text.IndexOf('\0') >= 0) return UpsertResult.Unavailable;   // 二进制
                cache.Snap[relN] = fi.LastWriteTimeUtc.ToString("o");
                IndexFile(cache, relN, text);
                BuildGrams(cache);      // 注释 2-gram 倒排同步刷新（Save 只写盘，倒排须显式重建）
                BuildSymGrams(cache);   // 符号名 2-gram 倒排同步刷新
                PropagateRefsInFile(cache, root, relN);   // 只重算本文件引用贡献（原为全量重扫全部文件）
                Save(root, cache);      // 落盘（全量传播已不在 Save 内）
                return UpsertResult.Changed;
            }
            catch { return UpsertResult.Unavailable; }
        }
    }

    /// <summary>移除某文件的符号/头注/Snap 条目（文件删除或 upsert 前调用）。</summary>
    public static void RemoveFile(string rel)
    {
        var relN = rel.Replace('\\', '/');
        lock (Sync)
        {
            if (cache == null) return;
            RemoveFileFromIndex(cache, relN);
            try { Save(cache.Root, cache); } catch { }
        }
    }

    static void RemoveFileFromIndex(Index idx, string relN)
    {
        idx.Snap.Remove(relN);
        idx.Heads.Remove(relN);
        foreach (var kv in idx.Defs)
        {
            var list = kv.Value;
            for (var i = list.Count - 1; i >= 0; i--)
                if (list[i].Rel.Equals(relN, StringComparison.OrdinalIgnoreCase)) list.RemoveAt(i);
        }
        foreach (var kv in idx.Refs)
        {
            var refs = kv.Value;
            for (var i = refs.Count - 1; i >= 0; i--)
                if (refs[i].Rel.Equals(relN, StringComparison.OrdinalIgnoreCase)) refs.RemoveAt(i);
        }
        foreach (var name in idx.Defs.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList())
            idx.Defs.Remove(name);
    }

    static void UpdateSnap(Index idx, string relN, string abs)
    {
        try { if (File.Exists(abs)) idx.Snap[relN] = File.GetLastWriteTimeUtc(abs).ToString("o"); }
        catch { }
    }

    /// <summary>从磁盘加载既有索引（P1 冷启动懒加载）：恢复 Defs/Refs/Snap 与派生倒排，
    /// 不跑全量引用传播（Refs 已由 Save 落盘，直接复用）。磁盘缺失/损坏/版本不符返回 null。
    /// Heads（文件头注释）Save 未单独持久化，但 file 符号的 note 即文件头注释，据此恢复。</summary>
    static Index? Load(string root)
    {
        try
        {
            var p = Path.Combine(root, DirName, IndexName);
            if (!File.Exists(p)) return null;
            if (JsonNode.Parse(File.ReadAllText(p)) is not JsonObject obj) return null;
            if (obj["ver"]?.GetValue<int>() != 3) return null;
            var idx = new Index { Root = root };
            if (obj["files"] is JsonObject files)
                foreach (var kv in files)
                {
                    var v = kv.Value?.GetValue<string>();
                    if (!string.IsNullOrEmpty(v)) idx.Snap[kv.Key] = v;
                }
            if (obj["symbols"] is JsonObject syms)
                foreach (var kv in syms)
                {
                    if (kv.Value is not JsonObject so) continue;
                    var defs = new List<Def>();
                    if (so["def"] is JsonArray da)
                        foreach (var e in da)
                            if (e is JsonObject o && !string.IsNullOrEmpty(o["rel"]?.GetValue<string>()))
                                defs.Add(new Def
                                {
                                    Rel = o["rel"]!.GetValue<string>(),
                                    Line = o["line"]?.GetValue<int>() ?? 0,
                                    Kind = o["kind"]?.GetValue<string>() ?? "",
                                    Ns = o["ns"]?.GetValue<string>() ?? "",
                                    Container = o["container"]?.GetValue<string>() ?? "",
                                    Sig = o["sig"]?.GetValue<string>() ?? "",
                                    Note = o["note"]?.GetValue<string>() ?? "",
                                });
                    if (defs.Count > 0) idx.Defs[kv.Key] = defs;
                    var refs = new List<RefPos>();
                    if (so["ref"] is JsonArray ra)
                        foreach (var e in ra)
                            if (e is JsonObject o && !string.IsNullOrEmpty(o["rel"]?.GetValue<string>()))
                                refs.Add(new RefPos { Rel = o["rel"]!.GetValue<string>(), Line = o["line"]?.GetValue<int>() ?? 0 });
                    if (refs.Count > 0) idx.Refs[kv.Key] = refs;
                }
            if (idx.Snap.Count == 0 || idx.Defs.Count == 0) return null;
            foreach (var kv in idx.Defs)
                foreach (var d in kv.Value)
                    if (d.Kind == "file") idx.Heads[d.Rel] = d.Note;   // 头注释从 file 符号恢复
            BuildGrams(idx);
            BuildSymGrams(idx);
            return idx;
        }
        catch { return null; }
    }

    static Index Build(string root)
    {
        var idx = new Index { Root = root };
        long total = 0;

        foreach (var f in Phase1Tools.EnumerateFiles(root))
        {
            if (idx.Snap.Count >= MaxFiles || total >= MaxTotalBytes) break;
            var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
            if (IsAutoRel(rel)) continue;
            var ext = Path.GetExtension(rel).ToLowerInvariant();
            if (!CodeExts.Contains(ext)) continue;
            try
            {
                var fi = new FileInfo(f);
                if (fi.Length > MaxScanBytes || fi.Length == 0) continue;
                var (text, _) = Phase1Tools.Decode(File.ReadAllBytes(f));
                if (text.IndexOf('\0') >= 0) continue;   // 二进制
                total += text.Length;
                idx.Snap[rel] = fi.LastWriteTimeUtc.ToString("o");
                IndexFile(idx, rel, text);
                if (idx.Defs.Count >= MaxSymbols) break;
            }
            catch { }
        }
        CleanupAfterBuild(root, idx);   // 记忆库失效清理（须在 Save 覆盖旧索引前读旧符号集）
        MergeSymbolNotes(root, idx);    // 合并符号级补注（.gairr/symbol-notes.json），源码 note 优先
        BuildGrams(idx);                // 注释 2-gram 倒排：中文查询从全量线性扫描降为倒排查表
        BuildSymGrams(idx);             // 符号名 2-gram 倒排：符号名 Contains 查询走倒排
        Save(root, idx, fullRefs: true); // 全量重建是唯一需要全量引用传播的入口（增量路径走 PropagateRefsInFile）
        return idx;
    }

    /// <summary>合并符号级补注：key="rel::name"，仅填当前为空的中文标签（源码注释不受影响）。</summary>
    static void MergeSymbolNotes(string root, Index idx)
    {
        try
        {
            var p = Path.Combine(root, DirName, "symbol-notes.json");
            if (!File.Exists(p)) return;
            if (JsonNode.Parse(File.ReadAllText(p)) is not JsonObject obj) return;
            foreach (var kv in obj)
            {
                var sep = kv.Key.IndexOf("::", StringComparison.Ordinal);
                if (sep <= 0 || kv.Value == null) continue;
                var rel = kv.Key[..sep];
                var name = kv.Key[(sep + 2)..];
                if (!idx.Defs.TryGetValue(name, out var defs)) continue;
                var note = kv.Value.GetValue<string>();
                if (note.Length == 0) continue;
                foreach (var d in defs)
                    if (string.Equals(d.Rel, rel, StringComparison.OrdinalIgnoreCase) && d.Note.Length == 0)
                        d.Note = note.Length > 200 ? note[..200] : note;
            }
        }
        catch { /* 补注文件损坏不影响索引构建 */ }
    }

    /// <summary>重建后记忆库清理钩子：比对新旧符号集，删除/改名/文件删除 →
    /// TagRefAccumulator.ValidateAndClean 把积累库（tag-refs.jsonl）中的失效关联标记为软删并重定位漂移行号。</summary>
    static void CleanupAfterBuild(string root, Index idx)
    {
        try
        {
            var oldPath = Path.Combine(root, DirName, IndexName);
            if (!File.Exists(oldPath) && File.Exists(oldPath + ".bak"))
            {
                File.Move(oldPath + ".bak", oldPath, true);   // Refresh 改名留的旧索引，读回后继续常规流程
            }
            if (!File.Exists(oldPath)) return;   // 首次构建无旧索引，无清理对象
            var oldKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (JsonNode.Parse(File.ReadAllText(oldPath)) is JsonObject old && old["symbols"] is JsonObject syms)
                foreach (var kv in syms) oldKeys.Add(kv.Key);
            try { File.Delete(oldPath + ".bak"); } catch { }
            var gone = oldKeys.Where(k => !idx.Defs.ContainsKey(k)).ToList();
            // 不做 gone 判空提前返回：行号重定位在“无符号删除”的重建（如插入/删除注释行）中也必须执行，
            // ValidateAndClean 无记录时零成本返回，有记录时自行校验与重定位
            // 存活符号位置表（rel → symbol → line），行号重定位用
            var live = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in idx.Defs)
                foreach (var d in kv.Value)
                {
                    if (d.Kind == "file") continue;
                    if (!live.TryGetValue(d.Rel, out var m)) live[d.Rel] = m = new(StringComparer.OrdinalIgnoreCase);
                    if (!m.ContainsKey(kv.Key)) m[kv.Key] = d.Line;
                }
            var files = new HashSet<string>(idx.Snap.Keys, StringComparer.OrdinalIgnoreCase);
            TagRefAccumulator.ValidateAndClean(root, gone, live, files);
        }
        catch { /* 清理失败不影响索引本身 */ }
    }

    static void IndexFile(Index idx, string rel, string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var limit = Math.Min(lines.Length, 4000);   // 超大文件只索引前 4000 行
        // 文件名主干本身是一个符号（kind=file），Note=文件头中文注释（文件级中文标签）
        idx.Heads[rel] = ExtractHeadNote(lines);   // 文件头注释独立记录（file 符号可能被同名类顶替，标签不能丢）
        AddDef(idx, Path.GetFileNameWithoutExtension(rel), rel, 1, "file", note: idx.Heads[rel]);
        // Vue SFC：三段式专用解析（template 组件/文案 + script setup/选项式），不进通用逐行扫描
        if (rel.EndsWith(".vue", StringComparison.OrdinalIgnoreCase))
        {
            IndexVueFile(idx, rel, text);
            return;
        }
        // 上下文跟踪：命名空间与当前类/接口容器，供方法/属性定义标注归属
        var curNs = "";
        var curClass = "";
        var pendingNote = "";   // 声明上方连续注释行（中文标签来源）
        var javaLike = rel.EndsWith(".java", StringComparison.OrdinalIgnoreCase)
                    || rel.EndsWith(".kt", StringComparison.OrdinalIgnoreCase)
                    || rel.EndsWith(".dart", StringComparison.OrdinalIgnoreCase);
        var classAnns = new List<string>();   // 最近注解名（类声明时消费：lombok 判定）
        var lombok = false;                   // 当前类启用了 lombok（字段 → get/set 伪符号）
        var classPath = "";                   // 类级 @RequestMapping 前缀（方法级路径拼接）
        var pendingEndpoint = "";             // 注解上的 HTTP 路径（方法声明时消费）
        for (var i = 0; i < limit; i++)
        {
            var line = NormalizeTuple(lines[i]);
            var t = line.Trim();
            if (t.Length == 0) { continue; }   // 空行不清空注释：允许注释与声明间隔空行
            if (ProjectMap.IsCommentLine(t))
            {
                // 累积注释文字（去前缀/XML 标签）；只保留含中文的段落作标签，控制体积
                var c = ProjectMap.StripComment(t);
                if (c.Length > 0 && ContainsCjk(c))
                    pendingNote = pendingNote.Length == 0 ? c : pendingNote + "；" + c;
                if (pendingNote.Length > 120) pendingNote = pendingNote[..120];
                continue;
            }
            if (t[0] == '-' || t[0] == '[') { pendingNote = ""; continue; }
            // 注解行（Java/Kotlin/Dart）：不打断声明上方注释积累——注解常夹在注释与方法之间，
            // 否则 "/** 登录接口 */ @PostMapping public ..." 的中文标签被注解行吃掉
            if (javaLike && t.StartsWith('@'))
            {
                var am = AnnotRe.Match(t);
                if (am.Success)
                {
                    classAnns.Add(am.Groups[1].Value);
                    if (classAnns.Count > 4) classAnns.RemoveAt(0);
                }
                var ep = EndpointRe.Match(t);
                if (ep.Success && pendingEndpoint.Length == 0)
                    pendingEndpoint = ep.Groups[1].Value;   // 路径已含前导 /
                continue;
            }
            // 泛型方法头归一化：`<T> T foo(` → `T foo(`（泛型方法原本 MethodRe 不命中）
            var gw = GenericMethodRe.Match(line);
            if (gw.Success && MethodLikeRe.IsMatch(line[gw.Length..]))
                line = line[gw.Length..];
            var ns = NsRe.Match(line);
            if (ns.Success) { curNs = ns.Groups[1].Value; pendingNote = ""; continue; }
            var cm = ClassSymRe.Match(line);
            if (cm.Success)
            {
                curClass = cm.Groups[2].Value;
                lombok = javaLike && classAnns.Any(a => LombokAnns.Contains(a));
                classPath = javaLike ? pendingEndpoint : "";   // 类级映射前缀（方法级路径拼接用）
                pendingEndpoint = "";
                classAnns.Clear();
                AddDef(idx, curClass, rel, i + 1, cm.Groups[1].Value, curNs, "", line, pendingNote);
                pendingNote = "";
                continue;
            }
            var pm = PropRe.Match(line);
            if (pm.Success && !CtrlWords.Contains(pm.Groups[1].Value))
            {
                AddDef(idx, pm.Groups[1].Value, rel, i + 1, "property", curNs, curClass, line, pendingNote);
                pendingNote = "";
                continue;
            }
            var mm = MethodRe.Match(line);
            if (mm.Success && !CtrlWords.Contains(mm.Groups[1].Value))
            {
                AddDef(idx, mm.Groups[1].Value, rel, i + 1, "method", curNs, curClass, line, pendingNote);
                // 注解上的 HTTP 路径 → endpoint 符号（用户可搜 "user/list" 定位接口方法）
                if (pendingEndpoint.Length > 0)
                {
                    var full = classPath.Length > 0
                        ? classPath.TrimEnd('/') + "/" + pendingEndpoint.TrimStart('/')
                        : pendingEndpoint;
                    AddDef(idx, full, rel, i + 1, "endpoint", curNs, curClass, line, "HTTP " + pendingEndpoint);
                    pendingEndpoint = "";
                }
                pendingNote = "";
                continue;
            }
            // lombok 类字段 → get/set 伪符号（源码不存在的生成代码，编译期产出）
            if (lombok && curClass.Length > 0)
            {
                var fm = FieldRe.Match(line);
                if (fm.Success && !CtrlWords.Contains(fm.Groups[1].Value))
                {
                    var cap = char.ToUpperInvariant(fm.Groups[1].Value[0]) + fm.Groups[1].Value[1..];
                    AddDef(idx, "get" + cap, rel, i + 1, "lombok", curNs, curClass, line, pendingNote);
                    AddDef(idx, "set" + cap, rel, i + 1, "lombok", curNs, curClass, line, pendingNote);
                    pendingNote = "";
                    continue;
                }
            }
            pendingNote = "";   // 普通代码行：前方注释失效
        }
    }

    /// <summary>Vue SFC 三段式索引：<template> 组件标签+中文文案（界面元素入索引）、
    /// <script setup> 顶层声明、选项式 export default 的 methods/computed/data。
    /// 用户能说出口的按钮文案/组件名由此可搜（"保存"→保存按钮所在组件）。</summary>
    static void IndexVueFile(Index idx, string rel, string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var limit = Math.Min(lines.Length, 4000);
        var comp = Path.GetFileNameWithoutExtension(rel);
        var (ts, te) = BlockRange(lines, "<template", "</template>");
        var (ss, se) = BlockRange(lines, "<script", "</script>");

        // ---- template：组件标签 + 行内中文文案 ----
        for (var i = ts; ts >= 0 && i <= te && i < limit; i++)
        {
            var t = lines[i].Trim();
            if (t.Length == 0) continue;
            foreach (Match m in VueCompTagRe.Matches(t))
            {
                var tag = m.Groups[1].Value;
                if (HtmlTags.Contains(tag) || tag.StartsWith("v-", StringComparison.Ordinal)) continue;
                AddDef(idx, tag, rel, i + 1, "vue-comp", "", comp, lines[i].Trim(),
                       CollectCjkNote(lines[i], 60));   // 按钮/标签中文文案作中文标签
                break;   // 一行只录首个组件：同文件同名符号互顶，重复收录无意义（引用覆盖靠 PropagateRefs）
            }
        }

        // ---- script 区 ----
        if (ss < 0) return;
        if (lines[ss].Contains("setup", StringComparison.OrdinalIgnoreCase))
            IndexVueSetup(idx, rel, comp, lines, ss, se, limit);
        else
            IndexVueOptions(idx, rel, comp, lines, ss, se, limit);
    }

    /// <summary><script setup> 顶层声明：const fn = () => / function foo / const count = ref(0)</summary>
    static void IndexVueSetup(Index idx, string rel, string comp, string[] lines, int ss, int se, int limit)
    {
        for (var i = ss + 1; i <= se && i < limit; i++)
        {
            var t = lines[i].Trim();
            if (t.Length == 0 || t.StartsWith("//")) continue;
            var fm = Regex.Match(t, @"^(?:export\s+)?(?:async\s+)?function\s+([A-Za-z_]\w*)\s*\(");
            if (fm.Success)
            {
                AddDef(idx, fm.Groups[1].Value, rel, i + 1, "method", "", comp, lines[i].Trim(), "");
                continue;
            }
            var cm = Regex.Match(t, @"^(?:export\s+)?(?:const|let|var)\s+([A-Za-z_]\w*)\s*=\s*(.*)$");
            if (!cm.Success) continue;
            var rhs = cm.Groups[2].Value;
            var kind = "const";
            if (rhs.Contains("=>") || rhs.StartsWith("function") ||
                Regex.IsMatch(rhs, @"^\w+\s*\(")) kind = "method";
            else if (Regex.IsMatch(rhs, @"^(ref|shallowRef|computed|reactive|watch|watchEffect)\s*\(")) kind = "property";
            AddDef(idx, cm.Groups[1].Value, rel, i + 1, kind, "", comp, lines[i].Trim(), "");
        }
    }

    /// <summary>选项式 script：括号平衡扫描 export default { methods/computed/watch/data } 段内键名。
    /// 段头识别用已知段名集合并放宽深度——单行 data() { return {...} } 会把深度推高，严格深度会漏段头。</summary>
    static void IndexVueOptions(Index idx, string rel, string comp, string[] lines, int ss, int se, int limit)
    {
        var depth = 0;          // 花括号深度（引号内不算）
        var inOptions = false;  // 已进入 export default { ... }
        var section = "";       // 当前段名（methods/computed/watch/data）
        for (var i = ss + 1; i <= se && i < limit; i++)
        {
            var depthAtStart = depth;
            var inStr = '\0';
            for (var k = 0; k < lines[i].Length; k++)
            {
                var ch = lines[i][k];
                if (inStr != '\0')
                {
                    if (ch == inStr && (k == 0 || lines[i][k - 1] != '\\')) inStr = '\0';
                    continue;
                }
                if (ch == '\'' || ch == '"' || ch == '`') inStr = ch;
                else if (ch == '{') depth++;
                else if (ch == '}') depth--;
            }
            if (!inOptions)
            {
                if (depth > 0 && (lines[i].Contains("export default") || lines[i].Contains("defineComponent")))
                    inOptions = true;
                continue;
            }
            var t = lines[i].Trim();
            if (t.Length == 0) continue;
            // 段头：已知段名（methods/computed/watch/data…），深度放宽防单行 data() 推高深度漏识别
            if (depthAtStart <= 4)
            {
                var ks = Regex.Match(t, @"^([A-Za-z_]\w*)\s*[:\()]");
                if (ks.Success && IsVueOptionSection(ks.Groups[1].Value)
                    && !Regex.IsMatch(t, @"^[A-Za-z_]\w*\s*:\s*[""'\[0-9]"))  // 排除值不是对象的键
                {
                    section = ks.Groups[1].Value;
                }
            }
            if (depthAtStart == 2 && section.Length > 0 && t.Length > 0)
            {
                if (section is "methods" or "computed" or "watch")
                {
                    var ms = Regex.Match(t, @"^([A-Za-z_]\w*)\s*\(");
                    if (ms.Success && !CtrlWords.Contains(ms.Groups[1].Value))
                        AddDef(idx, ms.Groups[1].Value, rel, i + 1, "method", "", comp, lines[i].Trim(), "");
                }
                else if (section == "data")
                {
                    var fs = Regex.Match(t, @"^([A-Za-z_]\w*)\s*:\s*(?!using|function)\s*[""'\[0-9]");
                    var ms2 = Regex.Match(t, @"^([A-Za-z_]\w*)\s*\(");
                    if (fs.Success && !ms2.Success && !CtrlWords.Contains(fs.Groups[1].Value))
                        AddDef(idx, fs.Groups[1].Value, rel, i + 1, "property", "", comp, lines[i].Trim(), "");
                }
            }
        }
    }

    /// <summary>选项式 API 已知段名（仅这些键切换 section，避免嵌套对象同名键误切）</summary>
    static bool IsVueOptionSection(string key) => key switch
    {
        "methods" or "computed" or "watch" or "data" or "setup" or "props" or "emits"
            or "inject" or "provide" or "created" or "mounted" or "beforeMount" or "updated"
            or "beforeDestroy" or "destroyed" or "beforeUnmount" or "unmounted" => true,
        _ => false,
    };

    /// <summary>按开/闭标签找块区间（未找到返回 -1）</summary>
    static (int start, int end) BlockRange(string[] lines, string openTag, string closeTag)
    {
        var start = -1; var end = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (start < 0 && lines[i].Contains(openTag, StringComparison.OrdinalIgnoreCase)) { start = i; continue; }
            if (start >= 0 && lines[i].Contains(closeTag, StringComparison.OrdinalIgnoreCase)) { end = i; break; }
        }
        return (start, end);
    }

    /// <summary>提取行内连续中文文案（去重拼接，最多 3 段、maxLen 字截断）</summary>
    static string CollectCjkNote(string line, int maxLen)
    {
        var parts = new List<string>();
        foreach (Match m in CjkRunRe.Matches(line))
        {
            var s = m.Value.Trim();
            if (s.Length >= 2 && !parts.Contains(s)) parts.Add(s);
            if (parts.Count >= 3) break;
        }
        if (parts.Count == 0) return "";
        var joined = string.Join("、", parts);
        return joined.Length > maxLen ? joined[..maxLen] : joined;
    }

    /// <summary>元组类型归一化：把返回类型位置的 (…)/List<(…)> 替换为 Tuple，让 MethodRe/PropRe 能命中元组返回类型的方法；
    /// 只替换后紧跟 > 或标识符的括号组（方法参数列表后接 { / 行尾，不受影响），嵌套元组替换两遍</summary>
    static string NormalizeTuple(string line)
    {
        if (line.IndexOf('(') < 0) return line;
        var s = Regex.Replace(line, @"\([^()]*\)(?=\s*(?:>|[A-Za-z_]))", "Tuple");
        s = Regex.Replace(s, @"\([^()]*\)(?=\s*(?:>|[A-Za-z_]))", "Tuple");
        return s;
    }

    /// <summary>文件头中文注释提取：前 12 行内的连续注释块（与 ProjectMap 文件头口径一致）</summary>
    static string ExtractHeadNote(string[] lines)
    {
        var head = new StringBuilder();
        for (var i = 0; i < Math.Min(12, lines.Length); i++)
        {
            var t = lines[i].Trim();
            if (t.Length == 0 && head.Length == 0) continue;
            if (ProjectMap.IsCommentLine(t))
            {
                var c = ProjectMap.StripComment(t);
                if (c.Length > 0 && ContainsCjk(c)) head.Append(c).Append(' ');
            }
            else if (head.Length > 0) break;
        }
        var s = head.ToString().Trim();
        return s.Length > 120 ? s[..120] : s;
    }

    /// <summary>是否含中日韩字符（只收录含中文的注释，纯英文注释不进标签索引）</summary>
    static bool ContainsCjk(string s) => s.Any(ch => ch >= 0x4E00 && ch <= 0x9FFF);

    static void AddDef(Index idx, string name, string rel, int line, string kind,
        string ns = "", string container = "", string srcLine = "", string note = "")
    {
        if (name.Length < 2 || (!char.IsLetter(name[0]) && name[0] != '/')) return;   // endpoint 路径以 / 开头
        if (!idx.Defs.TryGetValue(name, out var list)) idx.Defs[name] = list = new List<Def>();
        // 同文件同符号只保留一处：代码符号（class/方法）优先，文件主干符号（file）让位
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (!list[i].Rel.Equals(rel, StringComparison.OrdinalIgnoreCase)) continue;
            if (kind == "file" && list[i].Kind != "file") return;
            list.RemoveAt(i);
        }
        var sig = "";
        if (srcLine.Length > 0)
            sig = Regex.Replace(srcLine.Trim(), @"\s{2,}", " ");
        var d = new Def { Rel = rel, Line = line, Kind = kind, Ns = ns, Container = container, Note = note };
        d.Sig = sig.Length > 90 ? sig[..90] : sig;
        list.Add(d);
    }

    /// <summary>收录一处引用：定义在本文件内的符号不算引用（排除自身）；
    /// 同一文件同一符号最多 MaxRefPerFile 处（防样板/生成代码刷屏）；单符号引用位置总数上限 MaxRefPos。</summary>
    static void AddRef(Index idx, string sym, string rel, int line)
    {
        if (!idx.Defs.TryGetValue(sym, out var defs) || defs.Count == 0) return;
        if (!idx.Refs.TryGetValue(sym, out var refs)) idx.Refs[sym] = refs = new List<RefPos>();
        if (refs.Count >= MaxRefPos) return;
        if (defs.All(d => !d.Rel.Equals(rel, StringComparison.OrdinalIgnoreCase)))
        {
            var perFile = 0;
            for (var i = refs.Count - 1; i >= 0; i--)
                if (refs[i].Rel.Equals(rel, StringComparison.OrdinalIgnoreCase)) perFile++;
            if (perFile < MaxRefPerFile) refs.Add(new RefPos { Rel = rel, Line = line });
        }
    }

    /// <summary>扫描单个文件的引用贡献（逐行匹配、记录行号）。读取失败/二进制文件直接跳过。</summary>
    static void ScanRefsInFile(Index idx, string root, string rel, SymbolWordMatcher matcher)
    {
        string text;
        try
        {
            var (t, _) = Phase1Tools.Decode(File.ReadAllBytes(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar))));
            if (t.IndexOf('\0') >= 0) return;   // 二进制
            text = t;
        }
        catch { return; }
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var ln = 0; ln < lines.Length; ln++)
        {
            var no = ln + 1;
            matcher.Match(lines[ln], sym => AddRef(idx, sym, rel, no));
        }
    }

    /// <summary>引用传播（全量一趟）：定义提取完成后，全文词匹配符号 → 引用位置集（排除自身，带行号）。
    /// 入口先清空 Refs 全量重算（防跨调用重复累加）。只在全量 Build 时执行：
    /// 单文件增量的引用贡献由 PropagateRefsInFile 局部维护，不再每次都付这趟"读全部文件 + 全符号大正则"的税。</summary>
    static void PropagateRefs(Index idx, string root)
    {
        if (idx.Defs.Count == 0) return;
        idx.Refs.Clear();
        try
        {
            var matcher = new SymbolWordMatcher(idx.Defs.Keys, MaxSymbols);
            foreach (var kv in idx.Snap) ScanRefsInFile(idx, root, kv.Key, matcher);
        }
        catch { /* 引用传播失败不影响索引主体 */ }
    }

    /// <summary>单文件引用贡献重算（增量路径专用）：调用方须先移除该文件旧符号与旧引用，
    /// 本方法只读这一个文件全文匹配当前符号集 —— 成本从"全部收录文件"降到 1 个文件。</summary>
    static void PropagateRefsInFile(Index idx, string root, string rel)
    {
        if (idx.Defs.Count == 0) return;
        try { ScanRefsInFile(idx, root, rel, new SymbolWordMatcher(idx.Defs.Keys, MaxSymbols)); } catch { }
    }

    /// <summary>落盘索引。fullRefs=true 时才做全量引用传播（仅全量 Build 用）：
    /// 增量路径（单文件 upsert/删除）的引用贡献已由 PropagateRefsInFile 局部维护，
    /// 不再每次落盘都重扫全部收录文件跑全符号大正则。</summary>
    static void Save(string root, Index idx, bool fullRefs = false)
    {
        try
        {
            // 全量引用传播需要全文第二趟，一次几百个文件，只在重建时做
            if (fullRefs) PropagateRefs(idx, root);
            Directory.CreateDirectory(Path.Combine(root, DirName));
            var symObj = new JsonObject();
            foreach (var kv in idx.Defs.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                var defs = new JsonArray();
                foreach (var d in kv.Value)
                    defs.Add(new JsonObject
                    {
                        ["rel"] = d.Rel, ["line"] = d.Line, ["kind"] = d.Kind,
                        ["ns"] = d.Ns, ["container"] = d.Container, ["sig"] = d.Sig,
                        ["note"] = d.Note,
                    });
                var refs = new JsonArray();
                if (idx.Refs.TryGetValue(kv.Key, out var rl))
                    foreach (var r in rl.Take(MaxRefPos))
                        refs.Add(new JsonObject { ["rel"] = r.Rel, ["line"] = r.Line });
                symObj[kv.Key] = new JsonObject { ["def"] = defs, ["ref"] = refs };
            }
            var snapObj = new JsonObject();
            foreach (var kv in idx.Snap)
                snapObj[kv.Key] = kv.Value;
            var obj = new JsonObject
            {
                ["ver"] = 3,
                ["built"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["files"] = snapObj,
                ["symbols"] = symObj,
            };
            File.WriteAllText(Path.Combine(root, DirName, IndexName), obj.ToJsonString(JsonOpts), new UTF8Encoding(false));
        }
        catch { /* 索引写盘失败不影响本次检索 */ }
    }

    static bool IsAutoRel(string rel) =>
        rel.StartsWith(".gairr/", StringComparison.OrdinalIgnoreCase);
}