using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace GAIRR.AgentHost;

/// <summary>
/// 编排会话（阶段 3，文档 3.1-3.3）：对话式收集 L0 目标 → 产 doc.md + 任务树骨架 → 计划确认 → 执行。
/// 纯 AgentHost 逻辑，不依赖 WPF；prompt 文件 .gairr/prompts/plan-orchestration.md 热加载（8.3 决策 3）。
/// </summary>
public static class OrchestrationSession
{
    /// <summary>编排会话使用的 system prompt 文件名（AgentLoop.SwitchPrompt 切换）。</summary>
    public const string PromptFileName = "plan-orchestration.md";

    static readonly JsonSerializerOptions JsonOpt = new() { WriteIndented = true };

    /// <summary>编排 prompt 路径（工具级、与项目无关：程序目录 prompts/，与 AgentLoop.PromptPath 一致）。</summary>
    static string PromptPath(string projectRoot) =>
        Path.Combine(AppContext.BaseDirectory, "prompts", PromptFileName);

    /// <summary>默认 system prompt（文件缺失时的兜底，与 plan-orchestration.md 职责一致）。</summary>
    static string DefaultSystemPrompt() =>
        "你是软件需求调研主持人，以技术人员视角围绕 L0 目标逐项收集信息（目标/现状/验收/约束/拆分粒度），" +
        "不代用户假设答案；信息齐备后由用户发\"生成任务树\"指令，你一次性输出编排文档 doc.md + plan-tree JSON 任务树骨架。";

    /// <summary>读取编排会话 system prompt（热加载，便于免重编译改文案）。</summary>
    public static string LoadSystemPrompt(string projectRoot)
    {
        try
        {
            var p = PromptPath(projectRoot);
            if (File.Exists(p)) return File.ReadAllText(p, System.Text.Encoding.UTF8);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[OrchSession] prompt 读取失败: {ex.Message}"); }
        return DefaultSystemPrompt();
    }

    /// <summary>
    /// 构造编排会话首条 user 消息：注入项目上下文（目录结构/既有计划）后给出 L0 目标与主持人开场，
    /// 引导收集式问答（Collecting 阶段）。
    /// </summary>
    public static string InitialPrompt(string projectRoot, string title, string goal, string archRef)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("【编排会话已创建】\n");
        sb.Append("标题：").AppendLine(string.IsNullOrWhiteSpace(title) ? "（未命名）" : title);
        if (!string.IsNullOrWhiteSpace(archRef)) sb.Append("来源架构节点：").AppendLine(archRef);
        sb.AppendLine().Append("L0 目标：").AppendLine(string.IsNullOrWhiteSpace(goal) ? "（待澄清）" : goal);
        sb.AppendLine();

        // 项目上下文注入：目录结构 + 既有编排计划
        sb.AppendLine("## 项目上下文（只读参考）");
        try
        {
            var dirs = new DirectoryInfo(projectRoot).EnumerateDirectories().Take(12)
                .Select(d => d.Name).ToList();
            if (dirs.Count > 0) sb.AppendLine("顶层目录：" + string.Join("、", dirs));
            var plans = PlanStore.LoadAll(projectRoot);
            if (plans.Count > 0)
                sb.AppendLine("既有编排计划：" + string.Join("、", plans.Take(6).Select(p => p.Title)));
            else
                sb.AppendLine("既有编排计划：（无）");
        }
        catch (Exception ex) { sb.AppendLine("（上下文读取失败：" + ex.Message + "）"); }

        sb.AppendLine();
        sb.AppendLine("## 工作方式");
        sb.AppendLine("1. 按主持人流程逐项澄清（目标与范围/现状与依赖/验收标准/关键约束/拆分粒度），每次只问 1-3 个问题；");
        sb.AppendLine("2. 当我确认信息完备后（回复\"可以\"\"ok\"\"确认\"等），你必须立即输出任务树，不要再重复需求文档；");
        sb.AppendLine("3. 任务树必须以 ```plan-tree 代码块输出，格式如下：");
        sb.AppendLine("```plan-tree");
        sb.AppendLine("{ \"nodes\": [");
        sb.AppendLine("  { \"id\": \"t1\", \"title\": \"任务标题\", \"type\": \"leaf\", \"goal\": \"具体目标\", \"accept\": \"验收标准\" },");
        sb.AppendLine("  { \"id\": \"t2\", \"title\": \"父任务\", \"type\": \"group\", \"children\": [\"t2-1\",\"t2-2\"] }");
        sb.AppendLine("] }");
        sb.AppendLine("```");
        sb.AppendLine("4. type 取值：leaf（叶子任务）/ group（分组节点）；每个节点必须有 id、title、type。");
        return sb.ToString();
    }

