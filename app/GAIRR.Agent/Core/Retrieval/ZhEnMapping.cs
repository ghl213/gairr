using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GAIRR.Core;

/// <summary>
/// 中英文映射规则词典：常见编程概念的中文→英文映射，支持中文查询扩展为可能的英文符号名。
/// 三层合并：内置默认 → 项目级配置(.gairr/zh-mapping.json) → 运行时学习缓存。
/// 项目级配置热加载，改配置免重编译。
/// </summary>
public static class ZhEnMapping
{
    const int MaxExpandTerms = 12;    // 单次查询扩展词上限（防长查询展开过多，符号层逐个检索有限流）
    const string ProjectMappingName = "zh-mapping.json";   // 项目级配置名
    const string GlobalMappingName = "zh-mapping.json";    // 全局配置名（prompts/下）
    static readonly TimeSpan ReloadInterval = TimeSpan.FromMinutes(1); // 热加载检查间隔

    static DateTime _lastLoad = DateTime.MinValue;
    static readonly object Sync = new();

    /// <summary>合并后的实际使用字典（内置 + 项目级 + 运行时）</summary>
    static Dictionary<string, string[]> _effectiveMap = new();

    /// <summary>运行时学习缓存（会话级/自动学习写入，最高优先级）</summary>
    static readonly Dictionary<string, string[]> LearnedMap = new();

    static ZhEnMapping()
    {
        Load();
    }

    /* ---------- 三层配置加载 ---------- */

    /// <summary>加载所有配置层：内置默认 → 全局配置 → 项目级配置。后加载覆盖先加载。</summary>
    static void Load()
    {
        var merged = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        // 1. 内置默认（硬编码兜底）
        foreach (var kv in DefaultMap)
            merged[kv.Key] = kv.Value;

        // 2. 全局配置（exe 同目录 prompts/zh-mapping.json）
        var globalPath = Path.Combine(AppContext.BaseDirectory, "prompts", GlobalMappingName);
        MergeFromFile(globalPath, merged);

        // 3. 项目级配置（.gairr/zh-mapping.json）
        var projectRoot = AppConfig.ProjectRootStatic;
        if (!string.IsNullOrEmpty(projectRoot))
        {
            var projectPath = Path.Combine(projectRoot, ".gairr", ProjectMappingName);
            MergeFromFile(projectPath, merged);
        }

        // 4. 运行时学习缓存（最高优先级）
        lock (LearnedMap)
        {
            foreach (var kv in LearnedMap)
                merged[kv.Key] = kv.Value;
        }

        _effectiveMap = merged;
        _lastLoad = DateTime.Now;
    }

    /// <summary>从 JSON 文件合并映射（文件不存在静默跳过）</summary>
    static void MergeFromFile(string path, Dictionary<string, string[]> target)
    {
        try
        {
            if (!File.Exists(path)) return;
            var json = File.ReadAllText(path);
            var node = JsonNode.Parse(json) as JsonObject;
            if (node == null) return;

            foreach (var kv in node)
            {
                if (kv.Value is JsonArray arr)
                {
                    var values = arr.Select(v => v?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToArray();
                    if (values.Length > 0)
                        target[kv.Key] = values;
                }
            }
        }
        catch { /* 配置文件损坏不影响主流程 */ }
    }

    /// <summary>闲时检查是否需要重载（文件变更或学习缓存更新）</summary>
    static void EnsureFresh()
    {
        if (DateTime.Now - _lastLoad < ReloadInterval) return;
        lock (Sync)
        {
            if (DateTime.Now - _lastLoad < ReloadInterval) return;
            Load();
        }
    }

    /* ---------- 公共 API ---------- */

    /// <summary>将中文查询扩展为可能的英文符号名列表。
    /// 子串命中：中文自然语言查询无空格分词（"在哪里被调用""切成片""一字一字出现"是连续串），
    /// 整词精确匹配映射不到任何键；查询文本包含概念键即扩展，短语中的概念词才能激活。</summary>
    public static List<string> ExpandQuery(string zhQuery)
    {
        EnsureFresh();
        var result = new List<string>();
        // 原始英文词优先（用户直呼符号名最可靠）
        result.AddRange(SplitChinese(zhQuery).Where(w => w.All(c => char.IsLetter(c) && c <= 127)));
        // 概念子串：字典序遍历（插入序），长查询可能命中多键，超上限截断
        foreach (var kv in _effectiveMap)
            if (zhQuery.Contains(kv.Key, StringComparison.OrdinalIgnoreCase))
                result.AddRange(kv.Value);
        return result.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxExpandTerms).ToList();
    }

