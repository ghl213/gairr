using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GAIRR.AgentHost;

/// <summary>编排任务 DTO（plan.json 数据模型）：扁平节点列表+引用，UI 组装树。</summary>
public class PlanDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("goal")] public string Goal { get; set; } = "";
    /// <summary>pendingConfirm|approved|running|paused|done|failed</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = "pendingConfirm";
    [JsonPropertyName("createdAt")] public string CreatedAt { get; set; } = "";
    [JsonPropertyName("maxRetry")] public int MaxRetry { get; set; } = 2;
    /// <summary>规划模式：single=标准单模型；negotiation=多模型商讨。</summary>
    [JsonPropertyName("planMode")] public string PlanMode { get; set; } = "single";
    /// <summary>审查门禁（P3 用）：true=每个叶子须审查 Agent 通过。</summary>
    [JsonPropertyName("reviewGate")] public bool ReviewGate { get; set; } = true;
    /// <summary>候选方案（仅 negotiation 模式；含 isMerged 融合方案）。</summary>
    [JsonPropertyName("candidates")] public List<PlanCandidateDto> Candidates { get; set; } = new();
    /// <summary>入选候选 id（用户确认后指向 Candidates；single 模式为空）。</summary>
    [JsonPropertyName("selectedCandidateId")] public string? SelectedCandidateId { get; set; }
    /// <summary>true=评估 Agent 输出解析失败（降级：直接取最高分候选）。</summary>
    [JsonPropertyName("assessmentFailed")] public bool AssessmentFailed { get; set; }
    [JsonPropertyName("nodes")] public List<PlanNodeDto> Nodes { get; set; } = new();

    // ---- 编排会话（8.3，2026-08-31 三个决策点） ----
    /// <summary>编排文档相对路径（.gairr/plans/&lt;id&gt;/doc.md；决策 2 文档与树同源同轮产出）。</summary>
    [JsonPropertyName("sourceDoc")] public string? SourceDoc { get; set; }
    /// <summary>收集问答摘要（编排会话 Collecting 阶段逐条，决策 3 扩散勾选可追溯）。</summary>
    [JsonPropertyName("collectLog")] public List<CollectEntryDto> CollectLog { get; set; } = new();
    /// <summary>变更集扩散引用（决策 3：默认并入当前节点，扩散挂引用不复制任务树）。</summary>
    [JsonPropertyName("crossRefs")] public List<CrossRefDto> CrossRefs { get; set; } = new();
    /// <summary>来源架构节点路径（架构树 NodePath，如 "GAIRR/桌面客户端前端/会话管理"；决策 1 锚定）。</summary>
    [JsonPropertyName("archRef")] public string? ArchRef { get; set; }
    /// <summary>true=文档待同步：确认对话框改树后由模型按 diff 更新 doc.md（8.3 决策 2 修订，防双向漂移）。</summary>
    [JsonPropertyName("docOutdated")] public bool DocOutdated { get; set; }
}

/// <summary>收集问答摘要条目（collectLog）：问答对 + 时间。</summary>
public class CollectEntryDto
{
    [JsonPropertyName("q")] public string? Q { get; set; }
    [JsonPropertyName("a")] public string? A { get; set; }
    [JsonPropertyName("at")] public string? At { get; set; }
}

