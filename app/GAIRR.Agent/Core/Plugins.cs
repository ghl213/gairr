using System.IO;
using System.Text.Json.Nodes;

namespace GAIRR.Core;

/// <summary>声明式命令插件：plugins/ 下每个插件一个子目录，目录内必须包含 plugin.ini。
/// 子目录结构让脚本、配置与声明自包含，便于迁移和版本管理。
/// （Name/Description/Timeout/Command 模板 + ArgN=参数名|描述），启动时生成 Schema 注册进 ToolRegistry；
/// 执行时把 {参数} 占位符替换后走 cmd 管道（复用 Bash 的超时/编码防线，也受统一危险命令审查拦截）。
/// 热加载：Watch 监视目录内 *.ini 变化，防抖 2 秒后全量重扫重注册（同名覆盖），模型当轮写好的插件当轮即可调用。</summary>
public static class PluginLoader
{
    /// <summary>热加载完成事件：插件目录 ini 变化触发重扫后回调最新插件列表（UI 订阅刷新插件面板）</summary>
    public static event Action<List<PluginDef>>? Reloaded;

    /// <summary>热加载目标：每个会话各自的 (注册表, 配置)，目录级 watcher 共享、注册互不串扰</summary>
    static readonly List<(ToolRegistry Reg, AppConfig Cfg)> watchTargets = new();
    static readonly List<FileSystemWatcher> watchers = new();   // 持强引用：防 watcher 被 GC 导致监视失效
    static Timer? debounceTimer;

    /// <summary>解析插件目录（相对路径基于 exe 运行目录）</summary>
    static string PluginDir(AppConfig cfg)
    {
        var raw = cfg.Get("Plugins", "Dir", "plugins");
        return Path.IsPathRooted(raw) ? raw : Path.Combine(AppContext.BaseDirectory, raw);
    }

    /// <summary>解析技能目录（与 SkillLoader 同规则，供 {skills} 占位符引用）</summary>
    static string SkillDir(AppConfig cfg)
    {
        var raw = cfg.Get("Skills", "Dir", "skills");
        return Path.IsPathRooted(raw) ? raw : Path.Combine(AppContext.BaseDirectory, raw);
    }

    /// <summary>扫描插件目录并注册，返回插件定义列表（含加载成功/失败状态）</summary>
    public static List<PluginDef> Load(ToolRegistry reg, AppConfig cfg)
    {
        var plugins = new List<PluginDef>();
        var dir = PluginDir(cfg);
        if (!Directory.Exists(dir))
        {
            // 目录不存在：添加一个空状态占位（UI 显示"暂无插件"）
            return plugins;
        }

        // 兼容旧版平铺 ini：直接放在 plugins/ 下的 .ini 仍可加载
        foreach (var ini in Directory.GetFiles(dir, "*.ini"))
            TryLoad(ini, reg, cfg, plugins);

        // 新版目录化结构：plugins/<插件名>/plugin.ini
        foreach (var subDir in Directory.GetDirectories(dir))
        {
            var pluginIni = Path.Combine(subDir, "plugin.ini");
            if (File.Exists(pluginIni))
                TryLoad(pluginIni, reg, cfg, plugins);
        }

        return plugins;
    }

    /// <summary>启动热加载监视（每个会话调用一次；多会话各自登记注册目标，共享目录级 watcher）。
    /// 仅监视 *.ini：脚本/配置文件变更无需重注册（Command 模板执行时实时读脚本）。</summary>
    public static void Watch(ToolRegistry reg, AppConfig cfg)
    {
        lock (watchTargets)
        {
            if (!watchTargets.Any(t => ReferenceEquals(t.Reg, reg)))
                watchTargets.Add((reg, cfg));

            var dir = PluginDir(cfg);
            if (!Directory.Exists(dir)) return;   // 目录不存在暂不监视（用户创建后重启或重开应用生效）
            var norm = Path.GetFullPath(dir);
            if (watchers.Any(w => string.Equals(Path.GetFullPath(w.Path), norm, StringComparison.OrdinalIgnoreCase)))
                return;   // 该目录已有 watcher，复用
            try
            {
                var w = new FileSystemWatcher(norm, "*.ini") { IncludeSubdirectories = true, InternalBufferSize = 64 * 1024 };
                w.Changed += OnIniChanged; w.Created += OnIniChanged; w.Deleted += OnIniChanged; w.Renamed += OnIniChanged;
                w.EnableRaisingEvents = true;
                watchers.Add(w);
            }
            catch { /* 监视失败不影响启动加载：退回重启生效 */ }
        }
    }

    /// <summary>ini 变化回调：2 秒防抖（Write 工具多事件/连续保存合并），到期后台全量重扫</summary>
    static void OnIniChanged(object sender, FileSystemEventArgs e)
    {
        lock (watchTargets)
        {
            debounceTimer?.Dispose();
            debounceTimer = new Timer(_ => Task.Run(ReloadAll), null, 2000, Timeout.Infinite);
        }
    }

    /// <summary>防抖到期执行：对每个已登记会话全量重扫重注册（ToolRegistry.Register 同名覆盖），完成后广播最新列表</summary>
    static void ReloadAll()
    {
        List<(ToolRegistry Reg, AppConfig Cfg)> targets;
        lock (watchTargets) targets = watchTargets.ToList();
        List<PluginDef>? last = null;
        foreach (var (reg, cfg) in targets)
        {
            try { last = Load(reg, cfg); }
            catch { /* 单个会话重扫失败不影响其他会话 */ }
        }
        if (last != null)
        {
            try { Reloaded?.Invoke(last); } catch { /* 订阅方异常不影响注册结果 */ }
        }
    }

