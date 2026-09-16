using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GAIRR.Core;

/// <summary>工具注册表：Schema 发给模型，tool_calls 按名分发执行</summary>
public class ToolRegistry
{
    public record ToolDef(JsonObject Schema, Func<JsonObject, CancellationToken, Task<string>> Handler);

    readonly Dictionary<string, ToolDef> tools = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> disabled = new(StringComparer.OrdinalIgnoreCase);
    readonly object gate = new();   // tools/disabled 统一加锁：插件热加载在后台线程注册，任务线程并发读，需防竞态

    /// <summary>获取已注册的所有工具名称（供 UI 列表展示）</summary>
    public List<string> ToolNames => tools.Keys.OrderBy(k => k).ToList();

    /// <summary>禁用/启用指定工具（禁用后 Agent 不可见该工具）</summary>
    public void SetEnabled(string name, bool enabled)
    {
        lock (gate)
        {
            if (enabled) disabled.Remove(name);
            else disabled.Add(name);
        }
    }

    /// <summary>检查工具是否被禁用</summary>
    public bool IsEnabled(string name) { lock (gate) return !disabled.Contains(name); }

    /// <summary>检查工具是否已注册</summary>
    public bool IsRegistered(string name) { lock (gate) return tools.ContainsKey(name); }

    /// <summary>取工具注册时登记的用途描述（Schema.function.description），供 UI 侧栏展示；未注册或无描述返回空串</summary>
    public string GetDescription(string name)
    {
        lock (gate)
        {
            if (!tools.TryGetValue(name, out var def)) return "";
            var desc = def.Schema["function"]?["description"]?.GetValue<string>();
            return desc ?? "";
        }
    }

    public void Register(string name, JsonObject schema, Func<JsonObject, CancellationToken, Task<string>> handler)
    {
        lock (gate) tools[name] = new ToolDef(schema, handler);
    }

    /// <summary>注销指定工具（市场卸载插件时摘除注册；不存在时静默跳过）</summary>
    public void Unregister(string name)
    {
        lock (gate)
        {
            tools.Remove(name);
            disabled.Remove(name);
        }
    }

    public JsonArray Schemas()
    {
        lock (gate)
        {
            var arr = new JsonArray();
            foreach (var (name, def) in tools)
                if (!disabled.Contains(name))
                    arr.Add(def.Schema.CloneObj());
            return arr;
        }
    }

