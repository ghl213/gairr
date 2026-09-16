using System.IO;
using System.Text;

namespace GAIRR.Core;

/// <summary>开关持久化：工具/插件/技能三类开关统一走"黑名单写 .ini"（配置只写例外，缺省全开）。
/// 工具→system.ini [Tools] Disabled；技能→config.ini [Skills] Disabled；插件→插件目录 plugin.ini Enabled=0。</summary>
public static class SwitchStore
{
    static readonly object gate = new();   // system.ini 并发写保护（UI 切换与热加载重扫可能同时写）

    /// <summary>读逗号分隔黑名单（中英文逗号均可，忽略大小写）</summary>
    public static HashSet<string> ReadBlacklist(string iniPath, string section, string key)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var x in File.ReadAllLines(iniPath))
            {
                var line = x.Trim();
                if (line.Length == 0 || line.StartsWith(';')) continue;
                if (line.StartsWith('[')) { if (line[1..line.IndexOf(']')].Trim() != section) continue; }
                var ei = line.IndexOf('=');
                if (ei <= 0) continue;
                var k = line[..ei].Trim();
                if (k != key) continue;
                var v = line[(ei + 1)..];
                var ci = v.IndexOf(" ;", StringComparison.Ordinal);
                if (ci >= 0) v = v[..ci].TrimEnd();
                foreach (var p in v.Split(',', '，'))
                {
                    var t = p.Trim();
                    if (t.Length > 0) set.Add(t);
                }
                break;
            }
        }
        catch { /* 读失败按全开处理 */ }
        return set;
    }

    /// <summary>写黑名单到 ini（保留注释与其它行；节不存在则追加到文件尾）</summary>
    public static void WriteBlacklist(string iniPath, string section, string key, HashSet<string> list)
    {
        lock (gate)
        {
            var lines = File.Exists(iniPath) ? File.ReadAllLines(iniPath).ToList() : new List<string>();
            int secStart = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                var t = lines[i].Trim();
                if (t.StartsWith('[') && t.EndsWith(']') && t[1..^1].Trim() == section)
                {
                    secStart = i;
                    break;
                }
            }
            var val = string.Join(",", list.Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));
            if (secStart < 0)
            {
                lines.Add("");
                lines.Add("[" + section + "]");
                lines.Add(key + "=" + val);
            }
            else
            {
                for (int i = secStart + 1; i < lines.Count; i++)
                {
                    var t = lines[i].Trim();
                    if (t.StartsWith('[')) break;   // 到下一节仍未找到键：插入本节点尾
                    var ei = t.IndexOf('=');
                    if (ei <= 0) continue;
                    if (t[..ei].Trim() == key)
                    {
                        var raw = lines[i];
                        var lead = raw.Length - raw.TrimStart().Length;
                        var eq = lead + ei;
                        lines[i] = raw[..(eq + 1)] + val;
                        break;
                    }
                }
                if (!lines.Any(l => { var t = l.Trim(); var ei2 = t.IndexOf('='); return ei2 > 0 && t[..ei2].Trim() == key && t.IndexOf('[') < 0; }))
                {
                    int insertAt = secStart + 1;
                    while (insertAt < lines.Count && !lines[insertAt].Trim().StartsWith('[')
                        && !(lines[insertAt].Trim().Length > 0 && lines[insertAt].Trim().IndexOf('=') > 0))
                        insertAt++;
                    lines.Insert(insertAt, key + "=" + val);
                }
            }
            File.WriteAllLines(iniPath, lines, new UTF8Encoding(false));
        }
    }

    /// <summary>system.ini 工具开关：[Tools] Disabled 黑名单</summary>
    public static string ToolsIniPath => Path.Combine(AppContext.BaseDirectory, "system.ini");

    public static HashSet<string> ToolsDisabled() => ReadBlacklist(ToolsIniPath, "Tools", "Disabled");

    /// <summary>UI 切换内置工具后写回 [Tools] Disabled</summary>
    public static void SaveTool(string name, bool enabled)
    {
        var set = ToolsDisabled();
        if (enabled) set.Remove(name); else set.Add(name);
        WriteBlacklist(ToolsIniPath, "Tools", "Disabled", set);
    }

    /// <summary>config.ini 技能开关：[Skills] Disabled 黑名单</summary>
    public static HashSet<string> SkillsDisabled() =>
        ReadBlacklist(AppConfig.ConfigPathStatic, "Skills", "Disabled");

    public static void SaveSkill(string name, bool enabled)
    {
        var set = SkillsDisabled();
        if (enabled) set.Remove(name); else set.Add(name);
        WriteBlacklist(AppConfig.ConfigPathStatic, "Skills", "Disabled", set);
    }

    /// <summary>插件开关：插件目录 plugin.ini 的 Enabled=0（行内注释保留，只替换值）</summary>
    public static void SavePluginEnabled(string iniPath, bool enabled)
    {
        if (!File.Exists(iniPath)) return;
        var lines = File.ReadAllLines(iniPath).ToList();
        bool found = false;
        for (int i = 0; i < lines.Count; i++)
        {
            var t = lines[i].Trim();
            var ei = t.IndexOf('=');
            if (ei > 0 && t[..ei].Trim() == "Enabled")
            {
                found = true;
                var raw = lines[i];
                var lead = raw.Length - raw.TrimStart().Length;
                var eq = lead + ei;
                var tail = raw[(eq + 1)..];
                var ci = tail.IndexOf(" ;", StringComparison.Ordinal);   // 保留行内注释
                var comment = ci >= 0 ? tail[ci..] : "";
                lines[i] = raw[..(eq + 1)] + (enabled ? "1" : "0") + comment;
                break;
            }
        }
        if (!found) lines.Add("Enabled=" + (enabled ? "1" : "0"));
        File.WriteAllLines(iniPath, lines, new UTF8Encoding(false));
    }
}

