using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;

namespace GAIRR.Core.Lsp;

/// <summary>语言服务器管理器：按语言（csharp/java/vue）惰性启动对应 LSP server（首次 FindRefs 调用时拉起，
/// 环境缺失时按 system.ini 自动下载到 lsp\ 子目录）、按项目根缓存进程、请求串行化、空闲回收、
/// 失败降级提示（状态经 LspStatus 事件显示在左侧后台状态栏）。</summary>
public class LspManager
{
    public static LspManager? Instance { get; private set; }

    /// <summary>应用启动时注入配置与事件总线（须在任务运行前调用）</summary>
    public static void Init(AppConfig cfg, UiEventBus bus) => Instance ??= new LspManager(cfg, bus);

    readonly AppConfig cfg;
    readonly UiEventBus bus;
    readonly SemaphoreSlim gate = new(1, 1);
    readonly Timer recycleTimer;

    readonly Dictionary<string, LspClient> clients = new();      // 语言 → 就绪的 server
    readonly Dictionary<string, string> clientRoots = new();     // 语言 → server 启动时的项目根（切项目须重建）
    readonly Dictionary<string, DateTime> lastUsed = new();
    readonly Dictionary<string, string> lastFail = new();        // 最近失败原因（短时缓存防反复重试）
    readonly Dictionary<string, DateTime> lastFailTime = new();
    string? serverExe;          // Roslyn 探测结果缓存

