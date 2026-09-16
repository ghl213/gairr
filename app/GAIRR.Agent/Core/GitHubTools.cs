using System.Text.Json.Nodes;

namespace GAIRR.Core;

/// <summary>
/// GitHub 技能源工具注册：让模型能搜索他人公开仓库并安装其中的技能（SKILL.md 开放规范）。
/// 与目录册市场（MarketTools）互补：MarketList 查不到想要的技能时，用 GhSearch 搜 GitHub 仓库。
/// </summary>
public static class GitHubTools
{
    /// <summary>注册 GhSearch/GhInstallSkill/GhUninstall（与 MarketTools.RegisterAll 同处接线）</summary>
    public static void RegisterAll(ToolRegistry reg, AppConfig cfg)
    {
        reg.Register("GhSearch", ToolRegistry.Fn(
            "GhSearch",
            SystemCfg.Desc("GhSearch",
                "在 GitHub 搜索他人的公开仓库（技能库、文档、工具集等，按 star 排序）。当需要某项能力或参考资料、且 MarketList 的目录册市场里没有现成条目时使用；搜到后用 GhInstallSkill 安装。"),
            new JsonObject
            {
                ["q"] = ToolRegistry.Str("搜索关键词（英文效果最好，如 pdf tools、claude skills、excel）"),
            },
            new JsonArray { "q" }),
            (a, ct) => GitHubHub.SearchAsync(a["q"]?.GetValue<string>() ?? "", ct));

        reg.Register("GhInstallSkill", ToolRegistry.Fn(
            "GhInstallSkill",
            SystemCfg.Desc("GhInstallSkill",
                "从他人 GitHub 仓库下载安装技能（仓库内含 SKILL.md 的开放规范技能；多技能仓需用 skill 参数指定子目录）。安装后技能热加载约 2 秒生效。注意：GAIRR 插件格式（plugin.ini）是私有格式，GitHub 上搜不到，需要插件请按自开发规则自行包装。"),
            new JsonObject
            {
                ["repo"] = ToolRegistry.Str("仓库，格式 owner/repo（如 anthropics/skills，也接受完整 GitHub URL）"),
                ["skill"] = ToolRegistry.Str("多技能仓库中的技能子目录名（单技能仓可省略；不确定时先省略，工具会返回候选清单）"),
            },
            new JsonArray { "repo" }),
            async (a, ct) =>
            {
                var r = await GitHubHub.InstallSkillAsync(cfg,
                    a["repo"]?.GetValue<string>() ?? "", a["skill"]?.GetValue<string>() ?? "", ct);
                // 多技能仓候选清单转文本返回，让模型选一个后再带 skill 参数重调
                if (r.Candidates.Count > 0)
                    return r.Message + "：\n- " + string.Join("\n- ", r.Candidates);
                return r.Message;
            });

        reg.Register("GhUninstall", ToolRegistry.Fn(
            "GhUninstall",
            SystemCfg.Desc("GhUninstall",
                "卸载从 GitHub 安装的技能（删除 skills/<名称> 目录，热加载自动摘除清单）。仅在用户明确要求卸载时使用。"),
            new JsonObject
            {
                ["name"] = ToolRegistry.Str("技能名称（skills/ 下的目录名）"),
            },
            new JsonArray { "name" }),
            (a, ct) =>
            {
                var name = a["name"]?.GetValue<string>() ?? "";
                var raw = cfg.Get("Skills", "Dir", "skills");
                var root = System.IO.Path.IsPathRooted(raw) ? raw : System.IO.Path.Combine(AppContext.BaseDirectory, raw);
                var dest = System.IO.Path.Combine(root, name);
                if (!System.IO.Directory.Exists(dest)) return Task.FromResult($"错误：未安装技能 {name}");
                System.IO.Directory.Delete(dest, true);
                return Task.FromResult($"已卸载技能 {name}（技能热加载自动摘除清单）");
            });
    }
}