/// <summary>变更集扩散引用：源节点改动点 → 目标节点（引用最新结论，不复制任务树）。目标可为空=已确认需扩散但目标节点待定（确认框不填目标，后续编排补）。</summary>
public class CrossRefDto
{
    [JsonPropertyName("fromNodeId")] public string FromNodeId { get; set; } = "";
    [JsonPropertyName("toNodeId")] public string? ToNodeId { get; set; }
    [JsonPropertyName("changePoint")] public string? ChangePoint { get; set; }
    [JsonPropertyName("at")] public string? At { get; set; }
}
public class PlanCandidateDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    /// <summary>来源模型（provider:modelId；融合方案为 "merged"）。</summary>
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    /// <summary>judge 评分（0~1）；未参与评估为 0。</summary>
    [JsonPropertyName("score")] public double Score { get; set; }
    [JsonPropertyName("pros")] public List<string> Pros { get; set; } = new();
    [JsonPropertyName("cons")] public List<string> Cons { get; set; } = new();
    /// <summary>true=评估 Agent 综合多候选优点生成的融合方案。</summary>
    [JsonPropertyName("isMerged")] public bool IsMerged { get; set; }
    /// <summary>true=无效候选（JSON 解析失败或发生写操作），不参与评估。</summary>
    [JsonPropertyName("invalid")] public bool Invalid { get; set; }
    [JsonPropertyName("nodes")] public List<PlanNodeDto> Nodes { get; set; } = new();
}

/// <summary>编排节点：type=group（L1/L2 分组）| leaf（L3 最小会话任务单元）。</summary>
public class PlanNodeDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    /// <summary>group|leaf</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "leaf";
    /// <summary>子节点 id 列表（group 用）。</summary>
    [JsonPropertyName("children")] public List<string> Children { get; set; } = new();
    /// <summary>父节点 id（规划 Agent 填写；树组装以 children 为准，本字段仅辅助展示）。</summary>
    [JsonPropertyName("parentId")] public string? ParentId { get; set; }

    // ---- leaf 专属 ----
    /// <summary>叶子目标（会话 prompt 主体）。</summary>
    [JsonPropertyName("goal")] public string? Goal { get; set; }
    /// <summary>验收标准（审查 Agent 的判定依据；可含硬检查描述）。</summary>
    [JsonPropertyName("accept")] public string? Accept { get; set; }
    /// <summary>执行模式：auto=自主；flow:模板名=Flow 模式。</summary>
    [JsonPropertyName("mode")] public string? Mode { get; set; }
    /// <summary>pending|running|reviewing|passed|failed|skipped</summary>
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("attempts")] public int Attempts { get; set; }
    /// <summary>交接摘要（完成后写入，注入下一叶 prompt）。</summary>
    [JsonPropertyName("handoff")] public string? Handoff { get; set; }
    [JsonPropertyName("changedFiles")] public List<string> ChangedFiles { get; set; } = new();
    /// <summary>审查结论（审查 Agent 输出）。</summary>
    [JsonPropertyName("reviewSummary")] public string? ReviewSummary { get; set; }
    [JsonPropertyName("reviewIssues")] public List<string> ReviewIssues { get; set; } = new();
    [JsonPropertyName("sessionRunId")] public string? SessionRunId { get; set; }

    // ---- 编排会话（8.3 决策 3：疑似跨节点改动点，确认框挂标记、勾选目标节点才扩散） ----
    [JsonPropertyName("suspectCrossNode")] public bool SuspectCrossNode { get; set; }
}

