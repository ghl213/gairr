// PlanNegotiator.cs
// P2 多模型规划协商器：
//  - 标准模式：当前默认模型跑一次只读规划会话（plan-planner.md），解析 plan-tree 落盘；
//  - 商讨模式：[Planning] Models 列表的多个模型并行生成候选（并发取 [Planning] MaxParallel），
//    plan-judge 评估会话（无工具）输出 ranking + mergedPlan，择优落盘为 pendingConfirm 计划。
// 所有规划会话都是独立 AgentSession（promptFile=plan-planner.md / plan-judge.md），与用户主会话隔离。
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GAIRR.Core;

namespace GAIRR.AgentHost;

public static class PlanNegotiator
{
    /// <summary>只读工具白名单（候选生成会话：可检索项目但无法写入）。</summary>
    static readonly List<string> ReadOnlyTools = new()
        { "Map", "SmartSearch", "MapTrace", "MapSlice", "Grep", "Glob", "ListDir", "Read" };

    /// <summary>占位白名单：注册表无任何匹配 → 等价全部禁用（评估会话无工具）。</summary>
    static readonly List<string> NoTools = new() { "__none__" };

    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// 执行规划并返回待确认计划（status=pendingConfirm，已落盘 plan.json）；全部失败返回 null。
    /// negotiation=true 走多模型商讨；models 非空时以调用方指定模型为准（交互式对话框勾选），
    /// 空表则回退读取配置 [Planning] Models；log 回调输出进度（UI 展示用）。
    /// </summary>
    public static async Task<PlanDto?> NegotiateAsync(string projectRoot, string title, string goal,
        bool negotiation, Action<string>? log = null, CancellationToken ct = default,
        List<(string Provider, string Model)>? models = null)
    {
        log ??= _ => { };
        var plan = new PlanDto
        {
            Id = NewId(),
            Title = title,
            Goal = goal,
            Status = "pendingConfirm",
            PlanMode = negotiation ? "negotiation" : "single",
            CreatedAt = DateTime.Now.ToString("s"),
        };

        if (!negotiation)
        {
            // 标准模式：当前默认模型单会话规划
            log("[plan] 标准模式：当前模型规划中…");
            var (nodes, blockTitle, err) = await RunCandidateAsync(projectRoot, null, goal, log, ct);
            if (nodes == null) { log($"[plan] 规划失败：{err}"); return null; }
            if (string.IsNullOrWhiteSpace(plan.Title)) plan.Title = blockTitle.Length > 0 ? blockTitle : TruncGoal(goal);
            plan.Nodes = nodes;
            PlanStore.Save(projectRoot, plan);
            log($"[plan] 规划完成：{plan.Nodes.Count} 个节点");
            return plan;
        }

        // ── 商讨模式 ──
        var cfg = new AppConfig();
        if (models is null || models.Count == 0)
            models = ParseModels(cfg.Get("Planning", "Models", ""));
        if (models.Count == 0)
        {
            log("[plan] 未选择参与模型（[Planning] Models 也未配置），回退标准模式");
            return await NegotiateAsync(projectRoot, title, goal, false, log, ct);
        }
        var maxParallel = Math.Max(1, int.TryParse(cfg.Get("Planning", "MaxParallel", "2"), out var mp) ? mp : 2);

        // 1. 并行生成候选
        log($"[plan] 商讨模式：{models.Count} 个模型并行生成候选（并发 {maxParallel}）…");
        var sem = new SemaphoreSlim(maxParallel);
        var tasks = models.Select(m => Task.Run(async () =>
        {
            await sem.WaitAsync(ct);
            try { return await RunCandidateAsync(projectRoot, m, goal, log, ct); }
            finally { sem.Release(); }
        }, ct)).ToArray();
        var results = await Task.WhenAll(tasks);

        var candidates = new List<PlanCandidateDto>();
        for (var i = 0; i < models.Count; i++)
        {
            var (nodes, _, err) = results[i];
            var cand = new PlanCandidateDto { Id = $"c{i + 1}", Model = $"{models[i].Provider}:{models[i].Model}" };
            if (nodes == null) { cand.Invalid = true; log($"[plan] 候选 {cand.Id}（{cand.Model}）无效：{err}"); }
            else cand.Nodes = nodes;
            candidates.Add(cand);
        }
        plan.Candidates = candidates;
        var valid = candidates.Where(c => !c.Invalid && c.Nodes.Count > 0).ToList();
        if (valid.Count == 0) { log("[plan] 所有候选均无效，规划失败"); return null; }

        // 2. judge 评估（无工具）
        log("[plan] 评估 Agent 比较候选方案…");
        var verdict = await RunJudgeAsync(projectRoot, goal, valid, log, ct);
        if (verdict == null)
        {
            // 评估失败降级：直接取第一个有效候选，标记 assessmentFailed
            plan.AssessmentFailed = true;
            plan.SelectedCandidateId = valid[0].Id;
            plan.Nodes = CloneNodes(valid[0].Nodes);
            log("[plan] 评估失败，降级采用第一个有效候选");
        }
        else
        {
            ApplyVerdict(plan, valid, verdict);
        }

        PlanStore.Save(projectRoot, plan);
        log($"[plan] 商讨完成：入选 {plan.SelectedCandidateId}，{plan.Nodes.Count} 个节点");
        return plan;
    }