    /// <summary>运行时学习：将新映射加入内存缓存（立即生效，下次热加载时持久化）</summary>
    public static void Learn(string zhConcept, params string[] enSymbols)
    {
        if (zhConcept.Length < 2 || enSymbols.Length == 0) return;
        lock (LearnedMap)
        {
            LearnedMap[zhConcept] = enSymbols.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        // 触发立即重载
        lock (Sync) _lastLoad = DateTime.MinValue;
    }

    /// <summary>批量运行时学习</summary>
    public static void LearnMany(Dictionary<string, string[]> mappings)
    {
        lock (LearnedMap)
        {
            foreach (var kv in mappings)
                if (kv.Key.Length >= 2 && kv.Value.Length > 0)
                    LearnedMap[kv.Key] = kv.Value.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        lock (Sync) _lastLoad = DateTime.MinValue;
    }

    /// <summary>将运行时学习缓存持久化到项目级配置文件</summary>
    public static void PersistToProject(string projectRoot)
    {
        if (string.IsNullOrEmpty(projectRoot)) return;
        try
        {
            var dir = Path.Combine(projectRoot, ".gairr");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, ProjectMappingName);

            Dictionary<string, string[]> toSave;
            lock (LearnedMap)
            {
                if (LearnedMap.Count == 0) return;
                toSave = new Dictionary<string, string[]>(LearnedMap, StringComparer.OrdinalIgnoreCase);
            }

            var obj = new JsonObject();
            foreach (var kv in toSave.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                var arr = new JsonArray();
                foreach (var v in kv.Value) arr.Add(v);
                obj[kv.Key] = arr;
            }

            var opts = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            File.WriteAllText(path, obj.ToJsonString(opts), new UTF8Encoding(false));
        }
        catch { /* 持久化失败不影响主流程 */ }
    }

    /// <summary>获取当前生效的映射统计（调试用）</summary>
    public static string Stats()
    {
        EnsureFresh();
        return $"ZhEnMapping: 内置 {DefaultMap.Count} 条, 生效 {_effectiveMap.Count} 条 (含运行时学习 {LearnedMap.Count} 条)";
    }

    /* ---------- 内置默认词典 ---------- */

    static readonly Dictionary<string, string[]> DefaultMap = new()
    {
        // 数据持久化
        ["断点"] = new[] { "Checkpoint", "Breakpoint", "SavePoint" },
        ["续行"] = new[] { "Resume", "Continue", "Restore" },
        ["持久化"] = new[] { "Persist", "Save", "Store", "Serialize" },
        ["恢复"] = new[] { "Resume", "Restore", "Recover", "Reload" },
        ["保存"] = new[] { "Save", "Persist", "Store", "Write" },
        ["加载"] = new[] { "Load", "Read", "Restore", "Init" },
        ["缓存"] = new[] { "Cache", "Buffer", "Memo", "Lazy" },

        // 检索定位
        ["索引"] = new[] { "Index", "Search", "Query", "Find" },
        ["检索"] = new[] { "Search", "Query", "Find", "Locate", "Retrieve" },
        ["搜索"] = new[] { "Search", "Grep", "Find", "Query" },
        ["定位"] = new[] { "Locate", "Position", "Resolve", "Find" },
        ["切片"] = new[] { "Slice", "Chunk", "Extract", "Segment" },
        ["提取"] = new[] { "Extract", "Pull", "Fetch", "Get" },
        ["匹配"] = new[] { "Match", "Find", "Search", "Filter" },

        // 代码结构
        ["符号"] = new[] { "Symbol", "Token", "Identifier", "Name" },
        ["引用"] = new[] { "Ref", "Reference", "Refs", "Usage" },
        ["调用"] = new[] { "Call", "Invoke", "Ref", "Use" },
        ["依赖"] = new[] { "Dependency", "Ref", "Import", "Using" },
        ["图谱"] = new[] { "Graph", "Refs", "Map", "Relation" },
        ["关系"] = new[] { "Relation", "Graph", "Refs", "Dependency" },

        // 任务流程
        ["地图"] = new[] { "Map", "ProjectMap", "Tree", "Structure" },
        ["注释"] = new[] { "Note", "Comment", "Doc", "Annotation" },
        ["文档"] = new[] { "Doc", "Document", "Note", "Readme" },
        ["自动化"] = new[] { "Auto", "Automatic", "Batch", "Schedule" },
        ["验证"] = new[] { "Verify", "Validate", "Check", "Test" },
        ["构建"] = new[] { "Build", "Compile", "Make", "Pack" },
        ["编译"] = new[] { "Build", "Compile", "Make" },

        // 系统功能
        ["工具"] = new[] { "Tool", "Utils", "Helper", "Utility" },
        ["注册"] = new[] { "Registry", "Register", "Add", "Init" },
        ["配置"] = new[] { "Config", "Configuration", "Cfg", "Setting" },
        ["历史"] = new[] { "History", "Journal", "Log", "Record" },
        ["会话"] = new[] { "Session", "Conversation", "Chat", "Dialog" },
        ["任务"] = new[] { "Task", "Job", "Work", "Mission" },
        ["消息"] = new[] { "Message", "Msg", "Event", "Notification" },
        ["事件"] = new[] { "Event", "Bus", "Hook", "Callback" },
        ["总线"] = new[] { "Bus", "EventBus", "Queue", "Channel" },
        ["钩子"] = new[] { "Hook", "Callback", "Interceptor" },
        ["回调"] = new[] { "Callback", "Hook", "Delegate", "Action" },

        // UI/交互
        ["界面"] = new[] { "UI", "View", "Window", "Panel" },
        ["视图"] = new[] { "View", "UI", "Window", "Panel" },
        ["面板"] = new[] { "Panel", "View", "UI", "Window" },
        ["按钮"] = new[] { "Button", "Btn", "Control" },
        ["列表"] = new[] { "List", "Collection", "Array", "Items" },
        ["树"] = new[] { "Tree", "Node", "Hierarchy" },
        ["菜单"] = new[] { "Menu", "Nav", "Navigation" },

        // 网络/IO
        ["请求"] = new[] { "Request", "Req", "Http", "Call" },
        ["响应"] = new[] { "Response", "Resp", "Reply", "Result" },
        ["连接"] = new[] { "Connection", "Connect", "Link", "Socket" },
        ["超时"] = new[] { "Timeout", "Expire", "Deadline" },
        ["重试"] = new[] { "Retry", "Attempt", "ReTry" },
        ["流"] = new[] { "Stream", "Flow", "Pipe", "Channel" },
        ["缓冲"] = new[] { "Buffer", "Cache", "Queue", "Stream" },

        // 安全
        ["拦截"] = new[] { "Intercept", "Block", "Filter", "Guard" },
        ["认证"] = new[] { "Auth", "Authenticate", "Login", "Verify" },
        ["授权"] = new[] { "Auth", "Authorize", "Permission", "Access" },
        ["危险"] = new[] { "Danger", "Unsafe", "Risk", "Warning" },

        // 界面显示/样式（自然语言口语：标题/加粗/红色/圆点）
        ["加粗"] = new[] { "Bold", "Strong", "Weight" },
        ["红色"] = new[] { "Red", "Brush", "Foreground" },
        ["醒目"] = new[] { "Highlight", "Accent", "Bold" },
        ["显示"] = new[] { "Show", "Display", "Render", "Draw" },
        ["样式"] = new[] { "Style", "Theme", "Xaml", "Format" },
        ["标题"] = new[] { "Title", "Heading", "Header" },
        ["圆点"] = new[] { "Bullet", "Dot", "Marker" },
        ["颜色"] = new[] { "Color", "Brush", "Palette" },
        ["画刷"] = new[] { "Brush", "Pen", "Fill" },

        // 通用动作/状态（口语化查询高频词）
        ["备份"] = new[] { "Backup", "Snapshot", "Copy" },
        ["压缩"] = new[] { "Compress", "Compact", "Summarize" },
        ["摘要"] = new[] { "Summary", "Digest", "Compress" },
        ["删除"] = new[] { "Delete", "Remove", "Trash" },
        ["确认"] = new[] { "Confirm", "Ask", "Verify" },
        ["失败"] = new[] { "Fail", "Error", "Retry" },
        ["长文件"] = new[] { "Slice", "Chunk", "MaxLines" },
        ["临时"] = new[] { "Temp", "Scratch", "Temporary" },
        ["退出"] = new[] { "Exit", "Quit", "Close" },
        ["更新"] = new[] { "Refresh", "Update", "Rebuild", "Sync" },
        ["继续"] = new[] { "Resume", "Continue", "Restore" },
        ["被调用"] = new[] { "FindReferences", "Ref", "Caller" },

        // 流式/逐字（打字机效果口语）
        ["流式"] = new[] { "Stream", "Delta", "Incremental" },
        ["打字机"] = new[] { "Typewriter", "Stream", "Delta" },
        ["逐字"] = new[] { "Delta", "Stream", "Typewriter", "Char" },
        ["一字"] = new[] { "Delta", "Char", "Typewriter" },
    };

    /* ---------- 工具方法 ---------- */

    /// <summary>中文分词（简化版：连续中文字符为词，英文按空格）</summary>
    static List<string> SplitChinese(string text)
    {
        var result = new List<string>();
        var sb = new StringBuilder();

        foreach (var ch in text)
        {
            if (ch >= '\u4e00' && ch <= '\u9fff')
            {
                // 中文字符
                if (sb.Length > 0 && sb[0] >= '\u4e00' && sb[0] <= '\u9fff')
                {
                    sb.Append(ch);
                }
                else
                {
                    if (sb.Length > 0) result.Add(sb.ToString().Trim());
                    sb.Clear();
                    sb.Append(ch);
                }
            }
            else if (char.IsLetterOrDigit(ch) || ch == '_')
            {
                // 英文/数字
                if (sb.Length > 0 && !(sb[0] >= '\u4e00' && sb[0] <= '\u9fff'))
                {
                    sb.Append(ch);
                }
                else
                {
                    if (sb.Length > 0) result.Add(sb.ToString().Trim());
                    sb.Clear();
                    sb.Append(ch);
                }
            }
            else
            {
                // 分隔符
                if (sb.Length > 0) result.Add(sb.ToString().Trim());
                sb.Clear();
            }
        }

        if (sb.Length > 0) result.Add(sb.ToString().Trim());
        return result.Where(s => s.Length >= 2).ToList();
    }
}