    /// <summary>收敛指令检测：用户在收集阶段发出以下任一指令即进入 Generating（产树）。</summary>
    public static bool IsGenerateCommand(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim();
        // 精确匹配简短确认词（避免误触发）
        string[] exactMatches = { "可以", "好", "ok", "OK", "Ok", "确认", "没问题", "通过", "行", "是的", "对" };
        if (exactMatches.Any(e => string.Equals(trimmed, e, StringComparison.OrdinalIgnoreCase))) return true;
        // 包含匹配长关键词
        string[] markers =
        {
            "生成任务树", "生成任务", "生成计划", "确认生成", "开始生成", "产出方案",
            "生成树", "生成方案", "开始规划", "开始拆解", "可以生成", "请生成",
        };
        return markers.Any(m => trimmed.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>模型回复收敛信号检测：Collecting 阶段模型回复中包含以下信号时自动切换到 Generating。
    /// 用于捕获模型说“确认完毕/进入产出”等但未精确输出“生成任务树”的场景。</summary>
    public static bool IsConvergenceSignal(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return false;
        string[] signals =
        {
            "进入产出阶段", "进入产出", "收敛条件", "确认完毕",
            "信息已完备", "信息完备", "准备生成", "即将生成",
            "开始产出", "产出阶段", "拆分粒度与先后顺序通过",
        };
        return signals.Any(s => reply.Contains(s, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>plan-tree 代码块解析专用 DTO（宽松接受，缺省字段后补）。</summary>
    class PlanTreeIn
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("nodes")] public List<PlanNodeIn>? Nodes { get; set; }
    }
    class PlanNodeIn
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("children")] public List<string>? Children { get; set; }
        [JsonPropertyName("goal")] public string? Goal { get; set; }
        [JsonPropertyName("accept")] public string? Accept { get; set; }
        [JsonPropertyName("mode")] public string? Mode { get; set; }
        [JsonPropertyName("suspectCrossNode")] public bool SuspectCrossNode { get; set; }
    }

    /// <summary>
    /// 从模型最终回复解析任务树骨架（Generating 阶段收口）：
    /// 提取 ```plan-tree（或 ```json）代码块 → 扁平节点列表落盘 plan.Nodes；
    /// 同时把代码块之前的 Markdown 正文作为 doc.md 保存（8.3 决策 2：文档与树同轮产出）。
    /// 返回 null=成功；非 null=错误消息（调用方回退 Collecting 并提示重发）。
    /// </summary>
    public static string? ParseGeneration(string projectRoot, PlanDto plan, string reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
            return "模型回复为空，无法解析任务树。";

        // 1) 提取代码块（优先 ```plan-tree，其次 ```json），定位正文截断点
        var match = Regex.Match(reply, "```\\s*plan-tree\\s*([\\s\\S]*?)```", RegexOptions.IgnoreCase);
        int jsonStart = match.Success ? match.Index : Regex.Match(reply, "```\\s*json\\s*", RegexOptions.IgnoreCase)?.Index ?? -1;
        var err = ExtractPlanTree(reply, out var title, out var nodes);
        if (err != null) return err;

        // 2) 落盘：回填 plan + 保存
        plan.Title = title ?? plan.Title;
        plan.Nodes = nodes;
        if (string.IsNullOrWhiteSpace(plan.SourceDoc)) plan.Status = "pendingConfirm";
        var docText = reply;
        // doc.md = plan-tree 代码块之前的 Markdown 正文（去掉代码块部分）
        if (jsonStart > 0) docText = reply.Substring(0, jsonStart).Trim();
        if (string.IsNullOrWhiteSpace(docText)) docText = "# " + plan.Title + "\n\n目标：" + plan.Goal;
        // 兜底（决策 2 修订）：模型漏输出编排文档正文（只有过渡语/过短/缺固定四节）时，
        // 用任务树 goal/accept 自动拼最小 doc.md 骨架，保住可读设计，供预览与重新生成引用
        if (!IsPlausibleOrchDoc(docText)) docText = BuildDocSkeleton(plan);
        PlanStore.SaveDoc(projectRoot, plan, docText);
        PlanStore.Save(projectRoot, plan);
        return null;
    }

    /// <summary>编排文档特征标题（与 plan-orchestration.md 产出格式固定四节一致）。</summary>
    static readonly string[] OrchDocSections = { "## 目标与范围", "## 调研结论", "## 验收标准", "## 关键约束" };

    /// <summary>粗判文本是否为完整编排文档：命中固定四节标题 ≥2 且长度足够视为有效（一句过渡语/空壳不算）。</summary>
    static bool IsPlausibleOrchDoc(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 80) return false;
        int hit = 0;
        foreach (var s in OrchDocSections)
            if (text.Contains(s, StringComparison.Ordinal)) hit++;
        return hit >= 2;
    }

