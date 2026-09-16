// 编排会话引擎侧助手（8.3 编排会话）：状态机阶段常量、项目级上下文的初始 prompt 注入、
// 生成/重新生成任务措辞、生成产物（编排文档 + plan-tree 骨架）的解析与同目录原子落盘。
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace GAIRR.AgentHost;

/// <summary>编排会话共享逻辑（8.3 决策 1 两阶段状态机：Collecting → Generating → Done）。</summary>
public static class Orchestrator
{
    /// <summary>问答收集阶段：主持人按调研清单逐项确认。</summary>
    public const string PhaseCollecting = "collecting";
    /// <summary>生成阶段：目标已锁定、禁止问答注入；模型产出 doc.md + 任务树骨架。</summary>
    public const string PhaseGenerating = "generating";
    /// <summary>已生成待确认：叶子细节在确认对话框改树阶段补齐（8.3 决策 2）。</summary>
    public const string PhaseDone = "done";

    /// <summary>计划收集期状态（尚未生成树；生成完成后转 pendingConfirm）。</summary>
    public const string PlanStatusCollecting = "collecting";

    /// <summary>项目级上下文（8.3 决策 1：作为初始 prompt 注入每个编排会话，避免逐节点重复问答）：
    /// 顶层目录结构 + 技术栈标记 + 最近改动摘要。</summary>
    public static string BuildProjectContext(string projectRoot)
    {
        var sb = new StringBuilder();
        sb.AppendLine("【项目上下文】");
        try
        {
            var dirs = Directory.GetDirectories(projectRoot)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n) && !n.StartsWith("."))
                .OrderBy(n => n).Take(25);
            var files = Directory.GetFiles(projectRoot)
                .Select(Path.GetFileName).OrderBy(n => n).Take(15);
            sb.AppendLine("顶层结构: " + string.Join(", ", dirs.Concat(files)!));
            var stacks = new List<string>();
            foreach (var f in Directory.GetFiles(projectRoot, "*.sln")) stacks.Add(".NET 解决方案(" + Path.GetFileName(f) + ")");
            foreach (var f in Directory.GetFiles(projectRoot, "*.csproj")) stacks.Add(".NET/C#(" + Path.GetFileName(f) + ")");
            if (File.Exists(Path.Combine(projectRoot, "package.json"))) stacks.Add("Node/前端(package.json)");
            if (stacks.Count > 0) sb.AppendLine("技术栈标记: " + string.Join("；", stacks));
        }
        catch { }
        try
        {
            // 最近改动摘要：变更日志最后 5 条（存在才注入，避免无谓噪音）
            var journal = Path.Combine(projectRoot, ".gairr", "changes.jsonl");
            if (File.Exists(journal))
            {
                var lines = File.ReadAllLines(journal)
                    .Where(l => l.Trim().Length > 0).Reverse().Take(5).Reverse().ToList();
                if (lines.Count > 0)
                {
                    sb.AppendLine("最近改动(变更日志最后 5 条):");
                    foreach (var l in lines) sb.AppendLine("  " + (l.Length > 180 ? l[..180] + "…" : l));
                }
            }
        }
        catch { }
        return sb.ToString();
    }

    /// <summary>编排会话首条任务文本（上下文注入 + L0 目标 + 架构节点 + 请主持人开始提问）。</summary>
    public static string BuildKickoff(string title, string goal, string archRef, string projectContext)
        => projectContext
         + "\n【编排调研会话已启动】\n"
         + $"编排任务: {title}\n"
         + $"L0 目标: {goal}\n"
         + $"绑定架构节点: {archRef}\n"
         + "请以调研主持人身份，按系统提示词中的调研清单，从第一项「目标与范围」开始逐项提问。";

    /// <summary>生成任务措辞（收集收敛后）：锁定目标，产出编排文档 + plan-tree 骨架。</summary>
    public static string BuildGeneratePrompt(string goal)
        => $"调研已收敛，目标锁定（L0: {goal}）。请进入产出阶段：严格按系统提示词「产出格式」，"
         + "在一条回复中输出编排文档（固定四节）与 plan-tree 任务树骨架，不要再提问。";

    /// <summary>重新生成任务措辞（Done 阶段改需求）：模型按变更点修订既有 doc.md 后重新产出。
    /// 兜底：doc.md 若为无效空壳（上轮未输出正文），提示模型改从 plan.json 任务树 goal/accept 重建文档主体再修订。</summary>
    public static string BuildRegeneratePrompt(string goal, string planId, string changeRequest)
        => $"需求变更，请重新生成。\nL0 目标: {goal}\n变更点: {changeRequest}\n"
         + $"请先读取现有编排文档 .gairr/plans/{planId}/doc.md，只修订受变更点影响的部分，"
         + "再严格按「产出格式」重新输出完整编排文档与 plan-tree 任务树骨架。\n"
         + $"注意：若 doc.md 内容异常（过短/仅一句过渡语/缺少「目标与范围」「调研结论」「验收标准」「关键约束」固定四节），"
         + $"说明上一轮未输出编排文档正文，请改读 .gairr/plans/{planId}/plan.json 的任务树（各叶子 goal/accept），"
         + "先重建完整编排文档主体，再按上述变更点修订。";

    /// <summary>解析生成阶段回复：块前为编排文档正文（markdown），块内为任务树骨架节点。</summary>
    public static bool TryParseGeneration(string reply, out string docMd, out List<PlanNodeDto> nodes, out string error)
    {
        docMd = ""; nodes = new(); error = "";
        if (string.IsNullOrWhiteSpace(reply)) { error = "生成回复为空"; return false; }
        // 首个 plan-tree/json 代码块：块前为文档正文，块内为树骨架（契约见 plan-orchestration.md）
        var m = Regex.Match(reply, "```(?:plan-tree|json)\\s*\\r?\\n", RegexOptions.IgnoreCase);
        if (!m.Success) { error = "未找到 plan-tree 代码块"; return false; }
        var parsed = PlanNegotiator.ParsePlanTree(reply, out _);
        if (parsed == null || parsed.Count == 0) { error = "plan-tree 解析失败（需含 title/nodes 且节点带唯一 id）"; return false; }
        docMd = reply[..m.Index].Trim();
        if (docMd.Length == 0) { error = "代码块前缺少编排文档正文"; return false; }
        nodes = parsed;
        return true;
    }

    /// <summary>落盘生成产物：doc.md 与 plan.json 同目录原子写入（8.3 决策 3），计划转 pendingConfirm。</summary>
    public static void SaveGeneration(string projectRoot, PlanDto plan, string docMd, List<PlanNodeDto> nodes)
    {
        PlanStore.SaveDoc(projectRoot, plan, docMd);
        plan.SourceDoc = "doc.md";
        plan.Nodes = nodes;
        plan.Status = "pendingConfirm";
        plan.DocOutdated = false;
        PlanStore.Save(projectRoot, plan);
    }
}