    LspManager(AppConfig cfg, UiEventBus bus)
    {
        this.cfg = cfg;
        this.bus = bus;
        recycleTimer = new Timer(_ => CheckRecycle(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    void Status(string msg) => bus.Post(new UiEvent { Type = UiEventType.LspStatus, LogLine = msg });

    /// <summary>server 探测顺序：system.ini [LSP] ServerPath → dotnet tool 默认目录 → PATH</summary>
    static string? FindServerExe()
    {
        if (SystemCfg.LspServerPath.Length > 0 && File.Exists(SystemCfg.LspServerPath))
            return SystemCfg.LspServerPath;
        var tool = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dotnet", "tools", "dotnet-language-server.exe");
        if (File.Exists(tool)) return tool;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            var p = Path.Combine(dir.Trim(), "dotnet-language-server.exe");
            if (p.Length > 3 && File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>启动后环境自检（不启动进程、不下载）：静态探测各语言组件，状态栏一行汇总——开应用即可知道 FindRefs 可用性</summary>
    public void Probe()
    {
        if (clients.Count > 0) return;   // 已驻留过，无需再检
        _ = Task.Run(() =>
        {
            try
            {
                string cs;
                var exe = FindServerExe();
                if (exe == null) cs = "C# 未安装（FindRefs 将降级）";
                else
                {
                    var r = SystemCfg.LspDotnetRoot.Length > 0
                        ? Environment.ExpandEnvironmentVariables(SystemCfg.LspDotnetRoot) : "";
                    cs = r.Length > 0 && !Directory.Exists(r)
                        ? "C# 运行时缺失（DotnetRoot 目录不存在）"
                        : $"C# 就绪（{Path.GetFileName(exe)}）";
                }
                var java = LspEnv.JavaReady() ? "Java 就绪" : "Java 未就绪（首次 .java 查询自动下载 jdtls+JDK21 ~370MB）";
                var vue = LspEnv.VueReady() ? "Vue 就绪" : "Vue 未就绪（首次 .vue 查询自动安装 volar）";
                Status($"语言服务器：{cs} · {java} · {vue}");
            }
            catch { }
        });
    }

    /// <summary>编译器级引用查询（自动按文件扩展名选择语言后端）：返回 文件:行:上下文 列表文本，失败时返回降级提示</summary>
    public async Task<string> FindReferencesAsync(string fileRel, int line, string symbol, int max, CancellationToken ct)
    {
        var lang = LspEnv.LangOf(fileRel);
        if (lang.Length == 0)
            return $"错误：{Path.GetExtension(fileRel)} 暂不支持编译器级引用（当前支持 .cs/.java/.vue/.js/.ts）。已降级——请改用 MapTrace/Grep 做文本级检索";
        var root = cfg.ProjectRoot;
        if (root.Length == 0) return "错误：未设置项目根（config.ini [Agent] ProjectRoot），无法启动语言服务器，请改用 MapTrace";
        var absPath = Path.IsPathRooted(fileRel) ? fileRel
            : Path.GetFullPath(Path.Combine(root, fileRel.Replace('/', '\\')));

        LspClient lsp;
        try { lsp = await EnsureReadyAsync(lang, root, ct); }
        catch (Exception ex) { return $"错误：{ex.Message}。已降级——请改用 MapTrace/SmartSearch 做文本级检索"; }

        try
        {
            // 1. 读文件并定位符号所在列（行内精确匹配，保证 references 命中语义位置）
            string[] lines;
            try { lines = File.ReadAllLines(absPath); }
            catch (Exception ex) { return $"错误：读取 {fileRel} 失败：{ex.Message}"; }
            if (line < 1 || line > lines.Length) return $"错误：行号 {line} 超出文件范围（共 {lines.Length} 行）";
            var target = lines[line - 1];
            var col = 0;
            if (symbol.Length > 0)
            {
                var idx = IndexOfSymbol(target, symbol);
                if (idx >= 0) col = idx;
            }

            // 2. didOpen 后查询
            var uri = LspClient.UriOf(absPath);
            lsp.Notify("textDocument/didOpen", new JsonObject
            {
                ["textDocument"] = new JsonObject
                {
                    ["uri"] = uri,
                    ["languageId"] = LspEnv.LanguageIdOf(lang),
                    ["version"] = 1,
                    ["text"] = string.Join("\n", lines),
                },
            });
            var result = await lsp.RequestAsync("textDocument/references", new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = uri },
                ["position"] = new JsonObject { ["line"] = line - 1, ["character"] = col },
                ["context"] = new JsonObject { ["includeDeclaration"] = true },
            }, ct, TimeSpan.FromSeconds(SystemCfg.LspRequestTimeoutSec));
            lastUsed[lang] = DateTime.Now;

            // 3. 组装结果（引用文件上下文行懒读，限制数量防超长）
            var locs = result?["result"] as JsonArray ?? result as JsonArray;
            if (locs == null || locs.Count == 0)
                return $"符号 {symbol}（{fileRel}:{line}）未找到引用。请用 MapTrace/Grep 换关键词确认符号名与行号后重试";

            var sb = new System.Text.StringBuilder();
            var name = symbol.Length > 0 ? symbol : $"{Path.GetFileName(fileRel)}:{line}";
            sb.Append($"符号 {name} 的 {locs.Count} 处引用（{LspEnv.NameOf(lang)}编译器解析{(locs.Count > max ? $"·仅显示前 {max} 处" : "")}）：\n");
            var shown = 0;
            foreach (var loc in locs)
            {
                if (shown >= max) break;
                var locObj = loc as JsonObject;
                if (locObj == null) continue;
                var u = locObj["uri"]?.GetValue<string>() ?? "";
                var s = locObj["range"]?["start"];
                if (s == null) continue;
                var l = s["line"]?.GetValue<int>() ?? 0;
                var fs = u.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                    ? Uri.TryCreate(u, UriKind.Absolute, out var uu) ? uu.LocalPath : u
                    : u;
                var rel = fs.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                    ? fs[root.Length..].TrimStart('\\', '/') : fs;
                var ctx = ContextLine(fs, l + 1, lines, absPath);
                var isDef = locObj["range"]?["start"]?["line"]?.GetValue<int>() == line - 1
                    && string.Equals(fs, absPath, StringComparison.OrdinalIgnoreCase);
                sb.Append($"{(isDef ? "【定义】" : "")}{rel}:{l + 1}{(ctx.Length > 0 ? $"  {ctx}" : "")}\n");
                shown++;
            }
            return sb.ToString().TrimEnd();
        }
        catch (TimeoutException)
        {
            lastFail[lang] = $"语言服务器响应超时（>{SystemCfg.LspRequestTimeoutSec}s）";
            return $"错误:{lastFail[lang]}，已降级请改用 MapTrace";
        }
        catch (Exception ex)
        {
            lastFail[lang] = ex.Message;
            return $"错误：LSP 查询失败：{ex.Message}，已降级请改用 MapTrace";
        }
    }

    /// <summary>行内符号名匹配（完整标识符边界），返回 0 起列号；找不到返回 -1</summary>
    static int IndexOfSymbol(string line, string symbol)
    {
        for (var i = 0; (i = line.IndexOf(symbol, i, StringComparison.Ordinal)) >= 0; i += symbol.Length)
        {
            var prevOk = i == 0 || !IsIdent(line[i - 1]);
            var nextOk = i + symbol.Length >= line.Length || !IsIdent(line[i + symbol.Length]);
            if (prevOk && nextOk) return i;
        }
        return -1;
    }

    static bool IsIdent(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';

    /// <summary>引用行上下文：同一文件直接取；跨文件懒读（失败返回空，不阻断结果）</summary>
    static string ContextLine(string file, int lineNo, string[] selfLines, string selfPath)
    {
        try
        {
            var lines = string.Equals(file, selfPath, StringComparison.OrdinalIgnoreCase) ? selfLines : File.ReadAllLines(file);
            if (lineNo < 1 || lineNo > lines.Length) return "";
            var t = lines[lineNo - 1].Trim();
            return t.Length > 0 ? LLMClient.Trunc(t, 90) : "";
        }
        catch { return ""; }
    }

    /// <summary>惰性启动：首次调用拉起对应语言 server（30s 超时；Java/Vue 环境缺失时先按配置下载到 lsp\），
    /// 失败原因短时缓存防重复重试；项目根变化时重建进程</summary>
    async Task<LspClient> EnsureReadyAsync(string lang, string root, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (clients.TryGetValue(lang, out var c) && string.Equals(clientRoots[lang], root, StringComparison.OrdinalIgnoreCase))
            {
                lastUsed[lang] = DateTime.Now;
                return c;
            }
            if (clients.ContainsKey(lang))   // 有 client 但项目根变了：旧 server 加载的是旧项目，须重建
            {
                clients[lang].Dispose();
                clients.Remove(lang);
                clientRoots.Remove(lang);
                lastUsed.Remove(lang);
            }
            // 最近 30 秒内失败过：直接抛缓存原因，不反复拉起（首启初始化可达数秒）
            if (lastFail.TryGetValue(lang, out var why) && why != null && DateTime.Now - lastFailTime[lang] < TimeSpan.FromSeconds(30))
                throw new InvalidOperationException(why);

            if (lang == "csharp")
            {
                if (serverExe == null)
                {
                    serverExe = FindServerExe();
                    if (serverExe == null)
                    {
                        lastFail["csharp"] = "未找到 C# 语言服务器，请先在 system.ini [LSP] ServerPath 指定路径（如 D:\\tools\\roslyn-ls\\...\\Microsoft.CodeAnalysis.LanguageServer.exe）";
                        lastFailTime["csharp"] = DateTime.Now;
                        Status("C# 语言服务器不可用：未找到 Roslyn 入口");
                        throw new InvalidOperationException(lastFail["csharp"]);
                    }
                }
            }
            else
            {
                // Java/Vue：环境确保（缺则按 system.ini 自动下载到 lsp\ 子目录）
                var envErr = await LspEnv.EnsureAsync(lang, Status, ct);
                if (envErr != null)
                {
                    lastFail[lang] = envErr;
                    lastFailTime[lang] = DateTime.Now;
                    Status($"{LspEnv.NameOf(lang)}语言服务器不可用：{envErr}");
                    throw new InvalidOperationException(envErr);
                }
            }

            var psi = LspEnv.PsiFor(lang, root);
            if (psi == null)
            {
                lastFail[lang] = "语言服务器组件未就绪（环境自检后仍缺）";
                lastFailTime[lang] = DateTime.Now;
                throw new InvalidOperationException(lastFail[lang]);
            }

            Status($"{LspEnv.NameOf(lang)}语言服务器初始化中（首次约 5~30 秒）…");
            var sw = Stopwatch.StartNew();
            try
            {
                var cl = await LspClient.StartAsync(psi, root, ct, LspEnv.InitializeOptionsFor(lang));
                clients[lang] = cl;
                clientRoots[lang] = root;
                lastUsed[lang] = DateTime.Now;
                lastFail.Remove(lang);
                lastFailTime.Remove(lang);
                Status($"{LspEnv.NameOf(lang)}语言服务器就绪（{sw.Elapsed.TotalSeconds:F1}s）");
                return cl;
            }
            catch (Exception ex)
            {
                lastFail[lang] = $"语言服务器启动失败：{ex.Message}";
                lastFailTime[lang] = DateTime.Now;
                Status($"{LspEnv.NameOf(lang)}语言服务器启动失败：{ex.Message}");
                throw new InvalidOperationException(lastFail[lang]);
            }
        }
        finally { gate.Release(); }
    }

    /// <summary>空闲回收：超过配置分钟未使用则优雅退出，释放进程与内存（下次使用自动重启）</summary>
    void CheckRecycle()
    {
        if (clients.Count == 0) return;
        var idle = TimeSpan.FromMinutes(SystemCfg.LspIdleRecycleMin);
        if (!clients.Any(kv => DateTime.Now - lastUsed[kv.Key] > idle)) return;
        gate.Wait();
        try
        {
            foreach (var kv in clients.ToList())
            {
                if (DateTime.Now - lastUsed[kv.Key] <= idle) continue;
                kv.Value.Dispose();
                clients.Remove(kv.Key);
                clientRoots.Remove(kv.Key);
                lastUsed.Remove(kv.Key);
                Status($"{LspEnv.NameOf(kv.Key)}语言服务器已空闲回收（>{SystemCfg.LspIdleRecycleMin} 分钟未使用）");
            }
        }
        finally { gate.Release(); }
    }

    /// <summary>退出时释放（MainWindow.OnClosed 调用）</summary>
    public void Shutdown()
    {
        recycleTimer.Dispose();
        gate.Wait();
        try
        {
            foreach (var c in clients.Values) c.Dispose();
            clients.Clear();
            clientRoots.Clear();
        }
        finally { gate.Release(); }
    }
}