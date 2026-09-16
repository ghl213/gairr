// ArchTree.cs —— 架构树（功能架构）模型与加载
// 层级约定：L0=项目名称（根），L1=子系统（按系统模块及前后端），
//           L2..Ln=功能分类（可无限扩展下级），叶子=最小独立功能（能拆就拆，最小可验证）。
// 数据来源：{项目根}/.gairr/architecture.json；每个节点带 status：done/partial/todo。
// 本文件同时提供树视图绑定属性（缩进/状态圆点颜色/完成计数），由 MainWindow 架构面板渲染。
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using GAIRR.AgentHost;

namespace GAIRR;

/// <summary>架构树节点：JSON 字段（name/status/note/children）+ 加载后计算的 UI 绑定属性</summary>
public class ArchNode
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>状态：done=完成 / partial=部分完成 / todo=未完成（分类节点可为空，由叶子汇总）</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = "";

    /// <summary>备注（鼠标悬停提示）</summary>
    [JsonPropertyName("note")] public string? Note { get; set; }

    /// <summary>关联编排任务 id（plan.json plans[].id；无则为空；回写时按 id 定位）。
    /// 编排投影中所有节点（含子节点）都填计划 id，用于整体执行/裁决的定位。</summary>
    [JsonPropertyName("planId")] public string? PlanId { get; set; }

    /// <summary>编排投影中节点自身 id（plan.json nodes[].id）：标记通过/跳过、删除分支用。仅编排树分支节点有值。</summary>
    [JsonIgnore] public string? NodeRef { get; set; }

    /// <summary>节点来源："func"=功能架构（architecture.json）、"plan"=编排任务（plan.json 投影，运行时计算、不落盘）</summary>
    [JsonIgnore] public string? Kind { get; set; }

    /// <summary>节点类型（编排投影用）：0=分组/功能分类，1=叶子。功能树叶子 IsLeaf=true 即叶子。</summary>
    [JsonIgnore] public int NodeType { get; set; }

    /// <summary>叶子目标（编排投影的 leaf.goal，悬停/展示用）。</summary>
    [JsonIgnore] public string? LeafGoal { get; set; }

    /// <summary>叶子执行会话 id（编排投影的 leaf.sessionRunId；有值时可打开只读会话视图）。</summary>
    [JsonIgnore] public string? SessionRunId { get; set; }

    [JsonPropertyName("children")] public List<ArchNode> Children { get; set; } = new();

    /// <summary>节点路径（根为 Name，子为 "父路径/Name"），加载后计算，用作 archRef 定位锚点</summary>
    [JsonIgnore] public string NodePath { get; set; } = "";

    /* ---- 加载后计算的 UI 绑定属性 ---- */

    [JsonIgnore] public int Depth { get; set; }
    [JsonIgnore] public int Total { get; set; }    // 叶子（最小独立功能）总数
    [JsonIgnore] public int Done { get; set; }     // 已完成叶子数
    [JsonIgnore] public bool IsLeaf => Children.Count == 0;

    /// <summary>默认展开层级：L0/L1 展开，其余收起</summary>
    [JsonIgnore] public bool IsOpen { get; set; }

    /// <summary>行缩进（按层级深度）</summary>
    [JsonIgnore] public Thickness Indent => new Thickness(2 + Depth * 14, 2, 0, 2);

    /// <summary>分类节点右侧显示"完成/总数"，叶子不显示</summary>
    [JsonIgnore] public string CountText => IsLeaf ? "" : $"{Done}/{Total}";

    /// <summary>悬停提示：有备注带备注，否则显示名称</summary>
    [JsonIgnore] public string Tip => string.IsNullOrWhiteSpace(Note) ? Name : $"{Name}：{Note}";

    static Brush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        b.Freeze();
        return b;
    }

    static readonly Brush DoneBrush = Freeze("#3DDC97");    // 绿：完成
    static readonly Brush PartialBrush = Freeze("#F5B955"); // 桔：进行中/审查中
    static readonly Brush TodoBrush = Freeze("#5A6478");    // 灰：未完成
    static readonly Brush RunningBrush = Freeze("#F5B955"); // 桔：执行中（与审查中同色，区分靠文字粗体）
    static readonly Brush FailedBrush = Freeze("#E53E3E");  // 红：失败
    static readonly Brush SkippedBrush = Freeze("#6B7088"); // 灰：已跳过（删除线+灰，与文字色一致）
    static readonly Brush NameBrushNormal = Freeze("#DCDCE6");
    static readonly Brush NameBrushDim = Freeze("#8B90A8");

    /// <summary>编排叶子状态圆点色：按 plan 叶子状态映射（pending灰/running·reviewing桔/passed绿/failed红/skipped灰）。
    /// 功能树（Kind 非 plan）走下方按完成率/自身状态的逻辑。</summary>
    [JsonIgnore]
    public Brush PlanDotBrush => Status switch
    {
        "running" => RunningBrush,
        "reviewing" => PartialBrush,
        "passed" => DoneBrush,
        "failed" => FailedBrush,
        "done" => DoneBrush,
        "skipped" => SkippedBrush,
        _ => TodoBrush,   // pending / 空
    };

    /// <summary>状态后缀文字（仅编排叶子）：passed→"✓"、skipped→"已跳过"，让裁决反馈不依赖小圆点。</summary>
    [JsonIgnore]
    public string StatusSuffix => Kind == "plan" && NodeType == 1 ? Status switch
    {
        "passed" => "✓",
        "skipped" => "已跳过",
        _ => "",
    } : "";

    /// <summary>状态后缀颜色：与后缀语义一致（通过=绿、跳过=灰、其余=暗）。</summary>
    [JsonIgnore]
    public Brush StatusSuffixBrush => Status switch
    {
        "passed" => DoneBrush,
        "skipped" => SkippedBrush,
        _ => NameBrushDim,
    };

    /// <summary>状态圆点颜色：叶子按自身状态；分类节点按完成率（全绿/部分琥珀/全灰）。编排投影节点用 PlanDot 色。</summary>
    [JsonIgnore]
    public Brush DotBrush
    {
        get
        {
            if (Kind == "plan" && NodeType == 1) return PlanDotBrush;   // 编排叶子
            if (Kind == "plan")
            {
                // 编排分组：按子叶子完成率（同功能分类逻辑）
                if (Total == 0) return TodoBrush;
                if (Done == Total) return DoneBrush;
                return Done == 0 ? TodoBrush : PartialBrush;
            }
            if (!IsLeaf)
            {
                if (Total == 0) return TodoBrush;
                if (Done == Total) return DoneBrush;
                return Done == 0 ? TodoBrush : PartialBrush;
            }
            return Status switch { "done" => DoneBrush, "partial" => PartialBrush, _ => TodoBrush };
        }
    }

    /// <summary>名称颜色：未完成的叶子置灰，其余正常；编排失败的叶子也用正常色（由圆点示意）。</summary>
    [JsonIgnore] public Brush NameBrush => IsLeaf && Status != "done" && Kind != "plan" ? NameBrushDim : NameBrushNormal;

    /// <summary>递归预处理：计算深度、节点路径、叶子统计与默认展开状态（加载后调用一次）</summary>
    public void Prepare(int depth, string parentPath = "")
    {
        Depth = depth;
        NodePath = string.IsNullOrEmpty(parentPath) ? Name : parentPath + "/" + Name;
        IsOpen = depth <= 1;
        if (IsLeaf) { Total = 1; Done = Status is "done" or "passed" ? 1 : 0; return; }
        foreach (var c in Children) c.Prepare(depth + 1, NodePath);
        Total = Children.Sum(c => c.Total);
        Done = Children.Sum(c => c.Done);
    }

    /// <summary>按路径查找节点（根路径为 Name，子为 "父/子"）；未找到返回 null。</summary>
    public ArchNode? FindByPath(string path)
    {
        if (NodePath == path) return this;
        foreach (var c in Children)
        {
            var hit = c.FindByPath(path);
            if (hit != null) return hit;
        }
        return null;
    }
}