/// <summary>system.ini：内置工具参数/规则/提示词覆盖的集中配置（与业务配置 config.ini 分离）。
/// 首跑自动生成模板；启动时读取，改后重启生效。</summary>
public static class SystemCfg
{
    /* ---------- 内置默认值（system.ini 可覆盖/追加） ---------- */
    public static string[] IgnoreDirs = { ".git", ".gairr", "node_modules", "back", "__history", "bin", "obj", ".vs" };
    public static string[] IgnoreExts = { ".dcu", ".exe", ".dll", ".png", ".jpg", ".ico" };
    public static string[] DangerPatterns =
    {
        // 删除/破坏类（词尾带空格=要求词后有分隔；词前空白边界由 Tools 危险检查统一判定，数据无需写前导空格）
        "del ", "erase ", "rd ", "rmdir ", "deltree ",
        "remove-item", "rm -", "rm /", "unlink ", "shred ",
        "format ", "diskpart", "fdisk", "mkfs.", "dd if=", "dd of=",
        "cipher /w", "sdelete", "wipe ",

        // 权限/系统类
        "shutdown", "reboot", "poweroff", "stop-computer", "restart-computer",
        "taskkill ", "kill ", "pkill ", "killall ",
        "net user", "net localgroup", "net accounts", "net share",
        "reg add", "reg delete", "reg import", "reg restore",
        "takeown ", "icacls ", "attrib ", "cacls ",

        // 网络外发/远程控制类
        "git push", "git remote set", "git remote add",
        "curl ", "wget ", "invoke-webrequest", "invoke-restmethod", "iwr ",
        "bitsadmin", "certutil -urlcache", "certutil -decode", "certutil -encode",
        "nc ", "ncat ", "netcat ", "python -m http.server",
        "ftp ", "tftp ", "scp ", "sftp ", "ssh ", "telnet ", "rdp ", "mstsc ",

        // 下载执行类（常见无文件落地木马技术）
        "powershell -ep bypass", "powershell -executionpolicy bypass",
        "iex ", "invoke-expression", "downloadstring", "downloadfile",
        "mshta ", "regsvr32 ", "rundll32 ", "wscript ", "cscript ",
        "msbuild ", "csi ", "wmic ", "certutil -urlcache -split",
        "bitsadmin /transfer", "bash -i", "sh -i", "python -c 'import socket'",

        // 计划任务/日志清理/其它高风险
        "schtasks ", "at ", "crontab ",
        "wevtutil cl", "del %", "rmdir %", "mklink ", "subst ", "mountvol ",
    };
    public static int ReadMaxLines = 2000;
    public static int ReadMaxChars = 40000;   // Read 单次结果字符预算（超出→保留头尾+分段重取提示，防单条结果撑爆窗口）
    public static int ListMaxItems = 300;
    public static int GrepMaxHits = 50;
    public static int GlobMaxItems = 200;
    public static int OutputMaxChars = 6000;
    public static int CardMaxChars = 600;
    public static int RegexTimeoutSec = 3;

    // 挂起等人工（危险确认/计划审批）通知（[Notify] 节）：非视口/后台会话挂起后、"切出视口持续满"该秒数仍挂起 → 推钉钉
    // （用户在其它会话忙碌同样补发，防后台挂起失联；切回视口清零重新计时）；人不在兜底仍走 IdleGate 3 分钟（SilentThreshold）
    public static int NotifyShortSilentSec = 60;

