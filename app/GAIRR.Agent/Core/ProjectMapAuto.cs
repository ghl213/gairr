using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GAIRR.Core;

/// <summary>项目地图自动化：启动 / 切换项目后后台执行——
/// 1) 生成/增量更新项目地图（ProjectMap.Build，纯程序，零 token）；
/// 2) 文件级调用关系图谱（静态分析：using/import/require + 符号词匹配）→ .gairr/refs.json；
/// 3) 缺注释文件/方法由大模型补说明（写入 .gairr/notes.json，不改源码）；
/// 4) 自动维护文档清单：内置架构/功能文档（.gairr/docs/）首次自动生成；清单内文档（内置 + 用户标记）
///    在"重建地图"时按最新地图刷新（用户文档走"旧文即提纲"模式），清单外文件永不触碰。
/// 增量与状态集中在 .gairr/state.json；同项目根 10 秒节流、换项目排队；任何失败只记日志不阻塞。</summary>
/// <summary>项目地图自动化进度报告：消息 + 百分比 + 阶段描述。</summary>
public sealed class MapProgress
{
    public int Percent { get; init; }
    public string Message { get; init; } = "";
}

public static class ProjectMapAuto
{
    const string DirName = ".gairr";
    const string StateName = "state.json";
    const string NotesName = "notes.json";
    const string SymbolNotesName = "symbol-notes.json";
    const string RefsName = "refs.json";
    const string DocsDir = "docs";
    const string ArchName = "architecture.md";
    const string FeatName = "features.md";
    const string AiIgnoreName = "ai-ignore.json";  // LLM 扫描判断的忽略清单（按目录/扩展名/前缀）
    const int MaxScanBytes = 512 * 1024;      // 图谱扫描单文件上限（二进制/大文件跳过）
    const int MaxTotalBytes = 16 * 1024 * 1024; // 图谱全文缓存总上限（超限后停止收录）
    const int MaxNoteBytes = 300 * 1024;      // 补注释单文件上限
    const int SymbolNoteMaxLen = 100;         // 符号级说明长度上限（字）
    const int MaxRefFiles = 1500;             // 图谱扫描文件数上限
    const int MaxSymbolPattern = 2000;        // 全文词匹配符号上限
    const int MaxFailStreak = 3;              // 连续失败即停止本次（防配置错误反复打网络）
    const int NotesDelayMs = 500;             // 批间间隔
    const int AiSamplePerDir = 3;             // AiScan 采样：每目录最多列 N 个文件名
    const int AiSampleMaxDirs = 200;          // AiScan 采样：最多列 N 个目录（防 prompt 爆炸）
    const int AiPromptMaxChars = 8000;        // AiScan prompt 上限（超过按目录采样）
    // .NET 8 JsonNode 序列化要求显式 TypeInfoResolver（否则 JsonArray/JsonValue 写入抛 InvalidOperationException）
    static readonly System.Text.Json.JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // 派生 json（map/refs/notes/state 等）中文直存
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };
    static readonly string[] ImportExts =
        { ".ts", ".tsx", ".js", ".jsx", ".mjs", ".py", ".cs", ".go", ".java", ".kt", ".dart", ".rb", ".php", ".vue", ".cpp", ".h", ".c" };

    static readonly SemaphoreSlim gate = new(1, 1);   // 全应用同一时刻只跑一个自动化
    static readonly object schedLock = new();
    static string? activeRoot;                        // 最近一次调度/处理的项目根
    static DateTime lastCall = DateTime.MinValue;     // 同项目根 10 秒节流
    static CancellationTokenSource? currentCts;       // 当前运行任务的取消令牌源

    /// <summary>是否有项目地图自动化任务正在运行。</summary>
    public static bool IsRunning => currentCts != null && !currentCts.IsCancellationRequested;

    /// <summary>中止当前正在运行的项目地图自动化任务（如有）。</summary>
    public static void Cancel()
    {
        try { currentCts?.Cancel(); } catch { /* 已取消或 disposed 忽略 */ }
    }

    sealed class AutoState
    {
        public int Ver = 1;
        public string RefsHash = "";        // 图谱结构 hash（文件集合+mtime），一致则跳过写盘
        public string DocsHash = "";        // 生成文档时的结构 hash
        public int DocsCount = 0;           // 生成文档时的文件数（变化率 &gt;20% 提示重建）
        public string AiIgnoreHash = "";    // LLM 判断忽略清单时的结构 hash；一致则跳过重判（省 token）
        public string LastError = "";       // 最近一次失败说明
        public Dictionary<string, string> NotesDone = new();   // rel → 处理时 mtime（未变化不重复补）
        public Dictionary<string, string> SymbolsDone = new(); // rel::符号名 → 处理时 mtime（未变化不重复补）
        public List<string> Lessons = new();   // 项目经验（失败规律沉淀，跨会话注入系统提示词）
        public List<string> DocsAuto = new();  // 自动维护文档清单（正斜杠 rel；内置 .gairr/docs/* 由框架保证在列）
    }

    /// <summary>LLM 扫描判断的忽略清单（.gairr/ai-ignore.json）：与固定 IgnoreDirs/Exts 叠加。
    /// dirs=相对路径前缀（命中整棵子树跳过）；exts=扩展名（小写带点）；prefixes=文件名前缀（无扩展名文件）。</summary>
    internal sealed class AiIgnore
    {
        public List<string> Dirs = new();
        public List<string> Exts = new();
        public List<string> Prefixes = new();
        public string Hash = "";   // 生成时的结构 hash（用于增量判断）

        static readonly Dictionary<string, (AiIgnore? Ig, DateTime Mtime)> LoadCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>加载 ai-ignore.json（按 root 缓存，mtime 变化自动重载；无文件/空清单返回 null）。</summary>
        internal static AiIgnore? Load(string root)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;
            var path = Path.Combine(root, DirName, AiIgnoreName);
            if (!File.Exists(path)) return null;
            DateTime mtime;
            try { mtime = File.GetLastWriteTimeUtc(path); } catch { return null; }
            if (LoadCache.TryGetValue(root, out var hit) && hit.Mtime == mtime) return hit.Ig;
            AiIgnore? ai;
            try
            {
                var rt = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)).RootElement;
                ai = new AiIgnore();
                if (rt.TryGetProperty("dirs", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var e in d.EnumerateArray()) ai.Dirs.Add((e.GetString() ?? "").Replace('\\', '/').Trim());
                if (rt.TryGetProperty("exts", out var x) && x.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var e in x.EnumerateArray())
                    {
                        var s = (e.GetString() ?? "").Trim().ToLowerInvariant();
                        if (s.Length > 0 && !s.StartsWith('.')) s = "." + s;
                        if (s.Length > 0) ai.Exts.Add(s);
                    }
                if (rt.TryGetProperty("prefixes", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var e in p.EnumerateArray()) ai.Prefixes.Add((e.GetString() ?? "").Trim());
                if (rt.TryGetProperty("hash", out var h) && h.ValueKind == System.Text.Json.JsonValueKind.String)
                    ai.Hash = h.GetString() ?? "";
                ai.Dirs.RemoveAll(s => s.Length == 0);
                ai.Prefixes.RemoveAll(s => s.Length == 0);
                if (ai.Dirs.Count + ai.Exts.Count + ai.Prefixes.Count == 0) ai = null;
            }
            catch { ai = null; }
            LoadCache[root] = (ai, mtime);
            return ai;
        }

        /// <summary>只增不删合并：已有条目永不覆盖（用户可手工改 ai-ignore.json 追加），新条目追加。</summary>
        internal void MergeFrom(AiIgnore other)
        {
            foreach (var d in other.Dirs) if (!Dirs.Contains(d, StringComparer.OrdinalIgnoreCase)) Dirs.Add(d);
            foreach (var x in other.Exts) if (!Exts.Contains(x, StringComparer.OrdinalIgnoreCase)) Exts.Add(x);
            foreach (var p in other.Prefixes) if (!Prefixes.Contains(p, StringComparer.OrdinalIgnoreCase)) Prefixes.Add(p);
        }

        /// <summary>判断相对路径（'/' 分隔）是否命中：目录前缀 / 扩展名 / 文件前缀</summary>
        internal bool Hits(string rel)
        {
            if (rel.Length == 0) return false;
            var p = rel.Replace('\\', '/');
            if (Dirs.Count > 0)
                foreach (var d in Dirs)
                    if (p == d || p.StartsWith(d + "/", StringComparison.OrdinalIgnoreCase)) return true;
            if (Exts.Count > 0)
            {
                var dot = p.LastIndexOf('.');
                if (dot >= 0 && Exts.Contains(p.Substring(dot).ToLowerInvariant())) return true;
            }
            if (Prefixes.Count > 0)
            {
                var slash = p.LastIndexOf('/');
                var name = slash >= 0 ? p.Substring(slash + 1) : p;
                var dot = name.LastIndexOf('.');
                if (dot <= 0)   // 无扩展名文件才走前缀匹配
                    foreach (var pf in Prefixes)
                        if (name.StartsWith(pf, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        internal void Save(string root)
        {
            var path = Path.Combine(root, DirName, AiIgnoreName);
            var obj = new System.Text.Json.Nodes.JsonObject
            {
                ["dirs"] = new System.Text.Json.Nodes.JsonArray(Dirs.Select(d => (System.Text.Json.Nodes.JsonNode)d).ToArray()),
                ["exts"] = new System.Text.Json.Nodes.JsonArray(Exts.Select(d => (System.Text.Json.Nodes.JsonNode)d).ToArray()),
                ["prefixes"] = new System.Text.Json.Nodes.JsonArray(Prefixes.Select(d => (System.Text.Json.Nodes.JsonNode)d).ToArray()),
                ["hash"] = Hash,
            };
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, obj.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
            }), new UTF8Encoding(false));
            // 刷新缓存（Save 后 mtime 已变，缓存里旧对象作废，用新对象 + 新 mtime 覆盖）
            DateTime mtime;
            try { mtime = File.GetLastWriteTimeUtc(path); } catch { mtime = DateTime.UtcNow; }
            LoadCache[root] = (this, mtime);
        }
    }

    const int MaxLessons = 20;            // 项目经验上限（超出丢最旧）
    const int InjectLessons = 5;          // 每次注入提示词条数（最近 5 条）
    const int MaxGlobalLessons = 100;     // 全局经验库上限（跨项目共享，超出丢最旧）
    /// <summary>全局经验库路径：GAIRR.exe 同目录 data/lessons.json（发布时 data 目录保留，跨项目共享）</summary>
    static string GlobalLessonsPath => Path.Combine(System.AppContext.BaseDirectory, "data", "lessons.json");

    /// <summary>判断一条经验是否为"伪经验"（经验污染）：模型拼错/臆造工具名（未知工具）或被禁用工具导致的失败，
    /// 属模型幻觉与瞬时误用，不是可复用的踩坑规律，沉淀后注入会反过来诱导后续任务重犯</summary>
    public static bool IsPollutedLesson(string text) =>
        text.Contains("错误：未知工具", StringComparison.Ordinal)
        || text.Contains("已被禁用", StringComparison.Ordinal);

    /// <summary>沉淀一条项目经验（去重：近 20 条内同文本不重复；截断 200 字，超出丢最旧；伪经验直接丢弃）</summary>
    public static void RecordLesson(AppConfig cfg, string text)
    {
        try
        {
            if (IsPollutedLesson(text)) return;   // 记录侧拦截：工具名非法的失败不构成经验
            var root = cfg.ProjectRoot;
            if (root.Length == 0) return;
            var statePath = Path.Combine(root, DirName, StateName);
            var state = LoadState(statePath);
            var norm = text.Length > 200 ? text[..200] : text;
            if (state.Lessons.Any(l => string.Equals(l, norm, StringComparison.OrdinalIgnoreCase))) return;
            state.Lessons.Add(norm);
            state.Lessons = state.Lessons.TakeLast(MaxLessons).ToList();
            SaveState(statePath, state);
        }
        catch { }
    }

    /// <summary>沉淀一条全局经验（跨项目共享：存 exe 同目录 data/lessons.json；去重：全文忽略大小写不重复；
    /// 截断 200 字；tags 逗号分隔清洗为最多 6 个、每个 ≤20 字；超出 100 条丢最旧）</summary>
    public static void RecordGlobalLesson(AppConfig cfg, string text, string tags)
    {
        try
        {
            if (IsPollutedLesson(text)) return;   // 记录侧拦截：伪经验不进全局库
            var norm = text.Length > 200 ? text[..200] : text;
            var path = GlobalLessonsPath;
            var arr = LoadGlobalLessons(path);
            if (arr.Any(l => string.Equals(l?["text"]?.GetValue<string>() ?? "", norm, StringComparison.OrdinalIgnoreCase))) return;
            var tagArr = new JsonArray();
            foreach (var tg in tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                         .Where(tg => tg.Length <= 20).Distinct(StringComparer.OrdinalIgnoreCase).Take(6))
                tagArr.Add(tg);
            arr.Add(new JsonObject
            {
                ["text"] = norm,
                ["tags"] = tagArr,
                ["time"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss")
            });
            while (arr.Count > MaxGlobalLessons) arr.RemoveAt(0);
            SaveGlobalLessons(path, arr);
        }
        catch { }
    }

    /// <summary>跨会话经验段（供 AgentLoop 注入系统提示词）：【通用经验】= 全局库中当前项目名命中者优先 + 新→旧共 5 条；
    /// 【项目经验】= 本项目最近 5 条；两段均无可注入内容时返回空串</summary>
    public static string Lessons(AppConfig cfg)
    {
        try
        {
            var root = cfg.ProjectRoot;
            if (root.Length == 0) return "";
            var proj = Path.GetFileName(root.TrimEnd('\\', '/'));
            // 全局经验（跨项目共享）：标签/正文含当前项目名的排前，同类内按写入顺序新→旧；伪经验一律不注入
            var global = LoadGlobalLessons(GlobalLessonsPath)
                .Where(l => !IsPollutedLesson(l?["text"]?.GetValue<string>() ?? ""))
                .Select((l, i) => new { L = l, Hit = HitProject(l, proj), Ord = i })
                .OrderByDescending(x => x.Hit).ThenByDescending(x => x.Ord)
                .Take(InjectLessons).Select(x => x.L);
            var sb = new StringBuilder();
            var any = false;
            foreach (var l in global)
            {
                if (!any) { sb.Append("\n【通用经验】（此前其他项目沉淀的环境/工具坑，注意避免重犯）：\n"); any = true; }
                sb.Append("- ").Append(FormatGlobalLesson(l)).Append('\n');
            }
            var state = LoadState(Path.Combine(root, DirName, StateName));
            // 注入侧兜底过滤：存量 state.json 里的伪经验（未知工具/禁用工具）不再注入，无需等清理完成
            var projLessons = state.Lessons.Where(l => !IsPollutedLesson(l)).TakeLast(InjectLessons).ToList();
            if (projLessons.Count > 0)
            {
                sb.Append("\n【项目经验】（此前任务的失败记录，注意避免重犯）：\n");
                foreach (var l in projLessons)
                    sb.Append("- ").Append(l).Append('\n');
            }
            return sb.Length == 0 ? "" : sb.ToString();
        }
        catch { return ""; }
    }

    /// <summary>读取全局经验库（不存在/损坏返回空数组）</summary>
    static JsonArray LoadGlobalLessons(string path)
    {
        try
        {
            if (!File.Exists(path)) return new JsonArray();
            return JsonNode.Parse(File.ReadAllText(path)) as JsonArray ?? new JsonArray();
        }
        catch { return new JsonArray(); }
    }

    /// <summary>写回全局经验库</summary>
    static void SaveGlobalLessons(string path, JsonArray arr)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            File.WriteAllText(path, arr.ToJsonString(JsonOpts), new UTF8Encoding(false));
        }
        catch { }
    }

    /// <summary>全局经验是否命中当前项目：标签任一项含项目名（含"项目:x"写法）、或正文含项目名</summary>
    static bool HitProject(JsonNode? l, string proj)
    {
        if (proj.Length == 0 || l is not JsonObject o) return false;
        if ((o["text"]?.GetValue<string>() ?? "").Contains(proj, StringComparison.OrdinalIgnoreCase)) return true;
        return o["tags"] is JsonArray tags && tags.Any(t => (t?.GetValue<string>() ?? "").Contains(proj, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>全局经验渲染：[标签] (日期) 正文</summary>
    static string FormatGlobalLesson(JsonNode? l)
    {
        if (l is not JsonObject o) return "";
        var text = o["text"]?.GetValue<string>() ?? "";
        var tags = o["tags"] is JsonArray t ? string.Join("、", t.Select(x => x?.GetValue<string>() ?? "")) : "";
        var time = o["time"]?.GetValue<string>() ?? "";
        var date = time.Length >= 10 ? time[..10] : "";
        return (tags.Length > 0 ? $"[{tags}] " : "") + (date.Length > 0 ? $"({date}) " : "") + text;
    }

    /// <summary>调度自动化：同项目根运行中/10 秒内重复调用跳过；换项目根排队等上一个完成。<br/>
    /// UI 线程调用，实际工作放后台任务；progress 回调为进度消息（线程安全，切 UI 由回调方负责）。</summary>
    public static void Schedule(AppConfig cfg, Action<MapProgress>? progress = null)
    {
        var root = cfg.ProjectRoot;
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
        var now = DateTime.Now;
        lock (schedLock)
        {
            var sameRoot = string.Equals(activeRoot, root, StringComparison.OrdinalIgnoreCase);
            if (gate.CurrentCount == 0)
            {
                // 已有任务在跑：同 root 直接跳过；换 root 排队等完成后执行
                if (sameRoot) return;
            }
            else if (sameRoot && (now - lastCall) < TimeSpan.FromSeconds(10)) return;
            activeRoot = root;
        }
        _ = Task.Run(() => RunAsync(cfg, progress));
    }

    /// <summary>自动化主流程（必须在 gate 内串行）。任何异常只记录，不向 UI 抛。<br/>
    /// rebuildDocs=true（"重建地图"按钮）时按清单强制刷新全部自动维护文档；日常调度只做首次生成与过期提示。</summary>
    public static async Task RunAsync(AppConfig cfg, Action<MapProgress>? progress, bool rebuildDocs = false)
    {
        var root = cfg.ProjectRoot;
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
        await gate.WaitAsync();
        var cts = new CancellationTokenSource();
        IDisposable? llmSlot = null;   // 厂商并发占位：Agent 会话内工具调用的地图任务不重复占位（随父任务统计）
        try
        {
            currentCts = cts;
            lock (schedLock) activeRoot = root;
            // 系统资源闸：独立触发（手动"重建地图"、切项目自动调度、后台定时）时主机 CPU/内存 ≥80% 不再开新地图任务，
            // 本轮跳过等后续调度自动重试；Agent 会话内工具调用的地图任务不拦（父任务启动时已检查过资源闸，运行中不打断）
            if (AgentLoop.CurrentToolLoop == null && HostLoadMonitor.IsOverloaded())
            {
                progress?.Invoke(new MapProgress { Percent = 0, Message = $"地图自动化已跳过：主机资源占用过高（CPU {HostLoadMonitor.CpuPercent:0}% / 内存 {HostLoadMonitor.MemPercent:0}% ≥ {HostLoadMonitor.GatePct:0}%），将由后续调度自动重试" });
                return;
            }
            // 厂商并发闸：独立触发（手动"重建地图"、切项目自动调度、后台定时）的地图/文档任务启动前
            // 先占用 NotesProvider 一个并发位；该厂商已满则本轮跳过，等待后续调度自动重试，避免超限抢占
            if (AgentLoop.CurrentToolLoop == null)
            {
                // 占用位登记本次在跑的模型（占位时点快照）：NotesProvider 中途故障转移会改指别的厂商，
                // 登记不跟随，避免金色错挂到原厂商下的同名模型
                var notesModel = cfg.ProviderCfg(cfg.NotesProvider).Model;
                llmSlot = LlmConcurrencyGate.TryEnter(cfg.NotesProvider, cfg.MaxConcurrencyFor(cfg.NotesProvider), () => notesModel);
            }
            if (llmSlot == null && AgentLoop.CurrentToolLoop == null && !string.IsNullOrWhiteSpace(cfg.NotesProvider))
            {
                progress?.Invoke(new MapProgress { Percent = 0, Message = "地图自动化已跳过：NotesProvider 厂商并发已达上限（将由后续调度自动重试）" });
                return;
            }
            var dir = Path.Combine(root, DirName);
            try { Directory.CreateDirectory(dir); } catch { }
            var state = LoadState(Path.Combine(dir, StateName));
            try
            {
                await RunCore(cfg, root, state, progress, cts.Token, rebuildDocs);
                state.LastError = "";
            }
            catch (OperationCanceledException)
            {
                progress?.Invoke(new MapProgress { Percent = 0, Message = "项目地图：已中止" });
            }
            catch (Exception ex)
            {
                state.LastError = ex.Message;
                progress?.Invoke(new MapProgress { Percent = 0, Message = "项目地图自动化异常：" + ex.Message });
                System.Diagnostics.Debug.WriteLine($"[ProjectMapAuto] {ex}");
                RecordLesson(cfg, "项目地图自动化异常: " + ex.Message);
            }
            SaveState(Path.Combine(root, DirName, StateName), state);
        }
        finally
        {
            llmSlot?.Dispose();   // 释放厂商并发位
            currentCts = null;
            try { cts.Dispose(); } catch { }
            gate.Release();
            lock (schedLock) lastCall = DateTime.Now;
        }
    }

    /* ---------- 主流程 ---------- */

    static async Task RunCore(AppConfig cfg, string root, AutoState state, Action<MapProgress>? progress, CancellationToken ct, bool rebuildDocs = false)
    {
        var files = SafeFiles(root);
        var (docCount, structHash) = StructureHash(root, files);
        var ctx = new ProgressContext(progress);

        // 1) LLM 扫描判断"非源码/非资源/非配置"目录与文件（写 .gairr/ai-ignore.json，结构哈希不变则跳过）
        if (SystemCfg.AiScan && HasApiKey(cfg))
        {
            ct.ThrowIfCancellationRequested();
            ctx.Report(3, "AI 扫描判断忽略项（非源码/非资源）…");
            try { await AiScanAsync(cfg, root, files, structHash, state, ctx, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { ctx.Report(3, "AI 扫描失败（不影响后续）：" + ex.Message); }
        }

        // 1.5) 调用关系图谱（程序静态分析，零 token）：hash 一致则跳过写盘；只有写盘成功才登记 hash
        ct.ThrowIfCancellationRequested();
        ctx.Report(5, "分析文件调用关系…");
        var (toRefs, byRefs) = ScanRefs(root, files, ct);
        if (!string.Equals(state.RefsHash, structHash, StringComparison.Ordinal) &&
            SaveRefs(Path.Combine(root, DirName, RefsName), toRefs))
            state.RefsHash = structHash;

        // 2) 生成/增量更新项目地图缓存（对外文本在下一次 Map 调用时渲染）
        ct.ThrowIfCancellationRequested();
        ctx.Report(20, "生成/更新地图缓存…");
        var map = ProjectMap.Build(cfg);

        // 3) 合并已有补充说明与调用关系到地图缓存（Extra：说明/方法说明/被引用数）
        ct.ThrowIfCancellationRequested();
        ctx.Report(25, "合并补充说明…");
        var notes = ProjectMap.LoadNotes(Path.Combine(root, DirName, NotesName));
        ProjectMap.ApplyNotes(cfg, notes, byRefs);

        // 4) 缺注释补说明（LLM，写 .gairr/notes.json，不改源码）；成功后增量合并进地图缓存
        if (!SystemCfg.AutoNotes) { /* 配置关闭 */ }
        else if (!HasApiKey(cfg)) ctx.Report(30, "未配置模型 API Key，跳过缺注释补说明");
        else if (await FillNotesAsync(cfg, root, state, notes, byRefs, ctx, ct))
            ctx.Report(75, "补注释完成（已写入 .gairr/notes.json，下次 Map 生效）");
        else
            ctx.Report(75, "无需补注释");

        // 4.5) 符号级补注（LLM，写 .gairr/symbol-notes.json，不改源码）：符号索引中无中文标签的符号
        //      由 LLM 补一句说明，检索中文标签层（tag 权重 15）直接受益，文件接下来可被 MapTrace 按中文召回
        if (!SystemCfg.SymbolNotes) { /* 配置关闭 */ }
        else if (!HasApiKey(cfg)) { /* 无 API Key 时第 4 步已报告，静默跳过 */ }
        else if (await FillSymbolNotesAsync(cfg, root, state, ctx, ct))
            ctx.Report(76, "符号级补注完成（已写入 .gairr/symbol-notes.json）");

        // 4.6) 本地向量索引（ONNX embedding，写 .gairr/embed-index.json）：语料变更后增量/全量重建
        ct.ThrowIfCancellationRequested();
        if (!SystemCfg.Embed) { /* 配置关闭 */ }
        else
        {
            ctx.Report(78, "构建/更新本地向量索引…");
            try
            {
                if (EmbedIndexBuilder.NeedsRebuild(cfg))
                {
                    var n = EmbedIndexBuilder.Rebuild(cfg);
                    ctx.Report(80, $"向量索引重建完成（{n} 条）");
                }
                else
                {
                    var n = EmbedIndexBuilder.Update(cfg);
                    ctx.Report(80, $"向量索引增量更新（{n} 条变更）");
                }
            }
            catch (Exception ex)
            {
                ctx.Report(80, "向量索引构建失败（不影响其他层）：" + ex.Message);
            }
        }

        // 5) 自动维护文档：日常=首次生成+过期提示；重建地图=按清单全量刷新
        if (!SystemCfg.AutoDocs) { /* 配置关闭 */ }
        else if (!HasApiKey(cfg)) ctx.Report(85, "未配置模型 API Key，跳过架构/功能文档");
        else if (rebuildDocs)
        {
            ctx.Report(85, "刷新自动维护文档清单…");
            await RebuildDocsAsync(cfg, root, map, docCount, structHash, state, ctx, ct);
        }
        else
        {
            ctx.Report(85, "生成/检查架构与功能文档…");
            await EnsureDocsAsync(cfg, root, map, docCount, structHash, state, ctx, ct);
        }

        ct.ThrowIfCancellationRequested();
        ctx.Report(100, "完成");
    }

    /// <summary>阶段性进度上下文：基础百分比 + 阶段内子进度线性插值。</summary>
    sealed class ProgressContext
    {
        readonly Action<MapProgress>? _sink;
        int _base;
        public long Tokens;   // 本次自动化累计 token 消耗（附加在进度消息开头：状态栏尾部会被截断，开头才保证可见）
        public ProgressContext(Action<MapProgress>? sink) => _sink = sink;

        string Tag() => Tokens > 0 ? $"累计 token {Tokens:N0} · " : "";

        public void Report(int basePercent, string message)
        {
            _base = basePercent;
            _sink?.Invoke(new MapProgress { Percent = _base, Message = Tag() + message });
        }

        public void Report(int basePercent, int range, int done, int total, string message)
        {
            var sub = total > 0 ? range * done / total : 0;
            _sink?.Invoke(new MapProgress { Percent = basePercent + sub, Message = Tag() + message });
        }

        /// <summary>累计一次 LLM 调用的用量（部分供应商不返回 Total 时退化为 Prompt+Completion）</summary>
        public void AddUsage(LlmUsage u) => Tokens += u.Total > 0 ? u.Total : u.Prompt + u.Completion;
    }

    /* ---------- 缺注释补说明 ---------- */

    static async Task<bool> FillNotesAsync(AppConfig cfg, string root, AutoState state,
                                           Dictionary<string, NoteEntry> notes,
                                           Dictionary<string, HashSet<string>>? byRefs,
                                           ProgressContext ctx, CancellationToken ct)
    {
        var notesPath = Path.Combine(root, DirName, NotesName);
        var todo = new List<(string Rel, bool NoHead)>();
        foreach (var g in ProjectMap.PeekGaps(cfg))
        {
            ct.ThrowIfCancellationRequested();
            var full = Path.Combine(root, g.Rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) continue;
            var fi = new FileInfo(full);
            if (fi.Length > MaxNoteBytes) continue;
            var mtime = fi.LastWriteTimeUtc.ToString("o");
            if (state.NotesDone.TryGetValue(g.Rel, out var doneMtime) && doneMtime == mtime) continue;  // 已补且未变
            // 标记丢失但 notes.json 已有说明（旧版迁移 / 中途退出丢标记）：回填标记并跳过，避免重复请求模型
            if (!state.NotesDone.ContainsKey(g.Rel) && notes.ContainsKey(g.Rel))
            {
                state.NotesDone[g.Rel] = mtime;
                continue;
            }
            todo.Add((g.Rel, g.NoHead));
        }
        if (todo.Count == 0) return false;

        ctx.Report(30, $"补注释说明（{todo.Count} 个文件）");
        var (url, key, model) = cfg.ProviderCfg(cfg.NotesProvider);
        var client = new LLMClient(url, key, model);
        var currentProvider = cfg.NotesProvider;
        var failStreak = 0;
        var done = 0;
        for (var i = 0; i < todo.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (failStreak >= MaxFailStreak)
            {
                state.LastError = $"连续 {failStreak} 个文件补注释失败，已停止（剩余 {todo.Count - i} 个）";
                ctx.Report(30, 45, done, todo.Count, "补注释连续失败，本次停止（下轮再试）");
                break;
            }
            var (rel, noHead) = todo[i];
            var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                var note = await AskNoteAsync(client, rel, full, noHead, ctx.AddUsage, ct);
                if (note != null) notes[rel] = note;
                state.NotesDone[rel] = File.GetLastWriteTimeUtc(full).ToString("o");
                failStreak = 0;
                ctx.Report(30, 45, ++done, todo.Count, $"补注释 {done}/{todo.Count}：{rel}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                var why = ex.GetBaseException().Message;
                // 故障转移：尝试切换到下一个供应商
                var fallback = TryFallback(cfg, currentProvider, rel, ctx);
                if (!string.IsNullOrEmpty(fallback))
                {
                    currentProvider = fallback;
                    (url, key, model) = cfg.ProviderCfg(currentProvider);
                    client = new LLMClient(url, key, model);
                    try
                    {
                        var note = await AskNoteAsync(client, rel, full, noHead, ctx.AddUsage, ct);
                        if (note != null) notes[rel] = note;
                        state.NotesDone[rel] = File.GetLastWriteTimeUtc(full).ToString("o");
                        failStreak = 0;
                        ctx.Report(30, 45, ++done, todo.Count, $"补注释 {done}/{todo.Count}：{rel}（已切换至 {currentProvider}）");
                        continue;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex2)
                    {
                        why = ex2.GetBaseException().Message;
                        ctx.Report(30, 45, done, todo.Count, $"补注释失败（转移也失败） {rel}：{why}");
                    }
                }
                failStreak++;
                ctx.Report(30, 45, done, todo.Count, $"补注释失败 {rel}：{why}");
                RecordLesson(cfg, "补注释失败(" + rel + ")：" + why);   // 分析流程踩坑自动沉淀为项目经验
                System.Diagnostics.Debug.WriteLine($"[ProjectMapAuto] 补注释失败 {rel}: {ex}");
            }
            // 每批落盘一次：中断也不丢已补成果（notes.json + 跳过标记同步落盘，防中途退出丢标记导致下轮重复请求）
            if ((i + 1) % SystemCfg.NotesBatch == 0)
            {
                SaveNotes(notesPath, notes);
                SaveState(Path.Combine(root, DirName, StateName), state);
                ProjectMap.ApplyNotes(cfg, notes, byRefs);
                await Task.Delay(NotesDelayMs, ct);
            }
        }
        // 尾部不足一批时强制落盘（否则新增条目只存在于内存与 state 标记，notes.json 缺失）
        if (done > 0)
        {
            SaveNotes(notesPath, notes);
            SaveState(Path.Combine(root, DirName, StateName), state);
            ProjectMap.ApplyNotes(cfg, notes, byRefs);
        }
        return done > 0;
    }

    /* ---------- 符号级补注（方案 B：无中文标签的符号由 LLM 补说明，写 .gairr/symbol-notes.json） ---------- */

    static async Task<bool> FillSymbolNotesAsync(AppConfig cfg, string root, AutoState state,
                                                 ProgressContext ctx, CancellationToken ct)
    {
        var gaps = SymbolIndex.ListNoteGaps(cfg);
        if (gaps.Count == 0) return false;
        var cap = SystemCfg.SymbolNotesBatch > 0 ? SystemCfg.SymbolNotesBatch : gaps.Count;
        var notesPath = Path.Combine(root, DirName, SymbolNotesName);
        var notes = LoadSymbolNotes(notesPath);
        var skipped = 0;
        var todo = new List<(SymbolIndex.SymbolGap G, string Mtime)>();
        foreach (var g in gaps.Take(cap))
        {
            ct.ThrowIfCancellationRequested();
            var full = Path.Combine(root, g.Rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) continue;
            string mtime;
            try { mtime = File.GetLastWriteTimeUtc(full).ToString("o"); } catch { continue; }
            var key = g.Rel + "::" + g.Name;
            if (state.SymbolsDone.TryGetValue(key, out var doneMtime) && doneMtime == mtime) continue;  // 已补且未变
            // 标记丢失但补注已落盘（旧版退出/中途丢标记）：回填标记跳过，避免重复请求模型
            if (!state.SymbolsDone.ContainsKey(key) && notes.ContainsKey(key)) { state.SymbolsDone[key] = mtime; skipped++; continue; }
            todo.Add((g, mtime));
        }
        if (todo.Count == 0)
        {
            if (skipped > 0) SaveState(Path.Combine(root, DirName, StateName), state);   // 回填的标记也要持久化
            return false;
        }

        ctx.Report(76, 8, 0, todo.Count, $"符号级补注（{todo.Count} 个符号）");
        var (url, key2, model) = cfg.ProviderCfg(cfg.NotesProvider);
        var client = new LLMClient(url, key2, model);
        var currentProvider = cfg.NotesProvider;
        var failStreak = 0;
        var done = 0;
        for (var i = 0; i < todo.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (failStreak >= MaxFailStreak)
            {
                state.LastError = $"连续 {failStreak} 个符号补注失败，已停止（剩余 {todo.Count - i} 个）";
                ctx.Report(76, 8, done, todo.Count, "符号级补注连续失败，本次停止（下轮再试）");
                break;
            }
            var (g, mtime) = todo[i];
            var key = g.Rel + "::" + g.Name;
            var full = Path.Combine(root, g.Rel.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                var note = await AskSymbolNoteAsync(client, g, HeadSnippet(full), ctx.AddUsage, ct);
                if (note != null) notes[key] = note;
                state.SymbolsDone[key] = mtime;
                failStreak = 0;
                ctx.Report(76, 8, ++done, todo.Count, $"符号级补注 {done}/{todo.Count}：{g.Container}.{g.Name}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                var why = ex.GetBaseException().Message;
                var fallback = TryFallback(cfg, currentProvider, g.Name, ctx);
                if (!string.IsNullOrEmpty(fallback))
                {
                    currentProvider = fallback;
                    (url, key2, model) = cfg.ProviderCfg(currentProvider);
                    client = new LLMClient(url, key2, model);
                    try
                    {
                        var note = await AskSymbolNoteAsync(client, g, HeadSnippet(full), ctx.AddUsage, ct);
                        if (note != null) notes[key] = note;
                        state.SymbolsDone[key] = mtime;
                        failStreak = 0;
                        ctx.Report(76, 8, ++done, todo.Count, $"符号级补注 {done}/{todo.Count}：{g.Container}.{g.Name}（已切换至 {currentProvider}）");
                        continue;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex2)
                    {
                        why = ex2.GetBaseException().Message;
                        ctx.Report(76, 8, done, todo.Count, $"符号级补注失败（转移也失败） {g.Name}：{why}");
                    }
                }
                failStreak++;
                ctx.Report(76, 8, done, todo.Count, $"符号级补注失败 {g.Name}：{why}");
                RecordLesson(cfg, "符号级补注失败(" + g.Name + ")：" + why);
                System.Diagnostics.Debug.WriteLine($"[ProjectMapAuto] 符号级补注失败 {g.Name}: {ex}");
            }
            // 每批落盘一次（补注 + 跳过标记同步落盘，防中断丢成果/重复请求）
            if ((i + 1) % SystemCfg.NotesBatch == 0)
            {
                SaveSymbolNotes(notesPath, notes);
                SaveState(Path.Combine(root, DirName, StateName), state);
                await Task.Delay(NotesDelayMs, ct);
            }
        }
        if (done > 0)
        {
            SaveSymbolNotes(notesPath, notes);
            SaveState(Path.Combine(root, DirName, StateName), state);
        }
        return done > 0;
    }

    /// <summary>单符号补注：prompt 给符号名/签名/归属/文件头，要求返回严格 JSON；addUsage 累计本次调用 token。</summary>
    static async Task<string?> AskSymbolNoteAsync(LLMClient client, SymbolIndex.SymbolGap g, string head,
                                                  Action<LlmUsage> addUsage, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append("你是代码注释补全助手。下面这个代码符号缺少说明，请写一句 ≤")
          .Append(SymbolNoteMaxLen).Append(" 字的中文功能说明：它在这个项目里是做什么的（不要复述签名本身）。\n\n");
        sb.Append("文件: ").Append(g.Rel).Append('\n');
        sb.Append("符号: ").Append(g.Container.Length > 0 && g.Container != g.Name ? g.Container + "." : "").Append(g.Name).Append('\n');
        sb.Append("签名: ").Append(g.Sig.Length > 300 ? g.Sig[..300] : g.Sig).Append('\n');
        if (head.Length > 0) sb.Append("文件头说明: ").Append(head.Length > 500 ? head[..500] : head).Append('\n');
        sb.Append("只返回一个 JSON 对象，不要其他任何文字：{\"note\":\"一句话中文说明（不超过 ").Append(SymbolNoteMaxLen).Append(" 字）\"}\n");

        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = "你是严谨的项目文档工程师，只输出 JSON。" },
            new JsonObject { ["role"] = "user", ["content"] = sb.ToString() },
        };
        var resp = await client.ChatAsync(messages, new JsonArray(), ct);
        addUsage(resp.Usage);   // 无论返回是否有效都计费，先累计再校验
        var obj = ParseJsonObject(resp.Content);
        if (obj == null) throw new LlmException("模型返回不是有效 JSON");
        var note = obj["note"] ?? obj["Note"];
        if (note == null) throw new LlmException("模型返回缺少 note 字段");
        var v = note.GetValue<string>().Trim();
        if (v.Length == 0 || v.TrimStart().StartsWith("null", StringComparison.OrdinalIgnoreCase)) throw new LlmException("模型返回空说明");
        return v.Length > SymbolNoteMaxLen ? v[..SymbolNoteMaxLen] : v;
    }

    static Dictionary<string, string> LoadSymbolNotes(string path)
    {
        try
        {
            if (!File.Exists(path)) return new Dictionary<string, string>();
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject obj) return new Dictionary<string, string>();
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in obj)
                if (kv.Value is JsonValue v && v.GetValue<string>() is { Length: > 0 } s)
                    dict[kv.Key] = s;
            return dict;
        }
        catch { return new Dictionary<string, string>(); }
    }

    /// <summary>写 .gairr/symbol-notes.json：key = "rel::符号名"，值为 LLM 补充说明（不动源码）。</summary>
    static void SaveSymbolNotes(string path, Dictionary<string, string> notes)
    {
        try
        {
            var obj = new JsonObject();
            foreach (var kv in notes.OrderBy(k => k.Key, StringComparer.Ordinal)) obj[kv.Key] = kv.Value;
            File.WriteAllText(path, obj.ToJsonString(JsonOpts), new UTF8Encoding(false));
        }
        catch { /* 落盘失败不阻塞主流程 */ }
    }

    /// <summary>单文件补注释：prompt 给文件头结构与无注释方法清单，要求返回严格 JSON；addUsage 累计本次调用 token</summary>
    static async Task<NoteEntry?> AskNoteAsync(LLMClient client, string rel, string full, bool noHead,
                                               Action<LlmUsage> addUsage, CancellationToken ct)
    {
        var snippet = HeadSnippet(full);
        var unannotated = ProjectMap.ListUnannotated(full);
        var sb = new StringBuilder();
        sb.Append("你是项目文档补全助手。项目文件 ").Append(rel).Append(" 存在注释缺口，请补全说明。\n\n");
        sb.Append("文件头部（前 40 行）：\n---\n").Append(snippet.Length > 2000 ? snippet[..2000] : snippet).Append("\n---\n");
        if (unannotated.Count > 0)
            sb.Append("\n无注释方法清单：\n").Append(string.Join("\n", unannotated.Select((u, i) => (i + 1) + ". " + u))).Append('\n');
        sb.Append("\n只返回一个 JSON 对象，不要其他任何文字：\n");
        sb.Append("{\"head\":\"").Append(noHead ? "一句话说明文件职责" : "null（文件已有文件头说明）")
          .Append("\",\"methods\":{\"1\":\"第 1 个无注释方法的功能说明\",\"2\":\"第 2 个无注释方法的功能说明\"}}\n");
        sb.Append("无缺口的一侧用 null/空对象，不要编造内容。");

        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = "你是严谨的项目文档工程师，只输出 JSON。" },
            new JsonObject { ["role"] = "user", ["content"] = sb.ToString() },
        };
        var resp = await client.ChatAsync(messages, new JsonArray(), ct);
        addUsage(resp.Usage);   // 无论返回是否有效都计费，先累计再校验
        var obj = ParseJsonObject(resp.Content);
        if (obj == null) throw new LlmException("模型返回不是有效 JSON");

        var note = new NoteEntry();
        var head = obj["head"] ?? obj["Head"];
        if (head != null && head.GetValue<string>() is { Length: > 0 } hs &&
            !hs.TrimStart().StartsWith("null", StringComparison.OrdinalIgnoreCase))   // 含 "null（...）" 模板原样返回也排除
            note.Head = hs.Length > 200 ? hs[..200] : hs;
        if (obj["methods"] is JsonObject ms)
            foreach (var kv in ms)
                if (kv.Value?.GetValue<string>() is { Length: > 0 } v)
                    note.Methods[kv.Key] = v.Length > 120 ? v[..120] : v;
        if (note.Head.Length + note.Methods.Count == 0) throw new LlmException("模型返回空说明");
        return note;
    }

    static string HeadSnippet(string full)
    {
        try
        {
            var (text, _) = Phase1Tools.Decode(File.ReadAllBytes(full));
            var lines = text.Replace("\r\n", "\n").Split('\n');
            return string.Join("\n", lines.Take(40));
        }
        catch { return ""; }
    }

    /* ---------- 调用关系图谱（文件级，纯程序） ---------- */

    static readonly Regex ClassSymRe = new(@"\b(class|interface|enum|struct|record)\s+([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex UsingRe = new(@"^\s*(?:using|import)\s+(?:static\s+)?([A-Za-z_][A-Za-z0-9_.]*)\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex PyImportRe = new(@"^\s*(?:import|from)\s+([A-Za-z_][A-Za-z0-9_.]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex TsFromRe = new(@"(?:from\s+|require\s*\(\s*)['""]([^'""]+)['""]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex TsImportRe = new(@"^\s*import\s+['""]([^'""]+)['""]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /* ---------- AI 扫描判断忽略项（非源码/非资源/非配置） ---------- */

    /// <summary>LLM 扫描候选文件清单，判断哪些目录/扩展名/文件前缀是"非源码非资源"（应跳过扫描）。
    /// 结构哈希与 state.AiIgnoreHash 一致则跳过（省 token）；结果只增不删合并进 .gairr/ai-ignore.json。</summary>
    static async Task AiScanAsync(AppConfig cfg, string root, List<string> files, string structHash,
                                  AutoState state, ProgressContext ctx, CancellationToken ct)
    {
        // 结构哈希不变 → 跳过（省 token）
        if (string.Equals(state.AiIgnoreHash, structHash, StringComparison.Ordinal))
        {
            var cached = AiIgnore.Load(root);
            if (cached != null && cached.Hash == structHash)
                ctx.Report(3, $"AI 扫描：结构未变（{cached.Dirs.Count + cached.Exts.Count + cached.Prefixes.Count} 条忽略），跳过");
            return;
        }

        var (url, key, model) = cfg.ProviderCfg(cfg.NotesProvider);
        var client = new LLMClient(url, key, model);
        var currentProvider = cfg.NotesProvider;

        // 构造候选清单：按目录分组采样（防 prompt 爆炸）
        var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var total = 0;
        foreach (var f in files)
        {
            if (total >= SystemCfg.AiScanMaxFiles) break;
            var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
            if (rel.StartsWith(".gairr/", StringComparison.OrdinalIgnoreCase)) continue;  // 自身产物
            var slash = rel.LastIndexOf('/');
            var dir = slash < 0 ? "." : rel[..slash];
            if (!groups.TryGetValue(dir, out var list)) groups[dir] = list = new List<string>();
            list.Add(Path.GetFileName(rel));
            total++;
        }
        // 采样：目录数超限时按目录截断（保留前 N 个目录，每目录前 M 个文件）
        var sb = new StringBuilder();
        var dirCount = 0;
        foreach (var kv in groups.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (dirCount >= AiSampleMaxDirs) { sb.Append($"…（还有 {groups.Count - dirCount} 个目录未列出）\n"); break; }
            dirCount++;
            sb.Append(kv.Key).Append("/\n");
            var limit = Math.Min(kv.Value.Count, AiSamplePerDir);
            for (int i = 0; i < limit; i++) sb.Append("  ").Append(kv.Value[i]).Append('\n');
            if (kv.Value.Count > limit) sb.Append("  …（共 ").Append(kv.Value.Count).Append(" 个文件）\n");
        }

        // prompt 超限再截断
        var prompt = sb.ToString();
        if (prompt.Length > AiPromptMaxChars) prompt = prompt[..AiPromptMaxChars] + "\n…（已截断）";

        var userMsg =
            "你是项目扫描助手。以下是项目候选文件清单（按目录分组，每目录最多列 3 个文件名示例）。\n" +
            "请判断哪些目录/扩展名/文件前缀属于\"非源码、非资源、非配置\"，应被扫描工具跳过。\n\n" +
            "判断标准：\n" +
            "- 源码：.cs/.java/.py/.js/.ts/.go/.cpp/.h/.c/.rb/.php/.vue/.sh/.bat 等（保留）\n" +
            "- 配置：.ini/.json/.xml/.yaml/.yml/.toml/.properties/.env/.config（保留）\n" +
            "- 资源/媒体：.png/.jpg/.jpeg/.gif/.svg/.webp/.bmp/.ico/.mp4/.mp3/.wav/.ttf/.woff/.pdf 等（跳过）\n" +
            "- 构建产物：bin/obj/dist/build/target/out/release/debug 目录及其内容（跳过）\n" +
            "- 依赖/缓存：node_modules/vendor/.venv/__pycache__/Pods 目录（跳过）\n" +
            "- 归档/数据：.zip/.rar/.7z/.tar/.gz/.db/.sqlite/.pkl/.onnx/.pt/.bin/.log 等（跳过）\n" +
            "- 日志：logs/ 目录、*.log 文件（跳过）\n\n" +
            "只返回一个 JSON 对象，不要其他文字：\n" +
            "{\"dirs\":[\"相对路径前缀，命中整棵子树跳过\"],\"exts\":[\"扩展名，小写带点\"],\"prefixes\":[\"文件名前缀，无扩展名文件用\"]}\n" +
            "没有的项用空数组。不要编造清单外的目录/扩展名。\n\n" +
            "候选文件清单：\n" + prompt;

        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = "你是严谨的项目扫描助手，只输出 JSON。" },
            new JsonObject { ["role"] = "user", ["content"] = userMsg },
        };

        AiIgnore? result = null;
        try
        {
            var resp = await client.ChatAsync(messages, new JsonArray(), ct);
            ctx.AddUsage(resp.Usage);
            var obj = ParseJsonObject(resp.Content);
            if (obj == null) throw new LlmException("模型返回不是有效 JSON");
            result = new AiIgnore();
            if (obj["dirs"] is JsonArray da)
                foreach (var e in da)
                {
                    var s = e?.GetValue<string>() ?? "";
                    s = s.Replace('\\', '/').Trim().TrimEnd('/');
                    if (s.Length > 0) result.Dirs.Add(s);
                }
            if (obj["exts"] is JsonArray ea)
                foreach (var e in ea)
                {
                    var s = (e?.GetValue<string>() ?? "").Trim().ToLowerInvariant();
                    if (s.Length > 0 && !s.StartsWith('.')) s = "." + s;
                    if (s.Length > 0) result.Exts.Add(s);
                }
            if (obj["prefixes"] is JsonArray pa)
                foreach (var e in pa)
                {
                    var s = (e?.GetValue<string>() ?? "").Trim();
                    if (s.Length > 0) result.Prefixes.Add(s);
                }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // 故障转移：换一个供应商重试一次
            var fallback = TryFallback(cfg, currentProvider, "AI扫描", ctx);
            if (fallback != null)
            {
                currentProvider = fallback;
                (url, key, model) = cfg.ProviderCfg(currentProvider);
                var client2 = new LLMClient(url, key, model);
                var resp = await client2.ChatAsync(messages, new JsonArray(), ct);
                ctx.AddUsage(resp.Usage);
                var obj = ParseJsonObject(resp.Content);
                if (obj == null) throw new LlmException("重试仍非有效 JSON");
                result = new AiIgnore();
                if (obj["dirs"] is JsonArray da)
                    foreach (var e in da)
                    {
                        var s = (e?.GetValue<string>() ?? "").Replace('\\', '/').Trim().TrimEnd('/');
                        if (s.Length > 0) result.Dirs.Add(s);
                    }
                if (obj["exts"] is JsonArray ea)
                    foreach (var e in ea)
                    {
                        var s = (e?.GetValue<string>() ?? "").Trim().ToLowerInvariant();
                        if (s.Length > 0 && !s.StartsWith('.')) s = "." + s;
                        if (s.Length > 0) result.Exts.Add(s);
                    }
                if (obj["prefixes"] is JsonArray pa)
                    foreach (var e in pa)
                    {
                        var s = (e?.GetValue<string>() ?? "").Trim();
                        if (s.Length > 0) result.Prefixes.Add(s);
                    }
            }
            if (result == null) throw new LlmException("AI 扫描失败：" + ex.Message);
        }

        if (result == null || result.Dirs.Count + result.Exts.Count + result.Prefixes.Count == 0)
        {
            ctx.Report(3, "AI 扫描：未发现应忽略项");
            state.AiIgnoreHash = structHash;   // 登记 hash，避免下次重判
            return;
        }

        // 只增不删合并进已有 ai-ignore.json
        var existing = AiIgnore.Load(root) ?? new AiIgnore();
        existing.MergeFrom(result);
        existing.Hash = structHash;
        try { existing.Save(root); state.AiIgnoreHash = structHash; }
        catch (Exception ex) { ctx.Report(3, "AI 扫描：写 ai-ignore.json 失败 " + ex.Message); }

        ctx.Report(3, $"AI 扫描完成（{existing.Dirs.Count} 目录 + {existing.Exts.Count} 扩展名 + {existing.Prefixes.Count} 前缀，已写 .gairr/ai-ignore.json）");
    }

    /// <summary>扫描文件级引用边：返回 (to: 文件→被引文件集, by: 文件→引用它的文件集)</summary>
    static (Dictionary<string, HashSet<string>> To, Dictionary<string, HashSet<string>> By) ScanRefs(
        string root, List<string> files, CancellationToken ct)
    {
        var to = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var symbols = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var contents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var total = 0L;

        // 第一趟：读入可扫描文件文本，提取定义符号（类声明名 + 文件名主干）
        foreach (var f in files)
        {
            if (contents.Count >= MaxRefFiles || total >= MaxTotalBytes) break;
            var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
            if (IsAutoRel(rel)) continue;
            try
            {
                var fi = new FileInfo(f);
                if (fi.Length > MaxScanBytes || fi.Length == 0) continue;
                var (text, _) = Phase1Tools.Decode(File.ReadAllBytes(f));
                if (text.IndexOf('\0') >= 0) continue;   // 二进制文件
                total += text.Length;
                contents[rel] = text;
                foreach (Match m in ClassSymRe.Matches(text)) AddSymbol(symbols, m.Groups[2].Value, rel);
                AddSymbol(symbols, Path.GetFileNameWithoutExtension(rel), rel);
            }
            catch { }
        }

        // 全文符号词匹配正则在符号集固定后构建一次，全文件复用
        Regex? wordRe = null;
        if (symbols.Count > 0)
        {
            var pat = string.Join("|", symbols.Keys.Take(MaxSymbolPattern).Select(Regex.Escape));
            wordRe = new Regex(@"\b(" + pat + @")\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        }
        var known = new HashSet<string>(contents.Keys, StringComparer.OrdinalIgnoreCase);

        // 第二趟：逐文件解析 using/import/require + 全文符号引用
        foreach (var kv in contents)
        {
            var rel = kv.Key;
            var text = kv.Value;
            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Trim();
                Match m;
                if ((m = UsingRe.Match(line)).Success || (m = PyImportRe.Match(line)).Success)
                    ResolveImport(root, rel, m.Groups[1].Value, to, known, symbols);
                else if ((m = TsFromRe.Match(line)).Success || (m = TsImportRe.Match(line)).Success)
                    ResolveImport(root, rel, m.Groups[1].Value, to, known, symbols);
            }
            if (wordRe != null)
                foreach (Match w in wordRe.Matches(text))
                    foreach (var sf in symbols[w.Groups[1].Value])
                        AddEdge(to, rel, sf);
        }

        // 由“引用谁”推导“被谁引用”
        var by = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in to)
            foreach (var t in kv.Value)
            {
                if (!by.TryGetValue(t, out var set)) by[t] = set = new(StringComparer.OrdinalIgnoreCase);
                set.Add(kv.Key);
            }
        return (to, by);
    }

    /// <summary>解析 using/import/from 目标：相对路径解析到文件；点分尝试逐级路径与符号段匹配</summary>
    static void ResolveImport(string root, string fromRel, string target,
        Dictionary<string, HashSet<string>> to, HashSet<string> known,
        Dictionary<string, HashSet<string>> symbols)
    {
        var t = target.Trim().TrimEnd(';', ',').Trim();
        if (t.Length == 0) return;
        if (t.StartsWith("./") || t.StartsWith("../") || t.StartsWith('/'))
        {
            // 相对引用：以当前文件所在目录为基准解析，再尝试补扩展名 / index 文件
            var norm = RelResolve(root, fromRel, t);
            if (norm == null) return;
            foreach (var ext in ImportExts)
                if (known.Contains(norm + ext)) { AddEdge(to, fromRel, norm + ext); return; }
            foreach (var ext in ImportExts)
                if (known.Contains(norm + "/index" + ext)) { AddEdge(to, fromRel, norm + "/index" + ext); return; }
            var pref = norm + "/";
            foreach (var k in known)
                if (k.StartsWith(pref, StringComparison.OrdinalIgnoreCase)) AddEdge(to, fromRel, k);
            return;
        }
        // 点分：路径逐级尝试（a.b.c → a/b/c.ext、a/b.ext）+ 段名匹配符号
        var segs = t.Split('.');
        for (var i = segs.Length; i >= 1; i--)
        {
            var p = string.Join("/", segs.Take(i));
            foreach (var ext in ImportExts)
                if (known.Contains(p + ext)) { AddEdge(to, fromRel, p + ext); return; }
            if (known.Contains(p + "/__init__.py")) { AddEdge(to, fromRel, p + "/__init__.py"); return; }
        }
        foreach (var s in segs)
            if (symbols.TryGetValue(s, out var sf))
                foreach (var f in sf) AddEdge(to, fromRel, f);
    }

    static void AddSymbol(Dictionary<string, HashSet<string>> symbols, string name, string rel)
    {
        if (name.Length < 2) return;
        if (!symbols.TryGetValue(name, out var set)) symbols[name] = set = new(StringComparer.OrdinalIgnoreCase);
        set.Add(rel);
    }

    static void AddEdge(Dictionary<string, HashSet<string>> to, string from, string toRel)
    {
        if (string.Equals(from, toRel, StringComparison.OrdinalIgnoreCase)) return;   // 自引用不算
        if (!to.TryGetValue(from, out var set)) to[from] = set = new(StringComparer.OrdinalIgnoreCase);
        set.Add(toRel);
    }

    /// <summary>把相对引用解析为相对项目根的正规化路径（越界返回 null）</summary>
    static string? RelResolve(string root, string fromRel, string target)
    {
        try
        {
            var fromDir = fromRel.Contains('/') ? fromRel[..fromRel.LastIndexOf('/')] : "";
            var full = Path.GetFullPath(Path.Combine(root, fromDir, target.Trim()));
            var rel = Path.GetRelativePath(root, full).Replace('\\', '/');
            return rel.StartsWith("../") ? null : rel;
        }
        catch { return null; }
    }

    /* ---------- 架构/功能文档 ---------- */

    /* ---------- 自动维护文档清单 ---------- */

    /// <summary>内置文档 rel（框架私有区，自动挂清单，无需手工标记）</summary>
    static string BuiltinRel(string name) => $"{DirName}/{DocsDir}/{name}";
    static bool IsBuiltinDoc(string rel) =>
        string.Equals(rel, BuiltinRel(ArchName), StringComparison.OrdinalIgnoreCase) ||
        string.Equals(rel, BuiltinRel(FeatName), StringComparison.OrdinalIgnoreCase);

    /// <summary>确保两份内置文档在清单内（幂等）</summary>
    static void EnsureBuiltinDocs(AutoState state)
    {
        foreach (var rel in new[] { BuiltinRel(ArchName), BuiltinRel(FeatName) })
            if (!state.DocsAuto.Any(d => string.Equals(d, rel, StringComparison.OrdinalIgnoreCase)))
                state.DocsAuto.Add(rel);
    }

    static string NormRel(string rel) => rel.Replace('\\', '/').TrimStart('/');

    /// <summary>当前项目的自动维护文档清单（正斜杠 rel 副本，含内置）</summary>
    public static List<string> GetAutoDocs(AppConfig cfg)
    {
        try
        {
            var root = cfg.ProjectRoot;
            if (root.Length == 0) return new List<string>();
            var state = LoadState(Path.Combine(root, DirName, StateName));
            EnsureBuiltinDocs(state);
            return state.DocsAuto.ToList();
        }
        catch { return new List<string>(); }
    }

    /// <summary>查询某文档是否在自动维护清单内（rel 可用平台分隔符）</summary>
    public static bool IsAutoDoc(AppConfig cfg, string rel) =>
        GetAutoDocs(cfg).Any(d => string.Equals(d, NormRel(rel), StringComparison.OrdinalIgnoreCase));

    /// <summary>把可见目录下的 .md 加入/移出自动维护清单；内置文档由框架管理不接受手工操作。<br/>
    /// 返回操作结果文案（供状态栏显示）。</summary>
    public static string ToggleAutoDoc(AppConfig cfg, string rel, bool add)
    {
        var norm = NormRel(rel);
        if (!norm.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return "仅支持 .md 文档";
        if (IsBuiltinDoc(norm)) return "内置文档由框架自动维护，无需手工操作";
        if (norm.StartsWith(DirName + "/", StringComparison.OrdinalIgnoreCase)) return ".gairr 为框架缓存区，请把文档放到可见目录";
        try
        {
            var root = cfg.ProjectRoot;
            if (root.Length == 0) return "未打开项目";
            var statePath = Path.Combine(root, DirName, StateName);
            var state = LoadState(statePath);
            EnsureBuiltinDocs(state);
            var idx = state.DocsAuto.FindIndex(d => string.Equals(d, norm, StringComparison.OrdinalIgnoreCase));
            if (add)
            {
                if (idx >= 0) return "已在清单中：" + norm;
                state.DocsAuto.Add(norm);
                SaveState(statePath, state);
                return "已加入自动维护，下次\"重建地图\"生效：" + norm;
            }
            if (idx < 0) return "不在清单中：" + norm;
            state.DocsAuto.RemoveAt(idx);
            SaveState(statePath, state);
            return "已移出自动维护：" + norm;
        }
        catch (Exception ex) { return "操作失败：" + ex.Message; }
    }

    /// <summary>刷新单份自动维护文档（内置与用户文档共用）：内置文档旧文存在时走"旧文即提纲"增量刷新、
    /// 不存在才按固定模板首生成；用户文档恒走"旧文即提纲"。失败自动故障转移一次；返回是否成功写盘。</summary>
    static async Task<bool> RefreshDocAsync(AppConfig cfg, string root, string map, string rel,
                                            LLMClient client, ProgressContext ctx, CancellationToken ct)
    {
        var path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        var old = File.Exists(path) ? File.ReadAllText(path) : "";
        string? text;
        try
        {
            // 旧文存在 → 按项目变化刷新（保留原结构与主题）；否则按模板首生成
            text = await AskDocForRel(client, rel, old, root, map, ctx.AddUsage, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // 故障转移（TryFallback 内部已 SetNotesProvider 记住切换）后重试一次
            if (TryFallback(cfg, cfg.NotesProvider, rel, ctx) == null) return false;
            var (url, key, model) = cfg.ProviderCfg(cfg.NotesProvider);
            var c2 = new LLMClient(url, key, model);
            try
            {
                text = await AskDocForRel(c2, rel, old, root, map, ctx.AddUsage, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch { return false; }
        }
        if (string.IsNullOrWhiteSpace(text)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return true;
    }

    /// <summary>按文档类型选择生成方式：旧文存在走"旧文即提纲"增量刷新，否则按固定模板首生成</summary>
    static Task<string?> AskDocForRel(LLMClient client, string rel, string old, string root, string map,
                                      Action<LlmUsage> addUsage, CancellationToken ct)
        => old.Length > 0
            ? AskDocUpdateAsync(client, rel, old, map, addUsage, ct)
            : AskDocAsync(client, BuiltinKind(rel), root, map, addUsage, ct);

    /// <summary>内置文档类型（architecture/features）；用户文档走旧文即提纲不会用到</summary>
    static string BuiltinKind(string rel) =>
        rel.EndsWith(ArchName, StringComparison.OrdinalIgnoreCase) ? "architecture" : "features";

    /// <summary>按清单全量刷新自动维护文档：内置与用户文档统一走"旧文即提纲"（按项目变化改写过时内容）；
    /// 单个失败保留旧文件只记日志；用户文档已不存在则从清单移除。完成后才更新 DocsHash。</summary>
    static async Task RebuildDocsAsync(AppConfig cfg, string root, string map, int docCount,
                                       string structHash, AutoState state, ProgressContext ctx, CancellationToken ct)
    {
        EnsureBuiltinDocs(state);
        var list = state.DocsAuto.ToList();
        var (url, key, model) = cfg.ProviderCfg(cfg.NotesProvider);
        var client = new LLMClient(url, key, model);
        var removed = false;
        for (var i = 0; i < list.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var rel = list[i];
            ctx.Report(85, 14, i, list.Count, $"刷新文档（{i + 1}/{list.Count}）：{rel}");
            var path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!IsBuiltinDoc(rel) && !File.Exists(path))
            {   // 用户文档已被删除：移出清单
                state.DocsAuto.RemoveAll(d => string.Equals(d, rel, StringComparison.OrdinalIgnoreCase));
                removed = true;
                continue;
            }
            var ok = await RefreshDocAsync(cfg, root, map, rel, client, ctx, ct);
            if (!ok) ctx.Report(85, $"文档刷新失败，保留旧版：{rel}");
            try { await Task.Delay(NotesDelayMs, ct); } catch (OperationCanceledException) { throw; }
        }
        if (removed) ctx.Report(98, "已把不存在的文档移出清单");
        state.DocsHash = structHash;
        state.DocsCount = docCount;
        ctx.Report(100, $"自动维护文档已刷新（清单 {state.DocsAuto.Count} 份）");
    }

    /// <summary>用户文档刷新的提示词：旧文即提纲——保留章节结构与写作意图，按项目现状改写过时内容、补入新增模块</summary>
    static async Task<string?> AskDocUpdateAsync(LLMClient client, string rel, string old, string map,
                                                 Action<LlmUsage> addUsage, CancellationToken ct)
    {
        if (old.Length > 8000) old = old[..8000] + "\n…（原文过长已截断）";
        var user = $"以下是最新项目地图（目录树 + 每文件功能摘要）：\n{map}\n\n" +
                   $"以下是现有文档《{Path.GetFileName(rel)}》：\n{old}\n\n" +
                   "请根据项目现状更新该文档：保留原有章节结构与写作意图，改写过时内容，补入新增模块/功能，" +
                   "不引入与原文主题无关的内容。输出更新后的完整 Markdown，1500 字以内，中文。";
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = "你是资深软件架构师，擅长按既有文档的结构与意图刷新内容，只输出 Markdown。" },
            new JsonObject { ["role"] = "user", ["content"] = user },
        };
        var resp = await client.ChatAsync(messages, new JsonArray(), ct);
        addUsage(resp.Usage);
        return resp.Content;
    }

    /// <summary>日常调度路径：内置文档不存在→按模板首生成；已存在且结构 hash 变化→静默走"旧文即提纲"增量刷新，
    /// 让文档随项目变化而变化（小步迭代不再需要点"重建地图"）。失败保留旧版并登记 LastError，下次重试。</summary>
    static async Task EnsureDocsAsync(AppConfig cfg, string root, string map, int docCount,
                                      string structHash, AutoState state, ProgressContext ctx, CancellationToken ct)
    {
        var docsDir = Path.Combine(root, DirName, DocsDir);
        var arch = Path.Combine(docsDir, ArchName);
        var feat = Path.Combine(docsDir, FeatName);
        EnsureBuiltinDocs(state);

        var stale = File.Exists(arch) && File.Exists(feat) &&
                    !string.Equals(state.DocsHash, structHash, StringComparison.Ordinal);
        if (File.Exists(arch) && File.Exists(feat) && !stale) return;   // 全新：无事可做

        ctx.Report(85, stale ? "项目结构有变化，增量刷新架构/功能文档…" : "首次生成架构/功能文档（大模型分析）…");
        var (url, key, model) = cfg.ProviderCfg(cfg.NotesProvider);
        var client = new LLMClient(url, key, model);
        Directory.CreateDirectory(docsDir);
        var ok = true;
        // 架构文档：缺文件→模板首生成；已存在且过时→旧文即提纲增量刷新
        if (File.Exists(arch) || !stale)
            ok &= await RefreshDocAsync(cfg, root, map, BuiltinRel(ArchName), client, ctx, ct);
        // 功能文档
        if (File.Exists(feat) || !stale)
            ok &= await RefreshDocAsync(cfg, root, map, BuiltinRel(FeatName), client, ctx, ct);
        if (!ok)
        {
            state.LastError = "文档刷新失败（保留旧版）";
            ctx.Report(85, stale ? "文档增量刷新失败，保留旧版，下次启动重试" : "文档生成失败，下次启动重试");
            return;
        }
        state.DocsHash = structHash;
        state.DocsCount = docCount;
        ctx.Report(100, stale ? "架构/功能文档已按项目变化增量刷新（.gairr/docs/）" : "架构/功能文档已生成（.gairr/docs/）");
    }

    static async Task<string?> AskDocAsync(LLMClient client, string kind, string root, string map,
                                           Action<LlmUsage> addUsage, CancellationToken ct)
    {
        var user = kind == "architecture"
            ? $"项目根目录：{root}\n\n以下是项目地图（目录树 + 每文件功能摘要）：\n{map}\n\n" +
              "请撰写《技术架构说明》Markdown：一、总体架构与分层；二、模块职责；三、关键流程（启动/任务执行/数据流）；" +
              "四、扩展与注意事项。800 字以内，用标题与列表，中文。"
            : $"项目根目录：{root}\n\n以下是项目地图（目录树 + 每文件功能摘要）：\n{map}\n\n" +
              "请撰写《系统功能说明》Markdown：一、功能清单（按模块分组的列表）；二、核心交互流程；" +
              "三、配置与运行方式。800 字以内，用标题与列表，中文。";
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = "你是资深软件架构师，擅长从代码结构撰写简洁准确的中文文档，只输出 Markdown。" },
            new JsonObject { ["role"] = "user", ["content"] = user },
        };
        var resp = await client.ChatAsync(messages, new JsonArray(), ct);
        addUsage(resp.Usage);
        return resp.Content;
    }

    /* ---------- 状态/说明/图谱 持久化 ---------- */

    static AutoState LoadState(string path)
    {
        var s = new AutoState();
        try
        {
            if (!File.Exists(path)) return s;
            var obj = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (obj == null) return s;
            s.RefsHash = (string?)obj["refsHash"] ?? "";
            s.DocsHash = (string?)obj["docsHash"] ?? "";
            s.DocsCount = obj["docsCount"]?.GetValue<int>() ?? 0;
            s.LastError = (string?)obj["lastError"] ?? "";
            if (obj["notesDone"] is JsonObject nd)
                foreach (var kv in nd)
                    if (kv.Value is JsonNode v) s.NotesDone[kv.Key] = v.GetValue<string>();
            if (obj["symbolsDone"] is JsonObject sd)
                foreach (var kv in sd)
                    if (kv.Value is JsonNode v2) s.SymbolsDone[kv.Key] = v2.GetValue<string>();
            if (obj["lessons"] is JsonArray ls)
                foreach (var v in ls)
                    if (v is JsonNode jn && jn.GetValue<string>() is { Length: > 0 } t)
                        s.Lessons.Add(t);
            if (obj["docsAuto"] is JsonArray da)
                foreach (var v in da)
                    if (v is JsonNode jn2 && jn2.GetValue<string>() is { Length: > 0 } d)
                        s.DocsAuto.Add(d);
        }
        catch { /* 损坏按新状态 */ }
        return s;
    }

    static void SaveState(string path, AutoState s)
    {
        try
        {
            var nd = new JsonObject();
            foreach (var kv in s.NotesDone) nd[kv.Key] = kv.Value;
            var sd = new JsonObject();
            foreach (var kv in s.SymbolsDone) sd[kv.Key] = kv.Value;
            var ls = new JsonArray();
            foreach (var l in s.Lessons) ls.Add(l);
            var da = new JsonArray();
            foreach (var d in s.DocsAuto) da.Add(d);
            var obj = new JsonObject
            {
                ["ver"] = 1,
                ["refsHash"] = s.RefsHash,
                ["docsHash"] = s.DocsHash,
                ["docsCount"] = s.DocsCount,
                ["lastError"] = s.LastError,
                ["notesDone"] = nd,
                ["symbolsDone"] = sd,
                ["lessons"] = ls,
                ["docsAuto"] = da,
            };
            File.WriteAllText(path, obj.ToJsonString(JsonOpts), new UTF8Encoding(false));
        }
        catch { }
    }

    static void SaveNotes(string path, Dictionary<string, NoteEntry> notes)
    {
        try
        {
            var obj = new JsonObject();
            foreach (var kv in notes)
            {
                var ms = new JsonObject();
                foreach (var m in kv.Value.Methods) ms[m.Key] = m.Value;
                obj[kv.Key] = new JsonObject { ["head"] = kv.Value.Head, ["methods"] = ms };
            }
            File.WriteAllText(path, obj.ToJsonString(JsonOpts), new UTF8Encoding(false));
        }
        catch { }
    }

    static bool SaveRefs(string path, Dictionary<string, HashSet<string>> to)
    {
        try
        {
            var by = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in to)
                foreach (var t in kv.Value)
                {
                    if (!by.TryGetValue(t, out var set)) by[t] = set = new(StringComparer.OrdinalIgnoreCase);
                    set.Add(kv.Key);
                }
            var obj = new JsonObject();
            foreach (var rel in to.Keys.Union(by.Keys).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var toArr = new JsonArray();
                if (to.TryGetValue(rel, out var ts))
                    foreach (var t in ts.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) toArr.Add(t);
                var byArr = new JsonArray();
                if (by.TryGetValue(rel, out var bs))
                    foreach (var b in bs.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) byArr.Add(b);
                obj[rel] = new JsonObject { ["to"] = toArr, ["by"] = byArr };
            }
            File.WriteAllText(path, obj.ToJsonString(JsonOpts), new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex)
        {
            try
            {
                System.IO.File.AppendAllText(Path.Combine(Path.GetDirectoryName(path) ?? ".", "refs_error.log"),
                    ex.ToString() + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }
            return false;
        }
    }

    /* ---------- 工具 ---------- */

    static List<string> SafeFiles(string root)
    {
        try { return Phase1Tools.EnumerateFiles(root).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList(); }
        catch { return new List<string>(); }
    }

    /// <summary>结构签名：文件数 + 文件路径与 mtime 拼接的 SHA256（判断图谱/文档增量）</summary>
    static (int Count, string Hash) StructureHash(string root, List<string> files)
    {
        var count = 0;
        var sb = new StringBuilder();
        foreach (var f in files)
        {
            var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
            if (IsAutoRel(rel)) continue;
            try { sb.Append(rel).Append('|').Append(File.GetLastWriteTimeUtc(f).Ticks).Append(';'); count++; }
            catch { }
        }
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
        return (count, hash);
    }

    /// <summary>.gairr 自动产物（自身缓存/说明/图谱/文档/状态）：不参与图谱与结构统计</summary>
    static bool IsAutoRel(string rel) =>
        rel.StartsWith(".gairr/", StringComparison.OrdinalIgnoreCase);

    static bool HasApiKey(AppConfig cfg) => !string.IsNullOrEmpty(cfg.ProviderCfg(cfg.NotesProvider).ApiKey);

    /// <summary>故障转移：当前供应商失败时，返回下一个可用的供应商；
    /// 成功后会调用 cfg.SetNotesProvider 记住它，后续请求优先复用</summary>
    static string? TryFallback(AppConfig cfg, string currentProvider, string context, ProgressContext ctx)
    {
        var providers = cfg.NotesProvidersList();
        var idx = providers.FindIndex(p => p == currentProvider);
        // 从下一个开始找（绕一圈回到自己之前）
        for (var i = 1; i < providers.Count; i++)
        {
            var next = providers[(idx + i) % providers.Count];
            if (next == currentProvider) continue;
            var (url, key, model) = cfg.ProviderCfg(next);
            if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(url))
            {
                ctx.Report(-1, $"模型故障转移：{currentProvider} → {next}（{context}）");
                cfg.SetNotesProvider(next);
                return next;
            }
        }
        return null;
    }

    /// <summary>从模型回复中提取最外层 JSON 对象</summary>
    static JsonObject? ParseJsonObject(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var i = s.IndexOf('{');
        var j = s.LastIndexOf('}');
        if (i < 0 || j <= i) return null;
        try { return JsonNode.Parse(s[i..(j + 1)]) as JsonObject; } catch { return null; }
    }
}