/// <summary>架构树文件（.gairr/architecture.json）顶层结构。v1 为单根 tree，v1+ 支持多根 roots（功能根 + 编排根投影不落盘）。</summary>
public class ArchTreeFile
{
    [JsonPropertyName("version")] public string Version { get; set; } = "1.0";
    [JsonPropertyName("project")] public string Project { get; set; } = "";
    [JsonPropertyName("updated")] public string Updated { get; set; } = "";

    /// <summary>(兼容) v1 单根：读旧文件用。新文件写入 roots。</summary>
    [JsonPropertyName("tree")] public ArchNode? Root { get; set; }

    /// <summary>(v1+) 多根：功能根 + 任意扩展根。旧单根迁移后先有功能根一项。</summary>
    [JsonPropertyName("roots")] public List<ArchNode> Roots { get; set; } = new();
}

/// <summary>架构树加载器：读取/保存项目级 .gairr/architecture.json</summary>
public static class ArchTreeStore
{
    /// <summary>架构树文件路径（{项目根}/.gairr/architecture.json）</summary>
    public static string FilePath(string projectRoot) => Path.Combine(projectRoot, ".gairr", "architecture.json");

    /// <summary>加载架构树并预处理；文件不存在或解析失败返回 null（由调用方显示占位提示）。
    /// 兼容 v1 单根（tree 字段）→ 迁移为多根 Roots；全部根标 Kind=func。</summary>
    public static ArchTreeFile? Load(string projectRoot)
    {
        try
        {
            var path = FilePath(projectRoot);
            if (!File.Exists(path)) return null;
            var arch = JsonSerializer.Deserialize<ArchTreeFile>(File.ReadAllText(path));
            if (arch == null) return null;
            // v1 单根 → v1+ 多根兼容迁移
            if (arch.Root != null && arch.Roots.Count == 0) arch.Roots.Add(arch.Root);
            arch.Root = null;
 foreach (var r in arch.Roots) { r.Kind = "func"; r.Prepare(0); }
 RestoreLinks(projectRoot, arch); // ④ 关联台账自愈：architecture.json 被外部重写丢了 planId 时按节点路径补回
 return arch;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Error] 架构树加载失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>保存架构树（原子写：临时文件+替换）；失败返回 false 并输出调试日志。</summary>
    public static bool Save(string projectRoot, ArchTreeFile arch)
    {
        try
        {
            var dir = Path.Combine(projectRoot, ".gairr");
            Directory.CreateDirectory(dir);
            var path = FilePath(projectRoot);
            var tmp = path + ".tmp";
            arch.Updated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            // Encoder 放开：architecture.json 中文直存（人工可读/可手改）；Deserialize 对旧 \uXXXX 转义文件同样兼容
            File.WriteAllText(tmp, JsonSerializer.Serialize(arch, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
 File.Copy(tmp, path, true);
 File.Delete(tmp);
 WriteLinks(projectRoot, arch); // ④ 同步关联台账（planId 丢失时的自愈依据）
 return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Error] 架构树保存失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>按节点路径更新叶子状态（done/partial/todo），并刷新统计后落盘。在功能根上查找；返回是否更新成功。</summary>
    public static bool SetNodeStatus(string projectRoot, string nodePath, string status)
    {
        var arch = Load(projectRoot);
        var func = arch?.Roots.FirstOrDefault(r => r.Kind == "func");
        if (func == null) return false;
        var node = func.FindByPath(nodePath);
        if (node == null || !node.IsLeaf) return false;
        node.Status = status;
        func.Prepare(0);
        return Save(projectRoot, arch!);
    }

    /// <summary>同步已关联编排的功能叶子状态：以 plan.json 为唯一事实源（UI 各编排状态变更点调用，功能树状态随编排结果联动）。
    /// 只处理带 planId 的叶子（人工状态不受影响）；计划不存在（已从 plan.json 清除）或计划无执行叶子时保持叶子现状不动。
    /// 推导：全部叶子 passed→done；叶子全为 pending/skipped/空（计划未开始或整树跳过）→todo；其余（部分通过/执行中/失败/暂停）→partial。
    /// 无实际变化不落盘；返回本次更新的叶子数（0=无变化）。</summary>
    public static int SyncPlanStatus(string projectRoot)
    {
        var arch = Load(projectRoot);
        if (arch == null) return 0;
        var changed = 0;
        var plans = new Dictionary<string, PlanDto?>(StringComparer.Ordinal);
        foreach (var root in arch.Roots)   // Load 已把全部根置为 func 语义
        {
            foreach (var leaf in EnumerateLeaves(root))
            {
                if (string.IsNullOrWhiteSpace(leaf.PlanId)) continue;   // 仅同步带编排关联的功能叶
                if (!plans.TryGetValue(leaf.PlanId!, out var plan))
                {
                    plan = PlanStore.GetById(projectRoot, leaf.PlanId!);
                    plans[leaf.PlanId!] = plan;
                }
                if (plan == null) continue;   // 计划已被清除：保留叶子现状
                var derived = DeriveLeafStatus(plan);
                if (derived == null) continue;   // 无可判定执行叶子的计划：不动
                if (leaf.Status != derived)
                {
                    leaf.Status = derived;
                    changed++;
                }
            }
        }
        if (changed == 0) return 0;
        foreach (var root in arch.Roots) root.Prepare(0);   // 刷新统计
        return Save(projectRoot, arch) ? changed : 0;
    }

    /// <summary>递归枚举功能子树内的全部叶子节点（含多级功能分类）。</summary>
    static IEnumerable<ArchNode> EnumerateLeaves(ArchNode node)
    {
        foreach (var c in node.Children)
        {
            if (c.IsLeaf) yield return c;
            else foreach (var d in EnumerateLeaves(c)) yield return d;
        }
    }

    /// <summary>按编排计划推导功能叶完成状态；null=计划已删除或无执行叶子、无法判定（调用侧保持现状）。</summary>
    static string? DeriveLeafStatus(PlanDto plan)
    {
        if (plan.Status == "deleted") return null;
        var leaves = plan.Nodes.Where(n => n.Type == "leaf").ToList();
        if (leaves.Count == 0) return null;
        if (plan.Status == "done" || leaves.All(n => n.Status == "passed")) return "done";
        if (leaves.All(n => n.Status is null or "" or "pending" or "skipped")) return "todo";
        return "partial";
    }

    /// <summary>把功能节点关联到编排任务。叶子=直接写 planId（重复关联覆盖）；
    /// 根/分支=优先复用同名子叶，否则自动追加名为 leafName 的功能子叶并关联（分支新建编排的落地：确认后新功能落为叶子）。
    /// 返回 0=失败，1=关联到现有节点（叶子直连或复用同名子叶），2=新增了功能子叶。</summary>
    public static int AttachPlan(string projectRoot, string nodePath, string planId, string? leafName = null)
    {
        var arch = Load(projectRoot);
        var func = arch?.Roots.FirstOrDefault(r => r.Kind == "func");
        if (func == null) return 0;
        var node = func.FindByPath(nodePath);
        if (node == null) return 0;
        var added = false;
        if (node.IsLeaf)
        {
            node.PlanId = planId;
        }
        else
        {
            var name = string.IsNullOrWhiteSpace(leafName) ? planId : leafName.Trim();
            var exist = node.Children.FirstOrDefault(c => c.Name == name);
            if (exist != null) exist.PlanId = planId;   // 同名子叶已存在=同一功能再次编排，仅更新关联不重复追加
            else
            {
                node.Children.Add(new ArchNode { Name = name, PlanId = planId });
                added = true;
            }
        }
        func.Prepare(0);
        return Save(projectRoot, arch!) ? (added ? 2 : 1) : 0;
    }

    /// <summary>把 plan.json 编排任务投影为架构栏“编排根”（Kind=plan，不落盘；plan.json 为唯一事实源）。
    /// 返回 [根节点, 叶子数]。按 PlanNodeDto.Children(id 引用) 组装，保持声明顺序。</summary>
    /// <summary>解除某计划在功能树上的全部旧关联（重新关联前清理，防一个计划被两个功能叶同时引用）；
 /// 清空后连台账一并覆盖写入，避免下次 Load 时又被自愈补回。返回清除条数。</summary>
 public static int DetachPlan(string projectRoot, string planId)
 {
 var arch = Load(projectRoot);
 if (arch == null) return 0;
 var n = 0;
 foreach (var root in arch.Roots)
 foreach (var leaf in EnumerateLeaves(root))
 if (leaf.PlanId == planId) { leaf.PlanId = null; n++; }
 if (n == 0) return 0;
 foreach (var root in arch.Roots) root.Prepare(0);
 return Save(projectRoot, arch) ? n : 0;
 }

 /// <summary>把 plan.json 编排任务投影为架构栏编排根。</summary>
 public static (ArchNode Root, int Leaves) FromPlan(PlanDto p)
    {
        var leaves = 0;
        var byId = new Dictionary<string, ArchNode>(StringComparer.Ordinal);
        foreach (var d in p.Nodes)
        {
            if (d.Type == "group")
            {
                byId[d.Id] = new ArchNode { Name = d.Title, PlanId = p.Id, NodeRef = d.Id, Kind = "plan", NodeType = 0, Status = d.Status ?? "" };
            }
            else
            {
                leaves++;
                byId[d.Id] = new ArchNode
                {
                    Name = d.Title,
                    PlanId = p.Id,
                    NodeRef = d.Id,
                    Kind = "plan",
                    NodeType = 1,
                    Status = d.Status ?? "pending",
                    LeafGoal = d.Goal,
                    SessionRunId = d.SessionRunId,
                    Note = string.IsNullOrWhiteSpace(d.Accept) ? (d.Goal ?? d.Title) : $"{d.Title}：{d.Accept}",
                };
            }
        }
        // 顶层 = 无父引用（没有节点把它列为 children）的节点；若都在 children 中被引用则退化为第一个
        var topIds = p.Nodes.Where(n => !p.Nodes.Any(o => o.Children.Contains(n.Id))).Select(n => n.Id).ToList();
        if (topIds.Count == 0 && p.Nodes.Count > 0) topIds.Add(p.Nodes[0].Id);
        if (topIds.Count > 0)
        {
            var root = new ArchNode { Name = p.Title, Kind = "plan", NodeType = 0, PlanId = p.Id, Note = p.Goal, Depth = 0, IsOpen = true, NodePath = p.Title };
            root.Children = new List<ArchNode>();
            foreach (var tid in topIds)
            {
                if (!byId.ContainsKey(tid)) continue;
                var child = BuildPlanNode(p.Id, p.Nodes, byId, tid);
                root.Children.Add(child);
            }
            PreparePlan(root, root.Children);
            return (root, leaves);
        }
        var empty = new ArchNode { Name = p.Title, Kind = "plan", NodeType = 0, PlanId = p.Id, Note = p.Goal };
        empty.Total = 0; empty.Done = 0; empty.NodePath = empty.Name; empty.Depth = 0; empty.IsOpen = true;
        return (empty, 0);
    }

    /// <summary>按 id 递归组装节点树（children 为 id 引用）。</summary>
    static ArchNode BuildPlanNode(string planId, List<PlanNodeDto> nodes, Dictionary<string, ArchNode> byId, string id)
    {
        var arch = byId.TryGetValue(id, out var n) ? n : new ArchNode { Name = id, Kind = "plan", NodeType = 0, PlanId = planId, NodeRef = id };
        var d = nodes.FirstOrDefault(x => x.Id == id);
        if (d != null && d.Type == "group")
        {
            arch.Children = new List<ArchNode>();
            foreach (var cid in d.Children)
            {
                if (!byId.ContainsKey(cid)) continue;
                var child = BuildPlanNode(planId, nodes, byId, cid);
                child.Name = byId[cid].Name;
                child.Kind = "plan";
                child.PlanId = planId;
                child.NodeRef = byId[cid].NodeRef ?? cid;
                child.NodeType = byId[cid].NodeType;
                child.Status = byId[cid].Status ?? child.Status;
                child.LeafGoal = byId[cid].LeafGoal;
                child.Note = byId[cid].Note ?? child.Note;
                arch.Children.Add(child);
            }
        }
        return arch;
    }

    static void PreparePlan(ArchNode root, List<ArchNode> children)
    {
        foreach (var c in children) c.Prepare(1, root.Name);
        root.Total = children.Sum(c => c.Total);
        root.Done = children.Sum(c => c.Done);
 root.NodePath = root.Name;
 root.Depth = 0;
 root.IsOpen = true;
 }

 /// <summary>关联台账路径：{项目根}/.gairr/arch-links.json（节点路径 → planId）。</summary>
 public static string LinkLedgerPath(string projectRoot) => Path.Combine(projectRoot, ".gairr", "arch-links.json");

 /// <summary>在已加载的架构树上按来源路径重建关联（叶子直写；分支复用同名空关联子叶或新增）。
 /// 保守策略：同名子叶已被其它计划占用时不抢改、不重复追加；返回 1=有改动，0=未改。</summary>
 static int EnsureLink(ArchTreeFile arch, string nodePath, string planId, string? leafName)
 {
 var func = arch.Roots.FirstOrDefault(r => r.Kind == "func");
 var node = func?.FindByPath(nodePath);
 if (node == null) return 0;
 if (node.IsLeaf)
 {
 if (node.PlanId == planId) return 0;
 node.PlanId = planId;
 return 1;
 }
 var name = string.IsNullOrWhiteSpace(leafName) ? planId : leafName.Trim();
 var exist = node.Children.FirstOrDefault(c => c.Name == name);
 if (exist != null)
 {
 if (exist.PlanId == planId) return 0;
 if (!string.IsNullOrWhiteSpace(exist.PlanId)) return 0; // 已被其它计划占用：不自愈，避免误改
 exist.PlanId = planId;
 return 1;
 }
 node.Children.Add(new ArchNode { Name = name, PlanId = planId });
 return 1;
 }

 /// <summary>从关联台账补回丢失的 planId（architecture.json 被外部重写后自愈）；返回补回条数。</summary>
 static int RestoreLinks(string projectRoot, ArchTreeFile arch)
 {
 try
 {
 var lp = LinkLedgerPath(projectRoot);
 if (!File.Exists(lp)) return 0;
 var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(lp));
 if (map == null || map.Count == 0) return 0;
 var n = 0;
 foreach (var root in arch.Roots)
 foreach (var leaf in EnumerateLeaves(root))
 {
 if (!string.IsNullOrWhiteSpace(leaf.PlanId)) continue;
 if (map.TryGetValue(leaf.NodePath, out var pid) && !string.IsNullOrWhiteSpace(pid))
 {
 leaf.PlanId = pid;
 n++;
 }
 }
 return n;
 }
 catch (Exception ex)
 {
 System.Diagnostics.Debug.WriteLine($"[Error] 关联台账恢复失败: {ex.Message}");
 return 0;
 }
 }

 /// <summary>把当前架构树的 planId 关联写入台账（节点路径 到 planId），供 architecture.json 被重写后自愈。</summary>
 static void WriteLinks(string projectRoot, ArchTreeFile arch)
 {
 try
 {
 var map = new Dictionary<string, string>(StringComparer.Ordinal);
 foreach (var root in arch.Roots)
 foreach (var leaf in EnumerateLeaves(root))
 {
 if (string.IsNullOrWhiteSpace(leaf.PlanId) || string.IsNullOrEmpty(leaf.NodePath)) continue;
 map[leaf.NodePath] = leaf.PlanId!;
 }
 File.WriteAllText(LinkLedgerPath(projectRoot),
 JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
 }
 catch (Exception ex)
 {
 System.Diagnostics.Debug.WriteLine($"[Error] 关联台账写入失败: {ex.Message}");
 }
 }
}