    // 项目地图自动化（[ProjectMap] 节，后台服务，见 Core/ProjectMapAuto.cs）
    public static bool AutoNotes = true;    // 缺注释文件/方法由大模型补说明（写 .gairr/notes.json，不改源码）
    public static bool SymbolNotes = true;  // 符号级补注：符号索引中无中文注释的符号由 LLM 补说明（写 .gairr/symbol-notes.json，检索标签层受益）
    public static bool AutoDocs = true;     // 首次自动生成架构/功能文档，之后结构大变化时提示
    public static bool AiScan = true;       // 扫描前由 LLM 判断"非源码/非资源/非配置"目录与文件，写入 .gairr/ai-ignore.json；结构哈希不变则不重判（省 token）
    public static int AiScanMaxFiles = 2000; // AiScan 候选池上限：超过时按目录采样（大项目防 prompt 爆炸）
    public static int NotesBatch = 3;       // 补注释批大小（批间 500ms 节流）
    public static int SymbolNotesBatch = 0; // 符号级补注单轮上限；0=不限（小批验证可设 30）
    public static bool EnrichTagRefs = true; // 任务后主动分词关联（TagRefEnricher 后台异步，写 .gairr/tag-refs.jsonl）
    public static bool Embed = true;         // 本地向量检索（ONNX embedding，MapAuto 自动建库，SmartSearch 第四层）

    // 修复-验证循环（[Verify] 节，见 Core/Verify.cs）
    public static bool AutoVerify = true;   // Write/Edit 代码文件后自动构建验证并回喂结果
    public static string BuildCmd = "";    // 构建命令覆盖；留空=自动探测项目根的 *.sln/*.csproj 用 dotnet build
    public static int VerifyTimeoutSec = 180; // 验证命令超时（秒）
    public static int VerifyMaxChars = 1500;  // 验证输出回喂字符上限
    public static int MaxRepairRounds = 3;    // 连续构建失败上限：超限暂停自动验证并强提示模型停手上报，防耗满轮次
    public static bool AutoTest = true;       // 改动落在测试工程内时跑 dotnet test（增量验证），否则走构建

    // 写前护栏（[Guard] 节）：先回读再改动——Write 覆盖已有文件前必须本会话已 Read 过目标
    public static bool FirstReadCheck = true; // 1=未读目标文件时拦截 Write 并提示先 Read；0=关闭（Edit 有 old_text 匹配兜底，不受影响）

    // 敏感信息加密（[Sensitive] 节，见 Core/SensitiveGuard.cs）：发往大模型前对密钥/密码/敏感URL 加密脱敏（ENC 令牌），
    // 模型返回后再解密交工具执行/展示；密钥自动保存在 exe 同目录 sensitive.key
    public static bool SensitiveEnabled;           // 总开关：1=开启（默认关闭，避免影响本地模型与既有行为）
    public static bool SensitiveKeys = true;       // 密钥类：sk-/AKIA/ghp_/AIza/PEM 私钥等
    public static bool SensitivePasswords = true;  // 密码类：password/api_key/token 等赋值值
    public static bool SensitiveUrls = true;       // URL 类：带 userinfo 或 key/token/secret 参数的链接

    // 模型请求参数（[Tools] 节）
    public static bool EnableThinking = true;  // qwen3 系思考模式开关：1=保留思维链，0=关闭（content 直接输出，配合中文说明提示词）
    public static bool EditDiff = true;        // Edit 成功后结果附行级 diff（工具卡红绿渲染）；0=关闭（错误定位增强不受此开关影响）

    // 计划粒度控制（[Agent] 节）：控制 UpdateTodo 清单步数下限，压低简单任务的拆分层级以省轮次/token
    public static int PlanMinSteps = 1;        // 清单最小步数：默认 1（简单任务 1~2 步即可）；调高则强制更细拆分
    // Agent 工作模式（[Agent] 节）：agile=敏捷模式（默认，语义直达+够用即停），deep=严谨模式（四层检索+完整验证）
    public static string AgentMode = "agile";
    // 意图宣布兜底关键词（[Agent] 节）：模型纯文本收尾只说"接下来要做什么"却没调用工具时触发追发执行指令。
    // 逗号分隔正则片段；留空=用内置默认（"现在建清单…继续"等）。仅影响 LooksLikeIntentAnnouncement 的命中范围。
    public static string[] IntentRetryKeywords = Array.Empty<string>();

    // 服务模式（[Server] 节，见 AgentHost/AgentSession.cs）：gairr-cli / gairr-agent-server 无界面宿主
    public static string DangerPolicy = "Ask";   // 危险命令策略：Ask=挂起等外部决策（POST /decisions，默认）/ Allow=自动放行 / Deny=一律拒绝
    public static string SessionDir = "";        // 会话存储根目录；留空=各项目 .gairr/sessions（共享盘/多用户可填全局目录）

