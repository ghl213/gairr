// PlanConfirmDialog.xaml.cs
// 计划确认对话框（P2 + P3 5.2 增强）：把 PlanDto 的扁平节点组装成任务树展示；多模型商讨模式下右侧列出候选，
// 支持点击切换预览；"待补验收"叶子（缺 accept）高亮并阻止确认，提供一键默认值；疑似跨节点改动点勾选
// "确需扩散"（目标节点不在确认框填写，落 crossRefs 占位 toNodeId=null + collectLog 记录待定）；
// 任何改树置 DocOutdated="文档待同步"。
// "确认执行"置 approved 并落盘（P3 PlanRunner 消费入口）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GAIRR.AgentHost;
using GAIRR.Core;

namespace GAIRR;

public partial class PlanConfirmDialog : Window
{
    readonly PlanDto plan;
    readonly string projectRoot;
    readonly string sessionId;        // 8 位 GUI 会话 id（决策收件箱匹配维度）
    bool switching;   // 程序选中候选时抑制 SelectionChanged 重入
    bool treeChanged; // 树被修改过（补 accept / 勾选扩散）→ 置 DocOutdated
    DispatcherTimer? pollTimer;       // 手机端决策回写轮询：命中本会话 planId 即以编程方式注入结果关闭弹窗

    /// <summary>一键默认验收文本（决策 2：缺省 accept 由用户在确认框补，一键给默认值保证门禁不空转）。</summary>
    const string DefaultAccept = "按 goal 完成实现并通过编译";

    public PlanConfirmDialog(PlanDto plan, string projectRoot, string sessionId)
    {
        InitializeComponent();
        this.plan = plan;
        this.projectRoot = projectRoot;
        this.sessionId = sessionId;

        // L0 目标全文入只读文本框：可滚动查看，不再于信息区挤压标题/模式/任务树
        txtGoal.Text = plan.Goal;
        // 任务标题（新建编排会话时填写，留空则由目标截断生成）：有值才显示，位于模式行上方——
        // 信息层级按用户要求：窗口标题 → 任务标题 → 模式 → 主体内容
        if (!string.IsNullOrWhiteSpace(plan.Title))
        {
            tbTitle.Text = $"标题：{plan.Title}";
            tbTitle.Visibility = Visibility.Visible;
        }
        tbMode.Text = plan.PlanMode == "negotiation" ? $"模式：多模型商讨 · 入选候选 {plan.SelectedCandidateId ?? "-"}"
                                                      : "模式：标准（单模型）";
        if (plan.AssessmentFailed)
            tbWarn.Text = "⚠ 评估输出解析失败，已按降级策略选取候选";

        BuildTree();
        BuildCandidateList();
        BuildPendingLeaves();
        BuildCrossNode();
        UpdateConfirmState();
        StartMobilePolling();
    }

    // ────────────────────── 手机端决策回写轮询（方案 A：SSE 推 PlanConfirm → 手机回写 decisions.inbox.json → 弹窗消费） ──────────────────────

    /// <summary>启动手机端决策轮询：每 2s 读 decisions.inbox.json，命中本会话+planId 的条目即注入结果并收口。
    /// 尽力而为：读取/解析失败静默跳过下一拍；本地用户手动确认/取消时由 OnConfirm/OnCancel 停表。</summary>
    void StartMobilePolling()
    {
        try
        {
            pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            pollTimer.Tick += OnMobilePollTick;
            pollTimer.Start();
        }
        catch { /* 定时器启动失败不影响本地弹窗 */ }
    }