    // ────────────────────── 会话执行 ──────────────────────

    /// <summary>跑一次只读规划会话；返回 (节点列表或null, 块内title, 失败原因)。model 为 null 时用当前默认模型。</summary>
    static async Task<(List<PlanNodeDto>? Nodes, string Title, string Err)> RunCandidateAsync(
        string projectRoot, (string Provider, string Model)? model, string goal, Action<string> log, CancellationToken ct)
    {
        var sid = "plan-" + Guid.NewGuid().ToString("N")[..8];
        var session = new AgentSession(sid, projectRoot, null, "plan-planner.md");
        if (model != null) session.Loop.SwitchModelFull(model.Value.Provider, model.Value.Model);
        try
        {
            var text = $"L0 目标：{goal}\n\n请按你的系统提示词完成调研与拆解，最后输出 plan-tree 块。";
            var result = await session.StartAsync(new TaskRequest { Text = text, Tools = ReadOnlyTools }, ct);
            if (result.ChangedFiles.Count > 0)
                return (null, "", $"只读规划会话发生了写操作：{string.Join(",", result.ChangedFiles)}");
            var nodes = ParsePlanTree(result.Reply, out var title);
            if (nodes == null) return (null, "", "回复中未找到可解析的 plan-tree 块");
            return (nodes, title, "");
        }
        catch (Exception ex) { return (null, "", ex.Message); }
    }

    /// <summary>跑一次评估会话（无工具）；返回 verdict JsonObject 或 null（调用/解析失败）。</summary>
    static async Task<JsonObject?> RunJudgeAsync(string projectRoot, string goal,
        List<PlanCandidateDto> valid, Action<string> log, CancellationToken ct)
    {
        try
        {
            var sid = "judge-" + Guid.NewGuid().ToString("N")[..8];
            var session = new AgentSession(sid, projectRoot, null, "plan-judge.md");
            var sb = new StringBuilder();
            sb.AppendLine($"L0 目标：{goal}");
            sb.AppendLine($"项目根目录：{projectRoot}（无额外上下文，按候选本身质量评估）");
            sb.AppendLine("候选方案：");
            foreach (var c in valid)
                sb.AppendLine(JsonSerializer.Serialize(new { id = c.Id, model = c.Model, nodes = c.Nodes }, JsonOpts));
            var result = await session.StartAsync(new TaskRequest { Text = sb.ToString(), Tools = NoTools }, ct);
            return ParseVerdict(result.Reply);
        }
        catch (Exception ex) { log($"[plan] 评估会话异常：{ex.Message}"); return null; }
    }

    // ────────────────────── 结果处理 ──────────────────────

