using System.Text;

namespace GAIRR.Core;

/// <summary>
/// 市场工具注册：把市场能力（查目录/安装/卸载）注册为模型可调用工具，
/// 让 Agent 遇到能力缺口时自主"逛市场 → 安装 → 当轮调用"（插件安装即生效，热加载兜底）。
/// </summary>
public static class MarketTools
{
    /// <summary>注册 MarketList/MarketInstall/MarketUninstall 三个工具（主窗与 AgentSession 构造序列共用）</summary>
    public static void RegisterAll(ToolRegistry reg, AppConfig cfg)
    {
        reg.Register("MarketList", ToolRegistry.Fn(
            "MarketList",
            SystemCfg.Desc("MarketList",
                "查询已配置市场源的技能/插件目录册（名称+描述+版本）。当需要某项能力（查外部数据、消息推送、文档处理等）且现有工具/插件覆盖不了时，先查市场有无现成，再考虑自己写。"),
            new System.Text.Json.Nodes.JsonObject
            {
                ["q"] = ToolRegistry.Str("过滤关键词（可选，按名称/描述模糊匹配；空=列出全部）"),
            },
            new System.Text.Json.Nodes.JsonArray()),
            async (a, ct) => await ListAsync(cfg, a["q"]?.GetValue<string>() ?? "", ct));

        reg.Register("MarketInstall", ToolRegistry.Fn(
            "MarketInstall",
            SystemCfg.Desc("MarketInstall",
                "从市场安装指定技能或插件（下载→校验→解包落盘，插件装完即注册可当轮调用）。安装前应先调 MarketList 确认条目存在。"),
            new System.Text.Json.Nodes.JsonObject
            {
                ["name"] = ToolRegistry.Str("市场条目名称（MarketList 返回的 name）"),
            },
            new System.Text.Json.Nodes.JsonArray { "name" }),
            async (a, ct) => await InstallAsync(cfg, reg, a["name"]?.GetValue<string>() ?? "", ct));

        reg.Register("MarketUninstall", ToolRegistry.Fn(
            "MarketUninstall",
            SystemCfg.Desc("MarketUninstall",
                "卸载已安装的技能或插件（删除本地目录并摘除注册）。仅在用户明确要求卸载时使用。"),
            new System.Text.Json.Nodes.JsonObject
            {
                ["name"] = ToolRegistry.Str("要卸载的技能/插件名称"),
            },
            new System.Text.Json.Nodes.JsonArray { "name" }),
            (a, ct) => Task.FromResult(Uninstall(cfg, reg, a["name"]?.GetValue<string>() ?? "")));
    }

    /* ---------- 工具实现 ---------- */

    /// <summary>MarketList：拉取目录册并按关键词过滤，输出文本清单（含 kind/版本/描述）。
    /// 描述优先使用本地缓存的中文摘要；缓存缺失且为英文时后台批量翻译并落盘，下次直接命中。</summary>
    static async Task<string> ListAsync(AppConfig cfg, string q, CancellationToken ct)
    {
        var entries = await Market.FetchCatalogAsync(cfg, ct);
        if (entries.Count == 0)
            return "市场目录册为空（未配置市场源或拉取失败）。检查配置 Market.Sources。";

        // 翻译：优先命中本地缓存 .gairr/market_translations.json，未命中且为英文才请求大模型；失败静默回退英文原文
        var defs = entries.Select(e => new MarketEntryDef(e)).ToList();
        Dictionary<string, string> zhMap;
        try { zhMap = await MarketTranslator.TranslateAsync(cfg, defs); }
        catch { zhMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }

        var sb = new StringBuilder($"市场目录册共 {entries.Count} 项：\n");
        foreach (var e in entries)
        {
            if (q.Length > 0 &&
                !e.Name.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !e.Desc.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            var desc = zhMap.TryGetValue(e.Name, out var zh) && zh.Length > 0 ? zh : e.Desc;
            sb.Append("- [").Append(e.Kind).Append("] ").Append(e.Name)
              .Append(e.Version.Length > 0 ? $" v{e.Version}" : "")
              .Append("：").Append(desc).Append('\n');
        }
        var filtered = sb.ToString();
        return filtered.EndsWith("：\n") ? filtered + "（无匹配条目，换个关键词或不传 q 看全部）" : filtered;
    }

    /// <summary>MarketInstall：按名称在目录册中找条目并安装；找到同名多项时报歧义</summary>
    static async Task<string> InstallAsync(AppConfig cfg, ToolRegistry reg, string name, CancellationToken ct)
    {
        if (name.Length == 0) return "错误：缺少 name 参数";
        var entries = await Market.FetchCatalogAsync(cfg, ct);
        var matches = entries.Where(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) return $"错误：市场中没有名为 {name} 的条目（先调 MarketList 查看）";
        if (matches.Count > 1)
            return $"错误：{name} 在多个源中存在同名条目（{string.Join(" / ", matches.Select(m => m.Source))}），暂不支持选择，请手工处理";
        var r = await Market.InstallAsync(cfg, reg, matches[0], ct);
        // 插件安装成功后附带 Command 摘要，便于模型向用户说明该插件将执行什么命令（安全透明）
        if (r.Success && matches[0].Kind == "plugin")
        {
            var ini = Path.Combine(cfg.Get("Plugins", "Dir", "plugins"), name, "plugin.ini");
            var iniPath = Path.IsPathRooted(ini) ? ini : Path.Combine(AppContext.BaseDirectory, ini);
            if (File.Exists(iniPath))
            {
                var cmd = File.ReadAllLines(iniPath)
                    .FirstOrDefault(l => l.TrimStart().StartsWith("Command", StringComparison.OrdinalIgnoreCase));
                if (cmd != null) r = r with { Message = r.Message + "\n该插件的命令模板：" + cmd.Trim() };
            }
        }
        return r.Message;
    }

    /// <summary>MarketUninstall：按名称先查追溯记录定 kind，再调 Market.Uninstall</summary>
    static string Uninstall(AppConfig cfg, ToolRegistry reg, string name)
    {
        if (name.Length == 0) return "错误：缺少 name 参数";
        var meta = Market.Installed(cfg);
        var kind = meta.TryGetValue(name, out var v) ? v.Split('|')[0] : "";
        if (kind.Length == 0)
        {
            // 追溯记录缺失时按目录存在性兜底推断
            var pd = cfg.Get("Plugins", "Dir", "plugins");
            var pluginDir = Path.IsPathRooted(pd) ? pd : Path.Combine(AppContext.BaseDirectory, pd);
            kind = Directory.Exists(Path.Combine(pluginDir, name)) ? "plugin" : "skill";
        }
        return Market.Uninstall(cfg, reg, kind, name).Message;
    }
}