    void OnMobilePollTick(object? sender, EventArgs e)
    {
        try
        {
            if (!MobileDecisionStore.Take(Paths.DataDir, sessionId, plan.Id, out var allow)) return;   // 无本会话决策，下一拍再查
            pollTimer?.Stop();
            if (allow)
            {
                if (plan.Nodes.Count == 0) return;                      // 方案为空走不到确认（同本地 OnConfirm 语义）
                if (PendingLeaves().Count > 0) return;                  // 待补验收未清：与本地门禁一致，忽略外部确认
                FlushCrossNode();                                       // 复用本地确认收口链路（crossRefs/collectLog 落盘）
                plan.Status = "approved";
                GAIRR.AgentHost.PlanStore.Save(projectRoot, plan);
                DialogResult = true;
            }
            else DialogResult = false;                                  // 手机端拒绝了方案 → 等同本地取消
        }
        catch { /* 解析/IO 抖动静默，下一拍重试 */ }
    }

    // ────────────────────── 任务树 ──────────────────────

    /// <summary>按当前 plan.Nodes 重建任务树（切换候选后复用）。</summary>
    void BuildTree()
    {
        tree.Items.Clear();
        var byId = plan.Nodes.ToDictionary(n => n.Id, n => n);
        var childIds = plan.Nodes.SelectMany(n => n.Children).ToHashSet();
        var dim = FindResource("DimBrush") as Brush ?? Brushes.Gray;
        foreach (var root in plan.Nodes.Where(n => !childIds.Contains(n.Id)))
            tree.Items.Add(BuildItem(root, byId, dim));
    }