/// <summary>plan.json 存取：单一事实源，每次状态变更原子落盘（临时文件+替换），崩溃重开可续跑。</summary>
public static class PlanStore
{
    private static readonly JsonSerializerOptions Opt = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };   // plan.json 中文直存

    /// <summary>plan.json 路径：{ProjectRoot}/.gairr/plan.json。</summary>
    public static string PlanPath(string projectRoot) => System.IO.Path.Combine(projectRoot, ".gairr", "plan.json");

    /// <summary>加载全部编排任务（文件不存在/损坏返回空列表）。</summary>
    public static List<PlanDto> LoadAll(string projectRoot)
    {
        try
        {
            var p = PlanPath(projectRoot);
            if (!File.Exists(p)) return new();
            var list = JsonSerializer.Deserialize<List<PlanDto>>(File.ReadAllText(p));
            return list ?? new();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Plan] 加载失败: {ex.Message}"); return new(); }
    }

    /// <summary>按 id 加载单个编排任务（不存在返回 null）。</summary>
    public static PlanDto? GetById(string projectRoot, string id) =>
        LoadAll(projectRoot).FirstOrDefault(p => p.Id == id);

    /// <summary>原子保存全部编排任务。</summary>
    public static void SaveAll(string projectRoot, List<PlanDto> plans)
    {
        var p = PlanPath(projectRoot);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        var tmp = p + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(plans, Opt));
        File.Move(tmp, p, overwrite: true);
    }

    /// <summary>保存单个任务（按 id 替换/追加）。</summary>
    public static void Save(string projectRoot, PlanDto plan)
    {
        var all = LoadAll(projectRoot);
        var i = all.FindIndex(x => x.Id == plan.Id);
        if (i >= 0) all[i] = plan; else all.Add(plan);
        SaveAll(projectRoot, all);
    }

    /// <summary>doc.md 路径：{ProjectRoot}/.gairr/plans/{planId}/doc.md（与 plan.json 同属 .gairr，原子落盘）。</summary>
    public static string DocPath(string projectRoot, string planId) =>
        System.IO.Path.Combine(projectRoot, ".gairr", "plans", planId, "doc.md");

    /// <summary>原子落盘编排文档（临时文件+替换），并回写 PlanDto.SourceDoc（相对路径）。</summary>
    public static void SaveDoc(string projectRoot, PlanDto plan, string markdown)
    {
        var docPath = DocPath(projectRoot, plan.Id);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(docPath)!);
        var tmp = docPath + ".tmp";
        File.WriteAllText(tmp, markdown, new System.Text.UTF8Encoding(false));
        File.Move(tmp, docPath, overwrite: true);
        plan.SourceDoc = Path.GetRelativePath(projectRoot, docPath);
        Save(projectRoot, plan);
    }

    /// <summary>取第一个可执行叶子（按声明顺序 DFS 展开 group）。</summary>
    public static PlanNodeDto? NextLeaf(PlanDto plan, Func<PlanNodeDto, bool> canRun)
    {
        foreach (var nodeId in LeafOrder(plan))
        {
            var n = plan.Nodes.FirstOrDefault(x => x.Id == nodeId);
            if (n != null && n.Type == "leaf" && canRun(n)) return n;
        }
        return null;
    }

    /// <summary>叶子声明顺序（DFS 展开 group；未知引用跳过）。</summary>
    public static List<string> LeafOrder(PlanDto plan)
    {
        var result = new List<string>();
        var byId = plan.Nodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
        void Walk(string id)
        {
            if (!byId.TryGetValue(id, out var n)) return;
            if (n.Type == "group") foreach (var c in n.Children) Walk(c);
            else result.Add(id);
        }
        foreach (var n in plan.Nodes.Where(n => n.Type == "group" && n.Children.Count > 0).Concat(plan.Nodes.Where(n => n.Type == "leaf")))
            if (!result.Contains(n.Id) && (n.Type == "group" ? n.Children.Count > 0 : true)) Walk(n.Id);
        return result;
    }

    /// <summary>范围内叶子声明顺序（保持全局 DFS 声明顺序的子序列）：
    /// rootId 为空 = 全树；指向 group = 该分组子树；指向 leaf = 仅自身；节点不存在返回空列表。</summary>
    public static List<string> LeafOrder(PlanDto plan, string? rootId)
    {
        if (string.IsNullOrEmpty(rootId)) return LeafOrder(plan);
        var all = LeafOrder(plan);
        var set = SubtreeLeafIds(plan, rootId);
        return set.Count == 0 ? new List<string>() : all.Where(set.Contains).ToList();
    }

    /// <summary>某节点子树内全部叶子 id 集合（group 递归展开 children；leaf 含自身；未知节点返回空集）。</summary>
    public static HashSet<string> SubtreeLeafIds(PlanDto plan, string rootId)
    {
        var res = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byId = plan.Nodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
        void Walk(string id)
        {
            if (!byId.TryGetValue(id, out var n)) return;
            if (n.Type == "group") foreach (var c in n.Children) Walk(c);
            else res.Add(id);
        }
        Walk(rootId);
        return res;
    }
}