    /// <summary>把 judge 结论写回计划：填分/优劣、追加 merged 候选、确定入选方案与 nodes。</summary>
    static void ApplyVerdict(PlanDto plan, List<PlanCandidateDto> valid, JsonObject verdict)
    {
        // ranking → 填分
        double maxScore = 0; PlanCandidateDto? topByScore = null;
        if (verdict["ranking"] is JsonArray ranking)
        {
            foreach (var r in ranking)
            {
                if (r is not JsonObject ro) continue;
                var id = ro["id"]?.GetValue<string>() ?? "";
                var cand = valid.FirstOrDefault(c => c.Id == id);
                if (cand == null) continue;
                cand.Score = ro["score"]?.GetValue<double>() ?? 0;
                cand.Pros = ro["pros"]?.AsArray().Select(x => x?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToList() ?? new();
                cand.Cons = ro["cons"]?.AsArray().Select(x => x?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToList() ?? new();
                if (cand.Score > maxScore) { maxScore = cand.Score; topByScore = cand; }
            }
        }

        // mergedPlan → 追加融合候选
        PlanCandidateDto? merged = null;
        if (verdict["mergedPlan"] is JsonObject mp)
        {
            var nodes = ParseNodes(mp["nodes"]);
            if (nodes is { Count: > 0 })
            {
                merged = new PlanCandidateDto
                {
                    Id = "cm",
                    Model = "merged",
                    IsMerged = true,
                    Score = Math.Min(1.0, maxScore),
                    Pros = new() { "综合多候选优点" },
                    Nodes = nodes,
                };
                if (mp["title"]?.GetValue<string>() is string mt && mt.Length > 0 && string.IsNullOrWhiteSpace(plan.Title))
                    plan.Title = mt;
                plan.Candidates.Add(merged);
            }
        }

        // 入选：merged 优先 → recommendation → 得分最高 → 第一个有效候选
        var recId = verdict["recommendation"]?.GetValue<string>() ?? "";
        var rec = valid.FirstOrDefault(c => c.Id == recId);
        var chosen = merged ?? rec ?? topByScore ?? valid[0];
        if (merged == null && rec == null && topByScore == null) plan.AssessmentFailed = true;
        plan.SelectedCandidateId = chosen.Id;
        plan.Nodes = CloneNodes(chosen.Nodes);
    }

    // ────────────────────── 解析工具 ──────────────────────

    /// <summary>从模型回复提取 ```plan-tree（回退 ```json）块并解析节点列表；失败返回 null。</summary>
    public static List<PlanNodeDto>? ParsePlanTree(string content, out string title)
    {
        title = "";
        if (string.IsNullOrEmpty(content)) return null;
        var m = Regex.Match(content, "```plan-tree\\s*\\r?\\n([\\s\\S]*?)```", RegexOptions.IgnoreCase);
        if (!m.Success) m = Regex.Match(content, "```json\\s*\\r?\\n([\\s\\S]*?)```");
        if (!m.Success) return null;
        try
        {
            var obj = JsonSerializer.Deserialize<JsonObject>(m.Groups[1].Value.Trim());
            if (obj == null) return null;
            title = obj["title"]?.GetValue<string>() ?? "";
            var nodes = ParseNodes(obj["nodes"]);
            return nodes is { Count: > 0 } && UniqueIds(nodes) ? nodes : null;
        }
        catch { return null; }
    }

    /// <summary>解析 nodes 数组（宽松：空/格式错返回 null）。</summary>
    static List<PlanNodeDto>? ParseNodes(JsonNode? node)
    {
        try { return node?.Deserialize<List<PlanNodeDto>>(JsonOpts); }
        catch { return null; }
    }

    /// <summary>从模型回复提取 ```plan-judge-verdict 块并解析；失败返回 null。</summary>
    public static JsonObject? ParseVerdict(string content)
    {
        if (string.IsNullOrEmpty(content)) return null;
        var m = Regex.Match(content, "```plan-judge-verdict\\s*\\r?\\n([\\s\\S]*?)```", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        try { return JsonSerializer.Deserialize<JsonObject>(m.Groups[1].Value.Trim()); }
        catch { return null; }
    }

    static bool UniqueIds(List<PlanNodeDto> nodes) =>
        nodes.Select(n => n.Id).Distinct().Count() == nodes.Count && nodes.All(n => n.Id.Length > 0);

    /// <summary>深拷贝节点列表（入选候选复制到生效 nodes，便于后续二次编辑互不影响）。</summary>
    static List<PlanNodeDto> CloneNodes(List<PlanNodeDto> nodes) =>
        JsonSerializer.Deserialize<List<PlanNodeDto>>(JsonSerializer.Serialize(nodes), JsonOpts) ?? new();

    /// <summary>解析 [Planning] Models："provider:modelId,provider:modelId"。</summary>
    public static List<(string Provider, string Model)> ParseModels(string raw)
    {
        var list = new List<(string, string)>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var i = part.IndexOf(':');
            if (i > 0 && i < part.Length - 1) list.Add((part[..i].Trim(), part[(i + 1)..].Trim()));
        }
        return list;
    }

    static string NewId() => $"plan-{DateTime.Now:yyyyMMdd-HHmmss}-{Random.Shared.Next(1000, 9999)}";

    static string TruncGoal(string goal) => goal.Length <= 30 ? goal : goal[..30] + "…";
}