    public async Task<string> ExecuteAsync(string name, string argumentsJson, CancellationToken ct)
    {
        ToolDef? def;
        bool registered;
        lock (gate)
        {
            if (disabled.Contains(name))
                return "错误：工具 " + name + " 已被禁用";
            registered = tools.TryGetValue(name, out def);
        }
        if (!registered) return UnknownToolHint(name);   // 未注册：回纠错反馈（含可用工具清单），让模型下一轮改对
        JsonObject args;
        try { args = JsonObject.Parse(argumentsJson) as JsonObject ?? new JsonObject(); }
        catch
        {
            // 参数 JSON 解析失败（最常见成因：模型流截断留下半截参数）：落日志留痕，避免静默吞掉难以排查
            try { File.AppendAllText(Paths.AgentLog, $"[{DateTime.Now:HH:mm:ss.fff}] [Tools] 工具 {name} 参数 JSON 解析失败，已降级为空参数: {LLMClient.Trunc(argumentsJson, 200)}\n"); } catch { }
            args = new JsonObject();
        }
        try { return await def!.Handler(args, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return "工具执行异常：" + ex.Message; }
    }

    /// <summary>未知工具名的纠错反馈：模型臆造/拼错工具名（典型如把 Read 的参数名 "path" 当成工具名）时，
    /// 回一条"该名称更像哪个工具的参数 + 名称最接近的可用工具 + 当前可用工具清单"的结果，
    /// 让它下一轮直接改对，而不是反复瞎试。前缀保留"错误：未知工具"（ProjectMapAuto 依此识别伪经验、不入经验库）。</summary>
    string UnknownToolHint(string name)
    {
        List<string> usable;
        var owners = new List<string>();
        lock (gate)
        {
            usable = tools.Keys.Where(n => !disabled.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();
            foreach (var (n, d) in tools)
            {
                if (disabled.Contains(n)) continue;
                if (d.Schema?["function"]?["parameters"]?["properties"] is JsonObject props
                    && props.Any(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)))
                    owners.Add(n);
            }
        }
        var near = usable.Where(n => n.StartsWith(name, StringComparison.OrdinalIgnoreCase)
                                  || name.StartsWith(n, StringComparison.OrdinalIgnoreCase)
                                  || n.Contains(name, StringComparison.OrdinalIgnoreCase))
                         .Take(5).ToList();

        var sb = new StringBuilder("错误：未知工具 " + name + "。");
        sb.Append('「').Append(name).Append("」不是可用工具名（常见成因：把工具的参数名当成了工具名，或工具名拼错）。");
        if (owners.Count > 0)
            sb.Append("据参数定义，它更像是 ").Append(string.Join("、", owners.Take(6)))
              .Append(" 的参数名——请调用这些工具本身，把 ").Append(name).Append(" 放进参数里。");
        if (near.Count > 0)
            sb.Append("名称最接近的可用工具：").Append(string.Join("、", near)).Append('。');
        sb.Append("当前可用工具：").Append(string.Join("、", usable.Take(40)));
        if (usable.Count > 40) sb.Append(" 等 ").Append(usable.Count).Append(" 个");
        sb.Append("。请改用上述工具之一重新调用，不要重复调用不存在的工具。");
        return sb.ToString();
    }

    /* ---------- Schema 小助手（public：壳工程注册工具时跨程序集使用） ---------- */

    public static JsonObject Fn(string name, string description, JsonObject properties, JsonArray required) => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = required,
            },
        },
    };

    public static JsonObject Str(string description) => new() { ["type"] = "string", ["description"] = description };
    public static JsonObject Int(string description) => new() { ["type"] = "integer", ["description"] = description };
}

/// <summary>阶段一首批工具：Read / ListDir / Bash（编码防线 + 超时强杀照开发方案 11.4）</summary>
public static class Phase1Tools
{
    // 忽略清单/危险模式/各类上限统一由 SystemCfg（system.ini）集中配置

    /// <summary>危险命令拦截开关：由 MainWindow 技能栏同步，禁用后危险命令直接放行（不再拦截/确认）</summary>
    public static bool DangerInterceptionEnabled { get; set; } = true;