    /// <summary>用任务树 goal/accept 拼最小 doc.md 骨架（模型漏输出编排文档正文时兜底落盘，保住可读设计）。</summary>
    public static string BuildDocSkeleton(PlanDto plan)
    {
        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(string.IsNullOrWhiteSpace(plan.Title) ? "编排任务" : plan.Title);
        sb.AppendLine();
        sb.AppendLine("## 目标与范围");
        sb.AppendLine(string.IsNullOrWhiteSpace(plan.Goal) ? "（L0 目标未回填）" : plan.Goal);
        sb.AppendLine();
        sb.AppendLine("## 调研结论（分模块改动点）");
        var groups = plan.Nodes.Where(n => n.Type == "group").ToList();
        var leafIds = new HashSet<string>(groups.SelectMany(g => g.Children), StringComparer.OrdinalIgnoreCase);
        var bareLeaves = plan.Nodes.Where(n => n.Type == "leaf" && !leafIds.Contains(n.Id)).ToList();
        if (groups.Count == 0 && bareLeaves.Count == 0) sb.AppendLine("- （任务树为空）");
        foreach (var g in groups)
        {
            sb.Append("- ").AppendLine(g.Title);
            var kids = g.Children.Select(id => plan.Nodes.FirstOrDefault(n => n.Id == id)).Where(n => n != null).ToList();
            foreach (var k in kids) sb.Append("  - ").AppendLine(k!.Title);
        }
        foreach (var l in bareLeaves) sb.Append("- ").AppendLine(l.Title);
        sb.AppendLine();
        sb.AppendLine("## 验收标准（逐条）");
        var leaves = PlanStore.LeafOrder(plan).Select(id => plan.Nodes.FirstOrDefault(n => n.Id == id)).Where(n => n != null).ToList();
        if (leaves.Count == 0) sb.AppendLine("- （任务树无叶子）");
        foreach (var l in leaves)
        {
            sb.Append("- ").Append(l.Title);
            if (!string.IsNullOrWhiteSpace(l.Goal)) sb.Append("：").Append(l.Goal);
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(l.Accept)) { sb.Append("  - 验收：").AppendLine(l.Accept); }
        }
        sb.AppendLine();
        sb.AppendLine("## 关键约束");
        sb.AppendLine("- （执行约束以各叶子 goal/accept 为准）");
        sb.AppendLine();
        sb.AppendLine("> ⚠ 本 doc.md 由系统兜底生成（模型回复未输出完整编排文档正文），设计细节以 plan.json 任务树 goal/accept 为准，重新生成时基于本骨架修订。");
        return sb.ToString();
    }

    /// <summary>
    /// 从文本（模型回复，或宿主已落位 doc.md）提取 plan-tree 代码块并解析为规范任务树：
    /// 供 ParseGeneration（回复收口）与 UI 认领宿主自行落位的产物（doc.md 重建）共用；
    /// 只解析不落盘。返回 null=成功（title/nodes 有值）；非 null=错误消息。
    /// </summary>
    public static string? ExtractPlanTree(string text, out string? title, out List<PlanNodeDto> nodes)
    {
        title = null;
        nodes = new();
        if (string.IsNullOrWhiteSpace(text))
            return "文本为空，无法解析任务树。";

        // 1) 提取代码块：优先 ```plan-tree，其次 ```json
        var match = Regex.Match(text, "```\\s*plan-tree\\s*([\\s\\S]*?)```", RegexOptions.IgnoreCase);
        string? json = match.Success ? match.Groups[1].Value.Trim() : null;
        if (json == null)
        {
            var m2 = Regex.Match(text, "```\\s*json\\s*([\\s\\S]*?)```", RegexOptions.IgnoreCase);
            json = m2.Success ? m2.Groups[1].Value.Trim() : null;
        }
        if (json == null)
            return "文本中未找到 plan-tree / json 代码块，无法解析任务树。";

        // 2) 解析 JSON → 扁平节点列表
        PlanTreeIn tree;
        try { tree = JsonSerializer.Deserialize<PlanTreeIn>(json, JsonOpt) ?? throw new JsonException("空对象"); }
        catch (Exception ex) { return "任务树 JSON 解析失败：" + ex.Message; }
        if (tree.Nodes == null || tree.Nodes.Count == 0)
            return "任务树节点列表为空（nodes 缺省或空数组）。";

        // 3) 转换并补齐缺省字段
        int i = 0;
        nodes = tree.Nodes.Select(n =>
        {
            i++;
            var type = string.IsNullOrWhiteSpace(n.Type)
                ? ((n.Children != null && n.Children.Count > 0) ? "group" : "leaf")
                : n.Type;
            bool isGroup = type == "group";
            return new PlanNodeDto
            {
                Id = string.IsNullOrWhiteSpace(n.Id) ? "n" + i : n.Id,
                Title = n.Title ?? ("任务 " + i),
                Type = type,
                Children = isGroup ? (n.Children ?? new()) : new(),
                Goal = isGroup ? null : (n.Goal ?? ""),
                Accept = isGroup ? null : (n.Accept ?? ""),
                Mode = isGroup ? null : (n.Mode ?? "auto"),
                Status = isGroup ? null : "pending",
                Attempts = 0,
                ChangedFiles = new(),
                ReviewIssues = new(),
                SuspectCrossNode = n.SuspectCrossNode,
            };
        }).ToList();

        // 4) 校验：每个节点 id 唯一
        var dupes = nodes.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dupes.Count > 0) { title = tree.Title; return "节点 id 重复：" + string.Join("、", dupes); }
        // 叶子必须有 goal
        var noGoal = nodes.Where(n => n.Type == "leaf" && string.IsNullOrWhiteSpace(n.Goal)).Select(n => n.Title).ToList();
        if (noGoal.Count > 0) { title = tree.Title; return "以下叶子缺少 goal 字段：" + string.Join("、", noGoal); }

        title = tree.Title;
        return null;
    }

    /// <summary>问题卡片单选项（question-card 协议）：rec=true 为模型推荐项（最多一个）。</summary>
    public sealed class OrchQuestionOptionDto
    {
        [JsonPropertyName("text")] public string Text { get; set; } = "";
        [JsonPropertyName("rec")] public bool Rec { get; set; }
    }

    /// <summary>问题卡片（question-card 协议）：收集阶段模型每次提问以该卡片输出，用户点选作答不输入；
    /// multi=true 可多选（打勾），否则单选；推荐项由 Rec 标记。</summary>
    public sealed class OrchQuestionDto
    {
        [JsonPropertyName("question")] public string Question { get; set; } = "";
        [JsonPropertyName("multi")] public bool Multi { get; set; }
        [JsonPropertyName("options")] public List<OrchQuestionOptionDto> Options { get; set; } = new();
    }

    /// <summary>question-card 代码块正则（跨行非贪婪）。</summary>
    static readonly Regex QCardBlock = new(
        "```\\s*question-card\\s*([\\s\\S]*?)```", RegexOptions.IgnoreCase);

    /// <summary>
    /// 从模型回复提取全部问题卡片（```question-card JSON）：坏块跳过，返回好块列表；
    /// 无任何可解析卡片时返回空表（调用方按普通文本处理）。
    /// </summary>
    public static List<OrchQuestionDto> ParseQuestionCards(string reply)
    {
        var cards = new List<OrchQuestionDto>();
        if (string.IsNullOrWhiteSpace(reply)) return cards;
        try
        {
            foreach (Match m in QCardBlock.Matches(reply))
            {
                try
                {
                    var c = JsonSerializer.Deserialize<OrchQuestionDto>(m.Groups[1].Value.Trim(), JsonOpt);
                    if (c == null || string.IsNullOrWhiteSpace(c.Question) || c.Options.Count == 0) continue;
                    c.Options = c.Options.Where(o => !string.IsNullOrWhiteSpace(o.Text)).ToList();
                    if (c.Options.Count > 0) cards.Add(c);
                }
                catch (Exception) { /* 坏块跳过 */ }
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Orch] 卡片解析失败: {ex.Message}"); }
        return cards;
    }

    /// <summary>从回复中剥离全部 question-card 代码块（卡片已 UI 化，正文不再保留 JSON 源码噪音）。</summary>
    public static string StripQuestionCards(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return reply;
        var s = QCardBlock.Replace(reply, "");
        // 清洗剥除后残留的空行/尾部空白
        s = Regex.Replace(s, "(?:\r?\n){3,}", "\n\n").Trim();
        return s.Length == 0 ? reply.Trim() : s;
    }

    /// <summary>编排会话标识（会话列表/详情显示用）；planMode=multi 时收集阶段语义为"等一次生成"而非逐轮对话。</summary>
    public static string PhaseText(string? phase, string planMode = "interactive") => (phase, planMode) switch
    {
        ("collecting", "multi") => "方案待生成",
        ("generating", "multi") => "多模型决策中",
        ("done", "multi") => "方案已确认",
        ("collecting", _) => "对话收集",
        ("generating", _) => "生成任务树",
        ("done", _) => "方案已确认",
        _ => "编排会话",
    };
}