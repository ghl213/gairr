using System.Text;
using System.Text.Json.Nodes;
using GAIRR.Core;

namespace GAIRR.AgentHost;

/// <summary>角色自动匹配：发送前根据用户当前发言，调一次轻量非流式 LLM 从角色/模式仓库中选定最合适的 role|mode。
/// 只做"分类"：把全部角色的名称+说明作为候选清单喂给模型，要求只回 "role|mode" 一个标签；
/// 无可用 key / 超时 / 解析失败 / 命中不存在的角色 一律静默返回 null（调用方回落默认角色），绝不阻塞发送。</summary>
public static class RoleAutoMatch
{
    /// <summary>异步匹配：入参为当前厂商/模型（复用会话已配置的 key）；返回 (role, mode, 角色显示, 模式显示)；
    /// 匹配不可用或失败返回 null。全程 try/catch + 30s 取消兜底，任何异常不外抛。</summary>
    public static async Task<(string Role, string Mode, string RoleDisp, string ModeDisp)?> MatchAsync(
        AppConfig cfg, string provider, string model, string userText, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(userText)) return null;
            var roles = RoleModeStore.LoadAll();
            if (roles.Count == 0) return null;
            var (baseUrl, apiKey, cfgModel) = cfg.ProviderCfg(provider);
            if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(apiKey)) return null;
            var client = new LLMClient(baseUrl, apiKey,
                string.IsNullOrWhiteSpace(model) ? cfgModel : model);   // 不挂思考规则：分类只要结果不要思考链

            var messages = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] =
                    "你是角色路由助手。根据用户发言，从候选清单中选出最合适的一项，只回复该项的 role|mode 标识（小写，不含任何其它文字）。" },
                new JsonObject { ["role"] = "user", ["content"] =
                    "候选清单（每行格式：role|mode 角色·模式：说明）：\n" + CandidateList(roles) +
                    "\n用户发言：\n" + LLMClient.Trunc(userText, 2000) },
            };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            var resp = await client.ChatAsync(messages, new JsonArray(), linked.Token);
            return ParsePick(resp.Content ?? "", roles);
        }
        catch { return null; }   // 匹配失败静默：调用方回落默认角色，不阻塞发送
    }

    /// <summary>拼候选清单：每项一行 "role|mode 角色·模式：模式说明"（说明显示名取自仓库 JSON，用户写清说明=匹配准）。</summary>
    static string CandidateList(List<RoleProfile> roles)
    {
        var sb = new StringBuilder();
        foreach (var r in roles)
            foreach (var m in r.Modes)
                sb.AppendLine($"{r.Name}|{m.Name} {r.DisplayName}·{m.DisplayName}：{m.Description}");
        return sb.ToString();
    }

    /// <summary>解析模型回复：提取首个 role|mode 并回仓库校验存在（防幻觉标签）；非法/不存在返回 null。</summary>
    static (string, string, string, string)? ParsePick(string content, List<RoleProfile> roles)
    {
        var line = content.Trim().Trim('`').Split('\n')[0].Trim();   // 防回多行：只取首行
        var idx = line.IndexOf('|');
        if (idx <= 0) return null;
        var role = line[..idx].Trim();
        var mode = line[(idx + 1)..].Trim();
        if (role.Length == 0 || mode.Length == 0) return null;
        var rp = roles.FirstOrDefault(r => string.Equals(r.Name, role, StringComparison.OrdinalIgnoreCase));
        var mp = rp?.Modes.FirstOrDefault(m => string.Equals(m.Name, mode, StringComparison.OrdinalIgnoreCase));
        if (rp == null || mp == null) return null;
        return (rp.Name, mp.Name, rp.DisplayName, mp.DisplayName);
    }
}
