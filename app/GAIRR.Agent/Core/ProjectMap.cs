using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GAIRR.Core;

/// <summary>LLM 补充说明条目：head=文件职责，methods=无注释方法补充描述（key 为 1 起索引）。
/// 项目地图自动化（ProjectMapAuto）生成，渲染时合并进 .gairr/map.json 的 Extra。</summary>
public sealed class NoteEntry
{
    public string Head = "";
    public Dictionary<string, string> Methods = new();
}

/// <summary>项目地图：扫描项目生成"目录树 + 每文件功能摘要"，帮模型第一轮就了解全貌、精准定位。
/// 摘要自动提取（文件头注释 + 类声明 + 方法签名与其上方注释）；.gairr/map.json 缓存增量更新，
/// 手工编辑补充语义描述（manual 条目永不覆盖）。</summary>
public static class ProjectMap
{
    const string CacheName = ".gairr/map.json";
    const int MaxChars = 8000;            // 输出总长上限
    const int MaxSummaryChars = 900;      // 单文件摘要上限（文件头+类+方法清单）
    const int MaxMembers = 15;            // 单文件最多列出方法条数（超出折叠提示）
    const int MaxClasses = 5;             // 单文件最多列出类/接口条数
    const int MaxScanLines = 1500;        // 摘要提取最多扫描行数
    const int SigLen = 36;                // 声明签名截断长度
    const int CommentLen = 60;            // 方法注释截断长度
    const int HeadLen = 150;              // 文件头注释截断长度
    const int PlainFold = 10;             // 目录内无摘要文件超过此数折叠为一行