    /// <summary>递归构建单个节点项（group 递归子节点；leaf 悬停提示 goal/accept，待补/跨节点高亮）。</summary>
    TreeViewItem BuildItem(PlanNodeDto node, Dictionary<string, PlanNodeDto> byId, Brush dim)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock
        {
            Text = node.Type == "group" ? $"▸ {node.Title}" : $"· {node.Title}",
            Foreground = FindResource("TextBrush") as Brush ?? Brushes.White,
            FontSize = 13,
        });
        if (node.Type == "leaf")
        {
            var isPending = string.IsNullOrWhiteSpace(node.Accept);   // 待补叶子
            var isCross = node.SuspectCrossNode;
            // 验收尾注 / 待补标记 / 跨节点标记
            if (isPending)
                header.Children.Add(LeafMark("　⚠ 待补验收", new SolidColorBrush(Color.FromRgb(0xF0, 0xA1, 0x3E))));
            else
                header.Children.Add(new TextBlock
                {
                    Text = $"　✓ {Trunc(node.Accept!, 34)}",
                    Foreground = dim,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            if (isCross)
                header.Children.Add(LeafMark("　⇄ 疑似跨节点", new SolidColorBrush(Color.FromRgb(0xE8, 0xB9, 0x3E))));
        }
        var item = new TreeViewItem { Header = header, IsExpanded = true };
        if (node.Type == "leaf")
            item.ToolTip = $"目标：{node.Goal}\n验收：{(string.IsNullOrWhiteSpace(node.Accept) ? "（待补）" : node.Accept)}\n模式：{node.Mode}"
                         + (node.SuspectCrossNode ? "\n⚠ 疑似跨节点改动点" : "");
        foreach (var cid in node.Children)
            if (byId.TryGetValue(cid, out var child))
                item.Items.Add(BuildItem(child, byId, dim));
        return item;
    }

    static TextBlock LeafMark(string text, Brush color) => new()
    {
        Text = text,
        Foreground = color,
        FontSize = 11,
        VerticalAlignment = VerticalAlignment.Center,
    };

    // ────────────────────── 候选列表 ──────────────────────

    /// <summary>填充右侧候选列表（仅商讨模式且存在候选时展开右栏）。</summary>
    void BuildCandidateList()
    {
        if (plan.PlanMode != "negotiation" || plan.Candidates.Count == 0)
        {
            panelCandidates.Visibility = Visibility.Collapsed;
            UpdateSideBar();
            return;
        }
        panelCandidates.Visibility = Visibility.Visible;
        var dim = FindResource("DimBrush") as Brush ?? Brushes.Gray;
        foreach (var c in plan.Candidates.OrderByDescending(c => c.Score))
        {
            var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
            panel.Children.Add(new TextBlock
            {
                Text = $"{(c.IsMerged ? "★ 融合方案" : c.Model)}　评分 {c.Score:F2}{(c.Invalid ? "　✗ 无效" : "")}",
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = FindResource("TextBrush") as Brush ?? Brushes.White,
            });
            if (c.Pros.Count > 0) panel.Children.Add(MiniText("优：" + string.Join("；", c.Pros), dim));
            if (c.Cons.Count > 0) panel.Children.Add(MiniText("劣：" + string.Join("；", c.Cons), dim));
            lbCandidates.Items.Add(new ListBoxItem { Content = panel, Tag = c.Id });
        }
        // 默认选中当前入选候选
        switching = true;
        foreach (ListBoxItem it in lbCandidates.Items)
            if ((string)it.Tag == plan.SelectedCandidateId) { it.IsSelected = true; break; }
        switching = false;
        UpdateSideBar();
    }

    static TextBlock MiniText(string text, Brush dim) => new()
    {
        Text = text,
        FontSize = 11,
        Foreground = dim,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 2, 0, 0),
    };

    /// <summary>切换候选：复制该候选节点为生效 nodes 并重建任务树预览。</summary>
    void OnCandidateSelected(object sender, SelectionChangedEventArgs e)
    {
        if (switching || lbCandidates.SelectedItem is not ListBoxItem item) return;
        var cid = (string)item.Tag;
        var cand = plan.Candidates.FirstOrDefault(c => c.Id == cid);
        if (cand == null || cand.Nodes.Count == 0) return;
        plan.SelectedCandidateId = cid;
        plan.Nodes = JsonSerializer.Deserialize<List<PlanNodeDto>>(JsonSerializer.Serialize(cand.Nodes)) ?? plan.Nodes;
        tbMode.Text = $"模式：多模型商讨 · 入选候选 {cid}";
        MarkTreeChanged();
        BuildTree();
        BuildPendingLeaves();
        BuildCrossNode();
        UpdateConfirmState();
    }

    // ────────────────────── 待补验收（决策 2） ──────────────────────

    /// <summary>当前缺 accept 的叶子（待补）。</summary>
    List<PlanNodeDto> PendingLeaves() =>
        plan.Nodes.Where(n => n.Type == "leaf" && string.IsNullOrWhiteSpace(n.Accept)).ToList();

    /// <summary>填充待补叶子列表与提示；未补全则禁用确认。</summary>
    void BuildPendingLeaves()
    {
        var pending = PendingLeaves();
        lbPendingLeaves.Items.Clear();
        if (pending.Count == 0)
        {
            panelPending.Visibility = Visibility.Collapsed;
            lbPendingLeaves.ItemsSource = null;
            UpdateSideBar();
            return;
        }
        panelPending.Visibility = Visibility.Visible;
        lbPendingLeaves.ItemsSource = pending.Select(n => new { n.Title }).ToList();
        tbPendingHint.Text = $"共 {pending.Count} 个叶子未声明验收标准。请为它们补齐 accept（可点击右侧按钮一键补默认值），否则无法确认执行。";
        UpdateSideBar();
    }

    /// <summary>一键默认值：给所有待补叶子补"按 goal 完成实现并通过编译"。</summary>
    void OnFillDefaultAccept(object sender, RoutedEventArgs e)
    {
        var pending = PendingLeaves();
        if (pending.Count == 0) return;
        foreach (var n in pending) n.Accept = DefaultAccept;
        MarkTreeChanged();
        BuildTree();
        BuildPendingLeaves();
        UpdateConfirmState();
        tbConfirmWarn.Text = $"已为 {pending.Count} 个叶子补齐默认验收标准。";
    }

    // ────────────────────── 疑似跨节点扩散（决策 3） ──────────────────────

    /// <summary>跨节点行描述：叶子 + 勾选框（构建时持有引用，便于确认时读取；Leaf 用 null! 惰性赋值）。</summary>
    sealed class CrossRow
    {
        public PlanNodeDto Leaf = null!;
        public CheckBox Check = null!;
    }

    readonly List<CrossRow> crossRows = new();   // 当前界面跨节点行（BuildCrossNode 重建）

    /// <summary>当前疑似跨节点的叶子。</summary>
    List<PlanNodeDto> CrossNodeLeaves() =>
        plan.Nodes.Where(n => n.Type == "leaf" && n.SuspectCrossNode).ToList();

    /// <summary>填充疑似跨节点列表：逐行勾选（目标节点不在确认框填写，先挂待定占位）。</summary>
    void BuildCrossNode()
    {
        foreach (var row in crossRows)
            if (row.Check != null)
                row.Check.Unchecked -= OnCrossNodeUnchecked;
        crossRows.Clear();
        lbCrossNode.Children.Clear();
        var crosses = CrossNodeLeaves();
        panelCrossNode.Visibility = crosses.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (crosses.Count == 0) { UpdateSideBar(); return; }
        foreach (var leaf in crosses)
        {
            var check = new CheckBox
            {
                Content = new TextBlock { Text = $"· {leaf.Title}（{Trunc(leaf.Goal ?? leaf.Title, 46)}）", TextWrapping = TextWrapping.Wrap },
                Foreground = new SolidColorBrush(Color.FromRgb(0xEE, 0xF0, 0xF6)),
                FontSize = 11,
                Margin = new Thickness(0, 3, 0, 0),
            };
            check.Unchecked += OnCrossNodeUnchecked;
            lbCrossNode.Children.Add(check);
            crossRows.Add(new CrossRow { Leaf = leaf, Check = check });
        }
        UpdateSideBar();
    }

    /// <summary>取消勾选 → 移除该源叶子已记录的扩散占位（同源删除）。取消不置树已改，仅写回时生效。</summary>
    void OnCrossNodeUnchecked(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox chk) return;
        var leaf = crossRows.FirstOrDefault(r => r.Check == chk)?.Leaf;
        if (leaf == null) return;
        plan.CollectLog.RemoveAll(x => x.Q != null && x.Q.Contains($"扩散:{leaf.Id}"));
        MarkTreeChanged();
    }

    /// <summary>确认时写入勾选的跨节点扩散占位 + collectLog（决策 3：引用不复制树，可追溯；目标节点待后续编排时补）。</summary>
    void FlushCrossNode()
    {
        var anyCross = false;
        var now = DateTime.Now.ToString("s");
        foreach (var row in crossRows)
        {
            if (row.Check.IsChecked != true) continue;
            var leaf = row.Leaf;
            var cp = leaf.Goal ?? leaf.Title;
            // 目标节点不在此确认框填写：挂 toNodeId=null 待定占位 + collect 记录；后续编排到目标节点时再补引用。
            // (FlushCrossNode 仅确认时执行一次；取消勾选已由 OnCrossNodeUnchecked 清掉同源记录，这里确保不重复累积)
            if (!plan.CrossRefs.Any(x => x.FromNodeId == leaf.Id))
                plan.CrossRefs.Add(new CrossRefDto { FromNodeId = leaf.Id, ToNodeId = null, ChangePoint = cp, At = now });
            if (plan.CollectLog.Count(x => x.Q != null && x.Q.Contains($"扩散:{leaf.Id}:{cp}")) == 0)
                plan.CollectLog.Add(new CollectEntryDto { Q = $"扩散:{leaf.Id}:{cp}", A = "已确认需扩散，目标节点待定", At = now });
            anyCross = true;
        }
        if (anyCross) MarkTreeChanged();   // 勾选扩散 = 改树 → 文档待同步
    }

    // ────────────────────── 改树标记 ──────────────────────

    /// <summary>侧栏（候选/待补/跨节点）按需显隐：任一子卡可见才展开；全空收起让主区（目标+任务树）占满全宽。</summary>
    void UpdateSideBar()
    {
        sidePanel.Visibility =
            panelCandidates.Visibility == Visibility.Visible
            || panelPending.Visibility == Visibility.Visible
            || panelCrossNode.Visibility == Visibility.Visible
                ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>无系统标题栏（WindowStyle=None）：按住顶部标题行（整行宽）拖动窗口。</summary>
    void OnTitleDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }


    /// <summary>树被修改（补 accept / 切换候选 / 扩散）→ 置 DocOutdated="文档待同步"（决策 2 修订语义）。</summary>
    void MarkTreeChanged()
    {
        treeChanged = true;
        plan.DocOutdated = true;
    }

    /// <summary>刷新确认按钮与拦截提示：存在待补叶子则禁用确认。</summary>
    void UpdateConfirmState()
    {
        var pending = PendingLeaves().Count;
        if (pending > 0)
        {
            btnConfirm.IsEnabled = false;
            tbConfirmWarn.Text = $"尚有 {pending} 个叶子未补齐验收标准，无法确认执行。请先为一键补默认验收或取消。";
        }
        else
        {
            btnConfirm.IsEnabled = true;
            tbConfirmWarn.Text = treeChanged ? "⚠ 树已修改，确认后文档标记为待同步（docOutdated），将由模型按 diff 更新 doc.md。" : "";
        }
    }

    // ────────────────────── 按钮 ──────────────────────

    void OnCancel(object sender, RoutedEventArgs e)
    {
        pollTimer?.Stop();
        DialogResult = false;
    }

    /// <summary>确认执行：写入跨节点扩散引用 + 状态切 approved 并落盘；待补未清防漏（按钮已禁用，兜底校验）。</summary>
    void OnConfirm(object sender, RoutedEventArgs e)
    {
        pollTimer?.Stop();
        if (plan.Nodes.Count == 0) { MessageBox.Show(this, "当前方案为空，请选择候选或取消", "确认编排方案"); return; }
        var pending = PendingLeaves().Count;
        if (pending > 0)
        {
            MessageBox.Show(this, $"仍有 {pending} 个叶子未补齐验收标准，请一键补默认验收后再确认。", "确认编排方案");
            return;
        }
        FlushCrossNode();
        plan.Status = "approved";
        GAIRR.AgentHost.PlanStore.Save(projectRoot, plan);
        DialogResult = true;
    }

    static string Trunc(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

/// <summary>手机端决策回写收件箱（desktop 侧消费件，与 Server 的 GuiSync.AppendDecision 对写）：
/// 轮询读取 decisions.inbox.json，命中"会话+planId"条目即原子取走（读-过滤-写回），
/// 返回是否命中及 allow 决策。与 Server 写端共享文件级锁语义：同一决策条目只被消费一次。</summary>
static class MobileDecisionStore
{
    /// <summary>取走命中 sessionId 的危险决策条目（planId 为空串的条目即危险确认类，与方案决策互不串）；
    /// 命中返回 true 并 out 决策值。文件不存在/损坏/IO 抖动 → 返回 false，调用方下一拍重试。</summary>
    public static bool TakeDanger(string dataDir, string sessionId, out bool allow)
    {
        allow = false;
        try
        {
            var path = Path.Combine(dataDir, "decisions.inbox.json");
            if (!File.Exists(path)) return false;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            var text = reader.ReadToEnd();
            JsonArray? arr = null;
            try { arr = JsonNode.Parse(text) as JsonArray; } catch { }
            if (arr == null || arr.Count == 0) return false;

            // 找到第一条命中条目（sessionId 相同且 planId 为空 = 危险确认决策）
            JsonNode? hit = null;
            int index = -1;
            for (int i = 0; i < arr.Count; i++)
            {
                var item = arr[i] as JsonObject;
                if (item == null) continue;
                var sid = (item["SessionId"] ?? item["sessionId"])?.GetValue<string>() ?? "";
                var pid = (item["PlanId"] ?? item["planId"])?.GetValue<string>() ?? "";
                if (sid == sessionId && string.IsNullOrEmpty(pid)) { hit = item; index = i; break; }
            }
            if (hit == null) return false;
            allow = (hit["Allow"] ?? hit["allow"])?.GetValue<bool>() ?? false;

            // 原子取走该条目：删除命中项后整文件写回
            arr.RemoveAt(index);
            fs.SetLength(0);
            fs.Seek(0, SeekOrigin.Begin);
            var writer = new StreamWriter(fs);
            if (arr.Count > 0) writer.Write(arr.ToJsonString()); else { writer.Flush(); fs.SetLength(0); }
            writer.Flush();
            return true;
        }
        catch { return false; }
    }

    /// <summary>清除指定会话的全部危险决策残留（新危险卡挂起前调用：防上一张卡的陈旧决策被误消费）。</summary>
    public static void PurgeDanger(string dataDir, string sessionId)
    {
        try
        {
            var path = Path.Combine(dataDir, "decisions.inbox.json");
            if (!File.Exists(path)) return;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            JsonArray? arr = null;
            try { arr = JsonNode.Parse(reader.ReadToEnd()) as JsonArray; } catch { }
            if (arr == null || arr.Count == 0) return;
            var kept = new JsonArray();
            foreach (var node in arr)
            {
                var item = node as JsonObject;
                if (item == null) continue;
                var sid = (item["SessionId"] ?? item["sessionId"])?.GetValue<string>() ?? "";
                var pid = (item["PlanId"] ?? item["planId"])?.GetValue<string>() ?? "";
                if (sid == sessionId && string.IsNullOrEmpty(pid)) continue;   // 丢弃该会话的危险残留
                kept.Add(node?.DeepClone());
            }
            fs.SetLength(0);
            fs.Seek(0, SeekOrigin.Begin);
            var writer = new StreamWriter(fs);
            if (kept.Count > 0) writer.Write(kept.ToJsonString()); else { writer.Flush(); fs.SetLength(0); }
            writer.Flush();
        }
        catch { /* 清除失败不阻塞：消费端按最先一条命中，残留风险可接受 */ }
    }

    /// <summary>取走命中 sessionId+planId 的决策条目（若多条取最先一条）；命中返回 true 并 out 决策值。
    /// 文件不存在/损坏/IO 抖动 → 返回 false，调用方下一拍重试。路径缺失时回退 Paths.DataDir。</summary>
    public static bool Take(string dataDir, string sessionId, string planId, out bool allow)
    {
        allow = false;
        try
        {
            var path = Path.Combine(dataDir, "decisions.inbox.json");
            if (!File.Exists(path)) return false;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            var text = reader.ReadToEnd();
            JsonArray? arr = null;
            try { arr = JsonNode.Parse(text) as JsonArray; } catch { }
            if (arr == null || arr.Count == 0) return false;

            // 找到第一条命中条目（按 sessionId + planId）
            JsonNode? hit = null;
            int index = -1;
            for (int i = 0; i < arr.Count; i++)
            {
                var item = arr[i] as JsonObject;
                if (item == null) continue;
                var sid = (item["SessionId"] ?? item["sessionId"])?.GetValue<string>() ?? "";
                var pid = (item["PlanId"] ?? item["planId"])?.GetValue<string>() ?? "";
                if (sid == sessionId && pid == planId) { hit = item; index = i; break; }
            }
            if (hit == null) return false;
            allow = (hit["Allow"] ?? hit["allow"])?.GetValue<bool>() ?? false;

            // 原子取走该条目：删除命中项后整文件写回（FileShare.ReadWrite 下 Server 追加互斥由写回原子性保证）
            arr.RemoveAt(index);
            fs.SetLength(0);
            fs.Seek(0, SeekOrigin.Begin);
            var writer = new StreamWriter(fs);
            if (arr.Count > 0) writer.Write(arr.ToJsonString()); else { writer.Flush(); fs.SetLength(0); }
            writer.Flush();
            return true;
        }
        catch { return false; }
    }
}