    // 语言服务器（[LSP] 节，见 Core/Lsp/LspManager.cs）：FindRefs 编译器级引用检索的后端
    public static string LspServerPath = "";   // 显式指定 LSP server 可执行文件；留空=自动探测（dotnet tool / PATH）
    public static string LspDotnetRoot = "";   // server 运行时的 DOTNET_ROOT（server 可能要求比 GAIRR 更新的 .NET 运行时）；留空=继承当前环境
    public static int LspRequestTimeoutSec = 20; // 单次引用查询超时（秒）
    public static int LspIdleRecycleMin = 20;    // 空闲回收（分钟）：超过未使用则杀进程释放内存，下次自动重启

    // Java 语言服务器（[LspJava] 节，见 Core/Lsp/LspEnv.cs）：jdtls 后端
    public static string LspJdkPath = "";     // 显式指定 JDK 根目录（含 bin\javac.exe）；留空=自动探测（JAVA_HOME → lsp\java\ 自带 → PATH）
    public static string LspJdtlsPath = "";   // 显式指定 jdtls 解压根目录；留空=自动（lsp\jdtls\ → 缺失则下载）
    public static bool LspJavaAutoDownload = true; // 缺失时自动下载 JDK21+jdtls 到 GAIRR 目录 lsp\ 下（各 ~180MB，一次性）

    // Vue/TypeScript 语言服务器（[LspVue] 节，见 Core/Lsp/LspEnv.cs）：volar 后端
    public static string LspNodePath = "";    // 显式指定 node.exe；留空=自动探测（PATH → lsp\node\ 自带）
    public static string LspVolarPath = "";   // 显式指定 vue-language-server.js；留空=自动（lsp\volar\ → 缺失则 npm 安装）
    public static bool LspVueAutoDownload = true; // 缺失时自动下载 Node（lsp\node\）并 npm 安装 volar（lsp\volar\）

    // 插件/技能运行时节（[Runtime] 节，见 Core/RuntimeEnv.cs）：{python}/{node}/{java} 占位符的自带运行时
    public static bool RuntimeAutoDownload = true; // 本机缺 python/node/java 时自动下载便携版到 GAIRR 目录 envs\ 下（与 dotnet publish 的 runtimes\ 区分）

    static AppConfig? sys;