    public static void RegisterAll(ToolRegistry reg, AppConfig cfg)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);   // GBK 回退需要

        reg.Register("Read", ToolRegistry.Fn(
            "Read",
            SystemCfg.Desc("Read",
                "读取指定文件的内容。当需要查看已有文件的源码、配置或内容时使用。不要用于查看目录列表（用 ListDir）。单次结果有字符预算（默认约 4 万字符，超出只返回头尾），读大文件请优先用 start_line/end_line 按范围读，不要一次读全文。不确定文件路径时，先用 Map 浏览目录、SmartSearch q=关键词 语义搜索、或 MapTrace q=类名/方法名 符号检索确认正确路径，不要凭记忆猜测。"),
            new JsonObject
            {
                ["path"] = ToolRegistry.Str("文件的绝对路径或相对于项目根目录的路径，如 doc/方案.md"),
                ["start_line"] = ToolRegistry.Int("起始行号（从1开始）。文件很大时只读需要的部分"),
                ["end_line"] = ToolRegistry.Int("结束行号（含）"),
            },
            new JsonArray { "path" }),
            (a, ct) => Task.FromResult(Read(cfg, a)));

        reg.Register("ListDir", ToolRegistry.Fn(
            "ListDir",
            SystemCfg.Desc("ListDir",
                "列出目录下的文件和子目录名称。只返回路径清单，不读内容。需要看文件内容请用 Read。不确定目录路径时，先用 Map 浏览项目结构确认。"),
            new JsonObject
            {
                ["path"] = ToolRegistry.Str("目录路径，可为空（空=项目根目录）"),
                ["recursive"] = new JsonObject { ["type"] = "boolean", ["description"] = "是否递归子目录，默认 false" },
            },
            new JsonArray()),
            (a, ct) => Task.FromResult(ListDir(cfg, a)));

        reg.Register("Bash", ToolRegistry.Fn(
            "Bash",
            SystemCfg.Desc("Bash",
                "在 Windows cmd 下执行命令并返回输出（支持 &&，不要写 PowerShell 语法）。用于编译、运行脚本等。删除类危险命令会被拦截。"),
            new JsonObject
            {
                ["command"] = ToolRegistry.Str("要执行的命令，如 dir /b 或 cd /d x && y"),
                ["timeout"] = ToolRegistry.Int("超时秒数，默认取配置值"),
            },
            new JsonArray { "command" }),
            (a, ct) => Bash(cfg, a, ct));

        reg.Register("DbQuery", ToolRegistry.Fn(
            "DbQuery",
            SystemCfg.Desc("DbQuery",
                "执行 SQLite 数据库查询。支持 SELECT/INSERT/UPDATE/DELETE，返回结果表格。"),
            new JsonObject
            {
                ["db"] = ToolRegistry.Str("数据库文件路径（.db/.sqlite），相对于项目根目录"),
                ["sql"] = ToolRegistry.Str("SQL 语句"),
            },
            new JsonArray { "db", "sql" }),
            (a, ct) => Task.FromResult(DbQuery(cfg, a)));
    }

    internal static string Resolve(AppConfig cfg, string path)
    {
        path = path.Replace('/', Path.DirectorySeparatorChar).Trim();
        return Path.IsPathRooted(path) ? path : Path.Combine(cfg.ProjectRoot, path);
    }

    /// <summary>路径定位（严格校验）：先按原样解析；路径存在直接返回，
    /// 不存在时返回空串，note 给出错误提示并引导使用 Map/SmartSearch/MapTrace 定位。</summary>
    internal static string Locate(AppConfig cfg, string path, out string note)
    {
        var resolved = Resolve(cfg, path);
        if (File.Exists(resolved) || Directory.Exists(resolved)) { note = ""; return resolved; }

        var name = Path.GetFileName(resolved.TrimEnd('\\', '/'));
        var matches = new List<string>();
        if (name.Length > 0 && Directory.Exists(cfg.ProjectRoot))
        {
            foreach (var f in EnumerateFiles(cfg.ProjectRoot))
                if (string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase)) matches.Add(f);
            CollectDirMatches(cfg.ProjectRoot, name, matches);
        }
        matches = matches.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var head = "错误：路径不存在 " + Rel(cfg, resolved);
        note = head + "；不要猜测，请先用 MapTrace q=" + name + " 或 SmartSearch q=" + name + " 定位正确路径"; 
        return "";
    }

    /// <summary>候选清单条数上限（多命中提示用，防刷屏）</summary>
    const int LocateMaxCandidates = 5;

    /// <summary>递归收集目录名匹配（忽略大小写，跳忽略目录），供 Locate 纠正目录路径</summary>
    static void CollectDirMatches(string root, string name, List<string> matches)
    {
        foreach (var d in Directory.GetDirectories(root))
        {
            if (SystemCfg.IgnoreDirs.Contains(Path.GetFileName(d))) continue;
            if (string.Equals(Path.GetFileName(d), name, StringComparison.OrdinalIgnoreCase)) matches.Add(d);
            try { CollectDirMatches(d, name, matches); } catch { }
        }
    }

    /// <summary>绝对路径 → 项目内相对路径展示（根外原样），便于错误消息可读</summary>
    static string Rel(AppConfig cfg, string fullPath)
    {
        try
        {
            var r = Path.GetRelativePath(cfg.ProjectRoot, fullPath);
            return r.StartsWith("..", StringComparison.Ordinal) ? fullPath : r;
        }
        catch { return fullPath; }
    }

    /// <summary>递归遍历目录下文件（跳忽略目录/扩展名），供 Grep/Glob/Locate 复用。
    /// 过滤层：全局 IgnoreDirs/Exts（固定） + 项目级 .gairr/ignore.json + AI 扫描 .gairr/ai-ignore.json（模型判断）。</summary>
    internal static IEnumerable<string> EnumerateFiles(string root)
    {
        // 项目级忽略清单（.gairr/ignore.json，随项目走）：前缀命中即整棵子树跳过，扩展名按文件过滤
        var ig = SystemCfg.LoadProjectIgnore(root);
        // AI 扫描判断清单（.gairr/ai-ignore.json，模型生成）：与固定清单叠加
        var aiIg = ProjectMapAuto.AiIgnore.Load(root);
        var rootIg = SystemCfg.ProjectIgnoreHits(ig, "") || (aiIg != null && aiIg.Hits(""));
        foreach (var d in Directory.GetDirectories(root))
        {
            if (SystemCfg.IgnoreDirs.Contains(Path.GetFileName(d))) continue;
            if (SystemCfg.ProjectIgnoreDirName(ig, Path.GetFileName(d))) continue;
            if (!rootIg)
            {
                var rel = Path.GetRelativePath(root, d).Replace('\\', '/');
                if (SystemCfg.ProjectIgnoreHits(ig, rel)) continue;
                if (aiIg != null && aiIg.Hits(rel)) continue;
            }
            IEnumerable<string> sub;
            try { sub = EnumerateFiles(d); } catch { continue; }
            foreach (var f in sub) yield return f;
        }
        foreach (var f in Directory.GetFiles(root))
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (SystemCfg.IgnoreExts.Contains(ext)) continue;
            if (!rootIg)
            {
                var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
                if (SystemCfg.ProjectIgnoreHits(ig, rel)) continue;
                if (aiIg != null && aiIg.Hits(rel)) continue;
            }
            yield return f;
        }
    }

    /* ---------- Read：BOM 探测 → UTF-8 严格解码 → GBK 回退 ---------- */

    static string Read(AppConfig cfg, JsonObject a)
    {
        var path = Locate(cfg, a["path"]?.GetValue<string>() ?? "", out var locateNote);
        if (path.Length == 0) return locateNote;
        if (!File.Exists(path)) return "错误：文件不存在 " + path + locateNote;

        var bytes = File.ReadAllBytes(path);
        FileSnapshot.Record(path);   // 读成功即记录时间戳快照：后续 Write/Edit 写盘前据此检测并发改动
        var (text, encName) = Decode(bytes);
        var lines = text.Replace("\r\n", "\n").Split('\n');

        var start = a["start_line"]?.GetValue<int>() ?? 1;
        var end = a["end_line"]?.GetValue<int>() ?? lines.Length;
        start = Math.Max(1, start);
        end = Math.Min(lines.Length, end);
        if (end - start + 1 > SystemCfg.ReadMaxLines) { end = start + SystemCfg.ReadMaxLines - 1; }   // 大文件防护

        var sb = new StringBuilder();
        sb.Append($"[{encName} · 共 {lines.Length} 行 · 返回 {start}-{end}]\n");
        for (var i = start; i <= end; i++) sb.Append(i).Append('→').Append(lines[i - 1]).Append('\n');
        if (end < lines.Length) sb.Append("（文件更长，需要后续部分请用 start_line/end_line 分段读）");
        var full = sb.ToString();
        if (SystemCfg.ReadMaxChars > 0 && full.Length > SystemCfg.ReadMaxChars)
        {
            // 字符预算截断：单条 Read 结果过大是全链路最大上下文消耗源（2000 行×80 字 ≈ 40K+ token），
            // 超预算时保留头尾（头部多留：文件头注释/声明多在开头）+ 省略提示；模型可据可见行号用 start_line/end_line 重取中间段
            var headLen = SystemCfg.ReadMaxChars * 7 / 10;
            var tailLen = SystemCfg.ReadMaxChars - headLen;
            var headCut = full[..headLen].LastIndexOf('\n') + 1;   // 尽量按行边界截断，避免半行
            if (headCut <= 0) headCut = headLen;                   // 超长单行（minified 类）按字符硬切
            var nl = full.IndexOf('\n', full.Length - tailLen);
            var tailCut = nl < 0 ? full.Length - tailLen : nl + 1;
            if (tailCut <= headCut) return full;
            return full[..headCut] +
                $"\n…（Read 结果超字符预算：共 {full.Length} 字符，已省略中间部分；需全文请用 start_line/end_line 分段重取）…\n" +
                full[tailCut..];
        }
        return full;
    }

    /// <summary>字节解码：BOM/UTF-16 探测 → UTF-8 严格 → GBK 回退（与 Read 展示同源，UI 快照查看复用保证行号一致）</summary>
    public static (string Text, string Enc) Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), "UTF-8 BOM");
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), "UTF-16 LE");
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), "UTF-16 BE");
        try
        {
            var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
            return (strict.GetString(bytes), "UTF-8 无 BOM");
        }
        catch
        {
            return (Encoding.GetEncoding(936).GetString(bytes), "GBK");
        }
    }

    /* ---------- ListDir：忽略清单 + 条数上限 ---------- */

    static string ListDir(AppConfig cfg, JsonObject a)
    {
        var path = Locate(cfg, a["path"]?.GetValue<string>() ?? "", out var locateNote);
        if (path.Length == 0) return locateNote;
        if (!Directory.Exists(path)) return "错误：目录不存在 " + path + locateNote;
        var recursive = a["recursive"]?.GetValue<bool>() ?? false;

        var sb = new StringBuilder();
        var count = 0;
        Walk(path, "");
        sb.Append($"（共 {count} 项）");
        return sb.ToString();

        void Walk(string dir, string prefix)
        {
            foreach (var d in Directory.GetDirectories(dir))
            {
                var name = Path.GetFileName(d);
                if (SystemCfg.IgnoreDirs.Contains(name)) continue;
                if (count >= SystemCfg.ListMaxItems) return;
                sb.Append(prefix).Append("▸ ").Append(name).Append("/\n");
                count++;
                if (recursive) Walk(d, prefix + "  ");
            }
            foreach (var f in Directory.GetFiles(dir))
            {
                if (SystemCfg.IgnoreExts.Contains(Path.GetExtension(f).ToLowerInvariant())) continue;
                if (count >= SystemCfg.ListMaxItems) return;
                sb.Append(prefix).Append("· ").Append(Path.GetFileName(f)).Append('\n');
                count++;
            }
        }
    }

    /* ---------- Bash：cmd /c + 管道捕获 + 超时强杀 + 危险命令弹窗确认 ---------- */

    static async Task<string> Bash(AppConfig cfg, JsonObject a, CancellationToken ct)
    {
        var cmd = a["command"]?.GetValue<string>() ?? "";
        if (cmd.Length == 0) return "错误：command 为空";
        var timeout = a["timeout"]?.GetValue<int>() ?? cfg.CommandTimeout;
        return await RunCmd(cfg, cmd, timeout, ct, ConfirmDangerAsync);
    }

    /// <summary>危险命令确认处理器（单播注册制：后注册者替换前注册者）。
    /// UI 模式由 AgentLoop 默认注册会话内确认（提示 + 允许/取消按钮）；
    /// 多会话宿主（gairr-agent-server）注册 DangerHub 路由后按 AsyncLocal 分发到发起会话。
    /// 无人注册（后台/插件上下文）时返回 false 直接拒绝。</summary>
    public static Func<string, string, Task<bool>>? DangerConfirmHandler;

    internal static Task<bool> ConfirmDangerAsync(string cmd, string pattern)
        => DangerConfirmHandler?.Invoke(cmd, pattern) ?? Task.FromResult(false);

    /// <summary>统一危险命令检查：所有经 RunCmd 执行的命令（Bash/插件/Hooks）共用同一套模式。
    /// 检查前对命令做清洗：小写、归并空白、去掉 cmd ^ 转义；
    /// 关键词须独立成命令词才命中（前方为空白/分隔符或行首，等于带词前空白边界），避免子串误报。
    /// 技能栏关闭"危险命令拦截"时直接放行。</summary>
    internal static bool ContainsDangerousPattern(string cmd, out string matched)
    {
        matched = "";
        if (!DangerInterceptionEnabled) return false;
        var normalized = NormalizeCmdForDangerCheck(cmd);
        foreach (var p in SystemCfg.DangerPatterns)
        {
            // 命中点前方必须是空白或命令分隔符，否则只是普通子串：
            // 如 "model update" 含 "del "、"cat file" 含 "at "、路径 \shutdown_utils 含 "shutdown"，都不应误报。
            // 模式自带尾空格（"del " 型）仍要求词后有分隔；"rm -"、"git push" 等危险前缀型保持后接任意参数。
            int i = normalized.IndexOf(p, StringComparison.Ordinal);
            while (i >= 0)
            {
                if (i == 0 || IsCmdWordBoundary(normalized[i - 1]))
                {
                    matched = p;
                    return true;
                }
                i = normalized.IndexOf(p, i + 1, StringComparison.Ordinal);
            }
        }
        return false;
    }

    /// <summary>命令词前界字符判定：空白与 cmd 分隔符均可直接引领命令词（如 "&&del"、"(del ..."），
    /// 算边界则不会给紧贴写法的绕过留洞。</summary>
    static bool IsCmdWordBoundary(char c) => char.IsWhiteSpace(c) || c is '&' or '|' or ';' or '(' or ')';

    /// <summary>危险命令检查前的归一化：小写、连续空白变单空格、去掉 cmd 转义符 ^、去掉双引号。</summary>
    static string NormalizeCmdForDangerCheck(string cmd)
    {
        var sb = new StringBuilder(cmd.Length);
        var prevSpace = false;
        foreach (var ch in cmd.ToLowerInvariant())
        {
            if (ch == '^') continue;          // 去掉 CMD 转义符（常见绕过：d^e^l）
            if (ch == '"') continue;          // 去掉引号
            if (char.IsWhiteSpace(ch))
            {
                if (!prevSpace) sb.Append(' ');
                prevSpace = true;
                continue;
            }
            prevSpace = false;
            sb.Append(ch);
        }
        return sb.ToString().Trim();
    }

    /// <summary>cmd /c + 管道捕获 + 超时强杀 + GBK→UTF-8；统一危险命令审查。
    /// 只有显式传入 confirm 的调用方（如 Bash 用户命令）才进入会话确认；后台/插件/Hooks 未传确认时直接拒绝。</summary>
    internal static async Task<string> RunCmd(AppConfig cfg, string cmd, int timeoutSec, CancellationToken ct, Func<string, string, Task<bool>>? confirm = null)
    {
        if (ContainsDangerousPattern(cmd, out var pattern))
        {
            if (confirm == null)
                return "拦截：危险命令（" + pattern.Trim() + "）已被统一审查拒绝";
            if (!await confirm(cmd, pattern))
                return "拦截：危险命令（" + pattern.Trim() + "）操作已取消";
        }

        var timeout = timeoutSec * 1000;
        var psi = new ProcessStartInfo("cmd.exe", "/c " + cmd)
        {
            WorkingDirectory = cfg.ProjectRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // 不设置控制台代码页：cmd 子进程输出按各自默认（多数为 GBK），
            // 由 DetectDecode 在拿到原始字节后按内容自动选 UTF-8/GBK，兼容 dotnet 等 UTF-8 工具
        };
        // 自带运行时注入：把 envs\ 下已就绪的 python/node/java 目录追加到子进程 PATH 末尾。
        // 系统 PATH 由 ProcessStartInfo 自动继承，故自带目录仅作裸命令 python/node/java 的兜底（不覆盖用户系统同款），
        // 让 SKILL.md / Bash 直接敲 python script.py、node x.js、java -jar 也能命中 GAIRR 自带运行时。
        var ownBins = RuntimeEnv.OwnBinsPath();
        if (ownBins.Length > 0)
        {
            var cur = psi.Environment.TryGetValue("PATH", out var p) && !string.IsNullOrEmpty(p) ? p : "";
            psi.Environment["PATH"] = cur.Length > 0 ? cur + ";" + ownBins : ownBins;
        }
        using var proc = Process.Start(psi);
        if (proc == null) return "错误：进程启动失败";

        var outTask = ReadBytesAsync(proc.StandardOutput, ct);
        var errTask = ReadBytesAsync(proc.StandardError, ct);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { /* 已退出 */ }   // 无论超时还是用户停止，都先强杀子进程树
            if (ct.IsCancellationRequested) throw;   // 用户停止：杀完继续向上传播取消
            return $"超时：命令执行超过 {timeout / 1000} 秒，已强杀";
        }

        var output = DetectDecode(await outTask);
        var err = DetectDecode(await errTask);
        var sb = new StringBuilder();
        sb.Append("退出码 ").Append(proc.ExitCode).Append('\n');
        if (output.Length > 0) sb.Append(output);
        if (err.Length > 0) sb.Append("\n[stderr]\n").Append(err);
        return LLMClient.Trunc(sb.ToString(), SystemCfg.OutputMaxChars);
    }

    /// <summary>读取子进程输出原始字节（不依赖 StreamReader 默认编码，解码交给 DetectDecode）</summary>
    static async Task<byte[]> ReadBytesAsync(StreamReader sr, CancellationToken ct)
    {
        var ms = new MemoryStream();
        var buf = new byte[8192];
        var baseStream = sr.BaseStream;
        int n;
        while ((n = await baseStream.ReadAsync(buf, ct)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > 1_000_000) break;   // 防爆
        }
        return ms.ToArray();
    }

    /// <summary>子进程输出字节解码：优先按 UTF-8 严格解码；不是合法 UTF-8（或纯 ASCII）时回退 GBK。
    /// 兼容两类输出：dotnet/node 等按 UTF-8 输出、cmd 内置命令与多数 exe 按 GBK 输出。</summary>
    static string DetectDecode(byte[] bytes)
    {
        if (bytes.Length == 0) return "";
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);   // UTF-8 BOM
        var asciiOnly = true;
        foreach (var b in bytes) { if (b > 0x7F) { asciiOnly = false; break; } }
        if (asciiOnly) return Encoding.ASCII.GetString(bytes);
        try
        {
            var strict = new UTF8Encoding(false, true);
            return strict.GetString(bytes);   // 合法 UTF-8 → 用之（dotnet 等）
        }
        catch (DecoderFallbackException) { }
        return Encoding.GetEncoding(936).GetString(bytes);   // 非法 UTF-8 → 按 GBK（cmd/国产工具）
    }

    /* ---------- DbQuery：SQLite 直连 ---------- */

    static string DbQuery(AppConfig cfg, JsonObject a)
    {
        var dbPath = Locate(cfg, a["db"]?.GetValue<string>() ?? "", out var locateNote);
        var sql = a["sql"]?.GetValue<string>() ?? "";
        if (dbPath.Length == 0) return locateNote;
        if (!File.Exists(dbPath)) return "错误：数据库文件不存在 " + dbPath + locateNote;
        if (string.IsNullOrWhiteSpace(sql)) return "错误：SQL 为空";

        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            var lower = sql.TrimStart().ToLowerInvariant();
            if (lower.StartsWith("select"))
            {
                using var reader = cmd.ExecuteReader();
                var sb = new StringBuilder();
                var cols = new List<string>();
                for (int i = 0; i < reader.FieldCount; i++) cols.Add(reader.GetName(i));
                sb.AppendLine("| " + string.Join(" | ", cols) + " |");
                sb.AppendLine("|" + string.Join("|", cols.Select(_ => "---")) + "|");
                int rows = 0;
                while (reader.Read() && rows < 100)
                {
                    var vals = new List<string>();
                    for (int i = 0; i < reader.FieldCount; i++)
                        vals.Add(reader.IsDBNull(i) ? "NULL" : reader.GetValue(i)?.ToString() ?? "");
                    sb.AppendLine("| " + string.Join(" | ", vals) + " |");
                    rows++;
                }
                if (rows >= 100) sb.AppendLine("（仅显示前 100 行）");
                return sb.ToString();
            }
            else
            {
                var affected = cmd.ExecuteNonQuery();
                return $"执行成功，影响 {affected} 行";
            }
        }
        catch (Exception ex) { return "数据库错误：" + ex.Message; }
    }
}