    static void TryLoad(string ini, ToolRegistry reg, AppConfig cfg, List<PluginDef> plugins)
    {
        var fileName = Path.GetFileName(ini);
        Dictionary<string, string> kv;
        try
        {
            kv = ReadIni(ini);
        }
        catch
        {
            // ini 解析失败
            plugins.Add(new PluginDef(fileName, "配置文件解析失败", ini, false));
            return;
        }

        var name = Get(kv, "Name", Path.GetFileNameWithoutExtension(ini));
        var desc = Get(kv, "Description", "");
        var command = Get(kv, "Command", "");

        if (command.Length == 0)
        {
            // 缺少 Command 必填项
            plugins.Add(new PluginDef(name, "缺少 Command 配置", ini, false));
            return;
        }

        var timeout = int.TryParse(Get(kv, "Timeout", ""), out var t) ? t : cfg.CommandTimeout;

        // 简化参数声明：Arg1=参数名|描述（框架生成 Schema，用户不写 JSON Schema）
        var args = new List<(string Name, string Desc)>();
        for (var i = 1; i <= 8; i++)
        {
            var a = Get(kv, "Arg" + i, "");
            if (a.Length == 0) continue;
            var pi = a.IndexOf('|');
            args.Add(pi > 0 ? (a[..pi].Trim(), a[(pi + 1)..].Trim()) : (a.Trim(), a.Trim()));
        }

        var props = new JsonObject();
        var req = new JsonArray();
        foreach (var (an, ad) in args) { props[an] = ToolRegistry.Str(ad); req.Add(an); }
        if (args.Count == 0) props["input"] = ToolRegistry.Str("传给插件的输入（可选）");

        reg.Register(name, ToolRegistry.Fn(name, desc.Length > 0 ? desc : "自定义插件：" + name, props, req),
            (a, ct) => Phase1Tools.RunCmd(cfg, Fill(cfg, command, args, a), timeout, ct));
        // 插件开关：plugin.ini 的 Enabled=0 表示禁用（缺省/1=启用，配置只写例外）。
        // 热加载重扫会重新注册，Enabled 必须从 ini 读回，否则 UI 切换会被重扫覆盖丢失。
        var enabled = !Get(kv, "Enabled", "1").Equals("0", StringComparison.Ordinal);
        if (!enabled) reg.SetEnabled(name, false);   // 禁用插件对 Agent 不可见
        plugins.Add(new PluginDef(name, desc, ini, true) { Enabled = enabled });
    }

    /// <summary>占位符替换：{参数} + 内置 {project}/{python}/{node}/{java}/{config}/{plugins}/{skills}；参数值统一做命令行转义。
    /// {plugins}/{skills} 指向运行时插件/技能根目录——市场分发的插件用它们定位包内脚本，不依赖安装位置。
    /// {python}/{node}/{java} 由 RuntimeEnv 解析：config.ini [Script] 显式配置 → 自带 envs\（自动下载便携运行时）→ 系统 PATH。</summary>
    static string Fill(AppConfig cfg, string template, List<(string Name, string Desc)> args, JsonObject a)
    {
        var cmd = template.Replace("{project}", cfg.ProjectRoot)
                          .Replace("{plugins}", PluginDir(cfg))
                          .Replace("{skills}", SkillDir(cfg))
                          .Replace("{python}", RuntimeEnv.Py(cfg))
                          .Replace("{node}", RuntimeEnv.Node(cfg))
                          .Replace("{java}", RuntimeEnv.Java(cfg))
                          .Replace("{config}", cfg.ConfigPath);
        if (args.Count == 0)
            return cmd.Replace("{input}", EscCmdArg(a["input"]?.GetValue<string>() ?? ""));
        foreach (var (an, _) in args)
            cmd = cmd.Replace("{" + an + "}", EscCmdArg(a[an]?.GetValue<string>() ?? ""));
        return cmd;
    }

    /// <summary>命令行参数值转义：双引号→\"（CRT 解析还原），真实换行→字面 \n（两字符，防止截断
    /// cmd /c 命令导致多行内容丢失；接收方脚本如 dingtalk.js 会把 \n 还原为真实换行）</summary>
    static string EscCmdArg(string v) =>
        v.Replace("\"", "\\\"")
         .Replace("\r\n", "\\n")
         .Replace("\n", "\\n")
         .Replace("\r", "\\n");

    /// <summary>扁平 ini 解析（无节），去 ; 注释</summary>
    static Dictionary<string, string> ReadIni(string path)
    {
        var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('[')) continue;
            var ei = line.IndexOf('=');
            if (ei <= 0) continue;
            var key = line[..ei].Trim();
            var val = line[(ei + 1)..].Trim();
            var ci = val.IndexOf(" ;", StringComparison.Ordinal);   // 行内注释（值含空格+;）
            if (ci >= 0) val = val[..ci].TrimEnd();
            kv[key] = val;
        }
        return kv;
    }

    static string Get(Dictionary<string, string> kv, string key, string def) =>
        kv.TryGetValue(key, out var v) ? v : def;
}
