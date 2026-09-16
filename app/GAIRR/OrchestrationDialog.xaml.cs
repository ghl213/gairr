// OrchestrationDialog.xaml.cs
// 编排任务创建对话框：收集标题 + L0 目标 + 规划模式，后台调 PlanNegotiator 生成待确认计划；
// 成功时 Plan 属性非空且 DialogResult=true，由调用方接力打开计划确认对话框。
using System;
using System.Threading;
using System.Windows;
using GAIRR.AgentHost;

namespace GAIRR;

public partial class OrchestrationDialog : Window
{
    /// <summary>规划产出的待确认计划（成功时非空）。</summary>
    public PlanDto? Plan { get; private set; }

    /// <summary>标题预填（从架构树创建时为节点名）；窗口加载时写入输入框。</summary>
    public string InitialTitle { get; set; } = "";
    /// <summary>L0 目标预填（从架构树创建时为节点名+路径说明）；窗口加载时写入输入框。</summary>
    public string InitialGoal { get; set; } = "";
    /// <summary>来源架构节点路径（ArchNode.NodePath）；规划成功后写入 Plan.ArchRef。</summary>
    public string? ArchRef { get; set; }

    readonly CancellationTokenSource cts = new();
    string projectRoot = "";

    public OrchestrationDialog(string projectRoot)
    {
        InitializeComponent();
        this.projectRoot = projectRoot;
        Loaded += (_, _) =>
        {
            if (InitialTitle.Length > 0) tbTitle.Text = InitialTitle;
            if (InitialGoal.Length > 0) tbGoal.Text = InitialGoal;
        };
        rbStandard.Checked += (_, _) =>
            tbModeHint.Text = "标准：单个当前模型生成方案（快、成本低）";
        rbNegotiation.Checked += (_, _) =>
            tbModeHint.Text = "多模型商讨：config.ini [Planning] Models 配置的多个模型并行生成候选，评估 Agent 融合推荐（更稳健、更耗时）";
    }

    async void OnStart(object sender, RoutedEventArgs e)
    {
        var goal = tbGoal.Text.Trim();
        if (goal.Length == 0) { MessageBox.Show(this, "请填写 L0 目标", "新建编排任务"); return; }
        if (projectRoot.Length == 0) { MessageBox.Show(this, "未选择项目，无法规划", "新建编排任务"); return; }

        // 进入规划中状态：输入锁定，进度区展开
        tbTitle.IsEnabled = tbGoal.IsEnabled = rbStandard.IsEnabled = rbNegotiation.IsEnabled = false;
        btnStart.IsEnabled = false;
        btnStart.Content = "规划中…";
        btnCancel.Content = "中止";
        tbPhase.Text = "规划进行中";
        progressScroll.Visibility = Visibility.Visible;

        var title = tbTitle.Text.Trim();
        var negotiation = rbNegotiation.IsChecked == true;
        try
        {
            // 后台执行：log 回调来自工作线程，统一 Dispatcher 回 UI 追加进度行
            Plan = await System.Threading.Tasks.Task.Run(() => PlanNegotiator.NegotiateAsync(
                projectRoot, title, goal, negotiation,
                log: line => Dispatcher.BeginInvoke(new Action(() => AppendLog(line))),
                ct: cts.Token));
            if (Plan == null)
            {
                AppendLog("[plan] 规划失败，请查看上方日志或更换目标后重试");
                RestoreInputs();
                return;
            }
            Plan.ArchRef = ArchRef;   // 来源架构节点路径回写（非架构树创建时为 null）
            DialogResult = true;   // 成功：交由调用方打开确认对话框
        }
        catch (Exception ex)
        {
            AppendLog($"[plan] 规划异常：{ex.Message}");
            RestoreInputs();
        }
    }

    void OnCancel(object sender, RoutedEventArgs e)
    {
        if (btnStart.IsEnabled) { DialogResult = false; return; }   // 未开始规划：直接关窗
        cts.Cancel();                                                 // 规划中：请求中止（等待循环退出）
        tbPhase.Text = "正在中止…";
    }

    /// <summary>追加进度行并滚动到底部。</summary>
    void AppendLog(string line)
    {
        tbProgress.Text += line + "\n";
        progressScroll.ScrollToEnd();
    }

    /// <summary>规划失败后恢复可编辑状态。</summary>
    void RestoreInputs()
    {
        tbTitle.IsEnabled = tbGoal.IsEnabled = rbStandard.IsEnabled = rbNegotiation.IsEnabled = true;
        btnStart.IsEnabled = true;
        btnStart.Content = "开始规划";
        btnCancel.Content = "取消";
        tbPhase.Text = "规划失败，可调整后重试";
    }
}