    /// <summary>确保 system.ini 存在（缺失写模板）并加载覆盖值</summary>
    public static void Init()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "system.ini");
        if (!File.Exists(path))
            try { File.WriteAllText(path, Template(), new UTF8Encoding(false)); } catch { }
        sys = new AppConfig(path);

        IgnoreDirs = Merge(IgnoreDirs, sys.Get("Tools", "ExtraIgnoreDirs", ""));
        IgnoreExts = Merge(IgnoreExts, sys.Get("Tools", "ExtraIgnoreExts", ""));
        var dp = Split(sys.Get("Tools", "DangerPatterns", ""));
        if (dp.Length > 0) DangerPatterns = dp;      // 留空=用内置默认（安全底线）
        ReadMaxLines = sys.GetInt("Tools", "ReadMaxLines", ReadMaxLines);
        ReadMaxChars = sys.GetInt("Tools", "ReadMaxChars", ReadMaxChars);
        ListMaxItems = sys.GetInt("Tools", "ListMaxItems", ListMaxItems);
        GrepMaxHits = sys.GetInt("Tools", "GrepMaxHits", GrepMaxHits);
        GlobMaxItems = sys.GetInt("Tools", "GlobMaxItems", GlobMaxItems);
        OutputMaxChars = sys.GetInt("Tools", "OutputMaxChars", OutputMaxChars);
        CardMaxChars = sys.GetInt("Tools", "CardMaxChars", CardMaxChars);
        RegexTimeoutSec = sys.GetInt("Tools", "RegexTimeoutSec", RegexTimeoutSec);
        NotifyShortSilentSec = Math.Max(5, sys.GetInt("Notify", "ShortSilentSec", NotifyShortSilentSec));
        AutoNotes = sys.GetInt("ProjectMap", "AutoNotes", AutoNotes ? 1 : 0) == 1;
        SymbolNotes = sys.GetInt("ProjectMap", "SymbolNotes", SymbolNotes ? 1 : 0) == 1;
        AutoDocs = sys.GetInt("ProjectMap", "AutoDocs", AutoDocs ? 1 : 0) == 1;
        AiScan = sys.GetInt("ProjectMap", "AiScan", AiScan ? 1 : 0) == 1;
        AiScanMaxFiles = Math.Max(100, sys.GetInt("ProjectMap", "AiScanMaxFiles", AiScanMaxFiles));
        NotesBatch = Math.Max(1, sys.GetInt("ProjectMap", "NotesBatch", NotesBatch));
        SymbolNotesBatch = Math.Max(0, sys.GetInt("ProjectMap", "SymbolNotesBatch", SymbolNotesBatch));
        EnrichTagRefs = sys.GetInt("ProjectMap", "EnrichTagRefs", EnrichTagRefs ? 1 : 0) == 1;
        Embed = sys.GetInt("ProjectMap", "Embed", Embed ? 1 : 0) == 1;
        AutoVerify = sys.GetInt("Verify", "AutoVerify", AutoVerify ? 1 : 0) == 1;
        FirstReadCheck = sys.GetInt("Guard", "FirstReadCheck", FirstReadCheck ? 1 : 0) == 1;
        SensitiveEnabled = sys.GetInt("Sensitive", "Enabled", SensitiveEnabled ? 1 : 0) == 1;
        SensitiveKeys = sys.GetInt("Sensitive", "Keys", SensitiveKeys ? 1 : 0) == 1;
        SensitivePasswords = sys.GetInt("Sensitive", "Passwords", SensitivePasswords ? 1 : 0) == 1;
        SensitiveUrls = sys.GetInt("Sensitive", "Urls", SensitiveUrls ? 1 : 0) == 1;
        BuildCmd = sys.Get("Verify", "BuildCmd", "");
        VerifyTimeoutSec = Math.Max(10, sys.GetInt("Verify", "VerifyTimeoutSec", VerifyTimeoutSec));
        VerifyMaxChars = Math.Max(200, sys.GetInt("Verify", "VerifyMaxChars", VerifyMaxChars));
        MaxRepairRounds = Math.Max(1, sys.GetInt("Verify", "MaxRepairRounds", MaxRepairRounds));
        AutoTest = sys.GetInt("Verify", "AutoTest", AutoTest ? 1 : 0) == 1;
        EnableThinking = sys.GetInt("Tools", "EnableThinking", EnableThinking ? 1 : 0) == 1;
        EditDiff = sys.GetInt("Tools", "EditDiff", EditDiff ? 1 : 0) == 1;
        PlanMinSteps = Math.Clamp(sys.GetInt("Agent", "PlanMinSteps", PlanMinSteps), 1, 8);
        AgentMode = sys.Get("Agent", "AgentMode", AgentMode).Trim().ToLowerInvariant();
        if (AgentMode != "agile" && AgentMode != "deep") AgentMode = "agile";   // 无效值回退敏捷
        var irk = Split(sys.Get("Agent", "IntentRetryKeywords", ""));
        if (irk.Length > 0) IntentRetryKeywords = irk;   // 留空用内置默认
        DangerPolicy = sys.Get("Server", "DangerPolicy", "Ask");
        SessionDir = sys.Get("Server", "SessionDir", "");
        LspServerPath = sys.Get("LSP", "ServerPath", "");
        LspDotnetRoot = sys.Get("LSP", "DotnetRoot", "");
        LspRequestTimeoutSec = Math.Max(5, sys.GetInt("LSP", "RequestTimeoutSec", LspRequestTimeoutSec));
        LspIdleRecycleMin = Math.Max(1, sys.GetInt("LSP", "IdleRecycleMin", LspIdleRecycleMin));
        LspJdkPath = sys.Get("LspJava", "JdkPath", "");
        LspJdtlsPath = sys.Get("LspJava", "JdtlsPath", "");
        LspJavaAutoDownload = sys.GetInt("LspJava", "AutoDownload", LspJavaAutoDownload ? 1 : 0) == 1;
        LspNodePath = sys.Get("LspVue", "NodePath", "");
        LspVolarPath = sys.Get("LspVue", "VolarPath", "");
        LspVueAutoDownload = sys.GetInt("LspVue", "AutoDownload", LspVueAutoDownload ? 1 : 0) == 1;
        RuntimeAutoDownload = sys.GetInt("Runtime", "AutoDownload", RuntimeAutoDownload ? 1 : 0) == 1;
    }

    /// <summary>工具 description 覆盖（[ToolDesc] 节，提示词层调优；留空用内置文案）</summary>
    public static string Desc(string name, string def)
    {
        var v = sys?.Get("ToolDesc", name, "") ?? "";
        return v.Length > 0 ? v : def;
    }

    static string[] Split(string s) =>
        s.Split(',', '，').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();

    static string[] Merge(string[] def, string extra) =>
        extra.Length == 0 ? def : def.Concat(Split(extra)).ToArray();

    /* ---------- 项目级忽略清单（{root}/.gairr/ignore.json，随项目走） ---------- */
    internal sealed class ProjectIgnore
    {
        public List<string> DirPrefixes = new();   // 相对路径前缀（按 '/'），命中即跳过整棵子树
        public List<string> DirNames = new();      // 目录名（任意层级同名目录都跳过）
        public List<string> Exts = new();          // 扩展名（小写带点）
    }
    static readonly Dictionary<string, (ProjectIgnore Ig, DateTime Mtime)> IgnoreCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>加载项目级忽略清单（按 root 缓存，文件变更自动重载；无文件返回 null）。
    /// 与全局 IgnoreDirs/IgnoreExts 叠加：全局管通用底线，本项目清单只管当前项目，存 .gairr/ignore.json 随项目走。</summary>
    internal static ProjectIgnore? LoadProjectIgnore(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;
        var path = Path.Combine(root, ".gairr", "ignore.json");
        if (!File.Exists(path)) return null;
        DateTime mtime;
        try { mtime = File.GetLastWriteTimeUtc(path); } catch { return null; }
        if (IgnoreCache.TryGetValue(root, out var hit) && hit.Mtime == mtime) return hit.Ig;
        var ig = new ProjectIgnore();
        try
        {
            var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var rt = json.RootElement;
            if (rt.TryGetProperty("dirPrefixes", out var dp) && dp.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var e in dp.EnumerateArray()) ig.DirPrefixes.Add(e.GetString()?.Replace('\\', '/').Trim() ?? "");
            if (rt.TryGetProperty("dirNames", out var dn) && dn.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var e in dn.EnumerateArray()) ig.DirNames.Add((e.GetString()?.Trim() ?? "").TrimEnd('/'));
            if (rt.TryGetProperty("exts", out var ex) && ex.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var e in ex.EnumerateArray())
                {
                    var x = (e.GetString()?.Trim() ?? "").ToLowerInvariant();
                    if (x.Length > 0 && !x.StartsWith('.')) x = "." + x;
                    ig.Exts.Add(x);
                }
        }
        catch { ig = new ProjectIgnore(); }
        ig.DirPrefixes.RemoveAll(s => s.Length == 0);
        ig.DirNames.RemoveAll(s => s.Length == 0);
        ig.Exts.RemoveAll(s => s.Length == 0);
        IgnoreCache[root] = (ig, mtime);
        return ig;
    }

    /// <summary>判断某相对路径（root 起、'/' 分隔）是否被项目级忽略命中：前缀 / 扩展名</summary>
    internal static bool ProjectIgnoreHits(ProjectIgnore? ig, string rel)
    {
        if (ig == null) return false;
        var p = rel.Replace('\\', '/');
        if (ig.Exts.Count > 0)
        {
            var dot = p.LastIndexOf('.');
            if (dot >= 0 && ig.Exts.Contains(p.Substring(dot).ToLowerInvariant())) return true;
        }
        foreach (var pre in ig.DirPrefixes)
            if (p == pre || p.StartsWith(pre + "/", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>判断目录名是否被项目级 dirNames 命中（任意层级同名目录都跳过）</summary>
    internal static bool ProjectIgnoreDirName(ProjectIgnore? ig, string dirName)
    {
        if (ig == null) return false;
        for (int i = 0; i < ig.DirNames.Count; i++)
            if (string.Equals(ig.DirNames[i], dirName, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static string Template() =>
        "; GAIRR system.ini —— 内置工具参数/规则/提示词覆盖（exe 同目录，改后重启生效）\n" +
        "; 业务配置（模型/项目/钩子）在 config.ini；本文件只管内置工具行为\n" +
        "\n" +
        "[Tools]\n" +
        "; 扫描忽略目录/扩展名：逗号分隔，追加到内置默认（.git,node_modules,back,bin,obj 等）之后\n" +
        "ExtraIgnoreDirs=\n" +
        "ExtraIgnoreExts=\n" +
        "; Read 单次返回行数上限\n" +
        "ReadMaxLines=2000\n" +
        "; Read 单次结果字符预算（超出→保留头尾+分段重取提示）\n" +
        "ReadMaxChars=40000\n" +
        "; ListDir 条目上限\n" +
        "ListMaxItems=300\n" +
        "; Grep 命中上限\n" +
        "GrepMaxHits=50\n" +
        "; Glob 结果上限\n" +
        "GlobMaxItems=200\n" +
        "; Bash/Grep 返回字符上限\n" +
        "OutputMaxChars=6000\n" +
        "; 对话区工具卡内容预览字符上限\n" +
        "CardMaxChars=600\n" +
        "; Grep/Glob 正则超时秒数\n" +
        "RegexTimeoutSec=3\n" +
        "; qwen3 系思考模式开关：1=保留思维链（默认），0=关闭思考，content 直接输出（配合 prompts/agent-deep.md 中文说明规则）\n" +
        "EnableThinking=1\n" +
        "; Edit 成功后结果附行级 diff（工具卡红绿渲染+回喂模型自审）；0=关闭（错误定位增强不受此开关影响）\n" +
        "EditDiff=1\n" +
        "; Bash 危险命令拦截模式（逗号分隔）。留空=内置默认（del/rd/rmdir/format/git push/shutdown）；\n" +
        "; 填写则完全覆盖——清空拦截风险自担；Bash/插件/Hooks 均受该审查拦截。\n" +
        "DangerPatterns=\n" +
        "\n" +
        "[Agent]\n" +
        "; Agent 工作模式：agile=敏捷模式（默认，语义直达+够用即停），deep=严谨模式（四层检索+完整验证）\n" +
        "AgentMode=agile\n" +
        "; 待办清单最小步数：1=简单任务允许单步计划（省轮次/token）；调高则强制更细拆分\n" +
        "PlanMinSteps=1\n" +
        "; 意图宣布兜底自定义关键词：模型只说话没调用工具时触发追发执行指令。逗号分隔正则片段，留空=用内置默认\n" +
        "; 例：IntentRetryKeywords=现在建清单.*继续改，下一步.*读取，接下来.*修改\n" +
        "IntentRetryKeywords=\n" +
        "\n" +
        "[ToolDesc]\n" +
        "; 覆盖发给模型的工具 description（提示词层调优，留空用内置文案）\n" +
        "; 例：Bash=在 cmd 下执行命令。涉及删除的命令一律先问用户。\n" +
        "Map=\n" +
        "Read=\n" +
        "ListDir=\n" +
        "Bash=\n" +
        "Write=\n" +
        "Edit=\n" +
        "Grep=\n" +
        "Glob=\n" +
        "LoadSkill=\n" +
        "MapAuto=\n" +
        "MapTrace=\n" +
        "MapSlice=\n" +
        "\n" +
        "[Verify]\n" +
        "; Write/Edit 写入代码文件后自动构建验证，结果回喂 Agent（构建失败模型下一轮直接修复）\n" +
        "AutoVerify=1\n" +
        "; 构建命令覆盖；留空=自动探测项目根 *.sln/*.csproj 并执行 dotnet build\n" +
        "; 例：dotnet build -c Release\n" +
        "BuildCmd=\n" +
        "; 验证命令超时（秒）\n" +
        "VerifyTimeoutSec=180\n" +
        "; 验证输出回喂字符上限\n" +
        "VerifyMaxChars=1500\n" +
        "; 连续构建失败上限：超限暂停自动验证并强提示模型停手排查（防同一错误反复重试耗满轮次），0=不限\n" +
        "MaxRepairRounds=3\n" +
        "; 增量验证：改动文件位于测试工程（*.Tests/）时自动跑 dotnet test，否则走构建；0=关闭\n" +
        "AutoTest=1\n" +
        "\n" +
        "[Guard]\n" +
        "; 写前护栏：Write 覆盖已有文件前，要求本会话先用 Read 读取过目标（防未读直接覆盖丢代码）；0=关闭\n" +
        "FirstReadCheck=1\n" +
        "\n" +
        "[Sensitive]\n" +
        "; 敏感信息加密（Core/SensitiveGuard.cs）：发往大模型前对密钥/密码/敏感URL 做可逆加密脱敏（ENC 令牌，AES-256-GCM），模型返回后再解密交工具执行/展示\n" +
        "; 加密密钥自动生成保存在 exe 同目录 sensitive.key（丢失则旧 ENC 令牌不可解，本地历史明文不受影响）\n" +
        "; Enabled：总开关 1=开启 0=关闭（默认关闭，避免影响本地模型与既有行为）；Keys/Passwords/Urls：各类别独立开关 1=掩码 0=跳过\n" +
        "Enabled=0\n" +
        "Keys=1\n" +
        "Passwords=1\n" +
        "Urls=1\n" +
        "\n" +
        "[ProjectMap]\n" +
        "; 项目地图自动化（启动/切项目后后台执行）：生成地图、调用图谱、补注释、架构文档\n" +
        "; 另有内置工具 MapAuto 可在左侧栏禁用/启用——禁用后 Agent 不可见且自动触发一并停止\n" +
        "; AutoNotes=1 时缺注释文件/方法由大模型补说明（写 .gairr/notes.json，不改源码）\n" +
        "AutoNotes=1\n" +
        "; SymbolNotes=1 时符号索引中无中文注释的符号由 LLM 补说明（写 .gairr/symbol-notes.json，检索中文标签层受益，不改源码）\n" +
        "SymbolNotes=1\n" +
        "; 符号级补注单轮上限：0=不限（建议先设 30 小批验证质量，满意后改 0 全量）\n" +
        "SymbolNotesBatch=0\n" +
        "; 首次自动生成架构/功能文档（.gairr/docs/），之后结构大变化时提示重建\n" +
        "AutoDocs=1\n" +
        "; AiScan=1 时扫描前由 LLM 判断\"非源码/非资源/非配置\"目录与文件（写 .gairr/ai-ignore.json）；结构哈希不变不重判（省 token）\n" +
        "AiScan=1\n" +
        "; AiScan 候选池上限：超过时按目录采样（大项目防 prompt 爆炸；最小 100）\n" +
        "AiScanMaxFiles=2000\n" +
        "; 补注释批大小（批间 500ms 节流，防触发限流）\n" +
        "NotesBatch=3\n" +
        "; 任务结束后主动分词关联（后台异步调模型写 .gairr/tag-refs.jsonl，不阻塞对话；0=关闭）\n" +
        "EnrichTagRefs=1\n" +
        "; 本地向量检索（ONNX embedding，MapAuto 自动建 .gairr/embed-index.json，SmartSearch 第四层；0=关闭）\n" +
        "Embed=1\n" +
        "\n" +
        "[LSP]\n" +
        "; 语言服务器（FindRefs 编译器级引用检索的后端）：\n" +
        "; 留空=自动探测：%USERPROFILE%\\.dotnet\\tools（dotnet tool install -g Microsoft.CodeAnalysis.LanguageServer）→ PATH\n" +
        "; 也可填显式路径，如：D:\\tools\\dotnet-language-server.exe\n" +
        "ServerPath=\n" +
        "; server 运行时的 DOTNET_ROOT（server 可能要求比 GAIRR 更新的 .NET 运行时）；留空=继承当前环境\n" +
        "; 例：%USERPROFILE%\\.dotnet10（dotnet-install 安装的 .NET 10 SDK 目录）\n" +
        "DotnetRoot=\n" +
        "; 单次引用查询超时（秒）\n" +
        "RequestTimeoutSec=20\n" +
        "; 空闲回收（分钟）：超过未使用则杀进程释放内存，下次调用自动重启\n" +
        "IdleRecycleMin=20\n" +
        "\n" +
        "[LspJava]\n" +
        "; Java 语言服务器（jdtls，FindRefs 对 .java 文件生效）：\n" +
        "; JdkPath：JDK 根目录（含 bin\\javac.exe），如 D:\\java\\jdk-21；留空=自动探测 JAVA_HOME → lsp\\java\\ 自带 → PATH\n" +
        "; 注意：jdtls 最新版要求 JDK 21+，GAIRR 自动下载版自带 Temurin JDK21 到 lsp\\java\\（一次性 ~190MB）\n" +
        "JdkPath=\n" +
        "; JdtlsPath：jdtls 解压根目录（含 plugins\\）；留空=自动（lsp\\jdtls\\ → 缺失则自动下载 ~180MB）\n" +
        "JdtlsPath=\n" +
        "; 缺失自动下载到 GAIRR 目录 lsp\\ 下；0=只提示不下载\n" +
        "AutoDownload=1\n" +
        "\n" +
        "[LspVue]\n" +
        "; Vue/TypeScript 语言服务器（volar，FindRefs 对 .vue/.ts/.js 生效）：\n" +
        "; NodePath：node.exe 路径；留空=自动探测 PATH → lsp\\node\\ 自带\n" +
        "NodePath=\n" +
        "; VolarPath：vue-language-server.js 路径；留空=自动（lsp\\volar\\ → 缺失则 npm 安装，用国内镜像源）\n" +
        "VolarPath=\n" +
        "; 缺失自动下载/安装；0=只提示不下载\n" +
        "AutoDownload=1\n" +
        "\n" +
        "[Runtime]\n" +
        "; 插件/技能运行时节（plugin.ini 的 Command 与 SKILL.md 里的 {python}/{node}/{java} 占位符，见 Core/RuntimeEnv.cs）：\n" +
        "; 解释器缺失时自动下载便携版到 GAIRR 目录 envs\\ 下：python=官方 embeddable 3.12（~11MB，无 pip）、\n" +
        "; node=官方 LTS v20（~30MB）、java=Temurin JRE21（~55MB，够跑 jar）；系统 PATH 已有解释器则不动；0=一律不下载（缺了由命令自行报错）\n" +
        "AutoDownload=1\n" +
        "\n" +
        "[Notify]\n" +
        "; 挂起等人工（危险确认/计划审批）的钉钉提醒时机（后台/切走会话，见 Core/IdleGate.cs）：\n" +
        "; 短静默秒数 = 非视口会话挂起后、切出视口持续满该时长仍挂起即发一条钉钉提醒（默认 60；\n" +
        "; 用户在其它会话忙碌同样补发、切回视口清零再切走重新计时）；人不在的兜底沿用 IdleGate\n" +
        "; 3 分钟无操作阈值（SilentThreshold），无需在此配置\n" +
        "ShortSilentSec=60\n" +
        "\n" +
        "[Server]\n" +
        "; 服务模式（gairr-cli / gairr-agent-server 无界面宿主）专属；图形界面忽略本节\n" +
        "; 危险命令策略：Ask=挂起等外部决策（默认，经 POST /decisions 回答）/ Allow=自动放行 / Deny=一律拒绝\n" +
        "DangerPolicy=Ask\n" +
        "; 会话存储根目录；留空=各项目 .gairr/sessions；共享盘/多用户场景可填全局目录\n" +
        "SessionDir=\n";
}