    static readonly object Sync = new();
    // .NET 8 JsonNode 序列化要求显式 TypeInfoResolver（否则写盘抛 InvalidOperationException）
    static readonly System.Text.Json.JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };
    // 备份/旧版痕迹文件（.bak/.bak2/_260816_xxx 等）：只列名不生成摘要，省预算
    static readonly Regex BackupRe = new(@"\.bak\d*$|_\d{6}_", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    // 方法声明：修饰符* 类型* 方法名(参数) 后跟 { / => / where / 行尾 / :（Python def）/ ;（接口方法）（C#/Java/TS/Go/Python 通用形态）
    static readonly Regex MethodRe = new(
        @"^\s*(?:(?:public|private|protected|internal|static|virtual|override|sealed|partial|abstract|extern|readonly|async|export|declare|final|default|unsafe|volatile)\s+)*(?:[\w<>\[\]\.\?]+(?:\s*\[\s*\])?\s+)*([A-Za-z_][A-Za-z0-9_]*)\s*\([^;{}]*\)(?:\s*\{|\s*=>|\s*$|\s*where\b|\s*:\s*(?:#.*)?$|\s*;\s*(?://.*)?$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // 类/接口/枚举/结构/记录声明
    static readonly Regex ClassRe = new(
        @"\b(class|interface|enum|struct|record)\s+([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // XML 文档标签（<summary>/<param>/<returns>/<c>...）只保留文字；<!-- --> 普通注释除外
    static readonly Regex XmlTagRe = new("<(?!!--)[^>]+>", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // 控制流等非方法行首词（避免把 if/for/return new/lock/print(...) 等当方法）
    static readonly string[] CtrlWords =
    {
        "if", "for", "foreach", "while", "switch", "catch", "using", "return", "new", "typeof",
        "nameof", "lock", "case", "when", "else", "elif", "try", "do", "throw", "yield", "await",
        "var", "let", "const", "with", "except", "import", "from", "print", "assert", "del",
        "global", "nonlocal", "base", "this", "synchronized",
    };

    sealed class Entry
    {
        public string Mtime = "";
        public string Summary = "";
        public bool Manual;
        public bool NoHead;          // 无文件头注释（供自动化补注释）
        public int NoComment;        // 无注释方法数（供自动化补注释）
        public string Extra = "";    // 附加渲染文本（LLM 补充说明 + 调用关系）
    }

    sealed class Node
    {
        public string Name = "";
        public bool Dir;
        public string? Summary;
        public string? Extra;
        public readonly List<Node> Kids = new();
    }

    /// <summary>清除地图缓存文件（“重建地图”按钮全量重建前调用）。manual 手工条目一并清除——当前无 UI 录入途径，用户不会手工编辑。</summary>
    public static void ClearCache(AppConfig cfg)
    {
        var root = cfg.ProjectRoot;
        if (root.Length == 0) return;
        lock (Sync)
        {
            try { File.Delete(Path.Combine(root, CacheName)); } catch { }
            RenderCaches.Clear();   // 渲染缓存同步失效，避免重建后仍返回旧文本
        }
    }

    // 渲染结果进程级缓存：Build 每次都要全树枚举 + 解析 map.json（约 2MB）+ 渲染长文本，
    // 而语义检索的摘要层每查一次就付一次；按 (root|focus) 缓存，map.json 被改写或超 TTL 才重建
    sealed class RenderCache { public DateTime At; public string Mtime = ""; public string Text = ""; }
    static readonly Dictionary<string, RenderCache> RenderCaches = new();
    static readonly TimeSpan RenderTtl = TimeSpan.FromSeconds(15);

    /// <summary>生成/读取项目地图文本。focus 为相对子目录（可选）；refresh=true 强制重建自动摘要。</summary>
    public static string Build(AppConfig cfg, string? focus = null, bool refresh = false)
    {
        var root = cfg.ProjectRoot;
        if (root.Length == 0 || !Directory.Exists(root)) return "错误：项目根目录不存在 " + root;

        lock (Sync)
        {
            MigrateOldCache(root);   // 旧版 .gairr-map.json（根目录）→ .gairr/map.json
            var cachePath = Path.Combine(root, CacheName);
            var rkey = root + "|" + (focus ?? "");

            // 命中渲染缓存：非强制刷新 + map.json 未被改写 + 未超 TTL → 直接复用上次文本
            if (!refresh && RenderCaches.TryGetValue(rkey, out var rc))
            {
                var mt = File.Exists(cachePath) ? File.GetLastWriteTimeUtc(cachePath).ToString("o") : "";
                if (rc.Mtime == mt && DateTime.UtcNow - rc.At < RenderTtl) return rc.Text;
            }

            var cache = LoadCache(cachePath, out var ver);
            // 缓存协议升级（摘要提取规则变化）时自动全量重建；manual 手工条目仍保留
            var changed = UpdateCache(cache, root, refresh || ver < 3);

            // 每次 Build 都自动合并已有的 notes.json，避免 MapAuto 只触发一次后 Map 看不到说明
            var notes = LoadNotes(Path.Combine(root, ".gairr", "notes.json"));
            changed |= MergeNotesIntoCache(cache, notes, null);

            if (changed) SaveCache(cachePath, cache);
            var text = Render(root, cache, focus);
            RenderCaches[rkey] = new RenderCache
            {
                At = DateTime.UtcNow,
                Mtime = File.Exists(cachePath) ? File.GetLastWriteTimeUtc(cachePath).ToString("o") : "",
                Text = text
            };
            return text;
        }
    }

    /* ---------- 缓存：增量按 mtime 更新，manual 条目永不覆盖 ---------- */

    static bool UpdateCache(Dictionary<string, Entry> cache, string root, bool refresh)
    {
        var changed = false;
        List<string> files;
        try { files = Phase1Tools.EnumerateFiles(root).ToList(); }
        catch { files = new List<string>(); }

        foreach (var f in files)
        {
            var rel = Path.GetRelativePath(root, f);
            // 缓存自身与 .gairr 自动产物（说明/图谱/文档/状态）不入地图、不参与重写扫描
            if (IsGairrRel(rel)) continue;
            var mtime = File.GetLastWriteTimeUtc(f).ToString("o");
            if (cache.TryGetValue(rel, out var ex) && ex.Manual)
            {
                if (ex.Mtime != mtime) { ex.Mtime = mtime; changed = true; }   // 手工摘要保留，只刷新时间戳
                continue;
            }
            if (!refresh && cache.TryGetValue(rel, out var e) && e.Mtime == mtime) continue;
            changed = true;
            if (IsBackupLike(rel)) { cache[rel] = new Entry { Mtime = mtime }; continue; }
            cache[rel] = GenEntry(f, mtime);
        }

        var alive = files.Select(f => Path.GetRelativePath(root, f)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // 缓存自身与 .gairr 产物在 alive 里会阻止自身条目被清理，需显式排除
        var dead = cache.Keys.Where(k => !alive.Contains(k) || IsGairrRel(k)).ToList();
        foreach (var k in dead) { cache.Remove(k); changed = true; }
        return changed;
    }

    /// <summary>备份/旧版痕迹文件名：.bak 系列或带 _260816_ 这类日期戳</summary>
    static bool IsBackupLike(string rel) => BackupRe.IsMatch(rel);

    /// <summary>.gairr 自动产物（缓存自身/说明/图谱/文档/状态）：不出现在地图、不参与重写扫描</summary>
    static bool IsGairrRel(string rel)
    {
        var p = rel.Replace('\\', '/');
        return p.Equals(CacheName, StringComparison.OrdinalIgnoreCase) ||
               p.Equals(".gairr-map.json", StringComparison.OrdinalIgnoreCase) ||   // 兼容旧缓存
               p.StartsWith(".gairr/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>迁移旧版根目录缓存 .gairr-map.json → .gairr/map.json</summary>
    static void MigrateOldCache(string root)
    {
        try
        {
            var oldPath = Path.Combine(root, ".gairr-map.json");
            var newPath = Path.Combine(root, CacheName);
            if (!File.Exists(oldPath)) return;
            if (File.Exists(newPath)) { File.Delete(oldPath); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
            File.Move(oldPath, newPath);
        }
        catch { /* 迁移失败不阻塞地图生成 */ }
    }

    /// <summary>生成带缺注释统计的摘要条目</summary>
    static Entry GenEntry(string file, string mtime)
    {
        var s = GenSummary(file, out var noHead, out var noComment);
        return new Entry { Mtime = mtime, Summary = s, NoHead = noHead, NoComment = noComment };
    }

    static Dictionary<string, Entry> LoadCache(string path, out int version)
    {
        version = 1;
        var d = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(path)) return d;
            var obj = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            version = obj?["ver"]?.GetValue<int>() ?? 1;
            var files = obj?["files"] as JsonObject;
            if (files == null) return d;
            foreach (var kv in files)
                if (kv.Value is JsonObject e)
                    d[kv.Key] = new Entry
                    {
                        Mtime = (string?)e["mtime"] ?? "",
                        Summary = (string?)e["summary"] ?? "",
                        Manual = e["manual"]?.GetValue<bool>() ?? false,
                        NoHead = e["noHead"]?.GetValue<bool>() ?? false,
                        NoComment = e["noComment"]?.GetValue<int>() ?? 0,
                        Extra = (string?)e["extra"] ?? "",
                    };
        }
        catch { d.Clear(); }
        return d;
    }

    static void SaveCache(string path, Dictionary<string, Entry> cache)
    {
        try
        {
            var files = new JsonObject();
            foreach (var kv in cache.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                files[kv.Key] = new JsonObject
                {
                    ["mtime"] = kv.Value.Mtime,
                    ["summary"] = kv.Value.Summary,
                    ["manual"] = kv.Value.Manual,
                    ["noHead"] = kv.Value.NoHead,
                    ["noComment"] = kv.Value.NoComment,
                    ["extra"] = kv.Value.Extra,
                };
            var root = new JsonObject
            {
                ["ver"] = 3,
                ["updated"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["files"] = files,
            };
            File.WriteAllText(path, root.ToJsonString(JsonOpts), new UTF8Encoding(false));
        }
        catch { /* 写缓存失败不影响使用 */ }
    }

    /* ---------- 摘要提取：文件头注释 + 顶层声明行 ---------- */

    static string GenSummary(string file, out bool noHead, out int noComment)
    {
        noHead = false;
        noComment = 0;
        try
        {
            var (text, _) = Phase1Tools.Decode(File.ReadAllBytes(file));
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var n = Math.Min(lines.Length, MaxScanLines);

            // 文件头注释块：前 12 行内的连续注释（文件职责说明）
            var head = new StringBuilder();
            for (var i = 0; i < Math.Min(12, n); i++)
            {
                var t = lines[i].Trim();
                if (t.Length == 0 && head.Length == 0) continue;
                if (IsCommentLine(t)) head.Append(StripComment(t)).Append(' ');
                else if (head.Length > 0) break;
            }
            var headS = head.ToString().Trim();
            if (headS.Length > HeadLen) headS = headS[..HeadLen] + "…";

            // 类声明与方法清单：声明取紧邻上方的注释行（头部 12 行内不再重复取），
            // 输出"签名：功能"；方法数超上限只列前 MaxMembers 条并折叠提示；
            // 无注释的方法单独计数，末尾提示推动补全注释（支撑文件头/方法注释规范）
            var classes = new List<string>();
            var methods = new List<string>();
            var methodCount = 0;
            var annotated = 0;
            var comment = "";
            for (var i = 0; i < n; i++)
            {
                var t = lines[i].Trim();
                if (IsCommentLine(t))
                {
                    var c = StripComment(t);
                    if (c.Length > CommentLen) c = c[..CommentLen] + "…";
                    if (c.Length == 0) continue;   // 纯标签行（<summary> 等）不参与合并
                    comment = comment.Length == 0 ? c : comment + "；" + c;
                    if (comment.Length > CommentLen) comment = comment[..CommentLen] + "…";
                    continue;
                }
                if (t.Length == 0) continue;   // 空行：注释与声明之间允许空行
                if (IsMemberDecl(t, out var isClass))
                {
                    var sig = t.Length > SigLen ? t[..SigLen] + "…" : t;
                    if (isClass)
                    {
                        if (classes.Count < MaxClasses)
                            classes.Add("[类] " + sig + (i >= 12 && comment.Length > 0 ? "：" + comment : ""));
                    }
                    else if (i >= 12)   // 方法避免头部 12 行：防止把文件头注释当成方法注释
                    {
                        methodCount++;
                        if (comment.Length > 0) annotated++;
                        if (methods.Count < MaxMembers)
                            methods.Add(sig + (comment.Length > 0 ? "：" + comment : ""));
                    }
                }
                comment = "";   // 普通代码行：前方注释失效
            }

            var sb = new StringBuilder();
            if (headS.Length > 0) sb.Append(headS);
            else if (classes.Count + methods.Count > 0) sb.Append("（无文件头注释）");
            foreach (var c in classes) { if (sb.Length > 0) sb.Append(" ∷ "); sb.Append(c); }
            foreach (var m in methods) { if (sb.Length > 0) sb.Append(" ∷ "); sb.Append(m); }
            if (methodCount > annotated)
            {
                if (sb.Length > 0) sb.Append(" ∷ ");
                sb.Append("[").Append(methodCount - annotated).Append(" 个方法无注释]");
            }
            if (methodCount > MaxMembers)
            {
                if (sb.Length > 0) sb.Append(" ∷ ");
                sb.Append("+[").Append(methodCount).Append("]更多方法未列出");
            }
            noHead = headS.Length == 0 && classes.Count + methods.Count > 0;
            noComment = methodCount - annotated;
            return LLMClient.Trunc(sb.ToString(), MaxSummaryChars);
        }
        catch { noHead = false; noComment = 0; return ""; }
    }

    /// <summary>注释行识别：// /* * # ; -- <!-- 与 Python 文档字符串（internal 供 SymbolIndex 复用）</summary>
    internal static bool IsCommentLine(string t) =>
        t.StartsWith("//") || t.StartsWith("/*") || t.StartsWith('*') || t.StartsWith('#') ||
        t.StartsWith(';') || t.StartsWith("<!--") || t.StartsWith("--") ||
        t.StartsWith("\"\"\"") || t.StartsWith("'''");

    /// <summary>去掉注释行前缀、XML 文档标签与空白（internal 供 SymbolIndex 复用）</summary>
    internal static string StripComment(string t)
    {
        var s = t.Trim();
        if (s.StartsWith("<!--")) s = s.Replace("<!--", "").Replace("-->", "");
        s = XmlTagRe.Replace(s, "").Trim();
        return s.TrimStart('/', '*', ' ', '#', ';', '-', '!', '"', '\'').Trim();
    }

    /// <summary>是否为类/接口/枚举/结构/记录声明，或方法声明（排除 if/for/return 等控制流行）</summary>
    static bool IsMemberDecl(string t, out bool isClass)
    {
        isClass = ClassRe.IsMatch(t);
        if (isClass) return true;
        if (t.Length < 6 || t.Length > 220) return false;
        var sp = t.IndexOfAny(new[] { ' ', '\t', '(' });
        var first = sp < 0 ? t : t[..sp];
        return first.Length > 0 && !CtrlWords.Contains(first) && MethodRe.IsMatch(t);
    }

    /* ---------- 渲染：目录树 + 摘要，预算内优先保带摘要文件 ---------- */

    static string Render(string root, Dictionary<string, Entry> cache, string? focus)
    {
        var focusRel = (focus ?? "").Replace('\\', '/').Trim('/');
        var sb = new StringBuilder();
        var title = "【项目地图】" + root + (focusRel.Length > 0 ? " :: " + focusRel : "");
        sb.Append(title).Append("（文件 ").Append(cache.Count).Append(" 个）\n");

        var rootNode = new Node { Dir = true, Name = "" };
        foreach (var kv in cache.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            var p = kv.Key.Replace('\\', '/');
            if (focusRel.Length > 0 && p != focusRel &&
                !p.StartsWith(focusRel + "/", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = p.Split('/');
            var cur = rootNode;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                var name = parts[i];
                var next = cur.Kids.FirstOrDefault(k => k.Dir && string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase));
                if (next == null)
                {
                    next = new Node { Dir = true, Name = name };
                    cur.Kids.Add(next);
                }
                cur = next;
            }
            cur.Kids.Add(new Node { Dir = false, Name = parts[^1], Summary = kv.Value.Summary, Extra = kv.Value.Extra });
        }

        var budget = MaxChars - title.Length - 24;
        foreach (var n in rootNode.Kids.OrderByDescending(k => k.Dir).ThenBy(k => k.Name, StringComparer.OrdinalIgnoreCase))
            RenderNode(sb, n, "", 0, ref budget);

        sb.Append("（.gairr/map.json 可手工编辑补充语义描述，标 manual 的条目不被自动覆盖）");
        return LLMClient.Trunc(sb.ToString(), MaxChars);
    }

    static void RenderNode(StringBuilder sb, Node n, string prefix, int depth, ref int budget)
    {
        if (budget <= 0) return;
        var pad = new string(' ', depth * 2);
        var relPath = prefix.Length > 0 ? prefix + "/" + n.Name : n.Name;

        if (!n.Dir)
        {
            var body = n.Summary ?? "";
            if (!string.IsNullOrEmpty(n.Extra))
                body = body.Length == 0 ? n.Extra : body + " ∷ " + n.Extra;
            var extra = string.IsNullOrEmpty(body) ? "" : "  [" + body + "]";
            var line = pad + "· " + relPath + extra;
            if (line.Length + 1 > budget) { sb.Append(pad).Append("· …(省略)\n"); budget = -1; return; }
            sb.Append(line).Append('\n');
            budget -= line.Length + 1;
            return;
        }

        if (depth > 0)
        {
            var line = pad + "▸ " + n.Name + "/";
            if (line.Length + 1 > budget) { sb.Append(pad).Append("…\n"); budget = -1; return; }
            sb.Append(line).Append('\n');
            budget -= line.Length + 1;
        }

        var files = n.Kids.Where(k => !k.Dir).ToList();
        var dirs = n.Kids.Where(k => k.Dir).ToList();
        var withDesc = files.Where(k => !string.IsNullOrEmpty(k.Summary) || !string.IsNullOrEmpty(k.Extra))
                            .OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var plain = files.Count - withDesc.Count;

        foreach (var k in withDesc) RenderNode(sb, k, relPath, depth + 1, ref budget);
        if (plain <= PlainFold)
        {
            foreach (var k in files.Where(k => string.IsNullOrEmpty(k.Summary))
                                   .OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase))
                RenderNode(sb, k, relPath, depth + 1, ref budget);
        }
        else if (plain > 0 && budget > 0)
        {
            var line = pad + "  · …(+" + plain + " 个文件)";
            if (line.Length + 1 <= budget) { sb.Append(line).Append('\n'); budget -= line.Length + 1; }
        }
        foreach (var k in dirs.OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase)) RenderNode(sb, k, relPath, depth + 1, ref budget);
    }

    /* ---------- 自动化服务接口（ProjectMapAuto 调用） ---------- */

    /// <summary>缓存文件条目数（0=尚未生成过项目地图）</summary>
    internal static int PeekFiles(AppConfig cfg)
    {
        var root = cfg.ProjectRoot;
        if (root.Length == 0 || !Directory.Exists(root)) return 0;
        lock (Sync)
        {
            return LoadCache(Path.Combine(root, CacheName), out _).Count;
        }
    }

    /// <summary>列出注释缺口文件（无文件头注释或存在无注释方法），供自动化补注释使用</summary>
    internal static List<(string Rel, string Summary, int NoComment, bool NoHead)> PeekGaps(AppConfig cfg)
    {
        var list = new List<(string Rel, string Summary, int NoComment, bool NoHead)>();
        var root = cfg.ProjectRoot;
        if (root.Length == 0 || !Directory.Exists(root)) return list;
        lock (Sync)
        {
            var cache = LoadCache(Path.Combine(root, CacheName), out _);
            foreach (var kv in cache)
                if (!kv.Value.Manual && (kv.Value.NoHead || kv.Value.NoComment > 0) &&
                    !IsBackupLike(kv.Key))
                    list.Add((kv.Key, kv.Value.Summary, kv.Value.NoComment, kv.Value.NoHead));
        }
        return list.OrderBy(x => x.Rel, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>列出前 MaxMembers 条无注释方法签名（供补注释 prompt 使用，索引从 1 开始）</summary>
    internal static List<string> ListUnannotated(string file)
    {
        var list = new List<string>();
        try
        {
            var (text, _) = Phase1Tools.Decode(File.ReadAllBytes(file));
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var n = Math.Min(lines.Length, MaxScanLines);
            var comment = "";
            for (var i = 0; i < n; i++)
            {
                var t = lines[i].Trim();
                if (IsCommentLine(t))
                {
                    var c = StripComment(t);
                    if (c.Length == 0) continue;
                    comment = comment.Length == 0 ? c : comment + "；" + c;
                    if (comment.Length > CommentLen) comment = comment[..CommentLen] + "…";
                    continue;
                }
                if (t.Length == 0) continue;
                if (IsMemberDecl(t, out var isClass))
                {
                    if (!isClass && i >= 12 && comment.Length == 0 && list.Count < MaxMembers)
                    {
                        var sig = t.Length > SigLen ? t[..SigLen] + "…" : t;
                        list.Add(sig);
                    }
                }
                comment = "";
            }
        }
        catch { }
        return list;
    }

    /// <summary>把 notes 与 refs 合并进已有 cache，返回是否发生变更</summary>
    static bool MergeNotesIntoCache(Dictionary<string, Entry> cache,
                                    Dictionary<string, NoteEntry> notes,
                                    Dictionary<string, HashSet<string>>? refs)
    {
        var changed = false;
        foreach (var kv in cache)
        {
            if (kv.Value.Manual) continue;
            var parts = new List<string>();
            if (notes.TryGetValue(kv.Key, out var note))
            {
                if (note.Head.Length > 0) parts.Add("说明：" + note.Head);
                if (note.Methods.Count > 0)
                    parts.Add("方法说明：" + string.Join("；", note.Methods.Select(m => m.Key + "=" + m.Value)));
            }
            // refs 键为 '/' 分隔（ScanRefs 输出），缓存键为平台分隔符，先归一化再查
            if (refs != null && refs.TryGetValue(kv.Key.Replace('\\', '/'), out var by) && by.Count > 0)
                parts.Add("被 " + by.Count + " 文件引用");
            var extra = string.Join(" ∷ ", parts);
            if (kv.Value.Extra != extra) { kv.Value.Extra = extra; changed = true; }
        }
        return changed;
    }

    /// <summary>从 notes.json 读取 LLM 补充说明</summary>
    internal static Dictionary<string, NoteEntry> LoadNotes(string path)
    {
        var notes = new Dictionary<string, NoteEntry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(path)) return notes;
            var obj = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (obj == null) return notes;
            foreach (var kv in obj)
            {
                if (kv.Value is not JsonObject e) continue;
                var n = new NoteEntry();
                var h = (string?)e["head"] ?? "";
                if (!h.TrimStart().StartsWith("null", StringComparison.OrdinalIgnoreCase))
                    n.Head = h;   // 旧数据里模型可能把 "null（已有说明）" 字样原样返回，读取时过滤
                if (e["methods"] is JsonObject ms)
                    foreach (var m in ms)
                        if (m.Value is JsonNode v) n.Methods[m.Key] = v.GetValue<string>();
                notes[kv.Key] = n;
            }
        }
        catch { /* 损坏忽略 */ }
        return notes;
    }

    /// <summary>合并补充说明与调用关系进缓存（Extra 渲染：说明/方法说明/被引用数）并落盘</summary>
    internal static void ApplyNotes(AppConfig cfg, Dictionary<string, NoteEntry> notes,
                                    Dictionary<string, HashSet<string>>? refs)
    {
        var root = cfg.ProjectRoot;
        if (root.Length == 0 || !Directory.Exists(root)) return;
        lock (Sync)
        {
            var cachePath = Path.Combine(root, CacheName);
            var cache = LoadCache(cachePath, out _);
            if (MergeNotesIntoCache(cache, notes, refs)) SaveCache(cachePath, cache);
        }
    }
}