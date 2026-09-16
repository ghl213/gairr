/// <summary>主窗口：项目/会话/消息布局与渲染，内置工具注册与调用，会话历史与任务计划展示，侧栏页签与“重建地图”入口。</summary>
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using GAIRR.Core;
using GAIRR.Core.Lsp;

namespace GAIRR;

public partial class MainWindow : Window
{
    readonly ObservableCollection<ChatMessage> messages = new();
    // ===== 会话历史分段加载（长会话打开优化）=====
    // 打开历史会话时 messages 只装配"最近若干回合"（填满一屏即停），更早的消息暂存于此，向上滚动到顶时分批装配。
    // 注意：pendingOlderMsgs + messages 合起来才是当前会话的完整消息（SaveCurrentSession / 标题回退 / 一屏续装按二者合并读取）。
    readonly List<ChatMessage> pendingOlderMsgs = new();
    bool loadingOlderMsgs;         // 正在装配更早回合：防滚动事件重复触发
    int msgViewGeneration;         // 消息区装配世代：ResetMsgView 自增，用于中止在途的分段装配回调（防串会话）
    readonly ObservableCollection<ProjectItem> projects = new();
    readonly ObservableCollection<TaskItem> taskHistory = new();
    readonly ObservableCollection<TaskItem> autoTasks = new();     // 自动任务列表
    readonly ObservableCollection<TaskRunItem> taskRuns = new();  // 自动任务执行历史
    TaskItem? selectedTask;      // 当前选中的自动任务：非空时任务执行历史列表只显示该任务的记录
    TaskItem? lastClickTask;     // 上一次单击的任务（双击检测：500ms 内重复单击同一任务视为双击）
    DateTime lastClickTime;      // 上一次单击时间
    readonly ObservableCollection<SessionItem> sessionHistory = new();
    bool sessionLoaded;            // 会话历史已从磁盘加载完毕；启动阶段 OnProjectChanged 据此跳过保存，防空内存覆盖磁盘
    readonly ObservableCollection<SkillDef> builtinSkills = new();
    readonly ObservableCollection<PluginDef> plugins = new();
    readonly ObservableCollection<ToolItemDef> builtinTools = new();
    // 市场视图三栏条目集合：已安装 / 热度排行 / 在线搜索结果（条目含安装状态机）
    readonly ObservableCollection<MarketEntryDef> marketInstalledEntries = new();   // 已安装栏
    readonly ObservableCollection<MarketEntryDef> marketTrendingEntries = new();    // 热度排行栏
    readonly ObservableCollection<MarketEntryDef> marketResultEntries = new();      // 在线搜索结果栏
    System.ComponentModel.ICollectionView? marketView;                               // 搜索结果栏过滤视图（本地过滤目录册关键词）
    string lastSearchQuery = "";                                                     // 上次搜索词（同词不重发，防限流）
    AutoTaskScheduler? taskScheduler;
    TreeNode? selectedNode;
    TreeNode? treeMenuNode;   // 目录树右键时记录的节点（供右键菜单判定与操作）

    /// <summary>自动维护文档清单缓存（正斜杠 rel）：目录树加载时给在列 .md 节点加 ⟳ 标识</summary>
    HashSet<string> autoDocSet = new(StringComparer.OrdinalIgnoreCase);

    /* 目录树类型语义配色：中饱和档（灰调与纯色之间，明显但不刺眼）：目录青/代码紫/可执行珊瑚红/配置暖黄/文档绿/其它灰；
    ⟳ 自动维护文档用暖黄盖过绿色 */
    const string TreeDirColor = "#4FB1C3";
    const string TreeCodeColor = "#8789E0";
    const string TreeExeColor = "#D5667C";
    const string TreeCfgColor = "#D3A259";
    const string TreeDocColor = "#69C192";
    const string TreeDimColor = "#858CA0";
    /* git 变更节点色：分组标题琥珀 / 自动提交亮绿 / 手动提交灰蓝；变更文件行按状态：新增绿/修改暖黄/删除红/重命名青（其余中性） */
    const string TreeGitGroupColor = "#E8B96A";
    const string TreeGitAutoColor = "#8FD8B0";
    const string TreeGitManColor = "#B9C0D4";
    const string TreeGitAddColor = "#6FCF97";
    const string TreeGitModColor = "#E8C07A";
    const string TreeGitDelColor = "#E07070";
    const string TreeGitRenColor = "#4FB1C3";
    const string TreeGitFileColor = "#C9CFDC";

    /* git diff 行流配色：红绿行用半透明底（深色主题下柔和醒目），文本同色系亮色；@@ 区块与头/元信息仅前景区分 */
    static readonly Brush DiffAddFg = new SolidColorBrush(Color.FromRgb(0xA8, 0xE6, 0xC0));
    static readonly Brush DiffAddBg = new SolidColorBrush(Color.FromArgb(0x33, 0x22, 0x6B, 0x45));
    static readonly Brush DiffDelFg = new SolidColorBrush(Color.FromRgb(0xF2, 0xA0, 0xA0));
    static readonly Brush DiffDelBg = new SolidColorBrush(Color.FromArgb(0x33, 0x6B, 0x22, 0x22));
    static readonly Brush DiffHunkFg = new SolidColorBrush(Color.FromRgb(0xF0, 0xC6, 0x74));
    static readonly Brush DiffHeadFg = new SolidColorBrush(Color.FromRgb(0x8C, 0xA4, 0xD8));
    static readonly Brush DiffCtxFg = new SolidColorBrush(Color.FromRgb(0xC2, 0xC9, 0xD6));

    static readonly HashSet<string> TreeCodeExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".xaml", ".csproj", ".sln", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".py",
        ".go", ".java", ".kt", ".dart", ".rb", ".php", ".vue", ".c", ".h", ".cpp",
        ".sh", ".ps1", ".sql", ".html", ".css",
    };
    /// <summary>可执行类按"能双击运行"划界：.exe/.msi/.bat/.cmd/.lnk；.dll/.lib/.so/.pdb 是库/符号文件归其它（暗灰）；
    /// .sh/.ps1 在 Windows 双击默认不执行，归源码</summary>
    static readonly HashSet<string> TreeExeExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".bat", ".cmd", ".lnk",
    };
    static readonly HashSet<string> TreeCfgExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ini", ".json", ".xml", ".config", ".yml", ".yaml", ".toml", ".props", ".targets", ".editorconfig",
    };

    /// <summary>侧栏宽度≥360时显示时间列</summary>
    public static readonly DependencyProperty ShowTimeVisProperty =
        DependencyProperty.Register(nameof(ShowTimeVis), typeof(Visibility), typeof(MainWindow),
            new PropertyMetadata(Visibility.Visible));
    public Visibility ShowTimeVis
    {
        get => (Visibility)GetValue(ShowTimeVisProperty);
        set => SetValue(ShowTimeVisProperty, value);
    }

    void OnSideSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ShowTimeVis = e.NewSize.Width >= 360 ? Visibility.Visible : Visibility.Collapsed;
        if (gitDiffOpen) ReposGitFloat();   // 左栏尺寸变化（拖分隔条/窗口缩放）：提交详情浮层贴树位置跟随
    }

    /// <summary>会话历史行的鼠标指针：两会话并行下恒为手型（前台主任务运行中也可自由切换/新建会话，切走后任务后台照跑）</summary>
    public static readonly DependencyProperty SessionCursorProperty =
        DependencyProperty.Register(nameof(SessionCursor), typeof(Cursor), typeof(MainWindow),
            new PropertyMetadata(Cursors.Hand));
    public Cursor SessionCursor
    {
        get => (Cursor)GetValue(SessionCursorProperty);
        set => SetValue(SessionCursorProperty, value);
    }

    // ── 会话切换锁：发送消息后 → 首个回复 token 到达前，SSE 流尚未绑定本会话，此窗口内切会话会把流带进
    //    其它会话（串台）。锁定期所有切换入口（会话列表点击/新会话/计划会话/切项目）统一置灰禁用
    //    （XAML 绑 SwitchEnabled/SwitchLockTip）+ 代码守卫拦截；解锁点：首个 token（ThinkingLive/
    //    StreamDelta）、点停止（OnStop）、任务结束（Finished/Failed 兜底）、60 秒超时（防锁死）。 ──
    string? _switchLockKey;            // 被锁会话 Key（null=未锁）；设/释见 LockSessionSwitch/UnlockSessionSwitch
    System.Windows.Threading.DispatcherTimer? _switchLockTimer; // 60 秒超时自动解锁定时器

    /// <summary>会话切换入口可用性（false=锁定中，驱动会话行/新会话/计划会话按钮 IsEnabled 与置灰）</summary>
    public static readonly DependencyProperty SwitchEnabledProperty =
        DependencyProperty.Register(nameof(SwitchEnabled), typeof(bool), typeof(MainWindow), new PropertyMetadata(true));
    public bool SwitchEnabled
    {
        get => (bool)GetValue(SwitchEnabledProperty);
        set => SetValue(SwitchEnabledProperty, value);
    }

    /// <summary>会话切换入口 hover 提示文案（未锁=null=无提示；锁定中="AI 正在响应…"）</summary>
    public static readonly DependencyProperty SwitchLockTipProperty =
        DependencyProperty.Register(nameof(SwitchLockTip), typeof(string), typeof(MainWindow), new PropertyMetadata((string?)null));
    public string? SwitchLockTip
    {
        get => (string?)GetValue(SwitchLockTipProperty);
        set => SetValue(SwitchLockTipProperty, value);
    }

    /// <summary>锁定会话切换（OnSend 发送后调用）：全部切换入口置灰禁用；60 秒内首个 token 未到达则自动解锁（防锁死）。</summary>
    void LockSessionSwitch(string sessionKey)
    {
        _switchLockKey = sessionKey;
        SwitchEnabled = false;
        SwitchLockTip = "AI 正在响应，首个回复生成前不能切换会话";
        _switchLockTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _switchLockTimer.Tick -= OnSwitchLockTimeout;
        _switchLockTimer.Tick += OnSwitchLockTimeout;
        _switchLockTimer.Stop();
        _switchLockTimer.Start();
    }

    /// <summary>解除会话切换锁：会话 Key 与被锁会话一致才释（null=强制释放，供超时/停止用）。幂等，可重复调用。</summary>
    void UnlockSessionSwitch(string? sessionKey = null)
    {
        if (_switchLockKey == null) return;
        if (sessionKey != null && sessionKey != _switchLockKey) return;
        _switchLockKey = null;
        _switchLockTimer?.Stop();
        SwitchEnabled = true;
        SwitchLockTip = null;
    }

    /// <summary>60 秒超时：首个 token 仍未到达（网络异常/模型卡住）→ 自动解锁，避免入口永久锁死</summary>
    void OnSwitchLockTimeout(object? sender, EventArgs e)
    {
        _switchLockTimer?.Stop();
        UnlockSessionSwitch();
    }

    readonly AppConfig cfg = new();
    readonly ToolRegistry registry = new();
    readonly ChangeJournal journal;
    // ─── 本叶子（两会话并行改造）：删除唯一全局 AgentLoop 与全局忙闲门 ForegroundBusyAnywhere——
    //     每会话一个 SessionRunner（含独立 AgentLoop/上下文/Bus），惰性创建于该会话首个任务启动（EnsureRunner），
    //     挂 TaskRecord.Runner 常驻至会话删除；两会话前台主对话各自跑在自有 Runner 上互不中断。 ───
    readonly SkillLoader skillLoader;
    // ─── P1② 任务运行态已按会话收拢为 per-session TaskRecord（见 runTasks/ClearRec/TaskRecord）：
    //     原实例级任务字段改为"当前前台会话记录"的属性视图——读写自动落所属会话 rec（无记录时建账/清空忽略），
    //     两会话并行各自独立；单会话行为与改造前一致（零回归）。原字段删除即收拢。
    //     代理统一锚 ActiveRec（= 事件归属记录 _evRec ?? 当前会话记录）：验收7 前台主任务切项目放行后在后台跑，
    //     其事件按 SessionKey 找回归属记录继续驱动，进度直写记录不上当前视口；非事件路径 _evRec 恒为 null，行为不变。 ───
    /// <summary>drain 循环正在处理的事件归属记录（按事件 SessionKey 从 runTasks 解析）；非事件路径恒为 null</summary>
    TaskRecord? _evRec;
    TaskRecord? ActiveRec => _evRec ?? CurRec;
    /// <summary>事件/记录是否处于"后台运行"：归属会话不是当前视口会话（验收7 切项目放行后的前台主任务）。
    /// 后台运行的事件只更新归属记录、收口写回该会话，不滚动/不污染被查看的会话视口。</summary>
    bool EventBg => _evRec != null && !ReferenceEquals(_evRec, TaskOf(currentSession));
    /// <summary>UI 事件汇合总线：会话外宿主事件（Lsp/DocStatus/LeafEventBridge 兜底等）经此进入 drain 循环，
    /// 与各会话 Runner 总线同源消费；线程安全（后台线程 Post、UI drain）。原全局 loop.Bus 已随唯一全局 loop 删除。</summary>
    readonly UiEventBus uiBus = new();
    /// <summary>会话执行流镜像（手机实时观看数据源）：uiTimer 泵尾把归属会话事件转发本机 Server；构造时创建</summary>
    LiveMirror? liveMirror;

    /* ---------- 本叶子：会话运行器（SessionRunner）路由 ----------
       两会话前台主对话可并行：每会话一个 SessionRunner（含 AgentLoop/Bus/上下文），惰性创建于该会话首个任务
       启动（EnsureRunner），挂 TaskRecord.Runner 常驻至会话删除；切会话/切项目不再重置任何上下文，后台收口也不再
       回切——上下文随 Runner 归各会话（各自 history/Key/标题/阶段），互不串扰。原 ForegroundBusyAnywhere 全局门与
       唯一全局 AgentLoop 已删除。UI 配置类读写（模型/思考/模式）以 cfg 为权威源，经 SwitchModelForAll 等应用到
       全部已建 Runner；未建 Runner 的会话在其首任务 EnsureRunner 时按 cfg 对齐。 ---------- */

    /// <summary>取指定会话的运行器（无记录或未启动过任务则 null）</summary>
    GAIRR.AgentHost.SessionRunner? RunnerOf(SessionItem? s) => s != null && runTasks.TryGetValue(s.Id, out var r) ? r.Runner : null;

    /// <summary>当前视口会话的运行器（发送/停止等 UI 路径用；未启动过任务则 null）</summary>
    GAIRR.AgentHost.SessionRunner? CurRunner => currentSession != null && runTasks.TryGetValue(currentSession.Id, out var r) ? r.Runner : null;

    /// <summary>取事件归属会话的运行器（危险/计划审批等按事件键回路由；无归属回退当前会话）</summary>
    GAIRR.AgentHost.SessionRunner? RunnerForEventKey(string? key) =>
        key != null && runTasks.TryGetValue(key, out var r) ? r.Runner : CurRunner;

    /// <summary>全部已注册会话运行器（drain 事件轮询源 / 配置类操作目标：各会话 Runner 总线 + uiBus 汇合）</summary>
    IEnumerable<GAIRR.AgentHost.SessionRunner> AllRunners()
    {
        var seen = new HashSet<GAIRR.AgentHost.SessionRunner>();
        foreach (var r in runTasks.Values)
            if (r.Runner != null && seen.Add(r.Runner))
                yield return r.Runner;
    }

    /// <summary>编排阶段同步到指定会话的 Runner（若已建）：SwitchOrch 驱动框架阶段（守卫/工具集注入/收口判定用）。
    /// 无 Runner 时跳过——首任务 EnsureRunner 建 Runner 时按会话阶段一次性对齐；后台收口不干扰其它会话 Runner。</summary>
    void SyncOrch(SessionItem s, string? phase)
    {
        var r = RunnerOf(s);
        if (r != null) r.Loop.SwitchOrch(phase);
    }

    /// <summary>视口置位：按"当前视口会话"刷新全部已建会话 Runner 的 Loop.InViewport。
    /// AgentLoop 挂起钉钉路由以它区分通道：视口会话由 UI 弹卡就近处理不推钉钉，切走/非视口会话挂起走"后台短静默钉钉"
    /// 并在人不在时兜底（见 AgentLoop.RunHangNotifierAsync ②③）。所有改变视口会话归属的位置（打开/切换/新建会话、
    /// 删除当前会话、切项目、进入回放视口等）都必须调用本方法；currentSession=null（回放/删除后/清理态）→ 全部非视口。</summary>
    void SyncViewportFlags()
    {
        var vid = currentSession?.Id;
        foreach (var kv in runTasks)
        {
            var r = kv.Value.Runner;
            if (r == null) continue;
            var inVp = vid != null && kv.Key == vid;
            if (r.Loop.InViewport != inVp) r.Loop.InViewport = inVp;
        }
        RefreshChangedCard();   // 视口会话变更 = 左栏“本会话改动”卡片随动（所有切换点都过本方法，无需逐处补调）
    }

    /// <summary>模型切换应用到全部已建会话 Runner（每会话 AgentLoop client 立即更新；无 Runner 的会话在其首个任务
    /// EnsureRunner 建时按 cfg.LastProvider/LastModel 对齐）。旧全局 loop.SwitchModelFull 已被取代。</summary>
    void SwitchModelForAll(string provider, string model)
    {
        foreach (var r in AllRunners()) r.Loop.SwitchModelFull(provider, model);
    }

    /// <summary>把 UI 当前 Agent 模式同步给全部已建会话 Runner（新 Runner 在 EnsureRunner 创建时按其 prompt 对齐）。</summary>
    void SwitchUiAgentMode(string mode)
    {
        foreach (var r in AllRunners()) r.Loop.SwitchAgentMode(mode);
    }

    /// <summary>取/建当前会话的会话运行器（本叶子核心）：每会话一个 SessionRunner，首次任务启动时创建并按该会话
    /// 历史/标题/标识一次性装载；随后每次任务经 StartAsync 驱动 RunAsync。两会话前台任务并行跑在各自 Runner 上。</summary>
    GAIRR.AgentHost.SessionRunner EnsureRunner(SessionItem? s)
    {
        if (s == null) throw new InvalidOperationException("任务启动必须存在会话上下文");
        var rec = EnsureRec(s);
        if (rec.Runner != null) return rec.Runner;
        // 提示词按会话类型选择（编排=产树主持人；普通=UI 当前角色模式仓库里所选模式的提示词文件，缺省按 AgentMode 回落）
        // 自动匹配态（下拉恒为 __auto__）无法从下拉解析到命中项：直接按 _autoMode 命中模式装配提示词，与手动选中该模式等效
        var autoPrompt = _autoMode != null ? GAIRR.AgentHost.RoleModeStore.ResolvePromptFile(_autoMode) : null;
        var promptFile = s.IsOrchestration ? "plan-orchestration.md"
            : !string.IsNullOrEmpty(autoPrompt) ? autoPrompt
            : SelectedModePromptFile()
              ?? (SystemCfg.AgentMode == "deep" ? "agent-deep.md" : "agent-agile.md");
        var runner = new GAIRR.AgentHost.SessionRunner(s.Id, cfg, registry, journal, skillLoader, promptFile);
        // 首建一次性装载该会话历史/标题/标识（等效旧架构"打开会话重建大模型上下文"；此后上下文随本 Runner 自持，
        // 两会话各带各的 history，互不串台；重启恢复 RestoreLoopContext 亦经此装载）
        try
        {
            runner.Loop.ResetHistory();
            runner.Loop.LoadHistory(s.Messages
                .Where(m => (m.Kind == "User" || m.Kind == "Agent") && !string.IsNullOrWhiteSpace(m.Text))
                .Select(m => (m.Kind == "User" ? "user" : "assistant", m.Text!)));
        }
        catch { }   // 无会话历史（新会话首轮）等异常时为空装载，忽略（不影响任务正确性）
        var titleSet = !string.IsNullOrWhiteSpace(s.Title) && s.Title != "新会话";
        runner.Loop.SetSessionTitle(titleSet ? s.Title : "");
        runner.Loop.SetSessionKey(s.Id);
        // 编排会话：Runner 创建即带阶段（工具集注入/收口判定依赖 OrchMode 非空；收集会话首轮即注入编排工具集）
        if (s.IsOrchestration) runner.Loop.SwitchOrch(s.OrchPhase ?? "collecting");
        // UI 当前模型对齐（与旧全局 loop 在任务启动前的即时状态等效）；切换侧 cfg.LastProvider/LastModel 已同步写入
        try
        {
            var opt = cfg.ModelOptions().FirstOrDefault(o =>
                o.HasKey &&
                string.Equals(o.Provider, CurProvider, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(o.ModelId, CurModel, StringComparison.OrdinalIgnoreCase))
                ?? cfg.ModelOptions().FirstOrDefault(o =>
                    o.HasKey &&
                    string.Equals(o.Provider, CurProvider, StringComparison.OrdinalIgnoreCase))
                ?? cfg.ModelOptions().FirstOrDefault(o => o.HasKey);
            if (opt != null) runner.Loop.SwitchModelFull(opt.Provider, opt.ModelId);
        }
        catch { }   // 模型选项缺失等容错：沿用 Runner 默认模型
        rec.Runner = runner;
        SyncViewportFlags();   // 新 Runner 视口置位：仅当前会话的 Runner 为视口，切走/后台会话挂起才走短静默补发
        return runner;
    }

    /// <summary>UI 当前 Provider/Model 直读配置（每选即写 cfg.LastProvider/LastModel，为权威源；无 Runner 时亦可用，
    /// 首启未显式选过模型时回退 cfg.Provider 与首个可用模型，与旧全局 loop 初值语义一致）</summary>
    string CurProvider => string.IsNullOrWhiteSpace(cfg.LastProvider) ? cfg.Provider : cfg.LastProvider;
    string CurModel => cfg.ModelOptions().FirstOrDefault(o =>
            o.HasKey &&
            string.Equals(o.Provider, CurProvider, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(o.ModelId, cfg.LastModel, StringComparison.OrdinalIgnoreCase))?.ModelId
        ?? cfg.ModelOptions().FirstOrDefault(o =>
            o.HasKey &&
            string.Equals(o.Provider, CurProvider, StringComparison.OrdinalIgnoreCase))?.ModelId
        ?? cfg.ModelOptions().FirstOrDefault(o => o.HasKey)?.ModelId
        ?? "";

    /// <summary>任务工具卡（工具 Guid→卡片）：存所属会话 TaskRecord.CardMap，收口时清空</summary>
    Dictionary<Guid, ToolCall> cardMap => ActiveRec?.CardMap ?? _emptyGuidCardMap;
    static readonly Dictionary<Guid, ToolCall> _emptyGuidCardMap = new();   // 无任务上下文时的空兜底（时序边界不抛）
    /// <summary>步骤号 -> 步骤生命周期卡片（开始/完成复用）：存所属会话 TaskRecord.StepCardMap</summary>
    Dictionary<int, ToolCall> stepCardMap => ActiveRec?.StepCardMap ?? _emptyStepCardMap;
    static readonly Dictionary<int, ToolCall> _emptyStepCardMap = new();
    string lastTask = "";
    bool _pendingForceWebFullContext;   // 发送菜单「发送完整上下文」待发送标志：置位后由 OnSend 消费（强制网页新会话+整段投完整上下文）
    int qCardSeq;                   // 编排澄清问题卡片序号：会话内自增（主持人每轮新问题顺延），UI 题面前缀“n.”显示
    readonly HashSet<ChatMessage> qSubMsgs = new();   // 已订阅 CardsChanged 的带卡消息（会话级澄清进度聚合；消息离开列表自动反订阅）

    CancellationTokenSource? cts   // 前台主对话/产树任务取消源：随所属会话 TaskRecord
    {
        get => ActiveRec?.Cts;
        set { var r = ActiveRec ?? (currentSession != null ? EnsureRec(currentSession) : null); if (r != null) r.Cts = value; }
    }
    ChatMessage? workMsg          // 当前任务的工作消息（工具卡+最终文本）：存所属会话 TaskRecord.WorkMsg
    {
        get => ActiveRec?.WorkMsg;
        set { var r = ActiveRec ?? (currentSession != null ? EnsureRec(currentSession) : null); if (r != null) r.WorkMsg = value; }
    }
    TodoItem? currentTodo         // 当前任务待办清单卡（模型经 UpdateTodo 创建，计划气泡内置顶）：随所属会话
    {
        get => ActiveRec?.CurrentTodo;
        set { var r = ActiveRec ?? (currentSession != null ? EnsureRec(currentSession) : null); if (r != null) r.CurrentTodo = value; }
    }
    DangerConfirmItem? dangerCard // 当前挂起的危险确认卡片（决策/超时后隐藏）：挂所属会话 TaskRecord.DangerCard
    {
        get => ActiveRec?.DangerCard;
        set { var r = ActiveRec ?? (currentSession != null ? EnsureRec(currentSession) : null); if (r != null) r.DangerCard = value; }
    }
    System.Windows.Threading.DispatcherTimer? dangerTicker // 危险卡片倒计时定时器：每秒减1，到0停止；随所属会话
    {
        get => ActiveRec?.DangerTicker;
        set { var r = ActiveRec ?? (currentSession != null ? EnsureRec(currentSession) : null); if (r != null) r.DangerTicker = value; }
    }

    // 当前挂起危险确认的决策路由（建卡时按归属捕获：编排叶子卡→叶子会话，其余→主 loop）：
    // 点哪张卡解哪把锁，杜绝两会话各有挂起时点了 A 的卡解了 B 的锁（验收10 不互顶/不互相覆盖）
    Action<bool>? dangerResolve
    {
        get => ActiveRec?.DangerResolve;
        set { var r = ActiveRec ?? (currentSession != null ? EnsureRec(currentSession) : null); if (r != null) r.DangerResolve = value; }
    }

    // 手机端危险决策轮询（观看模式联动）：危险卡挂起期间每 2s 读 decisions.inbox.json，
    // 命中"会话+空 planId"条目即按建卡时捕获的路由裁决（同刻全局仅一张危险卡挂起，单实例即可）
    System.Windows.Threading.DispatcherTimer? dangerMobilePollTicker;
    string? dangerMobilePollSid;           // 挂起卡归属会话 id（= 手机端所用会话 id，收件箱按它匹配）
    Action<bool>? dangerMobilePollResolve; // 建卡时捕获的决策路由（与点卡上按钮同一把锁）
    TaskRecord? dangerMobilePollRec;       // 挂起卡归属记录（收卡按归属收，防用户正查看别的会话时收不到当前会话的卡）

    bool taskActive               // 任务执行中：顶部信息条强制显示待办进度；随所属会话 TaskRecord.TaskActive
    {
        get => ActiveRec?.TaskActive ?? false;
        set { var r = ActiveRec ?? (currentSession != null ? EnsureRec(currentSession) : null); if (r != null) r.TaskActive = value; }
    }
    string? pendingSessionTitle;   // 首条消息的模型摘要标题：后续无参保存会话时沿用，避免重复建条
    bool sessionTitleSet;          // 会话标题是否已确定；确定后不再被用户首句话覆盖
    SessionItem? currentSession;   // 当前正在使用的会话项（新会话/打开会话时设置），保存时优先更新此项
    string? openRunId;   // 任务历史回放容器 Id：回放期间作为快照目录键，切回会话/新对话时置空
    bool openingSession;           // OpenSession 执行中防重入标志（连点/回调交叉时忽略后续打开请求，finally 复位）
    bool codeOpen;                 // 右侧代码查看器是否打开（占用右栏第三页签“代码查看”）
    ToolCall? codeCard;            // 当前正在查看的读文件工具卡（“查看代码”打开时记录，关闭/切页后置空）
    string? codeKey;               // 代码打开时所属容器键（openRunId ?? 会话 Id）：容器切换后自动关闭防串台
    int codeSeq;                   // 代码查看器打开序号：后台源码加载完成时序号不匹配即丢弃（防快速连点乱序）
    bool ctxOpen;                  // 右侧“请求上下文”查看页是否打开（与代码查看互斥占用右栏查看页）
    string? ctxKey;                // ctx 打开时所属容器键（openRunId ?? 会话 Id）：容器切换后自动关闭防串台
    int ctxSeq;                    // ctx 打开序号：后台读盘完成时序号不匹配即丢弃（防快速连点乱序）
    bool ctxRespPage;              // ctx 页当前子页：false=请求上下文（打开默认，与旧行为一致）/ true=回应内容
    string? ctxReqText, ctxRespText;   // 两子页已格式化文本缓存：打开时一次读盘，来回切页零 IO；null=未载入或读失败
    string? ctxReqFile, ctxRespFile;   // 两子页快照文件全路径；ctxRespFile=null 表示本轮无配对回应快照（显示占位，不反推兜底）

    /* ctx 子页来源徽标配色：请求=琥珀（沿用改动前）、回应=青绿（一眼区分两页）、无回应快照=灰 */
    static readonly Brush CtxReqTagBg = new SolidColorBrush(Color.FromArgb(0x26, 0xF5, 0xB9, 0x55));
    static readonly Brush CtxReqTagFg = new SolidColorBrush(Color.FromRgb(0xF5, 0xB9, 0x55));
    static readonly Brush CtxRespTagBg = new SolidColorBrush(Color.FromArgb(0x26, 0x4E, 0xC9, 0xB0));
    static readonly Brush CtxRespTagFg = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0));
    static readonly Brush CtxMissTagBg = new SolidColorBrush(Color.FromArgb(0x26, 0x9A, 0xA0, 0xB4));
    static readonly Brush CtxMissTagFg = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xB4));
    bool codeEditing;              // 代码查看页编辑模式：true=codeEdit 可写（保存/放弃按钮可见；运行中/部分载入文件不可进入）
    bool codeDirty;                // 编辑后是否有未保存修改：TextChanged 实时比对“当前文本≠载入基线”
    bool codeTruncated;            // 编辑器只含文件部分内容（查看窗口化 / 磁盘超限截断）：整文件覆盖式保存会丢窗口外行，禁止编辑
    int codeFileLine0;             // 编辑器首行对应的文件行号-1（窗口偏移）：编辑器第 n 行 = 文件第 codeFileLine0+n 行；0=从文件第 1 行载入
    int codeFileTotal;             // 当前文件真实总行数（窗口/文案计算用；0=未载入）
    string? codeLoadedText;        // 编辑器内文本基线（\n 归一）：最近一次载入/保存/放弃后的内容，编辑脏判定基准
    string? codeDiskText;          // 载入时探测的磁盘当前文本（\n 归一；null=磁盘无此文件）：保存冲突比对与“⤓ 磁盘当前”切换目标
    bool codeMdPreview;            // 当前文件为 .md 且处于只读查看态：codeMdView 格式化预览代替 codeEdit 源码视图（进入编辑自动回落源码）
    string? codeMdSourceText;      // md 预览文档所基于的源文本缓存：与 codeEdit.Text 不一致时重建 FlowDocument
    RangeLineNumberMargin? codeLineMargin;   // codeEdit 行号边距自绘实例（首装时替换内置 LineNumberMargin，跨文件查看复用；读取行段内数字/底色突显）
    const int CodeWinPad = 500;    // 查看页窗口化：编辑器只显示“读取段 ±CodeWinPad”行（上下不足取到文件头/尾），替代旧“整文件+前 8000 行截断”

    /* ── 改动对比模式（左栏“会话文件变动”行点击打开）：codeDiffOn=true 时 codeEdit 装载“改前基线 vs 当前文本”的
       统一 diff 行流，复用 git 提交详情同款着色与双列行号（DiffBgRenderer / DiffLineNumberMargin / DiffRowColorizer），
       编辑与 md 预览停用；点工具栏“◧ 改动对比 ⇄ ☰ 全文”在两视图间来回切换（diff 文本已缓存，切换不重算）。 ── */
    bool codeDiffOn;                        // 当前是否处于改动对比视图（仅在 codeDiffText 非空时生效）
    ChangedFileVm? codeDiffVm;              // 触发本次对比的改动行（null=非改动入口，隐藏对比按钮）
    string? codeDiffText;                   // 已算好的统一 diff 文本（两视图来回切换不重算）
    string codeDiffNote = "";               // 头部副文本：基线来源 + 增删行统计
    string codeSrcLabel = "磁盘当前文件";    // 头部来源徽标原文（快照 / 磁盘当前文件 / Git HEAD）：对比视图会把它覆盖成“改动对比”，切回全文视图据此复原
    List<GitDiffRow>? codeDiffRows;         // 当前 diff 行模型（与 codeEdit 文档行一一对应）
    DiffLineNumberMargin? codeDiffMargin;   // codeEdit 的 diff 双列行号边距（与 codeLineMargin 互换占用行号区）
    DiffBgRenderer? codeDiffBg;             // codeEdit 的 diff 行底色渲染器（+ 淡绿 / - 淡红）
    DiffRowColorizer? codeDiffColorizer;    // codeEdit 的 diff 文本前景着色器（+ 绿 / - 红 / @@ 琥珀）

    // ──── git 变更跟踪（左侧“项目跟踪”分组树 + 贴左树浮层“提交详情”查看器）────
    TreeNode? gitGroupNode;        // “项目跟踪（Git 变更）”分组节点：RenderTree 创建；提交完成后若组已展开则冒顶插入新 commit 行
    bool gitDirty;                 // git 分组待重拉：产生新提交但未就地插入时置位，组展开时整拉最新记录
    string? gitLastKey            // 最近一次任务的提交归属标识（会话/运行短 id）：任务启动点记录，收尾自动提交时消费；随所属会话 TaskRecord
    {
        get => ActiveRec?.GitLastKey;
        set { var r = ActiveRec ?? (currentSession != null ? EnsureRec(currentSession) : null); if (r != null) r.GitLastKey = value; }
    }
    bool gitDiffOpen;              // “提交详情”浮层是否打开（git 变更树双击文件行触发，贴左树浮出；与右栏编排面板解耦、与代码查看互斥）
    TreeNode? gitDiffNode;         // 正在查看的变更文件树节点：关闭后置空
    string? gitDiffKey;            // diff 打开时所属容器键（openRunId ?? 会话 Id）：容器切换后自动关闭防串台
    int gitDiffSeq;                // diff 查看器打开序号：后台加载完成时序号不匹配即丢弃（防快速连点乱序）
    List<GitDiffRow>? gitDiffRows;      // 当前浮层内 diff 行模型：与 gitDiffEdit 文档行一一对应（行底/双行号/整段复制同源）
    DiffLineNumberMargin? gitDiffMargin;   // gitDiffEdit 行号边距自绘实例：双列（旧|新）文件行号（首开时替换内置）
    DiffBgRenderer? gitDiffBg;            // gitDiffEdit 行底色渲染器（+ 淡绿 / - 淡红）：装载时 SetRows 更新
    DiffRowColorizer? gitDiffColorizer;   // gitDiffEdit 文本前景着色器（+ 绿 / - 红 / @@ 琥珀…整行着色）：随 SetGitDiffRows 更新
    readonly HashSet<TreeNode> gitBusyNodes = new();   // 正在后台加载的 git 分组/commit 节点（防重入：重复展开/刷新不叠加请求）

    int accTokens                 // 当前任务累计 token：随所属会话 TaskRecord.AccTokens
    {
        get => ActiveRec?.AccTokens ?? 0;
        set { var r = ActiveRec ?? (currentSession != null ? EnsureRec(currentSession) : null); if (r != null) r.AccTokens = value; }
    }
    DateTime taskStart            // 当前任务开始时刻：steps 信息条累计用时基准；随所属会话 TaskRecord.TaskStart
    {
        get => ActiveRec?.TaskStart ?? default;
        set { var r = ActiveRec ?? (currentSession != null ? EnsureRec(currentSession) : null); if (r != null) r.TaskStart = value; }
    }
    bool updatingChatTopInfo;      // UpdateChatTopInfo 重入保护：避免 SizeChanged/显隐切换死循环
    /// <summary>流式打字机节流：积压的增量文本（定时器每次只上屏一小段，避免大块 chunk 突然弹出）；随所属会话</summary>
    string? pendingStream
    {
        get => ActiveRec?.PendingStream;
        set { var r = ActiveRec ?? (currentSession != null ? EnsureRec(currentSession) : null); if (r != null) r.PendingStream = value; }
    }
    /// <summary>打字机节流定时器：30ms 一次，每次上屏 StreamTickChars 个字符；随所属会话</summary>
    System.Windows.Threading.DispatcherTimer? streamTicker
    {
        get => ActiveRec?.StreamTicker;
        set { var r = ActiveRec ?? (currentSession != null ? EnsureRec(currentSession) : null); if (r != null) r.StreamTicker = value; }
    }
    const int StreamTickChars = 10;
    DateTime liveThinkStart         // 深度思考直播卡创建时刻：ThinkingLive 刷新时作为卡片右上角计时显示基准；随所属会话
    {
        get => ActiveRec?.LiveThinkStart ?? default;
        set { var r = ActiveRec ?? (currentSession != null ? EnsureRec(currentSession) : null); if (r != null) r.LiveThinkStart = value; }
    }

    /// <summary>当前登录用户（演示登录注入；未经登录门禁创建时为 null）。</summary>
    public UserInfo? CurrentUser { get; private set; }

    /// <summary>登录门禁入口：携带演示用户信息创建主窗口。</summary>
    public MainWindow(UserInfo user) : this()
    {
        CurrentUser = user;
    }

    /// <summary>登录成功后注入演示用户（用于“先显示主窗口、再模态弹登录窗”的启动模式，由 App 调用）。</summary>
    public void SetCurrentUser(UserInfo user) => CurrentUser = user;

    public MainWindow()
    {
        InitializeComponent();
        editorSearchPanel = ICSharpCode.AvalonEdit.Search.SearchPanel.Install(editor);   // 编辑窗安装查找栏：Ctrl+F 唤起、F3/Shift+F3 逐项定位
        IdleGate.Enable();   // 启用"3 分钟无操作才发钉钉"判定（UI 模式）
        SizeChanged += (_, _) => ReposGitFloat();   // 窗口缩放：提交详情浮层宽高/贴树位置随附刷新（内部按 gitDiffOpen 自滤）
        // 用户活跃打点：键盘按键 + 鼠标点击 + 鼠标移动 均视为"人还在"（隧道阶段捕获，子控件处理前即记录）
        PreviewKeyDown += (_, _) => IdleGate.MarkActive();
        PreviewMouseDown += (_, _) => IdleGate.MarkActive();
        PreviewMouseMove += (_, _) => IdleGate.MarkActive();
        // 模型下拉弹层滚轮兜底：弹层 Popup 是独立 hwnd/可视树，跨弹层的悬停解析与事件路由并不可靠，
        // 在窗口根部以 handledEventsToo 拦截一切滚轮，鼠标几何位置落在弹层内时手动滚动（见 OnDropGlobalWheel）
        AddHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnDropGlobalWheel), true);
        KeyDown += OnWindowF5;   // F5：刷新左侧项目目录树（整树重建，走与初始化相同的 RenderTree）
        msgList.ItemsSource = messages;
        // 会话区消息增删（发送上屏 / 新会话清空 / 切会话装载）→ 同步刷新发送钮「发送/继续」文案与置灰态；此处顺手定初始态
        messages.CollectionChanged += (_, _) => Ui(RefreshSendBtnState);
        RefreshSendBtnState();
        // 市场视图三栏：已安装/热度排行直接绑集合；搜索结果栏走 ICollectionView 本地过滤
        marketInstalledList.ItemsSource = marketInstalledEntries;
        marketTrendingList.ItemsSource = marketTrendingEntries;
        marketView = CollectionViewSource.GetDefaultView(marketResultEntries);
        marketView.Filter = o => o is MarketEntryDef m && MarketMatch(m);
        marketResultList.ItemsSource = marketView;
        projSel.ItemsSource = projects;
        // 会话历史树由 RefreshSessionList 重建 ItemsSource（分组为"普通会话/计划会话"两个顶层节点），
        // 折叠状态记录在 _collapsedSessionGroups，重建后由 SessionGroup.IsOpen 恢复，无选中态漂移问题。
        footYear.Text = "概尔工栈 GAIRR.COM";

        SystemCfg.Init();   // system.ini：内置工具参数/规则/提示词覆盖，须先于工具注册
        WebChannelHost.Install(cfg);   // 网页模型通道：按 [WebChannels] 注册后端，让模型下拉出现"网页-xxx"项
        // 网页窗口显隐变化（自动弹出登录 / 用户点窗口内"隐藏到后台"）→ 回刷标题栏「显示/隐藏」文案
        WebChannelHost.ChannelsChanged += () => Dispatcher.BeginInvoke(new Action(RefreshWebWinBtn));
        Phase1Tools.RegisterAll(registry, cfg);
        journal = new ChangeJournal(cfg);
        Phase2Tools.RegisterAll(registry, cfg, journal);
        registry.Register("Map", ToolRegistry.Fn(
            "Map",
            SystemCfg.Desc("Map",
                "获取项目目录结构与各文件功能摘要（自动提取文件头注释和顶层声明，.gairr/map.json 缓存可手工补充语义描述）。任务开始先调用它了解项目全貌，再精准定位文件，避免盲目扫目录/逐个读文件。"),
            new System.Text.Json.Nodes.JsonObject
            {
                ["path"] = ToolRegistry.Str("聚焦的子目录或文件路径（相对项目根），可为空（空=整个项目）"),
                ["refresh"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "boolean", ["description"] = "true=强制重建所有自动摘要（默认 false 增量更新）" },
            },
            new System.Text.Json.Nodes.JsonArray()),
            (a, ct) => Task.FromResult(ProjectMap.Build(cfg,
                a["path"]?.GetValue<string>(),
                a["refresh"]?.GetValue<bool>() ?? false)));
        registry.Register("MapAuto", ToolRegistry.Fn(
            "MapAuto",
            SystemCfg.Desc("MapAuto",
                "运行项目地图自动化：增量更新项目地图、刷新文件级调用图谱、为缺注释文件/方法调大模型补说明（写 .gairr/notes.json 不改源码）、首次生成架构/功能文档（结构变化时按旧文增量刷新）。默认后台执行，进度见状态栏与 agent.log。"),
            new System.Text.Json.Nodes.JsonObject
            {
                ["wait"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "boolean", ["description"] = "true=等待执行完成并返回全程进度（默认 false 立即返回，后台执行）" },
            },
            new System.Text.Json.Nodes.JsonArray()),
            async (a, ct) =>
            {
                if (!a["wait"]?.GetValue<bool>() ?? false)
                {
                    // 后台火并忘：同项目 10 秒内重复调度自动跳过（与启动/切项目自动触发共用节流）
                    ProjectMapAuto.Schedule(cfg, OnDocStatus);
                    return "项目地图自动化已后台启动（同项目 10 秒内重复调用会跳过；进度见状态栏 / agent.log）";
                }
                var logs = new List<string>();
                await ProjectMapAuto.RunAsync(cfg, p =>
                {
                    lock (logs) logs.Add($"{p.Percent}% · {p.Message}");
                    OnDocStatus(p);
                });
                return logs.Count > 0 ? string.Join("\n", logs) : "项目地图自动化已运行（无变更项）";
            });
        registry.Register("MapTrace", ToolRegistry.Fn(
            "MapTrace",
            SystemCfg.Desc("MapTrace",
                "按符号名检索代码：输入类名/方法名/文件名（支持部分匹配），返回定义位置（文件:行号）与引用文件列表。也支持中文查询：按文件头注释/方法注释里的中文标签定位（如 q=备份、危险命令）。找代码定义优先用 MapTrace，比 Grep 语义更准、比 Map 更细。"),
            new System.Text.Json.Nodes.JsonObject
            {
                ["q"] = ToolRegistry.Str("符号名：类名/方法名/文件名（支持部分匹配，如 AgentLoop、BuildAsync）"),
                ["def"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "boolean", ["description"] = "true=只返回定义位置（默认 false 同时列出引用文件）" },
                ["max"] = ToolRegistry.Int("最多返回符号数，默认 15"),
            },
            new System.Text.Json.Nodes.JsonArray { "q" }),
            (a, ct) => Task.FromResult(SymbolIndex.Run(cfg,
                a["q"]?.GetValue<string>() ?? "",
                a["max"]?.GetValue<int>() ?? 15,
                a["def"]?.GetValue<bool>() ?? false)));
        registry.Register("MapSlice", ToolRegistry.Fn(
            "MapSlice",
            SystemCfg.Desc("MapSlice",
                "按 MapTrace 定位结果直接提取方法/类完整代码块（自动含前置注释与块边界）：spec 形如 \"Core/Tools.cs:165\"、\"Tools.cs:165-185\"、\"Tools.cs:Read\"（符号名），多段逗号分隔（≤5）。定位到行号后用它一次拿到源码，比 Read 猜范围更快更省。chain=1 时自动附带该符号被引用处的上下文（所属方法+引用行±3行），跨文件修改/调用关系时用它一次拿全，免去多次 MapTrace+Read 往返。"),
            new System.Text.Json.Nodes.JsonObject
            {
                ["spec"] = ToolRegistry.Str("切片定位：文件:起始行 或 文件:起-止 或 文件:符号名，多段逗号分隔，如 Core/Tools.cs:165, WriteTools.cs:Grep"),
                ["chain"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "boolean", ["description"] = "true=附带主块符号的被引用处上下文（跨文件调用链），默认 false" },
            },
            new System.Text.Json.Nodes.JsonArray { "spec" }),
            (a, ct) => Task.FromResult(SliceExtractor.Run(cfg, a["spec"]?.GetValue<string>() ?? "", a["chain"]?.GetValue<bool>() ?? false)));
        registry.Register("FindRefs", ToolRegistry.Fn(
            "FindRefs",
            SystemCfg.Desc("FindRefs",
                "编译器级引用检索：查询符号（方法/类/字段）的全部真实引用位置（含定义位置），返回 文件:行:上下文。支持 .cs（Roslyn）/ .java（jdtls）/ .vue/.ts/.js（volar），按文件扩展名自动选语言后端，首次调用某语言会自动下载运行环境（状态栏可见，约 1~5 分钟）。能区分重载/同名符号、只算真实调用，比 MapTrace 文本搜索精确。file 与 line 先用 MapTrace 定位获得。改动公共方法/类之前先查它确认影响范围，避免改坏调用方。"),
            new System.Text.Json.Nodes.JsonObject
            {
                ["file"] = ToolRegistry.Str("符号所在文件（相对项目根，如 Core/AgentLoop.cs）"),
                ["line"] = ToolRegistry.Int("符号所在行号（1 起，MapTrace 返回的 :行号）"),
                ["symbol"] = ToolRegistry.Str("符号名（类名/方法名/字段名），用于精确定位列，可为空"),
                ["max"] = ToolRegistry.Int("最多返回引用数，默认 20"),
            },
            new System.Text.Json.Nodes.JsonArray { "file", "line" }),
            async (a, ct) => await (LspManager.Instance?.FindReferencesAsync(
                a["file"]?.GetValue<string>() ?? "",
                a["line"]?.GetValue<int>() ?? 1,
                a["symbol"]?.GetValue<string>() ?? "",
                a["max"]?.GetValue<int>() ?? 20,
                ct) ?? Task.FromResult("错误：语言服务器未初始化（重启应用后重试）")));
        skillLoader = new SkillLoader(cfg);
        skillLoader.Reloaded += RefreshSkills;   // 技能热加载：切 UI 线程重绑技能面板
        skillLoader.Watch();                     // 监视 skills/*.md 变化，2 秒防抖重扫（保留启用状态）
        registry.Register("LoadSkill", ToolRegistry.Fn(
            "LoadSkill",
            SystemCfg.Desc("LoadSkill",
                "加载指定技能的完整指令。当任务与 system prompt 技能清单中某技能描述匹配、或用户用 /技能名 强制触发时，先调本工具取全文再按技能步骤执行。"),
            new System.Text.Json.Nodes.JsonObject { ["name"] = ToolRegistry.Str("技能名称") },
            new System.Text.Json.Nodes.JsonArray { "name" }),
            (a, ct) => Task.FromResult(skillLoader.LoadFull(a["name"]?.GetValue<string>() ?? "")));
        registry.Register("SmartSearch", ToolRegistry.Fn(
            "SmartSearch",
            SystemCfg.Desc("SmartSearch",
                "智能语义检索：支持中文查询（如 q=数据持久化），按四层体系匹配（历史关联积累→代码注释中文标签→中英规则词典→文件摘要），返回相关符号定义与文件位置，中文找不到时可换更短关键词/英文符号名再试。"),
            new System.Text.Json.Nodes.JsonObject
            {
                ["q"] = ToolRegistry.Str("查询词，支持中文（如：断点续行、持久化、注册工具）"),
                ["max"] = ToolRegistry.Int("最多返回结果数，默认 5"),
            },
            new System.Text.Json.Nodes.JsonArray { "q" }),
            (a, ct) => Task.FromResult(SmartSearch.Run(cfg,
                a["q"]?.GetValue<string>() ?? "",
                a["max"]?.GetValue<int>() ?? 5)));
        var loadedPlugins = PluginLoader.Load(registry, cfg);
        foreach (var p in loadedPlugins) plugins.Add(p);
        // 工具开关启动加载：system.ini [Tools] Disabled 黑名单（UI 切换写回，重启/CLI/Server 同享）
        foreach (var name in GAIRR.Core.SwitchStore.ToolsDisabled())
            registry.SetEnabled(name, false);
        PluginLoader.Watch(registry, cfg);        // 插件热加载：plugins 目录 ini 变化自动重扫重注册，当轮即可调用
        PluginLoader.Reloaded += RefreshPlugins;  // 热加载完成刷新技能页签的插件面板
        MarketTools.RegisterAll(registry, cfg);   // 市场工具：MarketList/MarketInstall/MarketUninstall（模型自助接入外部市场）
        GitHubTools.RegisterAll(registry, cfg);   // GitHub 技能源：GhSearch/GhInstallSkill/GhUninstall（搜索安装他人仓库的技能）
        // 本叶子：不再创建唯一全局 AgentLoop——会话运行器（SessionRunner）于该会话首个任务启动时由 EnsureRunner 创建
        // （每会话一个，含独立 AgentLoop/Bus/上下文），两会话前台主对话可并行；语言服务器（FindRefs）事件改经 uiBus 汇入。
        LspManager.Init(cfg, uiBus);   // 语言服务器（FindRefs 后端）：事件注入 UI 汇合总线，须在任务运行前完成
        LspManager.Instance?.Probe();     // 启动后环境自检（后台线程）：状态栏直接提示 FindRefs 可用性，不必等首次调用
        
        // 初始化内置工具列表（按常见调用顺序/重要度排列）
        var toolOrder = new[] { "SmartSearch", "Map", "MapTrace", "MapSlice", "FindRefs", "MapAuto", "Read", "ListDir", "Grep", "Glob", "Bash", "DbQuery", "Write", "Edit", "LoadSkill", "MarketList", "MarketInstall", "MarketUninstall", "GhSearch", "GhInstallSkill", "GhUninstall" };
        foreach (var name in toolOrder)
        {
            if (!registry.IsRegistered(name)) continue;
            var desc = name switch
            {
                "SmartSearch" => "按中文语义检索代码",
                "Map" => "查看项目结构地图",
                "MapTrace" => "按符号名检索代码",
                "FindRefs" => "编译器级引用检索",
                "MapAuto" => "运行项目地图自动化",
                "Read" => "读取指定文件内容",
                "ListDir" => "列出目录文件和子目录",
                "Grep" => "正则搜索文件内容",
                "Glob" => "通配符查找文件",
                "Bash" => "执行 cmd 命令并返回输出",
                "DbQuery" => "执行 SQLite 数据库查询",
                "Write" => "创建或覆盖写入文件",
                "Edit" => "对已有文件做局部替换",
                "LoadSkill" => "加载指定技能完整指令",
                "MapSlice" => "按定位提取代码块",
                "MarketList" => "查询市场技能插件目录",
                "MarketInstall" => "从市场安装技能或插件",
                "MarketUninstall" => "卸载已安装技能或插件",
                "GhSearch" => "搜索 GitHub 公开仓库",
                "GhInstallSkill" => "从 GitHub 安装技能",
                "GhUninstall" => "卸载 GitHub 安装的技能",
                "DingTalk" => "发送钉钉群消息",
                "NewsDigest" => "生成科技热点中文摘要（不自动发钉钉）",
                "ImageOcr" => "识别图片中的文字（OCR）",
                _ => registry.GetDescription(name)   // 兜底：取注册时登记的用途描述，不再显示"自定义工具"
            };
            builtinTools.Add(new ToolItemDef(name, desc) { Enabled = registry.IsEnabled(name) });   // 反映 [Tools] Disabled 持久化状态
        }
        // 补充未在预设列表中的其他工具（UpdateTodo 为模型内部协作工具，不进侧栏开关；
        // 插件已注册进同一注册表，但归属"声明式插件"分组单独展示，此处排除避免两个列表重复）
        var pluginToolNames = loadedPlugins.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in registry.ToolNames)
        {
            if (toolOrder.Contains(name)) continue;
            if (name == "UpdateTodo") continue;
            if (pluginToolNames.Contains(name)) continue;
            builtinTools.Add(new ToolItemDef(name, registry.GetDescription(name)) { Enabled = registry.IsEnabled(name) });
        }
        
        // 初始化内置技能（可启用/禁用）
        builtinSkills.Add(new SkillDef("任务级备份", "每次任务后自动备份文件", "") { Enabled = true });
        builtinSkills.Add(new SkillDef("钉钉通知", "任务完成后发送钉钉通知", "") { Enabled = true });
        builtinSkills.Add(new SkillDef("危险命令拦截", "拦截 rm/format 等危险命令", "") { Enabled = true });
        
        WireLoop();
        liveMirror = new LiveMirror();
        RestoreLastModel();
        UpdateStatus();
        RefreshModelCombo();
        UpdateThinkingToggle();
        UpdateGpuMonitor();   // 本地模型且配了 MonitorUrl 时启动标题栏 GPU 三色柱轮询
        FillSkillPanel();

        // 初始化项目列表（从 config.ini [Projects] 读取；无配置时为空列表并引导添加）
        LoadProjects();

        // Git 全量管理：后台自动探测/安装 git 并确保 ProjectRoot 已 init（不阻塞启动）
        _ = Task.Run(async () =>
        {
            try { await GitMgr.EnsureInstalledAsync(cfg); await GitMgr.EnsureRepoAsync(cfg); } catch { }
        });

 UpdateStatus();
 // 目录树与左上 logo 渲染跟随启动选中的当前项目，而非固定首项
 if (projects.Count > 0) RenderTree((projSel.SelectedItem as ProjectItem)?.Path ?? projects[0].Path);

        // 启动兜底：清理上次异常退出残留的任务运行标记（busy_*.json），避免 GUI 已不在跑却永久拦住手机端接入
        ClearAllBusy();

        // 手机端服务开关状态记忆：上次用户拨动为开 → 启动自动拉起服务并点亮开关；为关/无记录 → 保持关。
        // 关窗自动停服不改写用户意图；真实运行状态仍在每次操作末尾回读同步。
        RestoreMobileSvcState();
        InitMobileTray();   // 手机端服务系统托盘常驻右下角：启停服务/打开主窗口/显示会话数；服务运行中关窗驻留托盘不退出

        // 加载会话历史列表（供左侧栏显示），但启动时不自动恢复上次会话内容
        LoadSessionHistory();

        // 初始化自动任务列表与调度器（独立的 AgentLoop 实例，避免打断用户当前会话）
        taskList.ItemsSource = autoTasks;
        taskRunList.ItemsSource = taskRuns;
        taskScheduler = new AutoTaskScheduler(cfg, autoTasks, RunAutoTaskAsync);
        taskScheduler.Start();

        // 加载当前项目的任务执行历史（未选中任务时显示全部）
        RefreshTaskRuns();

        // 项目地图自动化：启动即后台检查/生成地图、调用图谱、补注释、架构文档（不阻塞启动）；
        // 受内置工具 MapAuto 开关控制（禁用后连自动触发一起停）
        if (registry.IsEnabled("MapAuto")) ProjectMapAuto.Schedule(cfg, OnDocStatus);

        // 同步“重建地图/停止更新”按钮状态：自动/手动重建开始后切橙色停止按钮，结束后恢复
        var mapStateTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        mapStateTimer.Tick += (_, _) => SyncMapButtonState();
        mapStateTimer.Start();

        // 对话区顶部动态信息栏：仅在用户滚动停止后刷新"看不到的头像"信息。
        // 注意：不要在 SizeChanged / CollectionChanged 里直接调用 UpdateChatTopInfo，
        // 因为该函数会显隐 chatTopBar，进而改变 msgScroll 高度再次触发 SizeChanged，形成死循环。
        // 因此用防抖定时器：滚动事件只重置定时器，停止 150ms 后再真正刷新顶栏。
        var topInfoDebounceTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(150),
            IsEnabled = false
        };
        topInfoDebounceTimer.Tick += (_, _) =>
        {
            topInfoDebounceTimer.Stop();
            UpdateChatTopInfo();
        };
        msgScroll.ScrollChanged += (s, e) =>
        {
            // 仅由用户滚动或真实内容变化触发；过滤掉因显隐顶栏自身导致的 ScrollChanged 风暴
            if (e.VerticalChange != 0 || e.ViewportHeightChange != 0 || e.ExtentHeightChange != 0)
            {
                topInfoDebounceTimer.Stop();
                topInfoDebounceTimer.Start();
            }
            // 分段加载：用户向上滚动到顶且仍有更早历史 → 浮现提示并续装上一批回合。
            // 只在"用户向上滚"（VerticalChange<0）且已抵顶时触发，故打开会话/程序化滚到底都不会误触发
            if (e.VerticalChange < 0 && msgScroll.VerticalOffset <= 4 && pendingOlderMsgs.Count > 0)
                LoadOlderMsgsOnTopScroll();
        };

        if (messages.Count == 0)
        {
            AddMessage(new ChatMessage
            {
                Kind = MsgKind.Agent,
                Who = "概尔 Agent",
                Text = cfg.Exists
                    ? $"你好，我是运行在你电脑上的概尔 Agent。\n已接入真实模型（{ProviderLabel()}），把任务交给我就行。"
                    : "⚠ 未找到 config.ini：请把配置文件放到 exe 同目录后重启。",
            });
        }

        // 预填充工作模式下拉框：启动时即加载工具目录 flows/*.json 模板，避免只显示“自主模式”
        SelectedFlowSteps();
    }

    /// <summary>
    /// 标题栏 + 最小化/最大化/关闭按钮走系统深色模式；
    /// 挂接消息钩子，拦截 WM_GETMINMAXINFO 以约束最大化范围到显示器工作区。
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int dark = 1;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));

        // 安全挂接 WndProc 钩子：仅处理 WM_GETMINMAXINFO，任何异常都捕获，避免消息循环崩溃。
        // 注意：Windows 24H2 上 WPF 内部 HwndSubclass 子类化可能抛 DllNotFoundException（SetWindowLongPtr），
        // 该异常发生在 WPF 内部路径、SafeWndProc 内层拦截不到，必须在挂接处兜底：失败则放弃钩子，
        // 最大化约束退化为系统默认（能启动优先于约束）。
        try
        {
            var source = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
            source?.AddHook(SafeWndProc);
        }
        catch (Exception ex)
        {
            try
            {
                System.IO.File.AppendAllText(
                    GAIRR.Core.Paths.AgentLog,
                    $"[{DateTime.Now:O}] AddHook 失败（24H2 已知问题，放弃最大化约束钩子）: {ex.GetType().Name}: {ex.Message}\n");
            }
            catch { }
        }
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /* ---- 最大化约束到显示器工作区（底边停在任务栏上方） ---- */

    const int WM_GETMINMAXINFO = 0x0024;

    /// <summary>
    /// 带异常保护的 WndProc 转发器：将 WM_GETMINMAXINFO 交给实际处理逻辑，
    /// 任何异常都被吞掉并记录，防止 Win32 消息循环被异常中断导致 UI 卡死或崩溃。
    /// </summary>
    IntPtr SafeWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        try
        {
            return WndProc(hwnd, msg, wParam, lParam, ref handled);
        }
        catch (Exception ex)
        {
            try
            {
                System.IO.File.AppendAllText(
                    GAIRR.Core.Paths.AgentLog,
                    $"[{DateTime.Now:O}] SafeWndProc error: {ex}\n");
            }
            catch
            {
                // 日志也写失败时，静默忽略，优先保证消息循环不中断。
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// 拦截 WM_GETMINMAXINFO，将最大化窗口尺寸限制到当前显示器工作区（扣除任务栏）。
    /// </summary>
    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            var mon = MonitorFromWindow(hwnd, 2 /*MONITOR_DEFAULTTONEAREST*/);
            if (mon != IntPtr.Zero)
            {
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(mon, ref mi))
                {
                    var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                    mmi.ptMaxPosition.x = mi.rcWork.left - mi.rcMonitor.left;
                    mmi.ptMaxPosition.y = mi.rcWork.top - mi.rcMonitor.top;
                    mmi.ptMaxSize.x = mi.rcWork.right - mi.rcWork.left;
                    mmi.ptMaxSize.y = mi.rcWork.bottom - mi.rcWork.top;
                    // 约束最小尺寸 = 启动默认大小（XAML MinWidth/MinHeight 为 DIP，按 DPI 转物理像素）
                    if (MinWidth > 0 && MinHeight > 0)
                    {
                        var dpi = VisualTreeHelper.GetDpi(this);
                        mmi.ptMinTrackSize.x = (int)(MinWidth * dpi.DpiScaleX + 0.5);
                        mmi.ptMinTrackSize.y = (int)(MinHeight * dpi.DpiScaleY + 0.5);
                    }
                    Marshal.StructureToPtr(mmi, lParam, false);
                    handled = true;
                }
            }
        }
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    /* ================= 自绘标题栏：拖拽 / 窗口按钮 / 最大化修正 ================= */

    // —— 最大化状态下拖动标题栏：按下先记录，移动超过阈值才还原窗口，避免单击误还原 ——
    Point _maxPressWinPos;    // 按下点相对窗口坐标
    Point _maxPressScreenPos; // 按下点屏幕坐标
    bool _maxDragArmed;       // 是否已挂接移动/抬起监听

    /// <summary>标题栏拖动移动窗口，双击切换最大化；最大化时拖动自动还原并跟随（同系统行为）</summary>
    void OnTitleDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { ToggleMax(); return; }
        if (e.ButtonState != MouseButtonState.Pressed) return;

        if (WindowState == WindowState.Maximized)
        {
            _maxPressWinPos = e.GetPosition(this);
            _maxPressScreenPos = PointToScreen(_maxPressWinPos);
            if (!_maxDragArmed)
            {
                _maxDragArmed = true;
                PreviewMouseMove += OnMaxTitleDragMove;
                PreviewMouseUp += OnMaxTitleDragUp;
            }
            return;
        }
        DragMove();
    }

    /// <summary>最大化窗口标题栏按下后的移动处理：超过拖动阈值即还原窗口并继续拖拽</summary>
    void OnMaxTitleDragMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) { OnMaxTitleDragUp(sender, null); return; }
        Point cur = e.GetPosition(this);
        if (Math.Abs(cur.X - _maxPressWinPos.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(cur.Y - _maxPressWinPos.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        OnMaxTitleDragUp(sender, null);  // 先摘除监听，进入系统拖拽循环
        WindowState = WindowState.Normal;
        Left = Math.Max(_maxPressScreenPos.X - _maxPressWinPos.X, SystemParameters.WorkArea.Left);
        Top = Math.Max(_maxPressScreenPos.Y - _maxPressWinPos.Y, SystemParameters.WorkArea.Top);
        DragMove();
    }

    /// <summary>摘除最大化拖动的临时监听（松开左键或移动处理完成后调用）</summary>
    void OnMaxTitleDragUp(object sender, MouseButtonEventArgs? e)
    {
        if (!_maxDragArmed) return;
        _maxDragArmed = false;
        PreviewMouseMove -= OnMaxTitleDragMove;
        PreviewMouseUp -= OnMaxTitleDragUp;
    }

    void OnWinMin(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    /// <summary>网页模型通道窗口的显示/隐藏（标题栏按钮，仅当前模型为「网页-xxx」时可见）：
    /// 隐藏态点一下 = 显示并前置；可见态点一下 = 隐藏到后台（网页继续运行，Agent 照常调用）。</summary>
    void OnToggleWebWin(object sender, RoutedEventArgs e)
    {
        var provider = CurProvider;
        if (!Core.WebChannelSpec.IsWebProvider(provider)) return;
        WebChannelHost.ToggleVisible(provider);
        RefreshWebWinBtn();
    }

    /// <summary>刷新标题栏「显示/隐藏」按钮：非网页模型时整颗按钮隐藏；
    /// 是网页模型时按窗口当前显隐给出"隐藏/显示"文案（窗口被自动弹出、或被窗口内按钮隐藏，都会经事件回到这里）。</summary>
    void RefreshWebWinBtn()
    {
        if (btnWebWin == null) return;
        if (!Core.WebChannelSpec.IsWebProvider(CurProvider))
        {
            btnWebWin.Visibility = Visibility.Collapsed;
            return;
        }
        btnWebWin.Visibility = Visibility.Visible;
        // 开关样式（同「思考」按钮）：Tag=on 时渐变高亮表示窗口已显示
 btnWebWin.Tag = WebChannelHost.IsVisible(CurProvider) ? "on" : null;
    }

    void OnWinMax(object sender, RoutedEventArgs e) => ToggleMax();

    void OnWinClose(object sender, RoutedEventArgs e) => Close();

    void ToggleMax() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>最大化时切换标题栏按钮图标</summary>
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (btnMax == null) return;
        btnMax.Content = WindowState == WindowState.Maximized ? "❐" : "□";
    }

    /* ================= Agent 事件 → UI（全部切回 Dispatcher） ================= */

    void Ui(Action a) => Dispatcher.BeginInvoke(a);

    void WireLoop()
    {
        // UI 状态消费者：每 100ms 检查一次状态信号
        var uiTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        // 打字机节流定时器随会话建账（EnsureRec）创建：StreamDelta 只入队积压文本，
        // 定时器每 30ms 上屏一小段，保证逐字平滑显示；构造期无会话，不能在此经属性初始化
        uiTimer.Tick += (s, e) =>
        {
            // 本叶子：事件源=全部会话 Runner 总线（各会话 Agent 事件，带归属 Key）+ uiBus（会话外宿主事件）汇合同源消费。
            // 每轮每 Bus drain 量受控（验收2）：单轮单 Bus 至多取 MaxEvPerBus 条，余量留在各自队列由下一轮 100ms 节拍继续
            // 消费——突发大批量不在一轮内全量串行处理，打字机/滚动/输入不被拖卡（正常负载远低于该上限，仅兜底突发）。
            const int MaxEvPerBus = 256;   // 单 Bus 单轮处理上限（兜底突发；上游已有单次 StreamDelta/思考节流控量）
            var drainedEvs = AllRunners().SelectMany(r => r.Bus.Drain(MaxEvPerBus)).Concat(uiBus.Drain(MaxEvPerBus)).ToList();
            foreach (var ev in drainedEvs)
            {
                // 事件归属记录解析（验收7 后台运行）：按事件 SessionKey 从 runTasks 找回归属任务记录。
                // 代理属性统一锚 ActiveRec（= _evRec ?? 当前会话记录）：后台运行事件直写归属记录，不依赖 currentSession。
                // 归属会话解析：仅任务事件（Key 在 runTasks 里）才解析；无 Key 全局事件保持 _evRec=null 走原前台路径。
                _evRec = ev.SessionKey != null && runTasks.TryGetValue(ev.SessionKey, out var evRec) ? evRec : null;
                // 会话归属过滤：仅处理无 Key 的全局型事件与属于当前会话的事件
                // （编排叶子事件经 LeafEventBridge 转发时 Key 已被覆盖为所属会话，
                //   其它会话的事件不串入当前 UI 处理流程）
                // SecurityAlert 豁免：危险确认须全局可达——后台叶子挂起等待决策，切到其它会话时也要能建卡：
                // 卡片写事件归属会话的确认卡（不做模态打断），切回该会话经 RestoreBgWorkMsg 接回可见可裁决
                if (ev.Type != UiEventType.SecurityAlert
                    && ev.SessionKey != null && ev.SessionKey.Length > 0
                    && ev.SessionKey != (currentSession?.Id ?? ""))
                {
                    // 验收7 后台运行：归属会话的前台主任务（cts）切项目/切会话被放行后继续在后台跑，其事件不再丢弃——
                    // 直接走下方同一 switch（代理已锚归属记录 _evRec），UI 副作用由各处理点的 EventBg 守卫拦截
                    // （不滚动/不顶栏/不落被查看会话视口）；Finished/Failed 收口按归属会话写回其消息历史。
                    // 编排叶子等无记录归属的事件（_evRec=null）仍丢弃：由 LeafEventBridge 的目标会话自行消化。
                    if (_evRec == null) { _evRec = null; continue; }
                }
                switch (ev.Type)
                {
                    case UiEventType.WaitingModel:
                        // 模型调用前：等待提示条显示“正在思考”（清掉工具执行标记，文字随之切换）
                        EnsureWorkMsg();
                        workMsg!.IsRunningTool = false;
                        workMsg.IsCompressing = false;
                        workMsg.IsWaitingForModel = true;
                        // 刷新前判断是否贴底：用户翻看历史（已上翻）时不强制拽回底部
                        var waitAtBottom = IsMsgAtBottom();
                        workMsg.Refresh();
                        // 仅在原本贴底时滚动，保证提示条可见且不干扰用户回看
                        if (waitAtBottom) ScrollToBottom();
                        break;

                    case UiEventType.ToolStart:
                        // 模型已返回并进入工具执行阶段：等待提示条从“正在思考”切换为“正在执行”
                        // （思考轮文本不再进气泡打字机——气泡只打字最终结论，无需中途补全）
                        // 刷新前判断是否贴底：用户翻看历史（已上翻）时不强制拽回底部
                        var toolAtBottom = IsMsgAtBottom();
                        if (workMsg != null)
                        {
                            workMsg.IsWaitingForModel = false;
                            workMsg.IsRunningTool = true;
                            workMsg.IsCompressing = false;
                            workMsg.Refresh();
                        }
                        // 仅在原本贴底时滚动，保证提示条与新工具卡可见且不干扰用户回看
                        if (toolAtBottom) ScrollToBottom();
                        // 补全上一张思考卡被截断的完整原文：新工具卡出现时，先把思维链尾巴展示完整再插入卡片，
                        // 避免“思考卡显示被工具卡中止”的观感（仅当最后一张是思考卡且内容被截断时触发一次）。
                        // 步骤卡（UpdateTodo/IsStep）同样先补全——该轮不创建工具卡，漏补会造成思考卡仍截断
                        if (workMsg != null
                            && workMsg.ProcessItems.LastOrDefault() is ThinkingItem lastThink
                            && lastThink.Full != null
                            && lastThink.Content.Length < lastThink.Full.Length)
                        {
                            lastThink.Content = lastThink.Full;
                        }
                        // UpdateTodo 内部管理动作不创建工具卡：步骤生命周期已由 step_start/update 事件卡呈现
                        // （计划步骤N），避免与计划卡重复显示
                        if (ev.Tool != null && !ev.Tool.IsStep)
                        {
                            EnsureWorkMsg();
                            // 工具卡默认收缩：只显示标题与状态，不遮挡思考条
                            var card = new ToolCall { Title = ev.Tool.Title, Status = ev.Tool.Status, IsStep = ev.Tool.IsStep, Round = ev.Tool.Round, Icon = ev.Tool.Icon, IconColor = ev.Tool.IconColor, IconFontSize = ev.Tool.IconFontSize };
                            cardMap[ev.Tool.Id] = card;
                            workMsg!.ProcessItems.Add(card);
                            workMsg.Refresh();

                        }
                        break;

                    case UiEventType.ToolUpdate:
                        if (ev.Tool != null && cardMap.TryGetValue(ev.Tool.Id, out var updCard))
                        {
                            updCard.Status = ev.Tool.Status;
                            updCard.Inner = ev.Tool.Inner;
                            updCard.Summary = ev.Tool.Summary;
                            updCard.Warn = ev.Tool.Warn;
                            updCard.Open = ev.Tool.Open;
                            // 文件工具执行完成：回填文件路径与读取行段（供“查看代码”入口显示与定位突显）
                            updCard.FilePath = ev.Tool.FilePath;
                            updCard.StartLine = ev.Tool.StartLine;
                            updCard.EndLine = ev.Tool.EndLine;
                            // Read 成功：把“读取时刻”原码复制进会话快照目录（此刻模型刚读完、后续 Edit 尚未发生），
                            // 历史回放时即使盘上文件已被改，代码查看器仍能显示模型当时所见内容
                            if (ev.Tool.FilePath.Length > 0) EnsureSnapshot(ev.Tool.FilePath);
                            workMsg?.Refresh();

                        }
                        break;

                    case UiEventType.Round:
                        // 模型本轮已返回，进入思考/工具执行阶段，隐藏等待提示
                        if (workMsg != null) workMsg.IsWaitingForModel = false;
                        if (workMsg != null) workMsg.IsCompressing = false;
                        // 同步会话列表右侧执行进度：本轮次（后台运行按归属会话；前台当前会话）
                        (sessionHistory.FirstOrDefault(s => s.Id == ev.SessionKey) ?? currentSession)
                            ?.SetListProgress(rounds: ev.Round);
                        if (ev.Usage != null)
                            accTokens += ev.Usage.Total;
                        EnsureWorkMsg();
                        // 最终纯文本回复的思考内容已展示在结果气泡中，跳过避免重复
                        if (!ev.IsFinal)
                        {
                            // 隐藏所有“第n轮对话 执行工具”类的意图说明卡片，仅保留有实质思考内容的轮次
                            var intent = ev.Intent?.Trim() ?? "";
                            var fbPrefix = string.IsNullOrEmpty(ev.Fallback) ? "" : ev.Fallback + " ";
                            // 深度思考直播卡（ThinkingLive 事件创建的实时预览）就地转正，避免同轮出现两张卡
                            var liveCard = workMsg!.ProcessItems.LastOrDefault() is ThinkingItem { IsLive: true } lc ? lc : null;
                            // 先按语义算出正式卡参数，直播卡转正与新建路径共用同一套结果
                            string title = "", content = "";
                            string? full = null;
                            var open = false;
                            var skipCard = false;   // 历史残留“第n轮对话 执行工具”前缀意图：不建卡
                            if (!string.IsNullOrWhiteSpace(intent) && !intent.StartsWith($"第{ev.Round}轮对话 执行工具"))
                            {
                                title = $"{fbPrefix}第{ev.Round}轮对话 {intent}";
                            }
                            else if (string.IsNullOrWhiteSpace(intent))
                            {
                                // 边说明边调工具轮：有实质思考时展开显示模型 content 原文；
                                // 内容只有一句“执行…：…”式意图说明时并入标题显示（第n轮思考 · 执行…），卡片收缩
                                // Full 保存完整原文（未截断），工具卡出现时补全，保证思维链尾巴不丢失
                                var fullText = string.IsNullOrWhiteSpace(ev.Reasoning) ? null : CleanMd(ev.Reasoning.Trim());
                                var thinkingText = string.IsNullOrWhiteSpace(fullText)
                                    ? "（本轮无文字思考）"
                                    : TruncDots(fullText, SystemCfg.CardMaxChars);
                                var t = thinkingText.Trim();
                                var singleIntent = !t.Contains('\n')
                                    && t.StartsWith("执行")
                                    && !t.StartsWith("（本轮无");
                                title = singleIntent
                                    ? $"{fbPrefix}第{ev.Round}轮思考 · {TruncDots(t, 60)}"
                                    : $"{fbPrefix}第{ev.Round}轮思考";
                                content = singleIntent ? "" : thinkingText;
                                full = singleIntent ? null : fullText;
                                open = !singleIntent;
                            }
                            else skipCard = true;
                            if (liveCard != null)
                            {
                                // 直播卡转正：标题/正文替换为正式内容，思考时长更新为整轮实际耗时，上下文 token 补齐
                                liveCard.IsLive = false;
                                if (!skipCard)
                                {
                                    liveCard.Title = title;
                                    liveCard.Content = content;
                                    liveCard.Full = full;
                                    liveCard.Open = open;
                                }
                                else
                                {
                                    liveCard.Title = $"{fbPrefix}第{ev.Round}轮思考";
                                    liveCard.Content = "";
                                    liveCard.Open = false;
                                }
                                liveCard.DurationMs = ev.RoundDurationMs;
                                liveCard.ContextTokens = ev.Usage?.Prompt ?? 0;
                                liveCard.Round = ev.Round;   // 轮次归属：供「上下文」按钮定位请求上下文快照文件
                            }
                            else if (!skipCard)
                            {
                                workMsg!.ProcessItems.Add(new ThinkingItem
                                {
                                    Title = title,
                                    Content = content,
                                    Full = full,
                                    Open = open,
                                    DurationMs = ev.RoundDurationMs,
                                    ContextTokens = ev.Usage?.Prompt ?? 0,
                                    Round = ev.Round   // 轮次归属：供「上下文」按钮定位请求上下文快照文件
                                });
                            }
                        }
                        workMsg!.Steps = $"[第 {ev.Round} 轮完成] 本轮 {ev.Usage?.Total ?? 0} tokens · 累计 {accTokens} · 用时 {ElapsedText(taskStart)} · 第 {ev.Round + 1} 轮进行中…";
                        workMsg.Refresh();

                        if (!EventBg)
                            SaveCurrentSession();   // 每轮模型返回后落盘，保证异常/崩溃不丢进度（后台运行：写回归属会话会把被查看会话快照写脏，收口时由 Finished 按归属统一写回）
                        break;

                    case UiEventType.CompressStart:
                        // 上下文压缩整理开始（可能含 LLM 必用文件询问/LLM 压缩兜底，数秒~十几秒）：
                        // 等待条切“正在整理上下文”（压缩结束由 WaitingModel 事件接替切“正在思考”）；
                        // 快速路径的事件与 WaitingModel 同批合并，几乎不可见
                        EnsureWorkMsg();
                        workMsg!.IsRunningTool = false;
                        workMsg.IsWaitingForModel = false;
                        workMsg.IsCompressing = true;
                        workMsg.Refresh();
                        break;

                    case UiEventType.ThinkingLive:
                        UnlockSessionSwitch(ev.SessionKey);   // 首个思考 token 到达 = SSE 流已绑定本会话 → 解除会话切换锁（幂等，其它会话 no-op）
                        // 深度思考渐进直播：reasoning 增量由 AgentLoop 节流推送（约 1.2s 一次）；
                        // 思考中尚无直播卡时先建“深度思考中”卡（正文尾部预览实时刷新 + 右上角计时），
                        // 轮结束由 Round 事件就地转正为正式思考卡；无思考流的模型不触发此分支
                        if (workMsg != null && workMsg.IsWaitingForModel && !string.IsNullOrEmpty(ev.LiveText))
                        {
                            if (workMsg.ProcessItems.LastOrDefault() is not ThinkingItem live || !live.IsLive)
                            {
                                live = new ThinkingItem { Title = "深度思考中", Content = "", Open = true, IsLive = true };
                                workMsg.ProcessItems.Add(live);
                                liveThinkStart = DateTime.Now;
                                if (IsMsgAtBottom()) ScrollToBottom();
                            }
                            // 只取最新尾部预览（约 900 字），避免思考长文每次整串重排卡片
                            var liveT = ev.LiveText.Trim();
                            live.Content = liveT.Length <= 900 ? liveT : "…" + liveT[^900..];
                            live.DurationMs = (int)DateTime.Now.Subtract(liveThinkStart).TotalMilliseconds;
                            workMsg.Refresh();
                            if (IsMsgAtBottom()) ScrollToBottom();
                        }
                        break;

                    case UiEventType.StreamDelta:
                        UnlockSessionSwitch(ev.SessionKey);   // 首个结论 token 到达 = SSE 流已绑定本会话 → 解除会话切换锁（幂等，其它会话 no-op）
                        // 只有最终结论会走气泡打字机（思考轮文本只进思考卡，不再实时上屏）
                        if (!string.IsNullOrEmpty(ev.Delta))
                        {
                            // 结论首段：清空残留文本与积压队列，再打字机显示结论文本
                            if (ev.Start)
                            {
                                pendingStream = null;
                                if (workMsg != null)
                                {
                                    workMsg.Text = "";
                                    workMsg.Refresh();
                                }
                            }
                            // 只入队积压文本，由节流定时器逐小段上屏（避免 100ms 批量 Drain 直接蹦出大段）
                            pendingStream += ev.Delta;
                            if (streamTicker != null && !streamTicker.IsEnabled) streamTicker.Start();
                        }
                        break;

                    case UiEventType.SecurityAlert:
                        // 危险命令被拦截或待确认：
                        // Block=在会话里插入红色系统提示；Confirm=嵌入工作消息工具卡下方（允许/取消按钮）
                        if (ev.Alert != null)
                        {
                            var isConfirm = ev.Alert.Level == "Confirm";
                            if (isConfirm)
                            {
                                // 跨会话查看（含切到其它会话时后台叶子/审查触发的确认）不做模态打断：统一走下方会话内确认卡——
                                // 代理已锚事件归属记录（ActiveRec=_evRec：叶子事件=编排归属记录，后台主对话=其归属会话记录），
                                // 后台（EventBg）时卡片只挂归属会话记录不进被查看会话视口，切回该会话经 RestoreBgWorkMsg 接回可见可裁决。
                                // 待确认：嵌入执行过程时间线（工具卡下方），不作为新消息
                                EnsureWorkMsg();
                                var sb = new System.Text.StringBuilder();
                                if (!string.IsNullOrEmpty(ev.Alert.Command))
                                {
                                    sb.Append("发现了危险行为：").Append(ev.Alert.Command).Append('\n');
                                    if (!string.IsNullOrEmpty(ev.Alert.Pattern))
                                        sb.Append("命中规则：").Append(ev.Alert.Pattern.Trim()).Append('\n');
                                }
                                if (!string.IsNullOrEmpty(ev.Alert.Intent))
                                    sb.Append("模型要做的事：").Append(TruncDots(CleanMd(ev.Alert.Intent.Trim()), 120)).Append('\n');
                                sb.Append("是否允许执行？");
                                dangerCard = new DangerConfirmItem { Content = sb.ToString().TrimEnd() };
                                // 决策路由随卡收拢到事件归属：叶子/审查挂起（事件经 LeafEventBridge 携 orchOwner.Id）→ 叶子/审查会话的 Loop；
                                // 其余（前台/后台主对话各会话）→ 事件归属会话的 Runner（本叶子：主对话已按会话 Runner 化，
                                // 危险挂起/确认都落在该会话自己的 Runner 上，两会话各有挂起互不串）。判据与"正在查看哪个会话"无关——
                                // 卡建在事件归属记录上，切回该会话点卡时仍解正确的锁
                                var dangerLeaf = (orchOwner != null && ev.SessionKey == orchOwner.Id) ? planRunner?.CurrentLeafSession : null;
                                var dangerOwner = RunnerForEventKey(ev.SessionKey);
                                dangerResolve = dangerLeaf != null
                                    ? (Action<bool>)(allow => dangerLeaf.DecideDanger(allow))
                                    : allow => dangerOwner?.Loop.ResolveDanger(allow);
                                StartDangerMobilePoll(ev.SessionKey, dangerResolve, ev.Alert);   // 手机端（观看模式）决策回写收件箱，挂起期间轮询消费
                                workMsg!.ProcessItems.Add(dangerCard);
                                workMsg.Refresh();
                                // 倒计时定时器仅兼容旧"限时决策"模式（CountdownSeconds>=0）才启动；
                                // 默认 -1 = 永不超时：Agent 侧挂起等人工裁决（人不在时 IdleGate 钉钉提醒），无需每秒空转
                                if (dangerCard.CountdownSeconds >= 0)
                                {
                                    dangerTicker = new System.Windows.Threading.DispatcherTimer
                                    {
                                        Interval = TimeSpan.FromSeconds(1)
                                    };
                                    dangerTicker.Tick += OnDangerTick;
                                    dangerTicker.Start();
                                }
                            }
                            else
                            {
                                // 叶子被拦截提示带会话归属：非归属前台不上屏（避免污染被查看会话的历史；叶子会话记录里有完整过程）
                                if (orchOwner != null && currentSession != orchOwner && ev.SessionKey == orchOwner.Id) break;
                                // 已拦截：红色系统提示消息
                                var label = "🚫 危险命令已拦截";
                                var sb = new System.Text.StringBuilder();
                                if (!string.IsNullOrEmpty(ev.Alert.Command))
                                {
                                    sb.Append("发现了危险行为：").Append(ev.Alert.Command).Append('\n');
                                    if (!string.IsNullOrEmpty(ev.Alert.Pattern))
                                        sb.Append("命中规则：").Append(ev.Alert.Pattern.Trim()).Append('\n');
                                }
                                if (!string.IsNullOrEmpty(ev.Alert.Intent))
                                    sb.Append("模型要做的事：").Append(TruncDots(CleanMd(ev.Alert.Intent.Trim()), 120)).Append('\n');
                                if (!string.IsNullOrEmpty(ev.Alert.Message))
                                    sb.Append(ev.Alert.Message).Append('\n');
                                if (EventBg)
                                {
                                    // 后台运行（本会话在后台执行中被拦截，用户正查看其它会话）：红色提示不写被查看会话的
                                    // 视口/消息流（会污染被查看会话并在其落盘时写脏），改以红卡挂在本会话工作消息上——
                                    // 切回本会话经 RestoreBgWorkMsg 随工作消息接回可见，收口经 PersistBgResult 写回归属会话历史。
                                    EnsureWorkMsg();
                                    workMsg!.ProcessItems.Add(new ToolCall
                                    {
                                        Title = label,
                                        Status = "✗ 拦截",
                                        Warn = true,
                                        Icon = "🚫",
                                        Inner = sb.ToString().TrimEnd(),
                                    });
                                    workMsg.Refresh();
                                    break;
                                }
                                AddMessage(new ChatMessage
                                {
                                    Kind = MsgKind.Agent,
                                    Who = label,
                                    Text = sb.ToString().TrimEnd(),
                                    IsAlert = true,
                                    AlertLevel = "Block",
                                });
                            }

                        }
                        break;

                    case UiEventType.PlanPending:
                        // 计划审批：Agent 生成了执行计划，等待用户批准/拒绝
                        if (EventBg)
                        {
                            // 两会话并行：后台会话（当前正在查看其它会话）的计划审批挂起不做模态打断——
                            // 计划已挂在其归属 Runner（Loop.PlanPending），由事件泵轮末 RefreshPendingBadges 为该会话亮"?"角标；
                            // 切回该会话时在 OpenSession 尾部补弹审批（同 PromptPlanApproval 决策），批准/拒绝后挂起解除、角标熄灭
                            break;
                        }
                        if (ev.Plan != null)
                        {
                            var r = PromptPlanApproval(ev.Plan);
                            RunnerForEventKey(ev.SessionKey)?.Loop.ResolvePlan(r);   // 计划挂起在归属会话 Runner 上（两会话并行不串台）
                        }
                        break;

                    case UiEventType.DangerTimeout:
                        // 危险确认超时兜底（Agent 侧现永不超时：挂起等人工裁决，正常情况下收不到此事件；收到则隐藏卡片并中止）
                        if (dangerCard != null) { dangerCard.Visible = false; dangerCard = null; }
                        cts?.Cancel();
                        break;

                    case UiEventType.Todo:
                        // 待办清单变更：模型清单（create/update/done_all）或动态登记行 → 更新计划卡与顶部进度条
                        if (ev.Todo != null)
                        {
                            EnsureWorkMsg();
                            ApplyTodo(ev.Todo);
                            SyncSessionListTodo(ev.SessionKey);   // 同步会话列表右侧执行进度：计划完成 %
                        }
                        break;

                    case UiEventType.Summary:
                        // 首次请求模型已返回会话摘要标题，立即保存会话到历史列表；同时记住该标题，
                        // 后续任务完成/新建会话的无参保存沿用同一标题（否则会按首条消息前 30 字另建一条重复记录）
                        if (!string.IsNullOrWhiteSpace(ev.Reasoning))
                        {
                            if (!EventBg)
                            {
                                // 标题守卫（bug：任务消息"布署"曾顶掉历史会话原标题）：
                                // Summary 只允许给"尚未定题"的会话命名（新建占位/空标题）；历史会话即使
                                // 被某宿主误当首次任务而误发摘要，也绝不能覆盖已有标题。命中守卫时仅跳过
                                // 命名（不置 pendingSessionTitle、不 SaveCurrentSession），消息随任务收口照常落盘。
                                var sumS = sessionHistory.FirstOrDefault(s => s.Id == ev.SessionKey);
                                bool untitled = sumS == null
                                    ? currentSession == null || currentSession.Id == ev.SessionKey
                                    : string.IsNullOrWhiteSpace(sumS.Title) || sumS.Title == "新会话";
                                if (untitled)
                                {
                                    pendingSessionTitle = ev.Reasoning;
                                    sessionTitleSet = true;   // 标题已确定，后续切换/保存不再用用户首句话覆盖
                                    SaveCurrentSession(ev.Reasoning);
                                }
                            }
                            else if (_evRec != null)
                            {
                                // 后台运行：标题直写归属会话（视口标题态 pendingSessionTitle 属被查看会话，不得污染），
                                // 落盘随 Finished 收口统一按归属写回
                                // 归属会话=事件 SessionKey（_evRec 即按该键从 runTasks 解析，两者一致）
                                var ownS = sessionHistory.FirstOrDefault(s => s.Id == ev.SessionKey);
                                if (ownS != null && (string.IsNullOrWhiteSpace(ownS.Title) || ownS.Title == "新会话"))
                                    ownS.Title = TruncDots(SessionItem.ToOneLine(ev.Reasoning), 15);   // 单行化后再截断：会话列表项只显示一行
                            }
                        }
                        break;

                    case UiEventType.Failed:
                        HideDangerCard();   // 收尾兜底：失败/停止时收起残留危险确认卡（卡片现由用户裁决或点停止收起，不再自动超时）
                        // 任务失败/停止：保留未勾选步骤作为断点痕迹，信息条恢复滚动逻辑；
                        // 注意：Failed 之后必跟 Finished，currentTodo 统一由 FinishTodo 清空，
                        // 此处不能提前清，否则 FinishTodo 无法补全/收尾待办步骤
                        pendingStream = null;
                        streamTicker?.Stop();
                        taskActive = false;
                        RefreshTodoBar();
                        UpdateChatTopInfo();
                        // 模型调用失败：在对话区显示错误信息。具体原因优先取 Failed 事件携带的 Reasoning（Agent 侧异常/停止路径已补齐），
                        // 回退状态行 logStatus.Text（可能只剩"[Round N] 调用模型..."过程日志），再兜底"未知"，避免只见过程日志而看不到真因
                        var failReason = !string.IsNullOrWhiteSpace(ev.Reasoning) ? ev.Reasoning
                            : !string.IsNullOrWhiteSpace(logStatus.Text) ? logStatus.Text : "未知原因（详见运行日志）";
                        if (workMsg != null)
                        {
                            workMsg.Kind = MsgKind.Agent;
                            workMsg.Text = $"⚠️ 模型调用失败：{failReason}";
                            workMsg.Finished = true;
                            workMsg.ProcessOpen = false;
                            workMsg.StepsBase = $"[失败] {ev.Round} 轮 · {ev.TotalTokens} tokens · 用时 {ElapsedText(taskStart)}";
                            workMsg.Steps = workMsg.StepsBase;
                            workMsg.Refresh();

                        }
                        else if (!EventBg)
                        {
                            // 如果没有工作消息，添加一个新的错误消息（后台运行：不落被查看会话视口，结果随 Finished 收口写回归属会话）
                            AddMessage(new ChatMessage
                            {
                                Kind = MsgKind.Agent,
                                Who = "GAIRR",
                                Text = $"⚠️ 模型调用失败：{failReason}",
                            });
                        }
                        // 会话状态收口：模型调用失败 → 中断(红)。cts!=null 甄别真主任务（叶子桥接事件到达时 cts 为 null）；
                        // 后台运行：收口归属写事件归属会话（mark 其 RunState/红点），被查看会话不受影响
                        if (cts != null) MarkRunEnd(_evRec != null ? sessionHistory.FirstOrDefault(s => s.Id == ev.SessionKey) : currentSession, false);
                        // 模型调用失败且重试无效（"模型异常："前缀 = AgentLoop catch LlmException 出口）：把该任务实际所用模型
                        // （归属会话 Runner 的 Loop.Provider/ModelName，多会话并行/后台各自准）标红到模型下拉，供用户点选手动重试；
                        // 该模型后续某次任务正常完成时自动恢复（见 Finished 成功收口 ClearModelFault）
                        if (IsModelFaultReason(failReason))
                        {
                            var faultRunner = RunnerOf(_evRec != null ? sessionHistory.FirstOrDefault(s => s.Id == ev.SessionKey) : currentSession);
                            if (faultRunner != null)
                                MarkModelFault(faultRunner.Loop.Provider, faultRunner.Loop.ModelName, failReason);
                        }
                        UnlockSessionSwitch(ev.SessionKey);   // 任务失败兜底：解除会话切换锁（幂等）
                        UnlockSend();
                        break;

                    case UiEventType.Finished:
                        UnlockSessionSwitch(ev.SessionKey);   // 任务结束兜底：若全程未产出 token 也解除会话切换锁（幂等）
                        var orphanAborted = false;   // 孤儿工具指令中止：模型已发工具指令但循环未再回 LLM 收尾（无结论无失败事件）
                        HideDangerCard();   // 收尾兜底：任务整轮结束收起残留危险确认卡（幂等）
                        // 流式文本若还有节流积压（极速输出场景），立即补齐，保证结果完整
                        if (!string.IsNullOrEmpty(pendingStream))
                        {
                            AppendStreamText(pendingStream!);
                            pendingStream = null;
                            streamTicker?.Stop();
                        }
                        // 收尾兜底：任务结束时若最后一张思考卡仍是截断预览，补齐完整原文（幂等）
                        if (workMsg != null
                            && workMsg.ProcessItems.LastOrDefault() is ThinkingItem lastStillThink
                            && lastStillThink.Full != null
                            && lastStillThink.Content.Length < lastStillThink.Full.Length)
                        {
                            lastStillThink.Content = lastStillThink.Full;
                        }
                        // 框架级异常中止判定：气泡无结论正文、末项停在"第N轮对话 执行…"意图卡 = 模型已发工具指令、
                        // 框架进了工具执行阶段，但循环未再回 LLM 拿下一轮响应（工具阶段卡死等未兜住的中断）——
                        // 显式改写为失败提示，避免任务静默停在"执行工具:..."既无结论也无 ⚠️
                        if (workMsg != null
                            && string.IsNullOrWhiteSpace(workMsg.Text)
                            && ActiveRec?.StopRequested != true   // 手动停止排除在外：用户主动取消不算报错中止，不改写 ⚠ 气泡（终态卡不误显重试）
                            && workMsg.ProcessItems.LastOrDefault() is ThinkingItem { } orphanThink
                            && orphanThink.Title.Contains($"轮对话 执行"))
                        {
                            workMsg.Text = "⚠️ 任务在工具执行阶段中止（未产出结论）";
                            orphanAborted = true;
                        }
                        // 方案 i：任务正常完成后自动补全未勾选步骤（失败 ✗ 保留痕迹），信息条恢复滚动逻辑
                        FinishTodo();
                        var status = workMsg?.Text?.StartsWith("⚠") == true ? "失败" : "成功";
                        // 纯文本回复时 workMsg 一定存在，需要更新 UI 显示结果
                        if (workMsg != null)
                        {
                            workMsg.IsRunningTool = false;
                            workMsg.IsWaitingForModel = false;
                            workMsg.IsCompressing = false;
                            // 深度思考直播卡未及转正（思考中途失败/停止）：降级为普通卡并标记中断，不残留“深度思考中”悬空卡
                            if (workMsg.ProcessItems.LastOrDefault() is ThinkingItem liveLeft && liveLeft.IsLive)
                            {
                                liveLeft.IsLive = false;
                                liveLeft.Title = "思考中断";
                            }
                            workMsg.Kind = MsgKind.Agent;
                            // 完成后工具卡保持收缩，思考条保留展开状态（便于回看思考过程）
                            foreach (var p in workMsg.ProcessItems)
                                if (p is ToolCall tc) tc.Open = false;
                            workMsg.Finished = true;
                            workMsg.ProcessOpen = false;
                            var hasProcess = workMsg.HasProcessContent;
                            var elapsed = $" · 用时 {ElapsedText(taskStart)}";
                            var endMark = workMsg.Text?.StartsWith("⚠") == true ? "失败" : "完成";   // 孤儿中止/失败收口不再误显"完成"
                            workMsg.StepsBase = hasProcess
                                ? $"[{endMark}] {ev.Round} 轮 · {workMsg.ProcessItems.OfType<ThinkingItem>().Count()} 思考 · {workMsg.ProcessItems.OfType<ToolCall>().Count()} 动作 · {ev.TotalTokens} tokens{elapsed}"
                                : $"[{endMark}] {ev.Round} 轮 · {ev.TotalTokens} tokens{elapsed}";
                            workMsg.Steps = workMsg.StepsBase + (hasProcess ? StepsHint(false) : "");
                            workMsg.Refresh();

                        }
                        // 问题卡片（平台级）：任何会话（编排 + 普通）模型回复含 question-card 块时，解析为结构化卡片挂到消息上，
                        // 并当场从正文剥离原始卡片文本
                        // 卡片不落历史（剥离后才保存），重开会话只回放文字问答
                        // 后台运行（EventBg）跳过：卡片/面板/序号均属会话视口，收口写回归属会话时只保存文字问答，
                        // 切回该会话可继续作答（RestoreQuestionCards 还原，不区分会话类型）
                        if (!EventBg && currentSession != null && workMsg != null)
                        {
                            var cards = GAIRR.AgentHost.OrchestrationSession.ParseQuestionCards(workMsg.Text ?? "");
                            if (cards.Count > 0)
                            {
                                workMsg.Text = GAIRR.AgentHost.OrchestrationSession.StripQuestionCards(workMsg.Text ?? "");
                                foreach (var c in cards)
                                {
                                    var qv = MakeQCardVm(c);
                                    qv.No = ++qCardSeq;
                                    workMsg.AddQuestionCard(qv);   // 订阅作答变化 → 多卡时驱动公共提交条状态
                                }
                                workMsg.Refresh();
                                RefreshOrcQProgress();   // 新卡入列：会话级澄清进度即时更新（进度提示条仍只在编排收集期显示，普通会话内部自动隐藏）
                            }
                        }
                        // 本轮改动聚合（先于落盘，故清单随 MessageRecord.Changes 一起持久化）：按任务时间窗从变更日志
                        // 取本会话写入记录 → 写入工作消息（消息底部“本轮改动”条带）并并入归属会话累计清单（左栏卡片）。
                        // 归属口径同下方 PersistBgResult：后台收口归其会话（按 ev.SessionKey 反查），前台归当前查看会话
                        CollectRoundChanges(workMsg,
                            EventBg ? (_evRec != null ? sessionHistory.FirstOrDefault(s => s.Id == ev.SessionKey) : null) : currentSession,
                            taskStart);
                        // 记录任务历史
                        taskHistory.Insert(0, new TaskItem
                        {
                            Title = string.IsNullOrEmpty(lastTask) ? "（未命名任务）" : lastTask[..Math.Min(lastTask.Length, 40)],
                            Status = status,
                            Rounds = ev.Round,
                            Tokens = ev.TotalTokens,
                            Time = DateTime.Now,
                        });
                        // 后台运行（EventBg）收口：结果消息按归属写回其会话消息历史并落盘（离开时刻视口已清空，
                        // 不能走 SaveCurrentSession——那会写脏被查看会话）；前台走原视口落盘
                        if (EventBg)
                        {
                            var ownS = _evRec != null ? sessionHistory.FirstOrDefault(s => s.Id == ev.SessionKey) : null;
                            if (ownS != null && workMsg != null) PersistBgResult(ownS, workMsg);
                        }
                        else
                            SaveCurrentSession();

                        // 编排会话：每轮收尾刷新右侧面板（首轮回复后 L0 目标才可显示；新建/切换后不残留上一会话内容）。
                        // 后台运行（EventBg）跳过：面板属被查看会话视口，切回归属会话后由其前台事件刷新
                        if (!EventBg && currentSession?.IsOrchestration == true) UpdateOrcPanel();

                        // 编排会话收口（阶段 3）：Generating 结束 → 解析任务树 → 确认对话框 → 落盘执行
                        // 阶段判定以当前会话 Runner 的状态机为准（OrchMode 随 Runner 归各会话，两会话并行各自独立；
                        // OrchPhase 仍同步留 SessionItem 供序列化/还原——Runner 创建与收口时两处保持一致）。
                        // 后台运行（EventBg）跳过：确认/自动续跑均属归属会话视口，切回该会话后人工继续产树
                        if (!EventBg && currentSession?.IsOrchestration == true && CurRunner?.Loop.OrchMode == "generating")
                            HandleOrchestrationFinished();

                        // 编排会话 Collecting 阶段：模型回复中包含收敛信号时自动切换到 Generating
                        // 后台运行（EventBg）跳过：自动重发会挤占全局 loop/视口输入框，切回归属会话后人工触发
                        if (!EventBg && currentSession?.IsOrchestration == true && CurRunner?.Loop.OrchMode == "collecting")
                        {
                            var replyText = workMsg?.Text ?? "";
                            if (GAIRR.AgentHost.OrchestrationSession.IsConvergenceSignal(replyText))
                            {
                                CurRunner?.Loop.SwitchOrch("generating");
                                currentSession.OrchPhase = "generating";
                                SaveCurrentSession();
                                UpdateOrcPanel();
                                // 自动重新发送上一轮用户消息触发产树
                                _ = Dispatcher.InvokeAsync(() =>
                                {
                                    inputBox.Text = lastTask;
                                    OnSend(sendBtn, new RoutedEventArgs());
                                });
                            }
                        }

                        // 会话状态收口：整轮任务结束。记录=事件归属会话的任务记录（事件过滤已保证前台事件归属=当前会话，
                        // 后台运行=被放行的归属会话记录）；rec.Cts!=null 甄别"真主对话/编排产树任务"（叶子桥接事件到达时 Cts 已为 null 不误标）；
                        // 用户点击停止（rec.StopRequested）→ 中断(红)，否则正常完成 → 完成(绿)；后台运行按归属会话标记 RunState
                        var rec = ActiveRec;   // 收口直改记录：两会话并行/后台运行下各会话记录按所属会话自行收口，互不清对方字段
                        var ownSession = _evRec != null ? sessionHistory.FirstOrDefault(s => s.Id == ev.SessionKey) : currentSession;
                        if (rec != null && rec.Cts != null) MarkRunEnd(ownSession, !rec.StopRequested && !orphanAborted);   // 孤儿工具指令中止同样标中断(红)
                        // 模型异常红标恢复：该模型最近一次任务正常完成（status=="成功"——模型异常后紧跟的 Finished 其
                        // 气泡以 ⚠ 开头 status="失败"不会误清；用户停止同样不清）→ 下拉该模型"⚠异常"红标自动消除
                        if (status == "成功" && rec != null && rec.Cts != null && ownSession != null)
                        {
                            var okRunner = RunnerOf(ownSession);
                            if (okRunner != null) ClearModelFault(okRunner.Loop.Provider, okRunner.Loop.ModelName);
                        }
                        if (rec != null)
                        {
                            rec.WorkMsg = null;
                            rec.CardMap.Clear();
                            rec.AccTokens = 0;
                            rec.AutoHitText = null;   // 本轮自动匹配命中清除：顶部 logo 标题/头像标题恢复"🤖 自动匹配"显示
                            rec.Cts?.Dispose();
                            rec.Cts = null;
                        }
                        // 归属会话仍在当前视口：收口后同步把顶部标题恢复为"自动匹配"（命中文本不留到结束后）
                        if (ownSession != null && ReferenceEquals(ownSession, currentSession)) RefreshTopPinLine();
                        // 本叶子：不再需要回切——上下文已随会话 Runner 归各会话（每 Runner 自带 history/Key/标题，两会话
                        // 互不串扰），后台收口不改动任何其它会话 Runner。原"ForegroundBusyAnywhere 跳过后补齐 loop 上下文"
                        // 守卫随唯一全局 AgentLoop 一并删除（见 OnProjectChanged/OpenSession/NewSession 守卫注释）。
                        // 任务收尾：后台自动提交本任务产生的变更（归属标识=启动点记录的会话/计划短 id，git log 可反查），
                        // 有新提交则回 UI 线程把 commit 行插入左侧“项目跟踪”分组（git 关闭/无变更时 Created=false 不打扰）
                        var gkey = gitLastKey;
                        // 提交摘要与分组归属名用“会话标题”（模型按本轮目标生成的功能性摘要，≤15 字），
                        // 而不是用户发言原文（含“继续/可以按方案改”等无功能信息的承接话术），
                        // 使 git 提交在“项目跟踪”里按实际功能可读、可按会话聚合。后台运行取事件归属会话标题。
                        var commitTask = ownSession?.Title;
                        if (string.IsNullOrWhiteSpace(commitTask) || commitTask == "新会话") commitTask = pendingSessionTitle;
                        if (string.IsNullOrWhiteSpace(commitTask) || commitTask == "新会话") commitTask = lastTask;   // 标题缺失时兜底回退
                        _ = Task.Run(async () =>
                        {
                            // 收口 git 用归属项目根（验收 8）：后台任务在切项目后跑完时，实时 cfg.ProjectRoot 已指向新项目，
                            // 继续用它会把旧项目的改动提交/初始化到新项目仓库（串写旧根）；会话 Project 即发起时归属根。
                            var gcfg = cfg.ViewForProjectRoot(TaskOwnerProjectRoot(ownSession));
                            var goc = await GitMgr.AutoCommitAsync(gcfg, commitTask, ev.Round, ev.TotalTokens, gkey);
                            if (goc.Created)
                            {
                                // 拉变更清单做分类：纯内部/产物提交不冒顶（收纳进灰组）；源码提交就地插入
                                var fl = new List<GitFileStat>();
                                try { fl = await GitMgr.ShowStatAsync(gcfg, goc.Hash); } catch { }
                                _ = Dispatcher.InvokeAsync(() => NotifyGitCommit(goc, fl));
                            }
                        });
                        if (planRunner == null) gitLastKey = null;   // 编排执行中保留归属键：多个叶子收尾共用同一会话标识；否则防迟到收尾误带上一次
                        UnlockSend();
                        break;

                    case UiEventType.LockWait:
                        // 排队等文件锁：把所属会话置 2(黄点"等待文件锁")；锁到手自动恢复 1(进行中)。
                        // 不打断任务收口链：Finished/Interrupted 仍经各自 case 置 3/4，这里仅在当前仍是等待态时回退 1。
                        // EventBg(后台运行)：只 mark 归属会话圆点/徽标（UpdateTabBadges），logStatus 属被查看会话视口不写。
                        // 前台：等待行（含已等待时长/占用者）每 2s 节流刷到右侧状态栏 → 长等待持续可见，不像卡死。
                        var lockSession = _evRec != null ? sessionHistory.FirstOrDefault(s => s.Id == ev.SessionKey) : currentSession;
                        if (lockSession != null)
                        {
                            if (ev.Waiting) lockSession.RunState = 2;
                            else if (lockSession.RunState == 2) lockSession.RunState = 1;
                            UpdateTabBadges();
                        }
                        if (!EventBg && ev.LogLine != null)
                            logStatus.Text = ev.LogLine;
                        break;
                    case UiEventType.Log:
                        // 会话日志显示到右侧状态栏（logStatus）；Steps 条的轮次状态由 Round 事件统一维护，不再写入日志文本
                        // 后台运行（EventBg）不写：logStatus 属被查看会话视口，避免后台任务的日志串台
                        if (!EventBg && ev.LogLine != null)
                            logStatus.Text = ev.LogLine;
                        break;

                    case UiEventType.DocStatus:
                        // 项目地图自动化进度：后台运行日志，显示到左侧状态栏（bgStatus），与会话日志（logStatus）分离
                        if (ev.LogLine != null) bgStatus.Text = ev.LogLine;
                        break;

                    case UiEventType.LspStatus:
                        // 语言服务器生命周期：后台运行日志，显示到左侧状态栏（bgStatus）
                        if (ev.LogLine != null) bgStatus.Text = ev.LogLine;
                        break;
                }
                _evRec = null;   // 事件归属处理结束：恢复前台锚定（_evRec 不得泄漏到下一事件/定时器回调）
            }
            UpdateChatTopInfo();   // 任务 Steps 实时变化时同步顶部信息栏（uiTimer 每 100ms 节流；后台运行时在方法内被 EventBg 守卫跳过）
            RefreshPendingBadges();   // 待决策角标统一刷新：危险/计划挂起事件实时点亮对应会话角标（两会话各归各）；裁决/收口后本轮归 0
            MirrorSessionEvents(drainedEvs);   // 会话事件镜像：手机实时观看数据源（尽力而为，不阻塞 UI）
        };
        uiTimer.Start();
    }

    /// <summary>把本拍归属会话的事件推给实时镜像（手机观看）：仅镜像带 SessionKey 且已建任务记录的会话事件，
    /// 与泵内归属解析同判据（后台运行/切走的会话同样镜像——手机观看的是会话执行流而非桌面视口）</summary>
    void MirrorSessionEvents(List<UiEvent> drainedEvs)
    {
        if (liveMirror == null || drainedEvs.Count == 0) return;
        liveMirror.Push(drainedEvs
            .Where(ev => ev.SessionKey != null && runTasks.ContainsKey(ev.SessionKey))
            .ToList());
    }

    /// <summary>打字机节流 tick：每次从积压队列取一小段上屏（不管 chunk 多大/多密，界面节奏恒定）。
    /// 按定时器实例找归属记录（每会话记录一条节流定时器，EnsureRec 建账）：后台运行（切项目/切会话放行）后
    /// tick 仍落到归属记录，不走代理（代理只锚当前视口/事件归属），避免两会话/后台任务互相串写积压文本；
    /// 仅归属会话=当前视口才滚动。</summary>
    void StreamTickerTick(object? sender, EventArgs e)
    {
        var rec = runTasks.Values.FirstOrDefault(r => ReferenceEquals(r.StreamTicker, sender));
        if (rec == null) return;
        var pending = rec.PendingStream;
        if (string.IsNullOrEmpty(pending))
        {
            rec.StreamTicker?.Stop();
            return;
        }
        var take = Math.Min(pending.Length, StreamTickChars);
        var chunk = pending[..take];
        rec.PendingStream = pending.Length > take ? pending[take..] : null;
        if (rec.PendingStream == null) rec.StreamTicker?.Stop();
        var msg = rec.WorkMsg;
        if (msg == null) return;
        msg.IsWaitingForModel = false;
        if (msg.Kind == MsgKind.Typing) msg.Kind = MsgKind.Agent;
        msg.Text = (msg.Text ?? "") + chunk;
        msg.Refresh();
        if (ReferenceEquals(rec, TaskOf(currentSession))) ScrollToBottom();
    }

    /// <summary>把一段增量文本追加到 GAIRR 气泡（隐藏等待提示、切换消息类型、滚动到底部）</summary>
    void AppendStreamText(string delta)
    {
        if (string.IsNullOrEmpty(delta)) return;
        EnsureWorkMsg();
        // 流式输出已开始，模型已返回，隐藏等待提示
        workMsg!.IsWaitingForModel = false;
        if (workMsg.Kind == MsgKind.Typing) workMsg.Kind = MsgKind.Agent;
        workMsg.Text = (workMsg.Text ?? "") + delta;
        workMsg.Refresh();
        // 流式输出时跟随到底部，但不每次强制双滚
        ScrollToBottom();
    }

    static string StepsHint(bool open) =>
        open ? " · ▾ 收缩" : " · ▸ 展开";

    /// <summary>累计用时格式化：不足 1 分钟显示秒（如 45s），否则分+秒（如 3m12s）</summary>
    static string ElapsedText(DateTime start)
    {
        if (start == DateTime.MinValue) return "";
        var d = DateTime.Now - start;
        return d.TotalSeconds < 60
            ? $"{(int)d.TotalSeconds}s"
            : $"{(int)d.TotalMinutes}m{(int)d.TotalSeconds % 60}s";
    }

    /// <summary>思考条内容截断：超长时截断并保留尾部 ...（工具卡沿用 CardMaxChars 上限）</summary>
    static string TruncDots(string s, int n) => s.Length <= n ? s : s[..n] + "...";

    /// <summary>去除思考内容中的 Markdown 格式符（代码围栏/反引号/加粗/斜体/标题/引用），保留纯文本；
    /// 仅用于思考条展示，工具执行结果原样显示</summary>
    static string CleanMd(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        s = s.Replace("```", "");                          // 代码块围栏
        s = Regex.Replace(s, @"``([^`]*)``", "$1");        // 双反引号
        s = Regex.Replace(s, @"`([^`]*)`", "$1");          // 单反引号
        s = Regex.Replace(s, @"\*\*([^*]*)\*\*", "$1");  // 加粗
        s = Regex.Replace(s, @"(?m)^#{1,6}\s*", "");       // 标题标记
        s = Regex.Replace(s, @"(?m)^>\s*", "");            // 引用标记
        s = Regex.Replace(s, @"\*([^*]*)\*", "$1");        // 斜体（成对星号才替换，避免误伤通配符）
        return s;
    }

    /// <summary>点击 Steps 行：完成后展开/收缩执行过程区（无分析思路/工具卡/轮次内容时不交互）</summary>
    void OnStepsClick(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not ChatMessage m ||
                !m.Finished || !m.HasProcessContent)
                return;
            // 锚定点击位置：切换前记录收缩条相对滚动区的视觉 Y，切换并强制布局后按差值补偿滚动，
            // 使内容向下扩充/向上收缩而点击位置不被挤走
            var yBefore = fe.TransformToAncestor(msgScroll).Transform(new System.Windows.Point(0, 0)).Y;
            // 惰性装配：历史重放消息的卡片在首次展开瞬间才实例化（先补料后切换，布局尺寸真实后滚动补偿才准确）
            if (m.ProcessItems.Count == 0 && m.PendingItems is { Count: > 0 }) MaterializePending(m);
            m.ProcessOpen = !m.ProcessOpen;
            m.Steps = m.StepsBase + StepsHint(m.ProcessOpen);
            m.Refresh();
            msgScroll.UpdateLayout();
            var yAfter = fe.TransformToAncestor(msgScroll).Transform(new System.Windows.Point(0, 0)).Y;
            msgScroll.ScrollToVerticalOffset(msgScroll.VerticalOffset - (yAfter - yBefore));
            e.Handled = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Error] Steps点击异常: {ex.Message}");
        }
    }

    /// <summary>确保当前归属记录有工作消息（Typing 卡）。后台运行（EventBg）时消息只挂归属会话记录、不进
    /// 被查看会话视口消息流，收口时按归属写回该会话历史；前台路径维持"追加到消息流并滚动"原行为。</summary>
    void EnsureWorkMsg()
    {
        if (workMsg != null) return;
        // 工作消息标题（GAIRR 头像旁）：会话钉住（或顶部取样）的 角色+模式+模型 —— 每次执行固定展示该会话执行参数
        workMsg = new ChatMessage { Kind = MsgKind.Typing, Who = "概尔 Agent · " + SessionPinText(currentSession) };
        if (!EventBg) AddMessage(workMsg);
    }

    /* ================= 项目目录树（真实文件系统） ================= */

    void LoadProjects()
    {
        // 先尝试从 config.ini [Projects] 读取
        var list = cfg.Get("Projects", "List", "");
        if (list.Length > 0)
        {
            foreach (var entry in list.Split(','))
            {
                var parts = entry.Trim().Split('|');
                if (parts.Length >= 2) projects.Add(new ProjectItem(parts[0], parts[1]));
            }
        }
        if (projects.Count == 0)
        {
            // 配置里没有已保存项目：不再硬编码开发者本机路径，保留空列表并引导添加
            // （状态栏提示 + 窗口加载后弹一次目录选择；用户取消后可随时点“添加项目 +”再添加）
            if (mStatus != null) mStatus.Text = "尚未添加项目，点击左侧“+”添加项目目录";
            Dispatcher.BeginInvoke(new Action(() => OnProjectAdd(projAddBtn, new RoutedEventArgs())));
        }
 projSel.DisplayMemberPath = "Name";
 // 启动时以工作目录为准选中项目（从项目目录启动即当前项目）；工作目录不在项目列表内则退回首项
 var cwd = Environment.CurrentDirectory;
 var cwdItem = projects.FirstOrDefault(p => string.Equals(p.Path, cwd, StringComparison.OrdinalIgnoreCase));
 projSel.SelectedIndex = cwdItem != null ? projects.IndexOf(cwdItem) : 0;
 // 启动阶段 OnProjectChanged 因 sessionLoaded=false 被跳过，cfg.ProjectRoot 仍是 config.ini 旧值，
 // 会使随后的 LoadSessionHistory 按旧项目过滤会话；此处同步为当前选中项目，保证会话列表随当前项目刷新。
 if (projSel.SelectedItem is ProjectItem selProj)
 {
 cfg.ProjectRoot = selProj.Path;
 AppConfig.ProjectRootStatic = selProj.Path;
 }
 RefreshAppLogo(); // 启动后左上 logo 显示当前项目名称
    }

    void OnProjectAdd(object sender, RoutedEventArgs e)
    {
        // 用 Win32 API 调文件夹选择对话框（避免引用 System.Windows.Forms）
        var path = PickFolder();
        if (path == null) return;
        var name = System.IO.Path.GetFileName(path);
        projects.Add(new ProjectItem(name, path));
        projSel.SelectedIndex = projects.Count - 1;
        SaveProjects();
    }

    static string? PickFolder()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择项目根目录（选文件夹内任意文件即可）",
            CheckFileExists = false,
            FileName = "选择此文件夹"
        };
        if (dlg.ShowDialog() != true) return null;
        return System.IO.Path.GetDirectoryName(dlg.FileName);
    }

    void SaveProjects()
    {
        var lines = string.Join(",", projects.Select(p => $"{p.Name}|{p.Path}"));
        // 写回 config.ini [Projects] List=...
        var path = cfg.ConfigPath;
        if (!System.IO.File.Exists(path)) return;
        var raw = System.IO.File.ReadAllText(path);
        var re = new System.Text.RegularExpressions.Regex(@"\[Projects\][^\[]*");
        var entry = $"[Projects]\nList={lines}\n\n";
        if (re.IsMatch(raw)) raw = re.Replace(raw, entry);
        else raw += "\n" + entry;
        System.IO.File.WriteAllText(path, raw, System.Text.Encoding.UTF8);
    }

    /// <summary>递归加载目录树节点；自动维护清单内的 .md 文档名称带 ⟳ 标识</summary>
    ObservableCollection<TreeNode> LoadDirNodes(string root, int level = 0)
    {
        var nodes = new ObservableCollection<TreeNode>();
        try
        {
            foreach (var dir in System.IO.Directory.GetDirectories(root))
            {
                var name = System.IO.Path.GetFileName(dir);
                if (SystemCfg.IgnoreDirs.Contains(name)) continue;
                var node = new TreeNode { Name = name, FullPath = dir, IsDir = true, Level = level, ColorHex = TreeDirColor };
                node.Children.Add(new TreeNode { Name = "…", FullPath = "", IsDir = false, Level = level + 1 }); // 占位，展开时加载
                nodes.Add(node);
            }
            foreach (var file in System.IO.Directory.GetFiles(root))
            {
                // 目录树展示全部文件（含 .exe/.dll/图片等）：不再按 IgnoreExts 过滤；
                // 该过滤保留给检索/地图工具（防二进制噪音），树里用暗灰弱化非代码文件
                var name = System.IO.Path.GetFileName(file);
                var ext = System.IO.Path.GetExtension(name);
                var rel = System.IO.Path.GetRelativePath(cfg.ProjectRoot, file).Replace('\\', '/');
                var isAutoDoc = autoDocSet.Contains(rel);
                nodes.Add(new TreeNode
                {
                    Name = isAutoDoc ? name + " ⟳" : name,
                    FullPath = file,
                    IsDir = false,
                    Level = level,
                    ColorHex = isAutoDoc ? TreeCfgColor : ext.Equals(".md", StringComparison.OrdinalIgnoreCase) ? TreeDocColor
                        : TreeExeExts.Contains(ext) ? TreeExeColor
                        : TreeCodeExts.Contains(ext) ? TreeCodeColor
                        : TreeCfgExts.Contains(ext) ? TreeCfgColor : TreeDimColor,
                });
            }
        }
        catch { }
        return nodes;
    }

    /// <summary>渲染左侧目录树：顶层固定两分组——项目文件（工作目录，默认展开懒加载）+ 项目跟踪（Git 变更，展开时拉取）。
    /// 分组节点 IsDir=true 复用懒加载占位机制（展开事件替换 …）。</summary>
    void RenderTree(string path)
    {
        if (projSel != null) projSel.ToolTip = "项目路径：" + path;   // 路径小字已被项目选择框替换：完整路径挂选择框 ToolTip
        autoDocSet = new HashSet<string>(ProjectMapAuto.GetAutoDocs(cfg), StringComparer.OrdinalIgnoreCase);
        gitDirty = false;   // 新树：git 分组尚未加载，展开时整拉即可
        var fsNode = new TreeNode
        {
            Name = "项目文件（工作目录）",
            FullPath = path,          // 文件系统分组：懒加载根目录内容
            Tag = "group-fs",
            IsDir = true,
            Level = 0,
            IsOpen = true,
            ColorHex = TreeDirColor,
            Tip = "工作目录真实文件树（bin/obj/.gairr 等系统目录已过滤）",
        };
        fsNode.Children.Add(new TreeNode { Name = "…", IsDir = false, Level = 1 });   // 占位：展开时加载根目录
        gitGroupNode = new TreeNode
        {
            Name = "项目跟踪（Git 变更）",
            Tag = "group-git",
            IsDir = true,
            Level = 0,
            ColorHex = TreeGitGroupColor,
            Tip = "git 提交记录：自动提交含归属任务标识（auto@key），右键可刷新；\n单击/双击 commit 行展开其变更文件，双击文件行在右栏看 diff",
        };
        gitGroupNode.Children.Add(new TreeNode { Name = "…", IsDir = false, Level = 1 });   // 占位：展开时拉提交记录
        treeBox.ItemsSource = new ObservableCollection<TreeNode> { fsNode, gitGroupNode };
    }

    /// <summary>
    void OnWindowF5(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F5) return;
        RefreshProjectTree();
        e.Handled = true;
    }

    /// <summary>
    void RefreshProjectTree()
    {
        var root = cfg.ProjectRoot;
        if (string.IsNullOrWhiteSpace(root) && projects.Count > 0) root = projects[0].Path;
        if (string.IsNullOrWhiteSpace(root)) return;
        RenderTree(root);
    }

    /// <summary>TreeViewItem 展开事件：懒加载真实子节点（替换 … 占位）。文件系统目录 → LoadDirNodes；
    /// “项目跟踪”分组 → 拉取最近 git 提交；commit 行 → 拉取该提交变更文件清单。</summary>
    void OnTreeExpanded(object sender, RoutedEventArgs e)
    {
        if (e.Source is not TreeViewItem tvi || tvi.DataContext is not TreeNode node || !node.IsDir) return;
        var lazy = node.Children.Count == 0 || (node.Children.Count == 1 && node.Children[0].Name == "…");
        if (node.Tag == "group-git")
        {
            if (lazy || gitDirty) LoadGitGroup(node);
        }
        else if (node.Tag == "git-c")
        {
            if (lazy) LoadGitCommitFiles(node);
        }
        else if (lazy)
        {
            // 文件系统目录/项目文件分组：延迟加载以确保 UI 准备好
            Dispatcher.BeginInvoke(() =>
            {
                if (node.Children.Count == 1 && node.Children[0].Name == "…")
                {
                    node.Children.Clear();
                    foreach (var child in LoadDirNodes(node.FullPath, node.Level + 1)) node.Children.Add(child);
                }
            }, System.Windows.Threading.DispatcherPriority.Background);
        }
        e.Handled = true;
    }

    // ──── “项目跟踪（Git 变更）”分组：提交记录/变更文件加载与展示 ────

    /// <summary>后台拉取 git 最近提交（LogRecentAsync + LogFilesAsync 变更路径表）并渲染为分组行：
    /// 源码提交按归属任务 key 聚成任务组、纯内部产物提交收纳灰组、无归属的平铺；组首次展开（替换占位）/置脏重拉/右键刷新共用，
    /// 结果整体重建（清空后逐条添加）。防重入：加载中的组在 gitBusyNodes，重复请求直接跳过。</summary>
    void LoadGitGroup(TreeNode group)
    {
        if (!gitBusyNodes.Add(group)) return;
        var cfg0 = cfg;
        _ = Task.Run(async () =>
        {
            var commits = new List<GitCommitInfo>();
            var files = new Dictionary<string, List<string>>();
            try { commits = await GitMgr.LogRecentAsync(cfg0); } catch { }
            try { files = await GitMgr.LogFilesAsync(cfg0); } catch { }   // 变更路径表：纯内部提交分类与任务分组依据
            _ = Dispatcher.InvokeAsync(() =>
            {
                gitBusyNodes.Remove(group);
                if (group.Children.Count > 0 && !(group.Children.Count == 1 && group.Children[0].Name == "…")
                    && !group.IsOpen)
                {
                    gitDirty = true;   // 拉取期间组被折叠：保留旧数据不打扰浏览态，置脏待下次展开整拉
                    return;
                }
                group.Children.Clear();
                if (commits.Count == 0)
                    group.Children.Add(new TreeNode
                    {
                        Name = "暂无提交记录（任务结束后自动提交）",
                        IsDir = false,
                        Level = group.Level + 1,
                        ColorHex = TreeDimColor,
                        Tip = "git 管理开启后（config.ini [Git] Enabled/AutoCommit=1）每轮任务结束自动提交变更；也可在 git 分组上右键刷新",
                    });
                else
                    foreach (var n in BuildGitRows(commits, files, group.Level + 1)) group.Children.Add(n);
                gitDirty = false;
            }, System.Windows.Threading.DispatcherPriority.Background);
        });
    }

    /// <summary>commit 行展开：后台拉取该提交变更文件清单（ShowStatAsync 合并状态与行数），懒加载占位被文件行替换。</summary>
    void LoadGitCommitFiles(TreeNode node)
    {
        if (node.Tag != "git-c" || node.GitHash.Length == 0) return;
        if (!gitBusyNodes.Add(node)) return;   // 加载中防重入
        var hash = node.GitHash;
        var cfg0 = cfg;
        _ = Task.Run(async () =>
        {
            var files = new List<GitFileStat>();
            try { files = await GitMgr.ShowStatAsync(cfg0, hash); } catch { }
            _ = Dispatcher.InvokeAsync(() =>
            {
                gitBusyNodes.Remove(node);
                if (node.Children.Count == 1 && node.Children[0].Name == "…")
                    node.Children.Clear();
                else if (node.Children.Count > 0)
                    return;   // 已有数据（理论上不会：懒加载只触发一次）：不覆盖
                if (files.Count == 0)
                    node.Children.Add(new TreeNode
                    {
                        Name = "（提交内无可读变更文件）",
                        IsDir = false,
                        Level = node.Level + 1,
                        ColorHex = TreeDimColor,
                    });
                else
                    foreach (var f in files) node.Children.Add(MakeGitFileNode(f, hash, node.Level + 1));
            }, System.Windows.Threading.DispatcherPriority.Background);
        });
    }

    /// <summary>commit → 树行：主行=[N轮] 任务摘要（去 tokens；无轮数手动提交原样截断），次行=短 hash · 相对时间 · 归属任务；
    /// 自动提交绿系/手动提交灰系；IsDir=true 展开懒加载文件清单（单击行 toggle 展开，双击不触发系统打开）。</summary>
    TreeNode MakeCommitNode(GitCommitInfo c, int level)
    {
        var node = new TreeNode
        {
            Tag = "git-c",
            GitHash = c.Hash,
            GitKey = c.Key,
            GitAuto = c.IsAuto,
            IsDir = true,
            Level = level,
            ColorHex = c.IsAuto ? TreeGitAutoColor : TreeGitManColor,
            Name = GitCommitTitle(c),
            Sub = c.Short + " · " + RelTimeText(c.When) + (c.Key != null ? " · 任务 " + c.Key : ""),
            Tip = (c.IsAuto ? "GAIRR 自动提交" : "手动提交") + (c.Key != null ? " · 任务 " + c.Key : "") + "\n"
                + c.Hash + "\n" + c.Message,
        };
        node.Children.Add(new TreeNode { Name = "…", IsDir = false, Level = level + 1 });   // 展开时懒加载文件清单
        return node;
    }

    /// <summary>任务组行（“项目跟踪”一级子行，Tag=git-g）：同 key 提交聚合（单轮 1 条也独立成组），组标题=组内最早一条提交的
    /// 任务摘要（=会话/编排计划/自动任务的目标标题），组名即“这组提交干了什么”。</summary>
    TreeNode MakeTaskGroupNode(string key, List<GitCommitInfo> items, int level)
    {
        var first = items[0];    // items 组内已按轮数升序：首条=任务起点（标题取起点目标，Sub 给条数/最近时间）
        var tn = new TreeNode
        {
            Tag = "git-g",
            GitKey = key,
            IsDir = true,
            IsOpen = true,   // 组默认展开：展开态接近旧版平铺，提交仍全部可见，仅多一层分组归属
            Level = level,
            ColorHex = TreeGitGroupColor,
            Name = TruncDots(GitTaskTitle(first), 44),
            Sub = items.Count + " 条提交 · 最近 " + RelTimeText(items[^1].When),
            Tip = "会话/任务归属 key=" + key + "（git log 反查标识）\n" + first.Hash + "\n" + first.Message,
        };
        foreach (var c in items) tn.Children.Add(MakeCommitNode(c, level + 1));
        return tn;
    }

    /// <summary>提交消息里的任务摘要（“N 轮 · M tokens :: 摘要”取“:: ”后段；手动提交无分隔返回整条）。</summary>
    static string GitTaskTitle(GitCommitInfo c)
    {
        var m = c.Message ?? "";
        var i = m.IndexOf(":: ", StringComparison.Ordinal);
        return i >= 0 ? m[(i + 3)..].Trim() : m.Trim();
    }

    /// <summary>commit 行主标题："[N轮] 任务摘要"（去 tokens；无轮数前缀的提交原样截断）。</summary>
    static string GitCommitTitle(GitCommitInfo c)
    {
        var m = c.Message ?? "";
        var mm = Regex.Match(m, @"^\s*(\d+)\s*轮");
        var t = GitTaskTitle(c);
        return mm.Success && t.Length > 0 ? TruncDots($"[{mm.Groups[1].Value}轮] {t}", 44) : TruncDots(m, 44);
    }

    /// <summary>提交消息头部的轮数（"N 轮 · …" 的 N；无轮数返回 -1，供任务组内按轮序排列）。</summary>
    static int GitRoundNo(GitCommitInfo c)
    {
        var m = c.Message ?? "";
        var mm = Regex.Match(m, @"^\s*(\d+)\s*轮");
        return mm.Success ? int.Parse(mm.Groups[1].Value) : -1;
    }

    /// <summary>“项目跟踪”提交列表分组构建：源码/文档提交按归属 key 聚合为任务组（同 key=同一会话/编排计划/自动任务；
    /// 单轮仅 1 条提交也独立成组，组标题=任务目标摘要），无归属的提交平铺；变更全是内部/产物文件的自动提交（讨论/只读任务后仅索引缓存变化）收纳进末尾灰组默认收起。
    /// 任务组与平铺行按各自最新提交时间混合倒序；纯内部收纳组固定在末尾。files=LogFilesAsync 路径表（hash→路径）。</summary>
    List<TreeNode> BuildGitRows(List<GitCommitInfo> commits, Dictionary<string, List<string>> files, int lv)
    {
        var byKey = new Dictionary<string, List<GitCommitInfo>>(StringComparer.Ordinal);
        var loose = new List<GitCommitInfo>();
        var internals = new List<GitCommitInfo>();
        foreach (var c in commits)
        {
            // 纯内部提交：自动提交 + 变更清单齐全 + 全部路径为内部/产物（清单缺失/手动提交保守按源码平铺展示）
            var fl = files.TryGetValue(c.Hash, out var v) ? v : null;
            if (c.IsAuto && fl != null && fl.Count > 0 && fl.All(GitMgr.IsInternalGitPath))
            {
                internals.Add(c);
                continue;
            }
            if (c.Key != null)
            {
                if (!byKey.TryGetValue(c.Key, out var list)) byKey[c.Key] = list = new();
                list.Add(c);
            }
            else
                loose.Add(c);
        }
        // 混合排序单位：任务组（最新提交时间）与无归属单条（提交时间）统一倒序
        var units = new List<(DateTimeOffset When, List<GitCommitInfo> Items)>();
        foreach (var g in byKey)
        {
            // 组内按轮数小→大排（无轮数的按时间兜底，沉在组尾）：任务第 1 轮在顶；树级锚点仍用最新提交时间
            var items = g.Value.OrderBy(x => { var r = GitRoundNo(x); return r < 0 ? int.MaxValue : r; })
                .ThenBy(x => x.When).ToList();
            units.Add((items[^1].When, items));
        }
        foreach (var c in loose) units.Add((c.When, new List<GitCommitInfo> { c }));
        units.Sort((a, b) => b.When.CompareTo(a.When));
        var rows = new List<TreeNode>();
        foreach (var u in units)
        {
            // 有归属 key 的提交（会话/编排计划/自动任务）一律生成任务组节点：单轮 1 条也独立成“会话标题分组”，
            // 让组标题（=任务目标摘要）与整组形态一致；仅无归属（手动提交/旧格式自动）才平铺 commit 行。
            var firstKey = u.Items[0].Key;
            if (firstKey == null)
            {
                rows.Add(MakeCommitNode(u.Items[0], lv));
                continue;
            }
            rows.Add(MakeTaskGroupNode(firstKey, u.Items, lv));
        }
        if (internals.Count > 0)
        {
            internals.Sort((a, b) => b.When.CompareTo(a.When));
            var g = new TreeNode
            {
                Tag = "git-g",
                IsDir = true,
                IsOpen = false,   // 默认收起：这些是纯索引/缓存/临时产物的噪音提交，不占视线
                Level = lv,
                ColorHex = TreeDimColor,
                Name = "仅内部/产物文件更新 ×" + internals.Count,
                Sub = "展开查看（历史提交：.gairr 索引、临时文件等变更）",
                Tip = "只改动内部/产物文件的自动提交：符号索引（symbols.json）、refs/state、temp 临时文件等；\n已解除跟踪，新提交不会再产生这类变更；历史记录保留可展开核对",
            };
            foreach (var c in internals) g.Children.Add(MakeCommitNode(c, lv + 1));
            rows.Add(g);
        }
        return rows;
    }

    /// <summary>commit 内变更文件 → 树行：主行=状态码+相对路径（截断），次行=行数统计（+增 -删）；行色按变更状态；
    /// 双击在右栏打开该文件本提交的 diff（单击选中不动）。</summary>
    TreeNode MakeGitFileNode(GitFileStat f, string hash, int level)
    {
        var color = f.Status switch
        {
            "A" => TreeGitAddColor,
            "M" => TreeGitModColor,
            "D" => TreeGitDelColor,
            _ when f.Status.StartsWith("R", StringComparison.Ordinal) => TreeGitRenColor,
            _ => TreeGitFileColor,
        };
        return new TreeNode
        {
            Tag = "git-f",
            GitHash = hash,
            IsDir = false,
            Level = level,
            ColorHex = color,
            Name = TruncDots(f.Status + " " + f.Path, 52),
            Sub = f.Ins + f.Del > 0 ? "+" + f.Ins + " -" + f.Del : "",
            Tip = f.Path + "\n" + (f.Status == "A" ? "新增" : f.Status == "D" ? "删除" : f.Status == "M" ? "修改"
                : f.Status.StartsWith("R", StringComparison.Ordinal) ? "重命名/移动" : "状态 " + f.Status)
                + " · +" + f.Ins + " -" + f.Del + " 行\n双击在右栏查看 diff",
        };
    }

    /// <summary>相对时间（commit 行次行小字）：刚刚 / x 分钟前 / x 小时前 / x 天前；超 7 天给月日。</summary>
    static string RelTimeText(DateTimeOffset when)
    {
        var d = DateTimeOffset.Now - when;
        if (d.TotalMinutes < 1) return "刚刚";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} 分钟前";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} 小时前";
        if (d.TotalDays < 7) return $"{(int)d.TotalDays} 天前";
        return when.LocalDateTime.ToString("MM-dd HH:mm");
    }

    /// <summary>提交信息剥离 auto@key 头（"auto@xxx N 轮…" → "N 轮…"）：log 已剥的记录原样返回。</summary>
    static string GitMsgBody(string msg)
    {
        if (msg.StartsWith("auto@", StringComparison.Ordinal))
        {
            var sp = msg.IndexOf(' ');
            if (sp >= 0) return msg[(sp + 1)..];
        }
        else if (msg.StartsWith("auto ", StringComparison.Ordinal)) return msg[5..];
        return msg;
    }

    /// <summary>提取提交信息里的归属任务 key（auto@&lt;key&gt;；无则 null）。</summary>
    static string? GitKeyOf(string msg)
    {
        if (msg.StartsWith("auto@", StringComparison.Ordinal))
        {
            var sp = msg.IndexOf(' ');
            if (sp >= 8) return msg[5..sp];
        }
        return null;
    }

    /// <summary>任务收尾自动提交成功后的左树同步（UI 线程）：源码/文档类变更 → 组已展开且已有数据时冒顶就地归入分组
    /// （带归属 key 的并入/新建“会话标题分组”，无 key 平铺 commit 行；按 hash 防重复）；纯内部/产物类提交（讨论/只读任务后仅索引缓存变化）不打扰浏览态，
    /// 置 gitDirty 待下次整拉自动归入“仅内部更新”收纳组。组未展开/懒占位/加载中 → 置 gitDirty。状态栏轻提示一次。</summary>
    void NotifyGitCommit(GitCommitOutcome oc, List<GitFileStat>? files)
    {
        var g = gitGroupNode;
        if (g == null || oc == null || !oc.Created || oc.Hash.Length == 0) return;
        // 纯内部提交：不冒顶插入（噪声），下次整拉（展开/右键刷新）时按变更路径分类自动进收纳灰组
        var internalOnly = files != null && files.Count > 0 && files.All(f => GitMgr.IsInternalGitPath(f.Path));
        if (internalOnly)
        {
            gitDirty = true;
            if (mStatus != null && oc.Hash.Length > 7)
                mStatus.Text = "git 已提交 " + oc.Hash[..7] + "（仅内部/产物变更，已收纳不打扰）";
            return;
        }
        var lazy = g.Children.Count == 0 || (g.Children.Count == 1 && g.Children[0].Name == "…");
        if (g.IsOpen && !gitBusyNodes.Contains(g))
        {
            if (lazy)
            {
                LoadGitGroup(g);   // 展开中但尚未拉过：整拉（新提交就在结果顶部）
                return;
            }
            if (g.Children.Any(n => n.GitHash == oc.Hash)
                || g.Children.Any(n => n.Tag == "git-g" && n.Children.Any(c => c.GitHash == oc.Hash)))
                return;   // 防重放：同一提交已展示（含组内）
            var info = new GitCommitInfo
            {
                Hash = oc.Hash,
                Short = oc.Hash.Length > 7 ? oc.Hash[..7] : oc.Hash,
                Message = GitMsgBody(oc.Message),
                IsAuto = true,
                Key = GitKeyOf(oc.Message),
                When = DateTimeOffset.Now,
            };
            // 就地插入与整拉(BuildGitRows)同语义：带归属 key 的提交归入“会话标题分组”。
            // 已存在同 key 组 → 组置顶并并入最新提交；无组 → 新建单条组（编排/会话单轮也独立成组）；无 key → 平铺 commit 行。
            var key = info.Key;
            TreeNode? grp = key != null
                ? g.Children.FirstOrDefault(n => n.Tag == "git-g" && n.GitKey == key)
                : null;
            if (grp != null)
            {
                var gi = g.Children.IndexOf(grp);
                if (gi != 0) { g.Children.RemoveAt(gi); g.Children.Insert(0, grp); }   // 最新提交的组冒顶
                grp.Children.Insert(0, MakeCommitNode(info, grp.Level + 1));          // 组内最新提交置顶
                grp.Sub = grp.Children.Count + " 条提交 · " + RelTimeText(info.When);
            }
            else if (key != null)
                g.Children.Insert(0, MakeTaskGroupNode(key, new List<GitCommitInfo> { info }, g.Level + 1));
            else
                g.Children.Insert(0, MakeCommitNode(info, g.Level + 1));
            gitDirty = false;
        }
        else
            gitDirty = true;   // 组折叠/占位/加载中：不打扰浏览态，下次展开整拉
        if (mStatus != null && oc.Hash.Length > 7)
            mStatus.Text = "git 已提交 " + oc.Hash[..7] + "（左侧“项目跟踪”可查看记录）";
    }

    void OnTreeNodeClick(object sender, MouseButtonEventArgs e)
    {
        // TreeViewItem 默认会在 MouseLeftButtonUp 冒泡阶段标记 Handled，导致 TreeView 上的
        // MouseLeftButtonUp 无法到达；改用 PreviewMouseLeftButtonUp 在隧道阶段先拦截。
        // 双击由两次单击合成，只在第一次单击时切换展开，避免展开后立刻收回来回弹。
        if (e.ClickCount != 1) return;

        // 沿可视树找到真正的节点元素；若路径上先碰到折叠箭头（ToggleButton），
        // 说明由 TreeViewItem 原生展开，直接返回避免双重翻转
        var el = e.OriginalSource as DependencyObject;
        FrameworkElement? nodeEl = null;
        while (el != null)
        {
            if (el is System.Windows.Controls.Primitives.ToggleButton) return;
            if (el is FrameworkElement fe && fe.DataContext is TreeNode) { nodeEl = fe; break; }
            el = VisualTreeHelper.GetParent(el);
        }
        if (nodeEl == null) return;
        var node = (TreeNode)nodeEl.DataContext;
        if (node.IsDir)
        {
            node.IsOpen = !node.IsOpen;   // 双向绑定驱动 TreeViewItem.IsExpanded
        }
        // 选中态切换
        SelectNode(node);
        e.Handled = true;
    }

    /// <summary>目录树双击：文件系统节点调用系统默认方式打开（文件→关联程序，目录→资源管理器）；
    /// git 变更文件行在右栏打开 diff；分组/commit 行无系统打开对象（单击已切换展开，双击不再动作）。</summary>
    void OnTreeNodeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var el = e.OriginalSource as DependencyObject;
        while (el != null)
        {
            if (el is FrameworkElement fe && fe.DataContext is TreeNode node)
            {
                SelectNode(node);   // 保持选中态与打开对象一致
                if (node.Tag == "git-f")
                {
                    OpenGitDiff(node);   // 变更文件行：右栏查看本提交该文件的 diff
                    e.Handled = true;
                    break;
                }
                if (node.Tag.Length > 0) { e.Handled = true; break; }   // 分组/commit 行：不做系统打开
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = node.FullPath,
                        UseShellExecute = true   // 走系统默认打开方式（关联程序/资源管理器）
                    });
                }
                catch (Exception ex)
                {
                    OnDocStatus("打开失败：" + ex.Message);
                }
                e.Handled = true;
                break;
            }
            el = VisualTreeHelper.GetParent(el);
        }
    }

    /// <summary>切换选中节点：与上一个选中项互斥，经 IsSelected 绑定驱动 TreeViewItem 高亮；
    /// .NET 8 选中触发 BringIntoView 引起的水平滚动恢复见 OnTreeSelectedChanged。</summary>
    void SelectNode(TreeNode node)
    {
        if (selectedNode != null) selectedNode.IsSelected = false;
        node.IsSelected = true;
        selectedNode = node;
    }

    // .NET 8 下 TreeViewItem 选中会触发 BringIntoView，把外层 panelDir 的水平滚动条随选中项左右拖走（多轮布局迭代）。
    // 恢复策略：选中瞬间记录当前水平偏移为目标，之后每轮 LayoutUpdated 都拉回目标，直到布局稳定（偏移==目标）或超轮数收手；
    // 垂直滚动不干预（上下仍自动跟随选中）；恢复期短（毫秒级）且不响应手工拖动（拖动不触发布局）。
    double? dirHKeepOffset;    // 需保持的水平偏移目标；null=无恢复任务
    int dirHKeepRounds;        // 已拉回轮数（超限放弃，防极端布局风暴）
    bool dirHKeepHooked;       // LayoutUpdated 挂载标记（防重复挂载）

    /// <summary>目录树选中变化（XAML SelectedItemChanged；覆盖单击/双击/右键/键盘导航/代码置选中）：
    /// 以选中前偏移为恢复目标，把随后 BringIntoView 引发的水平滚动拉回原位，默认横条停在左侧（用户位置）。</summary>
    void OnTreeSelectedChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        dirHKeepOffset = panelDir.HorizontalOffset;
        dirHKeepRounds = 0;
        if (!dirHKeepHooked)
        {
            dirHKeepHooked = true;
            LayoutUpdated += OnDirHKeepLayout;
        }
    }

    void OnDirHKeepLayout(object? s, EventArgs e)
    {
        if (dirHKeepOffset is not double t) return;   // 解挂前空转一轮
        if (++dirHKeepRounds > 16 || Math.Abs(panelDir.HorizontalOffset - t) < 0.01)
        {
            if (Math.Abs(panelDir.HorizontalOffset - t) >= 0.01) panelDir.ScrollToHorizontalOffset(t);   // 超轮数兜底拉回一次
            dirHKeepOffset = null;
            dirHKeepHooked = false;
            LayoutUpdated -= OnDirHKeepLayout;
        }
        else panelDir.ScrollToHorizontalOffset(t);
    }

    /// <summary>目录树鼠标滚轮：驱动外层 panelDir 滚动</summary>
    void OnTreeWheel(object sender, MouseWheelEventArgs e)
    {
        panelDir.ScrollToVerticalOffset(panelDir.VerticalOffset - e.Delta / 3.0);
        e.Handled = true;
    }

    /// <summary>会话树鼠标滚轮：TreeView 自带 ScrollViewer 会吞掉滚轮冒泡（内容超高时内部又滚不动），
    /// 在 Preview 阶段接管并驱动外层 panelHistory 滚动</summary>
    void OnSessionTreeWheel(object sender, MouseWheelEventArgs e)
    {
        panelHistory.ScrollToVerticalOffset(panelHistory.VerticalOffset - e.Delta / 3.0);
        e.Handled = true;
    }

    /// <summary>右键记录命中的树节点并将其切为选中态（供随后弹出的右键菜单判定与操作；
    /// 选中态与菜单操作对象保持一致，避免用户迷惑）。注意不要 Handled，否则 ContextMenu 不弹出</summary>
    void OnTreeRightDown(object sender, MouseButtonEventArgs e)
    {
        treeMenuNode = null;
        var el = e.OriginalSource as DependencyObject;
        while (el != null)
        {
            if (el is FrameworkElement fe && fe.DataContext is TreeNode tn) { treeMenuNode = tn; break; }
            el = VisualTreeHelper.GetParent(el);
        }
        if (treeMenuNode != null) SelectNode(treeMenuNode);
    }

    /// <summary>右键菜单打开：普通文件/目录=加入会话 + 自动维护文档（仅 .md 可见）；“项目跟踪”分组=刷新提交记录；
    /// 项目文件分组/git commit 行/变更文件行无可用操作，直接不弹菜单。</summary>
    void OnTreeMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (treeMenuNode == null) { e.Handled = true; return; }
        var tag = treeMenuNode.Tag ?? "";
        if (tag is "group-fs" or "git-c" or "git-f" or "git-g") { e.Handled = true; return; }   // 无右键操作的类型
        var isGitGroup = tag == "group-git";
        var hasDoc = !isGitGroup && TreeMenuDocRel() != null;
        menuGitRefresh.Visibility = isGitGroup ? Visibility.Visible : Visibility.Collapsed;
        menuAddToSession.Visibility = isGitGroup ? Visibility.Collapsed : Visibility.Visible;
        menuAddAutoDoc.Visibility = isGitGroup || !hasDoc ? Visibility.Collapsed : Visibility.Visible;
        menuRemoveAutoDoc.Visibility = menuAddAutoDoc.Visibility;
        menuSep1.Visibility = isGitGroup ? Visibility.Visible : Visibility.Collapsed;
        menuSep2.Visibility = hasDoc ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>右键“刷新提交记录”（挂在“项目跟踪”分组上）：展开中直接整拉重建（纳入新提交/外部变更）；
    /// 折叠中置 gitDirty，展开事件里再整拉。</summary>
    void OnGitRefreshClick(object sender, RoutedEventArgs e)
    {
        var g = treeMenuNode;
        if (g?.Tag != "group-git") return;
        if (g.IsOpen) LoadGitGroup(g);
        else gitDirty = true;
    }

    /// <summary>目录树右键"加入到会话"：把当前右键命中的目录/文件以 @file/@dir 标记插入输入框</summary>
    void OnTreeAddToSession(object sender, RoutedEventArgs e)
    {
        if (treeMenuNode == null) return;
        var node = treeMenuNode;
        var full = node.FullPath ?? "";
        var rel = cfg.ProjectRoot.Length > 0 && full.StartsWith(cfg.ProjectRoot, StringComparison.OrdinalIgnoreCase)
            ? full.Substring(cfg.ProjectRoot.Length).TrimStart('\\', '/').Replace('\\', '/') : full.Replace('\\', '/');
        InsertRefMarker(node.IsDir ? "@dir " + rel : "@file " + rel);
    }

    /// <summary>右键节点对应的文档 rel；目录/非 .md/.gairr 内（内置文档框架管理）返回 null</summary>
    string? TreeMenuDocRel()
    {
        if (treeMenuNode == null || treeMenuNode.IsDir || cfg.ProjectRoot.Length == 0) return null;
        var rel = System.IO.Path.GetRelativePath(cfg.ProjectRoot, treeMenuNode.FullPath).Replace('\\', '/');
        if (!rel.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return null;
        if (rel.StartsWith(".gairr/", StringComparison.OrdinalIgnoreCase)) return null;
        return rel;
    }

    /// <summary>加入/移出自动维护文档清单：就地更新命中节点的名称与颜色（属性通知驱动），
    /// 保留树的展开/选中状态，不重绘整棵树</summary>
    void OnToggleAutoDoc(object sender, RoutedEventArgs e)
    {
        var rel = TreeMenuDocRel();
        var node = treeMenuNode;
        if (rel == null || node == null) return;
        var add = sender == menuAddAutoDoc;
        OnDocStatus(ProjectMapAuto.ToggleAutoDoc(cfg, rel, add));
        autoDocSet = new HashSet<string>(ProjectMapAuto.GetAutoDocs(cfg), StringComparer.OrdinalIgnoreCase);   // 保持缓存与后续重绘一致
        var baseName = System.IO.Path.GetFileName(node.FullPath);
        node.Name = add ? baseName + " ⟳" : baseName;
        node.ColorHex = add ? TreeCfgColor : TreeDocColor;   // 菜单仅对 .md 弹出，移出后回归文档绿
    }

    /// <summary>项目切换：保存当前会话、清空对话区与缓存，然后重绘目录树并刷新历史列表</summary>
    void OnProjectChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            // 启动加载项目列表时会触发本事件，此时 sessionHistory 尚未 LoadSessionHistory，
            // 必须跳过，否则 SaveCurrentSession 会把空内存整体写回磁盘覆盖全部历史
            if (!sessionLoaded) return;
            if (projSel == null || tabDir == null) return;
            // 会话切换锁：锁定期（发送后→首个回复 token 到达前）切项目会中止刚发送未绑流的任务 → 不执行切换主体；
            // 若确为切走（选中项≠当前项目）则静默弹回原项目并提示；弹回产生的二次事件在此被静默吞掉（不执行主体、不成环）
            if (!SwitchEnabled)
            {
                var cur = projSel.SelectedItem as ProjectItem;
                if (cur != null && !string.Equals(cur.Path, cfg.ProjectRoot, StringComparison.OrdinalIgnoreCase))
                {
                    mStatus.Text = "AI 正在响应，首个回复生成前不能切换项目或会话";
                    var back = projSel.Items.OfType<ProjectItem>().FirstOrDefault(p =>
                        string.Equals(p.Path, cfg.ProjectRoot, StringComparison.OrdinalIgnoreCase));
                    if (back != null)
                        Dispatcher.BeginInvoke(new Action(() => { if (!ReferenceEquals(projSel.SelectedItem, back)) projSel.SelectedItem = back; }));
                }
                return;
            }
            // 切项目统一收口（本叶子）：先保存当前会话消息，再统一中止当前项目全部会话 Runner 并清空运行中枢（Hub 摘出）——
            // cfg.ProjectRoot 随后切到新项目，旧项目任何在跑任务（前台主对话/后台多模型/编排执行）都必须先行停止并摘出，
            // 否则会继续按新项目根读写文件（串写旧项目代码库）；运行中会话的最后工作消息按"已中止"收口写回归属会话历史
            // 落盘（切回原项目打开历史即见中断快照，可续聊）。孤儿线程随后发来的收口事件因记录已摘除、被 drain 归属
            // 守卫丢弃，不触碰新项目会话。runTasks 已清空，下方一律走原全量清理（清消息区/复位发送态）。
            if (projSel.SelectedItem is ProjectItem p)
            {
                SaveCurrentSession();
                ShutdownAllSessionRunners();
                RefreshPendingBadges();   // 切项目：runTasks 已清空，旧会话角标本轮全部归 0，不残留脏角标
                ClearChatState();

                cfg.ProjectRoot = p.Path;   // 同步到配置
                AppConfig.ProjectRootStatic = p.Path;
 RefreshAppLogo(); // 左上 logo 同步新项目名称 // 同步静态根目录（供 Markdown 链接用）  // 同步静态根目录（供 Markdown 链接用）
                _sessionGroupShown.Clear();   // 切项目分页回到默认一页：新项目历史从最近 10 条起展示
                RenderTree(p.Path);
                // 切换项目：后台自动检查/生成该项目的地图/图谱/注释/文档（受 MapAuto 工具开关控制）
                if (registry.IsEnabled("MapAuto")) ProjectMapAuto.Schedule(cfg, OnDocStatus);
                // 切项目：会话区进入空白待发起态（不预建占位“新会话”项，避免左侧列表凭空多一条空会话），
                // 首条消息发送时才建会话项，用户在新项目下依旧有明确会话入口（提示消息 + 输入即建）
                BeginNewSessionAfterProjectSwitch();
                RefreshSessionList();
                // 切换项目时刷新左侧自动任务列表（任务随项目隔离）；选中态为旧项目实例，需重置
                taskScheduler?.LoadTasks();
                SetSelectedTask(null);
                ActivateTab(tabDir);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Error] 切换项目失败: {ex.Message}");
        }
    }

    /// <summary>清空当前对话区内容及相关运行期缓存/变量</summary>
    void ClearChatState()
    {
        // 取消可能正在运行的任务
        if (cts != null)
        {
            cts.Cancel();
            cts.Dispose();
            cts = null;
        }

        workMsg = null;
        pendingStream = null;      // 清掉打字机积压队列，防止残留文本串到新任务
        streamTicker?.Stop();
        cardMap.Clear();
        stepCardMap.Clear();
        accTokens = 0;
        taskActive = false;
        lastTask = "";
        currentTodo = null;
        dangerCard = null;
        pendingSessionTitle = null;
        sessionTitleSet = false;
        currentSession = null;
        DropPendingSession();   // 离开会话/切项目：未发送过首条消息的占位「新会话」一并摘除，列表不残留空项
        qCardSeq = 0;   // 退出当前会话：澄清问题序号随之复位（新会话从 1 重新编号）
        RefreshTodoBar();
        UpdateChatTopInfo();

        // 清空右侧会话区（本方法仅在离开会话无任何在跑任务时到达）
        // 本叶子：上下文随会话 Runner 归各会话，此处无需也不得重置任何 Runner 的 history
        ResetMsgView();   // 连分段加载暂存一起复位：不留上一会话的待装配历史
        SyncViewportFlags();   // 视口已空（currentSession=null）：全部 Runner 置非视口，切走/放行的后台挂起转短静默补发
    }

    /* ---------- 项目地图自动化：进度、取消、按钮状态 ---------- */

    System.Diagnostics.Stopwatch? mapSw;
    System.Windows.Threading.DispatcherTimer? mapProgressTimer;
    int mapPercent;
    bool mapProgressActive;    // 按钮当前是否处于橙色“中止”模式
    bool rebuildMode;          // 当前任务类型：true=手动“重建地图”（按钮触发），false=自动“增补地图”（启动/切项目/工具调度）
    bool manualRebuildActive;  // 手动重建任务全程（含 RunAsync 前的准备阶段）：期间轮询不得误停进度，避免 rebuildMode 被重置成“增补”

    string MapOpLabel => rebuildMode ? "重建" : "增补";

    /// <summary>项目地图自动化进度回调（后台线程调用）：写入 agent.log、经 DocStatus 事件转发左侧后台状态栏（bgStatus）并更新按钮进度。</summary>
    void OnDocStatus(MapProgress p)
    {
        try
        {
            System.IO.File.AppendAllText(
                GAIRR.Core.Paths.AgentLog,
                $"[{DateTime.Now:HH:mm:ss.fff}] [ProjectMap][{MapOpLabel}] {p.Percent}% · {p.Message}{Environment.NewLine}",
                System.Text.Encoding.UTF8);
        }
        catch { }
        mapPercent = p.Percent;
        uiBus.Post(new UiEvent { Type = UiEventType.DocStatus, LogLine = $"{p.Percent}% · {p.Message}" });   // 项目地图为会话外宿主事件：经 uiBus 汇入 UI drain
    }

    /// <summary>兼容旧版文本回调：包装为 MapProgress（不更新百分比）。</summary>
    void OnDocStatus(string msg) => OnDocStatus(new MapProgress { Percent = mapPercent, Message = msg });

    void StartMapProgressUi()
    {
        mapPercent = 0;
        mapProgressActive = true;
        mapSw = System.Diagnostics.Stopwatch.StartNew();
        mapBtnProgressText.Text = $"{MapOpLabel}：0% 0.0s";
        rebuildMapBtn.Foreground = (System.Windows.Media.Brush)FindResource("AmberBrush");
        rebuildMapBtn.BorderBrush = (System.Windows.Media.Brush)FindResource("AmberBrush");
        rebuildMapBtn.ToolTip = $"点击中止当前{MapOpLabel}";
        mapProgressTimer?.Stop();
        mapProgressTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        mapProgressTimer.Tick += (_, _) => RefreshMapButton();
        mapProgressTimer.Start();
    }

    void StopMapProgressUi()
    {
        mapProgressActive = false;
        mapProgressTimer?.Stop();
        mapProgressTimer = null;
        if (!manualRebuildActive) rebuildMode = false;   // 恢复默认“增补”（自动调度）；手动重建全程中不重置，避免被误停后丢标签
        mapBtnProgressText.Text = "重建地图";
        rebuildMapBtn.Foreground = (System.Windows.Media.Brush)FindResource("GreenBrush");
        rebuildMapBtn.BorderBrush = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#7A8C9B"));
        rebuildMapBtn.ToolTip = "全量重建地图缓存与符号索引（清除派生缓存后重建，并检查标签合规）";
    }

    void RefreshMapButton()
    {
        var elapsed = mapSw?.Elapsed ?? TimeSpan.Zero;
        var time = $"{elapsed.TotalSeconds:F1}s";
        if (!mapBtnHover)
            mapBtnProgressText.Text = $"{MapOpLabel}：{mapPercent}% {time}";
    }

    bool mapBtnHover;   // 鼠标悬停时按钮显示“中止{类型}”而非进度

    void MapBtnMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!mapProgressActive) return;
        mapBtnHover = true;
        mapBtnProgressText.Text = $"中止{MapOpLabel}";
    }

    void MapBtnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        mapBtnHover = false;
        if (mapProgressActive) RefreshMapButton();   // 恢复进度文案
    }

    /// <summary>同步按钮状态：自动/手动重建开始后切橙色，结束后恢复绿色并停止计时。</summary>
    void SyncMapButtonState()
    {
        if (ProjectMapAuto.IsRunning && !mapProgressActive)
            StartMapProgressUi();
        else if (!ProjectMapAuto.IsRunning && mapProgressActive && !manualRebuildActive)
            StopMapProgressUi();   // 手动重建的准备阶段（RunAsync 未启动）不视为结束，避免误停丢失“重建”标签
    }


    /// <summary>“全部收缩”按钮（重建地图左侧，- 号）点击：折叠项目跟踪树全部节点到根分组。</summary>
    void OnCollapseAll(object sender, RoutedEventArgs e) => CollapseTree(treeBox);

    /// <summary>递归折叠 ItemsControl 树全部可见节点：父收起后未展开子树的容器本就未生成，无需再下钻。</summary>
    static void CollapseTree(ItemsControl root)
    {
        foreach (var item in root.Items)
        {
            if (root.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem tvi)
            {
                tvi.IsExpanded = false;
                CollapseTree(tvi);
            }
        }
    }

    /// <summary>点击侧栏“重建地图/停止更新”按钮：运行时取消，否则清除派生缓存并全量重建（符号索引+地图缓存+标签合规检查）。</summary>
    void OnRebuildMap(object sender, RoutedEventArgs e)
    {
        if (ProjectMapAuto.IsRunning)
        {
            ProjectMapAuto.Cancel();
            OnDocStatus("正在停止项目地图更新…");
            return;
        }
        rebuildMode = true;          // 标记本次为“重建”（清除式全量）；OnDocStatus/按钮文案据此显示类型
        manualRebuildActive = true;  // 锁定全程：准备阶段（RunAsync 未启动）期间防止轮询误停、标签被重置
        StartMapProgressUi();
        RunRebuildMapTask();
    }

    /// <summary>在后台线程运行项目地图重建，并在完成后恢复按钮状态。</summary>
    async void RunRebuildMapTask()
    {
        try
        {
            ProjectMap.ClearCache(cfg);   // 删除 .gairr/map.json（自动产物，随后全量重建）
            OnDocStatus("正在重建符号索引…");
            await Task.Run(() => SymbolIndex.Refresh(cfg));   // 删 symbols.json 并全量重建（剔除已删符号、过时中文标签）
            OnDocStatus("正在更新项目地图…");
            await ProjectMapAuto.RunAsync(cfg, OnDocStatus, rebuildDocs: true);   // 重建时顺带刷新自动维护文档清单（内置+用户标记）
            OnDocStatus("正在检查标签合规…");
            var report = await Task.Run(() => SymbolIndex.ComplianceReport(cfg));
            OnDocStatus(report);   // 写 agent.log 并在状态栏显示缺文件头/方法注释清单
        }
        finally { manualRebuildActive = false; StopMapProgressUi(); }   // 全程结束：复位标志并恢复按钮为“重建地图”
    }

    /* ================= 左侧栏标签页切换 ================= */

    void OnTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button b) ActivateTab(b);
    }

    // ===== 编排计划（1-4 执行闭环）=====
    GAIRR.AgentHost.PlanRunner? planRunner   // 当前运行中的执行器：UI 暂停/继续/停止（存所属会话 TaskRecord.PlanRunner，两会话并行各一份）
    {
        get => CurRec?.PlanRunner;
        set { var r = CurRec ?? (currentSession != null ? EnsureRec(currentSession) : null); if (r != null) r.PlanRunner = value; }
    }
    SessionItem? orchOwner   // 编排执行归属会话（后台消费判定用：危险确认分流/右栏执行页常驻/收口提示）；执行结束置空；存所属会话 TaskRecord.OrcOwner
    {
        get => CurRec?.OrcOwner;
        set { var r = CurRec ?? (currentSession != null ? EnsureRec(currentSession) : null); if (r != null) r.OrcOwner = value; }
    }
    string? selPlanId;              // 编排面板选中计划 id（右键命中节点的整个计划 id）
    string? selLeafId;              // 右键命中的叶子/分组节点 id（标记通过/跳过/删除分支用）；命中根节点时为 null
    string? lastOrchTreeSig;        // 编排执行树状态指纹（状态变化才重建，避免因高亮亮闪）
    System.Windows.Threading.DispatcherTimer? orcRefreshTimer;   // 编排执行期间的树状态轮询刷新
    // 编排右栏宽度记忆：>0=用户 GridSplitter 拖过的宽度（恢复时优先）；0=从未拖过 → 面板默认按会话区 1/3 展开（OrcPanelW）
    double orcUserWidth = 0;
    // ─── 编排执行顶部常驻（窗口级快照，不依赖 CurRec 代理：执行中可切到其它会话只读查看，切走后 planRunner 代理为 null）───
    bool orcExecActive;            // 编排执行进行中：chatTopBar 常驻顶部，计划行/用时/叶子行每秒同步（需求 1-3）
    string orcExecStage = "";     // 当前叶子阶段：executing|reviewing|paused（PlanRunner 日志/ReviewStageChanged 驱动）
    string orcExecPlanId = "";    // 执行中计划 id（OrcStatusRefresh 从 plan.json 聚合进度用）
    string orcExecPlanTitle = ""; // 执行中计划标题（顶部计划行）
    int orcExecLeafDone;           // 已完成叶子数（passed/skipped，状态轮询聚合）
    int orcExecLeafTotal;          // 计划叶子总数（顶部编排计划行"已完成 n/总数"用）
    string orcExecLeaf = "";      // 当前叶子标题（顶部叶子行）
    DateTime orcExecStart;         // 编排启动时刻（顶部"用时"计时基准）
    string? topUserSig;            // 顶部用户区已渲染消息指纹：变化才重建气泡（防 100ms 高频重建闪烁）
    System.Windows.Threading.DispatcherTimer? toastTimer;   // 轻量提示浮层倒计时（3 秒自动消失）
    CancellationTokenSource? multiGenCts   // 多模型决策生成（PlanNegotiator 后台任务）取消源：生成中锁输入，停止钮点击取消；随所属会话 TaskRecord.MultiGenCts
    {
        get => CurRec?.MultiGenCts;
        set { var r = CurRec ?? (currentSession != null ? EnsureRec(currentSession) : null); if (r != null) r.MultiGenCts = value; }
    }

    void ActivateTab(Button target)
    {
        if (target != tabDir && gitDiffOpen) CloseGitDiff();   // 提交详情浮层锚定“项目”页内树：切走即收，防浮层悬空
        foreach (var t in new[] { tabDir, tabArch, tabTask, tabHistory })
            t.Tag = t == target ? "cur" : null;
        panelDirRoot.Visibility = target == tabDir ? Visibility.Visible : Visibility.Collapsed;
        panelArchRoot.Visibility = target == tabArch ? Visibility.Visible : Visibility.Collapsed;
        panelTaskRoot.Visibility = target == tabTask ? Visibility.Visible : Visibility.Collapsed;
        panelHistoryRoot.Visibility = target == tabHistory ? Visibility.Visible : Visibility.Collapsed;
        panelSkillRoot.Visibility = target == tabSkill ? Visibility.Visible : Visibility.Collapsed;
        if (target == tabArch) LoadArchTree();   // 进入架构页签时按当前项目重新加载
    }

    /// <summary>加载当前项目的功能架构树（.gairr/architecture.json）并渲染到架构面板。
    /// 编排树已迁移到右侧编排执行 Tab，架构栏仅显示功能架构。</summary>
 void LoadArchTree(string? rootOverride = null)
 {
 var root = string.IsNullOrWhiteSpace(rootOverride) ? cfg.ProjectRoot : rootOverride;
 var arch = ArchTreeStore.Load(root);
        if (arch == null && archStats == null) return;
        var funcNodes = arch?.Roots.Where(r => r.Kind == "func").ToList() ?? new List<ArchNode>();
        var func = funcNodes.FirstOrDefault();
        archStats.Text = func != null
            ? $"功能架构 · 最小独立功能完成 {func.Done}/{func.Total}" +
              (string.IsNullOrEmpty(arch!.Updated) ? "" : $" · 更新于 {arch.Updated}")
            : "暂无架构文件（.gairr/architecture.json）";
        if (funcNodes.Count == 0)
        {
            treeArch.ItemsSource = null;
            return;
        }
        // 功能架构分组根
        var funcGroup = new ArchNode
        {
            Name = "功能架构",
            Kind = "group",
            IsOpen = _archGroupCollapsed.Contains("func") ? false : true,
            NodePath = "功能架构",
            Depth = 0,
        };
        funcGroup.Children = funcNodes;
        var roots = new List<ArchNode> { funcGroup };
        treeArch.ItemsSource = roots;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (treeArch.ItemsSource is List<ArchNode> rs)
                foreach (var r in rs.Where(x => x.Kind == "group"))
                    UpdateGrpArrowByText(r, r.IsOpen ? "▾" : "▸");
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>按 ArchNode 查找对应 TreeViewItem 并更新箭头。</summary>
    void UpdateGrpArrowByText(ArchNode node, string arrow)
    {
        foreach (var r in treeArch.Items)
        {
            var tvi = treeArch.ItemContainerGenerator.ContainerFromItem(r) as TreeViewItem;
            if (tvi?.DataContext == node)
            { UpdateGrpArrow(tvi, arrow); break; }
        }
    }

    /// <summary>架构栏分组折叠状态（点击分组标题切换）。</summary>
    readonly HashSet<string> _archGroupCollapsed = new();
    /// <summary>架构栏分组手动展开记录（防止有编排时功能架构被自动收起后无法手动展开）。</summary>
    readonly HashSet<string> _archGroupExpanded = new();

    /// <summary>架构树节点展开事件：追踪分组节点的展开状态，更新右侧箭头。</summary>
    void OnArchNodeExpanded(object sender, RoutedEventArgs e)
    {
        if (sender is TreeViewItem tvi && tvi.DataContext is ArchNode n && n.Kind == "group")
        {
            var key = n.Name == "功能架构" ? "func" : "orch";
            _archGroupCollapsed.Remove(key);
            _archGroupExpanded.Add(key);
            UpdateGrpArrow(tvi, "▾");
        }
    }

    /// <summary>架构树节点收起事件：追踪分组节点的折叠状态，更新右侧箭头。</summary>
    void OnArchNodeCollapsed(object sender, RoutedEventArgs e)
    {
        if (sender is TreeViewItem tvi && tvi.DataContext is ArchNode n && n.Kind == "group")
        {
            var key = n.Name == "功能架构" ? "func" : "orch";
            _archGroupCollapsed.Add(key);
            _archGroupExpanded.Remove(key);
            UpdateGrpArrow(tvi, "▸");
        }
    }

    /// <summary>更新分组节点右侧箭头文本（▾/▸）。</summary>
    static void UpdateGrpArrow(TreeViewItem tvi, string arrow)
    {
        // 在 Header 的可视化树中查找名为 grpArrow 的 TextBlock
        var header = tvi.Template.FindName("PART_Header", tvi) as FrameworkElement;
        // PART_Header 不一定存在，直接遍历子元素查找
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(tvi); i++)
        {
            if (FindTextBlockByName(VisualTreeHelper.GetChild(tvi, i), "grpArrow") is TextBlock tb)
            { tb.Text = arrow; break; }
        }
    }

    /// <summary>递归查找指定名称的 TextBlock。</summary>
    static TextBlock? FindTextBlockByName(DependencyObject obj, string name)
    {
        if (obj is TextBlock tb && tb.Name == name) return tb;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++)
        {
            var found = FindTextBlockByName(VisualTreeHelper.GetChild(obj, i), name);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>普通架构仅功能根时据此统计：编排根数量变化时调用本方法刷新（用于新建/删除编排根后）。</summary>
    void ReloadArch() => LoadArchTree();

    /// <summary>架构树定位：把 nodePath 所在链逐级展开并选中滚动可见（新增功能子叶后的落点提示）。
    /// 容器展开由 ItemContainerStyle 的 IsExpanded←IsOpen 双向绑定驱动：先改数据 IsOpen（未生成容器取新值），
    /// 已生成的容器再直接置 IsExpanded 并用 UpdateLayout 强制实现化下一层，最后选中目标行。</summary>
    void RevealArchPath(string nodePath)
    {
        var roots = treeArch.ItemsSource as List<ArchNode>;
        var grp = roots?.FirstOrDefault(x => x.Kind == "group");
        if (grp == null || string.IsNullOrEmpty(nodePath)) return;
        // 路径逐段推进收集祖先链；任一段在所有功能根上找不到即放弃（定位失败不影响数据）
        var chain = new List<ArchNode>();
        var path = "";
        foreach (var seg in nodePath.Split('/'))
        {
            if (seg.Length == 0) return;
            path = path.Length == 0 ? seg : path + "/" + seg;
            var hit = grp.Children.Select(r => r.FindByPath(path)).FirstOrDefault(n => n != null);
            if (hit == null) return;
            chain.Add(hit);
        }
        foreach (var node in chain) node.IsOpen = true;
        grp.IsOpen = true;                              // 分组被收起时也强制展开（定位动作优先于折叠偏好）
        _archGroupCollapsed.Remove("func");
        var cur = treeArch.ItemContainerGenerator.ContainerFromItem(grp) as TreeViewItem;
        if (cur == null) { treeArch.UpdateLayout(); cur = treeArch.ItemContainerGenerator.ContainerFromItem(grp) as TreeViewItem; }
        if (cur == null) return;
        cur.IsExpanded = true;
        cur.UpdateLayout();
        UpdateGrpArrow(cur, "▾");                      // 分组展开后箭头文本即时对齐（模板初值 ▾，收起过则为 ▸）
        TreeViewItem? leaf = null;
        for (var i = 0; i < chain.Count; i++)
        {
            TreeViewItem? child = null;
            foreach (var item in cur.Items)
            {
                var tvi = cur.ItemContainerGenerator.ContainerFromItem(item) as TreeViewItem;
                if (tvi?.DataContext == chain[i]) { child = tvi; break; }
            }
            if (child == null) { cur.UpdateLayout(); child = null; foreach (var item in cur.Items) { var tvi = cur.ItemContainerGenerator.ContainerFromItem(item) as TreeViewItem; if (tvi?.DataContext == chain[i]) { child = tvi; break; } } }
            if (child == null) break;
            if (i < chain.Count - 1) { child.IsExpanded = true; child.UpdateLayout(); cur = child; }
            else leaf = child;
        }
        if (leaf != null)
        {
            leaf.IsSelected = true;
            leaf.BringIntoView();
        }
    }

    /* ================= 架构树右键：在此功能新建编排任务（L0 目标=节点名+路径） ================= */

    ArchNode? archMenuNode;   // 右键命中的架构节点（OnArchMenuOpening 记录，Click 处理消费）

    /// <summary>沿鼠标位置命中架构树某一行对应的 ArchNode（未命中行返回 null）。</summary>
    ArchNode? ArchNodeAtMouse()
    {
        // ContextMenuEventArgs 没有 GetPosition（那是 MouseEventArgs 的方法），统一用 Mouse.GetPosition 取当前鼠标坐标
        var hit = VisualTreeHelper.HitTest(treeArch, Mouse.GetPosition(treeArch));
        for (var d = hit?.VisualHit; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is TreeViewItem tvi && tvi.DataContext is ArchNode n) return n;
        return null;
    }

    /// <summary>架构树右键菜单打开前：记录命中节点。菜单项启停统一在 OnArchMenuOpened（已知节点后）处理。</summary>
    void OnArchMenuOpening(object sender, ContextMenuEventArgs e)
    {
        archMenuNode = ArchNodeAtMouse();
    }

    /// <summary>架构树右键菜单展开：按命中节点来源（功能/编排 根-分组-叶子）动态启停菜单项；
    /// 各段（更新/新建/执行/判定/删除/叶子会话）定稿后统一校准分隔线，整段隐藏时不残留孤立分隔线。</summary>
    void OnArchMenuOpened(object sender, RoutedEventArgs e)
    {
        var n = archMenuNode ?? treeArch.SelectedItem as ArchNode;
        var isGroup = n?.Kind == "group";
        var isPlan = n?.Kind == "plan";
        // 更新架构：恒显示（分组/功能根/编排根均可），仅空命中时禁用
        menuArchUpdate.IsEnabled = n != null;
        // 新建编排：分组隐藏；功能树任意节点可用——叶子=直接关联本功能；根/分支=方案确认后自动在其下新增以标题命名的功能子叶并关联
        menuArchCreateOrchestration.Visibility = isGroup ? Visibility.Collapsed : Visibility.Visible;
        menuArchCreateOrchestration.IsEnabled = n != null && !isPlan;
        menuArchCreateOrchestration.Header = menuArchCreateOrchestration.IsEnabled
            ? $"在“{n!.Name}”新建编排任务"
            : "在此功能新建编排任务";
        menuArchCreateOrchestration.ToolTip = n != null && !isPlan && !n.IsLeaf
            ? "以本分支为目标新建编排任务；方案确认后，自动在分支下新增以编排标题命名的功能子叶并建立关联。"
            : "以本功能为目标新建编排任务；方案确认后，把编排关联写回该功能节点。";
        // 编排执行控制：命中编排节点时，把 selPlanId 设为其所属编排计划
        bool isPlanHit = isPlan && n != null;
        if (isPlanHit)
        {
            var pid = ArchPlanIdOf(n!);
            if (pid != null) selPlanId = pid;
        }
        ShowPlanMenuItems(isPlanHit);
        var pr = isPlanHit && selPlanId != null ? GAIRR.AgentHost.PlanStore.GetById(cfg.ProjectRoot, selPlanId) : null;
        var running = planRunner != null;
        menuArchPlanStart.Header = "开始执行";
        menuArchPlanStart.IsEnabled = pr != null && pr.Status is "approved" or "paused" or "failed";
        menuArchPlanPause.IsEnabled = running && pr?.Status == "running";
        menuArchPlanResume.IsEnabled = running || pr?.Status == "paused";
        menuArchPlanStop.IsEnabled = running;
        menuArchPlanDelete.IsEnabled = pr != null;
        var canJudge = pr != null && pr.Nodes.Any(x => x.Status == "failed");
        menuArchPlanPass.IsEnabled = canJudge;
        menuArchPlanSkip.IsEnabled = canJudge;
        // 查看叶子会话：仅编排叶子且有 sessionRunId 时启用
        bool leafSession = n != null && n.Kind == "plan" && n.NodeType == 1 && !string.IsNullOrEmpty(n.SessionRunId);
        menuArchViewLeafSession.Visibility = leafSession ? Visibility.Visible : Visibility.Collapsed;
        menuArchViewLeafSession.IsEnabled = true;
        RefreshArchMenuSeparators();
    }

    /// <summary>按各段可见项校准 5 条分隔线：菜单按分隔线划为 6 段（更新架构/新建编排/执行控制/标记判定/删除编排/叶子会话），
    /// 分隔线仅当紧邻两侧段都至少含一个可见项时显示——分组节点只剩“更新架构树”、功能叶子无编排段等整段隐藏后不残留。</summary>
    void RefreshArchMenuSeparators()
    {
        var segs = new[]
        {
            new[] { menuArchUpdate },
            new[] { menuArchCreateOrchestration },
            new[] { menuArchPlanStart, menuArchPlanPause, menuArchPlanResume, menuArchPlanStop },
            new[] { menuArchPlanPass, menuArchPlanSkip },
            new[] { menuArchPlanDelete },
            new[] { menuArchViewLeafSession },
        };
        var seps = new[] { menuArchSep1, menuArchSep2, menuArchSep3, menuArchSep4, menuArchSep5 };
        for (var i = 0; i < seps.Length; i++)
            seps[i].Visibility = segs[i].Any(s => s.Visibility == Visibility.Visible)
                                 && segs[i + 1].Any(s => s.Visibility == Visibility.Visible)
                ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>编排树全部子节点的菜单项统一显隐。</summary>
    void ShowPlanMenuItems(bool show)
    {
        foreach (var it in new[] { menuArchPlanStart, menuArchPlanPause, menuArchPlanResume, menuArchPlanStop,
                                   menuArchPlanPass, menuArchPlanSkip, menuArchPlanDelete })
            it.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>取命中编排节点所属编排根（Kind=plan 顶层）的 plan id。
    /// 沿 items 根逐一检查命中节点是否为其子孙；不是编排根（命中节点自身即 plan 根）则取自身 PlanId。</summary>
    string? ArchPlanIdOf(ArchNode n)
    {
        if (n.Kind != "plan") return null;
        if (n.PlanId != null && treeArch.ItemsSource is List<ArchNode> roots)
        {
            // 遍历所有顶层节点（含分组节点）查找命中节点所属的编排根
            foreach (var r in roots)
            {
                if (r.Kind == "plan" && ReferenceEquals(r, n)) return r.PlanId;
                if (r.Kind == "group")
                {
                    foreach (var c in r.Children.Where(x => x.Kind == "plan"))
                    {
                        if (ReferenceEquals(c, n)) return c.PlanId;
                        if (ContainsNode(c, n)) return c.PlanId;
                    }
                }
                else if (ContainsNode(r, n)) return r.PlanId;
            }
        }
        return n.PlanId;
    }

    /// <summary>root 及其子孙中是否存在 target（含 root 自身）。</summary>
    static bool ContainsNode(ArchNode root, ArchNode target)
    {
        if (ReferenceEquals(root, target)) return true;
        return root.IsLeaf ? false : root.Children.Any(c => ContainsNode(c, target));
    }

    /// <summary>更新架构树：重新加载功能架构（.gairr/architecture.json）并刷新显示（静默操作，不向会话区输出气泡）。</summary>
    void OnArchUpdate(object sender, RoutedEventArgs e)
    {
        LoadArchTree();
    }

    /// <summary>删除编排根：仅从架构栏移除视图（恢复显示时下次 LoadArchTree 重新投影），不删 plan.json 数据。</summary>
    void OnArchPlanDelete(object sender, RoutedEventArgs e)
    {
        if (selPlanId == null) return;
        if (planRunner != null)
        {
            MessageBox.Show(this, "正在执行中，请先停止再删除编排视图", "删除编排视图");
            return;
        }
        if (MessageBox.Show(this, "仅移除架构栏中的该编排视图（plan.json 数据保留，可重新加载）。删除？", "删除编排视图",
                            MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        _archHiddenPlans ??= new HashSet<string>();
        _archHiddenPlans.Add(selPlanId);
        var deletedPlanId = selPlanId;
        selPlanId = null;
        // 删除编排视图后，若当前会话是该编排的源会话，重置为 collecting 以便重新生成
        if (currentSession?.IsOrchestration == true && currentSession.PlanId == deletedPlanId)
        {
            SyncOrch(currentSession, "collecting");            currentSession.OrchPhase = "collecting";   // 仅复位本会话 Runner 的框架阶段
            SaveCurrentSession();
            UpdateOrcPanel();
        }
        LoadArchTree();
    }

    /// <summary>被用户“删除视图”隐藏的编排计划 id（本次会话内有效；重新进入架构页签仍隐藏）。</summary>
    HashSet<string>? _archHiddenPlans;

    /// <summary>右键功能树节点（叶子/根/分支）新建编排会话（阶段 3）：弹窗预填标题与 L0 目标并选生成方案，archRef 锚定架构节点；
    /// 交互式收集 → 生成任务树 / 多模型决策 → 确认后回写关联（叶子直写；根/分支自动新增以标题命名的功能子叶）、跳转编排页签等待手工执行。</summary>
    void OnArchCreateOrchestration(object sender, RoutedEventArgs e)
    {
        var node = archMenuNode ?? treeArch.SelectedItem as ArchNode;
        archMenuNode = null;
        if (node == null) return;
        // 两会话并行 + 厂商并发闸：本会话前台主任务（cts）运行中同样允许新建编排会话——切走后原任务照跑
        // （验收7 后台收口直写其会话记录）；新编排会话的交互收集/产树各自建 Runner 并行，多会话占位由
        // Agent 层 LlmConcurrencyGate 按厂商并发上限把关（占满时新任务启动即被拒并提示），口径同 OnNewSession/OnSessionClick
        if (cfg.ProjectRoot.Length == 0)
        {
            MessageBox.Show(this, "请先选择项目再创建编排会话", "新建编排会话");
            return;
        }
        // 预填：标题=节点名（可改为目标功能名，方案确认后即作为新增子叶名）；目标=叶子直接实现该功能，分支/根=在范围内规划新功能
        var dlg = new OrchSessionInputDialog(node.Name,
            node.IsLeaf
                ? $"实现功能“{node.Name}”（架构节点路径：{node.NodePath}）"
                : $"在功能“{node.Name}”下规划并实现一个新功能（架构节点路径：{node.NodePath}；方案确认后将自动以编排标题新增功能子叶）",
            KeyedModels()) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        NewOrchestrationSession(dlg.ResultTitle, dlg.ResultGoal, node.NodePath, dlg.ResultMode, dlg.ResultModels, dlg.ResultMaterials);
    }

    /* ================= 模型切换：单一厂商-模型下拉列表 ================= */

    string ProviderLabel() => $"{cfg.ProviderDisplayName(CurProvider)} {CurModel}";   // 本叶子：UI 当前模型以 cfg 权威源为准（CurProvider/CurModel），不再依赖全局 loop

    // L1 状态行不再显示"在线"与版本号（需求：名称后只留名称本身）；
    // mStatus 仅作临时提示载体，空闲时清空即隐藏
    void UpdateStatus() => mStatus.Text = "";

    /* ================= 思考控件（标题栏，模型下拉右侧；形态由 [ModelThinking] 规则决定） ================= */

    bool suppressThinkingSel;   // 程序化刷新等级下拉时屏蔽 SelectionChanged

    /// <summary>按当前模型的思考规则刷新控件形态与状态：等级模式显示下拉（档位来自配置 levels），
    /// 开关模式显示按钮（Tag=on 点亮），不支持关的模型按钮锁定常亮，未配置规则的模型两个都隐藏。
    /// 本方法入口先无条件刷新模型框 Tooltip（含并发/负载事件的首次挂接）：Tooltip 是公共提示，不受
    /// 当前模型是否配置了思考规则影响——放 rule 判空之前，避免无规则模型导致 Tooltip 永不赋值。</summary>
    void UpdateThinkingToggle()
    {
        UpdateModelTooltip();   // 公共提示：与思考规则分支解耦，任何模型都必须赋值/挂事件
        var rule = ThinkingRules.Load(cfg).For(CurModel);   // 本叶子：思考规则/value 均读 cfg 权威源（CurModel），不再依赖全局 loop
        if (rule == null)
        {
            thinkingBtn.Visibility = Visibility.Collapsed;
            thinkingLevelSel.Visibility = Visibility.Collapsed;
            return;
        }
        var value = cfg.ThinkingValueFor(CurModel);
        if (rule.IsLevel)
        {
            thinkingBtn.Visibility = Visibility.Collapsed;
            thinkingLevelSel.Visibility = Visibility.Visible;
            suppressThinkingSel = true;
            // 档位来自配置；供应商支持关（有 off 值）时追加"关闭"项（UI 专用值 off）
            thinkingLevelSel.ItemsSource = rule.CanOff
                ? rule.Levels.Append("off").ToArray()
                : rule.Levels;
            thinkingLevelSel.SelectedItem = !string.IsNullOrWhiteSpace(value) && value != "on"
                && (rule.Levels.Contains(value, StringComparer.OrdinalIgnoreCase) || value == "off")
                ? value : rule.Default;
            // 选中具体档位=思考开（Tag=on，渐变高亮+隐藏下箭头）；选中"关闭"=Tag=off（隐藏下箭头）
            thinkingLevelSel.Tag = thinkingLevelSel.SelectedItem is string s && s != "off" ? "on" : "off";
            suppressThinkingSel = false;
        }
        else
        {
            thinkingLevelSel.Visibility = Visibility.Collapsed;
            thinkingBtn.Visibility = Visibility.Visible;
            thinkingBtn.IsEnabled = rule.CanOff;
            var on = value.Length == 0 || value != "off";
            thinkingBtn.Tag = on ? "on" : null;
            thinkingBtn.Content = string.IsNullOrWhiteSpace(rule.Label) ? "思考" : rule.Label;
        }
    }

    /* ================= 本地模型 GPU 资源监控（标题栏三色柱，15 秒刷新） ================= */

    /// <summary>按当前供应商刷新 GPU 三色柱：本地模型且配置了 MonitorUrl 时常驻后台轮询，
    /// 但面板只在"本地模型真有任务在跑"时才显示，空闲即收起（不占标题栏）。
    /// 轮询与可见性解耦：常驻拉取，任务一启动面板立刻是真实数值，不用等首轮 15 秒。</summary>
    void UpdateGpuMonitor()
    {
        var isLocal = string.Equals(CurProvider, "Local", StringComparison.OrdinalIgnoreCase);
        var url = cfg.LocalMonitorUrl;
        if (isLocal) GpuMonitor.Current.Start(url);   // Timer 启停线程安全，无需切 UI 线程
        else GpuMonitor.Current.Stop();
        // 可见性写 UI 属性：MarkRunStart/MarkRunEnd 可能在后台线程收口，统一经 Ui() 派发
        Ui(() => gpuPanel.Visibility = isLocal && !string.IsNullOrWhiteSpace(url) && AnyTaskRunning()
            ? Visibility.Visible : Visibility.Collapsed);
    }

    /// <summary>是否有任一会话任务在跑：以会话运行态为准（1=进行中 / 2=锁等待；3=中断、4=完成、0=空闲都不算）。
    /// 不读 TaskRecord.Cts 是因为收口时取消源可能尚未置空，会把已结束的任务误判为仍在跑。
    /// 状态由 MarkRunStart/MarkRunEnd（主对话/产树/多模型/编排执行的统一入口）维护，两会话并行时有一个在跑即算。</summary>
    bool AnyTaskRunning()
    {
        foreach (var s in sessionHistory) if (s.RunState == 1 || s.RunState == 2) return true;
        return false;
    }

    /// <summary>点击切换思考开关：立即作用于模型客户端并按模型持久化到 [ModelThinking] Values</summary>
    void OnThinkingClick(object sender, RoutedEventArgs e)
    {
        var on = !string.Equals(thinkingBtn.Tag as string, "on");
        foreach (var r in AllRunners()) r.Loop.SwitchThinking(on ? "on" : "off");   // 应用到全部已建会话 Runner；未建会话首任务由 EnsureRunner 按 cfg 对齐
        cfg.SetThinkingValue(CurModel, on ? "on" : "off");
        cfg.Write();
        thinkingBtn.Tag = on ? "on" : null;
        UpdateModelTooltip();
    }

    /// <summary>思考等级下拉变更：立即作用于客户端并按模型持久化（等级档位来自 [ModelThinking] 配置）</summary>
    void OnThinkingLevelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressThinkingSel || thinkingLevelSel.SelectedItem is not string level) return;
        // 选中具体档位=思考开（按档位值）；选中"关闭"=off（按供应商 off 值发送；无 off 值的模型不出现该项）
        foreach (var r in AllRunners()) r.Loop.SwitchThinking(level);   // 应用到全部已建会话 Runner
        cfg.SetThinkingValue(CurModel, level);
        cfg.Write();
        thinkingLevelSel.Tag = level != "off" ? "on" : "off";   // 开=渐变高亮（对齐"思考"按钮开态），关=隐藏下箭头
        UpdateModelTooltip();
    }

    /// <summary>启动时把上次使用的供应商/模型写回 cfg 权威源并刷新 UI（本叶子：不再切任何全局/会话 Runner——
    /// 首任务由 EnsureRunner 创建会话运行器时按其对齐；运行期切换经 SwitchModelForAll 应用到全部已建 Runner）。
    /// cfg.Last* 为空或指向不可用模型时回退默认（provider=cfg.Provider 下首个有 key 模型），旧实现直作用于全局 loop。</summary>
    void RestoreLastModel()
    {
        var options = cfg.ModelOptions();
        var fallback = options.FirstOrDefault(o =>
                o.HasKey && string.Equals(o.Provider, cfg.Provider, StringComparison.OrdinalIgnoreCase))
            ?? options.FirstOrDefault(o => o.HasKey);
        if (fallback == null) return;
        var lastProvider = string.IsNullOrWhiteSpace(cfg.LastProvider) ? fallback.Provider : cfg.LastProvider;
        var match = options.FirstOrDefault(o =>
                o.HasKey &&
                string.Equals(o.Provider, lastProvider, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(o.ModelId, cfg.LastModel, StringComparison.OrdinalIgnoreCase))
            ?? options.FirstOrDefault(o =>
                o.HasKey &&
                string.Equals(o.Provider, lastProvider, StringComparison.OrdinalIgnoreCase))
            ?? fallback;
        if (!string.Equals(cfg.LastProvider, match.Provider, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(cfg.LastModel, match.ModelId, StringComparison.OrdinalIgnoreCase))
        {
            cfg.LastProvider = match.Provider;
            cfg.LastModel = match.ModelId;
            cfg.Write();
        }
        UpdateThinkingToggle();   // 启动恢复模型后刷新思考控件形态（UI 各刷新点经 CurProvider/CurModel 读 cfg 权威源）
    }

    /* ================= 模型下拉：候选清单来自 config.ini [Providers] + [Models] ================= */

    bool suppressModelSel;

    /// <summary>模型调用异常红标表：key=provider+"\u001F"+modelId，value=失败原因（提示用）。
    /// 某模型最近一次任务以"模型异常：…"收尾（额度/网络/鉴权等重试无效）时记入 → 下拉该项标红"⚠异常"；
    /// 该模型后续任务正常完成（Finished 成功收口）时移除。仅存内存，非持久化配置。</summary>
    readonly Dictionary<string, string> modelFaults = new();

    /// <summary>模型下拉打开时，把滚轮接管挂到弹层内部的 ScrollViewer（模板 x:Name=DropScroll）上：
    /// 弹层 Popup 是独立可视树，其内的滚轮事件不会路由到 modelSel 本体，默认 ScrollViewer 滚轮处理
    /// 在自定义模板下不可靠；在弹层内接管（事件源通道）+ 窗口根部几何兜底（OnDropGlobalWheel）双路保证。
    /// -= 再 += 保证重复打开只挂一份。
    /// 同时兼做弹层高度自适应（与 cbMode 同规则）：内容自然撑高，上限=窗口高 50%，下限=默认固定弹层高 240。</summary>
    void OnModelSelDropDownOpened(object sender, EventArgs e)
    {
        if (modelSel.Template?.FindName("DropScroll", modelSel) is not System.Windows.Controls.ScrollViewer sv) return;
        sv.PreviewMouseWheel -= OnDropScrollWheel;
        sv.PreviewMouseWheel += OnDropScrollWheel;
        double cap = Math.Max(240.0, ActualHeight * 0.5);
        if (Math.Abs(sv.MaxHeight - cap) > 0.5) sv.MaxHeight = cap;
    }

    /// <summary>弹层树内通道：事件源即在弹层树内（悬停弹层列表项时必然经过 DropScroll），直接滚动；
    /// 列表内容不足一屏（无可滚）也一律吞掉事件，杜绝任何向外的滚轮穿透。</summary>
    void OnDropScrollWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not System.Windows.Controls.ScrollViewer sv) return;
        if (sv.ScrollableHeight > 0)
            sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta / 3.0);
        e.Handled = true;
    }

    /// <summary>几何判定鼠标是否落在某下拉弹层的 DropScroll 覆盖区内：落内且内容可滚则手动滚动，
    /// 无论可滚与否一律吞掉事件（杜绝弹层上滚轮穿透到下方会话区），返回 true=已拦截。
    /// 不信任悬停解析/DirectlyOver，直接以鼠标相对 DropScroll 的坐标判定。</summary>
    bool ConsumeWheelInComboDrop(System.Windows.Controls.ComboBox combo, MouseWheelEventArgs e)
    {
        if (!combo.IsDropDownOpen) return false;
        if (combo.Template?.FindName("DropScroll", combo) is not System.Windows.Controls.ScrollViewer sv) return false;
        var p = Mouse.GetPosition(sv);
        if (p.X < 0 || p.Y < 0 || p.X > sv.ActualWidth || p.Y > sv.ActualHeight) return false;   // 鼠标不在这层弹层内
        if (sv.ScrollableHeight > 0)
            sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta / 3.0);
        e.Handled = true;
        return true;
    }

    /// <summary>窗口根部兜底通道：弹层 Popup 是独立可视树/可能不持焦点，鼠标悬停弹层上滚动时事件会被主窗树截获，
    /// 导致会话区跟着滚（弹层项上滚动却滚不动即此类）。这里对 modelSel/cbMode 两个下拉逐一做几何判定：
    /// 鼠标落在任一弹层覆盖区内就滚动该弹层并吞掉事件；都不落在才放行默认滚动（会话区正常滚动不受影响）。
    /// 注：早期版本在 ScrollableHeight&lt;=0（列表不足一屏）时直接放行，正是"弹层上滚轮穿透到会话区"的漏网处，已修复为一律吞掉。</summary>
    void OnDropGlobalWheel(object sender, MouseWheelEventArgs e)
    {
        if (ConsumeWheelInComboDrop(modelSel, e) || ConsumeWheelInComboDrop(cbMode, e)) return;
        // 鼠标不在任何弹层内：放行（会话区等正常滚动不受影响）
    }

    /// <summary>刷新单一厂商-模型下拉；配置首项为默认选项</summary>
    void RefreshModelCombo()
    {
        suppressModelSel = true;
        var options = cfg.ModelOptions();
        AnnotateFullOptions(options);   // 按 LlmConcurrencyGate 实时占用标注"在用/满额"（金色模型名 / 整行金色+满额禁选依据）
        modelSel.ItemsSource = options;
        // 本叶子：下拉高亮以 cfg 权威源（CurProvider/CurModel）为准，不再依赖全局 loop
        var current = options.FirstOrDefault(o =>
            string.Equals(o.Provider, CurProvider, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(o.ModelId, CurModel, StringComparison.OrdinalIgnoreCase));
        modelSel.SelectedItem = current ?? options.FirstOrDefault();
        suppressModelSel = false;
        UpdateStatus();
        UpdateThinkingToggle();
        UpdateGpuMonitor();
    }

    /// <summary>按 LlmConcurrencyGate 实时占用给下拉各选项标注并发状态：
    /// ① Full（厂商级，cur ≥ 上限）→ 该项禁选并标注"（满额）"；
    /// ② InUse（模型级，占用位登记的具体模型 == 本项）→ 该项着金色表示"正在被使用"。
    /// 两者相互独立：金色只落在真正在跑的那一个模型上，同厂商其它空闲模型保持正文色（不按厂商整片涂金）；
    /// 仅标注有 key 的可用选项，无 key 项本就不参与并发占用统计。</summary>
    void AnnotateFullOptions(List<AppConfig.ModelOption> options)
    {
        foreach (var o in options)
        {
            if (!o.HasKey) continue;
            o.Full = LlmConcurrencyGate.IsFull(o.Provider, cfg.MaxConcurrencyFor(o.Provider));
            o.InUse = LlmConcurrencyGate.IsModelInUse(o.Provider, o.ModelId);   // 精确到模型：只有正在跑的那一项为 true
            o.Faulted = modelFaults.ContainsKey($"{o.Provider}\u001F{o.ModelId}");   // 最近一次该模型调用"模型异常"收尾 → 标红
        }
    }

    /// <summary>并发占用变化（任务进入/释放并发位）时刷新下拉"满额"标记：重建列表并回选原选项，
    /// 不回写 cfg/会话（仅改 UI 展示，当前选择保持不动）。</summary>
    void RefreshModelFullMarks()
    {
        var sel = modelSel.SelectedItem as AppConfig.ModelOption;
        var provider = sel?.Provider ?? CurProvider;
        var model = sel?.ModelId ?? CurModel;
        var options = cfg.ModelOptions();
        AnnotateFullOptions(options);
        suppressModelSel = true;
        modelSel.ItemsSource = options;
        modelSel.SelectedItem = options.FirstOrDefault(o =>
            string.Equals(o.Provider, provider, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(o.ModelId, model, StringComparison.OrdinalIgnoreCase))
            ?? options.FirstOrDefault(o => o.Selectable);
        suppressModelSel = false;
    }

    /// <summary>LlmConcurrencyGate.Changed 的 UI 线程入口：刷新 Tooltip 并发占用 + 下拉"满额"标记。</summary>
    void OnConcurrencyChanged()
    {
        UpdateModelTooltip();
        RefreshModelFullMarks();
    }

    /// <summary>标记某厂商-模型为"调用异常"：记录失败原因并刷新下拉（该项变红"⚠异常"）。
    /// 模型仍可选——用户点选再次发送即重试；重试任务正常完成后由 Finished 成功收口 ClearModelFault 自动恢复。</summary>
    void MarkModelFault(string provider, string model, string reason)
    {
        if (string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(model)) return;
        modelFaults[$"{provider}\u001F{model}"] = reason;
        RefreshModelFullMarks();
    }

    /// <summary>清除某厂商-模型的"调用异常"标记（该模型最近一次任务已正常完成，重试成功→下拉恢复正常显示）。
    /// 未命中（本就正常）不刷新，避免无谓重建下拉列表。</summary>
    void ClearModelFault(string provider, string model)
    {
        if (string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(model)) return;
        if (modelFaults.Remove($"{provider}\u001F{model}"))
            RefreshModelFullMarks();
    }

    /// <summary>判定失败文案是否属"模型调用失败且重试无效"：AgentLoop 出口 catch LlmException 统一加 "模型异常：" 前缀
    /// （额度用尽/鉴权/网络/模型名错误等，LLMClient 内部已重试仍失败）；用户取消"任务已取消"、资源限制、Agent 逻辑异常等不属模型故障，不标红。</summary>
    static bool IsModelFaultReason(string reason) =>
        !string.IsNullOrEmpty(reason) && reason.StartsWith("模型异常：", StringComparison.Ordinal);

    void OnModelSelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressModelSel || modelSel.SelectedItem is not AppConfig.ModelOption opt) return;
        // 阻止选择无 ApiKey 或厂商并发已满的模型（灰显/桔色展示但不可选；
        // opt.Full 为并发回调前的快照，点击瞬间再对 HasKey 项实时复核一次作兜底）
        bool liveFull = opt.HasKey && LlmConcurrencyGate.IsFull(opt.Provider, cfg.MaxConcurrencyFor(opt.Provider));
        if (!opt.HasKey || liveFull)
        {
            // 回退到之前的选择
            var current = cfg.ModelOptions().FirstOrDefault(o =>
                string.Equals(o.Provider, CurProvider, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(o.ModelId, CurModel, StringComparison.OrdinalIgnoreCase));
            suppressModelSel = true;
            modelSel.SelectedItem = current ?? cfg.ModelOptions().FirstOrDefault(o => o.Selectable);
            suppressModelSel = false;
            return;
        }
        if (string.Equals(opt.Provider, CurProvider, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(opt.ModelId, CurModel, StringComparison.OrdinalIgnoreCase))
        {
            // 网页通道例外：重复点同一项时也走一次登录态检查——未登录则弹出窗口让用户登录，
            // 已登录则保持后台静默（用户想主动查看时用标题栏「显示」）
            if (Core.WebChannelSpec.IsWebProvider(opt.Provider))
                WebChannelHost.ShowChannelByProvider(opt.Provider);
            return;
        }

        // P 需求3：cfg 先落权威源（供新会话/未钉住会话取样与下拉高亮），并把新选择写回当前会话钉住参数
        // （会话全程使用钉住的模型，切换会话顶部随之回显）。生效范围收敛为当前会话已建 Runner——各会话钉各自
        // 的模型，不再全局强切其它会话 Loop（旧 SwitchModelForAll 语义废除：其它会话的执行保持各自钉住模型）
        cfg.LastProvider = opt.Provider;
        cfg.LastModel = opt.ModelId;
        cfg.Write();
        if (currentSession != null)
        {
            currentSession.PinProvider = opt.Provider;
            currentSession.PinModel = opt.ModelId;
        }
        var curRunner = RunnerOf(currentSession);
        if (curRunner != null)
            curRunner.Loop.SwitchModelFull(opt.Provider, opt.ModelId);
        UpdateStatus();
        UpdateThinkingToggle();
        UpdateGpuMonitor();
        RefreshTopPinLine();   // P：会话内改模型 → 同步顶部 logo 参数标题
        // 网页模型通道：选中即拉起其独立网页窗口（未登录时用户就在该窗口内扫码/输手机号登录），
        // 适配/页面逻辑全部在窗口内部，主窗口只负责"叫出来"
        if (Core.WebChannelSpec.IsWebProvider(opt.Provider))
            WebChannelHost.ShowChannelByProvider(opt.Provider);
    }

    /// <summary>更新模型选择框的 Tooltip：当前模型/上下文、可用厂商并发占用汇总（当前使用/总支持），
    /// 以及主机 CPU/内存负载。未配置 ApiKey 的不可用厂商不展示其并发数。
    /// 运行中任务进入/释放并发位或主机负载每秒刷新时经事件触发本方法实时更新。</summary>
    void UpdateModelTooltip()
    {
        if (!concurrencyTipHooked)
        {
            concurrencyTipHooked = true;   // 只挂一次：并发占用变化（任意线程）→ 切 UI 线程刷新 Tooltip + 下拉"满额"标记
            LlmConcurrencyGate.Changed += () => Dispatcher.BeginInvoke(OnConcurrencyChanged);
            HostLoadMonitor.Changed += () => Dispatcher.BeginInvoke(UpdateModelTooltip);
        }
        var lines = new List<string>
        {
            $"供应商：{CurProvider} ({cfg.ProviderDisplayName(CurProvider)})",
            $"模型：{CurModel}",
            $"上下文窗口：{cfg.MaxTokens / 1000}k",
        };
        // 只统计可用厂商（已配 ApiKey、下拉可选）；不可用厂商不展示并发数
        var seen = new List<string>();
        var perLines = new List<string>();
        var curTotal = 0;
        var maxTotal = 0;
        foreach (var o in cfg.ModelOptions())
        {
            if (!o.HasKey) continue;
            if (seen.Contains(o.Provider, StringComparer.OrdinalIgnoreCase)) continue;
            seen.Add(o.Provider);
            var (cur, _) = LlmConcurrencyGate.Current(o.Provider);
            var max = cfg.MaxConcurrencyFor(o.Provider);
            curTotal += cur;
            maxTotal += max;
            var isCur = string.Equals(o.Provider, CurProvider, StringComparison.OrdinalIgnoreCase);
            perLines.Add($"  {o.ProviderDisplay}：{cur}/{max}{(isCur ? "  ←当前" : "")}");
        }
        if (maxTotal > 0)
        {
            lines.Add("");
            lines.Add($"并发：当前使用 {curTotal} / 总支持 {maxTotal}（满额自动拒启并提示）");
            lines.AddRange(perLines);
        }
        var cpu = HostLoadMonitor.CpuPercent;
        var mem = HostLoadMonitor.MemPercent;
        var load = $"主机负载：CPU {(cpu >= 0 ? $"{cpu:0}%" : "未知")} / 内存 {(mem >= 0 ? $"{mem:0}%" : "未知")}（≥{HostLoadMonitor.GatePct:0}% 拒启新任务）";
        if (HostLoadMonitor.IsOverloaded()) load += " ⚠ 当前超载";
        lines.Add("");
        lines.Add(load);
        modelSel.ToolTip = string.Join("\n", lines);
    }

    static bool concurrencyTipHooked;
    /// <summary>切换会话回显顶部选择器置位：置位期间的角色/模式/模型选择变化不再二次写回会话钉住参数（防回流）。</summary>
    bool _syncingSelectors;

    /* ================= 会话钉住执行参数：角色+模式+模型（首任务取样固定 / 切会话回显 / 改选择即时更新） ================= */

    /// <summary>解析顶部 角色/模式 下拉当前选中项（tag=role|mode）→ (角色名, 角色显示, 模式名, 模式显示)；未选中返回 null。</summary>
    (string Role, string RoleDisp, string Mode, string ModeDisp)? SelectedRoleMode()
    {
        var tag = (cbMode.SelectedItem as ComboBoxItem)?.Tag as string;
        if (string.IsNullOrWhiteSpace(tag)) return null;
        string role, mode;
        if (tag.StartsWith("__")) { role = "software-engineer"; mode = tag == "__deep__" ? "deep" : "agile"; }
        else
        {
            var p = tag.Split('|');
            if (p.Length < 2) return null;
            role = p[0];
            mode = p[1];
        }
        var mp = GAIRR.AgentHost.RoleModeStore.FindMode(role, mode);
        var rp = GAIRR.AgentHost.RoleModeStore.FindRole(role);
        return (role, rp?.DisplayName ?? role, mode, mp?.DisplayName ?? mode);
    }

    /// <summary>给会话钉住执行参数（角色/模式/模型）：首次执行前取样顶部选择器并写入会话；已钉住则原样保留。
    /// 钉住即固定——会话全程使用钉住值执行，顶部后续变化只影响新钉住的会话与当前会话钉住值的更新，不回溯已钉住的其它会话。</summary>
    void EnsureSessionPin(SessionItem? s)
    {
        if (s == null || !string.IsNullOrWhiteSpace(s.PinProvider)) return;
        var rm = SelectedRoleMode();
        if (rm == null) return;   // 下拉尚未填充等极端时序：留到下次执行再钉
        s.PinRole = rm.Value.Role;
        s.PinRoleDisplay = rm.Value.RoleDisp;
        s.PinMode = rm.Value.Mode;
        s.PinModeDisplay = rm.Value.ModeDisp;
        s.PinProvider = CurProvider;
        s.PinModel = CurModel;
    }

    /// <summary>自动匹配瞬态：__auto__ 下本次发送判定的命中模式（判定失败/不可用回落默认 software-engineer|agile）；
    /// 手动固定选择时恒 null。下拉保持 __auto__ 无法从下拉解析到命中项，此字段供 EnsureRunner 首建提示词
    /// 与 OnSend 的 Loop.Flow 任务级装配对齐命中模式；每条消息发送前由 AutoMatchRoleIfNeeded 先重置再赋值。</summary>
    GAIRR.AgentHost.ModeProfile? _autoMode;

    /// <summary>角色自动匹配：顶部下拉选中"🤖 自动匹配"（tag=__auto__）时，发送前调一次轻量模型判定，
    /// 命中则把 role|mode 结果直接写回本会话钉住参数、同步当前会话已建 Runner（engine 模式 SwitchAgentMode /
    /// 纯提示词 SwitchPrompt），并把命中模式存入 _autoMode 供 EnsureRunner/Loop.Flow 装配。
    /// 下拉框保持 __auto__ 不切换——每条消息独立判定路由（完全不钉住）；判定失败/不可用回落默认角色
    /// software-engineer|agile。全程 try/catch，绝不阻塞发送。OnSend 以 await 调用（UI 线程不阻塞）；
    /// ct 供匹配期间点停止即时取消 LLM 判定。sendSession = 本次发送的发起会话：await 期间用户可能已切走，
    /// 命中结果/提示只写回发起会话，且仅当发起会话仍是当前视口才刷新顶部与追加提示（防串台到被查看会话）。</summary>
    async Task AutoMatchRoleIfNeeded(SessionItem? sendSession, string userText, CancellationToken ct)
    {
        _autoMode = null;
        try
        {
            // 编排会话走 PlanRunner 驱动，不做角色自动匹配（防命中改写其钉住参数/流程）
            if (sendSession?.IsOrchestration == true) return;
            if ((cbMode.SelectedItem as ComboBoxItem)?.Tag as string != "__auto__") return;
            var hit = await GAIRR.AgentHost.RoleAutoMatch.MatchAsync(cfg, CurProvider, CurModel, userText, ct);
            if (ct.IsCancellationRequested) return;   // 匹配期间被停止：不写钉住参数（OnSend 统一清理解锁）
            // 命中模式（含回落默认 agile）；仓库异常连默认角色都没有的极端场景原样不动
            var role = hit != null
                ? GAIRR.AgentHost.RoleModeStore.FindRole(hit.Value.Role)
                : GAIRR.AgentHost.RoleModeStore.FindRole("software-engineer");
            var mode = hit != null
                ? GAIRR.AgentHost.RoleModeStore.FindMode(hit.Value.Role, hit.Value.Mode)
                : GAIRR.AgentHost.RoleModeStore.FindMode("software-engineer", "agile");
            if (role == null || mode == null) return;
            _autoMode = mode;
            // 拆开声明：var 不允许多声明符（CS0819），逐行显式声明保证编译通过
            var roleName = role.Name;
            var roleDisp = role.DisplayName ?? role.Name;
            var modeName = mode.Name;
            var modeDisp = mode.DisplayName ?? mode.Name;
            // 不切下拉（保持 __auto__）：结果直接写回会话钉住参数（首条补钉厂商/模型，防 EnsureSessionPin 首任务取样把
            // __auto__ 默认覆盖本次命中）；每条消息都覆盖旧值 = 每条消息独立路由
            if (sendSession != null)
            {
                if (string.IsNullOrWhiteSpace(sendSession.PinProvider))
                {
                    sendSession.PinProvider = CurProvider;
                    sendSession.PinModel = CurModel;
                }
                sendSession.PinRole = roleName;
                sendSession.PinRoleDisplay = roleDisp;
                sendSession.PinMode = modeName;
                sendSession.PinModeDisplay = modeDisp;
                // 命中（含回落默认角色）写入任务记录：开始执行时 SessionPinText 即显示具体命中 角色/模式
                // （GAIRR 头像标题与顶部 logo 标题共用），不再"执行中显示自动匹配、回合结束才见命中"；收口时清除恢复自动匹配显示
                var hitModel = ModelDisplayFor(sendSession.PinProvider, sendSession.PinModel);
                var hitText = string.IsNullOrWhiteSpace(hitModel) ? $"{roleDisp} · {modeDisp}" : $"{roleDisp} · {modeDisp} · {hitModel}";
                (TaskOf(sendSession) ?? EnsureRec(sendSession)).AutoHitText = hitText;
                if (ReferenceEquals(currentSession, sendSession)) RefreshTopPinLine();   // 仍停在发起会话才刷新顶部回显
            }
            // 同步已建 Runner（首条 Runner 尚空，由随后 EnsureRunner 按 _autoMode 装配提示词）：与 OnCbModeChanged 同款语义
            var curRunner = RunnerOf(sendSession);
            if (curRunner != null)
            {
                var n = modeName.Trim().ToLowerInvariant();
                if (n == "agile" || n == "deep") curRunner.Loop.SwitchAgentMode(n);
                else if (string.IsNullOrWhiteSpace(mode.FlowName) && !string.IsNullOrWhiteSpace(mode.PromptFile))
                    curRunner.Loop.SwitchPrompt(GAIRR.AgentHost.RoleModeStore.ResolvePromptFile(mode));
            }
            // 命中且发起会话仍是当前视口才提示（await 期间切走不往被查看会话消息流里塞提示）
            if (hit != null && ReferenceEquals(currentSession, sendSession))
                AddMessage(new ChatMessage { Kind = MsgKind.Agent, Who = "GAIRR",
                    Text = $"已自动匹配：{roleDisp} · {modeDisp}" });
        }
        catch { /* 匹配任何异常静默：不阻塞发送 */ }
    }

    /// <summary>模型显示名（供应商显示名 + 模型 id，与标题区模型展示同口径）；入参空缺回退 UI 当前。</summary>
    string ModelDisplayFor(string? provider, string? model)
    {
        var p = string.IsNullOrWhiteSpace(provider) ? CurProvider : provider;
        var m = string.IsNullOrWhiteSpace(model) ? CurModel : model;
        if (string.IsNullOrWhiteSpace(m)) return "";
        return (string.IsNullOrWhiteSpace(p) ? "" : cfg.ProviderDisplayName(p) + " ") + m;
    }

    /// <summary>会话钉住参数 → “角色 + 模式 + 模型” 展示文本（头像标题/执行参数卡共用）。
    /// 显示名以钉住时存档为准；未钉住（新会话尚未执行）回退顶部当前选择——此时顶部即该会话首任务取样源。</summary>
    string SessionPinText(SessionItem? s)
    {
        // 自动匹配会话：本轮匹配已命中（开始执行至收口）→ 显示具体命中 角色/模式（GAIRR 头像标题与顶部 logo 标题
        // 同步，不再"开始执行显示自动匹配、回合结束才回显命中"）；未命中/已收口 → 显示"🤖 自动匹配 · 模型"
        // （会话选择=自动），不随最后回合命中角色跳变；回合级"实际用了哪个角色"由各消息头像标题/执行参数卡的消息级快照负责
        var model0 = ModelDisplayFor(s?.PinProvider, s?.PinModel);
        if (s != null && s.AutoRoute)
        {
            var hitTxt = TaskOf(s)?.AutoHitText;
            if (!string.IsNullOrWhiteSpace(hitTxt)) return hitTxt;
            return $"🤖 自动匹配 · {model0}";
        }
        var role = s != null && !string.IsNullOrWhiteSpace(s.PinRoleDisplay) ? s.PinRoleDisplay
            : s != null && !string.IsNullOrWhiteSpace(s.PinRole) ? s.PinRole
            : SelectedRoleMode()?.RoleDisp ?? "通用";
        var mode = s != null && !string.IsNullOrWhiteSpace(s.PinModeDisplay) ? s.PinModeDisplay
            : s != null && !string.IsNullOrWhiteSpace(s.PinMode) ? s.PinMode
            : SelectedRoleMode()?.ModeDisp ?? "自主";
        return $"{role} · {mode} · {model0}";
    }

    /// <summary>消息入列时刻的执行参数六要素（角色/模式/模型 + 存档显示名）：
    /// 会话已钉住 → 取钉住值；未钉住（新会话尚未执行）→ 取顶部当前选择，作为首任务取样源；顶部也无可取样 → null。</summary>
    (string Role, string RoleDisp, string Mode, string ModeDisp, string Provider, string Model)? CurrentExecParams(SessionItem? s)
    {
        if (s != null && !string.IsNullOrWhiteSpace(s.PinProvider))
            return (s.PinRole ?? "", s.PinRoleDisplay ?? s.PinRole ?? "",
                    s.PinMode ?? "", s.PinModeDisplay ?? s.PinMode ?? "",
                    s.PinProvider, s.PinModel ?? "");
        var rm = SelectedRoleMode();
        if (rm == null) return null;
        return (rm.Value.Role, rm.Value.RoleDisp, rm.Value.Mode, rm.Value.ModeDisp, CurProvider, CurModel);
    }

    /// <summary>执行参数六要素 → “角色 + 模式 + 模型” 展示文本（头像标题/顶部信息栏共用）。
    /// 展示名以快照存档显示名为准（角色包改名仍回退存档名）；模型空缺回退顶部当前模型；全无可取样 → “通用 · 自主”。</summary>
    string ExecTextOf((string Role, string RoleDisp, string Mode, string ModeDisp, string Provider, string Model)? p)
    {
        string r, md;
        if (p == null) { r = "通用"; md = "自主"; }
        else
        {
            r = !string.IsNullOrWhiteSpace(p.Value.RoleDisp) ? p.Value.RoleDisp
                : !string.IsNullOrWhiteSpace(p.Value.Role) ? p.Value.Role : "通用";
            md = !string.IsNullOrWhiteSpace(p.Value.ModeDisp) ? p.Value.ModeDisp
                : !string.IsNullOrWhiteSpace(p.Value.Mode) ? p.Value.Mode : "自主";
        }
        string? modelTxt = null;
        if (p is { } x)
        {
            var m = ModelDisplayFor(x.Provider, x.Model);
            if (!string.IsNullOrWhiteSpace(m)) modelTxt = m;
        }
        return modelTxt == null ? $"{r} · {md}" : $"{r} · {md} · {modelTxt}";
    }

    /// <summary>把执行参数快照写进消息六要素（历史落盘/重放用）。</summary>
    void ApplyExecParams(ChatMessage m, (string Role, string RoleDisp, string Mode, string ModeDisp, string Provider, string Model)? p)
    {
        if (p == null) return;
        m.RunRole = p.Value.Role;
        m.RunRoleDisplay = p.Value.RoleDisp;
        m.RunMode = p.Value.Mode;
        m.RunModeDisplay = p.Value.ModeDisp;
        m.RunProvider = p.Value.Provider;
        m.RunModel = p.Value.Model;
    }

    /// <summary>历史记录自带执行参数快照 → 展示文本；旧历史（RunXxx 全空）无快照返回 null（由调用方回退会话钉住参数）。</summary>
    string? SnapshotTextOf(MessageRecord m)
    {
        if (string.IsNullOrWhiteSpace(m.RunProvider) && string.IsNullOrWhiteSpace(m.RunRole)
            && string.IsNullOrWhiteSpace(m.RunMode)) return null;
        return ExecTextOf((m.RunRole ?? "", m.RunRoleDisplay ?? m.RunRole ?? "",
                           m.RunMode ?? "", m.RunModeDisplay ?? m.RunMode ?? "",
                           m.RunProvider ?? "", m.RunModel ?? ""));
    }

    /// <summary>把历史记录 RunXxx 快照拷回消息（保持再次保存的往返一致；无快照则拷 null）。</summary>
    void ApplyRecordSnapshot(ChatMessage msg, MessageRecord m)
    {
        msg.RunRole = m.RunRole;
        msg.RunRoleDisplay = m.RunRoleDisplay;
        msg.RunMode = m.RunMode;
        msg.RunModeDisplay = m.RunModeDisplay;
        msg.RunProvider = m.RunProvider;
        msg.RunModel = m.RunModel;
    }

    /// <summary>会话最后带头像消息（Agent/Typing/Cmd）落盘的执行参数快照；无任何带头像消息或均为旧历史（无快照）→ null。
    /// 打开历史会话时顶部选择器据此回显——“历史最后保存的角色/模式/模型”随消息逐条保存，顶部修改不回溯旧消息。</summary>
    (string Role, string RoleDisp, string Mode, string ModeDisp, string Provider, string Model)? LastExecSnapshot(SessionItem s)
    {
        for (int i = s.Messages.Count - 1; i >= 0; i--)
        {
            var m = s.Messages[i];
            var kind = Enum.TryParse<MsgKind>(m.Kind, out var k) ? k : MsgKind.Agent;
            if (kind is not (MsgKind.Agent or MsgKind.Typing or MsgKind.Cmd)) continue;
            if (string.IsNullOrWhiteSpace(m.RunProvider) && string.IsNullOrWhiteSpace(m.RunRole)
                && string.IsNullOrWhiteSpace(m.RunMode)) continue;
            return (m.RunRole ?? "", m.RunRoleDisplay ?? m.RunRole ?? "",
                    m.RunMode ?? "", m.RunModeDisplay ?? m.RunMode ?? "",
                    m.RunProvider ?? "", m.RunModel ?? "");
        }
        return null;
    }

    /// <summary>切换会话后顶部选择器回显该会话【最后保存的执行参数】：取最后带头像消息落盘的
    /// 角色/模式/模型快照（随消息逐条保存，顶部改参不回溯历史旧消息），无消息快照（旧历史/从未执行）回退会话钉住参数。
    /// 两者皆无（新会话尚未执行）不移动选择器——顶部保持上次选择，作为该会话首任务取样源。</summary>
    void SyncTopSelectorsToSession(SessionItem s)
    {
        // 自动匹配会话：角色/模式下拉回显"🤖 自动匹配"项（会话选择=自动，每回合独立重匹配），
        // 模型取会话钉住值（重启后 Pin 丢失则回退消息快照）。不回落上回合命中的具体角色——
        // 快照仅"最后回合实际使用"记录（LastExecSnapshot），若回显到下拉会把自动会话钉死在
        // 上次命中角色上，后续发送不再重新匹配，自动匹配语义即丢
        if (s.AutoRoute)
        {
            var snapA = LastExecSnapshot(s);
            var providerA = s.PinProvider ?? snapA?.Provider;
            if (string.IsNullOrWhiteSpace(providerA)) return;   // 无钉住也无快照（理论上不出现）：下拉保持现状
            _syncingSelectors = true;
            cfg.LastProvider = providerA;
            cfg.LastModel = s.PinModel ?? (string.IsNullOrWhiteSpace(snapA?.Model) ? "" : snapA.Value.Model);
            RefreshModelCombo();   // 按 CurProvider/CurModel 重选下拉（内部自带 suppressModelSel 防回流）
            var autoItem = cbMode.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (i.Tag as string) == "__auto__");
            if (autoItem != null) cbMode.SelectedItem = autoItem;   // 触发 OnCbModeChanged（_syncingSelectors 置位，跳过写回）
            _syncingSelectors = false;
            RefreshTopPinLine();   // 顶部信息栏：🤖 自动匹配 · 模型
            return;
        }
        var snap = LastExecSnapshot(s);   // 会话最后带头像消息的消息级执行参数快照
        var provider = snap?.Provider ?? s.PinProvider;
        if (string.IsNullOrWhiteSpace(provider)) return;
        _syncingSelectors = true;
        cfg.LastProvider = provider;
        cfg.LastModel = !string.IsNullOrWhiteSpace(snap?.Model) ? snap.Value.Model : (s.PinModel ?? "");
        RefreshModelCombo();   // 按 CurProvider/CurModel 重选下拉（内部自带 suppressModelSel 防回流）
        // 角色/模式下拉定位到 快照/钉住的 role|mode；找不到（角色包被删/改名）保持现状，执行参数仍照旧
        var want = (snap?.Role ?? s.PinRole ?? "") + "|" + (snap?.Mode ?? s.PinMode ?? "");
        var match = cbMode.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals(i.Tag as string, want, StringComparison.OrdinalIgnoreCase));
        if (match != null)
        {
            cbMode.SelectedItem = match;   // 触发 OnCbModeChanged（_syncingSelectors 置位，跳过写回）
        }
        _syncingSelectors = false;
        // 旧数据缺显示名时回填一次（优先快照存档显示名，其次下拉回显名）
        if (string.IsNullOrWhiteSpace(s.PinRoleDisplay) || string.IsNullOrWhiteSpace(s.PinModeDisplay))
        {
            var rm = SelectedRoleMode();
            if (rm != null)
            {
                if (string.IsNullOrWhiteSpace(s.PinRoleDisplay))
                    s.PinRoleDisplay = snap?.RoleDisp ?? rm.Value.RoleDisp;
                if (string.IsNullOrWhiteSpace(s.PinModeDisplay))
                    s.PinModeDisplay = snap?.ModeDisp ?? rm.Value.ModeDisp;
            }
        }
        RefreshTopPinLine();   // P：切换会话回显后立即同步顶部信息栏 logo 参数标题
    }

    /// <summary>左上角标题栏 logo 文本同步当前项目名称（随项目切换实时刷新）。</summary>
 void RefreshAppLogo()
 {
 var name = (projSel?.SelectedItem as ProjectItem)?.Name;
 if (string.IsNullOrWhiteSpace(name))
 {
 var root = (cfg.ProjectRoot ?? "").TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
 name = string.IsNullOrWhiteSpace(root) ? "GAIRR" : System.IO.Path.GetFileName(root);
 }
 if (string.IsNullOrWhiteSpace(name)) name = "GAIRR";
 if (appLogoText != null && appLogoText.Text != name) appLogoText.Text = name;
 }

 /// <summary>顶部信息栏 GAIRR logo 区同步当前会话参数标题：logo 下方小字行显示 角色+模式+模型，
    /// logo 悬停 ToolTip 显示完整"概尔 Agent · 角色 · 模式 · 模型"（随会话切换/会话内修改实时刷新）。
    /// Agent 段整体显隐由 UpdateChatTopInfo 锚点逻辑负责，本方法只管文本/标题同步。</summary>
    void RefreshTopPinLine()
    {
        var pin = currentSession != null ? SessionPinText(currentSession) : null;
        var empty = string.IsNullOrWhiteSpace(pin);
        if (topPinLine != null)
        {
            if (!empty && topPinLine.Text != pin)
            {
                topPinLine.Text = pin;
                topPinLine.ToolTip = "概尔 Agent · " + pin;
            }
            if (topPinLine.Visibility != (empty ? Visibility.Collapsed : Visibility.Visible))
                topPinLine.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        }
        if (topLogo != null)
        {
            var tip = empty ? null : "概尔 Agent · " + pin;
            if ((string?)topLogo.ToolTip != tip) topLogo.ToolTip = tip;
        }
        RefreshWebWinBtn();   // 顶部信息栏是"当前执行参数"的落点：顺带同步标题栏「显示/隐藏」（启动/切会话/换模型都会走到这）
    }

    /* ================= 消息流与发送（真实 Agent） ================= */

    /// <summary>在消息流末尾追加一条消息，并自动滚动到底部。
    /// Agent/工作中等带头像的消息自动携带该消息入列时刻的执行参数：展示文本作头像标题（头像右侧/ToolTip），
    /// 六要素快照随消息落盘——历史重放按各消息自身快照显示，顶部修改不回溯已保存的历史消息。</summary>
    void AddMessage(ChatMessage m)
    {
        if (m.AgentVisible == Visibility.Visible && string.IsNullOrEmpty(m.AvatarTitle))
        {
            var p = CurrentExecParams(currentSession);   // 会话钉住值；未钉住（新会话首任务）取样顶部当前选择
            m.AvatarTitle = ExecTextOf(p);
            ApplyExecParams(m, p);
        }
        messages.Add(m);
        ScrollToBottom();
    }

    /// <summary>轻量操作反馈：会话区顶部浮层提示，3 秒后淡出自动消失（不写入消息流与历史）。
    /// 重复触发时替换文本并重新计时；淡出期间来新提示会取消旧动画直接显示。</summary>
    void ShowToast(string text)
    {
        toastTipText.Text = text;
        toastTip.BeginAnimation(OpacityProperty, null);   // 打断上次淡出动画
        toastTip.Opacity = 1;
        toastTip.Visibility = Visibility.Visible;
        if (toastTimer == null)
        {
            toastTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            toastTimer.Tick += OnToastTimerTick;
        }
        toastTimer.Stop();
        toastTimer.Start();
    }

    /// <summary>toast 3 秒到点：300ms 淡出后收起。</summary>
    void OnToastTimerTick(object? sender, EventArgs e)
    {
        toastTimer?.Stop();
        var fade = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(300));
        fade.Completed += (_, _) => toastTip.Visibility = Visibility.Collapsed;
        toastTip.BeginAnimation(OpacityProperty, fade);
    }

    bool scrollQueued;   // 已有排队的滚动到底请求：并发调用只保留一条，防大量消息/连点会话时滚动任务积压拖垮 UI

    /// <summary>滚动到对话区底部。使用 Background 优先级，避免阻塞输入/渲染。
    /// 后台运行（EventBg）跳过：滚动属被查看会话视口，后台任务事件不得拉动其滚动位置。</summary>
    void ScrollToBottom()
    {
        if (EventBg) return;
        if (scrollQueued) return;
        scrollQueued = true;
        Dispatcher.BeginInvoke(() => { scrollQueued = false; msgScroll.ScrollToEnd(); },
            System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>延迟滚动到底部：等 UI 线程空闲（本帧批量填充的布局已完成后）再滚动。
    /// 专供"一次性清空+批量填充大量消息"后调用（如打开历史会话）：
    /// 若立即 ScrollToEnd，内容尺寸尚未算完，滚动触发重新布局、布局又触发滚动，
    /// 互相触发会卡死；延到 ContextIdle 时机滚动，此时布局已稳定，安全无死锁。</summary>
    void ScrollToBottomAfterLayout()
    {
        if (scrollQueued) return;
        scrollQueued = true;
        Dispatcher.BeginInvoke(() => { scrollQueued = false; msgScroll.ScrollToEnd(); },
            System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    /* ================= 会话历史分段加载（长会话打开优化） =================
       长会话（上百回合、富卡片）全量实例化+布局是打开卡顿的主因。改法：打开时只装配"最近若干回合"，
       不足一屏再向前逐回合补装；用户继续上滚到顶时提示"正在加载更早消息"并再装一批。*/

    /// <summary>当前会话完整消息序列（时间顺序）：未装配的更早历史在前、已装配的 messages 在后。
    /// 分段加载下 messages 只是完整历史的"尾部窗口"，凡按"整个会话"读取消息的地方（保存会话、标题回退、
    /// 启动选中列表项、一屏续装）都必须走本方法，否则会丢掉尚未装配的更早消息。</summary>
    IEnumerable<ChatMessage> AllViewMsgs() => pendingOlderMsgs.Count == 0
        ? (IEnumerable<ChatMessage>)messages
        : pendingOlderMsgs.Concat(messages);

    /// <summary>清空消息区与分段加载暂存（会话切换/清场的统一复位口径：临时清屏点一律走它，
    /// 避免上一个会话的待装配历史残留到另一个会话）。</summary>
    void ResetMsgView()
    {
        msgViewGeneration++;   // 使在途的分段装配回调失效（防上一个会话的装配串到新会话）
        messages.Clear();
        pendingOlderMsgs.Clear();
        loadingOlderMsgs = false;
        if (olderLoadingBar != null) olderLoadingBar.Visibility = Visibility.Collapsed;
    }

    /// <summary>最近一个回合的起始下标：最后一条 User 消息即最近回合的起点（其前无 User 的开场消息归首个回合）。
    /// 找不到 User 时返回 0（整段都算最近回合，等于不分段）。</summary>
    static int LastTurnStart(IList<ChatMessage> list)
    {
        for (int i = list.Count - 1; i >= 0; i--)
            if (list[i].Kind == MsgKind.User) return i;
        return 0;
    }

    /// <summary>从 pendingOlderMsgs 末尾摘出"上一个回合"（该段内最后一条 User 起至末尾）并返回。</summary>
    List<ChatMessage> TakePrevTurn()
    {
        int n = pendingOlderMsgs.Count;
        if (n == 0) return new List<ChatMessage>();
        int start = 0;
        for (int i = n - 1; i >= 0; i--)
            if (pendingOlderMsgs[i].Kind == MsgKind.User) { start = i; break; }
        var turn = pendingOlderMsgs.GetRange(start, n - start);
        pendingOlderMsgs.RemoveRange(start, n - start);
        return turn;
    }

    /// <summary>把一批更早的消息按时间顺序插到 messages 头部（维持 messages = 完整历史的尾部窗口）。</summary>
    void PrependMsgs(List<ChatMessage> older)
    {
        for (int i = older.Count - 1; i >= 0; i--) messages.Insert(0, older[i]);
    }

    /// <summary>分段装配核心：逐回合装配更早消息，且「每装一回合就让出一帧」，待布局稳定后再测量判定。
    /// 同一帧内连续插入时，惰性时间线卡片（PendingItems）、图片等的高度要等下一帧才测得准，ExtentHeight 会偏小，
    /// "不足一屏"的判断便一直成立，于是一次装进远超一屏的历史（表现为"一次加载好多"）。
    /// targetPx &lt;= 0：首屏模式——内容超过一屏即停（打开会话用，装配完保持贴底）；
    /// targetPx &gt; 0：滚顶模式——新增高度达到 targetPx 即停（用户上滚用，装配完回补等高保视口）。</summary>
    void LoadOlderTurns(double targetPx, bool keepViewport)
    {
        if (loadingOlderMsgs || pendingOlderMsgs.Count == 0 || msgScroll == null) return;
        loadingOlderMsgs = true;
        if (keepViewport && olderLoadingBar != null) olderLoadingBar.Visibility = Visibility.Visible;
        int gen = msgViewGeneration;              // 会话世代：中途切走会话则静默中止本批
        double anchor = msgScroll.ExtentHeight;   // 基准高度（滚顶模式比新增高度；首屏模式比一屏）
        double offsetBase = msgScroll.VerticalOffset;
        bool wasAtBottom = IsMsgAtBottom();

        void Finish()
        {
            if (gen != msgViewGeneration) return;   // 会话已切换：不结算、不碰新会话视口
            if (keepViewport)
            {
                double added = msgScroll.ExtentHeight - anchor;
                if (added > 0) msgScroll.ScrollToVerticalOffset(offsetBase + added);   // 内容插在上方：回补等高保视口
            }
            else if (wasAtBottom) msgScroll.ScrollToEnd();
            UpdateChatTopInfo();   // 分段后重算顶部信息栏（首屏只含少量消息）
            loadingOlderMsgs = false;
            if (pendingOlderMsgs.Count == 0 && olderLoadingBar != null)
                olderLoadingBar.Visibility = Visibility.Collapsed;   // 更早内容已装完：收起提示
        }

        void Step(int guard)
        {
            if (gen != msgViewGeneration) return;   // 会话已切换：静默中止（loadingOlderMsgs 由 ResetMsgView 复位）
            if (pendingOlderMsgs.Count == 0 || guard > 400) { Finish(); return; }
            msgScroll.UpdateLayout();
            double vp = msgScroll.ViewportHeight;
            if (vp <= 0)
            {
                // 布局未完成（启动恢复/窗口未显示）：延到布局完成后再判定，别把未测量的高度当成"已满一屏"
                if (guard < 60) Dispatcher.BeginInvoke(new Action(() => Step(guard + 1)),
                    System.Windows.Threading.DispatcherPriority.Loaded);
                else Finish();
                return;
            }
            if (anchor <= 0) anchor = msgScroll.ExtentHeight;   // 入口时尚未布局：首次测到内容高度时锁定基准
            bool enough = targetPx > 0
                ? msgScroll.ExtentHeight - anchor >= targetPx
                : msgScroll.ExtentHeight > vp + 1;
            if (enough) { Finish(); return; }
            PrependMsgs(TakePrevTurn());   // 仍不足一屏/未达目标：再向前装一个回合
            // 关键：让出一帧再判定——本帧插入的富卡片高度还没测准，紧循环会一连连装好几个回合
            Dispatcher.BeginInvoke(new Action(() => Step(guard + 1)),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        Step(0);
    }

    /// <summary>打开会话（或启动恢复）首屏装配：先看最近一回合是否填满一屏，不足就向前逐回合补装，超过一屏即停。</summary>
    void FillOlderUntilOneScreen() => LoadOlderTurns(0, keepViewport: false);

    /// <summary>用户在顶部继续向上滚动：显示"正在加载更早消息"提示并再装配约半屏。
    /// 逐回合异步装配（见 LoadOlderTurns），内容插在上方后回补等高，保住视口位置、读到的内容不跳动。</summary>
    void LoadOlderMsgsOnTopScroll() =>
        LoadOlderTurns(Math.Max(160, msgScroll.ViewportHeight * 0.5), keepViewport: true);

    /// <summary>会话区当前是否贴底（像素级，20px 容差防浮点/布局抖动）。
    /// 容器未生成或内容不足一屏时视为贴底（滚动无害），保守放行。</summary>
    bool IsMsgAtBottom()
    {
        if (msgScroll == null) return true;
        var extent = msgScroll.ExtentHeight;
        if (extent <= 0) return true;
        return msgScroll.VerticalOffset >= extent - msgScroll.ViewportHeight - 20.0;
    }

    /// <summary>读取输入区工作模式选择器：从角色/模式仓库（工具级 roles/*.json，两级 JSON 免编译热加载）刷新列表，
    /// 保留上次选择。返回所选模式的流程步骤（flow 型=引用 flows 模板步骤；纯提示词型返回 null）。</summary>
    List<GAIRR.AgentHost.FlowStep>? SelectedFlowSteps()
    {
        var prev = (cbMode.SelectedItem as ComboBoxItem)?.Tag as string
            ?? "__auto__";   // 初始空白态（尚无带 tag 的选择）默认"🤖 自动匹配"：启动后空白对话/新建会话固定默认自动匹配，不记忆上次选择
        cbMode.Items.Clear();
        // ── 自动匹配（置顶）：发送前按用户发言自动选定角色/模式，tag=__auto__ 占位，命中后切换为真实 role|mode ──
        cbMode.Items.Add(new ComboBoxItem
        {
            Content = "🤖 自动匹配（角色-模式）",
            Tag = "__auto__",
            Style = FindResource("DarkComboItem") as Style,
            ToolTip = "根据当前发言自动选择最合适的角色/模式（每次发送前判定，判定失败回落默认角色）",
        });
        // ── 角色/模式仓库（每个模式一行"角色 · 模式"，tag = role|mode）──
        foreach (var role in GAIRR.AgentHost.RoleModeStore.LoadAll())
        {
            var rIco = string.IsNullOrWhiteSpace(role.Icon) ? "👤" : role.Icon;
            foreach (var m in role.Modes)
            {
                var mIco = string.IsNullOrWhiteSpace(m.Icon) ? "📋" : m.Icon;
                cbMode.Items.Add(new ComboBoxItem
                {
                    Content = rIco + role.DisplayName + " · " + mIco + m.DisplayName,
                    Tag = role.Name + "|" + m.Name,
                    Style = FindResource("DarkComboItem") as Style,
                    ToolTip = $"【{role.DisplayName} · {m.DisplayName}】\n{m.Description}\n" +
                        $"提示词: {(string.IsNullOrWhiteSpace(m.PromptFile) ? "沿用默认" : m.PromptFile)} | " +
                        $"流程: {(string.IsNullOrWhiteSpace(m.FlowName) ? "自主" : "flows/" + m.FlowName + ".json")}",
                });
            }
        }
        // 兼容旧固定项 tag：启动未填充前的 __agile__/__deep__ 映射到默认角色同名模式；__auto__ 原样保留（自动匹配项已在顶部）
        if (prev.StartsWith("__") && prev != "__auto__")
            prev = "software-engineer|" + (prev == "__deep__" ? "deep" : "agile");
        var match = cbMode.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals(i.Tag as string, prev, StringComparison.OrdinalIgnoreCase));
        cbMode.SelectedIndex = match == null ? 0 : cbMode.Items.IndexOf(match);
        // 返回所选模式解析出的流程步骤（纯提示词自主模式返回 null）
        return CurrentModeFlowSteps();
    }

    /// <summary>解析 cbMode 当前选中项（tag=role|mode，兼容旧 __agile__/__deep__ 固定项）为仓库模式对象；无返回 null。</summary>
    GAIRR.AgentHost.ModeProfile? CurrentModeProfile()
    {
        var tag = (cbMode.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        if (string.IsNullOrWhiteSpace(tag)) return null;
        string role = "software-engineer", mode = "";
        if (tag.StartsWith("__")) mode = tag == "__deep__" ? "deep" : "agile";
        else
        {
            var p = tag.Split('|');
            if (p.Length < 2) return null;
            role = p[0];
            mode = p[1];
        }
        return GAIRR.AgentHost.RoleModeStore.FindMode(role, mode);
    }

    /// <summary>当前所选模式的提示词文件（经角色包解析：包内 prompts/ 存在则返回绝对路径，否则原名走引擎共享池；null=沿用引擎默认）。</summary>
    string? SelectedModePromptFile()
    {
        var m = CurrentModeProfile();
        return m == null ? null : GAIRR.AgentHost.RoleModeStore.ResolvePromptFile(m);
    }

    /// <summary>当前所选模式解析出的流程步骤（flow 型引用 flows 目录模板；纯提示词型返回 null）。</summary>
    List<GAIRR.AgentHost.FlowStep>? CurrentModeFlowSteps()
    {
        var m = CurrentModeProfile();
        return m == null ? null : GAIRR.AgentHost.RoleModeStore.ResolveFlow(m)?.Steps;
    }

    /// <summary>角色/模式下拉展开时按需调整弹层最大高度：内容自然自适应（ItemsPresenter 自动撑高），
    /// 上限=窗口高 50%，下限=默认固定弹层高 240 —— 弹层内 DropScroll（ModelCombo 模板）是独立可视树，
    /// 固定 MaxHeight 在模板里，这里展开瞬间把它改为与当前窗口成比例的可变上限。</summary>
    void OnCbModeDropDownOpened(object sender, EventArgs e)
    {
        if (cbMode.Template?.FindName("DropScroll", cbMode) is not System.Windows.Controls.ScrollViewer sv) return;
        double cap = Math.Max(240.0, ActualHeight * 0.5);
        if (Math.Abs(sv.MaxHeight - cap) > 0.5) sv.MaxHeight = cap;
    }

    /// <summary>工作模式选择变化：把新模式即时同步到全部已建会话 Runner（本叶子：两会话并行各自 Loop 即时生效；
    /// 未建 Runner 的会话在其首个任务 EnsureRunner 建时按其 prompt 对齐，见 EnsureRunner 注释）。
    /// 内置敏捷/严谨沿用旧 AgentMode 全局切换；纯提示词型自定义模式用 SwitchPrompt 换提示词文件；
    /// flow 型（引用流程模板）只影响任务级步骤注入，不即时改已建 Runner 提示词。</summary>
    void OnCbModeChanged(object sender, SelectionChangedEventArgs e)
    {
        // Items.Clear() 触发的 SelectionChanged 跳过（AddedItems 为空）
        if (e.AddedItems.Count == 0) return;
        var tag = (cbMode.SelectedItem as ComboBoxItem)?.Tag as string;
        // 手动切到"🤖 自动匹配"：只记会话选择（AutoRoute=true），不覆写钉住参数（保留最后回合命中记录，
        // 下次发送时 AutoMatchRoleIfNeeded 匹配后同步 Runner），也不即时改已建 Runner 模式（下一回合随命中切换）
        if (tag == "__auto__")
        {
            if (!_syncingSelectors && currentSession != null && !currentSession.IsOrchestration)
                currentSession.AutoRoute = true;
            RefreshTopPinLine();   // 顶部信息栏：🤖 自动匹配 · 模型
            return;
        }
        var mode = CurrentModeProfile();
        if (mode == null) return;
        // P 需求3：把新选 角色/模式 更新到当前会话钉住参数（会话全程执行用钉住的模式；切换会话顶部随之回显）。
        // 会话切换回显置位（_syncingSelectors）期间跳过写回，防止回显触发本回调造成无限回流。
        if (!_syncingSelectors && currentSession != null)
        {
            if (!currentSession.IsOrchestration) currentSession.AutoRoute = false;   // 手选具体角色：会话钉该角色（会话与每回合均按之）
            var rm = SelectedRoleMode();
            if (rm != null)
            {
                currentSession.PinRole = rm.Value.Role;
                currentSession.PinRoleDisplay = rm.Value.RoleDisp;
                currentSession.PinMode = rm.Value.Mode;
                currentSession.PinModeDisplay = rm.Value.ModeDisp;
            }
        }
        RefreshTopPinLine();   // P：会话内改 角色/模式 → 同步顶部 logo 参数标题
        var n = mode.Name.Trim().ToLowerInvariant();
        // P 语义：模式切换即时生效当前会话已建 Runner（各会话钉各自的模式）；无 Runner 的会话由首任务
        // EnsureRunner 按其钉住模式装配提示词。不再把模式全局广播到其它会话的 Runner。
        var curRunner = RunnerOf(currentSession);
        if (n == "agile") { if (curRunner != null) curRunner.Loop.SwitchAgentMode("agile"); return; }
        if (n == "deep") { if (curRunner != null) curRunner.Loop.SwitchAgentMode("deep"); return; }
        // 纯提示词型自定义模式（无流程引用）：把该模式提示词文件（经角色包解析）即时同步到当前会话 Runner
        if (curRunner != null && string.IsNullOrWhiteSpace(mode.FlowName) && !string.IsNullOrWhiteSpace(mode.PromptFile))
            curRunner.Loop.SwitchPrompt(GAIRR.AgentHost.RoleModeStore.ResolvePromptFile(mode));
        // flow 型：仅注入任务级步骤（OnSend 每次赋 Flow），不即时改已建 Runner 提示词，与旧 Flow 模板行为一致
    }

    /// <summary>发送前厂商并发预检（发送入口调用）：与 AgentLoop.RunAsync 内部厂商并发闸同口径——已建 Runner 按会话钉住厂商、
    /// 未建 Runner 按顶部当前模型（首任务将按它钉住）判定。并发已满时弹窗提示并返回 true（调用方中止发送：此时文本尚在
    /// 输入框、未上屏未启动，避免"先上屏/启动、到 RunAsync 里才报并发已达上限"的滞后提示）。</summary>
    bool ProviderFullBeforeSend(GAIRR.AgentHost.SessionRunner? runner)
    {
        var provider = runner?.Loop.Provider;
        if (string.IsNullOrWhiteSpace(provider)) provider = CurProvider;   // 首任务（Runner 未建）按顶部模型对齐
        if (string.IsNullOrWhiteSpace(provider)) return false;             // 无厂商信息不拦（AgentLoop 内部以 cfg.Provider 兜底）
        var max = cfg.MaxConcurrencyFor(provider);
        if (!LlmConcurrencyGate.IsFull(provider, max)) return false;
        var (cur, _) = LlmConcurrencyGate.Current(provider);
        MessageBox.Show(this,
            $"厂商 {cfg.ProviderDisplayName(provider)} 并发已达上限（当前 {cur}/{max}），消息未发送。\n请等待正在进行的任务结束，或改用其它模型后再试。",
            "发送未启动");
        return true;
    }

    async void OnSend(object sender, RoutedEventArgs e)
    {
        SessionItem? sendSession = null;   // 发送发起会话：await 角色匹配期间用户可能切走其它会话（currentSession 会变），
                                           // 其后任务归属/钉住参数/消息上屏一律锚定它，防本次回复串台显示到被查看会话
        try
        {
            var text = inputBox.Text.Trim();
            bool forceWebFullContext = _pendingForceWebFullContext;   // 发送菜单「发送完整上下文」：本次强制网页新会话+整段投完整上下文
            _pendingForceWebFullContext = false;   // 立即消费：早退路径（并发满/编排拦截等）不残留到下次发送
            // 发送前必须先拿到**可落盘的正式会话**，两种待发起态都要处理：
            // ①无会话关联（启动后未点任何会话 / 任务回放视口 currentSession==null）→ 新建会话项，否则 EnsureRec 抛
            //   “任务启动必须存在会话上下文”，首条消息直接失败；
            // ②只有内存占位「新会话」（点过「+ 新会话」但尚未发送，IsPending=true）→ **就地转正**（Id 不变，会话区消息与
            //   列表项直接承接）。这一步绝不能省：占位项在 SaveCurrentSession/SaveSessionHistory 里一律被跳过，
            //   不转正则整段对话永远不会落盘（重启即丢）。
            // 有文本才触发（空输入直接返回：不建会话、也不把占位项转正）。转正/建项在任务启动前**同步**完成，
            // 会话 Id 随即经 EnsureRec 绑定记录、Loop.SetSessionKey 绑定事件归属，下面的 sendSession 也锚定它 ——
            // 事件按 SessionKey 归属 + 发起会话锚定的防串台链路（切走会话时 SSE 回流不写错会话）与原逻辑完全一致。
            // 空输入按发送钮态分流：会话区有历史（钮显「继续」）→ 视同点击自动补发「继续」；无历史（钮已置灰）→ 双保险忽略
            if (text.Length == 0)
            {
                if (messages.Count == 0) return;
                text = "继续";
            }
            forceWebFullContext = forceWebFullContext || IsResumeCommand(text);   // 「继续」类命令发给网页模型同样强制新会话+整段重投完整上下文
            if (EnsureSessionForSend() == null) return;   // 建会话未成（异常兜底）→ 放弃本次发送
            var rec = EnsureRec(currentSession);   // P1② 建/取本会话任务记录（任务启动点在记录上写运行态，两会话互不踩踏）
            if (text.Length == 0 || rec.Cts != null) return;
            // 本会话编排执行中（编排执行器归属本会话记录 PlanRunner 在跑）：只放行编排会话的“继续”等续跑控制指令，
            // 其余一律拦截。仅按当前会话自身判定——A 编排后台执行时只拦 A 自身普通发送（仍可发继续/继续执行等控制语），
            // 切到 B 后 B 的 rec.PlanRunner==null，可正常发主对话，不被 A 后台编排误拦。
            // 拦截本会话自身的并行发送，是防主对话任务与本会话编排执行并行（并行会让 UnlockSend 的恢复被
            // planRunner 分支吞掉，停止后列表仍锁）；其它会话的任务收口走各自记录（PlanRunner==null）不受影响。
            if (rec.PlanRunner != null && !(currentSession?.IsOrchestration == true && IsResumeCommand(text)))
            {
                MessageBox.Show(this, "本会话编排执行中，暂不能发送新消息。可发送「继续」续跑已暂停计划，或先停止本编排后再试。", "编排执行");
                return;
            }
            lastTask = text;
            rec.GitLastKey = currentSession?.Id;   // 任务归属键：本任务收尾自动提交时带上（=会话短 id，git log 反查归属）

            // 离开任务回放视口：后续消息归属普通会话容器（防回放中发送时快照挂错键）
            openRunId = null;
            // 任务启动前拦截：代码页编辑未保存时先保存/放弃（任务运行中禁止编辑，避免 UI 与 Agent 写盘竞争）
            if (!EnsureNoCodeEditingBeforeRun()) return;

            // 编排会话（阶段 3）：Generating 阶段禁止问答注入（等待树生成结束）；
            // Collecting 阶段收到收敛指令则切换状态机到 Generating。阶段判定以框架层 loop.OrchMode 为准，
            // SessionItem.OrchPhase 仅同步保存供序列化/重启还原。
            if (currentSession?.IsOrchestration == true)
            {
                if (rec.MultiGenCts != null)
                {
                    MessageBox.Show(this, "多模型决策生成中，请等待生成完成后确认，暂不能追加输入。", "编排会话");
                    return;
                }
                // 方案已确认并等待手工执行：除“继续”等续跑指令（paused 计划续跑）外不再接受普通消息
                if (currentSession.OrchPhase == "done" && !IsResumeCommand(text))
                {
                    MessageBox.Show(this, "方案已确认并等待手工执行。如需重新生成方案，请新建编排会话。", "编排会话");
                    return;
                }
                // 本会话自身处于 Generating：若本会话产树/决策任务真在跑（rec.Cts/MultiGenCts/PlanRunner 任一非空），
                // 前面 P3 入口拦截已静默返回或提示，不会走到这里——能走到这里即说明本会话已无任何在跑任务，
                // generating 是遗留态而非进行时（应用重启中断 / EventBg 后台 Finished 收口被跳过 / 前台收口异常未复位）。
                // 此时直接弹“任务树生成中”会永久卡死本会话输入；改为复位回 collecting 并放行本次输入——
                // 若文本恰为收敛指令（如“生成任务树”），下方 IsGenerateCommand 分支会立即重新切到 Generating 再次产树。
                if (currentSession.OrchPhase == "generating")
                {
                    SyncOrch(currentSession, "collecting");   // 框架阶段同步回收集（仅本会话 Runner）
                    currentSession.OrchPhase = "collecting";
                    SaveCurrentSession();
                    UpdateOrcPanel();
                }
                // 交互式收集会话：收到收敛指令（或点“生成任务树”按钮注入的文本）则切换状态机到 Generating 产树；
                // multi 模式不走主持人问答（由后台 PlanNegotiator 决策），无需此切换
                if (currentSession.Mode != "multi"
                    && GAIRR.AgentHost.OrchestrationSession.IsGenerateCommand(text))
                {
                    CurRunner?.Loop.SwitchOrch("generating");   // 本叶子：Runner 已建则即时切阶段状态机；未建则由 EnsureRunner 按其 OrchPhase 对齐
                    currentSession.OrchPhase = "generating";
                    SaveCurrentSession();
                    UpdateOrcPanel();
                }
                // 中断后回复“继续”：续跑当前会话关联的 paused 计划（保留已完成叶子与交接上下文）
                if (IsResumeCommand(text))
                {
                    var pid = currentSession.PlanId;
                    var myPlan = pid != null ? GAIRR.AgentHost.PlanStore.GetById(cfg.ProjectRoot, pid) : null;
                    if (myPlan?.Status == "paused")
                    {
                        if (rec.PlanRunner != null)
                            rec.PlanRunner.Resume();   // 实例仍在（run 层暂停）：直接解除暂停
                        else
                            StartPlanExecution(pid!);   // 实例已结束：新建续跑
                        return;   // 输入作为续跑指令消费，不进入主对话发送
                    }
                    // 续跑指令但计划不在暂停态（实例仍在 running，或 PlanId 缺失）：编排执行中不允许并行发送/追加素材
                    if (rec.PlanRunner != null)
                    {
                        MessageBox.Show(this, "计划正在执行（非暂停状态），不能发送消息或追加素材。请先停止该计划。", "编排执行");
                        return;
                    }
                }
                // 多模型决策会话：普通消息 = 追加素材（目标/约束/参考说明），保存后自动触发一次生成
                if (currentSession.Mode == "multi")
                {
                    PromoteSessionOnActivity(currentSession);   // 追加素材同属用户发言：与主发言路径同口径前置
                    AddMessage(new ChatMessage
                    {
                        Kind = MsgKind.User,
                        Who = "你（刚才）",
                        Text = text,
                    });
                    inputBox.Clear();
                    ScrollToBottom();
                    SaveCurrentSession();
                    StartMultiPlanGen();
                    return;   // 素材消息不进入主对话 loop（multi 由 PlanNegotiator 后台决策）
                }
            }

            // 本叶子：两会话前台主对话并行——已删除单前台全局门（ForegroundBusyAnywhere）。本会话 rec.Cts!=null
            // 已在入口拦截；其它会话在跑不影响本会话新起任务（各会话 Runner 独立并行，不共享同一 AgentLoop）。

            // 发送前并发预检：与 RunAsync 内部厂商并发闸同口径。并发满即在此提示并中止（文本留在输入框，改模型后可重发），
            // 不再"先上屏/启动、发给大模型前才被拒报错"。
            if (ProviderFullBeforeSend(rec.Runner))
            {
                // 编排收集会话若在拦截前已切 generating（收敛/生成任务树指令路径）：任务未启动，复位回 collecting，避免界面滞留"生成中"
                if (currentSession?.IsOrchestration == true && currentSession.OrchPhase == "generating")
                {
                    SyncOrch(currentSession, "collecting");
                    currentSession.OrchPhase = "collecting";
                    SaveCurrentSession();
                    UpdateOrcPanel();
                }
                return;
            }

            // 锁定发送：隐藏发送按钮，显示停止按钮；输入框保持可输入（发送由 cts != null 拦截）
            sendBtn.Visibility = Visibility.Collapsed;
            sendMenuBtn.Visibility = Visibility.Collapsed;   // 运行中同步隐藏下拉箭头
            stopBtn.Visibility = Visibility.Visible;
            btnOrchGen.IsEnabled = false;   // 运行期同步禁用生成钮（编排收集会话在等回复时防重复收口无效点击）

            // 用户发言：按活跃前置策略决定是否把本会话跳到组首（前 N 名内发言不动列表）
            PromoteSessionOnActivity(currentSession);
            AddMessage(new ChatMessage
            {
                Kind = MsgKind.User,
                Who = "你（刚才）",
                Text = text,
            });
            // 用户主动发送：强制置底（发送动作本身代表要看最新回复）
            ScrollToBottom();
            inputBox.Clear();
            // 角色自动匹配：顶部选中"🤖 自动匹配"时，先按本次发言判定角色/模式（等效手动改选，随后走原钉住链路）。
            // 会话选择 = 发言时选择器状态：写回会话 AutoRoute——切会话顶部回显/顶部信息栏显示
            // 以该字段为准（true=自动，每回合独立重匹配；false=钉具体角色，会话与每回合均按之）
            if (currentSession != null && !currentSession.IsOrchestration)
                currentSession.AutoRoute = (cbMode.SelectedItem as ComboBoxItem)?.Tag as string == "__auto__";
            // Cts 提前创建：await 期间拦截重发（入口 rec.Cts!=null 拦截）+ 停止钮可即时取消 LLM 判定。
            // 改 await（原 GetAwaiter().GetResult() 同步阻塞 UI 线程最长 30s = 选自动匹配点发送后"死机"）
            rec.Cts = new CancellationTokenSource();
            sendSession = currentSession;   // 锚定发起会话：await 匹配期间用户切走时 currentSession 会变，任务归属/上屏以此为准
            LockSessionSwitch(sendSession?.Id ?? "");   // 会话切换锁：SSE 流绑定前（首个回复 token 到达前）禁止切换会话（60 秒超时自动解锁）
            await AutoMatchRoleIfNeeded(sendSession, text, rec.Cts.Token);
            if (rec.Cts.IsCancellationRequested)
            {
                // 匹配期间被停止：任务未启动，复位运行态并解锁发送区（不进入后续执行链路）
                rec.Cts = null;
                rec.StopRequested = false;
                UnlockSend();
                UnlockSessionSwitch();   // 任务未启动：立即解除会话切换锁（锁只可能属于当前会话，无条件释放安全）
                AddMessage(new ChatMessage { Kind = MsgKind.Agent, Who = "GAIRR", Text = "已停止（角色自动匹配未完成，任务未启动）" });
                return;
            }
            // P：每次执行开始前确认本会话钉住 角色/模式/模型（首任务取样顶部选择器固定；已钉住保持会话参数不回溯）
            EnsureSessionPin(currentSession);
            // 首任务惰性建本会话 Runner（普通会话/主对话路径与编排 SendInitialPrompt 同源）：两会话各持独立
            // AgentLoop/上下文/Bus，常驻本会话 TaskRecord 至会话删除；已建会话直接复用，不重建不串扰。
            // 建好后 rec.Runner 非空，下方 Flow/SetSessionKey/RunAsync 才有运行载体（此前普通会话首任务
            // rec.Runner==null 于 Flow 赋值处空引用必抛"发送失败"）。
            EnsureRunner(currentSession);
            MarkRunStart(currentSession);   // 会话状态：进入进行中（会话树行桔点桔字 + 顶部"会话"徽标计数）
            // 立即进度提示：发送即出工作消息与进度行，不等模型首响应；顶部信息条进入任务态
            EnsureWorkMsg();   // 工作消息经 workMsg 代理挂到当前会话任务记录（任务刚锁当前会话，记录=上面 rec，两者一致）
            rec.WorkMsg!.IsRunningTool = false;
            rec.WorkMsg.IsWaitingForModel = true;
            rec.CurrentTodo = null;
            rec.StepCardMap.Clear();
            rec.TaskActive = true;
            RefreshTodoBar();
            UpdateChatTopInfo();
            rec.TaskStart = DateTime.Now;
            rec.WorkMsg.Steps = "[开始] 任务已发送 · 第 1 轮 · 模型思考中…";
            rec.WorkMsg.Refresh();
            // 本次任务工作模式：自主（默认）或 Flow 模板（框架逐步驱动；RunAsync 消费后自动置空）。
            // 自动匹配态下拉恒为 __auto__，流程模板取 _autoMode（命中模式/回落默认）的引用流，与真实选择该模式的路径等效
            rec.Runner!.Loop.Flow = _autoMode != null
                ? GAIRR.AgentHost.RoleModeStore.ResolveFlow(_autoMode)?.Steps
                : SelectedFlowSteps();
            // 同步会话标识给 AgentLoop：请求上下文快照按会话 Id 落盘 data/rounds/&lt;id&gt;/（每轮请求前由 Agent 侧拍盘）
            rec.Runner!.Loop.SetSessionKey(currentSession?.Id ?? "");
            // 在后台线程运行本会话的 Runner（两会话并行各持独立 AgentLoop，互不共享同一执行循环），避免阻塞 UI
            _ = Task.Run(async () => await rec.Runner!.Loop.RunAsync(ApplySlashSkill(text), rec.Cts.Token, forceWebFullContext));
        }
        catch (Exception ex)
        {
            AddMessage(new ChatMessage
            {
                Kind = MsgKind.Agent,
                Who = "GAIRR",
                Text = "发送失败：" + ex.Message,
            });
            UnlockSend();   // 异常路径同样解锁发送区（与编排 SendInitialPrompt catch 同源），避免按钮锁死在停止态
            UnlockSessionSwitch();   // 异常路径同样解除会话切换锁，避免切换入口卡死禁用
        }
    }

    /// <summary>失败/异常气泡「重试」：与用户手工在当前会话输入“继续”并点发送完全同链路——
    /// 复用 OnSend（含会话归属/并发/编排/并发闸守卫与上屏收口），仅自动注入“继续”文本；
    /// 不吞并输入框内正在打的草稿（发出后原位放回）。编排会话语义不同（“继续”= 续跑已暂停计划，
    /// 入口在计划面板），不在此提供，避免误触发恢复。</summary>
    void OnRetryBubbleClick(object sender, RoutedEventArgs e)
    {
        if (currentSession == null) return;
        var rec = EnsureRec(currentSession);
        // 会话正忙（本会话任务运行中）：提示等待，避免静默无效点击
        if (rec.Cts != null)
        {
            MessageBox.Show(this, "当前会话正在执行任务，请等待本轮结束（或先点停止）后再重试。", "重试",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        // 编排会话：等效“继续”走的是续跑已暂停计划路径，语义不同，指引用户用计划面板入口
        if (currentSession.IsOrchestration)
        {
            MessageBox.Show(this, "编排会话请使用计划面板的「继续执行」入口恢复，不要用本条气泡重试。", "重试",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var saved = inputBox.Text;
        inputBox.Text = "继续";
        OnSend(sendBtn, new RoutedEventArgs());
        // OnSend 在首个 await 前已同步 Clear 输入框：
        // - 文本原样保留 = 被入口守卫拦截未消费（如并发闸满）→ 还原草稿；无草稿则保留“继续”便于手点重发
        // - 文本已被消费 = “继续”已作为本轮用户消息发出 → 若有原草稿则还原，不吞正在打的字
        if (inputBox.Text == "继续")
        {
            if (!string.IsNullOrWhiteSpace(saved)) inputBox.Text = saved;
        }
        else if (!string.IsNullOrWhiteSpace(saved))
        {
            inputBox.Text = saved;
        }
    }

    /// <summary>/技能名 强制触发：识别文本中的 /技能名 标记（开头手打或右键追加到末尾均可），
    /// 注入"先 LoadSkill 加载全文"指令，其余原文（去掉该标记）附后</summary>
    string ApplySlashSkill(string text)
    {
        // 从文本中定位第一个可识别的 /技能名 标记：开头（手打）或行首/末尾（右键追加）
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"/([A-Za-z0-9_-]+)"))
        {
            var skill = skillLoader.Find(m.Groups[1].Value);
            if (skill == null) continue;
            // 去掉标记本身，其余原文（含其他引用标记）保留为用户请求
            var rest = (text.Remove(m.Index, m.Length) + " ").Trim();
            return $"[用户强制指定技能 {skill.Name}] 先调 LoadSkill 取 \"{skill.Name}\" 的完整指令，再按技能步骤执行。\n用户请求：{rest}";
        }
        return text;
    }
    
    // ─────────── 会话运行状态（会话树行圆点/标题 + 顶部"会话"计数徽标；0空闲/1进行中/2锁等待/3中断/4完成） ───────────
    // P1②：两会话并行。实例级"当前任务"运行态字段（cts/multiGenCts/workMsg/currentTodo/dangerCard/_stopRequested/
    // gitLastKey/taskStart/taskActive/stepCardMap 等）收拢为 per-session TaskRecord，按 SessionItem.Id(8位短GUID) 索引，
    // 每运行会话一份，两会话并行时各自状态/取消源/停止标记/收口归属互不踩踏。TaskRecord 定义见类尾。
    readonly Dictionary<string, TaskRecord> runTasks = new();

    /// <summary>取指定会话的任务记录（无则 null）</summary>
    TaskRecord? TaskOf(SessionItem? s) => s != null && runTasks.TryGetValue(s.Id, out var r) ? r : null;

    /// <summary>当前前台会话任务记录（无会话/无记录则 null）</summary>
    TaskRecord? CurRec => currentSession != null && runTasks.TryGetValue(currentSession.Id, out var r) ? r : null;

    /// <summary>会话记录运行缓冲复位（P6②）：仅当记录存在且无活动任务（无前台 cts/无后台多模型决策/无编排执行）时，
    /// 清其工作消息、打字机积压/定时器、工具卡等残留缓冲；有活动任务的记录原样保留——任务切到后台照跑，
    /// 缓冲随所属会话记录保存，切回后运行进度/结果完整可查。由 OpenSession 切换后调用，只影响目标会话记录。</summary>
    static void ResetSessionBuffers(TaskRecord? rec)
    {
        if (rec == null || rec.AnyRunning) return;
        rec.WorkMsg = null;
        rec.PendingStream = null;
        rec.StreamTicker?.Stop();
        rec.CardMap.Clear();
        rec.StepCardMap.Clear();
        rec.AccTokens = 0;
    }

    /// <summary>取或建指定会话任务记录（各任务启动点统一经此建账）</summary>
    TaskRecord EnsureRec(SessionItem? s)
    {
        if (s == null) throw new InvalidOperationException("任务启动必须存在会话上下文");
        if (!runTasks.TryGetValue(s.Id, out var r)) runTasks[s.Id] = r = new TaskRecord();
        // 会话级打字机节流定时器：首个任务建账时创建，ClearRec 不清（同会话下个任务复用），删会话随记录移除。
        // 用窗口 Dispatcher 构造，保证后台线程建账（如自动任务）时定时器仍归属 UI 线程可触发
        if (r.StreamTicker == null)
            r.StreamTicker = new System.Windows.Threading.DispatcherTimer(
                TimeSpan.FromMilliseconds(30),
                System.Windows.Threading.DispatcherPriority.Background,
                StreamTickerTick, Dispatcher);
        return r;
    }

    /// <summary>任务收口/取消后清空会话任务记录的全部运行态（保留空记录供下个任务复用；删除会话时整条移除）。
    /// 编排执行（PlanRunner 归属）与前台任务并行期间互不清除对方字段。</summary>
    void ClearRec(SessionItem? s)
    {
        var r = TaskOf(s); if (r == null) return;
        r.Cts?.Dispose(); r.Cts = null;
        r.MultiGenCts = null;
        r.WorkMsg = null;
        r.CurrentTodo = null;
        r.DangerTicker?.Stop(); r.DangerTicker = null;
        r.DangerCard = null;
        r.DangerResolve = null;
        if (dangerMobilePollRec == r) StopDangerMobilePoll();   // 挂起卡所属记录被清：同步停手机端决策轮询
        r.StopRequested = false;
        r.TaskActive = false;
        r.AutoHitText = null;
        r.GitLastKey = null;
        r.TaskStart = default;
        r.StepCardMap.Clear();
    }

    /// <summary>中止指定会话运行器并收口（本叶子：删除会话/切项目/退出共用同一出口）。
    /// 停其全部任务源（编排执行器/主对话产树/多模型决策）与打字机、危险卡定时器，释放记录上的活动字段；
    /// persist=true（切项目/退出）时把该会话最后一轮工作消息按"已中止"收口写回归属会话历史并落盘——
    /// 后台/切走放行会话不依赖孤儿异步收口（记录随后被摘除、收口事件按 drain 归属守卫丢弃）也能留档，重启/切回历史完整。</summary>
    void StopSessionRunner(TaskRecord rec, bool persist)
    {
        if (rec == null) return;
        try
        {
            if (rec.PlanRunner != null) { var pr = rec.PlanRunner; rec.PlanRunner = null; try { pr.Stop(); } catch { } }
            if (rec.Cts != null) { rec.StopRequested = true; try { rec.Cts.Cancel(); } catch { } }
            if (rec.MultiGenCts != null) try { rec.MultiGenCts.Cancel(); } catch { }
            if (rec.Runner != null) { try { rec.Runner.Cancel(); } catch { } rec.Runner = null; }  // 主对话宿主双保险（Loop 取消令牌即 rec.Cts）
            if (rec.StreamTicker != null) rec.StreamTicker.Stop();
            if (rec.DangerTicker != null) { rec.DangerTicker.Stop(); rec.DangerTicker = null; }
            if (rec.DangerCard != null) { rec.DangerCard.Visible = false; rec.DangerCard = null; }
            rec.DangerResolve = null;  // 不再有人工裁决入口：Cancel 已传播，Loop 挂起随令牌退出释放
            // 中断快照（persist 场景）：最后一轮工作消息按收口口径写回归属会话（与 PersistBgResult 相同半成品替换逻辑，不重复入列）
            var wm = rec.WorkMsg;
            if (persist && wm != null)
            {
                rec.WorkMsg = null;
                if (string.IsNullOrEmpty(wm.Steps)) wm.Steps = "[中止] 已因切换项目/退出应用中断，本轮未完成。";
                wm.Finished = true;
                if (FindOwn(rec) is { } own) PersistBgResult(own, wm);
            }
            ClearRec(FindOwn(rec));  // 清记录全部运行态（Cts Dispose 等）；记录本身是否摘除由调用方决定
        }
        catch { }
    }

    /// <summary>按会话运行记录反查归属会话项（前台当前会话优先，否则扫全历史按记录匹配）</summary>
    SessionItem? FindOwn(TaskRecord rec)
    {
        if (ReferenceEquals(rec, CurRec)) return currentSession;
        foreach (var s in sessionHistory)
            if (TaskOf(s) != null && ReferenceEquals(TaskOf(s), rec)) return s;
        return null;
    }

    /// <summary>统一中止全部会话运行器并清空运行中枢（Hub 摘出，本叶子：切换项目/应用退出共用）。
    /// 逐会话 StopSessionRunner(persist:true) 落盘中止快照后清空 runTasks——后台孤儿线程随后发出的收口事件
    /// 因记录已摘除、被 drain 归属守卫（SessionKey 不在 runTasks 且非当前会话）丢弃，不触碰任何会话 UI/记录；
    /// 无悬挂任务/定时器/线程。currentSession 摘除后各代理属性（cts/planRunner/workMsg 等）随之归空。</summary>
    void ShutdownAllSessionRunners()
    {
        if (runTasks.Count != 0)
        {
            foreach (var rec in runTasks.Values.ToList()) StopSessionRunner(rec, persist: true);
            runTasks.Clear();
        }
        ClearAllBusy();   // 退出/切项目：统一清空任务运行标记（无任务时也清，兼作异常残留兜底）
    }

    /// <summary>会话运行任务记录（P1②：两会话并行）。原 MainWindow 实例级"当前任务"字段收拢为 per-session，
    /// 按 SessionItem.Id（8 位短 GUID）索引，每运行会话一份，两会话并行时各自任务状态/取消源/停止标记/收口归属互不踩踏。
    /// 记录生命周期：任务启动 EnsureRec 建账/复用 → 收口/取消后 ClearRec 清运行态（保留空记录）；删除会话时随 runTasks 整条移除。</summary>
    sealed class TaskRecord
    {
        public CancellationTokenSource? Cts;          // 前台主对话/编排产树任务取消源
        public CancellationTokenSource? MultiGenCts;  // 多模型后台决策取消源
        public GAIRR.AgentHost.SessionRunner? Runner;   // 会话运行器（本叶子：每会话一个常驻，两会话前台主对话并行互不中断；首任务 EnsureRunner 建）
        public GAIRR.AgentHost.PlanRunner? PlanRunner; // 编排执行器（归属本会话的后台叶子任务；同步实例 planRunner 单例）
        public GAIRR.AgentHost.PlanDto? PendingPlan;  // 多模型方案后台生成成功时挂起（收口时刻前台已切走）：切回本会话时补弹确认（OpenSession 消费）
        public SessionItem? OrcOwner;                  // 编排执行归属会话（与实例 orchOwner 同步；记录内冗余便于两会话并行判定）
        public ChatMessage? WorkMsg;                   // 正在生成的工作消息
        public TodoItem? CurrentTodo;                  // 待办清单卡（顶部 todoBar 进度）
        public DangerConfirmItem? DangerCard;          // 危险确认卡（连带7：会话级各自挂起/出卡）
        public System.Windows.Threading.DispatcherTimer? DangerTicker; // 危险卡倒计时
    public Action<bool>? DangerResolve;  // 危险决策路由（建卡时捕获：叶子卡→叶子会话，其余→主 loop），点哪张卡解哪把锁
        public bool StopRequested;                     // 本任务被用户请求停止（取代实例级 _stopRequested：停止只作用所属会话）
        public bool TaskActive;                        // 顶部信息条强制显示待办进度
        public string? GitLastKey;                     // git 提交归属标识（任务启动点记录，收尾自动提交消费）
        public DateTime TaskStart;                     // 任务开始时刻（Steps 信息条累计用时基准）
        public string? PendingStream;                  // 流式打字机积压文本
        public System.Windows.Threading.DispatcherTimer? StreamTicker; // 打字机节流定时器
        public int AccTokens;                          // 当前任务累计 token
        public DateTime LiveThinkStart;                // 深度思考直播卡计时基准
        public string? AutoHitText;                    // 本轮自动匹配命中的 角色/模式 展示文本：开始执行起至收口，显示到 GAIRR 头像标题/顶部 logo 标题（SessionPinText 共用）；收口清除恢复"自动匹配"显示
        public Dictionary<int, ToolCall> StepCardMap { get; } = new(); // 步骤号 → 步骤生命周期卡片
        public Dictionary<Guid, ToolCall> CardMap { get; } = new();    // 工具 Guid → 工具卡（工具执行过程卡片）
        /// <summary>是否有本会话任务在跑（两会话并行时各自独立判定：前台主任务/多模型决策/编排执行）</summary>
        public bool AnyRunning => Cts != null || MultiGenCts != null || PlanRunner != null;
    }

    /// <summary>任务启动：把目标会话置为进行中(1/桔点桔字)并刷新顶部会话徽标。由各启动点（主对话/产树/多模型/计划执行）调用。</summary>
    // ---- 桌面任务运行标记（手机端接入/发送拦截用，Server 读 data/busy_<会话Id>.json 判断“桌面端正在执行该会话”）----
    // 写点 = MarkRunStart（主对话/产树/多模型/编排执行各启动点统一入口）：busy 文件存在即拦手机端 resume/发消息；
    // 删点 = MarkRunEnd（各任务收口统一入口）：任务结束才放行；退出/切项目 ShutdownAllSessionRunners 清全部；启动兜底清理残留。
    static string BusyPath(string? sessionId) =>
        System.IO.Path.Combine(GAIRR.Core.Paths.DataDir, "busy_" + (sessionId ?? "") + ".json");

    void WriteBusy(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        try { System.IO.File.WriteAllText(BusyPath(sessionId), "{\"at\":\"" + DateTime.Now.ToString("s") + "\"}"); }
        catch { }
    }

    void ClearBusy(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        try { var p = BusyPath(sessionId); if (System.IO.File.Exists(p)) System.IO.File.Delete(p); }
        catch { }
    }

    void ClearAllBusy()
    {
        try { foreach (var f in System.IO.Directory.GetFiles(GAIRR.Core.Paths.DataDir, "busy_*.json")) System.IO.File.Delete(f); }
        catch { }
    }

    // ──── 手机端服务开关（左下角版权行右侧 DarkSwitch）：调 mobile-service.bat start/stop 启停本机 Server+SSH 公网隧道 ────
    bool mobileSvcBusy;   // 启停执行中：期间点击以真实状态回摆，不重复触发
    bool mobileSvcUp;     // 当前真实状态缓存（127.0.0.1:8123 可达）
    bool closingStoppingSvc;   // 关窗停服进行中：防 Close() 二次触发时重复拦截
    bool trayExitRequested;    // 托盘"退出程序"发起：允许跳过"最小化到托盘"直接走关窗收尾（含停服）
    MobileSvcTray? mobileTray; // 手机端服务系统托盘（常驻右下角：启停服务/打开主窗口/显示会话数），服务运行中关窗时驻留托盘不退出

    /// <summary>从程序目录向上（最多 6 级）找 mobile-service.bat：发布版(publish/)上 1 级=项目根；Debug 上 4 级=项目根。</summary>
    static string? FindMobileSvcBat()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
        {
            var p = System.IO.Path.Combine(dir.FullName, "mobile-service.bat");
            if (System.IO.File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>本机 Server 可达性探测（同机 connect refused 毫秒级返回，不做网络等待）</summary>
    static bool MobileSvcLocalUp()
    {
        try { using var t = new System.Net.Sockets.TcpClient(); t.Connect("127.0.0.1", 8123); return true; }
        catch { return false; }
    }

    /// <summary>开关点击：开→bat start（Server+公网隧道）；关→bat stop（断隧道+停 Server）；执行完回读状态纠正开关。</summary>
    async void OnMobileSvcToggle(object sender, RoutedEventArgs e)
    {
        var sw = (System.Windows.Controls.Primitives.ToggleButton)sender;
        if (mobileSvcBusy || sw.IsChecked == mobileSvcUp)
        {
            sw.IsChecked = mobileSvcUp;   // 执行中/与真实状态不符：以真实状态为准回摆
            return;
        }
        var bat = FindMobileSvcBat();
        if (bat == null)
        {
            sw.IsChecked = mobileSvcUp;
            ShowToast("未找到 mobile-service.bat（应放在程序目录或其上级）");
            return;
        }
        var wantOn = sw.IsChecked == true;
        if (e.OriginalSource != null) SaveMobileSvcIntent(wantOn);   // 仅真实用户点击持久化意图：关窗自动停服/启动自动恢复均为程序化调用（OriginalSource=null），不改写用户记忆
        mobileSvcBusy = true;
        try
        {
            await RunMobileBat(bat, wantOn ? "start" : "stop");
        }
        finally
        {
            mobileSvcBusy = false;
            await Task.Delay(500);   // Server/隧道进程就绪需一点时间，稍候再探测回读
            mobileSvcUp = MobileSvcLocalUp();
            sw.IsChecked = mobileSvcUp;
            ShowToast(wantOn == mobileSvcUp
                ? (wantOn ? "手机端服务已开启（手机可经 app.gairr.com 连接）" : "手机端服务已关闭")
                : "手机端服务操作未达预期，请查看 mobile-service 窗口提示");
        }
    }

    /// <summary>启动恢复手机端服务开关：读 persisted 用户意图，上次为开 → 复用开关启停整套逻辑自动拉起
    /// （本机已有残留 Server 时 OnMobileSvcToggle 的回摆守卫直接点亮、不重复 start）；为关或无记录 → 保持默认关。</summary>
    void RestoreMobileSvcState()
    {
        try
        {
            bool wantOn;
            try { wantOn = System.IO.File.ReadAllText(GAIRR.Core.Paths.MobileSvcState).Trim() == "on"; }
            catch { return; }   // 无记录/读取失败：保持默认关
            if (!wantOn) return;
            mobileSvcUp = MobileSvcLocalUp();          // 先探测本机残留 Server，防重复 start
            mobileSvcSwitch.IsChecked = true;
            OnMobileSvcToggle(mobileSvcSwitch, new RoutedEventArgs());   // 程序化调用（OriginalSource=null）：不改写 persisted 意图
        }
        catch { }
    }

    /// <summary>持久化手机端服务开关用户意图（data/mobile_svc.state 写 on/off，原子写：临时文件+替换）；
    /// 仅用户真实点击拨动时调用——关窗自动停服、启动自动恢复均为程序化调用，不改写用户意图。</summary>
    static void SaveMobileSvcIntent(bool on)
    {
        try
        {
            var p = GAIRR.Core.Paths.MobileSvcState;
            var tmp = p + ".tmp";
            System.IO.File.WriteAllText(tmp, on ? "on" : "off");
            System.IO.File.Move(tmp, p, true);
        }
        catch { }
    }

    // ──── 手机端服务系统托盘（右下角常驻）：开启/退出服务、打开主窗口、显示实时会话数 ────
    void InitMobileTray()
    {
        mobileTray = new MobileSvcTray();
        mobileTray.ToggleRequested += OnTrayToggleSvc;
        mobileTray.OpenWindowRequested += OnTrayOpenWindow;
        mobileTray.ExitRequested += OnTrayExitProgram;
    }

    /// <summary>托盘菜单"开启服务/退出服务"：与左下角开关同一套逻辑——复用 OnMobileSvcToggle 启停 bat；
    /// 托盘操作视为真实用户操作，先持久化意图再拨动开关（开关若与真实状态一致会由守卫回摆，不重复启停）。</summary>
    void OnTrayToggleSvc(bool on)
    {
        if (mobileSvcBusy)
        {
            ShowToast("手机端服务正在启停中，请稍候");
            return;
        }
        SaveMobileSvcIntent(on);   // 托盘操作等同用户点击：持久化用户意图
        mobileSvcSwitch.IsChecked = on;
        OnMobileSvcToggle(mobileSvcSwitch, new RoutedEventArgs());
    }

    /// <summary>托盘菜单"打开主窗口"：恢复/显示/激活窗口（从托盘或最小化状态召回）。</summary>
    void OnTrayOpenWindow()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;   // 闪一下置顶抢焦点，随后让位
    }

    /// <summary>关闭并真正退出程序：置托盘退出标志（跳过"服务开启时最小化到托盘"的拦截），
    /// 服务开启着则由 OnWindowClosing 停服收尾——供登录取消等程序化强制退出路径调用。</summary>
    public void ForceExit()
    {
        trayExitRequested = true;
        Close();
    }

    /// <summary>托盘菜单"退出程序"：置退出标志后关闭主窗口——服务开启时由 OnWindowClosing 走停服收尾，否则直接退出。</summary>
    void OnTrayExitProgram()
    {
        trayExitRequested = true;
        Close();
    }

    /// <summary>隐藏窗口执行 mobile-service.bat（30s 兜底；start 的隧道由 bat 内 PowerShell 以隐藏后台方式启动为独立进程，本进程退出不影响）</summary>
    static async Task RunMobileBat(string bat, string arg)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c \"\"{bat}\" {arg}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return;
            await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch { }
    }

    void MarkRunStart(SessionItem? s)
    {
        if (s == null) return;
        EnsureRec(s).StopRequested = false;
        s.RunState = 1;
        s.ResetListProgress();   // 新任务启动：会话列表右侧进度清零（下次 Round/Todo 刷新）
        UpdateTabBadges();
        WriteBusy(s.Id);   // 任务运行标记：任务生命周期内手机端对该会话的接入/发送被 Server 拦截（问题2 规格）
        UpdateGpuMonitor();   // 本地模型有任务在跑 → 标题栏 GPU 三色柱现身
    }

    /// <summary>任务收口：completed=true→完成(4/绿，点击会话恢复默认色)；停止/失败/取消→中断(3/红)。</summary>
    void MarkRunEnd(SessionItem? s, bool completed)
    {
        if (s == null) return;
        s.RunState = completed ? 4 : 3;
        if (TaskOf(s) is { } r) r.StopRequested = false;
        UpdateTabBadges();
        ClearBusy(s.Id);   // 任务收口：删除运行标记，手机端恢复接入/发送该会话
        UpdateGpuMonitor();   // 全部会话都不在跑了 → GPU 三色柱收起（两会话并行时另一个仍在跑则继续显示）
    }

    /// <summary>顶部"会话"按钮徽标：统计普通+编排会话中处于 进行中(1)/锁等待(2)/中断(3) 的个数；
    /// 存在中断(3)→红色徽标，否则随按钮前景；无执行中任务→隐藏徽标。由状态变更点调用。</summary>
    void UpdateTabBadges()
    {
        if (tabHistoryBadge == null) return;
        var n = 0;
        var hasPaused = false;
        foreach (var s in sessionHistory)
        {
            if (s.RunState >= 1 && s.RunState <= 3)
            {
                n++;
                if (s.RunState == 3) hasPaused = true;
            }
        }
        if (n == 0)
        {
            tabHistoryBadge.Text = "";
            tabHistoryBadge.Visibility = Visibility.Collapsed;
            return;
        }
        tabHistoryBadge.Text = $"({n})";
        tabHistoryBadge.Visibility = Visibility.Visible;
        tabHistoryBadge.Foreground = hasPaused ? (Brush)FindResource("PauseBrush") : null;
    }

    /// <summary>计划审批弹窗（PlanPending 前台事件 / 两会话切回补弹共用）：组装计划全文，返回用户是否批准（Yes=true）。</summary>
    static bool PromptPlanApproval(Plan plan)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Agent 生成了以下执行计划：");
        sb.AppendLine();
        sb.AppendLine($"概要：{plan.Summary}");
        sb.AppendLine();
        for (int i = 0; i < plan.Steps.Count; i++)
        {
            var st = plan.Steps[i];
            sb.AppendLine($"{i + 1}. [{st.Action}] {st.Target}: {st.Description}");
        }
        sb.AppendLine();
        sb.Append("是否批准执行？");
        return MessageBox.Show(sb.ToString().TrimEnd(), "计划审批", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    /// <summary>会话树"待决策提醒"角标统一刷新（两会话并行验收：各会话挂起角标各归各、互不串台）。
    /// 规则：非当前会话存在人工介入点挂起才亮角标——来源=该会话 TaskRecord 挂起状态，逐会话独立判定：
    ///   1=危险命令待裁决（归属记录 DangerCard 在卡，含后台切走挂起/前台出卡）；
    ///   2=计划审批/方案待确认（归属 Runner.Loop.PlanPending，或 TaskRecord.PendingPlan 切回补弹确认）。
    /// 当前会话（正在视口处理决策卡）不亮；切回/裁决/任务收口/删除/切项目后本轮扫描自动归 0，不残留脏角标。
    /// 每 uiTimer drain 轮末调用（后台事件实时更新 ≤100ms），并在切会话/裁决/切项目等路径同步触发保证即时。</summary>
    void RefreshPendingBadges()
    {
        var curId = currentSession?.Id;
        foreach (var s in sessionHistory)
        {
            var kind = 0;
            if (s.Id != curId && runTasks.TryGetValue(s.Id, out var rec))
            {
                if (rec.DangerCard != null) kind = 1;
                else if (rec.Runner?.Loop.PlanPending == true || rec.PendingPlan != null) kind = 2;
            }
            if (s.PendingKind != kind) s.PendingKind = kind;
        }
    }

    /// <summary>收起危险确认卡（幂等）：停止倒计时并隐藏卡片。任务收口（Finished/Failed）/点停止/停止编排时兜底调用。</summary>
    void HideDangerCard()
    {
        if (dangerCard == null) return;
        dangerTicker?.Stop();
        dangerTicker = null;
        StopDangerMobilePoll();   // 卡收起即停手机端决策轮询，防残留轮询消费到下一轮的决策
        dangerCard.Visible = false;
        dangerCard = null;
        dangerResolve = null;   // 决策路由随卡一并收拢清理，防止残留 resolver 串到下一个挂起
        RefreshPendingBadges();   // 危险裁决/收口收起卡片：对应会话角标即时归 0
    }

    /// <summary>点击停止按钮：优先停止编排执行（PlanRunner），否则取消当前主对话任务并解锁发送。</summary>
    void OnStop(object sender, RoutedEventArgs e)
    {
        try
        {
            UnlockSessionSwitch();   // 点停止：立即解除会话切换锁（锁只可能属于当前会话，无条件释放安全）
            if (planRunner != null)   // 编排执行在进行中：立即停止编排（取消叶子/审查会话，计划置 paused）
            {
                planRunner.Stop();
                HideDangerCard();   // 叶子可能正卡在危险确认卡片：Stop 已代为拒绝，收起卡片避免残留误导
                return;
            }
            var rec = CurRec;   // 停止只作用于当前会话自己的任务记录（两会话并行互不取消对方）
            if (rec?.Cts != null)
            {
                rec.Cts.Cancel();
                HideDangerCard();   // 主任务停止：有挂起的危险确认卡立即收起（永不超时后，停止是卡片的收起途径之一）
                rec.StopRequested = true;   // 记录级停止标记（取代实例级 _stopRequested）：Finished 收口据此判"中断(红)"
                // 解锁发送在 Finished 事件中统一处理
            }
            else if (rec?.MultiGenCts != null)
            {
                rec.MultiGenCts.Cancel();   // 多模型决策生成中：取消后台规划（解锁由完成回调统一处理）
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Error] 停止任务失败: {ex.Message}");
        }
    }
    
    /// <summary>危险决策路由：编排执行中（叶子/审查会话）把决策发给 PlanRunner 当前会话（其 Loop 才是挂起方）；
    /// 否则走当前会话 Runner（普通任务危险确认）。避免编排场景决策落到无挂起的 Runner 而解不开叶子/审查会话。</summary>
    void ResolveDangerFor(bool allow)
    {
        var s = planRunner?.CurrentLeafSession;
        if (s != null) s.DecideDanger(allow);
        else CurRunner?.Loop.ResolveDanger(allow);   // 普通任务危险确认挂在本会话 Runner；无 Runner 时无挂起卡片，忽略
    }

    /// <summary>危险倒计时（兼容旧"限时决策"模式）：每秒减1，到0秒时自动拒绝并隐藏卡片。
    /// 默认 -1 = 永不超时（Agent 侧挂起等人工裁决，不会自动拒绝），tick 直接忽略。
    /// 按定时器实例找归属记录（每会话记录一条倒计时）：后台任务（切走放行）的卡片到点照拒不误当前会话的卡。</summary>
    void OnDangerTick(object? sender, EventArgs e)
    {
        var rec = runTasks.Values.FirstOrDefault(r => ReferenceEquals(r.DangerTicker, sender));
        var card = rec?.DangerCard;
        if (card != null && card.CountdownSeconds >= 0)
        {
            card.CountdownSeconds--;
            if (card.CountdownSeconds <= 0)
            {
                rec!.DangerTicker?.Stop();
                rec.DangerTicker = null;
                StopDangerMobilePoll();   // 超时自动拒绝同样停手机端轮询
                card.Visible = false;
                rec.DangerCard = null;
                var resolve = rec.DangerResolve;   // 按所属记录路由：照拒这张卡归属的挂起，不误伤其它会话
                rec.DangerResolve = null;
                if (resolve != null) resolve(false); else ResolveDangerFor(false);
                ScrollToBottom();
            }
        }
    }

    /// <summary>危险命令确认：允许执行（重放命令，结果回喂模型），卡片自动隐藏。决策按所属记录路由：点哪张卡解哪把锁。</summary>
    void OnDangerAllowClick(object sender, RoutedEventArgs e)
    {
        if (dangerCard != null)
        {
            dangerTicker?.Stop();
            dangerTicker = null;
            StopDangerMobilePoll();
            dangerCard.Visible = false;
            dangerCard = null;
            var resolve = dangerResolve;   // 旧卡无 resolver 时回退全局路由（叶子优先）
            dangerResolve = null;
            if (resolve != null) resolve(true); else ResolveDangerFor(true);
            ScrollToBottom();
        }
    }

    /// <summary>危险命令确认：取消执行（拒绝文本回喂模型），卡片自动隐藏。决策按所属记录路由：点哪张卡解哪把锁。</summary>
    void OnDangerCancelClick(object sender, RoutedEventArgs e)
    {
        if (dangerCard != null)
        {
            dangerTicker?.Stop();
            dangerTicker = null;
            StopDangerMobilePoll();
            dangerCard.Visible = false;
            dangerCard = null;
            var resolve = dangerResolve;   // 旧卡无 resolver 时回退全局路由（叶子优先）
            dangerResolve = null;
            if (resolve != null) resolve(false); else ResolveDangerFor(false);
            ScrollToBottom();
        }
    }

    /// <summary>手机端（观看模式）危险决策轮询启动：危险卡挂起期间每 2s 读 decisions.inbox.json，
    /// 命中"会话+空 planId"条目即按建卡时捕获的决策路由裁决（与点卡上按钮同一把锁）。
    /// 启动前先清该会话的危险残留决策，防上一张卡的陈旧条目被本轮误消费。</summary>
    void StartDangerMobilePoll(string? sessionKey, Action<bool>? resolve, GAIRR.Core.SecurityAlert? alert = null)
    {
        StopDangerMobilePoll();
        if (string.IsNullOrEmpty(sessionKey) || resolve == null) return;
        // 危险挂起标记 danger_<sid>.json：Server /gui-events 回放据此合成/过滤 SecurityAlert 帧——
        // 手机端任何时刻（重）进会话都能拿到待处理卡，已决策的旧帧不再重显
        try
        {
            if (alert != null)
                System.IO.File.WriteAllText(System.IO.Path.Combine(Paths.DataDir, "danger_" + sessionKey + ".json"),
                    System.Text.Json.JsonSerializer.Serialize(alert));
        }
        catch { /* 尽力而为：标记缺失不阻断桌面决策流程 */ }
        MobileDecisionStore.PurgeDanger(Paths.DataDir, sessionKey);
        dangerMobilePollSid = sessionKey;
        dangerMobilePollResolve = resolve;
        dangerMobilePollRec = ActiveRec ?? (currentSession != null ? EnsureRec(currentSession) : null);   // 与建卡同一代码路径解析归属
        dangerMobilePollTicker = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        dangerMobilePollTicker.Tick += OnDangerMobilePollTick;
        dangerMobilePollTicker.Start();
    }

    /// <summary>停止手机端危险决策轮询并清空捕获状态（卡收起/裁决/超时各出口统一调用，幂等）。</summary>
    void StopDangerMobilePoll()
    {
        try   // 清危险挂起标记：决策已收口，Server 回放不再合成/保留 SecurityAlert 帧
        {
            if (!string.IsNullOrEmpty(dangerMobilePollSid))
            {
                var f = System.IO.Path.Combine(Paths.DataDir, "danger_" + dangerMobilePollSid + ".json");
                if (System.IO.File.Exists(f)) System.IO.File.Delete(f);
            }
        }
        catch { /* 尽力而为 */ }
        dangerMobilePollTicker?.Stop();
        dangerMobilePollTicker = null;
        dangerMobilePollSid = null;
        dangerMobilePollResolve = null;
        dangerMobilePollRec = null;
    }

    /// <summary>手机端危险决策轮询节拍：收件箱命中即按归属记录收卡、按捕获路由裁决（与点卡上按钮同一把锁）。</summary>
    void OnDangerMobilePollTick(object? sender, EventArgs e)
    {
        var sid = dangerMobilePollSid;
        var resolve = dangerMobilePollResolve;
        var rec = dangerMobilePollRec;
        if (string.IsNullOrEmpty(sid) || resolve == null) { StopDangerMobilePoll(); return; }
        if (!MobileDecisionStore.TakeDanger(Paths.DataDir, sid, out var allow)) return;
        StopDangerMobilePoll();
        if (rec != null)   // 按归属记录收卡：停倒计时、隐卡、清 resolver（用户可能正查看别的会话）
        {
            rec.DangerTicker?.Stop();
            rec.DangerTicker = null;
            if (rec.DangerCard != null) { rec.DangerCard.Visible = false; rec.DangerCard = null; }
            rec.DangerResolve = null;
        }
        else HideDangerCard();
        RefreshPendingBadges();
        resolve(allow);
        ScrollToBottom();
    }
    void UnlockSend()
    {
        Ui(() =>
        {
            if (planRunner != null) return;   // 编排执行中：叶子会话的 Finished 事件也会走到这里，
                                              // 不能清掉停止按钮（由 SetSendBusy 在 runner 结束时统一解锁）
            sendBtn.Visibility = Visibility.Visible;
            sendMenuBtn.Visibility = Visibility.Visible;   // 恢复发送时同步显示下拉箭头
            stopBtn.Visibility = Visibility.Collapsed;
            inputBox.Focus();
            // 任务结束，恢复历史会话行的手型鼠标指针（重新允许切换）
            SessionCursor = Cursors.Hand;
            RefreshOrchGenBtn();
        });
    }

    /// <summary>统一设置发送区忙碌状态：busy=编排执行/多模型决策生成 → 显示停止按钮（发送钮隐藏）。
    /// 编排执行（planRunner）与主对话共用发送区，任一在跑即进入中止状态；
    /// 忙闲状态同时驱动会话列表锁定光标（No/Hand）——编排停止/结束的唯一解锁点也在此收口。</summary>
    void SetSendBusy(bool busy)
    {
        Ui(() =>
        {
            var isBusy = busy || planRunner != null || cts != null || multiGenCts != null;
            sendBtn.Visibility = isBusy ? Visibility.Collapsed : Visibility.Visible;
            sendMenuBtn.Visibility = sendBtn.Visibility;   // 忙碌态同步隐藏下拉箭头
            stopBtn.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
            // 会话列表光标恒为手型：两会话并行 + 厂商并发闸下前台主任务（cts）运行中同样允许切换/新建会话
            // （切走后任务后台照跑、事件按会话 Key 归属，口径同 OnSessionClick/OnNewSession gate）。
            // 统一在此收口：UnlockSend 在 planRunner!=null 时会提前返回，若只靠它恢复会导致编排结束后光标状态不一致
            SessionCursor = Cursors.Hand;
            RefreshOrchGenBtn();
        });
    }

    /* ---------- 技能页签：绑定 SkillLoader 实时数据 ---------- */

    void FillSkillPanel()
    {
        skillList.ItemsSource = skillLoader.Skills;
        builtinSkillList.ItemsSource = builtinSkills;
        // 内置工具列表
        builtinToolList.ItemsSource = builtinTools;
        // 插件列表：有数据时直接绑定，无数据时显示空状态提示
        if (plugins.Count == 0)
        {
            plugins.Add(new PluginDef("暂无插件", "plugins/ 目录每插件一个 ini", "", false));
        }
        pluginList.ItemsSource = plugins;

        // 市场安装联动：追溯记录（.gairr/market.json）中出现的条目给本地行打「来自市场」标记
        var inst = Market.Installed(cfg);
        foreach (var s in skillLoader.Skills) s.MarketInstalled = IsMarketInstalled(inst, s.Name, s.Path);
        foreach (var p in plugins) p.MarketInstalled = IsMarketInstalled(inst, p.Name, p.Path);
        skillList.Items.Refresh();
        pluginList.Items.Refresh();
    }

    /// <summary>市场安装判定：追溯记录按技能/插件名或所在目录名匹配（目录名=市场条目名）</summary>
    static bool IsMarketInstalled(Dictionary<string, string> inst, string name, string path)
    {
        if (inst.ContainsKey(name)) return true;
        var dir = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path) ?? "");
        return dir.Length > 0 && inst.ContainsKey(dir);
    }

    /* ---------- 市场视图：本地/市场切换、目录册拉取、搜索过滤、安装/卸载 ---------- */

    /// <summary>技能页签 本地/市场 视图切换（深色分段样式按 Tag=cur 高亮；市场视图首次进入时拉取目录册）</summary>
    void OnSkillViewClick(object sender, RoutedEventArgs e)
    {
        var market = ReferenceEquals(sender, skillViewMarket);
        panelSkill.Visibility = market ? Visibility.Collapsed : Visibility.Visible;
        panelMarket.Visibility = market ? Visibility.Visible : Visibility.Collapsed;
        // 选中态交给样式（Tag=cur）：选中段抬亮，未选中段还原默认
        skillViewLocal.Tag = market ? "off" : "cur";
        skillViewMarket.Tag = market ? "cur" : "off";
        if (market) _ = LoadMarketPanelAsync(false);
    }

    /// <summary>市场搜索框：本地过滤搜索结果栏（ICollectionView.Filter 实时生效；回车触发在线搜索）</summary>
    void OnMarketSearch(object sender, TextChangedEventArgs e) => marketView?.Refresh();

    /// <summary>搜索框回车 = 触发在线搜索（显式触发，防每键一次打爆限流）</summary>
    void OnMarketSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; _ = SearchOnlineAsync(); }
    }

    /// <summary>市场按钮统一入口：搜索框空=强制刷新三栏；有内容=聚合搜索 ClawHub/GitHub/Gitee + 本地目录册</summary>
    void OnMarketSearchBtnClick(object sender, RoutedEventArgs e)
    {
        if (marketSearchBox.Text.Trim().Length == 0) _ = LoadMarketPanelAsync(true);
        else _ = SearchOnlineAsync();
    }

    /// <summary>搜索框文本变化：本地过滤即时刷新 + 按钮文字随内容切换（空=刷新列表，有内容=搜索市场）</summary>
    void OnMarketSearchBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        marketView?.Refresh();
        marketGitHubBtn.Content = marketSearchBox.Text.Trim().Length == 0 ? "刷新列表" : "搜索市场";
    }

    /// <summary>在线搜索：聚合 ClawHub（匿名）+ GitHub + Gitee（需 token）+ 本地目录册关键词命中项，填充搜索结果栏。
    /// 同词且结果栏非空不重发；单源失败不影响其余源，状态栏汇总各源条数。</summary>
    async Task SearchOnlineAsync()
    {
        var q = marketSearchBox.Text.Trim();
        if (q.Length == 0) { marketStatus.Text = "请先输入关键词，再点“在线搜索”"; return; }
        if (q.Equals(lastSearchQuery, StringComparison.OrdinalIgnoreCase) && marketResultEntries.Count > 0)
        {
            marketStatus.Text = $"已搜索过“{q}”，结果在“搜索结果”栏；换关键词可重新搜索";
            marketResultExp.IsExpanded = true;
            return;
        }
        marketGitHubBtn.IsEnabled = false;
        marketStatus.Text = $"正在在线搜索“{q}”（ClawHub/GitHub/Gitee）…";
        marketResultEntries.Clear();
        var inst = Market.Installed(cfg);
        var parts = new List<string>();
        await SearchClawHubAsync(q, inst, parts);
        await SearchGitHubAsync(q, inst, parts);
        await SearchGiteeAsync(q, inst, parts);
        await SearchLocalCatalogAsync(q, inst);
        lastSearchQuery = q;
        marketResultExp.IsExpanded = true;
        marketResultEmpty.Visibility = marketResultEntries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        marketStatus.Text = $"搜索“{q}”：" + string.Join(" · ", parts);
        marketGitHubBtn.IsEnabled = true;
        marketView?.Refresh();   // 过滤视图刷新（搜索结果栏本地过滤）
        TriggerMarketTranslate();   // 后台翻译本次在线搜索的英文摘要
    }

    /// <summary>搜索源 1：ClawHub（匿名可用；失败仅计入汇总不影响其余源）</summary>
    async Task SearchClawHubAsync(string q, Dictionary<string, string> inst, List<string> parts)
    {
        try
        {
            var items = await ClawHubHub.SearchAsync(q, CancellationToken.None);
            foreach (var h in items) AddResultEntry(ClawHubHub.ToCatalogEntry(h), inst);
            parts.Add($"ClawHub {items.Count}");
        }
        catch { parts.Add("ClawHub 失败"); }
    }

    /// <summary>搜索源 2：GitHub 仓库（免 token 限流 10 次/分钟；只有含 SKILL.md 的仓库可安装）</summary>
    async Task SearchGitHubAsync(string q, Dictionary<string, string> inst, List<string> parts)
    {
        try
        {
            var repos = await GitHubHub.SearchItemsAsync(q, CancellationToken.None);
            foreach (var r in repos)
                AddResultEntry(new CatalogEntry("skill", r.FullName, r.Description,
                    r.Stars.ToString(), "", "", "github:" + r.FullName), inst);
            parts.Add($"GitHub {repos.Count}");
        }
        catch { parts.Add("GitHub 失败(限流?)"); }
    }

    /// <summary>搜索源 3：Gitee（匿名已被限制，仅配置 [Market] GiteeToken 后参与搜索）</summary>
    async Task SearchGiteeAsync(string q, Dictionary<string, string> inst, List<string> parts)
    {
        if (!GiteeHub.Enabled(cfg)) { parts.Add("Gitee 未配置token"); return; }
        try
        {
            var repos = await GiteeHub.SearchItemsAsync(cfg, q, CancellationToken.None);
            foreach (var r in repos)
                AddResultEntry(new CatalogEntry("skill", r.FullName, r.Description,
                    r.Stars.ToString(), "", "", "gitee:" + r.FullName), inst);
            parts.Add($"Gitee {repos.Count}");
        }
        catch { parts.Add("Gitee 失败"); }
    }

    /// <summary>搜索源 4：本地目录册（Market.Sources 目录列表）关键词命中项并入结果栏</summary>
    async Task SearchLocalCatalogAsync(string q, Dictionary<string, string> inst)
    {
        try
        {
            foreach (var c in await Market.FetchCatalogAsync(cfg))
                if (c.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || c.Desc.Contains(q, StringComparison.OrdinalIgnoreCase))
                    AddResultEntry(c, inst);
        }
        catch { /* 目录册源失败不阻断 */ }
    }

    /// <summary>搜索结果栏加一条目（按 EntryKey 去重；追溯记录已有者直接标记已安装）</summary>
    void AddResultEntry(CatalogEntry c, Dictionary<string, string> inst)
    {
        var key = c.Source + "|" + c.Name;
        if (marketResultEntries.Any(m => m.EntryKey == key)) return;
        var m = new MarketEntryDef(c);
        if (inst.ContainsKey(c.Name)) m.State = "installed";
        marketResultEntries.Add(m);
    }

    /// <summary>刷新按钮：强制重载已安装栏 + 热度排行栏</summary>
    void OnMarketRefresh(object sender, RoutedEventArgs e) => _ = LoadMarketPanelAsync(true);

    /// <summary>搜索结果栏本地过滤：关键字为空全部通过；否则按名称/描述匹配（忽略大小写）</summary>
    bool MarketMatch(MarketEntryDef m)
    {
        var q = marketSearchBox?.Text?.Trim() ?? "";
        if (q.Length == 0) return true;
        return m.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || m.Description.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>市场面板加载：已安装栏（追溯记录）+ 热度排行栏（ClawHub）；切页不重复拉取，刷新按钮才 force</summary>
    bool marketLoaded;
    async Task LoadMarketPanelAsync(bool force)
    {
        if (marketLoaded && !force) return;
        marketLoaded = true;
        marketLoading.Visibility = Visibility.Visible;
        RefreshInstalledColumn();
        await LoadTrendingAsync();
        marketLoading.Visibility = Visibility.Collapsed;
        UpdateMarketEmptyHint();
        TriggerMarketTranslate();   // 后台把三栏英文摘要翻成中文，完成即刷新卡片
    }

    /// <summary>已安装栏：按追溯记录 .gairr/market.json 重建（描述优先取本地技能/插件说明），
    /// 并同步热度/结果栏中同名条目的已安装状态</summary>
    void RefreshInstalledColumn()
    {
        var inst = Market.Installed(cfg);
        marketInstalledEntries.Clear();
        foreach (var (name, v) in inst)
        {
            var parts = v.Split('|');
            var kind = parts.Length > 0 ? parts[0] : "skill";
            var ver = parts.Length > 1 ? parts[1] : "";
            var src = parts.Length > 2 ? parts[2] : "";
            var m = new MarketEntryDef(new CatalogEntry(kind, name, LookupLocalDesc(name, kind), ver, "", "", src))
            { State = "installed" };
            m.CnDesc = MarketTranslator.Peek(cfg, name);   // 预填缓存中文摘要，避免闪英文
            marketInstalledEntries.Add(m);
        }
        marketInstalledEmpty.Visibility = marketInstalledEntries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var m in marketTrendingEntries.Concat(marketResultEntries))
            if (inst.ContainsKey(m.Name) && m.State != "installed") m.State = "installed";
    }

    /// <summary>已安装栏描述：优先取本地技能/插件的说明；未找到返回空（卡片摘要行留白）</summary>
    string LookupLocalDesc(string name, string kind) => kind == "plugin"
        ? plugins.FirstOrDefault(p => p.Name == name)?.Description ?? ""
        : skillLoader.Skills.FirstOrDefault(s => s.Name == name)?.Description ?? "";

    /// <summary>热度排行栏：ClawHub 近期热度榜前 10（匿名可用）；失败仅提示状态栏不影响已安装栏</summary>
    async Task LoadTrendingAsync()
    {
        try
        {
            var inst = Market.Installed(cfg);
            var items = await ClawHubHub.TrendingAsync(CancellationToken.None);
            marketTrendingEntries.Clear();
            foreach (var h in items)
            {
                var m = new MarketEntryDef(ClawHubHub.ToCatalogEntry(h));
                if (inst.ContainsKey(h.Slug)) m.State = "installed";
                marketTrendingEntries.Add(m);
            }
            if (marketStatus.Text.Length == 0) marketStatus.Text = $"热度榜 {items.Count} 条（ClawHub）";
        }
        catch (Exception ex) { marketStatus.Text = "热度榜拉取失败：" + ex.Message + "（检查网络可达性）"; }
        marketTrendingEmpty.Visibility = marketTrendingEntries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>顶层空提示：三栏全空才显示（引导点“在线搜索”或 ↻ 刷新）</summary>
    void UpdateMarketEmptyHint() => marketEmpty.Visibility =
        marketInstalledEntries.Count == 0 && marketTrendingEntries.Count == 0 && marketResultEntries.Count == 0
        ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>后台翻译一批市场条目的英文描述并写回中文摘要（UI 线程安全，失败静默回退英文原文）</summary>
    async Task TranslateEntriesAsync(IEnumerable<MarketEntryDef> items)
    {
        var list = items.Where(m => m.CnDesc.Length == 0 && m.Entry.Desc.Length > 0).ToList();
        if (list.Count == 0) return;
        try
        {
            var map = await MarketTranslator.TranslateAsync(cfg, list);
            if (map.Count == 0) return;
            foreach (var m in list)
                if (map.TryGetValue(m.Name, out var zh) && zh.Length > 0)
                {
                    var mm = m;
                    _ = Dispatcher.BeginInvoke(() => mm.CnDesc = zh);   // 写回 UI 线程触发卡片刷新
                }
        }
        catch { /* 翻译失败静默：保留英文原文 */ }
    }

    /// <summary>后台启动三栏英文摘要翻译（不等待；已翻译/含中文/失败静默回退英文）</summary>
    void TriggerMarketTranslate() =>
        _ = TranslateEntriesAsync(marketInstalledEntries.Concat(marketTrendingEntries).Concat(marketResultEntries));

    /// <summary>市场行操作按钮：未装/失败→安装；已装→卸载。走 Market 层，完成后联动本地列表刷新</summary>
    async void OnMarketAction(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string key) return;
        // 按钮 Tag=EntryKey（源|名），在三栏集合中定位条目（同名条目可来自不同源，故不能只按 Name 匹配）
        var entry = marketInstalledEntries.Concat(marketTrendingEntries).Concat(marketResultEntries)
            .FirstOrDefault(m => m.EntryKey == key);
        if (entry == null || entry.State == "installing") return;

        if (entry.State == "installed")
        {
            // GitHub / Gitee 行的技能名是实际落盘名（InstalledName，可能≠仓库名）；目录册行即条目名
            var r = Market.Uninstall(cfg, registry, entry.Entry.Kind,
                entry.IsGitHub || entry.IsGitee ? entry.InstalledName : entry.Name);
            entry.State = r.Success ? "install" : "failed";
            entry.Error = r.Success ? "" : r.Message;
            if (r.Success) entry.InstalledName = "";
            marketStatus.Text = r.Message;
            FillSkillPanel();   // 本地列表同步摘除标记
            if (r.Success) RefreshInstalledColumn();   // 已安装栏同步移除该条目
            return;
        }

        // GitHub / Gitee 搜索结果行：走对应 Hub 链路（下载仓库→找 SKILL.md→落盘），分阶段显示进度
        if (entry.IsGitHub) { await InstallGitHubEntryAsync(entry); return; }
        if (entry.IsGitee) { await InstallGiteeEntryAsync(entry); return; }

        // 插件会执行外部命令：安装前人工确认（技能为纯文本无执行风险，直接装）
        if (entry.Entry.Kind == "plugin" &&
            MessageBox.Show(this, $"将安装插件 {entry.Name}，安装后可被 Agent 调用（执行外部命令）。\n来源：{entry.Entry.Source}\n确认安装？",
                "市场安装确认", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        entry.State = "installing"; entry.Error = "";
        try
        {
            var r = await Market.InstallAsync(cfg, registry, entry.Entry);
            entry.State = r.Success ? "installed" : "failed";
            entry.Error = r.Success ? "" : r.Message;
            marketStatus.Text = r.Message;
            FillSkillPanel();   // 本地列表联动：新条目 + 「来自市场」标记
            if (r.Success)
            {
                RefreshInstalledColumn();   // 已安装栏立即出现该条目
                TriggerMarketTranslate();   // 新条目摘要（可能英文）后台翻译
            }
        }
        catch (Exception ex) { entry.State = "failed"; entry.Error = ex.Message; }
    }

    /// <summary>安装 GitHub 搜索结果行（走 GitHubHub 链路，描述列分阶段显示进度）。
    /// 多技能仓库弹框逐个选择：是=装该技能 / 否=跳过换下一个 / 取消=放弃安装。</summary>
    async Task InstallGitHubEntryAsync(MarketEntryDef entry)
    {
        // 重搜会清掉旧 GitHub 行，已装仓库再装 = 覆盖更新（与目录册"同名覆盖"语义一致）
        entry.State = "installing"; entry.Error = ""; entry.ProgressText = "准备下载…";
        try
        {
            var skill = "";
            while (true)
            {
                var r = await GitHubHub.InstallSkillAsync(cfg, entry.RepoPath, skill,
                    CancellationToken.None, p => Dispatcher.BeginInvoke(() => entry.ProgressText = p));
                if (r.Success)
                {
                    entry.InstalledName = r.SkillName; entry.State = "installed"; entry.ProgressText = "";
                    marketStatus.Text = r.Message; FillSkillPanel();
                    RefreshInstalledColumn();   // GitHub 安装成功后同步进已安装栏
                    TriggerMarketTranslate();
                    return;
                }
                if (r.Candidates.Count == 0)          // 真失败：无 SKILL.md / 下载失败等
                {
                    entry.State = "failed"; entry.Error = r.Message; entry.ProgressText = "";
                    marketStatus.Text = r.Message; return;
                }
                skill = PickSkillFromCandidates(entry.RepoPath, r.Candidates);
                if (skill.Length == 0)                // 全部跳过 = 放弃安装
                {
                    entry.State = "install"; entry.ProgressText = "";
                    marketStatus.Text = "已取消安装（未选择技能）"; return;
                }
            }
        }
        catch (Exception ex) { entry.State = "failed"; entry.Error = ex.Message; entry.ProgressText = ""; }
    }

    /// <summary>安装 Gitee 搜索结果行（走 GiteeHub 链路：API+token 下载 zip，不弹 git 凭证窗口）。
    /// 未配置 token 时安装会失败并提示，引导用户到 config.ini [Market] GiteeToken 配置。</summary>
    async Task InstallGiteeEntryAsync(MarketEntryDef entry)
    {
        entry.State = "installing"; entry.Error = ""; entry.ProgressText = "准备下载…";
        try
        {
            if (!GiteeHub.Enabled(cfg))
            {
                entry.State = "failed"; entry.ProgressText = "";
                entry.Error = "Gitee 未配置 token：请在 config.ini [Market] GiteeToken 配置个人访问令牌";
                marketStatus.Text = entry.Error;
                return;
            }
            var skill = "";
            while (true)
            {
                var r = await GiteeHub.InstallSkillAsync(cfg, entry.RepoPath, skill,
                    CancellationToken.None, p => Dispatcher.BeginInvoke(() => entry.ProgressText = p));
                if (r.Success)
                {
                    entry.InstalledName = r.SkillName; entry.State = "installed"; entry.ProgressText = "";
                    marketStatus.Text = r.Message; FillSkillPanel();
                    RefreshInstalledColumn();   // Gitee 安装成功后同步进已安装栏
                    TriggerMarketTranslate();
                    return;
                }
                if (r.Candidates.Count == 0)          // 真失败：无 SKILL.md / 下载失败等
                {
                    entry.State = "failed"; entry.Error = r.Message; entry.ProgressText = "";
                    marketStatus.Text = r.Message; return;
                }
                skill = PickSkillFromCandidates(entry.RepoPath, r.Candidates);
                if (skill.Length == 0)                // 全部跳过 = 放弃安装
                {
                    entry.State = "install"; entry.ProgressText = "";
                    marketStatus.Text = "已取消安装（未选择技能）"; return;
                }
            }
        }
        catch (Exception ex) { entry.State = "failed"; entry.Error = ex.Message; entry.ProgressText = ""; }
    }

    /// <summary>多技能仓候选选择弹框：是=装当前项 / 否=看下一个 / 取消=放弃；返回选中的候选名或空串</summary>
    string PickSkillFromCandidates(string repo, List<string> candidates)
    {
        foreach (var c in candidates)
        {
            var mb = MessageBox.Show(this, $"仓库 {repo} 含多个技能，是否安装：{c}？\n（“否”查看下一个，“取消”放弃安装）",
                "选择技能", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (mb == MessageBoxResult.Yes) return c;
            if (mb == MessageBoxResult.Cancel) return "";
        }
        return "";
    }

    /// <summary>插件热加载完成回调（后台线程）：切 UI 线程用最新列表刷新插件面板</summary>
    void RefreshPlugins(List<PluginDef> latest)
    {
        Dispatcher.BeginInvoke(() =>
        {
            plugins.Clear();
            foreach (var p in latest) plugins.Add(p);
            if (plugins.Count == 0) plugins.Add(new PluginDef("暂无插件", "plugins/ 目录每插件一个 ini", "", false));
        });
    }

    /// <summary>技能热加载完成回调（后台线程）：切 UI 线程重绑技能列表（新列表已由加载器保留启用状态）</summary>
    void RefreshSkills(List<SkillDef> latest)
    {
        Dispatcher.BeginInvoke(() => skillList.ItemsSource = latest);
    }

    void OnSkillSelected(object sender, SelectionChangedEventArgs e)
    {
        // Skill 库已改为 StackPanel 静态显示，不再需要 ListBox 选择事件
    }

    void AddRow(StackPanel host, string name, string sig, bool mono)
    {
        var border = new Border { Style = (Style)FindResource("RowBox") };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(new System.Windows.Shapes.Ellipse { Style = (Style)FindResource("RowDot") });
        left.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 12.5,
            FontFamily = mono ? new FontFamily("Consolas") : new FontFamily("Microsoft YaHei UI"),
        });
        var right = new TextBlock { Text = sig, Style = (Style)FindResource("RowSig") };
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        border.Child = grid;
        host.Children.Add(border);
    }

    void OnInputKey(object sender, KeyEventArgs e)
    {
        // Enter 发送，Shift+Enter 换行
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            OnSend(sender, e);
        }
    }

    /// <summary>输入框内容变化 → 刷新发送钮「发送/继续」文案与置灰态</summary>
    void OnInputTextChanged(object sender, TextChangedEventArgs e) => RefreshSendBtnState();

    /// <summary>发送钮态收口：有输入 →「发送」可点；空输入 + 会话区有历史 →「继续」可点（点击等效补发「继续」）；
    /// 空输入 + 会话区无历史 →「发送」置灰（样式自带 0.5 透明度降显）。忙碌期发送钮隐藏，busy 态不在这里判，
    /// 可见性恢复统一走 RefreshOrchGenBtn → 本方法跟随刷新文案/置灰。</summary>
    void RefreshSendBtnState()
    {
        var hasText = inputBox.Text.Trim().Length > 0;
        sendBtn.Content = hasText || messages.Count == 0 ? "发送" : "继续";
        sendBtn.IsEnabled = hasText || messages.Count > 0;
    }

    void OnInputFocus(object sender, RoutedEventArgs e) =>
        inputBorder.BorderBrush = (Brush)FindResource("P1Brush");

    void OnInputBlur(object sender, RoutedEventArgs e) =>
        inputBorder.BorderBrush = (Brush)FindResource("BorderBrush2");

    /// <summary>点击输入区任意位置（含内边距空白）都把焦点给输入框</summary>
    void OnInputBorderClick(object sender, MouseButtonEventArgs e) =>
        inputBox.Focus();

    /// <summary>点击底部版权行，浏览器打开官网</summary>
    void OnFootClick(object sender, MouseButtonEventArgs e) =>
        System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("https://gairr.com") { UseShellExecute = true });

    /// <summary>窗口级滚轮接管：鼠标几何位置落在会话消息区（msgScroll 可视矩形）内时，
    /// 统一滚动对话区——挂窗口根而非 ScrollViewer 自身，规避内嵌查看器/子控件吞事件的个别情况；
    /// 鼠标不在消息区（目录树/输入框/右侧编排面板等）时放行给各自控件，互不干扰</summary>
    void OnWindowPreviewWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || msgScroll == null) return;
        if (msgScroll.Visibility != Visibility.Visible || msgScroll.ActualWidth < 1 || msgScroll.ActualHeight < 1) return;
        // 提交详情浮层/代码查看面板盖在会话区之上：鼠标落在其可视矩形内时放行，
        // 由内部控件（AvalonEdit 自带滚轮）自行滚动；否则按下方几何判定会被误当"消息区"接管而吞掉滚轮
        if (WheelOverPanel(e, gitFloatPanel) || WheelOverPanel(e, editorPanel)) return;
        var pt = e.GetPosition(msgScroll);
        if (pt.X < 0 || pt.Y < 0 || pt.X >= msgScroll.ActualWidth || pt.Y >= msgScroll.ActualHeight) return;
        msgScroll.ScrollToVerticalOffset(msgScroll.VerticalOffset - e.Delta / 3.0);
        e.Handled = true;
    }

    /// <summary>判断鼠标是否落在指定面板的可视矩形内（浮层/查看面板盖在会话区之上时用于放行内部滚轮）</summary>
    static bool WheelOverPanel(MouseWheelEventArgs e, FrameworkElement panel)
    {
        if (panel == null || panel.Visibility != Visibility.Visible) return false;
        if (panel.ActualWidth < 1 || panel.ActualHeight < 1) return false;
        var p = e.GetPosition(panel);
        return p.X >= 0 && p.Y >= 0 && p.X < panel.ActualWidth && p.Y < panel.ActualHeight;
    }

    /* ================= 对话区顶部动态信息栏：当前回看轮次的锚点消息滚出视口时固定显示名称/Steps/问题 ================= */

    /* ================= 待办清单（模型经 UpdateTodo 管理） ================= */

    /// <summary>待办事件分发：模型经 UpdateTodo 创建/更新/完成清单（create/update/done_all）或框架步骤开始卡片（step_start）</summary>
    void ApplyTodo(TodoUpdate t)
    {
        switch (t.Action)
        {
            case "create":
                if (t.Steps == null || t.Steps.Count == 0) return;
                currentTodo = BuildTodoCard(t.Steps);
                // 计划卡置顶：排在时间线最前面，后续思考条/工具卡在其下展开
                workMsg!.ProcessItems.Insert(0, currentTodo);
                break;
            case "step_start":
                // 步骤生命周期卡片：先检查是否已有同步骤未完成卡，复用则更新标题；否则新建并缓存
                if (stepCardMap.TryGetValue(t.Index, out var existingStart))
                {
                    existingStart.Title = $"计划步骤{t.Index}：{TruncDots(t.Text, 24)}";
                    existingStart.Status = "✓ 0s";
                }
                else
                {
                    var newStepCard = new ToolCall
                    {
                        Title = $"计划步骤{t.Index}：{TruncDots(t.Text, 24)}",
                        Status = "✓ 0s",
                        IsStep = true,
                    };
                    stepCardMap[t.Index] = newStepCard;
                    workMsg!.ProcessItems.Add(newStepCard);
                }
                break;
            case "update":
                if (currentTodo != null) MarkTodoStep(t.Index, t.Done ? "[✓] " : "[ ] ");
                // 复用步骤生命周期卡片：若该步骤已开始，标记完成时把同一张卡片改为完成状态
                if (t.Done && t.Index >= 1 && stepCardMap.TryGetValue(t.Index, out var stepCard))
                {
                    stepCard.Title = stepCard.Title.StartsWith("计划步骤")
                        ? stepCard.Title
                        : $"计划步骤{t.Index}";
                    stepCard.Status = "✓";
                }
                break;
            case "done_all":
                if (currentTodo != null)
                    for (int i = 1; i <= TodoStepCount(); i++) MarkTodoStep(i, "[✓] ");
                // 同步把所有已创建的步骤卡片标记为完成
                foreach (var kv in stepCardMap)
                {
                    kv.Value.Title = kv.Value.Title.StartsWith("计划步骤")
                        ? kv.Value.Title
                        : $"计划步骤{kv.Key}";
                    kv.Value.Status = "✓";
                }
                break;
        }
        RecalcTodoTitle();
        RefreshTodoBar();
        workMsg?.Refresh();
    }

    /// <summary>待办清单变更后同步会话列表右侧执行进度：按事件归属键（后台排空为后台会话，前台为当前会话）
    /// 取计划卡完成数/总数换算百分比，与轮次组合显示为"第N轮 · 计划 X%"。</summary>
    void SyncSessionListTodo(string? key)
    {
        var s = sessionHistory.FirstOrDefault(x => x.Id == key) ?? currentSession;
        if (s == null || currentTodo == null) return;
        var rows = currentTodo.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (rows.Length == 0) return;
        var done = rows.Count(r => r.StartsWith("[✓]") || r.StartsWith("✗"));
        s.SetListProgress(done: done, total: rows.Length);
    }

    /// <summary>按模型步骤数组构建计划卡（状态符号 + 序号 + 步骤文本）</summary>
    TodoItem BuildTodoCard(List<string> steps)
    {
        var rows = new List<string>();
        for (int i = 0; i < steps.Count; i++) rows.Add("[ ] " + (i + 1) + ". " + steps[i]);
        return new TodoItem { Title = "📋 执行计划 · 0/" + steps.Count, Content = string.Join("\n", rows), Open = true };
    }

    /// <summary>替换某步骤行的状态符号（[ ] 待办 / [✓] 完成 / ✗ 失败）</summary>
    void MarkTodoStep(int idx, string mark)
    {
        var rows = currentTodo!.Content.Split('\n').ToList();
        if (idx < 1 || idx > rows.Count) return;
        var row = rows[idx - 1];
        rows[idx - 1] = mark + (row.Length > 4 ? row[4..] : "");
        currentTodo.Content = string.Join("\n", rows);
    }

    int TodoStepCount() => currentTodo!.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>重算计划卡标题进度 N/M</summary>
    void RecalcTodoTitle()
    {
        if (currentTodo == null) return;
        var rows = currentTodo.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var done = rows.Count(r => r.StartsWith("[✓]") || r.StartsWith("✗"));
        currentTodo.Title = $"📋 执行计划 · {done}/{rows.Length}";
    }

    /// <summary>点击顶部待办进度条：展开气泡内计划卡并滚动到底（快速查看完整清单）</summary>
    void OnTodoBarClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (currentTodo != null)
        {
            currentTodo.Open = true;
            // 用户主动点击计划条：强制滚到底部展开的计划卡
            ScrollToBottom();
        }
    }

    /// <summary>任务正常完成：未勾选步骤补为完成（失败 ✗ 保留痕迹），随后交还顶部信息条给滚动逻辑</summary>
    void FinishTodo()
    {
        if (currentTodo != null)
        {
            var rows = currentTodo.Content.Split('\n').ToList();
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].StartsWith("[ ] "))
                    rows[i] = "[✓] " + (rows[i].Length > 3 ? rows[i][3..] : "");
            currentTodo.Content = string.Join("\n", rows);
            RecalcTodoTitle();
            workMsg?.Refresh();
        }
        currentTodo = null;   // 任务结束：顶部信息栏回归按头像可见性自动显隐
        taskActive = false;
        RefreshTodoBar();
        UpdateChatTopInfo();
    }

    /// <summary>顶部信息条 L4 任务计划卡（头像行之下、锚点 Steps 之上）：任务执行中常驻显示完整计划卡（标题进度 N/M + 各步骤状态行），
    /// 可直接查看哪些步骤已完成/未做；任务结束隐藏。Tooltip 保留完整计划清单。</summary>
    /// <remarks>编排执行会话：叶子任务事件桥接归本会话（TaskRecord.PlanRunner != null），
    /// 但每片叶子 Finished 收口会走 FinishTodo 把 TaskActive 置 false——若只按 taskActive 判显隐，
    /// 首片叶子结束后顶栏计划卡即永久隐藏。故当前会话正在被编排执行（PlanRunner 运行中）时同样允许显示，
    /// 由 currentTodo 是否存在决定显示内容；后台运行（EventBg）仍一律不驱动本视口。</remarks>
    void RefreshTodoBar()
    {
        if (todoBar == null) return;
        // 当前会话正被编排执行器驱动（叶子执行/审查事件经桥接归本会话，TaskRecord.PlanRunner 在跑）：
        // 等同前台任务，允许显示计划卡。编排叶子以独立 TaskRecord 后台执行，事件排空期间
        // _evRec=叶子记录≠当前会话记录 → EventBg=true；若照普通"无关后台任务"直接跳过，
        // 编排会话顶部将永远看不到当前叶子任务的计划步骤（本需求要修的点）。
        bool orchActiveHere = TaskOf(currentSession) is { PlanRunner: not null };
        // 无关后台运行（EventBg 且本会话没有编排执行器在跑）直接跳过：todoBar 属被查看会话视口，
        // taskActive/currentTodo 代理此时锚的是归属记录，不得驱动本视口
        if (EventBg && !orchActiveHere) return;
        if ((!taskActive && !orchActiveHere) || currentTodo == null)
        {
            if (todoBar.Visibility != Visibility.Collapsed) todoBar.Visibility = Visibility.Collapsed;
            return;
        }
        var rows = currentTodo.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (rows.Length == 0)
        {
            if (todoBar.Visibility != Visibility.Collapsed) todoBar.Visibility = Visibility.Collapsed;
            return;
        }
        // 计划卡文本：首行=标题（N/M 进度），其后每行=步骤状态行；当前执行行加 🔄 标记，
        // 🔄 已表明执行中状态，故执行中行不再显示 "[ ]" 前缀（气泡内计划卡与 Tooltip 保留）
        int currentIdx = FindCurrentStepIdx(rows);
        var sb = new System.Text.StringBuilder(currentTodo.Title);
        for (int i = 0; i < rows.Length; i++)
        {
            var body = rows[i].Length > 4 ? rows[i][4..].Trim() : "";
            sb.Append('\n');
            if (i != currentIdx) sb.Append(rows[i].Length > 4 ? rows[i][..4] : rows[i]); // 状态符号前缀
            else sb.Append("🔄 ");
            sb.Append(TruncDots(body, 40));
        }
        var text = sb.ToString();
        todoBar.Visibility = Visibility.Visible;
        if (todoBar.Text != text) todoBar.Text = text;
        if ((string?)todoBar.ToolTip != currentTodo.Title + "\n" + currentTodo.Content)
            todoBar.ToolTip = currentTodo.Title + "\n" + currentTodo.Content;
    }

    /// <summary>定位当前执行步骤行下标：第一个仍处 [ ] 状态的行；全部完成时返回 -1</summary>
    static int FindCurrentStepIdx(string[] rows)
    {
        for (int i = 0; i < rows.Length; i++)
            if (rows[i].StartsWith("[ ] ")) return i;
        return -1;
    }

    /// <summary>刷新顶部信息栏：锚点取"可视区上方正在回看的那一轮"——GAIRR 段显示滚出视口上方最近的
    /// Agent 类消息（头像+名称+Steps），"你"段显示滚出视口上方最近的用户问题（压单行，悬停看全文）；
    /// 往下滚过一轮锚点即切换到下一轮；滚回顶部锚点消息头像可见时自动隐藏，L4 待办进度随之隐藏</summary>
    /// <summary>点击顶部信息栏的 Steps 完成信息条：将消息流滚动到锚点轮次自己那条 Steps 行处，
    /// 使真实完成信息条恰好显示在顶栏正下方（头像区仍在上方隐藏，顶栏锚点不变）。</summary>
    void OnTopStepsClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (msgScroll == null || msgList == null) return;
        if (FindTopAnchor(k => k is MsgKind.Agent or MsgKind.Typing or MsgKind.Cmd) is not ChatMessage anchor)
            return;
        if (msgList.ItemContainerGenerator.ContainerFromItem(anchor) is not FrameworkElement c || !c.IsVisible)
            return;
        var strip = FindChildByTag(c, "MsgStepsStrip") as FrameworkElement;
        if (strip == null || !strip.IsVisible) return;
        try
        {
            var top = strip.TransformToAncestor(msgScroll).Transform(new Point(0, 0)).Y;
            msgScroll.ScrollToVerticalOffset(Math.Max(0, msgScroll.VerticalOffset + top - 8));
        }
        catch { /* 布局未就绪时忽略本次点击 */ }
    }

    /// <summary>点击顶部信息栏 L1 的 GAIRR Agent 头像/名称行：消息流直接滚回最底部。
    /// （L2~L5 各行各自挂了自己的点击处理器，互不冲突；后台运行 EventBg 时不做滚动）</summary>
    void OnTopAgentClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (EventBg) return;
        ScrollToBottomAfterLayout();
    }

    /// <summary>深度优先查找 Tag 匹配的子元素（用于在消息卡容器内定位 Steps 行 Border）</summary>
    static DependencyObject? FindChildByTag(DependencyObject root, object tag)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe && Equals(fe.Tag, tag)) return child;
            var hit = FindChildByTag(child, tag);
            if (hit != null) return hit;
        }
        return null;
    }

    void UpdateChatTopInfo()
    {
        if (EventBg) return;   // 后台运行（EventBg）：顶栏属被查看会话视口，taskActive 代理锚的是归属记录，不得据此刷新/收起本视口顶栏
        if (updatingChatTopInfo) return;
        updatingChatTopInfo = true;
        try
        {
            if (msgScroll == null || msgList == null)
            {
                if (chatTopBar != null && chatTopBar.Visibility != Visibility.Collapsed)
                    chatTopBar.Visibility = Visibility.Collapsed;
                return;
            }
            if (!orcExecActive && !taskActive && messages.Count == 0)
            {
                if (chatTopBar != null && chatTopBar.Visibility != Visibility.Collapsed)
                    chatTopBar.Visibility = Visibility.Collapsed;
                return;
            }

            // Agent 段：可视区上方最近的 Agent 类消息（含进行中的工作消息）→ 头像+名称+Steps；
            // 任务中回看历史时跟随上方锚点，待办进度随信息栏一起显隐
            var agentAnchor = FindTopAnchor(k => k is MsgKind.Agent or MsgKind.Typing or MsgKind.Cmd);
            if (agentAnchor != null)
            {
                // 优先 StepsBase（不含 · ▸ 收缩 后缀）；进行中消息没有 StepsBase 时退回 Steps
                var steps = agentAnchor.StepsBase;
                if (string.IsNullOrEmpty(steps)) steps = agentAnchor.Steps;
                if (string.IsNullOrEmpty(steps)) steps = mStatus.Text;   // 无 Steps 时退回临时状态提示（常态为空）
                if (topSteps.Text != steps) topSteps.Text = steps;
                if ((string?)topSteps.ToolTip != steps) topSteps.ToolTip = steps;   // 截断时悬停看全文
                // 仅真实 Steps（含[前缀）可点击跳转该轮完成信息条；临时提示回退文本不可点
                topSteps.Cursor = steps.StartsWith("[") ? Cursors.Hand : Cursors.Arrow;
                // 在线状态已从 L1 移除：此处文本为空时整行隐藏，避免留出空白行
                topSteps.Visibility = string.IsNullOrEmpty(steps) ? Visibility.Collapsed : Visibility.Visible;
            }
            else if (orcExecActive)
            {
                // 编排执行且上方无 Agent 锚点（刚启动/当前会话无历史可回看）：步骤行显示占位提示不可点，
                // 使头像+名称+编排计划行构成常驻顶栏（需求 1：执行中的计划/审核提示常驻顶部）
                const string runHint = "编排执行中 · 详情见右侧“编排执行”页";
                if (topSteps.Text != runHint) topSteps.Text = runHint;
                if ((string?)topSteps.ToolTip != runHint) topSteps.ToolTip = runHint;
                topSteps.Cursor = Cursors.Arrow;
                topSteps.Visibility = Visibility.Visible;   // 占位提示恒显（上方无锚点时也要有 L5 行）
            }
            // 编排执行中 Agent 段恒显（它承载顶部计划行/叶子行）；否则随锚点显隐
            var showAgent = agentAnchor != null || orcExecActive;
            if (topAgentSec.Visibility != (showAgent ? Visibility.Visible : Visibility.Collapsed))
                topAgentSec.Visibility = showAgent ? Visibility.Visible : Visibility.Collapsed;
            RefreshTopPinLine();   // P：Agent 段显示时同步当前会话 角色+模式+模型 参数标题（文本/悬停标题）

            // 用户段：滚出顶部的用户问题竖排展示（旧→新，最新一条在最底贴头像、整段底部对齐）。
            // 条数与折行由 RebuildTopUserStack 按"左侧 Agent 段高度"预算决定：每条最多折 3 行，
            // 上方还有空间就继续追加上一条用户消息，直到超出信息栏高度为止（栏高只随左侧变化）；
            // 气泡按 宽度+预算+消息集合指纹 差异重建——未变化不重建（防 100ms 高频重建闪烁）
            var userMsgs = CollectTopUsers();   // 全部滚出视口上方的用户消息（时间序 旧→新）
            var showUser = userMsgs.Count > 0;
            if (showUser) RebuildTopUserStack(userMsgs);
            if (topUserSec.Visibility != (showUser ? Visibility.Visible : Visibility.Collapsed))
                topUserSec.Visibility = showUser ? Visibility.Visible : Visibility.Collapsed;

            // 信息栏整体：编排执行中恒显示（执行计划/用时/审核提示常驻，需求 1-3）；否则任一段可见才显示
            var showBar = orcExecActive || showAgent || showUser;
            chatTopBar.Visibility = showBar ? Visibility.Visible : Visibility.Collapsed;
            RefreshTodoBar();
            UpdateOrcTopVis();   // 编排执行行显隐随常驻开关（文本/颜色由 OrcStatusRefresh 维护）
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Error] 更新顶部信息栏失败: {ex.Message}");
        }
        finally
        {
            updatingChatTopInfo = false;
        }
    }

    /// <summary>查找滚出可视区上方之外的"锚点消息"：满足 kindMatch 且头像区（顶部 48px）已整体移出视口顶边的
    /// 最后一条消息；无则返回 null（对应段隐藏）。往下滚过一轮，锚点自动切换为更靠下（更近）的一条；
    /// 往上滚回、锚点消息头像重新可见时不再命中。进行中的工作消息同样在 messages 中参与查找。</summary>
    ChatMessage? FindTopAnchor(Func<MsgKind, bool> kindMatch)
    {
        if (msgList == null || msgScroll == null) return null;
        var viewH = msgScroll.ViewportHeight;
        ChatMessage? anchor = null;
        foreach (var m in messages)
        {
            if (!kindMatch(m.Kind)) continue;
            // 容器未生成（刚添加尚未布局）无法判定位置，跳过
            if (msgList.ItemContainerGenerator.ContainerFromItem(m) is not FrameworkElement c || !c.IsVisible)
                continue;
            try
            {
                var top = c.TransformToAncestor(msgScroll).Transform(new System.Windows.Point(0, 0)).Y;
                if (top + 48 <= 0) anchor = m;              // 头像整体在视口顶边之上 → 候选，继续找更近的一条
                else if (top > viewH) break;                // 已扫到视口下方，后续只会更低
            }
            catch
            {
                // 容器正在生成/回收时 TransformToAncestor 可能异常，跳过该条即可
                continue;
            }
        }
        return anchor;
    }

    /// <summary>收集已滚出视口上方的全部用户消息（时间序 旧→新），供顶部用户区竖排展示（需求 10）。
    /// 与 FindTopAnchor 同规则判定（头像区整体在视口顶边之上）；返回全部而非单条，由调用方取尾部 k 条。</summary>
    List<ChatMessage> CollectTopUsers()
    {
        var res = new List<ChatMessage>();
        if (msgList == null || msgScroll == null) return res;
        var viewH = msgScroll.ViewportHeight;
        foreach (var m in messages)
        {
            if (m.Kind != MsgKind.User) continue;
            // 容器未生成（刚添加尚未布局）无法判定位置，跳过
            if (msgList.ItemContainerGenerator.ContainerFromItem(m) is not FrameworkElement c || !c.IsVisible)
                continue;
            try
            {
                var top = c.TransformToAncestor(msgScroll).Transform(new System.Windows.Point(0, 0)).Y;
                if (top + 48 <= 0) res.Add(m);   // 头像整体在视口顶边之上 → 候选
                else if (top > viewH) break;     // 已扫到视口下方，后续只会更低
            }
            catch
            {
                continue;   // 容器正在生成/回收时 TransformToAncestor 可能异常，跳过该条即可
            }
        }
        return res;
    }

    SessionItem? topUserSess;    // 顶部用户区所属会话（缓存键：切会话强制重建气泡，防位置指纹撞车残留上会话内容）

    // 顶部用户区气泡排版常量：量高/截断/渲染三处必须同源，否则预算与实际高度不符会撑高信息栏
    const double TopUserLineH = 16;         // 折行行高（BlockLineHeight 恒定行高，便于按像素预算折算行数）
    const int TopUserMaxLines = 3;          // 单条气泡长内容最多折 3 行
    const double TopUserPadX = 20;          // 气泡左右内边距合计（Padding 10+10）
    const double TopUserPadY = 10;          // 气泡上下内边距(3+3) + 条目下外边距(4)
    const double TopUserBubbleMaxRatio = 0.5;   // 气泡宽度上限 = 会话区宽度的此比例（内容短则自适应收窄，不强制折行）
    const double TopUserRightExtra = 54;        // 右侧段固定占宽：左外边距 20 + 头像 26 + 头像左间距 8

    /// <summary>重建顶部用户区气泡竖排：整段底部对齐，高度预算 = 左侧 GAIRR 信息项（topAgentSec）的高度——
    /// 信息栏高度只由左侧计算决定，右侧永不撑高。最新一条必显（超预算时按可容纳行数截断），其上仍有空间
    /// 就继续往上追加更早的用户消息，直到放不下为止；每条气泡宽度上限为会话区的 50%（内容短时自适应收窄、不强制折行），
    /// 长内容折行、最多 3 行、悬停看全文。
    /// 指纹（气泡宽度+高度预算+消息位置序列）未变化不重建（防 100ms 高频重建闪烁）。</summary>
    void RebuildTopUserStack(List<ChatMessage> upAbove)
    {
        if (topUserStack == null || topUserSec == null || topAgentSec == null || chatTopBar == null || msgList == null) return;
        if (upAbove.Count == 0) return;
        // 会话切换：位置指纹可能与上会话撞车（同样 index 序列），清空强制重建防残留上会话气泡
        if (!ReferenceEquals(topUserSess, currentSession))
        {
            topUserSess = currentSession;
            topUserSig = null;
        }

        // 1) 栏内可用宽度（chatTopBar 内边距之内）；顶栏尚未布局时按常见宽度估算，下一轮 100ms 刷新自动校正
        var clientW = chatTopBar.ActualWidth - chatTopBar.Padding.Left - chatTopBar.Padding.Right;
        if (clientW < 400) clientW = 1100;

        // 2) 会话区宽度（msgScroll：编排面板展开时会变窄）→ 气泡宽度上限 = 会话区 50%；顶栏未布局时按 clientW 估算
        var chatW = msgScroll != null && msgScroll.ActualWidth > 200 ? msgScroll.ActualWidth : clientW;
        var bubbleCap = Math.Max(200, chatW * TopUserBubbleMaxRatio);

        // 3) 左侧 GAIRR 信息项尺寸：宽度取其自然内容宽（ActualWidth 会被 DockPanel 拉满剩余空间，不能用），
        //    高度优先取已布局实测值；刚切显（0 值）时按"预留右侧最大气泡宽"量一次，下一轮 100ms 刷新自动校正
        var leftW = topAgentSec.DesiredSize.Width;
        var leftH = topAgentSec.Visibility == Visibility.Visible ? topAgentSec.ActualHeight : 0;
        if (leftW <= 0 || leftH <= 0)
        {
            topAgentSec.Measure(new Size(Math.Max(240, clientW - bubbleCap - TopUserRightExtra), double.PositiveInfinity));
            if (leftW <= 0) leftW = topAgentSec.DesiredSize.Width;
            if (leftH <= 0 && topAgentSec.Visibility == Visibility.Visible) leftH = topAgentSec.DesiredSize.Height;
        }
        // 预算 = 左侧高度；左侧整段隐藏（无 Agent 锚点且非编排执行）时回落到 3 行，保证最新一条完整可见
        var budget = leftH > 0 ? leftH : TopUserMaxLines * TopUserLineH + TopUserPadY;
        // 气泡宽度：左侧信息项之外还剩多少给多少，封顶会话区 50%；MaxWidth 只是上限，内容短时气泡自适应收窄（不强制折行）
        var bubbleW = Math.Min(bubbleCap, Math.Max(200, clientW - leftW - TopUserRightExtra));
        var textW = bubbleW - TopUserPadX;
        topUserSec.MaxHeight = Math.Max(26, budget);   // 兜底：测量有偏差时也不让右侧撑高信息栏（超出部分裁掉）

        // 4) 从最新一条往上装：最新必显（按预算截断行数），更早的仅在完整放得下时追加
        var items = new List<(string Full, string Text)>();   // 逆序收集（新→旧），渲染前反转成 旧→新
        var firstPos = -1;                                    // 最上面那条在 messages 中的位置（进指纹）
        var used = 0.0;
        for (var i = upAbove.Count - 1; i >= 0; i--)
        {
            var full = upAbove[i].Text ?? "";
            var txt = full.Replace("\r", " ").Replace("\n", " ").Trim();
            if (string.IsNullOrWhiteSpace(txt)) txt = "（无文本内容）";
            double h;
            if (i == upAbove.Count - 1)
            {
                // 最新一条：行数 = 预算能装下的行数（1~3 行），装不下的尾部截断，保证贴头像这条不被裁掉
                var lines = Math.Max(1, Math.Min(TopUserMaxLines, (int)Math.Floor((budget - TopUserPadY) / TopUserLineH)));
                txt = TruncateToLines(txt, textW, lines);
                h = Math.Min(MeasureWrapHeight(txt, textW), lines * TopUserLineH) + TopUserPadY;
            }
            else
            {
                txt = TruncateToLines(txt, textW, TopUserMaxLines);   // 长内容折行、最多 3 行
                h = MeasureWrapHeight(txt, textW) + TopUserPadY;
                if (used + h > budget + 0.5) break;                   // 上面已无空间 → 不再往上追加更早的消息
            }
            used += h;
            firstPos = messages.IndexOf(upAbove[i]);
            items.Add((full, txt));
        }
        items.Reverse();

        // 5) 指纹 = 气泡宽度 + 高度预算 + 消息位置序列（位置唯一稳定：同文本不同消息的增删也能区分）
        var sig = $"{(int)bubbleW}|{(int)budget}|{firstPos}|{string.Join(",", upAbove.Select(m => messages.IndexOf(m)))}";
        if (sig == topUserSig) return;
        topUserSig = sig;
        topUserStack.Children.Clear();
        var bubble = (FindResource("GradUserBubble") as Brush) ?? Brushes.DodgerBlue;
        foreach (var it in items)
        {
            topUserStack.Children.Add(new Border
            {
                Background = bubble,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(0, 0, 0, 4),
                MaxWidth = bubbleW,
                Child = new TextBlock
                {
                    Text = it.Text,
                    MaxWidth = textW,
                    FontSize = 12,
                    Foreground = Brushes.White,
                    TextWrapping = TextWrapping.Wrap,                 // 长内容折行（取代原单行截断）
                    LineHeight = TopUserLineH,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    MaxHeight = TopUserMaxLines * TopUserLineH,       // 最多 3 行硬上限
                    ToolTip = it.Full,
                },
            });
        }
    }

    /// <summary>测量文本按气泡排版参数（字号 12 / 行高 TopUserLineH / 最多 3 行）在指定宽度折行后的高度</summary>
    static double MeasureWrapHeight(string text, double width)
        => MeasureTextHeight(text, width, TopUserMaxLines * TopUserLineH);

    /// <summary>离屏 TextBlock 量折行文本高度（与气泡同排版参数）；maxH 传 PositiveInfinity 表示不限行数，供截断二分判断</summary>
    static double MeasureTextHeight(string text, double width, double maxH)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = TopUserLineH,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };
        if (!double.IsPositiveInfinity(maxH)) tb.MaxHeight = maxH;
        tb.Measure(new Size(Math.Max(1, width), double.PositiveInfinity));
        return tb.DesiredSize.Height;
    }

    /// <summary>按最大行数截断折行文本：WPF 的 TextTrimming 对 TextWrapping=Wrap 无效，只能自行按测量裁剪——
    /// 二分查找能塞进 maxLines 行的最长前缀并补省略号；本来就放得下则原样返回。</summary>
    static string TruncateToLines(string text, double width, int maxLines)
    {
        if (string.IsNullOrEmpty(text) || maxLines <= 0) return text;
        var limit = maxLines * TopUserLineH + 0.5;
        if (MeasureTextHeight(text, width, double.PositiveInfinity) <= limit) return text;
        var lo = 0;
        var hi = text.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (MeasureTextHeight(text.Substring(0, mid) + "…", width, double.PositiveInfinity) <= limit) lo = mid;
            else hi = mid - 1;
        }
        return (lo > 0 ? text.Substring(0, lo).TrimEnd() : "") + "…";
    }

    /// <summary>编排执行常驻行显隐（信息栏 100ms 刷新调用）：执行编排期间顶部计划行恒显；
    /// 叶子行仅在阶段+内容齐备时显示（执行/审查/暂停提示）；收口后两行整体隐藏。
    /// 文本与颜色由 OrcStatusRefresh（1Hz + 日志/审查事件）维护。</summary>
    void UpdateOrcTopVis()
    {
        if (orcTopBar == null || orcTopLeaf == null) return;
        if (!orcExecActive || string.IsNullOrEmpty(orcExecPlanTitle))
        {
            orcTopBar.Visibility = Visibility.Collapsed;
            orcTopLeaf.Visibility = Visibility.Collapsed;
            return;
        }
        orcTopBar.Visibility = Visibility.Visible;
        var wantLeaf = orcExecStage == "reviewing" || orcExecStage == "paused"
            || (orcExecStage == "executing" && orcExecLeaf.Length > 0);
        orcTopLeaf.Visibility = wantLeaf ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>从执行器日志行解析顶部同步状态（需求 1/3）：叶子切换/暂停/恢复即时刷新顶部行。
    /// 由 StartPlanExecution 的 log 回调在 UI 线程调用；进度 done/total 与用时由 OrcStatusRefresh 读盘聚合。</summary>
    void ParseOrcExecLogLine(string line)
    {
        if (!orcExecActive) return;
        if (line.StartsWith("[plan-run] 执行叶子：", StringComparison.Ordinal))
        {
            orcExecStage = "executing";
            var t = line["[plan-run] 执行叶子：".Length..];
            var p = t.IndexOf("（id=", StringComparison.Ordinal);
            orcExecLeaf = p > 0 ? t[..p] : t;   // 剥掉（id=…）尾缀只留叶子标题
            OrcStatusRefresh();
        }
        else if (line.Contains("暂停中", StringComparison.Ordinal))
        {
            orcExecStage = "paused";
            OrcStatusRefresh();
        }
        else if (line.Contains("已恢复执行", StringComparison.Ordinal))
        {
            orcExecStage = "executing";
            OrcStatusRefresh();
        }
        // 其余日志行（开始/停止/完成/失败提示等）不逐条驱动：1Hz 轮询或收口复位已覆盖
    }

    /// <summary>编排执行状态同步入口（需求 1-3/7）：1Hz 定时器与日志/审查事件共用。
    /// 读一次 plan.json → 更新顶部计划行（标题+进度 done/total+用时）与叶子行（执行/审查/暂停文案与琥珀色），
    /// 并聚合各计划会话子叶状态到 SessionItem.PlanState（会话树圆点着色，需求 7）。
    /// 收口后（orcExecActive=false）仅做圆点聚合，顶部行由 UpdateOrcTopVis 隐藏。</summary>
    void OrcStatusRefresh()
    {
        if (orcTopBar == null || orcTopLeaf == null) return;
        var plans = GAIRR.AgentHost.PlanStore.LoadAll(cfg.ProjectRoot ?? "");
        if (orcExecActive)
        {
            var pl = plans.FirstOrDefault(p => string.Equals(p.Id, orcExecPlanId, StringComparison.OrdinalIgnoreCase));
            if (pl != null)
            {
                int done = 0, total = 0;
                foreach (var leafId in GAIRR.AgentHost.PlanStore.LeafOrder(pl))
                {
                    var n = pl.Nodes.FirstOrDefault(x => string.Equals(x.Id, leafId, StringComparison.OrdinalIgnoreCase));
                    if (n == null) continue;
                    total++;
                    if (n.Status is "passed" or "skipped") done++;
                }
                orcExecLeafDone = done;
                orcExecLeafTotal = total;
            }
            var bar = $"▶ {orcExecPlanTitle} · 已完成 {orcExecLeafDone}/{orcExecLeafTotal} · 用时 {ElapsedText(orcExecStart)}";
            if (orcTopBar.Text != bar) orcTopBar.Text = bar;
            if ((string?)orcTopBar.ToolTip != bar) orcTopBar.ToolTip = bar;   // 计划标题过长截断时可悬停看全文
            var leaf = orcExecStage switch
            {
                "reviewing" => "🔍 审查中：" + orcExecLeaf,
                "paused" => orcExecLeaf.Length > 0 ? "⏸ 已暂停：" + orcExecLeaf : "⏸ 已暂停 · 待继续执行",
                _ when orcExecLeaf.Length > 0 => "▸ 正在执行：" + orcExecLeaf,
                _ => "",
            };
            if (orcTopLeaf.Text != leaf) orcTopLeaf.Text = leaf;
            if ((string?)orcTopLeaf.ToolTip != leaf) orcTopLeaf.ToolTip = leaf;
            // 审查阶段琥珀提示（需求 2：顶部审核提示）；执行/暂停回落灰
            var fg = (FindResource(orcExecStage == "reviewing" ? "AmberBrush" : "DimBrush") as Brush)
                ?? System.Windows.Media.Brushes.Gray;
            if (!ReferenceEquals(orcTopLeaf.Foreground, fg)) orcTopLeaf.Foreground = fg;
        }
        UpdateOrcTopVis();
        RefreshPlanSessionDots(plans);
    }

    /// <summary>需求 7：计划会话圆点按子叶任务状态聚合着色（覆盖 RunState 运行态闪变，圆点不再随会话执行态变）：
    /// 任一 failed → 红(3)；有 pending/running/reviewing 未完成 → 桔(1)；全部 passed/skipped → 绿(2)；
    /// 无叶子/计划文件缺失 → 0（回落 RunState 色）。状态变化经 SessionItem.PlanState 通知树行刷新。
    /// 入参 plans 复用调用方已读的 plan.json 列表（null=自行读盘）。</summary>
    void RefreshPlanSessionDots(List<GAIRR.AgentHost.PlanDto>? plans = null)
    {
        try
        {
            if (sessionHistory == null) return;
            if (plans == null) plans = GAIRR.AgentHost.PlanStore.LoadAll(cfg.ProjectRoot ?? "");
            if (plans.Count == 0) return;
            var byId = plans.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var s in sessionHistory)
            {
                if (!s.IsOrchestration || string.IsNullOrWhiteSpace(s.PlanId)) continue;
                if (!byId.TryGetValue(s.PlanId, out var pl))
                {
                    if (s.PlanState != 0) s.PlanState = 0;   // 计划文件缺失/跨项目：回落默认
                    continue;
                }
                var order = GAIRR.AgentHost.PlanStore.LeafOrder(pl);
                var ps = 0;
                if (order.Count > 0)
                {
                    var anyFail = false;
                    var anyNotDone = false;
                    foreach (var leafId in order)
                    {
                        var n = pl.Nodes.FirstOrDefault(x => string.Equals(x.Id, leafId, StringComparison.OrdinalIgnoreCase));
                        if (n == null) continue;
                        switch (n.Status)
                        {
                            case "failed": anyFail = true; break;
                            case "passed":
                            case "skipped": break;
                            default: anyNotDone = true; break;   // pending/running/reviewing/空 → 未完成
                        }
                    }
                    ps = anyFail ? 3 : anyNotDone ? 1 : 2;
                }
                if (s.PlanState != ps) s.PlanState = ps;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Error] 刷新计划会话圆点失败: {ex.Message}");
        }
    }

    /* ================= 编辑窗（AvalonEdit） ================= */

    string? editorFilePath;
    /// <summary>编辑窗查找栏（AvalonEdit 内置 SearchPanel）：挂在 editor 的 adorner 层；编辑窗打开时显示、关闭时隐藏，未打开文件时不干扰主界面</summary>
    ICSharpCode.AvalonEdit.Search.SearchPanel editorSearchPanel = null!;

    /// <summary>打开文件到编辑窗</summary>
    public void OpenEditor(string path)
    {
        if (!System.IO.File.Exists(path)) return;
        editorFilePath = path;
        editorTitle.Text = System.IO.Path.GetFileName(path);
        editor.Text = System.IO.File.ReadAllText(path);
        // 按扩展名切换语法高亮（内置定义按浅色纸面设计，附在暗底上不可读，统一经 DarkSyntax.Adapt 就地改暗底配色）
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        editor.SyntaxHighlighting = DarkSyntax.Adapt(ext switch
        {
            ".cs" => ICSharpCode.AvalonEdit.Highlighting.HighlightingManager.Instance.GetDefinition("C#"),
            ".xaml" => ICSharpCode.AvalonEdit.Highlighting.HighlightingManager.Instance.GetDefinition("XML"),
            ".xml" => ICSharpCode.AvalonEdit.Highlighting.HighlightingManager.Instance.GetDefinition("XML"),
            ".json" => ICSharpCode.AvalonEdit.Highlighting.HighlightingManager.Instance.GetDefinition("Json"),
            ".md" => ICSharpCode.AvalonEdit.Highlighting.HighlightingManager.Instance.GetDefinition("MarkDown"),
            ".js" => ICSharpCode.AvalonEdit.Highlighting.HighlightingManager.Instance.GetDefinition("JavaScript"),
            ".ts" => ICSharpCode.AvalonEdit.Highlighting.HighlightingManager.Instance.GetDefinition("TypeScript"),
            ".html" => ICSharpCode.AvalonEdit.Highlighting.HighlightingManager.Instance.GetDefinition("HTML"),
            ".css" => ICSharpCode.AvalonEdit.Highlighting.HighlightingManager.Instance.GetDefinition("CSS"),
            ".py" => ICSharpCode.AvalonEdit.Highlighting.HighlightingManager.Instance.GetDefinition("Python"),
            ".ini" => ICSharpCode.AvalonEdit.Highlighting.HighlightingManager.Instance.GetDefinition("Ini"),
            _ => null
        });
        editorPanel.Visibility = Visibility.Visible;
        editorSearchPanel.Open();   // 查找栏随编辑窗显示在顶部（随后 editor.Focus 把焦点还给编辑器）
        editor.Focus();
    }

    void OnEditorSave(object sender, RoutedEventArgs e)
    {
        if (editorFilePath == null) return;
        System.IO.File.WriteAllText(editorFilePath, editor.Text, new System.Text.UTF8Encoding(false));
        editorTitle.Text = System.IO.Path.GetFileName(editorFilePath) + " ✓";
    }

    void OnEditorClose(object sender, RoutedEventArgs e)
    {
        editorSearchPanel.Close();   // 关闭编辑器时收起查找栏并清除高亮，避免残留
        editorPanel.Visibility = Visibility.Collapsed;
        editorFilePath = null;
    }

    void OnWindowClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        // 代码页编辑未保存：拦截关闭让用户先保存/放弃（SaveSession 只存会话内容，不含代码页修改）
        if (codeCard != null && codeEditing && codeDirty)
        {
            var r = MessageBox.Show(this,
                $"代码查看页正在编辑「{codeCard.FilePath}」且尚未保存，关闭窗口将丢失这些修改。\n\n" +
                "是(Y)：保存到磁盘后关闭（原文件自动备份到 back/）\n" +
                "否(N)：放弃修改并关闭\n" +
                "取消：继续使用（不关闭）",
                "未保存的修改", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.No);
            if (r == MessageBoxResult.Yes && !SaveCodeToDisk()) return;   // 用户选择保存但失败/被中途取消：留在窗口
            if (r == MessageBoxResult.Cancel) return;
        }
        // 手机端服务开启着且非托盘"退出程序"发起：拦截关闭、最小化到托盘继续服务——
        // 托盘常驻右下角，可开启/退出服务、打开主窗口、查看实时会话数；仅托盘"退出程序"才真正停服退出
        if (mobileSvcUp && !closingStoppingSvc && !trayExitRequested)
        {
            e.Cancel = true;
            Hide();
            ShowToast("手机端服务运行中，已最小化到系统托盘（点托盘图标可打开主窗口）");
            return;
        }
        // 手机端服务开启着（托盘"退出程序"/登录取消强制退出发起）：操作手机端按钮的"关"动作停服务（与用户手动点开关同一套逻辑），
        // 等 1 秒后退出主程序——拦截本次关闭，异步触发按钮关闭动作，稍候二次 Close 走正常退出流程
        if (mobileSvcUp && !closingStoppingSvc)
        {
            closingStoppingSvc = true;
            e.Cancel = true;
            ToggleOffMobileSvcThenCloseAsync();
            return;
        }
        SaveSession();
        // 本叶子：退出统一中止全部运行会话（含后台挂起/切走放行任务）并清空运行中枢——
        // 各会话工作消息按"已中止"收口写回归属历史落盘（重启打开历史完整、不残留运行态），无孤儿线程/悬挂定时器/句柄
        ShutdownAllSessionRunners();
        if (mobileTray != null)   // 真正退出：销毁托盘图标（含轮询定时器），避免进程退出后图标残影
        {
            mobileTray.Dispose();
            mobileTray = null;
        }
        taskScheduler?.Stop();
        GpuMonitor.Current.Stop();   // 释放监控轮询定时器
    }

    /// <summary>关窗停服：操作手机端按钮的"关"动作（拨开关为关并触发其 Click 处理，复用按钮的启停/回读/提示整套逻辑）执行 stop，
    /// 稍候 1 秒让 Server/公网隧道退出，然后二次 Close 走正常保存/中止/退出收尾——不强等后台 stop 命令结束，避免窗口卡死在关闭上。</summary>
    async void ToggleOffMobileSvcThenCloseAsync()
    {
        // 模拟用户把手机端开关拨到"关"并触发其关闭动作（服务开启着即执行 bat stop）
        if (!mobileSvcBusy && mobileSvcSwitch.IsChecked != false)
        {
            mobileSvcSwitch.IsChecked = false;
            OnMobileSvcToggle(mobileSvcSwitch, new RoutedEventArgs());
        }
        await Task.Delay(1000);      // 等待按钮关闭动作执行窗口（断公网隧道/停 Server），随后退出主程序
        Close();                      // 二次进入 OnWindowClosing：closingStoppingSvc=true → 不再拦截，直接走保存/中止/退出收尾
    }

    /* ================= 会话持久化（JSON 存盘恢复） ================= */

    string SessionPath => GAIRR.Core.Paths.Session;

    /// <summary>把执行过程时间线条目按原顺序转换为持久化记录</summary>
    ProcessItemRecord ToProcessItemRecord(ProcessItem p) => p switch
    {
        TodoItem t => new ProcessItemRecord { Kind = "todo", Title = t.Title, Content = t.Content, Open = t.Open },
        ThinkingItem t => new ProcessItemRecord { Kind = "thinking", Title = t.Title, Content = t.Content, Open = t.Open, DurationMs = t.DurationMs, ContextTokens = t.ContextTokens, Round = t.Round, Time = t.CreatedAt },
        ToolCall t => new ProcessItemRecord { Kind = "tool", Title = t.Title, Status = t.Status, Inner = t.Inner, Summary = t.Summary, Warn = t.Warn, Icon = t.Icon, IconColor = t.IconColor, IsStep = t.IsStep, Open = t.Open, Round = t.Round, FilePath = t.FilePath, StartLine = t.StartLine, EndLine = t.EndLine },
        DangerConfirmItem d => new ProcessItemRecord { Kind = "danger", Content = d.Content, Visible = d.Visible, Open = d.Open },
        _ => new ProcessItemRecord { Kind = "unknown" }
    };

    /// <summary>问题卡片 → 持久化记录（题面/选项/勾选文本引用/补充草稿/折叠状态）；无卡返回 null 不落盘（旧存档兼容）。</summary>
    static List<QuestionCardRecord>? ToQuestionCardRecords(ChatMessage m)
    {
        if (m.QuestionCards.Count == 0) return null;
        return m.QuestionCards.Select(q => new QuestionCardRecord
        {
            CardId = q.CardId,
            No = q.No,
            Question = q.Question,
            Multi = q.Multi,
            Options = q.Options.Select(o => new QuestionOptionRecord { Text = o.Text, Rec = o.Rec }).ToList(),
            SelectedTexts = q.Selected.Select(o => o.Text).ToList(),
            ExtraText = q.ExtraText,
            ExtraOpen = q.ExtraOpen,
            Answered = q.Answered,
            Summary = q.Summary,
        }).ToList();
    }

    /// <summary>历史消息惰性装配：把消息的待装配记录实例化为卡片。会话重放只挂 PendingItems 骨架，
    /// 用户首次展开该轮（OnStepsClick）时调用一次，此后 PendingItems 置空（已实例化卡片保留于 ProcessItems）。</summary>
    void MaterializePending(ChatMessage m)
    {
        if (m.PendingItems == null || m.PendingItems.Count == 0) return;
        foreach (var pi in m.PendingItems)
            AddProcessItemFromRecord(m, pi);
        m.PendingItems = null;
    }

    /// <summary>折算待装配记录：新版 ProcessItems 优先原样使用；旧版分类型字段（Todos/ThinkingItems/Reasonings/Tools）折算为统一时间线记录。
    /// 折算后的内容清洗（CleanMd 等）在实例化（AddProcessItemFromRecord）时进行，与实时路径一致。</summary>
    static List<ProcessItemRecord> ToPendingRecords(MessageRecord m)
    {
        if (m.ProcessItems is { Count: > 0 }) return m.ProcessItems;
        var list = new List<ProcessItemRecord>();
        if (m.Todos != null)
            foreach (var td in m.Todos)
                list.Add(new ProcessItemRecord { Kind = "todo", Title = td.Title ?? "", Content = td.Content ?? "", Open = true });
        if (m.ThinkingItems is { Count: > 0 })
        {
            foreach (var ti in m.ThinkingItems)
                list.Add(new ProcessItemRecord { Kind = "thinking", Title = ti.Title ?? "", Content = ti.Content ?? "", Open = ti.Open });
        }
        else if (m.Reasonings != null)
        {
            foreach (var r in m.Reasonings)
                list.Add(new ProcessItemRecord { Kind = "thinking", Title = "思考过程", Content = r ?? "", Open = true });
        }
        if (m.Tools != null)
            foreach (var t in m.Tools)
                list.Add(new ProcessItemRecord { Kind = "tool", Title = t.Title ?? "", Status = t.Status ?? "", Inner = t.Inner ?? "", Warn = t.Warn, Summary = t.Summary ?? "", IsStep = t.IsStep });
        return list;
    }

    /// <summary>按原顺序把持久化记录还原为执行过程时间线条目</summary>
    void AddProcessItemFromRecord(ChatMessage msg, ProcessItemRecord pi)
    {
        switch (pi.Kind?.ToLowerInvariant())
        {
            case "todo":
                msg.ProcessItems.Add(new TodoItem { Title = pi.Title ?? "", Content = pi.Content ?? "", Open = pi.Open });
                break;
            case "thinking":
                msg.ProcessItems.Add(new ThinkingItem { Title = pi.Title ?? "", Content = CleanMd(pi.Content?.Trim() ?? ""), Open = pi.Open, DurationMs = pi.DurationMs, ContextTokens = pi.ContextTokens, Round = pi.Round, CreatedAt = pi.Time ?? DateTime.Now });
                break;
            case "tool":
                msg.ProcessItems.Add(new ToolCall { Title = pi.Title ?? "", Status = pi.Status ?? "", Inner = pi.Inner ?? "", Summary = pi.Summary ?? "", Warn = pi.Warn, Icon = pi.Icon, IconColor = pi.IconColor, IsStep = pi.IsStep, Open = pi.Open, Round = pi.Round, FilePath = pi.FilePath ?? "", StartLine = pi.StartLine, EndLine = pi.EndLine });
                break;
            case "danger":
                msg.ProcessItems.Add(new DangerConfirmItem { Content = pi.Content ?? "", Visible = pi.Visible, Open = pi.Open });
                break;
        }
    }

    /// <summary>历史消息的问题卡片还原：record → 卡 VM（题面/选项/勾选/补充草稿/折叠状态原样恢复）挂回消息并订阅多卡公共提交；
    /// 已提交卡折叠为摘要行，未提交卡可继续勾选/补充/提交（中断续选）。会话内题号顺延到已恢复的最大号，新题不重号。</summary>
    void RestoreQuestionCards(ChatMessage msg, MessageRecord m)
    {
        if (m.QuestionCards is not { Count: > 0 }) return;
        foreach (var rc in m.QuestionCards)
        {
            var qv = new QuestionCardVm
            {
                CardId = rc.CardId,
                No = rc.No,
                Question = rc.Question,
                Multi = rc.Multi,
                ExtraText = rc.ExtraText,
                ExtraOpen = rc.ExtraOpen,
                Answered = rc.Answered,
                Summary = rc.Summary,
            };
            foreach (var ro in rc.Options)
                qv.Options.Add(new QuestionOptionVm { Owner = qv, Text = ro.Text, Rec = ro.Rec });
            // 勾选项按文本映射回选项本体：Selected 与 Options 必须同一实例，后续 SyncSelect/提交判定才一致
            foreach (var t in rc.SelectedTexts ?? new())
            {
                var opt = qv.Options.FirstOrDefault(o => o.Text == t);
                if (opt != null) qv.Selected.Add(opt);
            }
            if (qv.No > qCardSeq) qCardSeq = qv.No;   // 续号：后续主持人新题从 max+1 起
            msg.AddQuestionCard(qv);   // 订阅作答变化 → 多卡公共提交条状态即时恢复
        }
        msg.Refresh();
    }

    /* ================= 读取快照（data/snap/{会话Id}/）：Read 成功瞬间原码落盘 ================= */

    /// <summary>当前消息容器对应的快照目录键（会话 Id 或任务回放 Id；null=暂无快照域，查看时退回磁盘文件）</summary>
    string? SnapDirKey() => openRunId ?? currentSession?.Id;

    /// <summary>快照目录根（与 session_history.json 同域的 data/snap/，跨项目可写且随数据目录迁移）</summary>
    static string SnapRoot => System.IO.Path.Combine(GAIRR.Core.Paths.DataDir, "snap");

    /// <summary>快照目标路径的安全拼接（防 rel 含 ../ 或盘符逃出快照根）</summary>
    static string? SnapFilePath(string key, string rel)
    {
        var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(SnapRoot, key));
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, rel.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        return full.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <summary>把文件读取时刻的原码复制进会话快照：字节同步读取（此刻=模型刚读完、后续 Edit 尚未执行），
    /// 解码为 UTF-8 文本后异步写盘（统一 \n 行分隔，行号与 Read 工具展示一致）。快照写失败不影响工具卡流程。</summary>
    void EnsureSnapshot(string rel)
    {
        var key = SnapDirKey();
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(rel)) return;
        try
        {
            var src = System.IO.Path.GetFullPath(System.IO.Path.Combine(cfg.ProjectRoot ?? "", rel.Replace('/', System.IO.Path.DirectorySeparatorChar)));
            if (!System.IO.File.Exists(src)) return;
            var text = GAIRR.Core.Phase1Tools.Decode(System.IO.File.ReadAllBytes(src)).Text.Replace("\r\n", "\n");
            var abs = SnapFilePath(key, rel);
            if (abs == null) return;
            _ = Task.Run(() =>
            {
                try
                {
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(abs)!);
                    System.IO.File.WriteAllText(abs, text, new System.Text.UTF8Encoding(false));
                }
                catch { }
            });
        }
        catch { }
    }

    /// <summary>读取会话快照文本（无快照返回 null；调用方再退回磁盘/git）</summary>
    static string? TryReadSnapshotText(string key, string rel)
    {
        var abs = SnapFilePath(key, rel);
        if (abs == null || !System.IO.File.Exists(abs)) return null;
        try { return System.IO.File.ReadAllText(abs, System.Text.Encoding.UTF8); }
        catch { return null; }
    }


    void SaveSession()
    {
        try
        {
            var record = new SessionRecord
            {
                Project = cfg.ProjectRoot,
                // 完整口径：分段加载下 messages 只是尾部窗口，落盘不能丢掉尚未装配的更早历史
                Messages = AllViewMsgs().Select(m => new MessageRecord
                {
                    Kind = m.Kind.ToString(),
                    Who = m.Who,
                    Text = m.Text,
                    Cmd = m.Cmd,
                    Note = m.Note,
                    IsAlert = m.IsAlert,
                    AlertLevel = m.AlertLevel,
                    Steps = m.Steps,
                    // 消息级执行参数快照：该条消息生成时刻的 角色+模式+模型（随消息落盘，历史重放按各消息自身快照显示；空回退会话 Pin）
                    RunRole = m.RunRole ?? currentSession?.PinRole,
                    RunRoleDisplay = m.RunRoleDisplay ?? currentSession?.PinRoleDisplay,
                    RunMode = m.RunMode ?? currentSession?.PinMode,
                    RunModeDisplay = m.RunModeDisplay ?? currentSession?.PinModeDisplay,
                    RunProvider = m.RunProvider ?? currentSession?.PinProvider,
                    RunModel = m.RunModel ?? currentSession?.PinModel,
                    // 新版：按实际发生顺序保存全部时间线条目（含历史消息尚未展开实例化的 PendingItems，防止惰性装配丢卡）；
                    // 展开过的消息 PendingItems 已置空，此处在实例化后只保存 ProcessItems，与实时消息一致
                    ProcessItems = m.ProcessItems.Select(p => ToProcessItemRecord(p))
                        .Concat(m.PendingItems ?? Enumerable.Empty<ProcessItemRecord>()).ToList(),
                    QuestionCards = ToQuestionCardRecords(m),  // 澄清卡片+作答进度随消息落盘：中断/重启后可还原续选
                    // 本轮改动文件清单（消息底部“本轮改动”条带 + 左栏“本会话改动”卡片的数据源）；无改动不写该键
                    Changes = m.RoundChanges.Count > 0 ? SessionChanges.ToRecords(m.RoundChanges) : null
                }).ToList()
            };
            var json = System.Text.Json.JsonSerializer.Serialize(record, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });   // 会话文件中文直存
            System.IO.File.WriteAllText(SessionPath, json, System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Error] 保存会话失败: {ex.Message}");
        }
    }

    void LoadSession()
    {
        if (!System.IO.File.Exists(SessionPath)) return;
        try
        {
            var json = System.IO.File.ReadAllText(SessionPath);
            var record = System.Text.Json.JsonSerializer.Deserialize<SessionRecord>(json);
            if (record?.Messages == null) return;
            qCardSeq = 0;   // 载入会话：澄清问题序号重新编号（还原卡片时顺延到最大号）
            ResetMsgView(); // 连分段加载暂存一起复位（下方按回合重新分段装配）
            var loaded = new List<ChatMessage>();
            foreach (var m in record.Messages)
            {
                var msg = new ChatMessage
                {
                    Kind = Enum.Parse<MsgKind>(m.Kind),
                    Who = m.Who,
                    Text = m.Text,
                    Cmd = m.Cmd,
                    Note = m.Note,
                    IsAlert = m.IsAlert,
                    AlertLevel = m.AlertLevel,
                    Steps = m.Steps,
                    Finished = true,
                    ProcessOpen = false,
                };
                // 惰性装配：时间线卡片不立即实例化，挂 PendingItems 待用户首次展开该轮时再建（OnStepsClick → MaterializePending）
                msg.PendingItems = ToPendingRecords(m);
                // 清理旧 Steps 后缀，重建基础文本和显示文本
                var baseText = (m.Steps ?? "").Replace(" · ▸ 展开", "").Replace(" · ▾ 收缩", "");
                msg.StepsBase = baseText;
                // 有过程内容（思考条/工具卡）时才提供展开/收缩交互
                msg.Steps = baseText + (msg.HasProcessContent ? StepsHint(msg.ProcessOpen) : "");
                RestoreQuestionCards(msg, m);   // 还原澄清卡片（题面/作答进度/折叠状态）：中断续选的基础
                RestoreRoundChanges(msg, m, record.Project);   // 还原本轮改动清单（消息底部条带）；存量历史无记录不动
                // P：带头像历史消息按各消息落盘的执行参数快照回填标题/Who（启动恢复路径；旧历史无快照不填，保持现状）
                if (msg.AgentVisible == Visibility.Visible)
                {
                    ApplyRecordSnapshot(msg, m);
                    var snapText = SnapshotTextOf(m);
                    if (snapText != null)
                    {
                        msg.AvatarTitle = snapText;
                        if (msg.Who.Contains("概尔 Agent", StringComparison.OrdinalIgnoreCase))
                            msg.Who = "概尔 Agent · " + snapText;
                    }
                }
                loaded.Add(msg);
            }
            // 分段装配（与 OpenSession 同一口径）：先装最近一回合，不足一屏再向前补装
            int cut = LastTurnStart(loaded);
            for (int i = cut; i < loaded.Count; i++) messages.Add(loaded[i]);
            for (int i = 0; i < cut; i++) pendingOlderMsgs.Add(loaded[i]);
            RefreshOrcQProgress();   // 澄清进度首刷（与 OpenSession 一致；LoadSession 为备用恢复路径）
            FillOlderUntilOneScreen();   // 不足一屏则继续向前逐回合补装（超过一屏即停）
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Error] 加载会话失败: {ex.Message}");
        }
    }

    /* ================= 任务历史 & 会话历史 ================= */

    /// <summary>启动时把当前对话对应的会话项在列表中标记为选中</summary>
    void SelectCurrentSessionInList()
    {
        if (messages.Count == 0 && pendingOlderMsgs.Count == 0) return;
        // 用完整口径取用户首句：分段加载下首句可能还在未装配的更早历史里
        var titleMsg = AllViewMsgs().FirstOrDefault(m => m.Kind == MsgKind.User);
        var title = titleMsg?.Text ?? "新会话";
        title = SessionItem.ToOneLine(title);   // 单行化：去除回车/换行，与列表项单行显示口径一致
        title = title[..Math.Min(title.Length, 30)];
        var projRoot = cfg.ProjectRoot ?? "";
        var match = sessionHistory.FirstOrDefault(s => s.Title == title && s.Project == projRoot);
        if (match == null) return;
        foreach (var s in sessionHistory) { s.Selected = false; s.IsCurrent = false; }
        match.Selected = true;
        match.IsCurrent = true;
        RefreshSessionList();
    }

    // （原 RestoreLoopContext 启动恢复已删除：重启后历史统一在各会话 Runner 创建时按会话装载，见 EnsureRunner 装载段，
    //  两会话各带各的 history 互不串台；无全局 loop 可载）

    /// <summary>后台任务"归属项目根"：优先所属会话 Project（=发起时归属根，随会话记录持久），兜底实时 cfg.ProjectRoot。
    /// 任务落盘/收口/快照（git 提交、运行记录、计划存储）必须用它 —— 后台任务在切项目后跑完时实时根已指向新项目，
    /// 继续用实时根会把旧项目的提交/记录串写到新项目根（验收 8：切回不丢、不串写旧根）。</summary>
    string TaskOwnerProjectRoot(SessionItem? owner) =>
        !string.IsNullOrWhiteSpace(owner?.Project) ? owner.Project : cfg.ProjectRoot;

    // ================= 本会话改动 / 本轮改动（数据层见 SessionChanges.cs：变更日志 ↔ 行 VM ↔ 落盘记录） =================

    /// <summary>任务收口聚合本轮改动：按任务时间窗从变更日志取本会话的写入记录 → 行 VM，
    /// 写入工作消息（消息底部“本轮改动”条带）并并入归属会话累计清单（左栏“本会话改动”卡片）。
    /// 调用点在落盘之前，故本轮清单随 MessageRecord.Changes 一起持久化；
    /// 变更日志无记录（纯问答/只读轮次）→ 两处均不动，条带与卡片按空清单自动隐藏。</summary>
    void CollectRoundChanges(ChatMessage? msg, SessionItem? session, DateTime since)
    {
        try
        {
            var entries = journal.EntriesSince(since, session?.Id ?? "");
            if (entries.Count == 0) return;
            var root = TaskOwnerProjectRoot(session);
            var rows = entries.Select(e => SessionChanges.ToVm(e, root)).ToList();
            if (msg != null) { SessionChanges.Merge(msg.RoundChanges, rows); msg.RefreshChanges(); }
            if (session != null) SessionChanges.Merge(session.ChangedFiles, rows);
            if (ReferenceEquals(session, currentSession)) RefreshChangedCard();   // 当前会话：卡片计数实时跟进
        }
        catch { }   // 聚合失败不影响收口主链（条带/卡片保持空）
    }

    /// <summary>左栏“本会话改动”卡片随当前会话切换：DataContext 指向该会话，计数/清单/折叠全走 SessionItem 绑定；
    /// 无当前会话（新建未落盘/欢迎页）→ 整卡隐藏。切会话、收口、历史还原后各调一次。</summary>
    void RefreshChangedCard()
    {
        if (sessionChangedCard == null) return;
        sessionChangedCard.DataContext = currentSession;
        sessionChangedCard.Visibility = currentSession == null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>历史消息还原本轮改动清单：落盘的 Changes → 行 VM 挂到该条消息（底部“本轮改动”条带）；
    /// root 为该会话所属项目根（相对路径基准），无记录（存量历史/纯问答轮次）→ 不动，条带保持隐藏。</summary>
    void RestoreRoundChanges(ChatMessage msg, MessageRecord m, string? projectRoot)
    {
        if (m.Changes == null || m.Changes.Count == 0) return;
        var root = string.IsNullOrWhiteSpace(projectRoot) ? cfg.ProjectRoot : projectRoot;
        SessionChanges.Merge(msg.RoundChanges, m.Changes.Select(r => SessionChanges.ToVm(r, root)));
        msg.RefreshChanges();
    }

    /// <summary>打开历史会话：按各消息落盘的改动记录还原会话累计清单（存量历史无 Changes 字段 → 清单空、整卡隐藏）。</summary>
    void RestoreSessionChanges(SessionItem session)
    {
        try { session.ResetChangedFiles(SessionChanges.FromMessages(session.Messages, TaskOwnerProjectRoot(session))); }
        catch { }
    }

    /// <summary>消息底部“本轮改动”条带点击：展开/收起该轮文件清单（摘要行常驻，默认折叠）。</summary>
    void OnChangesToggle(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ChatMessage msg)
        {
            msg.ChangesOpen = !msg.ChangesOpen;
            e.Handled = true;   // 不冒泡到消息流/滚动容器
        }
    }

    /// <summary>左栏卡片标题行点击：展开/收起本会话改动清单。</summary>
    void OnChangedCardToggle(object sender, MouseButtonEventArgs e)
    {
        if (currentSession != null) currentSession.ChangedOpen = !currentSession.ChangedOpen;
        e.Handled = true;
    }

    /// <summary>改动文件行点击（左栏卡片 / 消息条带共用隐式模板）：在右侧代码查看器打开该文件，
    /// 并默认进“改动对比”视图——把本会话改前基线（back/ 写前备份，缺失时退会话快照）与当前内容做行级 diff，
    /// 增行绿底 / 删行红底 / 双列行号着色突显；工具栏“◧ 改动对比 ⇄ ☰ 全文”可切回文件全文视图编辑。</summary>
    void OnChangedFileClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ChangedFileVm vm && vm.RelPath.Length > 0)
        {
            OpenCodeViewer(new ToolCall { Title = vm.FileName, FilePath = vm.RelPath }, vm);
            e.Handled = true;
        }
    }

    void SaveCurrentSession(string? customTitle = null)
    {
        try
        {
            // 占位会话（点「+ 新会话」刚插入左侧列表、尚未发送首条消息）一律不落盘：它只是内存里的视觉落点，
            // 发送首条消息时由 EnsureSessionForSend 转正后才随本次保存写盘；未发送就离开由 DropPendingSession 摘除。
            // 必须放在最前面——落盘触发点有 8 处（保存会话/删除/排序前置/编排标记/切项目…），
            // 任一处顺手调进来都会把占位项写盘，历史里就又冒出空「新会话」记录。
            if (currentSession?.IsPending == true) return;
            if (messages.Count == 0 && pendingOlderMsgs.Count == 0) return;
            // 没有绑定当前会话且对话中没有任何用户实际输入时，不创建新会话占位。
            // 否则启动后点击历史会话、或打开空会话时，会把欢迎消息误存成一条"新会话"记录。
            // 读取口径用 AllViewMsgs()：分段加载下 messages 只是完整历史的尾部窗口，用户首句可能还在未装配段里
            if (currentSession == null && !AllViewMsgs().Any(m => m.Kind == MsgKind.User))
                return;
            // 标题来源优先级：
            // 1) 外部传入的自定义标题（如模型任务摘要）
            // 2) 会话内已记住的摘要标题
            // 3) 未生成摘要时，只有会话标题尚未确定才取第一条用户消息前 30 字；
            //    一旦通过摘要/用户手动设置等方式确定过标题，不再回退到用户首句话
            var title = customTitle ?? pendingSessionTitle;
            if (string.IsNullOrWhiteSpace(title))
            {
                if (!sessionTitleSet)
                {
                    var titleMsg = AllViewMsgs().FirstOrDefault(m => m.Kind == MsgKind.User);
                    title = titleMsg?.Text ?? "新会话";
                }
                else
                {
                    title = currentSession?.Title ?? "新会话";
                }
            }
            title = SessionItem.ToOneLine(title);   // 标题单行化：去除回车/换行/制表符，会话列表项只显示一行
            title = title[..Math.Min(title.Length, 30)].Trim();
            if (string.IsNullOrWhiteSpace(title)) title = "新会话";

            var projRoot = cfg.ProjectRoot ?? "";
            SessionItem saved;
            bool listInserted = false;   // 本次是否新增会话项：仅新项插入才重建整树；纯更新标题/时间由 INPC 实时刷新列表，免去每轮全量重建的卡顿

            if (currentSession != null && currentSession.Project == projRoot)
            {
                // 当前已绑定会话项：直接更新标题与消息，避免按标题匹配导致覆盖其它会话
                saved = currentSession;
                saved.Title = title;
                saved.Time = DateTime.Now;
                if (!sessionHistory.Contains(saved)) { sessionHistory.Insert(0, saved); listInserted = true; }
            }
            else
            {
                // 兼容历史路径/兜底：按标题+项目匹配已有会话（排除占位项：它尚未转正、也不该被兜底路径顺手写盘）
                var existing = sessionHistory.FirstOrDefault(s => s.Title == title && s.Project == projRoot && !s.IsPending);
                if (existing != null)
                {
                    saved = existing;
                    existing.Time = DateTime.Now;
                }
                else
                {
                    var now = DateTime.Now;
                    saved = new SessionItem { Title = title, Project = projRoot, Time = now, Created = now, Messages = new() };
                    sessionHistory.Insert(0, saved);
                    listInserted = true;
                }
                currentSession = saved;
                qCardSeq = 0;   // 切到既有会话：澄清问题序号从 1 重新编号
                SyncViewportFlags();   // 兜底绑定会话成为当前视口：刷新各 Runner 的 InViewport
            }

            // 落盘口径 = 完整会话消息（未装配的更早历史 + 已装配窗口）：分段加载只是"少渲染"，
            // 绝不能把尚未渲染的更早消息丢掉，否则一次保存就永久截断该会话历史
            saved.Messages = AllViewMsgs().Where(m => !m.NoPersist).Select(m => new MessageRecord
            {
                Kind = m.Kind.ToString(), Who = m.Who, Text = m.Text,
                Cmd = m.Cmd, Note = m.Note, Steps = m.Steps,
                // 消息级执行参数快照：该条消息生成时刻的 角色+模式+模型（随消息落盘，历史重放按各消息自身快照显示；空回退会话 Pin）
                RunRole = m.RunRole ?? saved.PinRole,
                RunRoleDisplay = m.RunRoleDisplay ?? saved.PinRoleDisplay,
                RunMode = m.RunMode ?? saved.PinMode,
                RunModeDisplay = m.RunModeDisplay ?? saved.PinModeDisplay,
                RunProvider = m.RunProvider ?? saved.PinProvider,
                RunModel = m.RunModel ?? saved.PinModel,
                // 新版：按实际发生顺序保存全部时间线条目，含历史消息尚未展开实例化的 PendingItems（防止惰性装配丢卡）；
                // 展开过的消息 PendingItems 已置空，此处在实例化后只保存 ProcessItems，与实时消息一致
                ProcessItems = m.ProcessItems.Select(p => ToProcessItemRecord(p))
                    .Concat(m.PendingItems ?? Enumerable.Empty<ProcessItemRecord>()).ToList(),
                QuestionCards = ToQuestionCardRecords(m),  // 澄清卡片+作答进度随消息落盘：中断/重启后可还原续选
                // 本轮改动文件清单（历史回放时还原条带，并向上汇总成会话累计清单）；无改动不写该键
                Changes = m.RoundChanges.Count > 0 ? SessionChanges.ToRecords(m.RoundChanges) : null
            }).ToList();

            // 当前保存的会话在列表中显示选中状态并置顶（树项经 INPC 实时刷新；无需重建整树）
            foreach (var s in sessionHistory) { s.Selected = false; s.IsCurrent = false; }
            saved.Selected = true;
            saved.IsCurrent = true;
            SaveSessionHistory();
            // 新增会话项才重建会话树（新分组/计数需重投影）；已有项的标题/时间/选中刷新已由属性通知完成，
            // 不再整树重建 —— 大历史量下该重建含排序+分组+容器重建，是点击会话/每轮消息卡顿感的主要来源之一
            if (listInserted) RefreshSessionList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Error] 保存当前会话失败: {ex.Message}");
        }
    }

    /// <summary>后台运行收口持久化（验收7）：前台主任务切项目/切会话放行后在后台跑完，其最终结果消息按归属写回
    /// 所属会话的消息历史并落盘（不依赖 currentSession/当前视口——离开时刻视口已清空）。离开时刻落盘的半成品快照
    /// （最后一条未完成的 Typing/Agent 记录，Steps 为空）就地替换，避免与最终结果重复；随后整条会话历史原子写盘。</summary>
    void PersistBgResult(SessionItem own, ChatMessage msg)
    {
        try
        {
            var record = new MessageRecord
            {
                Kind = msg.Kind.ToString(), Who = msg.Who, Text = msg.Text,
                Cmd = msg.Cmd, Note = msg.Note, Steps = msg.Steps,
                // 消息级执行参数快照：后台收口的消息未经过视口 AddMessage（六要素为空），回退其归属会话钉住参数
                RunRole = msg.RunRole ?? own.PinRole,
                RunRoleDisplay = msg.RunRoleDisplay ?? own.PinRoleDisplay,
                RunMode = msg.RunMode ?? own.PinMode,
                RunModeDisplay = msg.RunModeDisplay ?? own.PinModeDisplay,
                RunProvider = msg.RunProvider ?? own.PinProvider,
                RunModel = msg.RunModel ?? own.PinModel,
                // 与 SaveCurrentSession 同一口径：全部时间线条目，含未展开实例化的 PendingItems
                ProcessItems = msg.ProcessItems.Select(p => ToProcessItemRecord(p))
                    .Concat(msg.PendingItems ?? Enumerable.Empty<ProcessItemRecord>()).ToList(),
                QuestionCards = ToQuestionCardRecords(msg),
                // 本轮改动文件清单：后台收口的改动同样随归属会话落盘（重开该会话可见条带与会话累计清单）
                Changes = msg.RoundChanges.Count > 0 ? SessionChanges.ToRecords(msg.RoundChanges) : null
            };
            // 半成品快照判定：最后一条且未完成（无 Steps 收尾标记）的助手消息即离开视口时的活消息
            if (own.Messages.LastOrDefault() is { } last
                && (last.Kind == "Typing" || last.Kind == "Agent")
                && string.IsNullOrEmpty(last.Steps))
                own.Messages[own.Messages.Count - 1] = record;
            else
                own.Messages.Add(record);
            own.Time = DateTime.Now;
            SaveSessionHistory();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Error] 后台任务收口写回归属会话失败: {ex.Message}");
        }
    }

    /// <summary>切换项目后进入“未关联会话”的空白待发起态（与点「+ 新会话」同一口径）：**不再**为新项目预建占位“新会话”项，
    /// 避免左侧列表凭空多出一条空会话记录；ClearChatState 已清消息区/摘当前会话/复位视口标志，这里只补提示消息与选择器复位。
    /// 用户在新项目下发出首条消息时，才由 CreateSessionForFirstSend 建会话项并置顶落盘。</summary>
    void BeginNewSessionAfterProjectSwitch()
    {
        EnterBlankNewSession();   // 含：提示消息、恢复普通主持人 prompt、顶部角色/模式选择器回「🤖 自动匹配」、SyncViewportFlags
    }

    string SessionHistoryPath => GAIRR.Core.Paths.SessionHistory;

    void SaveSessionHistory()
    {
        try
        {
            // 历史尚未加载完成前禁止整体重写磁盘，堵住启动阶段空内存覆盖全部历史的窗口
            if (!sessionLoaded) return;
            // 防数据丢失保护：磁盘已有远多于内存的会话时（如外部恢复/合并了备份），
            // 拒绝整体覆盖，只记录告警；正常增量保存（内存≥磁盘）不受影响
            if (System.IO.File.Exists(SessionHistoryPath) && sessionHistory.Count < 5)
            {
                try
                {
                    var onDisk = System.Text.Json.JsonSerializer.Deserialize<List<SessionItem>>(
                        System.IO.File.ReadAllText(SessionHistoryPath));
                    if (onDisk != null && onDisk.Count >= sessionHistory.Count * 3 && onDisk.Count > sessionHistory.Count)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Warn] 保存会话历史被拦截：磁盘 {onDisk.Count} 条 ≥ 内存 {sessionHistory.Count} 条×3，疑似启动未加载完或数据被外部恢复，拒绝覆盖");
                        return;
                    }
                }
                catch { /* 磁盘文件解析失败不拦截，交给原子写覆盖修复 */ }
            }
            // 紧凑序列化：历史可达数十 MB（每条含完整过程卡片），缩进美化会让文件再膨胀约 1/3 且拖慢每次保存；
            // 该 JSON 仅供程序自身读写恢复，不追求人工可读性
            // 落盘前先做毒字符清洗（内存数据若被污染，下次载入/渲染会触发 PtsHost fail-fast 崩溃）
            foreach (var s in sessionHistory) SessionSanitizer.Clean(s);
            // 占位会话（点「+ 新会话」插入、尚未发送首条消息）只在内存里给左侧列表一个视觉落点，永不写盘：
            // 序列化前整体过滤掉，历史文件里因此不会出现空「新会话」记录（发送时 EnsureSessionForSend 转正后自然入库）
            var json = System.Text.Json.JsonSerializer.Serialize(sessionHistory.Where(s => !s.IsPending).Select(s => new
            {
                s.Id, s.Title, s.Project, s.Time, s.Created, s.SortAt,
                IsOrchestration = s.IsOrchestration, PlanId = s.PlanId, NodeId = s.NodeId, OrchPhase = s.OrchPhase,
                PlanMode = s.PlanMode, MultiModels = s.MultiModels,
                AutoRoute = s.AutoRoute,
                Messages = s.Messages
            }).ToList(), new System.Text.Json.JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });   // 中文直存：体积更小且排障可直接 grep 到中文（仍紧凑无缩进）
            // 原子写：先写临时文件再 replace，防止进程崩溃产生半截 JSON 损坏历史
            var tmp = SessionHistoryPath + ".tmp";
            System.IO.File.WriteAllText(tmp, json, System.Text.Encoding.UTF8);
            if (System.IO.File.Exists(SessionHistoryPath))
                System.IO.File.Replace(tmp, SessionHistoryPath, null);
            else
                System.IO.File.Move(tmp, SessionHistoryPath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Error] 保存会话历史失败: {ex.Message}");
        }
    }

    /// <summary>旧数据 AutoRoute 启发式回填（方案 A，载入时执行一次）：AutoRoute 上线前的自动匹配会话无此标记，
    /// 若会话消息快照中跨回合出现 ≥2 个不同 角色|mode（多回合命中过不同角色 = 当时在自动路由），回填 AutoRoute=true，
    /// 升级后切回该会话顶部仍回显"自动匹配"、每回合继续重匹配。误判场景极少（用户曾在会话内手动切过模式 →
    /// 会被当作自动会话，重新手选具体角色即纠正）。新数据已带标记（持久化白名单），直接跳过；
    /// 编排会话不参与角色自动匹配，直接跳过。</summary>
    static void BackfillAutoRoute(SessionItem s)
    {
        if (s.AutoRoute || s.IsOrchestration) return;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in s.Messages)
        {
            if (string.IsNullOrWhiteSpace(m.RunRole) || string.IsNullOrWhiteSpace(m.RunMode)) continue;
            seen.Add(m.RunRole + "|" + m.RunMode);
            if (seen.Count >= 2) { s.AutoRoute = true; return; }
        }
    }

    void LoadSessionHistory()
    {
        try
        {
            sessionHistory.Clear();
            if (!System.IO.File.Exists(SessionHistoryPath)) { sessionLoaded = true; RefreshSessionList(); return; }
            var json = System.IO.File.ReadAllText(SessionHistoryPath);
            var list = System.Text.Json.JsonSerializer.Deserialize<List<SessionItem>>(json);
            if (list == null) { sessionLoaded = true; RefreshSessionList(); return; }
            foreach (var s in list)
            {
                // 旧数据无 Created 字段（反序列化为 MinValue），用 Time 兜底，避免旧会话全部挤到最前
                if (s.Created == default) s.Created = s.Time;
                // 毒字符防线：历史文件曾被工具二进制输出污染（PtsHost fail-fast 崩溃根因），载入即清洗
                SessionSanitizer.Clean(s);
                BackfillAutoRoute(s);   // 旧数据自动匹配会话启发式回填（快照跨回合≥2个不同 角色|mode）
                sessionHistory.Add(s);
            }
            // 历史加载完成后才允许 OnProjectChanged/SaveCurrentSession 落盘，避免启动时空内存覆盖磁盘
            sessionLoaded = true;
        }
        catch (Exception ex)
        {
            // 文件损坏视为已加载（空列表），否则启动后 SaveCurrentSession 会用空内存覆盖坏文件
            sessionLoaded = true;
            System.Diagnostics.Debug.WriteLine($"[Error] 加载会话历史失败: {ex.Message}");
        }
        RefreshPlanSessionDots();   // 启动聚合一次计划会话圆点（需求 7：按子叶终态着色，不随运行态闪变）
        MergeSessionInbox();   // 手机侧续聊收件箱：启动时合并（会话列表渲染前消费，冲突跳过不破坏桌面历史）
        RefreshSessionList();
    }

    // ---- 手机侧续聊收件箱（GAIRR.Server 写 data/session_history.inbox.json：手机任务收口时把该会话副本尾部问答刷入，
    // GUI 下次启动在此合并，实现“GUI 重开可见手机续聊内容”；条目即取即弃，不累积重试）----
    string InboxPath => System.IO.Path.Combine(GAIRR.Core.Paths.DataDir, "session_history.inbox.json");

    /// <summary>启动合并手机侧续聊消息（一次性消费）：条目创建后桌面未再往该会话加消息（GuiBaseCount==当前消息数）才
    /// 顺序追加，防两段历史错位拼接；会话已删/桌面已新增 → 丢弃该条目（消息仍安全保留在 Server 副本，手机上继续可见，
    /// 不破坏 GUI 数据）。有合并 → 原子落盘一次；无论结果如何删除收件箱（条目即取即弃）。</summary>
    void MergeSessionInbox()
    {
        try
        {
            var path = InboxPath;
            if (!System.IO.File.Exists(path)) return;
            List<InboxEntry>? entries = null;
            try
            {
                entries = System.Text.Json.JsonSerializer.Deserialize<List<InboxEntry>>(System.IO.File.ReadAllText(path));
            }
            catch { }
            try { System.IO.File.Delete(path); } catch { }   // 条目一次性消费：合并成功/放弃都即取即弃（Server 副本永久兜底）
            if (entries == null || entries.Count == 0) return;
            var touched = false;
            foreach (var entry in entries)
            {
                var s = sessionHistory.FirstOrDefault(x => x.Id == entry.SessionId);
                if (s == null) continue;                                  // 会话已删除：放弃
                if (s.Messages.Count != entry.GuiBaseCount) continue;     // 桌面侧又新增过：防错位拼接，放弃（手机消息留 Server 副本）
                foreach (var m in entry.Messages)                         // 毒字符防线：手机侧文本同样可能携带脏数据（同落盘边界规则）
                {
                    m.Text = GAIRR.Core.Markdown.Sanitize(m.Text);
                    m.Who = GAIRR.Core.Markdown.Sanitize(m.Who);
                }
                s.Messages.AddRange(entry.Messages);
                s.Time = DateTime.Now;   // 最后活动时间随合并刷新（近似手机侧实际活动时刻）
                touched = true;
            }
            if (touched) SaveSessionHistory();
        }
        catch { }
    }

    /// <summary>每页会话条数：分组下默认展示最近 10 条，点"加载更多"再追加一页。</summary>
    const int SessionPage = 10;

    /// <summary>各会话分组当前已展示条数（key=组名；默认一页，点加载更多逐页累加；切项目时清空回到默认）。
    /// 普通浏览态分组展示 Item 截断到该值，搜索态展示全部命中不截断。</summary>
    readonly Dictionary<string, int> _sessionGroupShown = new();

    /// <summary>重绘会话历史树：过滤"当前项目 + 搜索关键字"后按 ListGroup 分组为"普通会话/计划会话"两个顶层节点，
    /// 组间顺序固定不变：普通会话组恒在上、计划会话组恒在下（末尾 OrderBy 硬编码，不随组内活跃时间跳动）；
    /// 组内按排序键 SortKey 倒序（SortAt 优先、回退创建时间）：被重新激活的老会话一次性跳到组首，
    /// 其余仍按创建时间稳定排列（前置条件见 PromoteSessionOnActivity）。分组是 TreeView 顶层节点 —— 折叠仅隐藏子项、组头永不消失；
    /// 原 ICollectionView.Filter 折叠会把组内项全部过滤导致组头一并消失且无法再展开，此方案根治该问题。
    /// 浏览态每组默认只展示最近一页（SessionPage 条），还有更多时组尾挂 SessionMoreItem 占位（点击再翻页）；
    /// 搜索态不截断 —— 命中项全部展示，保证搜索结果完整可见。</summary>
    void RefreshSessionList()
    {
        var projRoot = cfg.ProjectRoot ?? "";
        bool searching = !string.IsNullOrWhiteSpace(sessionSearchText);
        var groups = sessionHistory
            .Where(s => SessionMatch(s, projRoot))
            .OrderByDescending(s => s.SortKey)
            .GroupBy(s => s.ListGroup)
            .Select(g =>
            {
                var list = g.ToList();   // 本组全部命中项（已倒序，最近在前）
                int total = list.Count;
                // 展示条数：搜索态全量；浏览态取"已翻页数"，未翻页默认一页（SessionPage），不足一页则全显示
                int shown = searching ? total : Math.Min(total,
                    _sessionGroupShown.TryGetValue(g.Key, out var n) ? n : SessionPage);
                var grp = new SessionGroup
                {
                    Name = g.Key,
                    Total = total,
                    IsOpen = !_collapsedSessionGroups.Contains(g.Key),
                    Items = new ObservableCollection<object>(),
                };
                for (int i = 0; i < shown; i++) grp.Items.Add(list[i]);
                // 还有更多 → 组尾挂加载更多占位（搜索态已全量，不挂）
                if (!searching && shown < total)
                    grp.Items.Add(new SessionMoreItem { GroupName = g.Key, Remain = total - shown });
                return grp;
            })
            // 组间顺序固定：普通会话恒在上、计划会话恒在下（不再随组内最近活跃/新建时间"上下跳动"）；
            // LINQ OrderBy 稳定，组内顺序仍保持上方 SortKey 倒序不变。
            .OrderBy(g => g.Name == "普通会话" ? 0 : 1)
            .ToList();
        sessionTree.ItemsSource = groups;
        sessionEmpty.Visibility = groups.Sum(g => g.Total) == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>活跃前置阈值：分组内排在该名次之前（含）的会话发言不改变列表顺序（与默认一页条数同口径）。</summary>
    const int SessionPromoteTop = SessionPage;

    /// <summary>同一会话两次"跳到组首"的最小间隔：防止两个组外会话交替发言时列表来回抖动。</summary>
    static readonly TimeSpan SessionPromoteCooldown = TimeSpan.FromSeconds(60);

    /// <summary>用户发言后的会话前置（活跃会话前置性）：仅当该会话在"当前项目 + 同分组"的展示顺序里已掉出前
    /// SessionPromoteTop 名时，才把排序键 SortAt 刷成当前时间使其一次性跳到组首；前 N 名内发言完全不动列表
    /// （否则每句话都重排、列表一直变来变去，体验差），冷却期内也不重复前置（交替发言不抖）。
    /// rank 按浏览态口径算（不掺搜索关键字：搜索命中集里的名次会误判成"已在前 N 名"而漏前置）；
    /// 分组内计算（跨组算名次时，若组首被计划会话占满，普通会话永远进不了前 N 名，前置逻辑会失效）。
    /// 选中态与关联 id（会话 Id/PlanId/NodeId）都在会话对象内部、与列表顺序无关，重排后由 Selected/IsCurrent
    /// 数据绑定自然跟随，无需额外调整。</summary>
    void PromoteSessionOnActivity(SessionItem? s)
    {
        if (s == null) return;
        var projRoot = cfg.ProjectRoot ?? "";
        var group = sessionHistory
            .Where(x => x.Project == projRoot && x.ListGroup == s.ListGroup)
            .OrderByDescending(x => x.SortKey)
            .ToList();
        var rank = group.IndexOf(s) + 1;   // 0 = 不在当前项目列表内（未落盘/已切项目），不处理
        if (rank <= 0 || rank <= SessionPromoteTop) return;
        if (s.SortAt != default && DateTime.Now - s.SortAt < SessionPromoteCooldown) return;
        s.SortAt = DateTime.Now;
        RefreshSessionList();
        SaveSessionHistory();   // 排序键随历史落盘：重启后前置结果保持
    }

    /// <summary>点击分组下的"加载更多"占位行：把该组已展示条数再追加一页（SessionPage 条）并重绘。</summary>
    void OnSessionMoreClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1) return;
        if ((sender as FrameworkElement)?.DataContext is SessionMoreItem more)
        {
            int cur = _sessionGroupShown.TryGetValue(more.GroupName, out var n) ? n : SessionPage;
            _sessionGroupShown[more.GroupName] = cur + SessionPage;
            RefreshSessionList();
            e.Handled = true;   // 占位行点击由本处消费，阻断向容器/滚动面板的无关冒泡
        }
    }

    /// <summary>会话搜索关键字（来自搜索框输入，空格分隔多个关键字）</summary>
    string sessionSearchText = "";

    /// <summary>已折叠的会话分组名称集合（会话树 TreeViewItem 折叠时记录，RefreshSessionList 重建后据其恢复 IsOpen）。</summary>
    readonly HashSet<string> _collapsedSessionGroups = new();

    /// <summary>会话树分组展开（TreeViewItem.Expanded）：清除该组折叠记录。</summary>
    void OnSessionGroupExpanded(object sender, RoutedEventArgs e)
    {
        if (sender is TreeViewItem tvi && tvi.DataContext is SessionGroup g)
            _collapsedSessionGroups.Remove(g.Name);
    }

    /// <summary>会话树分组折叠（TreeViewItem.Collapsed）：记录折叠状态，后续刷新重建（新会话加入等）组头仍在、状态保持。</summary>
    void OnSessionGroupCollapsed(object sender, RoutedEventArgs e)
    {
        if (sender is TreeViewItem tvi && tvi.DataContext is SessionGroup g)
            _collapsedSessionGroups.Add(g.Name);
    }

    /// <summary>分组行单击：切换会话分组折叠/展开（无左侧箭头，整行可点）。
    /// 模板 Bd 上的 MouseLeftButtonUp 冒泡命中；叶子行点击由 OnSessionClick 处理并标记 Handled 不至此，此处按类型过滤双保险。</summary>
    void OnSessionGroupHeaderClick(object sender, MouseButtonEventArgs e)
    {
        // 双击由两次单击合成，仅首击切换一次，避免展开后立刻收回
        if (e.ClickCount != 1) return;
        if ((sender as FrameworkElement)?.TemplatedParent is TreeViewItem tvi &&
            tvi.DataContext is SessionGroup)
        {
            tvi.IsExpanded = !tvi.IsExpanded;
        }
    }

    /// <summary>会话是否通过"当前项目 + 搜索关键字"双重过滤；搜索时匹配标题或消息内容（不区分大小写）。
    /// 支持空格分隔的多关键字，所有关键字都在该会话中出现过才显示该会话。
    /// 注意：分组折叠不在此过滤，折叠由 TreeViewItem.IsExpanded 控制（组头作为树节点保留于列表）。</summary>
    bool SessionMatch(SessionItem s, string projRoot)
    {
        if (s.Project != projRoot) return false;
        if (string.IsNullOrWhiteSpace(sessionSearchText)) return true;
        var keywords = sessionSearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (keywords.Length == 0) return true;
        return keywords.All(kw => SessionContainsKeyword(s, kw));
    }

    /// <summary>判断单个会话中是否包含指定关键字（标题或任意消息内容，不区分大小写）</summary>
    bool SessionContainsKeyword(SessionItem s, string kw)
    {
        if (s.Title != null && s.Title.Contains(kw, StringComparison.OrdinalIgnoreCase)) return true;
        return s.Messages.Any(m =>
            (m.Text != null && m.Text.Contains(kw, StringComparison.OrdinalIgnoreCase)) ||
            (m.Cmd != null && m.Cmd.Contains(kw, StringComparison.OrdinalIgnoreCase)) ||
            (m.Steps != null && m.Steps.Contains(kw, StringComparison.OrdinalIgnoreCase)) ||
            (m.ProcessItems != null && m.ProcessItems.Any(p =>
                (p.Title != null && p.Title.Contains(kw, StringComparison.OrdinalIgnoreCase)) ||
                (p.Content != null && p.Content.Contains(kw, StringComparison.OrdinalIgnoreCase)))));
    }

    /// <summary>搜索框按键：回车提交查询（输入时不随动刷新），Esc 清空关键字并恢复完整列表</summary>
    void OnSessionSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            sessionSearchText = sessionSearchBox?.Text ?? "";
            RefreshSessionList();
        }
        else if (e.Key == Key.Escape)
        {
            sessionSearchBox.Clear();
            sessionSearchText = "";
            RefreshSessionList();
        }
    }

    /// <summary>搜索框文本变化：内容非空显示 ✕ 清除图标，空则隐藏</summary>
    void OnSessionSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sessionSearchClear == null) return;
        sessionSearchClear.Visibility = string.IsNullOrEmpty(sessionSearchBox?.Text)
            ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>点击 ✕ 清除图标：清空关键字并恢复完整列表（同 Esc 行为）</summary>
    void OnSessionSearchClear(object sender, MouseButtonEventArgs e)
    {
        sessionSearchBox.Clear();
        sessionSearchText = "";
        RefreshSessionList();
    }

    /// <summary>判断当前是否有任务/编排正在运行（cts=主对话任务，multiGenCts=多模型决策生成，planRunner=编排执行；
    /// 均在发起时创建、结束/停止后置空）。仅作任务存在性判断：切换/新建会话已在两会话并行下放行（并发占位由
    /// Agent 层厂商并发闸把关）；本标志仍用于运行中代码编辑卡片只读等仍需与运行态串行的场景。</summary>
    bool IsTaskRunning() => cts != null || multiGenCts != null || planRunner != null;

    /// <summary>新建编排会话入口（阶段 3）：选生成方案（interactive 交互式卡片问答 / multi 多模型决策）→ 创建会话。</summary>
    void OnNewOrchestration(object sender, RoutedEventArgs e)
    {
        // 会话切换锁：SSE 流未绑定（发送后→首个回复 token 到达前）新建计划会话等同切换会话（串台）→ 拦截并提示
        if (!SwitchEnabled)
        {
            mStatus.Text = SwitchLockTip ?? "AI 正在响应，首个回复生成前不能切换会话";
            return;
        }
        // 两会话并行 + 厂商并发闸：本会话前台主任务（cts）运行中同样允许新建编排会话——切走后原任务照跑
        // （验收7 后台收口直写其会话记录）；新编排会话的交互收集/产树各自建 Runner 并行，多会话占位由
        // Agent 层 LlmConcurrencyGate 按厂商并发上限把关（占满时新任务启动即被拒并提示），口径同 OnNewSession/OnSessionClick
        if (cfg.ProjectRoot.Length == 0)
        {
            MessageBox.Show(this, "请先选择项目再创建编排会话", "新建编排会话");
            return;
        }
        var dlg = new OrchSessionInputDialog("", "", KeyedModels()) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        NewOrchestrationSession(dlg.ResultTitle, dlg.ResultGoal, null, dlg.ResultMode, dlg.ResultModels, dlg.ResultMaterials);
    }

    /* ================= 编排会话（阶段 3：Collecting→Generating→Done 状态机 + 对话式收集） ================= */

    /// <summary>创建编排会话：加入会话历史 → 依生成方案分支。
    /// interactive：切主持人 prompt，发首条注入项目上下文 + L0 目标，主持人卡片问答逐项收集；
    /// multi：素材（目标+参考资料）入会话，点发送区「🎯 生成方案」后台多模型并行决策。
    /// archRef 为架构树右键传入的节点路径（8.3 决策 1 锚定；无则普通入口传 null）。</summary>
    void NewOrchestrationSession(string title, string goal, string? archRef,
        string planMode = "interactive", List<string>? multiModels = null, string materials = "")
    {
        var session = new SessionItem
        {
            Title = string.IsNullOrWhiteSpace(title) ? "编排会话" : title,
            Project = cfg.ProjectRoot,
            Time = DateTime.Now,
            Created = DateTime.Now,
            IsOrchestration = true,
            OrchPhase = "collecting",
            PlanId = null,
            NodeId = archRef,
            PlanMode = planMode,
            MultiModels = multiModels ?? new(),
            Messages = new(),
        };
        sessionHistory.Insert(0, session);
        SaveSessionHistory();
        RefreshSessionList();
        OpenSession(session);

        if (session.Mode == "multi")
        {
            // 多模型决策：目标+参考资料作为素材进入会话（生成时拼全部用户消息）；引导用户在输入框补充或直接点“生成方案”
            var material = "【L0 目标】" + goal;
            if (!string.IsNullOrWhiteSpace(materials)) material += "\n【参考资料】" + materials;
            AddMessage(new ChatMessage { Kind = MsgKind.User, Who = "你（刚才）", Text = material });
            AddMessage(new ChatMessage
            {
                Kind = MsgKind.Agent,
                Who = "多模型决策",
                Text = $"已按「多模型决策」方案创建编排会话，参与模型：{string.Join("、", session.MultiModels.Select(m => m[(m.IndexOf(':') + 1)..]))}。\n" +
                       "可继续在输入框补充目标/参考资料（发送即追加素材），然后点发送区「🎯 生成方案」启动并行决策。",
            });
            SaveCurrentSession();
            UpdateOrcPanel();
            return;
        }
        // interactive：首条注入项目上下文 + L0 目标 → 主持人开场（主动发起收集问答）
        var prompt = GAIRR.AgentHost.OrchestrationSession.InitialPrompt(
            cfg.ProjectRoot, session.Title, goal, archRef ?? "");
        lastTask = prompt;   // 记录任务原文（历史/钉钉用）
        SendInitialPrompt(prompt);
    }

    /// <summary>编排会话首轮发送（与 OnSend 同链路：锁定发送、建 cts、RunAsync；不套 Flow 走自主收集）。</summary>
    void SendInitialPrompt(string prompt)
    {
        try
        {
            // 发送前并发预检：与 OnSend 同链路，并发满即时提示、不启动（编排首轮未上屏；会话保留 collecting，
            // 待正在进行的任务结束后在输入框发送任意消息即可继续需求收集）
            if (ProviderFullBeforeSend(RunnerOf(currentSession)))
            {
                AddMessage(new ChatMessage { Kind = MsgKind.Agent, Who = "GAIRR",
                    Text = "已创建编排会话，但当前模型厂商并发已达上限，主持人开场暂未启动。待正在进行的任务结束后，在输入框发送任意消息即可继续需求收集。" });
                SaveCurrentSession();
                return;
            }
            sendBtn.Visibility = Visibility.Collapsed;
            sendMenuBtn.Visibility = Visibility.Collapsed;   // 编排首轮运行中同步隐藏下拉箭头
            stopBtn.Visibility = Visibility.Visible;
            btnOrchGen.IsEnabled = false;   // 编排首轮开场回复中禁用生成钮（回复结束由 UnlockSend 恢复）
            AddMessage(new ChatMessage { Kind = MsgKind.User, Who = "你（刚才）", Text = prompt });
            ScrollToBottom();
            // P：编排首轮同样先钉住本会话 角色/模式/模型（取样顶部选择器）
            EnsureSessionPin(currentSession);
            // 两会话并行：编排首轮同样收拢到本会话 TaskRecord 并建本会话 Runner（历史按会话装载，cts/workMsg 等代理随记归位）
            EnsureRunner(currentSession);
            cts = new CancellationTokenSource();
            MarkRunStart(currentSession);   // 编排首次自动产树等同一次主任务：会话置进行中
            EnsureWorkMsg();
            workMsg!.IsRunningTool = false;
            workMsg.IsWaitingForModel = true;
            currentTodo = null;
            stepCardMap.Clear();
            taskActive = true;
            RefreshTodoBar();
            UpdateChatTopInfo();
            taskStart = DateTime.Now;
            workMsg.Steps = "[开始] 编排需求收集 · 第 1 轮 · 模型思考中…";
            workMsg.Refresh();
            var rnr = CurRec?.Runner;   // EnsureRunner 已在方法开头建好本会话 Runner 并挂记录
            if (rnr == null) { UnlockSend(); return; }
            rnr.Loop.Flow = null;
            // 同步会话标识给 AgentLoop：编排会话同样按会话 Id 落盘请求上下文快照
            rnr.Loop.SetSessionKey(currentSession?.Id ?? "");
            _ = Task.Run(async () => await rnr.Loop.RunAsync(ApplySlashSkill(prompt), cts.Token));
        }
        catch (Exception ex)
        {
            AddMessage(new ChatMessage { Kind = MsgKind.Agent, Who = "GAIRR", Text = "发送失败：" + ex.Message });
            UnlockSend();
        }
    }

    /// <summary>发送按钮右侧下拉箭头点击：弹出“打开新会话/转为编排会话”菜单（编排会话自身不显示该入口）。
    /// 空白待发起态（点过「+ 新会话」尚未发送，currentSession==null）同样放行菜单：“打开新会话”等价于直接发送
    /// （会话项由 OnSend 发送首条消息时补建），“转为编排会话”由 ConvertCurrentSessionToOrchestration 给出提示。</summary>
    void OnSendMenuClick(object sender, RoutedEventArgs e)
    {
        if (currentSession?.IsOrchestration == true) return;
        if (sendMenuBtn.ContextMenu is not ContextMenu cm) return;
        cm.PlacementTarget = sendMenuBtn;
        cm.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        cm.IsOpen = true;
    }

    /// <summary>发送菜单「发送完整上下文」点击：置强制标志后复用 OnSend 全链路发送（空输入自动补「继续」），
    /// 使本次强制网页模型开新会话并整段重投完整上下文；非网页模型等同普通发送。编排会话无此项（菜单整体隐藏）。</summary>
    void OnSendFullContextClick(object sender, RoutedEventArgs e)
    {
        if (sendMenuBtn.ContextMenu is ContextMenu cm) cm.IsOpen = false;
        _pendingForceWebFullContext = true;
        OnSend(sendBtn, new RoutedEventArgs());
    }

    /// <summary>菜单“打开新会话”点击：先保存当前会话并把会话区复位成空白待发起态（**不建**列表占位项、输入框内容不动），
    /// 再走 OnSend 全链路——会话项由 OnSend 在发送首条消息时补建，随后把当前输入框内容作为该会话首条消息发出去。</summary>
    void OnOpenNewSessionClick(object sender, RoutedEventArgs e)
    {
        if (sendMenuBtn.ContextMenu is ContextMenu cm) cm.IsOpen = false;
        // 会话切换锁：SSE 流未绑定（发送后→首个回复 token 到达前）「打开新会话」等同切换（串台）→ 拦截并提示
        if (!SwitchEnabled)
        {
            mStatus.Text = SwitchLockTip ?? "AI 正在响应，首个回复生成前不能切换会话";
            return;
        }
        var text = inputBox.Text.Trim();
        if (text.Length == 0)
        {
            MessageBox.Show(this, "输入框为空，请先输入要发送的内容，再选择“打开新会话”。", "打开新会话");
            return;
        }
        OnNewSession(null!, null!);   // 保存旧会话 + 复位会话区为空白待发起态；handler 不使用 sender/e
        OnSend(null!, null!);         // 会话项在此发送时补建；发送成功后由 OnSend 清空输入框
    }

    /// <summary>菜单“转为编排会话”点击：收起菜单并把当前普通会话就地转为编排会话。</summary>
    void OnConvertToOrchClick(object sender, RoutedEventArgs e)
    {
        if (sendMenuBtn.ContextMenu is ContextMenu cm) cm.IsOpen = false;
        ConvertCurrentSessionToOrchestration();
    }

    /// <summary>普通会话就地转为编排会话（讨论好方案后一键转编排，无弹窗）。
    /// ① 标题沿用当前标题、消息与讨论结论全部保留（Runner 上下文无缝承接）、生成方案固定单模型 interactive；
    /// ② IsOrchestration=true 使该会话在列表中移入“计划会话”分组并持久化；
    /// ③ 复用打开链路（OpenSession）同步主持人 prompt（plan-orchestration.md）与右栏编排面板；
    /// ④ 注入 L0 目标后走 SendInitialPrompt（与“新建编排会话”同一入口）启动主持人收集流程。</summary>
    void ConvertCurrentSessionToOrchestration()
    {
        var s = currentSession;
        if (s == null)
        {
            // 空白待发起态（点过“新会话”尚未发送）：没有可承接的讨论内容，提示即可——
            // 不为此凭空建会话项（那正是“左侧列表多出空新会话”的成因）
            MessageBox.Show(this, "当前会话还没有内容，先在会话中讨论好方案，再一键转为编排会话。", "转为编排会话");
            return;
        }
        if (s.IsOrchestration) return;
        if (cts != null)
        {
            MessageBox.Show(this, "当前会话正在运行任务，请先停止后再转为编排会话。", "转为编排会话");
            return;
        }
        if (cfg.ProjectRoot.Length == 0)
        {
            MessageBox.Show(this, "请先选择项目，再转为编排会话。", "转为编排会话");
            return;
        }
        if (s.Messages.Count == 0)
        {
            MessageBox.Show(this, "当前会话还没有内容，先在会话中讨论好方案，再一键转为编排会话。", "转为编排会话");
            return;
        }

        // ① 就地标记编排会话：标题沿用；内容保留既有讨论消息；单模型对话式收集
        s.IsOrchestration = true;
        s.OrchPhase = "collecting";
        s.PlanMode = "interactive";
        s.PlanId = null;
        s.NodeId = null;
        s.MultiModels.Clear();
        SaveSessionHistory();   // 落盘编排标记（重启后仍归“计划会话”分组）
        RefreshSessionList();   // 会话列表移入“计划会话”分组

        // ② 与“新建编排会话”同一打开链路：同步主持人 prompt（plan-orchestration.md）+ 右栏编排面板；
        //    若该普通会话曾运行过（Runner 已建）此处一并切到 collecting，未建则由首轮 EnsureRunner 自动装配
        OpenSession(s);
        var runner = RunnerOf(s);
        if (runner != null)
            runner.Loop.SwitchPrompt(GAIRR.AgentHost.OrchestrationSession.PromptFileName);

        // ③ 直接调起编排会话创建首轮：L0 目标承接上方已讨论结论，主持人开始收集/细化（正常编排流程）
        var goal = "承接本会话上方已讨论的方案结论：信息已齐备则直接进入细化与任务树产出，无需重复需求澄清；如有缺口请针对性补充提问。";
        var prompt = GAIRR.AgentHost.OrchestrationSession.InitialPrompt(cfg.ProjectRoot, s.Title, goal, "");
        lastTask = prompt;
        SendInitialPrompt(prompt);
    }

    // ───── 编排：生成按钮 + 多模型后台决策 + 问题卡片作答（interactive/multi 两条路径共用） ─────

    /// <summary>会话级澄清进度提示刷新：统计当前会话全部消息中未提交卡（还差几题/全部完成可生成任务树），
    /// 在编排收集阶段显示于输入区下方提示条；卡作答变化经 CardsChanged 即时重算，消息离开列表自动反订阅自愈。</summary>
    void RefreshOrcQProgress()
    {
        // 反订阅已不在消息列表中的卡消息（会话切换/清空后自动收敛，避免旧消息滞留）
        if (qSubMsgs.Count > 0)
        {
            List<ChatMessage>? gone = null;
            foreach (var m in qSubMsgs)
                if (!messages.Contains(m)) (gone ??= new List<ChatMessage>()).Add(m);
            if (gone != null)
                foreach (var m in gone)
                {
                    m.CardsChanged -= OnMsgCardsChanged;
                    qSubMsgs.Remove(m);
                }
        }
        if (orcQHint == null) return;   // InitializeComponent 完成前被早期调用兜底
        // “主持人提问待答/继续问答”提示仅在当前会话自身处于收集阶段时展示（按会话 OrchPhase 判定，不用全局
        // loop.OrchMode：两会话并行下后台决策会话遗留的 generating 态曾让当前收集会话的提示被误隐藏）
        if (currentSession?.IsOrchestration != true || currentSession.OrchPhase != "collecting")
        {
            orcQHint.Visibility = Visibility.Collapsed;
            return;
        }
        int pending = 0, left = 0;
        bool any = false;
        foreach (var m in messages)
        {
            if (m.QuestionCards.Count == 0) continue;
            any = true;
            if (qSubMsgs.Add(m)) m.CardsChanged += OnMsgCardsChanged;   // 惰性订阅：本消息内卡作答变化即时上抛
            foreach (var c in m.QuestionCards)
            {
                if (c.Answered) continue;   // 已提交折叠的卡不再计入待办
                pending++;
                if (!c.HasAnswer) left++;
            }
        }
        if (!any) { orcQHint.Visibility = Visibility.Collapsed; return; }
        if (left > 0)
        {
            // 还有未作答：琥珀提示差几题
            orcQHint.Text = $"澄清进度：还有 {left} 题未作答（共 {pending} 张卡待提交），全部作答完成后即可生成任务树";
            orcQHint.Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0xA1, 0x3E));
        }
        else if (pending > 0)
        {
            // 已作答未提交：提醒点提交
            orcQHint.Text = $"澄清进度：{pending} 张卡均已作答，请点击卡下「✓ 提交答案」提交";
            orcQHint.Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xB9, 0x55));
        }
        else
        {
            // 全部已提交且主持人仍在收集：提示可手动收口生成
            orcQHint.Text = "澄清问题已全部作答提交 —— 请等待分析结果，之后点击「🎯 生成任务树」生成方案";
            orcQHint.Foreground = new SolidColorBrush(Color.FromRgb(0x3D, 0xDC, 0x97));
        }
        orcQHint.Visibility = Visibility.Visible;
    }

    void OnMsgCardsChanged() => RefreshOrcQProgress();

    /// <summary>刷新发送区按钮（发送/停止/下拉/生成）可见性。生成按钮恒居输入区最右（XAML DockPanel 首槽），
    /// 左邻为 发送↔停止 同位互斥钮。
    /// - 交互式（interactive）编排收集期：空闲显示「发送」+「🎯 生成任务树」（发送在左、生成在右）；
    ///   发送后（等待模型回复中）发送钮原位变「停止」，右侧生成钮保留并禁用（防重复收口无效点击）——交流与随时收口并存。
    /// - multi 编排收集期：发送钮隐藏（发送=自动追加素材并启动决策，已由「🎯 生成方案」接管），仅显示生成钮。
    /// - 产树中（generating）/方案执行（planRunner）/多模型决策生成（multiGen）/非编排会话：隐藏生成钮。
    /// 忙闲状态按当前会话视角收口：busy → 显示停止钮、隐藏发送钮与下拉箭头（两会话并行互不残留忙碌态）。</summary>
    void RefreshOrchGenBtn()
    {
        RefreshOrcQProgress();   // 澄清进度提示与按钮状态同面刷新（会话/阶段切换兜底）
        var s = currentSession;
        var isOrch = s?.IsOrchestration == true;
        var interactive = isOrch && s.Mode != "multi";
        var collecting = isOrch && s.OrchPhase == "collecting";
        var busy = cts != null || planRunner != null || multiGenCts != null;
        // 生成钮显示：编排收集期保留右侧生成钮（interactive 交流与等待回复中均显；multi 收集素材期亦显）。
        // 一旦进入产树（OrchPhase=generating）、编排执行（planRunner）或决策生成（multiGenCts）即隐藏。
        var showGen = collecting && planRunner == null && multiGenCts == null;
        if (showGen)
            btnOrchGen.Content = s!.Mode == "multi" ? "🎯 生成方案" : "🎯 生成任务树";
        btnOrchGen.Visibility = showGen ? Visibility.Visible : Visibility.Collapsed;
        // 生成钮可用性：interactive 等待本轮模型回复中禁用（样式灰显），回复结束由 UnlockSend 恢复可点
        btnOrchGen.IsEnabled = !(interactive && collecting && cts != null);
        // 发送/停止/下拉三钮按当前会话视角互斥收口（自洽：无论此前状态，本句把发送区恢复正确）：
        // - busy → 隐藏发送钮与下拉、显示停止钮（停止接管同位左钮）
        // - multi 编排收集期空闲 → 发送钮让位（隐藏），仅右侧「🎯 生成方案」
        // - 其余空闲 → 恢复发送钮；下拉箭头仅普通会话显示（编排态一并隐藏）
        // 停止钮归属 owner 会话见 OnStop：其经 CurRec 代理只作用当前会话自己的执行器/任务，不误伤他会话。
        if (busy) { sendBtn.Visibility = Visibility.Collapsed; sendMenuBtn.Visibility = Visibility.Collapsed; stopBtn.Visibility = Visibility.Visible; }
        else if (isOrch && collecting && !interactive) { sendBtn.Visibility = Visibility.Collapsed; sendMenuBtn.Visibility = Visibility.Collapsed; stopBtn.Visibility = Visibility.Collapsed; }
        else { sendBtn.Visibility = Visibility.Visible; sendMenuBtn.Visibility = isOrch ? Visibility.Collapsed : Visibility.Visible; stopBtn.Visibility = Visibility.Collapsed; }
        RefreshSendBtnState();   // 可见性收口后跟随刷新「发送/继续」文案与置灰（UnlockSend/SetSendBusy 均经此方法恢复发送钮）
    }

    /// <summary>发送区“生成”按钮点击：interactive=把收敛指令注入输入框走 OnSend 全链路（含 Generating 切换/锁发送）；
    /// multi=直接启动多模型后台决策（不再过主持人问答）。</summary>
    void OnOrchGenClick(object sender, RoutedEventArgs e)
    {
        var s = currentSession;
        if (s?.IsOrchestration != true) return;
        if (s.Mode == "multi") { StartMultiPlanGen(); return; }
        inputBox.Text = "生成任务树";
        OnSend(sendBtn, new RoutedEventArgs());
    }

    /// <summary>多模型决策生成：拼接目标+素材（会话内全部用户消息）→ 后台 PlanNegotiator.NegotiateAsync 并行产候选 + judge 融合；
    /// 完成后弹共用确认对话框；取消/失败回 collecting 供补充素材后重跑。运行期锁输入（OnSend 拦截），停止钮可取消。</summary>
    void StartMultiPlanGen()
    {
        var s = currentSession;
        if (s?.IsOrchestration != true || s.Mode != "multi") return;
        if (multiGenCts != null) return;   // 已在生成（OnSend/按钮已拦截，双保险）
        // 两会话并行下另一会话仍有后台多模型决策在跑时禁止再起一个：全局 loop.OrchMode 在切换会话时已被
        // 复位成当前会话阶段，反映不了“其它会话是否在生成”——改按各会话任务记录的 MultiGenCts 判定（按会话区分）
        if (runTasks.Any(kv => kv.Key != s.Id && kv.Value.MultiGenCts != null))
        {
            MessageBox.Show(this, "另一编排会话的方案生成仍在进行。请等其收口（或切回该会话点击停止）后再发起本轮生成。", "多模型决策");
            return;
        }
        if (s.MultiModels == null || s.MultiModels.Count == 0)
        {
            MessageBox.Show(this, "尚未选择参与决策的模型。请新建编排会话时勾选模型，或在 config.ini [Planning] Models 中配置。", "多模型决策");
            return;
        }
        // 目标+素材 = 会话内全部用户消息正文（L0 目标与历次补充的参考资料/约束）
        var goal = string.Join("\n", s.Messages
            .Where(m => m.Kind == "User" && !string.IsNullOrWhiteSpace(m.Text))
            .Select(m => m.Text!.Trim()));
        if (goal.Length == 0)
        {
            MessageBox.Show(this, "请先在输入框给出 L0 目标与参考资料（素材）再生成方案。", "多模型决策");
            return;
        }
        var models = GAIRR.AgentHost.PlanNegotiator.ParseModels(string.Join(",", s.MultiModels));
        if (models.Count == 0)
        {
            MessageBox.Show(this, "勾选的模型列表格式无效（应为 provider:modelId）。", "多模型决策");
            return;
        }
        if (!EnsureNoCodeEditingBeforeRun()) return;   // 任务启动锁：代码页编辑未保存 → 拦截（同主任务/计划执行收口）
        s.OrchPhase = "generating";
        SyncOrch(s, "generating");
        SaveCurrentSession();
        UpdateOrcPanel();   // 刷新阶段徽标；收集期按钮随之隐藏
        multiGenCts = new CancellationTokenSource();
        MarkRunStart(s);   // 多模型后台决策：会话先置进行中（后台线程跑，行内可见）
        SetSendBusy(true);
        AddMessage(new ChatMessage
        {
            Kind = MsgKind.Agent,
            Who = "多模型决策",
            Text = $"已启动 {models.Count} 个模型的并行调研与方案决策（只读调研 → 候选 → 评估融合），完成后将弹出候选确认窗口…",
        });
        var ctsRef = multiGenCts;
        var session = s;
        _ = Task.Run(async () =>
        {
            GAIRR.AgentHost.PlanDto? plan = null;
            var cancelled = false;
            var err = "";
            try
            {
                plan = await GAIRR.AgentHost.PlanNegotiator.NegotiateAsync(
                    cfg.ProjectRoot, session.Title ?? "编排任务", goal, true,
                    line => Dispatcher.BeginInvoke(() =>
                    {
                        // 决策进度行仅实时显示不写历史：两会话并行下生成期间前台可能已切到别的会话，
                        // 不把进度串入其它会话聊天（口径同 planRunner 收口守卫）；切回本会话后照常上屏
                        if (currentSession != session) return;
                        AddMessage(new ChatMessage
                        {
                            Kind = MsgKind.Cmd,
                            Who = "多模型决策",
                            Text = line,
                            NoPersist = true,   // 进度行仅实时显示，不写历史
                        });
                    }),
                    ctsRef.Token, models);
            }
            catch (Exception ex)
            {
                cancelled = ctsRef.IsCancellationRequested;
                if (!cancelled) err = ex.Message;
            }
            var result = plan;
            Dispatcher.Invoke(() => OnMultiPlanDone(session, result, cancelled, err));
        });
    }

    /// <summary>多模型决策后台线程收口（UI 线程执行）：解锁输入区；失败/取消回 collecting 供重跑；成功后弹共用确认对话框（不自动执行）。</summary>
    void OnMultiPlanDone(SessionItem s, GAIRR.AgentHost.PlanDto? plan, bool cancelled, string err)
    {
        var mrec = EnsureRec(s);   // 多模型决策按发起会话 s 直写落账（两会话并行：生成期间允许切到别的会话，收口时刻前台可能已切走，不依赖 currentSession 代理）
        if (mrec.MultiGenCts != null) { mrec.MultiGenCts.Dispose(); mrec.MultiGenCts = null; }
        // 会话状态收口：后台多模型决策结束。成功生成→完成(绿，待人工确认)；失败/取消→中断(红)
        MarkRunEnd(s, plan != null);
        if (plan == null)
        {
            // 失败/取消：回收集阶段，可补充素材后重新生成
            s.OrchPhase = "collecting";
            SyncOrch(s, "collecting");   // Runner 按会话隔离：仅同步发起会话自己的 Runner（无 Runner 时跳过，首建时对齐）
            if (s == currentSession)
            {
                SaveCurrentSession();
                UpdateOrcPanel();
                AddMessage(new ChatMessage
                {
                    Kind = MsgKind.Agent,
                    Who = "多模型决策",
                    Text = cancelled
                        ? "已取消本轮决策生成，可补充素材后点「🎯 生成方案」重新生成。"
                        : $"方案生成失败：{(err.Length > 0 ? err : "所有候选模型均未能产出有效方案")}\n请检查参与模型的配置后重新生成。",
                });
            }
            else
                ShowToast(cancelled ? $"[编排]「{s.Title}」本轮决策已取消（可切回补充素材后重新生成）。"
                                     : $"[编排]「{s.Title}」方案生成失败（可切回查看原因后重新生成）。");
        }
        else
        {
            // 成功 → 弹候选/融合确认框（共用收口：approved 落盘，等待手工执行）
            if (s == currentSession)
            {
                ConfirmOrchestrationPlan(plan);
            }
            else
            {
                // 后台成功收口（生成期间前台已切到别的会话，与 planRunner 收口同范式）：状态直写发起会话 s——
                // SessionItem 内存态权威，下次 s 成为当前会话时随切换保存落盘；框架 generating 态复位；
                // 确认框挂起：候选方案暂存 s 的任务记录，用户切回该会话时由 OpenSession 补弹确认
                s.PlanId = plan.Id;
                s.OrchPhase = "done";
                // 收口时刻前台在别的会话：全局 loop 已同步成该会话的阶段（可能正在生成/收集），此处不再复位它
                // （防止清掉前台会话自己的 generating 态）。s 阶段落盘为 done，用户切回 s 时 OpenSession 随会话
                // 阶段重新同步 loop 为 done 并补弹确认框（PendingPlan 消费）。
                EnsureRec(s).PendingPlan = plan;
                ShowToast($"[编排]「{s.Title}」多模型方案已生成，切回该会话后弹窗确认。");
            }
        }
        SetSendBusy(false);   // 恢复发送按钮（含会话被切走的防御路径）
    }

    /// <summary>编排方案确认收口（交互式与多模型两条生成路径共用）：阶段置 done → 弹 PlanConfirmDialog；
    /// 确认 → approved 落盘并跳转编排执行页（不自动执行，用户在计划节点右键「开始执行」手工触发）；取消 → 回 collecting 继续调整。</summary>
    void ConfirmOrchestrationPlan(GAIRR.AgentHost.PlanDto plan)
    {
        var s = currentSession;
        if (s?.IsOrchestration != true) return;
        s.PlanId = plan.Id;
        s.OrchPhase = "done";
        SyncOrch(s, "done");   // 生成成功进入待确认，框架阶段同步（仅本会话 Runner）
        SaveCurrentSession();
        UpdateOrcPanel();

        // 方案确认按归属项目根（验收 8）：编排会话 Project 即发起时归属根，防会话在跨项目上下文中确认时串写
        var ownerRoot = TaskOwnerProjectRoot(s);
        var confirm = new PlanConfirmDialog(plan, ownerRoot, s.Id) { Owner = this };
        // 手机端联动（控制流外尽力而为）：弹框前推送挂起帧（手机端 SSE 可见待决策），失败不阻塞弹框
        liveMirror?.NotifyPlanPending(s.Id, plan);
        if (confirm.ShowDialog() == true)
        {
            // 关联回写：叶子=直写 planId；根/分支=自动在其下新增以编排标题命名的功能子叶并关联（AttachPlan 内落地）；
            // 新增成功后在架构树中展开定位到该子叶，让“新功能已落到分支下”即时可见
 string? attachInfo = null;
 string? revealPath = null;
 // ① 会话关联节点缺失（未取到关联 id 或中途丢失）时，按计划来源路径 archRef 回退反查并补回，不再静默跳过
 if (string.IsNullOrWhiteSpace(s.NodeId) && !string.IsNullOrWhiteSpace(plan.ArchRef))
 s.NodeId = plan.ArchRef;
 if (!string.IsNullOrWhiteSpace(s.NodeId))
 {
 var r = ArchTreeStore.AttachPlan(ownerRoot, s.NodeId, plan.Id, s.Title);
                attachInfo = r switch
                {
                    2 => $"功能架构：已在“{s.NodeId}”下新增功能子叶「{s.Title}」并关联本编排。",
                    1 => $"功能架构：已关联功能节点“{s.NodeId}”。",
                    _ => $"功能架构：节点“{s.NodeId}”关联写回失败（可右键「更新架构树」后重试）。",
                };
                if (r == 2) revealPath = s.NodeId + "/" + s.Title.Trim();
            }
 if (attachInfo == null) attachInfo = "功能架构：本编排未关联任何功能节点（会话关联节点缺失且计划无来源路径），架构树不会随之联动刷新。";
 selPlanId = plan.Id;
 LoadArchTree(ownerRoot); // ② 刷新按会话归属项目根，与回写同口径
            if (revealPath != null)
                Dispatcher.BeginInvoke(new Action(() => RevealArchPath(revealPath)), System.Windows.Threading.DispatcherPriority.Loaded);
            // 8.3 修订：确认后不自动执行，由用户在「编排执行」页计划节点右键「开始执行」手工触发
            AddMessage(new ChatMessage
            {
                Kind = MsgKind.Agent,
                Who = "GAIRR",
                Text = "✅ 编排方案已确认并落盘（未自动执行）。\n" + (attachInfo == null ? "" : attachInfo + "\n")
                     + "在右侧「编排执行」页的计划节点上右键 →「开始执行」即可手工启动。",
            });
            UpdateOrcPanel();   // hasPlan=true：自动切到编排执行 Tab，执行树可见
        }
        else
        {
            s.OrchPhase = "collecting";   // 用户取消：回到收集阶段继续调整
            SyncOrch(s, "collecting");   // 框架阶段同步回收集（仅本会话 Runner）
            SaveCurrentSession();
            UpdateOrcPanel();
            var hint = s.Mode == "multi"
                ? "可继续补充素材后点发送区「🎯 生成方案」重新生成。"
                : "可继续对话调整需求后重新发送\"生成任务树\"。";
            AddMessage(new ChatMessage
            {
                Kind = MsgKind.Agent,
                Who = "GAIRR",
                Text = "已取消确认，" + hint,
            });
        }
        // 手机端联动：本地确认/取消收口统一通知 Server 清 pending + 推 PlanResolved（手机端关闭确认 UI）
        liveMirror?.NotifyPlanResolved(s.Id, plan.Id, confirm.DialogResult == true);
    }

    // ───── 问题卡片作答（平台级：编排 + 普通会话通用；模型回复 question-card → 点选/打勾 → 答案作为用户消息走 OnSend 全链路） ─────

    /// <summary>协议解析出的题目卡片 DTO → 聊天区卡片 VM（选项附推荐标记，单选/多选依 Multi 定形）。</summary>
    QuestionCardVm MakeQCardVm(GAIRR.AgentHost.OrchestrationSession.OrchQuestionDto dto)
    {
        var card = new QuestionCardVm
        {
            Question = dto.Question,
            Multi = dto.Multi,
        };
        foreach (var o in dto.Options)
            card.Options.Add(new QuestionOptionVm { Owner = card, Text = o.Text, Rec = o.Rec });
        return card;
    }

    /// <summary>卡片选项勾选/取消（行模板 Checked/Unchecked 驱动）：同步到卡片 VM 逻辑状态（单选互斥由 SyncSelect 双序收敛）。</summary>
    void OnQOptionToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Primitives.ToggleButton tb) return;
        if (tb.DataContext is QuestionOptionVm opt && opt.Owner != null)
            opt.Owner.SyncSelect(opt, tb.IsChecked == true);
    }

    /// <summary>「✏️ 补充说明」点击：展开/收起手打输入框（选项不足以表达时用）。</summary>
    void OnCardExtraClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is QuestionCardVm card)
            card.ExtraOpen = !card.ExtraOpen;
    }

    /// <summary>「✓ 提交答案」（单卡：卡内按钮）：走公共收口提交本卡，校验失败提示显示在卡内。</summary>
    void OnCardSubmitClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not QuestionCardVm card) return;
        if (card.Answered) return;
        CommitCardAnswers(new[] { card }, err => card.ErrorText = err);
    }

    /// <summary>「✓ 提交答案」（多卡：消息底部公共按钮）：一次性提交本批全部未提交卡，答案合并为一条用户消息；
    /// 按钮在全卡作答完成前禁用（防御性再校验一次）。</summary>
    void OnCardsSubmitAllClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ChatMessage msg) return;
        var pending = msg.QuestionCards.Where(c => !c.Answered).ToList();
        if (pending.Count == 0) return;
        CommitCardAnswers(pending, err => msg.SetMultiSubmitError(err));
    }

    /// <summary>卡片答案提交公共收口（单卡/多卡共用，编排/普通会话通用）：会话空闲时允许提交（生成中/执行中提交会丢失答案，提前拦截）；
    /// 全部已作答才折叠为答案摘要，并作为一条用户消息进入对话（走 OnSend 全链路：编排会话为主持人继续/收敛产树，普通会话即普通一轮对话）。</summary>
    void CommitCardAnswers(IReadOnlyList<QuestionCardVm> cards, Action<string> showError)
    {
        var isOrch = currentSession?.IsOrchestration == true;
        var phase = isOrch ? (currentSession?.OrchPhase ?? "collecting") : null;   // 阶段以会话自身记录为准（两会话并行不读全局 loop）；普通会话无阶段概念
        if (cts != null || planRunner != null || multiGenCts != null || (isOrch && phase != "collecting"))
        {
            showError(isOrch && phase == "generating"
                ? "主持人已开始生成任务树，本批卡片不再作答（可在输入框补充）。"
                : "当前会话正在执行任务，本批卡片暂不能提交（等本轮结束后再作答）。");
            return;
        }
        if (cards.Any(c => !c.HasAnswer))
        {
            showError(cards.Count == 1
                ? "请至少勾选一个选项，或展开「补充说明」手打作答。"
                : "请为所有题目作答：勾选选项，或展开「补充说明」手打作答。");
            return;
        }
        var answer = string.Join("\n", cards.Select(c => c.BuildAnswerText().Trim()));
        foreach (var card in cards)
        {
            var t = card.BuildAnswerText().Trim();
            card.Summary = t.Length > 120 ? t[..120] + "…" : t;
            card.Answered = true;
        }
        SendCardAnswer(answer);
    }

    /// <summary>卡片答案注入输入框后走 OnSend（与手打等价：编排会话含收集期拦截/收敛检测/Generating 切换全链路；普通会话即普通一轮对话）。</summary>
    void SendCardAnswer(string text)
    {
        inputBox.Clear();
        inputBox.Text = text;
        OnSend(sendBtn, new RoutedEventArgs());
    }

    /// <summary>可参与决策的模型（已配 key 的才可选）：新建编排对话框勾选列表数据源。</summary>
    List<AppConfig.ModelOption> KeyedModels() => cfg.ModelOptions().Where(m => m.HasKey).ToList();

    /// <summary>编排会话 Generating 收口（Finished 事件调用）：解析任务树 → doc.md 落盘 → 打开增强确认对话框；
    /// 确认后回写架构节点关联并跳转编排页签等待手工执行；取消则回退 Collecting 继续对话调整。</summary>
    void HandleOrchestrationFinished()
    {
        try
        {
            var s = currentSession;
            if (s?.IsOrchestration != true || s.OrchPhase != "generating") return;   // 阶段以会话自身记录为准（收敛发送时已同步）
            var reply = workMsg?.Text ?? "";
            var plan = new GAIRR.AgentHost.PlanDto
            {
                Id = Guid.NewGuid().ToString("N")[..8],
                Title = s.Title ?? "编排任务",
                Goal = FirstUserText(s),
                PlanMode = "single",
                CreatedAt = DateTime.Now.ToString("O"),
                Status = "pendingConfirm",
                ArchRef = string.IsNullOrWhiteSpace(s.NodeId) ? null : s.NodeId,
            };
            // 落盘解析用归属项目根（验收 8）：编排会话 Project 即发起时归属根，不随实时 cfg.ProjectRoot 漂移
            var err = GAIRR.AgentHost.OrchestrationSession.ParseGeneration(TaskOwnerProjectRoot(s), plan, reply);
            if (err != null)
            {
                // 兜底：宿主未按协议在回复内嵌 plan-tree 代码块、而是把任务树直接落位 .gairr 时，
                // 认领已落位产物（沿用宿主 id/doc，重建规范节点）→ 走正常确认收口，不误报“解析失败”
                var landed = TryAdoptHostLandedPlan(reply, TaskOwnerProjectRoot(s));
                if (landed != null)
                {
                    plan = landed;
                    s.PlanId = plan.Id;
                    AddMessage(new ChatMessage
                    {
                        Kind = MsgKind.Agent,
                        Who = "GAIRR",
                        Text = "任务树已由 Agent 直接落位 .gairr/plans/" + plan.Id + "（回复中未输出 plan-tree 代码块），已自动认领该产物，请在确认框中核对。",
                    });
                    ConfirmOrchestrationPlan(plan);
                    return;
                }
                s.OrchPhase = "collecting";
                SyncOrch(s, "collecting");   // 解析失败回退收集阶段，框架阶段同步（仅本会话 Runner）
                SaveCurrentSession();
                UpdateOrcPanel();
                AddMessage(new ChatMessage
                {
                    Kind = MsgKind.Agent,
                    Who = "GAIRR",
                    Text = "任务树解析失败：" + err
                        + "\n注意：任务树必须内嵌在回复的 plan-tree 代码块中输出（不要自行把产物写入 .gairr/plan.json、.gairr/plans/、doc/ 等文件，落盘由系统解析后统一完成）。"
                        + "\n可继续对话补充信息后重新发送\"生成任务树\"。",
                });
                return;
            }
            s.PlanId = plan.Id;
            // 弹确认对话框统一收口：确认 → approved 落盘并等待手工执行；取消 → 回 collecting 继续调整
            ConfirmOrchestrationPlan(plan);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Orch] 生成收口失败: {ex.Message}");
        }
    }

    /// <summary>生成阶段遗留恢复（OpenSession 补位）：本会话 OrchPhase 为 generating 但已无任何在跑任务
    /// （产树任务重启中断 / EventBg 后台收口被跳过 / 前台收口异常未复位），说明“生成中”是遗留态而非进行时。
    /// 若归属会话历史中最后一条有文本的回复性消息即已产出的任务树产物（后台 Finished 已由 PersistBgResult 把
    /// 产物文本落盘本会话、但确认框未弹出），走与前台 HandleOrchestrationFinished 同口径的解析并补弹确认；
    /// 否则复位回 collecting 并给出可恢复提示，杜绝“任务树生成中，暂不能追加输入”对会话输入的永久卡死。
    /// 前提：currentSession==s（OpenSession 内调用），本会话任务记录无任何在跑任务（含 Cts/MultiGenCts/PlanRunner）。</summary>
    void TryRestoreStaleGenerating(SessionItem? s)
    {
        try
        {
            if (s?.IsOrchestration != true || s.OrchPhase != "generating") return;
            var srec = TaskOf(s);
            if (srec != null && srec.AnyRunning) return;   // 产树任务真在跑（切回时后台仍在续跑）：留给前台 Finished 正常收口弹确认
            // 产物候选 = 历史中最后一条有文本的回复性消息（收口被跳过时产物文本已落盘该会话）
            var last = s.Messages.LastOrDefault(m => m.Kind != "User" && !string.IsNullOrWhiteSpace(m.Text));
            var reply = last?.Text ?? "";
            var ownerRoot = TaskOwnerProjectRoot(s);
            var plan = new GAIRR.AgentHost.PlanDto
            {
                Id = Guid.NewGuid().ToString("N")[..8],
                Title = s.Title ?? "编排任务",
                Goal = FirstUserText(s),
                PlanMode = "single",
                CreatedAt = DateTime.Now.ToString("O"),
                Status = "pendingConfirm",
                ArchRef = string.IsNullOrWhiteSpace(s.NodeId) ? null : s.NodeId,
            };
            var err = GAIRR.AgentHost.OrchestrationSession.ParseGeneration(ownerRoot, plan, reply);
            if (err != null)
            {
                // 兜底：宿主未在回复内嵌 plan-tree 代码块、而把任务树直接落位 .gairr 时认领产物（与前台同口径）
                var landed = TryAdoptHostLandedPlan(reply, ownerRoot);
                if (landed != null)
                {
                    plan = landed;
                    s.PlanId = plan.Id;
                    AddMessage(new ChatMessage
                    {
                        Kind = MsgKind.Agent,
                        Who = "GAIRR",
                        Text = "检测到上次任务树产物已由 Agent 落位 .gairr/plans/" + plan.Id + "（回复中未输出 plan-tree 代码块），已自动认领该产物，请在确认框中核对。",
                    });
                    ConfirmOrchestrationPlan(plan);
                    return;
                }
                // 无可用产物：是生成被中断而非收口遗漏——复位回收集阶段，允许继续对话/重新生成
                var hint = s.Mode == "multi"
                    ? "可继续补充素材后点发送区「🎯 生成方案」重新生成。"
                    : "可继续补充信息后重新发送\"生成任务树\"。";
                s.OrchPhase = "collecting";
                SyncOrch(s, "collecting");   // 阶段同步回收集（仅本会话 Runner）
                SaveCurrentSession();
                UpdateOrcPanel();
                AddMessage(new ChatMessage
                {
                    Kind = MsgKind.Agent,
                    Who = "GAIRR",
                    Text = "检测到上次任务树生成已中断/未完成（应用重启或后台收口未接管），已回到收集阶段，" + hint,
                });
                return;
            }
            // 有已产出但未确认的任务树：补弹确认（复用前台确认 → approved 落盘全链路）
            s.PlanId = plan.Id;
            ConfirmOrchestrationPlan(plan);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Orch] 生成遗留恢复失败: {ex.Message}");
        }
    }

    /// <summary>取会话首条用户消息文本（L0 目标展示截断 400 字；会话为空返回空串）。</summary>
    string FirstUserText(SessionItem? s)
    {
        var m = s?.Messages.FirstOrDefault(x => x.Kind == "User")?.Text ?? "";
        return m.Length > 400 ? m[..400] : m;
    }

    /// <summary>生成收口兜底：宿主绕过回复代码块协议、把任务树直接落位 .gairr 时，
    /// 从回复文本识别其 plan id，以 doc.md 的 plan-tree 块重建规范任务树并认领（沿用宿主 id/doc，产物不丢）；
    /// 认领失败返回 null（调用方按解析失败回退）。ownerRoot=编排会话归属项目根（验收 8，不随实时根漂移）。</summary>
    GAIRR.AgentHost.PlanDto? TryAdoptHostLandedPlan(string reply, string ownerRoot)
    {
        try
        {
            // 候选 id 识别（优先级：.gairr/plans/<id> 路径 → "plan <id>" → 行首 "<id> ·" 总览行）
            var ids = new List<string>();
            void TryAdd(Match m)
            {
                if (m.Success)
                {
                    var id = m.Groups[1].Value.ToLowerInvariant();
                    if (!ids.Contains(id)) ids.Add(id);
                }
            }
            TryAdd(Regex.Match(reply, @"\.gairr[\\/]+plans[\\/]+([0-9a-fA-F]{8})"));
            TryAdd(Regex.Match(reply, @"plan\s+([0-9a-fA-F]{8})", RegexOptions.IgnoreCase));
            TryAdd(Regex.Match(reply, @"(?m)^\s*([0-9a-fA-F]{8})\s*[·•]", RegexOptions.IgnoreCase));
            foreach (var id in ids)
            {
                var stored = GAIRR.AgentHost.PlanStore.GetById(ownerRoot, id);   // 已落位条目（仅取元数据；缺失容忍）
                var docPath = GAIRR.AgentHost.PlanStore.DocPath(ownerRoot, id);
                // 节点事实源：优先 doc.md 的 plan-tree 块重建（规范结构），已落位条目 nodes 兜底
                var nodes = new List<GAIRR.AgentHost.PlanNodeDto>();
                string? title = null;
                if (System.IO.File.Exists(docPath))
                {
                    var docText = System.IO.File.ReadAllText(docPath, System.Text.Encoding.UTF8);
                    if (GAIRR.AgentHost.OrchestrationSession.ExtractPlanTree(docText, out title, out var fromDoc) == null)
                        nodes = fromDoc;
                }
                if (nodes.Count == 0 && stored != null && stored.Nodes.Count > 0)
                {
                    nodes = stored.Nodes;
                    title = stored.Title;
                }
                if (nodes.Count == 0) continue;
                // 组装规范计划：沿用宿主 id 与 doc 产物，元数据按 UI 收口语义补齐（goal 取 L0 首条，不沿用宿主杂糅内容）
                var adopted = new GAIRR.AgentHost.PlanDto
                {
                    Id = id,
                    Title = title ?? stored?.Title ?? (currentSession?.Title ?? "编排任务"),
                    Goal = FirstUserText(currentSession),
                    Status = "pendingConfirm",
                    CreatedAt = stored != null && !string.IsNullOrWhiteSpace(stored.CreatedAt)
                        ? stored.CreatedAt : DateTime.Now.ToString("O"),
                    ArchRef = string.IsNullOrWhiteSpace(currentSession?.NodeId) ? null : currentSession.NodeId,
                    SourceDoc = System.IO.Path.GetRelativePath(ownerRoot, docPath),
                    Nodes = nodes,
                };
                GAIRR.AgentHost.PlanStore.Save(ownerRoot, adopted);   // 以规范结构覆盖宿主条目（doc.md 保留宿主交付原文）
                return adopted;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Orch] 落位产物认领失败: {ex.Message}");
        }
        return null;
    }

    /// <summary>会话切换时切换 system prompt + 编排右栏显隐 + 编排框架阶段模式（OpenSession 尾部调用）。
    /// 普通会话传 null 复位：编排课题 schema 白名单不再生效（非全局 registry 开关，无残留污染）。</summary>
    void UpdateSessionPromptAndPanel()
    {
        // 两会话并行：提示词/编排阶段随各会话 Runner 自持（首任务 EnsureRunner 按会话类型装载），不随会话切换改全局 loop；
        // 切回编排会话时仅把已建 Runner 的阶段补同步到会话记录值（无 Runner 则跳过，首建时按 OrchPhase 对齐）
        if (currentSession?.IsOrchestration == true) SyncOrch(currentSession, currentSession.OrchPhase ?? "collecting");
        UpdateOrcPanel();
    }

    /// <summary>刷新右侧编排信息面板：阶段徽标、标题、L0 目标、doc.md 预览 + 编排执行树（普通会话隐藏面板）。</summary>
    void UpdateOrcPanel()
    {
        if (orcPanel == null || orcCol == null) return;
        var s = currentSession;
        // 会话/回放容器切换（快照键变化）后，查看页仍指向旧容器内容：自动关闭避免跨会话串台
        // （git diff 只读直接关；代码页编辑中有未保存修改会弹三选确认；用户选取消保留页面时把 codeKey 同步为新容器键防反复弹窗——
        //   保存目标是磁盘路径，与快照容器归属无关）
        if (gitDiffOpen && SnapDirKey() != gitDiffKey) CloseGitDiff();
        if (codeOpen && SnapDirKey() != codeKey && !CloseCodeView()) codeKey = SnapDirKey();
        if (ctxOpen && SnapDirKey() != ctxKey) CloseCtxView();   // 请求上下文页只读无脏，容器切换直接关（无未保存拦截）
        if (s?.IsOrchestration != true)
        {
            // 编排执行中切到普通会话：右栏保持“编排执行”页展开（执行进度不随会话切换消失）；
            // 代码/请求上下文查看页打开时先让位，关闭后由 CloseCodeView/CloseCtxView 的编排分支切回执行页
            if (planRunner != null && !codeOpen && !ctxOpen)
            {
                orcPanel.Visibility = Visibility.Visible;
                orcSplit.Visibility = Visibility.Visible;
                if (orcCol.Width.Value <= 0) orcCol.Width = new GridLength(OrcPanelW());
                if (orcTabBar != null) orcTabBar.Visibility = Visibility.Visible;
                OnOrcTabClick(orcTabExec, new RoutedEventArgs());
                RefreshOrchGenBtn();   // 非编排会话：隐藏发送区生成按钮
                return;
            }
            if (codeOpen || ctxOpen)
            {
                // 代码查看/请求上下文查看打开中（普通会话点“查看代码/查看”展开的右栏；git 提交详情已浮层化贴树显示，与右栏无关）：
                // 刷新时保持展开与当前查看页，不随非编排分支收起
                orcPanel.Visibility = Visibility.Visible;
                orcSplit.Visibility = Visibility.Visible;
                if (orcCol.Width.Value <= 0) orcCol.Width = new GridLength(OrcPanelW());
                if (orcTabBar != null) orcTabBar.Visibility = Visibility.Visible;
                SyncOrcTabs();
                RefreshOrchGenBtn();   // 发送区生成按钮随会话状态刷新
                return;
            }
            // 右栏收起时不写记忆：默认 1/3 由 OrcPanelW 现算（窗口缩放后重新展开仍贴合当前会话区）
            orcPanel.Visibility = Visibility.Collapsed;
            orcSplit.Visibility = Visibility.Collapsed;   // 拖把收起后其 Auto 列归零，会话区重新占满整行
            orcCol.Width = new GridLength(0);
            RefreshOrchGenBtn();   // 非编排会话：隐藏发送区生成按钮
            return;
        }
        orcPanel.Visibility = Visibility.Visible;
        orcSplit.Visibility = Visibility.Visible;         // 露出右栏拖把（独立 Auto 列）
        // 首次展开/重新展开：未拖过按会话区 1/3（OrcPanelW），拖过用记忆宽度，刷新不重置
        if (orcCol.Width.Value <= 0) orcCol.Width = new GridLength(OrcPanelW());
        // 编排方案 Tab 内容
        orcPhaseDot.Fill = (FindResource(s.OrchPhase switch
        {
            "generating" => "AmberBrush",
            "done" => "GreenBrush",
            _ => "DimBrush",
        }) as Brush) ?? new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x77));
        orcPhaseText.Text = GAIRR.AgentHost.OrchestrationSession.PhaseText(s.OrchPhase, s.Mode);
        orcTitle.Text = s.Title ?? "编排会话";
        orcGoal.Text = FirstUserText(s);
        // 编排文档 doc.md 预览（Markdown 轻量渲染：标题加粗变色/行内粗体/代码变色，字号字体不变）
        var docText = "";
        if (!string.IsNullOrWhiteSpace(s.PlanId))
        {
            try
            {
                var docPath = GAIRR.AgentHost.PlanStore.DocPath(cfg.ProjectRoot, s.PlanId);
                if (System.IO.File.Exists(docPath))
                    docText = System.IO.File.ReadAllText(docPath);
            }
            catch { }
        }
        SetOrcDocPreview(docText);
        // 编排执行树同步刷新
        LoadOrchExecTree();
        // 有计划时显示 Tab 条并默认切到编排执行；无计划时隐藏 Tab 条只显示编排方案；
        // 代码查看页打开时 Tab 条保持显示（页签细节由 SyncOrcTabs 管理），阶段刷新不把用户正在看的代码页切走
        var hasPlan = !string.IsNullOrWhiteSpace(s.PlanId);
        if (orcTabBar != null)
            orcTabBar.Visibility = (hasPlan || codeOpen || ctxOpen) ? Visibility.Visible : Visibility.Collapsed;
        if (codeOpen || ctxOpen)
            SyncOrcTabs();
        else if (hasPlan)
            OnOrcTabClick(orcTabExec, new RoutedEventArgs());
        else
            OnOrcTabClick(orcTabPlan, new RoutedEventArgs());
        // 发送区生成按钮随会话状态刷新（收集期显示；生成中/已确认隐藏）
        RefreshOrchGenBtn();
    }

    /// <summary>编排右栏拖把拖动结束：记忆新宽度；拖得太窄（<200）视为误拖，回弹默认宽度避免面板消失。
    /// 记忆只在用户真正拖动时写入（拖太窄按未拖过处理），代码查看等临时改宽不污染记忆。</summary>
    void OnOrcSplitDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (orcCol.Width.Value < 200)
        {
            orcUserWidth = 0;   // 误拖：视同未拖过，下次展开按默认 1/3（OrcPanelW）
            orcCol.Width = new GridLength(OrcPanelW());
        }
        else
            orcUserWidth = orcCol.Width.Value;   // 仅用户拖动才记忆：恢复时优先此宽度
    }

    /// <summary>编排右栏默认/恢复宽度：用户拖过（orcUserWidth>0）用记忆值；未拖过按会话区 1/3 展开
    /// （会话区此时约占总行 2/3，右栏=(总宽-拖把 6px)/3；需求 5 默认宽）；布局未完成回退 320 防塌。</summary>
    double OrcPanelW()
    {
        if (orcUserWidth > 0) return orcUserWidth;
        if (msgGrid == null || msgGrid.ActualWidth <= 0) return 320;
        var w = (msgGrid.ActualWidth - 6) / 3.0;
        return Math.Max(240, w);   // 窄窗保护：太窄不可读时放宽
    }
    
    /// <summary>右栏（代码查看/git 提交详情宿主）弹出时把宽度设为会话区的 50%：
    /// 会话区+拖把列(6px)+右栏铺满整行，故右栏 = (总宽-6)/2 时两侧正好各占一半；未布局完成回退默认宽。
    /// 只在"打开"时调用一次：不写 orcUserWidth 记忆——打开期间的拖拽宽度由 OnOrcSplitDragCompleted 记忆，刷新不重置。</summary>
    void SetOrcHalfWidth()
    {
        if (orcCol == null) return;
        double w;
        if (msgGrid != null && msgGrid.ActualWidth > 0)
            w = Math.Max(220, Math.Min((msgGrid.ActualWidth - 6) / 2.0, msgGrid.ActualWidth * 0.7));
        else
            w = OrcPanelW();   // 布局尚未完成：暂按面板默认宽
        orcCol.Width = new GridLength(w);
    }

    // ───── 编排文档 doc.md 富文本预览：轻量 Markdown 渲染（字号与字体保持不变，仅加粗/变色两类强调） ─────

    string? lastOrcDocText;   // 已渲染的 doc 文本缓存：未变化不重建（保留预览滚动位置）

    static readonly Regex MdHeading = new(@"^#{1,6}\s+(.+)$");
    static readonly Regex MdRule = new(@"^(-{3,}|\*{3,}|_{3,})\s*$");
    static readonly Regex MdList = new(@"^(\s*(?:[-*+]|\d+[.)])\s+)(.+)$");
    static readonly Regex MdInline = new(@"\*\*(.+?)\*\*|`([^`]+)`");

    /// <summary>doc.md 预览渲染入口：文本与上次相同则跳过重建（保留滚动位置，避免消息刷新时跳顶）。</summary>
    void SetOrcDocPreview(string md)
    {
        md = GAIRR.Core.Markdown.Sanitize(md);   // 毒字符防线：RichTextBox 内部 FlowDocument 同样走 PtsHost，脏文本会 fail-fast 崩溃
        if (md == lastOrcDocText) return;
        lastOrcDocText = md;
        var doc = orcDocPreview.Document;
        doc.Blocks.Clear();
        if (string.IsNullOrWhiteSpace(md)) return;
        doc.FontFamily = new FontFamily("Consolas");   // 字号/字体与纯文本时代一致，不做视觉放大
        doc.FontSize = 12;
        doc.Foreground = FindResource("TextBrush") as Brush ?? Brushes.White;
        BuildMdBlocks(doc, md);
    }

    /// <summary>Markdown → FlowDocument 块：标题加粗+琥珀色；行内 **粗体**=纯白加粗、`代码`=淡蓝；
    /// 围栏代码块整段淡蓝；列表/序号前缀转灰；空行/分隔线仅断段。字号字体全部不变。</summary>
    void BuildMdBlocks(FlowDocument doc, string md)
    {
        var amber = FindResource("AmberBrush") as Brush ?? Brushes.Orange;   // 标题色
        var dim = FindResource("DimBrush") as Brush ?? Brushes.Gray;          // 列表/序号前缀
        var code = new SolidColorBrush(Color.FromRgb(0x8F, 0xC1, 0xFF));      // 行内/块代码：淡蓝
        var strong = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));    // **粗体**：纯白
        Paragraph NewP()
        {
            var np = new Paragraph { Margin = new Thickness(0, 0, 0, 5) };
            doc.Blocks.Add(np);
            return np;
        }
        void AddInline(Paragraph p, string s)
        {
            var pos = 0;
            foreach (Match m in MdInline.Matches(s))
            {
                if (m.Index > pos) p.Inlines.Add(new Run(s[pos..m.Index]));
                if (m.Groups[1].Success)
                    p.Inlines.Add(new Run(m.Groups[1].Value) { FontWeight = FontWeights.Bold, Foreground = strong });
                else
                    p.Inlines.Add(new Run(m.Groups[2].Value) { Foreground = code });
                pos = m.Index + m.Length;
            }
            if (pos < s.Length) p.Inlines.Add(new Run(s[pos..]));
        }
        Paragraph? cur = null;
        var inCode = false;
        foreach (var raw in md.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw;
            var t = line.Trim();
            if (inCode)
            {
                if (t.StartsWith("```")) { inCode = false; cur = null; continue; }
                (cur ??= NewP()).Inlines.Add(new Run(line) { Foreground = code });
                cur.Inlines.Add(new LineBreak());
                continue;
            }
            if (t.StartsWith("```")) { inCode = true; cur = null; continue; }
            if (t.Length == 0 || MdRule.IsMatch(t)) { cur = null; continue; }   // 空行/分隔线：断段
            var mh = MdHeading.Match(t);
            if (mh.Success)
            {
                cur = null;
                var np = NewP();
                np.Inlines.Add(new Run(mh.Groups[1].Value) { FontWeight = FontWeights.Bold, Foreground = amber });
                continue;
            }
            var ml = MdList.Match(line);
            if (ml.Success)
            {
                cur = null;
                var np = NewP();
                np.Inlines.Add(new Run(ml.Groups[1].Value) { Foreground = dim });
                AddInline(np, ml.Groups[2].Value);
                continue;
            }
            cur ??= NewP();   // 相邻普通行并入同一段落（Markdown 语义）
            AddInline(cur, line);
        }
    }

    /// <summary>加载编排执行树到右侧 treeOrchExec：仅显示当前会话关联的 planId 对应的编排树。</summary>
    void LoadOrchExecTree()
    {
        if (treeOrchExec == null) return;
        var planId = currentSession?.PlanId;
        if (string.IsNullOrWhiteSpace(planId))
        {
            treeOrchExec.ItemsSource = null;
            return;
        }
        // ② 统一口径：编排树/架构树读写一律按会话归属项目根，避免切项目后 cfg.ProjectRoot 漂移导致读不到计划、同步不到功能树
 var orchRoot = TaskOwnerProjectRoot(currentSession);
 var p = GAIRR.AgentHost.PlanStore.GetById(orchRoot, planId);
        if (p == null || p.Status is "pendingConfirm" or "deleted")
        {
            treeOrchExec.ItemsSource = null;
            lastOrchTreeSig = null;
            return;
        }
        // 中断对账：上次进程退出（强杀）时计划会残留 running、叶子停在 running/reviewing 而无人续跑。
        // 无运行实例时把计划降级为 paused：恢复“开始/继续执行”入口，残留叶子保留供人工裁决/撤销。
        if (planRunner == null && p.Status == "running")
        {
            p.Status = "paused";
            GAIRR.AgentHost.PlanStore.Save(orchRoot, p);
            AddMessage(new ChatMessage
            {
                Kind = MsgKind.Cmd,
                Who = "编排执行",
                NoPersist = true,   // 运行状态提示：不写入会话历史
                Text = $"[plan] 检测到计划「{p.Title}」上次执行被中断（进程退出），已置为暂停。" +
                       "中断时进行中的叶子可右键「标记通过/标记跳过」收尾，或「撤销标记」后重新执行续跑。",
            });
        }
        // 状态指纹：仅当叶子状态集合变化才重建树（避免无谓刷新导致的高亮闪烁）
        var sig = p.Status + ":" + string.Join(",", p.Nodes.OrderBy(n => n.Id).Select(n => $"{n.Id}={n.Status}"));
        if (sig == lastOrchTreeSig && treeOrchExec.ItemsSource != null) return;
        lastOrchTreeSig = sig;
        // 编排树状态/结构变化 → 回写已关联功能叶子（plan.json 为唯一事实源）；确有变化才重建功能树，避免无谓刷新
        if (ArchTreeStore.SyncPlanStatus(orchRoot) > 0) LoadArchTree(orchRoot);
        var (root, _) = ArchTreeStore.FromPlan(p);
        treeOrchExec.ItemsSource = new List<ArchNode> { root };
        // 执行期间：自动展开并滚动到当前 running 叶子的附近层级（联动定位）
        if (planRunner != null) ExpandToRunning(root);
        RefreshPlanSessionDots();   // 树状态变化落盘后同步会话树圆点（含人工裁决标记通过/跳过/撤销）
    }

    /// <summary>从编排树根递归展开到第一个处于 running 的叶子所在链（去除视觉定位，帮助用户追踪当前执行点）。</summary>
    void ExpandToRunning(ArchNode root)
    {
        Stack<ArchNode> chain = new();
        if (!TryFindRunning(root, chain)) return;
        // 逐级展开（直接改节点 IsOpen；重建前 Prepare 已把 L0/L1 展开）
        ArchNode? target = null;
        foreach (var node in chain) { node.IsOpen = true; target = node; }
        // 选中 running 叶子所在 TreeViewItem
        if (target != null) SelectTreeItem(target);
    }

    bool TryFindRunning(ArchNode n, Stack<ArchNode> chain)
    {
        chain.Push(n);
        if (n.NodeType == 1 && n.Status == "running") return true;
        foreach (var c in n.Children) if (TryFindRunning(c, chain)) return true;
        chain.Pop();
        return false;
    }

    /// <summary>按数据项选中编排树中对应 TreeViewItem（递归查找并展开）。</summary>
    void SelectTreeItem(ArchNode target)
    {
        foreach (var item in treeOrchExec.Items)
        {
            if (SelectTreeItemRec(treeOrchExec.ItemContainerGenerator.ContainerFromItem(item) as TreeViewItem, target))
                return;
        }
    }

    bool SelectTreeItemRec(TreeViewItem? tv, ArchNode target)
    {
        if (tv == null) return false;
        if (tv.DataContext is ArchNode n && ReferenceEquals(n, target))
        {
            tv.IsSelected = true;
            tv.BringIntoView();
            return true;
        }
        foreach (var child in tv.Items)
        {
            if (SelectTreeItemRec(tv.ItemContainerGenerator.ContainerFromItem(child) as TreeViewItem, target))
                return true;
        }
        return false;
    }

    /// <summary>按 plan 叶子 id（NodeRef）定位编排树中对应节点：逐级展开祖先链并选中，
    /// 用于人工裁决（标记通过/跳过）后让被标记叶子的状态反馈立即可见。</summary>
    void RevealOrchNode(ArchNode root, string leafId)
    {
        if (string.IsNullOrEmpty(leafId)) return;
        Stack<ArchNode> chain = new();
        if (!TryFindNodeRef(root, leafId, chain)) return;
        // 逐级展开：须在容器生成前改 IsOpen，SelectTreeItem 强制生成容器时绑定即取到新值（同 ExpandToRunning）
        foreach (var node in chain) node.IsOpen = true;
        SelectTreeItem(chain.Peek());   // Peek = 链顶 = 被标记叶子
    }

    /// <summary>从 root 递归查找 NodeRef==leafId 的叶子（NodeType==1），沿途节点压入 chain（根→叶）。</summary>
    bool TryFindNodeRef(ArchNode n, string leafId, Stack<ArchNode> chain)
    {
        chain.Push(n);
        if (n.NodeRef == leafId && n.NodeType == 1) return true;
        foreach (var c in n.Children) if (TryFindNodeRef(c, leafId, chain)) return true;
        chain.Pop();
        return false;
    }

    /* ================= 右侧代码查看器（第三页签“代码查看”）：读文件卡“⌕ 查看代码”打开，快照→磁盘→Git 三层取源码 ================= */

    /// <summary>右栏页签样式与内容区随 codeOpen/ctxOpen 同步：代码查看/请求上下文两查看页与编排执行/方案两页互斥共用宿主；
    /// 两查看页也互斥（请求上下文页只读无脏，另一页打开时直接卸载）。git 提交详情已浮层化（贴左树浮出）与右栏完全解耦，
    /// 不再参与页签与内容互斥。UpdateOrcPanel 编排分支会重复调用
    /// （先 OnOrcTabClick 再 SyncOrcTabs），幂等无副作用。</summary>
    void SyncOrcTabs()
    {
        if (orcTabBar == null || orcTabCode == null || orcCodeContent == null || codeTabClose == null
            || orcTabCtx == null || orcCtxContent == null) return;
        var viewerOpen = codeOpen || ctxOpen;
        // 页签行右端 ✕（面板右上角）仅查看页打开时显示：编排方案/执行页签无需关闭钮
        codeTabClose.Visibility = viewerOpen ? Visibility.Visible : Visibility.Collapsed;
        codeTabClose.ToolTip = ctxOpen ? "关闭请求上下文" : "关闭代码查看";
        var isOrch = currentSession?.IsOrchestration == true;
        // 编排执行中（含切到普通会话）执行 Tab 常驻可见；方案 Tab 仅编排会话可看（内容取自编排会话字段）
        var hasPlan = (isOrch && !string.IsNullOrWhiteSpace(currentSession?.PlanId)) || planRunner != null;
        orcTabExec.Visibility = hasPlan ? Visibility.Visible : Visibility.Collapsed;
        orcTabPlan.Visibility = (isOrch && hasPlan) ? Visibility.Visible : Visibility.Collapsed;
        if (ctxOpen)
        {
            // 请求上下文页激活：代码查看页签隐藏（内容回切只能重新点入口卡，与代码查看页切走行为一致）
            orcTabCtx.Visibility = Visibility.Visible;
            orcTabCtx.Tag = "cur";
            orcTabCode.Visibility = Visibility.Collapsed;
            orcTabCode.Tag = null;
            orcTabExec.Tag = null;
            orcTabPlan.Tag = null;
            orcCtxContent.Visibility = Visibility.Visible;
            orcCodeContent.Visibility = Visibility.Collapsed;
            orcPlanContent.Visibility = Visibility.Collapsed;
            treeOrchExec.Visibility = Visibility.Collapsed;
        }
        else if (codeOpen)
        {
            orcTabCtx.Visibility = Visibility.Collapsed;
            orcTabCtx.Tag = null;
            orcTabCode.Visibility = Visibility.Visible;
            orcTabCode.Tag = "cur";
            orcTabExec.Tag = null;
            orcTabPlan.Tag = null;
            orcCtxContent.Visibility = Visibility.Collapsed;
            orcCodeContent.Visibility = Visibility.Visible;
            orcPlanContent.Visibility = Visibility.Collapsed;
            treeOrchExec.Visibility = Visibility.Collapsed;
        }
        else
        {
            orcTabCtx.Visibility = Visibility.Collapsed;
            orcTabCtx.Tag = null;
            orcTabCode.Visibility = Visibility.Collapsed;
            orcTabCode.Tag = null;
            orcCtxContent.Visibility = Visibility.Collapsed;
            orcCodeContent.Visibility = Visibility.Collapsed;
        }
        SyncCodeEditUi();   // 编辑/保存/放弃/磁盘当前按钮随 codeOpen/编辑态/运行态刷新（查看态编辑钮在任务启停后同步禁用/恢复）
    }

    /* ================= 请求上下文查看器（思考卡“上下文”按钮打开）：每轮请求前 Agent 已把将发给模型的 history 落盘 data/rounds/ ================= */

    /// <summary>思考卡“上下文”入口点击：取卡片（ThinkingItem，含轮次与卡创建时刻）打开请求上下文查看器。</summary>
    void OnThinkCtxClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ThinkingItem card)
            OpenCtxViewer(card);
    }

    /// <summary>在右侧查看器打开指定思考卡的请求上下文快照：定位 data/rounds/&lt;会话&gt;/round{No}[-k].json
    /// （同轮重跑自动追加序号防覆盖），按“卡创建时刻之前最近写入”就近匹配，内容只读复盘该轮发给模型的完整上下文。
    /// 与代码查看互斥（ctx 只读直接卸载对方）；seq 防抖丢弃过期后台读盘结果。</summary>
    void OpenCtxViewer(ThinkingItem card)
    {
        var round = card.Round;
        if (round <= 0) return;
        if (gitDiffOpen) CloseGitDiff();   // 提交详情浮层与右栏查看互斥：先收浮层
        if (codeOpen && !CloseCodeView()) return;   // 代码页编辑未保存：保存/放弃/取消，取消则中止打开
        if (ctxOpen) TeardownCtxView();    // 已在 ctx 页：重开前清场（防上次残留内容闪烁）
        var key = SnapDirKey();
        var file = FindCtxSnapFile(key, round, card.CreatedAt);
        if (string.IsNullOrEmpty(file))
        {
            ShowToast($"第 {round} 轮未找到请求上下文快照（快照功能启用前的轮次或自动任务执行不落盘）");
            return;
        }
        ctxOpen = true;
        ctxKey = key;
        var seq = ++ctxSeq;
        var fullPath = file;
        // 回应快照定位：与请求快照严格同基名配对（roundN[-k].json → roundN[-k]-resp.json，Agent 侧本轮回应到手即落盘）；
        // 不存在=功能启用前的轮次/本轮被中断/自动任务不落盘 → 回应子页显示占位，不从下一轮 history 反推（截断压缩场景会失真）
        ctxReqFile = fullPath;
        ctxRespFile = CtxRespSnapPath(fullPath);
        ctxReqText = ctxRespText = null;
        ctxRespPage = false;   // 打开默认停在“请求上下文”子页（与改动前一致，老手感不变）
        // 展开右栏并切到“请求上下文”页（普通会话也借编排面板宿主，页签条只留请求上下文一项）；弹出宽度=会话区 50%
        var stickBottom = IsMsgAtBottom();   // 展开前记贴底状态：会话区将变窄、消息重新折行增高，原贴底需在布局稳定后补滚
        orcPanel.Visibility = Visibility.Visible;
        orcSplit.Visibility = Visibility.Visible;
        SetOrcHalfWidth();
        if (orcTabBar != null) orcTabBar.Visibility = Visibility.Visible;
        SyncOrcTabs();
        if (stickBottom) ScrollToBottomAfterLayout();   // 折行增高布局完成后再滚底（同代码查看器）
        ctxTitle.Text = card.Title.Length > 0 ? card.Title : $"第 {round} 轮思考";
        ApplyCtxSubPage();   // 头部徽标/路径/正文按当前子页装（两页文本未就绪时先显示“载入中”占位）
        // 后台一次读两页文件（工具结果多时快照可达数百 KB，不进 UI 线程）；回来后双双进缓存，
        // 此后子页来回切换纯内存换文本，零读盘
        var reqPath = fullPath;
        var respPath = ctxRespFile;
        Task.Run(() => (req: ReadAllOrNull(reqPath), resp: ReadAllOrNull(respPath)))
            .ContinueWith(t =>
            {
                if (t.IsFaulted || !ctxOpen || seq != ctxSeq) return;   // 已关闭/已被新打开覆盖：过期结果丢弃
                if (t.Result.req == null) { ShowToast("读取请求上下文快照失败（文件可能已被清理）"); return; }
                ctxReqText = FormatCtxSnapshot(t.Result.req);   // 内嵌 JSON 缩进美化 + \uXXXX 转中文，便于复盘阅读
                ctxRespText = t.Result.resp == null ? null : FormatCtxSnapshot(t.Result.resp);
                ApplyCtxSubPage();
            }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion,
               System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>读文本文件，不存在/失败返回 null（供后台线程使用，不碰 UI 对象）</summary>
    static string? ReadAllOrNull(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try { return System.IO.File.ReadAllText(path); } catch { return null; }
    }

    /// <summary>由请求快照路径派生配对的回应快照路径：同目录、同基名 + "-resp.json"；
    /// 文件不存在返回 null（回应子页据此显示占位文案，不做反推兜底）。</summary>
    static string? CtxRespSnapPath(string reqPath)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(reqPath);
            if (string.IsNullOrEmpty(dir)) return null;
            var p = System.IO.Path.Combine(dir, System.IO.Path.GetFileNameWithoutExtension(reqPath) + "-resp.json");
            return System.IO.File.Exists(p) ? p : null;
        }
        catch { return null; }
    }

    /// <summary>ctx 查看器子页切换点击（请求上下文 / 回应内容）：只换正文与头部徽标，不重新读盘、
    /// 不动右栏宽度与顶层页签态（两子页共用同一宿主、头部、✕ 关闭钮与会话区 50% 宽度规则）。</summary>
    void OnCtxSubClick(object sender, RoutedEventArgs e)
    {
        if (!ctxOpen) return;
        var toResp = ReferenceEquals(sender, ctxSubResp);
        if (toResp == ctxRespPage) return;   // 已在该子页：幂等无操作（防连点重复装文本）
        ctxRespPage = toResp;
        if (toResp) RetryLoadCtxResp();      // 回应快照可能在查看器打开后才落盘（本轮仍在跑）：切页时重探一次磁盘
        ApplyCtxSubPage();
    }

    /// <summary>切到回应子页且尚未探到配对文件时重探磁盘并后台补读：请求快照先落、回应快照后落，
    /// 若在本轮回应完成前就打开查看器，重探即可补上；仍无则维持占位文案。</summary>
    void RetryLoadCtxResp()
    {
        if (ctxReqFile == null || ctxRespFile != null || ctxRespText != null) return;
        var p = CtxRespSnapPath(ctxReqFile);
        if (p == null) return;
        ctxRespFile = p;
        var seq = ctxSeq;   // 不递增：请求页在途读盘仍有效；补读结果按同一序号校验（期间重开新卡则自动作废）
        Task.Run(() => ReadAllOrNull(p))
            .ContinueWith(t =>
            {
                if (t.IsFaulted || !ctxOpen || seq != ctxSeq) return;
                if (t.Result == null)
                {
                    // 探测到文件却读不出（落盘进行中/被清理）：复位为“无回应快照”，避免正文卡在“载入中”
                    ctxRespFile = null;
                    if (ctxRespPage) ApplyCtxSubPage();
                    return;
                }
                ctxRespText = FormatCtxSnapshot(t.Result);
                if (ctxRespPage) ApplyCtxSubPage();   // 仍停在回应子页才刷新（用户可能已切回请求页）
            }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion,
               System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>按当前子页（ctxRespPage）装载 ctx 查看器：子页按钮高亮 + 来源徽标文案配色 + 快照文件路径 + 正文，
    /// 并滚回顶部。文本取打开时的缓存（切页零读盘）；缓存未就绪显示“载入中”；回应快照缺失显示占位说明。</summary>
    void ApplyCtxSubPage()
    {
        if (ctxEdit == null || ctxSubReq == null || ctxSubResp == null) return;
        ctxSubReq.Tag = ctxRespPage ? null : "cur";     // SkillViewBtn 的 Tag=cur 触发器：当前子页高亮，另一个恢复常态
        ctxSubResp.Tag = ctxRespPage ? "cur" : null;
        var file = ctxRespPage ? ctxRespFile : ctxReqFile;
        var missing = ctxRespPage && file == null;
        if (ctxSrcText != null && ctxSrcTag != null)
        {
            ctxSrcText.Text = !ctxRespPage ? "请求快照" : missing ? "无回应快照" : "回应快照";
            ctxSrcTag.Background = !ctxRespPage ? CtxReqTagBg : missing ? CtxMissTagBg : CtxRespTagBg;
            ctxSrcText.Foreground = !ctxRespPage ? CtxReqTagFg : missing ? CtxMissTagFg : CtxRespTagFg;
        }
        if (ctxRange != null)
        {
            if (missing)
            {
                var reqBase = ctxReqFile == null ? "" : System.IO.Path.GetFileNameWithoutExtension(ctxReqFile);
                ctxRange.Text = $"期望配对文件 {reqBase}-resp.json 不存在";
            }
            else if (file != null)
            {
                var dirName = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(file)) ?? "";
                ctxRange.Text = $"data/rounds/{dirName}/{System.IO.Path.GetFileName(file)}";
            }
            else ctxRange.Text = "";
        }
        var text = ctxRespPage ? ctxRespText : ctxReqText;
        ctxEdit.Text = text ?? (missing ? CtxRespMissingText : "（快照载入中…）");
        ctxEdit.ScrollToHome();
    }

    /// <summary>回应快照缺失时的正文占位文案（含缺失原因与“为何不反推兜底”的说明）</summary>
    const string CtxRespMissingText =
        "本轮无回应快照（功能启用前的轮次或本轮被中断）\n\n"
        + "说明：\n"
        + "· 回应快照由 Agent 侧在每轮模型回应到手时落盘 data/rounds/会话/roundN-resp.json，\n"
        + "  与请求快照 roundN.json 严格同基名配对（同轮重跑的 roundN-2.json 配 roundN-2-resp.json）；\n"
        + "· 本功能启用之前的历史轮次没有回应快照文件，这些轮次只能查看请求上下文；\n"
        + "· 本轮被中断（停止/异常退出）或属自动任务执行（不落盘）时同样没有该文件；\n"
        + "· 不做“从下一轮请求 history 反推上一条 assistant 消息”的兜底：截断/压缩场景下\n"
        + "  反推结果与模型当时的真实回应不一致，宁缺勿假。";

    /* ---------- 快照显示格式化：内嵌 JSON 缩进美化 + \uXXXX 转中文 ---------- */

    /// <summary>\uXXXX 代理对（emoji 等增补平面字符，如 \uD83D\uDE00）先行合并解码</summary>
    static readonly Regex CtxUniPair = new(@"\\u([Dd][89ABab][0-9A-Fa-f]{2})\\u([DdCDEFcdef][0-9A-Fa-f]{2})", RegexOptions.Compiled);

    /// <summary>单个 \uXXXX（BMP 字符）解码</summary>
    static readonly Regex CtxUniSingle = new(@"\\u([0-9A-Fa-f]{4})", RegexOptions.Compiled);

    /// <summary>快照文本显示前整理：整行内嵌 JSON（工具参数等）逐行尝试缩进美化（ relaxed 编码中文直出），
    /// 剩余 \uXXXX 转中文（含代理对）；非 JSON 行原样保留，解析失败也原样保留，绝不破坏原文。</summary>
    static string FormatCtxSnapshot(string text)
    {
        try
        {
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
                if (TryPrettyJsonLine(lines[i]) is { } pretty)
                    lines[i] = pretty;
            var joined = string.Join("\n", lines);
            joined = CtxUniPair.Replace(joined, m => char.ConvertFromUtf32(
                (Convert.ToInt32(m.Groups[1].Value, 16) - 0xD800) * 0x400
                + Convert.ToInt32(m.Groups[2].Value, 16) - 0xDC00 + 0x10000));
            return CtxUniSingle.Replace(joined, m => char.ConvertFromUtf32(Convert.ToInt32(m.Groups[1].Value, 16)));
        }
        catch { return text; }   // 格式化是纯展示增强，任何异常回退原文
    }

    /// <summary>单行 JSON 缩进美化：行首（去空白）为 { 或 [ 且整行可解析时返回缩进文本，否则 null。</summary>
    static string? TryPrettyJsonLine(string line)
    {
        var t = line.TrimStart();
        if (t.Length == 0 || (t[0] != '{' && t[0] != '[')) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(line);
            return System.Text.Json.JsonSerializer.Serialize(doc.RootElement, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping   // 中文直出不再 \u 转义
            });
        }
        catch { return null; }
    }

    /// <summary>定位轮次快照文件：目录 data/rounds/&lt;key&gt;/ 下 round{N}.json / round{N}-{k}.json（k≥2）。
    /// 同轮多次重跑时多版本并存，取“写入时刻 ≤ 卡创建时刻+30s”中最近者（快照必先于 Round 事件建卡，
    /// 落在卡之后写入的同轮文件只能来自后续任务的同轮次，需排除）；无匹配回退该轮最新文件。</summary>
    string? FindCtxSnapFile(string? key, int round, DateTime created)
    {
        if (string.IsNullOrEmpty(key)) return null;
        try
        {
            var dir = System.IO.Path.Combine(GAIRR.Core.Paths.RoundsDir, SnapSafeKey(key));
            if (!System.IO.Directory.Exists(dir)) return null;
            var files = System.IO.Directory.GetFiles(dir, "round*.json")
                .Where(f => System.Text.RegularExpressions.Regex.IsMatch(System.IO.Path.GetFileName(f), @"^round" + round + @"(-\d+)?\.json$"))
                .ToList();
            if (files.Count == 0) return null;
            var limit = created.AddSeconds(30);
            var hit = files.Where(f => System.IO.File.GetLastWriteTime(f) <= limit)
                .OrderBy(f => System.IO.File.GetLastWriteTime(f)).LastOrDefault();
            return hit ?? files.OrderBy(f => System.IO.File.GetLastWriteTime(f)).Last();
        }
        catch { return null; }
    }

    /// <summary>会话键转目录名：只保留字母/数字（与 AgentLoop 快照目录同规则）</summary>
    static string SnapSafeKey(string key) => new(key.Where(char.IsLetterOrDigit).ToArray());

    /// <summary>关闭请求上下文页：ctx 只读无脏直接卸载，按会话类型切回编排默认页或收起右栏（规则同 CloseCodeView）。</summary>
    void CloseCtxView()
    {
        if (!ctxOpen && ctxKey == null) return;
        TeardownCtxView();
        var s = currentSession;
        if (s?.IsOrchestration == true)
        {
            // 编排会话：切回与 UpdateOrcPanel 相同的默认页（有计划执行页 / 无计划方案页）
            // 宽度恢复（需求 5）：ctx 页占半宽打开，关闭后回编排面板默认（会话区 1/3 或用户拖宽）
            orcCol.Width = new GridLength(OrcPanelW());
            var hasPlan = !string.IsNullOrWhiteSpace(s.PlanId);
            if (hasPlan)
                OnOrcTabClick(orcTabExec, new RoutedEventArgs());
            else
                OnOrcTabClick(orcTabPlan, new RoutedEventArgs());
            if (orcTabBar != null)
                orcTabBar.Visibility = hasPlan ? Visibility.Visible : Visibility.Collapsed;
        }
        else if (planRunner != null)
        {
            // 编排执行中：查看页关闭后右栏回“编排执行”页（不收起，执行进度保持可见）；宽度同样恢复默认
            orcPanel.Visibility = Visibility.Visible;
            orcSplit.Visibility = Visibility.Visible;
            orcCol.Width = new GridLength(OrcPanelW());   // ctx(半宽)关闭：恢复会话区 1/3 或用户拖宽（需求 5）
            if (orcTabBar != null) orcTabBar.Visibility = Visibility.Visible;
            OnOrcTabClick(orcTabExec, new RoutedEventArgs());
            RefreshOrchGenBtn();   // 非编排会话：隐藏发送区生成按钮
        }
        else
        {
            // 普通收起不写宽度记忆：拖宽只由拖把记录，默认 1/3 由 OrcPanelW 现算（避免半宽被记成默认）
            orcPanel.Visibility = Visibility.Collapsed;
            orcSplit.Visibility = Visibility.Collapsed;
            orcCol.Width = new GridLength(0);
            if (orcTabBar != null) orcTabBar.Visibility = Visibility.Collapsed;
            RefreshOrchGenBtn();   // 与 UpdateOrcPanel 非编排收起分支一致：隐藏发送区生成按钮
            SyncOrcTabs();
        }
    }

    /// <summary>卸载请求上下文页内容与状态（不询问不切页：调用方决定后续页面去向；只读页无脏数据）</summary>
    void TeardownCtxView()
    {
        ctxOpen = false;
        ctxSeq++;                 // 后台读盘中的结果经 seq 校验自动丢弃
        ctxKey = null;
        ctxRespPage = false;      // 子页复位到“请求上下文”：下次打开不残留上一张卡的回应页状态
        ctxReqText = ctxRespText = null;   // 两页文本缓存一并丢弃（单快照可达数百 KB，不长期占内存）
        ctxReqFile = ctxRespFile = null;
        if (ctxEdit != null) { ctxEdit.Text = ""; ctxEdit.ScrollToHome(); }
        if (ctxTitle != null) ctxTitle.Text = "";
        if (ctxSrcText != null) { ctxSrcText.Text = ""; ctxSrcText.Foreground = CtxReqTagFg; }
        if (ctxSrcTag != null) ctxSrcTag.Background = CtxReqTagBg;
        if (ctxRange != null) ctxRange.Text = "";
        if (ctxSubReq != null) ctxSubReq.Tag = "cur";    // 子页按钮高亮复位（Tag=cur 触发器）
        if (ctxSubResp != null) ctxSubResp.Tag = null;
        SyncOrcTabs();
    }


    /// <summary>工具卡“查看代码”入口点击：取卡片（ToolCall，含文件相对路径与读取行段）打开代码查看器。</summary>
    void OnCodeViewClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ToolCall card)
            OpenCodeViewer(card);
    }

    /// <summary>查看页头部 ✕ 关闭按钮点击：请求上下文页优先（两查看页互斥，ctx 打开时代码页必未开）。</summary>
    void OnCodeViewClose(object sender, RoutedEventArgs e)
    {
        if (ctxOpen) CloseCtxView();
        else CloseCodeView();
    }

    /// <summary>关闭代码查看器：按当前会话类型切回编排默认页（有执行页/无方案页）或收起右栏；
    /// 后台加载中的结果经 codeSeq 校验自动丢弃。编辑中有未保存修改先三选拦截（保存/放弃/取消）。
    /// 编排 Tab 条可见性规则与 UpdateOrcPanel 一致。返回 false=用户取消关闭（代码页保留）。</summary>
    bool CloseCodeView()
    {
        if (!codeOpen && codeCard == null && !codeEditing) return true;
        if (!GuardCodeDirty("关闭代码查看")) return false;   // 有未保存修改：用户选取消 → 保持打开
        TeardownCodeView();
        var s = currentSession;
        if (s?.IsOrchestration == true)
        {
            // 编排会话：切回与 UpdateOrcPanel 相同的默认页（有计划执行页 / 无计划方案页）
            // 宽度恢复（需求 5）：代码查看占半宽打开，关闭后回编排面板默认（会话区 1/3 或用户拖宽）
            orcCol.Width = new GridLength(OrcPanelW());
            var hasPlan = !string.IsNullOrWhiteSpace(s.PlanId);
            if (hasPlan)
                OnOrcTabClick(orcTabExec, new RoutedEventArgs());
            else
                OnOrcTabClick(orcTabPlan, new RoutedEventArgs());
            if (orcTabBar != null)
                orcTabBar.Visibility = hasPlan ? Visibility.Visible : Visibility.Collapsed;
        }
        else if (planRunner != null)
        {
            // 编排执行中：查看页关闭后右栏回“编排执行”页（不收起，执行进度保持可见）；宽度同样恢复默认
            orcPanel.Visibility = Visibility.Visible;
            orcSplit.Visibility = Visibility.Visible;
            orcCol.Width = new GridLength(OrcPanelW());   // 代码查看(半宽)关闭：恢复会话区 1/3 或用户拖宽（需求 5）
            if (orcTabBar != null) orcTabBar.Visibility = Visibility.Visible;
            OnOrcTabClick(orcTabExec, new RoutedEventArgs());
            RefreshOrchGenBtn();   // 非编排会话：隐藏发送区生成按钮
        }
        else
        {
            // 普通收起不写宽度记忆：拖宽只由拖把记录，默认 1/3 由 OrcPanelW 现算（避免代码页半宽被记成默认）
            orcPanel.Visibility = Visibility.Collapsed;
            orcSplit.Visibility = Visibility.Collapsed;
            orcCol.Width = new GridLength(0);
            if (orcTabBar != null) orcTabBar.Visibility = Visibility.Collapsed;
            RefreshOrchGenBtn();   // 与 UpdateOrcPanel 非编排收起分支一致：隐藏发送区生成按钮
            SyncOrcTabs();
        }
        return true;
    }

    /* ================= git 提交详情查看器（右栏“提交详情”页）：左树“项目跟踪”双击变更文件打开，只读展示该提交内单文件 diff ================= */

    /// <summary>打开 git 提交详情浮层（双击左树“项目跟踪”变更文件行触发，最大化铺于会话区/右栏上层，平时不占布局）：
    /// 头部=页签行与 git 信息区（路径/变更徽标/提交短 hash），下方分隔线后=git show 该提交该文件 diff 行流
    /// （逐行旧/新文件行号 + +/- /@@ 着色，可滚动、行文本可拖选、整体可一键复制）。
    /// 与右栏编排面板解耦、与代码查看互斥：打开前若代码页有未保存编辑先三选拦截。后台拉取，seq 防连点/连开乱序。</summary>
    void OpenGitDiff(TreeNode node)
    {
        if (node?.Tag != "git-f" || node.GitHash.Length == 0) return;
        var rel = node.Tip ?? "";
        var nl = rel.IndexOf('\n');
        if (nl >= 0) rel = rel[..nl];   // Tip 是多行（路径\n状态说明…），取首行路径行
        if (gitDiffOpen && ReferenceEquals(gitDiffNode, node))
            return;   // 已在浮层查看同一文件：内容在屏无需动作
        if (codeOpen && !CloseCodeView()) return;   // 代码页有未保存修改：保存/放弃/取消
        var hash = node.GitHash;
        gitDiffNode = node;
        gitDiffOpen = true;
        gitDiffKey = SnapDirKey();          // 打开时刻所属容器键：此后容器切换（键变化）时 UpdateOrcPanel 自动关闭本页
        var seq = ++gitDiffSeq;
        ShowGitFloat();   // 浮层贴项目树右缘定位弹出（宽高随窗口自适应，平时 Collapsed 不占布局）
        gitDiffTitle.Text = rel.Length > 0 ? rel : node.Name;
        gitDiffStatText.Text = node.Name;
        gitDiffMeta.Text = "提交 " + (hash.Length > 7 ? hash[..7] : hash)
            + " · 双击左侧其他变更文件行可切换";
        // 清场后由加载回调重建（AvalonEdit 单文本承载：文本/行底/行号映射一次 Set 到位，防旧内容闪烁）
        EnsureGitDiffEditor();   // 首开时装自绘行号边距与行底渲染器（幂等）
        SetGitDiffRows(null);    // 先清行底/行号映射
        gitDiffEdit.Text = "";
        gitDiffEdit.ScrollToHome();
        var cfg0 = cfg;
        _ = Task.Run(async () =>
        {
            var raw = "";
            try { raw = await GitMgr.FileDiffAsync(cfg0, hash, rel); } catch { }
            _ = Dispatcher.InvokeAsync(() =>
            {
                if (!gitDiffOpen || seq != gitDiffSeq) return;   // 已关闭/已切换其他文件：过期结果丢弃
                // 行解析必须在 UI 线程做（详见 ParseDiffRows 注释）；git show 输出已限 ≤600 行，此处解析开销可忽略
                var rows = ParseDiffRows(raw, "（无 diff 内容：该提交未含此文件或仓库不可用）");
                var sb = new System.Text.StringBuilder();
                foreach (var r in rows) sb.AppendLine(r.Text);
                SetGitDiffRows(rows);          // 行底/双行号映射先行（渲染同帧生效）
                gitDiffEdit.Text = sb.ToString();   // 文档行与 rows 一一对应（含尾空行）
                gitDiffEdit.ScrollToHome();
            }, System.Windows.Threading.DispatcherPriority.Background);
        });
    }

    /// <summary>解析 unified diff 原始文本为着色行流（git 提交详情与会话改动对比共用）。
    /// 行号状态机：@@ 头重置新旧行号游标，其后内容行按标记推进（+ 只进新号 / - 只进旧号 / 空格上下文同进）；
    /// 文件头行（diff/---/+++/index）与 \ No newline 提示行无行号不推进；未遇 @@（异常/空输出）时游标为 0，行号列留空。
    /// 空文本 → 单行 emptyHint 提示。必须在 UI 线程调用：GitDiffRow 携带 Brush（DispatcherObject），
    /// 后台线程构造的未冻结 Brush 供渲染器/着色器在 UI 线程直接绘制，跨线程创建/使用会抛
    /// “无法绑定到不同线程上创建的 DependencySource”致进程崩溃。</summary>
    List<GitDiffRow> ParseDiffRows(string? raw, string emptyHint)
    {
        var rows = new List<GitDiffRow>();
        var lines = (raw ?? "").Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 1 && string.IsNullOrWhiteSpace(lines[0])) lines = Array.Empty<string>();
        int oldCur = 0, newCur = 0;
        foreach (var line in lines)
        {
            var r = DiffRow(line);
            if (line.StartsWith("@@"))
            {
                var m = DiffHunkRe.Match(line);
                if (m.Success)
                {
                    oldCur = int.Parse(m.Groups[1].Value);
                    newCur = int.Parse(m.Groups[2].Value);
                }
            }
            else if (line.StartsWith("+++") || line.StartsWith("---")
                     || line.StartsWith("diff ") || line.StartsWith("index "))
            {
                // 文件头行：无行号
            }
            else if (line.Length > 0 && line[0] == '+')
            {
                if (newCur > 0) r.NewNo = newCur;
                newCur++;
            }
            else if (line.Length > 0 && line[0] == '-')
            {
                if (oldCur > 0) r.OldNo = oldCur;
                oldCur++;
            }
            else if (line.Length > 0 && line[0] == ' ')
            {
                if (oldCur > 0) r.OldNo = oldCur;
                if (newCur > 0) r.NewNo = newCur;
                oldCur++;
                newCur++;
            }
            // 其余（空串/\ No newline 等）：无行号不推进
            rows.Add(r);
        }
        if (rows.Count == 0) rows.Add(new GitDiffRow(emptyHint, null, DiffHeadFg));
        return rows;
    }

    /// <summary>diff 行着色解析：行首 +（新增绿）/ -（删除红）/ @@（区块琥珀）/ diff 头与元信息（灰蓝）；余为上下文默认。</summary>
    static GitDiffRow DiffRow(string line)
    {
        if (line.StartsWith("@@")) return new GitDiffRow(line, null, DiffHunkFg);
        if (line.StartsWith("+++") || line.StartsWith("---") || line.StartsWith("diff ") || line.StartsWith("index "))
            return new GitDiffRow(line, null, DiffHeadFg);
        if (line.StartsWith("+")) return new GitDiffRow(line, DiffAddBg, DiffAddFg);
        if (line.StartsWith("-")) return new GitDiffRow(line, DiffDelBg, DiffDelFg);
        return new GitDiffRow(line);   // 上下文行默认灰白
    }

    /// <summary>hunk 头解析：@@ -旧起[,旧数] +新起[,新数] @@，捕获新旧起始行号供内容行号逐行推进。</summary>
    static readonly Regex DiffHunkRe = new(@"^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@");

    /// <summary>关闭 git 提交详情浮层（✕/切走“项目”页/会话容器切换自动调用）：diff 纯只读无脏数据直接关，
    /// 后台加载中的结果经 seq 校验自动丢弃；与右栏编排面板无关，不动 orcPanel/页签状态。</summary>
    void CloseGitDiff()
    {
        if (!gitDiffOpen && gitDiffNode == null) return;
        gitDiffOpen = false;
        gitDiffNode = null;
        gitDiffSeq++;                     // 后台加载中的 diff 结果随之丢弃
        SetGitDiffRows(null);             // 清行底/行号/前景映射（文本随浮层隐藏，下次打开先清场）
        gitDiffTitle.Text = "";
        gitDiffStatText.Text = "";
        gitDiffMeta.Text = "";
        if (gitFloatPanel != null) gitFloatPanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>提交详情浮层滚轮接管：鼠标落在浮层内任意位置（标题/信息区/行号/文本/滚动条）都统一滚动 diff 内容，
    /// 不再依赖悬停点恰好命中文本区——悬在非文本区时滚轮原本“空转”。滚动幅度与全窗其他接管（对话区/目录树）
    /// 保持一致（Delta/3），内容无可滚或未就绪时也一律吞掉事件，杜绝滚轮穿透到下方被浮层盖住的会话区。
    /// 挂 Border 的 Preview 通道，在 Window 根接管（OnWindowPreviewWheel 按矩形先放行、不置 Handled）之后、
    /// TextArea 原生滚轮之前命中：本方法滚一次并置 Handled，AvalonEdit 不会二次滚动。</summary>
    void OnGitFloatWheel(object sender, MouseWheelEventArgs e)
    {
        // 滚动量在编辑器模板内 ScrollViewer(PART_ScrollViewer) 上，TextArea 不带滚动接口：从模板取之再滚
        if (gitDiffEdit?.Template?.FindName("PART_ScrollViewer", gitDiffEdit) is ScrollViewer sv)
        {
            if (sv.ScrollableHeight > 0)
                sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta / 3.0);
        }
        e.Handled = true;
    }

    /// <summary>提交详情浮层头部 ✕ 关闭按钮点击。</summary>
    void OnGitDiffClose(object sender, RoutedEventArgs e) => CloseGitDiff();

    /// <summary>复制当前 diff 全部行文本（含 + / - 标记的纯文本，不带行号列）。成功 true。</summary>
    bool TryCopyAllDiff()
    {
        if (gitDiffRows == null || gitDiffRows.Count == 0) return false;
        var sb = new System.Text.StringBuilder();
        foreach (var r in gitDiffRows) sb.AppendLine(r.Text);
        try { Clipboard.SetText(sb.ToString()); return true; }
        catch { return false; }
    }

    /// <summary>“查看磁盘文件”按钮点击（浮层右上角）：在资源管理器中定位到当前 diff 对应磁盘文件；
    /// 文件已被提交删除（磁盘不存在）时按钮就地提示“文件已删除”。</summary>
    async void OnGitOpenFile(object sender, RoutedEventArgs e)
    {
        var node = gitDiffNode;
        if (node == null || node.Tag != "git-f") return;
        var rel = node.Tip ?? "";
        var nl = rel.IndexOf('\n');
        if (nl >= 0) rel = rel[..nl];   // Tip 首行=文件相对路径
        var full = System.IO.Path.Combine(cfg.ProjectRoot, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
        if (System.IO.File.Exists(full))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "/select,\"" + full + "\"") { UseShellExecute = true });
            }
            catch { }
            return;
        }
        var btn = sender as System.Windows.Controls.Button;
        if (btn == null) return;
        btn.Content = "文件已删除";
        await Task.Delay(1200);
        if (btn.Content?.ToString() == "文件已删除") btn.Content = "查看磁盘文件";
    }

    /// <summary>gitDiffEdit 右键菜单“复制”：AvalonEdit 原生多行选区复制（亦可用 Ctrl+C）。</summary>
    void OnGitDiffMenuCopy(object sender, RoutedEventArgs e) => gitDiffEdit.Copy();

    /// <summary>gitDiffEdit 右键菜单“全选”。</summary>
    void OnGitDiffMenuSelectAll(object sender, RoutedEventArgs e) => gitDiffEdit.SelectAll();

    /// <summary>gitDiffEdit 右键菜单“复制整个 diff”：全部行文本入剪贴板（失败静默）。</summary>
    void OnGitDiffCopyAll(object sender, RoutedEventArgs e) => TryCopyAllDiff();

    /// <summary>gitDiffEdit 首次显示时把内置 LineNumberMargin 替换为 DiffLineNumberMargin（双列旧/新文件行号 + 行号区同色底），
    /// 并注册 DiffBgRenderer（文本区行底色）；幂等仅装一次，行内容随后经 SetGitDiffRows 逐次更新。</summary>
    void EnsureGitDiffEditor()
    {
        if (gitDiffEdit == null) return;
        if (gitDiffMargin == null)
        {
            var margins = gitDiffEdit.TextArea.LeftMargins;
            for (int i = 0; i < margins.Count; i++)
            {
                if (margins[i] is ICSharpCode.AvalonEdit.Editing.LineNumberMargin)
                {
                    var dim = (FindResource("DimBrush") as Brush) ?? new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xB4));
                    gitDiffMargin = new DiffLineNumberMargin(dim);
                    margins[i] = gitDiffMargin;
                    break;
                }
            }
        }
        if (gitDiffBg == null)
        {
            gitDiffBg = new DiffBgRenderer();
            gitDiffEdit.TextArea.TextView.BackgroundRenderers.Add(gitDiffBg);
        }
        if (gitDiffColorizer == null)
        {
            gitDiffColorizer = new DiffRowColorizer();
            gitDiffEdit.TextArea.TextView.LineTransformers.Add(gitDiffColorizer);
        }
    }

    /// <summary>更新 diff 行模型引用：行底渲染器与行号边距同步（null=清空）。</summary>
    void SetGitDiffRows(List<GitDiffRow>? rows)
    {
        gitDiffRows = rows;
        gitDiffMargin?.SetRows(rows);
        gitDiffBg?.SetRows(rows);
        gitDiffColorizer?.SetRows(rows);
    }

    /// <summary>提交详情浮层弹出：可见并按最大化布局铺于主工作区上层。</summary>
    void ShowGitFloat()
    {
        if (gitFloatPanel == null) return;
        gitFloatPanel.Visibility = Visibility.Visible;
        ReposGitFloat();
    }

    /// <summary>提交详情浮层定位（最大化铺于主工作区上层，不影响后台会话任务）：左缘=项目树右缘+8（树不可见时锚左栏右缘），
    /// 顶部留 8、右/底部各留 10，宽高铺满至窗口边缘；仅露出左栏树便于继续双击切换 diff。显示期间窗口缩放/左栏拖宽时随附调用。</summary>
    void ReposGitFloat()
    {
        if (gitFloatPanel == null || rootGrid == null || !gitDiffOpen) return;
        double x;
        if (panelDir.IsVisible)
        {
            var tl = panelDir.TransformToAncestor(rootGrid).Transform(new Point(0, 0));
            x = tl.X + panelDir.ActualWidth + 8;   // 项目树右缘外 8px 间隙
        }
        else
        {
            x = rootGrid.ColumnDefinitions[0].ActualWidth + 12;   // 树所在项目页不可见（常态已被 ActivateTab 关浮层）
        }
        gitFloatPanel.Margin = new Thickness(x, 8, 0, 0);
        gitFloatPanel.Width = Math.Max(240, rootGrid.ActualWidth - x - 10);    // 铺满至窗口右缘（右侧留白 10）
        // 浮层所在 Grid.Row=1 的顶部在 Row0（标题栏）之下，Margin 顶 8 相对该 cell；
        // 高度须扣除 Row0 高，使底边 = 窗口内容可视底 - 10，不越进底部状态栏/被窗口裁剪
        var titleBarH = rootGrid.RowDefinitions.Count > 0 ? rootGrid.RowDefinitions[0].ActualHeight : 0;
        gitFloatPanel.Height = Math.Max(220, rootGrid.ActualHeight - titleBarH - 8 - 10);  // 铺满内容区（底部留白 10）
    }

    /// <summary>把 codeEdit 内置行号边距替换为 RangeLineNumberMargin（普通行号 + 读取行段金色数字与同系底色突显），并设置本次突显范围。
    /// 幂等：margin 实例只建一次，其后复用并只更新范围；start&lt;=0 表示无读取段（清除突显，仅保留统一的行号样式）。</summary>
    void InstallCodeLineMargin(int start, int end)
    {
        if (codeEdit == null) return;
        if (codeLineMargin == null)
        {
            var dim = (FindResource("DimBrush") as Brush) ?? new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xB4));
            codeLineMargin = new RangeLineNumberMargin(dim);
        }
        SetCodeMargin(codeLineMargin);       // 改动对比视图可能占用着行号区：切回全文视图时换回来
        codeLineMargin.SetFileBase(codeFileLine0);   // 行号随窗口首行偏移：显示与突显均换算为文件真实行号
        codeLineMargin.SetRange(start, end);
    }

    /// <summary>把 codeEdit 的行号区换成指定自绘边距（读取段版 RangeLineNumberMargin ⇄ diff 双列版 DiffLineNumberMargin 互换）。
    /// 行号区只有一个槽位：首装命中原生 LineNumberMargin 原位替换，其后在两种自绘边距之间原位互换（自绘版也派生自 LineNumberMargin，故同样命中）。</summary>
    void SetCodeMargin(ICSharpCode.AvalonEdit.Editing.AbstractMargin m)
    {
        if (codeEdit == null || m == null) return;
        var margins = codeEdit.TextArea.LeftMargins;
        for (int i = 0; i < margins.Count; i++)
        {
            if (margins[i] is ICSharpCode.AvalonEdit.Editing.LineNumberMargin)
            {
                if (!ReferenceEquals(margins[i], m)) margins[i] = m;   // 已在位则不重装（避免 Remove/Add 视觉子项无谓抖动）
                return;
            }
        }
        margins.Insert(0, m);   // 兜底：行号边距已被外部移除时补回（正常路径不会走到）
    }

    /// <summary>惰性装配 codeEdit 的 diff 渲染件（双列行号边距 + 行底色渲染器 + 文本前景着色器）并占住行号区。
    /// 与 git 提交详情浮层同款类型但独立实例：两套行流互不干扰，浮层与代码页可同时存在。</summary>
    void EnsureCodeDiffEditor()
    {
        if (codeEdit == null) return;
        if (codeDiffMargin == null)
        {
            var dim = (FindResource("DimBrush") as Brush) ?? new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xB4));
            codeDiffMargin = new DiffLineNumberMargin(dim);
        }
        codeDiffBg ??= new DiffBgRenderer();
        if (codeDiffColorizer == null)
        {
            codeDiffColorizer = new DiffRowColorizer();
            codeEdit.TextArea.TextView.LineTransformers.Add(codeDiffColorizer);
        }
        SetCodeMargin(codeDiffMargin);
        if (!codeEdit.TextArea.TextView.BackgroundRenderers.Contains(codeDiffBg))
            codeEdit.TextArea.TextView.BackgroundRenderers.Add(codeDiffBg);
    }

    /// <summary>切到“改动对比”视图：codeEdit 装载统一 diff 行流（增行绿底 / 删行红底 / @@ 区块琥珀 / 上下文灰 + 双列行号），
    /// 强制只读并停用编辑与 md 预览；头部副文本显示基线来源与增删行统计。diff 文本为空（内容一致）时给单行说明。</summary>
    void ApplyCodeDiffView()
    {
        if (codeEdit == null || codeDiffVm == null) return;
        codeEditing = false;
        codeDirty = false;
        codeMdPreview = false;
        codeMdSourceText = null;
        if (codeMdView != null) codeMdView.Document = null;
        EnsureCodeDiffEditor();
        var rows = ParseDiffRows(codeDiffText, "（改前与当前内容一致：本会话未改动该文件的文本）");
        codeDiffRows = rows;
        codeDiffMargin?.SetRows(rows);
        codeDiffBg?.SetRows(rows);
        codeDiffColorizer?.SetRows(rows);
        var sb = new StringBuilder();
        foreach (var r in rows) sb.AppendLine(r.Text);
        codeEdit.TextArea.TextView.BackgroundRenderers.Clear();      // 清掉读取段高亮渲染器（diff 行底色重新装回）
        codeEdit.TextArea.TextView.BackgroundRenderers.Add(codeDiffBg!);
        codeEdit.Text = sb.ToString();        // codeEditing=false 已先行：TextChanged 不会误置脏
        codeEdit.IsReadOnly = true;
        codeEdit.ScrollToHome();
        SyncCodeEditUi();
    }

    /// <summary>切回“文件全文”视图：恢复已装载的当前文本与读取段行号边距，diff 着色件摘除（编辑能力按 SyncCodeEditUi 规则复原）。</summary>
    void ApplyCodeFullView()
    {
        if (codeEdit == null) return;
        codeDiffRows = null;
        codeDiffMargin?.SetRows(null);
        codeDiffBg?.SetRows(null);
        codeDiffColorizer?.SetRows(null);
        codeEdit.TextArea.TextView.BackgroundRenderers.Clear();
        codeEdit.Text = codeLoadedText ?? "";
        var rs = codeCard?.StartLine ?? 0;
        var re = codeCard?.EndLine ?? 0;
        // 读取段淡黄高亮复原（对比视图清场时被摘除）：按窗口偏移换算，与载入时一致
        if (rs > 0) codeEdit.TextArea.TextView.BackgroundRenderers.Add(new ReadRangeBgRenderer(rs, re, codeFileLine0));
        // .md 文件切回全文视图恢复格式化预览（进对比视图时被压成源码；缓存置空=下次 Sync 必然重建文档）
        codeMdPreview = !codeEditing && (codeCard?.FilePath ?? "").EndsWith(".md", StringComparison.OrdinalIgnoreCase);
        codeMdSourceText = null;
        InstallCodeLineMargin(rs, re);
        SyncCodeEditUi();
    }

    /// <summary>工具栏“◧ 改动对比 / ☰ 全文”点击：在 diff 着色视图与文件全文视图之间来回切换。
    /// 编辑中且有未保存修改时先走三选拦截（保存/放弃/取消），取消则不切。</summary>
    void OnCodeDiffClick(object sender, RoutedEventArgs e)
    {
        if (codeDiffVm == null) return;
        if (codeDiffText == null) { ShowToast("改动对比数据仍在计算中，请稍候"); return; }   // 后台尚未算完：不切空视图
        if (!GuardCodeDirty("切换改动对比")) return;
        codeDiffOn = !codeDiffOn;
        if (codeDiffOn) ApplyCodeDiffView(); else ApplyCodeFullView();
        // 头部来源徽标与副文本随视图切换：对比视图显示基线说明，全文视图恢复内容来源与读取行段
        if (codeSrcText != null) codeSrcText.Text = codeDiffOn ? "改动对比" : (codeSrcLabel);
        if (codeRange != null) codeRange.Text = codeDiffOn
            ? codeDiffNote
            : (codeCard?.StartLine > 0 ? $"读取 {codeCard.StartLine}~{Math.Max(codeCard.StartLine, codeCard.EndLine)} 行" : "");
        UpdateCodeDiffBtn();
    }

    /// <summary>同步“◧ 改动对比”按钮的可见性与文案：仅从“会话文件变动”行打开时可用（读文件卡入口无基线概念，按钮隐藏）；
    /// 文案随当前视图切换（对比中 → “☰ 全文”，全文中 → “◧ 改动对比”）。</summary>
    void UpdateCodeDiffBtn()
    {
        if (codeDiffBtn == null) return;
        var usable = codeDiffVm != null;
        codeDiffBtn.Visibility = usable ? Visibility.Visible : Visibility.Collapsed;
        if (!usable) return;
        codeDiffBtn.Content = codeDiffOn ? "☰ 全文" : "◧ 改动对比";
        codeDiffBtn.ToolTip = codeDiffOn
            ? "切回文件全文视图（恢复编辑能力）"
            : "查看本会话对该文件的改动：增行绿底 / 删行红底 / 双列行号";
    }

    /// <summary>解析改动行的“改前基线”文本（后台线程调用，含文件 IO；入参均为纯值，不触碰 UI 绑定对象）：
    /// 1) back/ 写前备份（变更日志记下的本会话最早一次备份 = 会话起点状态，最准）；
    /// 2) 会话快照 data/snap（模型 Read 该文件时刻的原码，备份被清理时兜底）；
    /// 3) 都没有 → null（本会话新建文件，整文件按新增展示）。
    /// 返回 (文本, 来源标签)；来源标签直接进头部副文本，让用户知道对比的是哪一版。</summary>
    (string? text, string label) ResolveChangeBaseline(string basePath, bool isNew, string key, string rel)
    {
        // 1) 写前备份：优先用变更记录里的绝对路径；该路径已失效（back/ 只留每文件最近 10 份）则按扁平命名扫目录取时间窗内最早一份
        var bk = basePath ?? "";
        if (bk.Length > 0 && System.IO.File.Exists(bk))
            return (SafeReadAllText(bk), $"写前备份 {System.IO.Path.GetFileName(bk)}");
        var scanned = ScanEarliestBackup(rel);
        if (scanned != null) return (SafeReadAllText(scanned), $"写前备份 {System.IO.Path.GetFileName(scanned)}");
        // 2) 会话快照：读取时刻原码（备份缺失但本会话读过该文件时可得）
        if (!string.IsNullOrEmpty(key))
        {
            var snap = TryReadSnapshotText(key, rel);
            if (snap != null) return (snap, "会话快照（读取时刻原文）");
        }
        // 3) 无基线：新建文件
        return (null, isNew ? "无基线（本会话新建）" : "无基线（备份已清理，按整文件新增展示）");
    }

    /// <summary>按 back/ 扁平命名（项目目录名_yyMMdd_HHmmss）扫描该文件的全部写前备份，返回会话创建之后最早的一份
    /// （= 本会话改动前的状态）；会话时间未知或全部早于会话时退回最新一份（至少能看出“最近一次改了什么”）。
    /// 目录不存在/无备份 → null。</summary>
    string? ScanEarliestBackup(string rel)
    {
        try
        {
            var dir = System.IO.Path.Combine(cfg.ProjectRoot ?? "", "back");
            if (!System.IO.Directory.Exists(dir)) return null;
            // 扁平名规则在 Agent 侧（back/ 由 ChangeJournal 写）：必须走同一函数，两侧命名口径不能各写一份
            var abs = System.IO.Path.GetFullPath(System.IO.Path.Combine(cfg.ProjectRoot ?? "", rel.Replace('/', System.IO.Path.DirectorySeparatorChar)));
            var flat = GAIRR.Core.ChangeJournal.FlatName(abs);
            var hits = new List<string>();
            foreach (var f in System.IO.Directory.GetFiles(dir, flat + "_*"))
                if (System.IO.File.Exists(f)) hits.Add(f);
            if (hits.Count == 0) return null;
            hits.Sort(StringComparer.OrdinalIgnoreCase);   // 文件名尾部为 yyMMdd_HHmmss，字典序即时间序
            var since = currentSession?.Created ?? default;
            if (since != default)
            {
                foreach (var f in hits)                    // 会话创建之后的最早一份 = 本会话改动前状态（留 2 秒容差防时钟/写入抖动）
                {
                    try { if (System.IO.File.GetLastWriteTime(f) >= since.AddSeconds(-2)) return f; }
                    catch { }
                }
            }
            return hits[hits.Count - 1];
        }
        catch { return null; }
    }

    /// <summary>读文件文本（统一 \n 行分隔，解码走与工具卡一致的 Phase1Tools.Decode）；失败返回 null 不抛。</summary>
    static string? SafeReadAllText(string path)
    {
        try { return GAIRR.Core.Phase1Tools.Decode(System.IO.File.ReadAllBytes(path)).Text.Replace("\r\n", "\n"); }
        catch { return null; }
    }

    /// <summary>卸载代码页内容与全部编辑状态（不询问：调用方须先经 GuardCodeDirty 处理未保存修改）；编排容器切换等自动路径也走这里</summary>
    void TeardownCodeView()
    {
        codeOpen = false;
        codeSeq++;                    // 后台加载中的结果经 seq 校验自动丢弃
        codeCard = null;
        codeEditing = false;
        codeDirty = false;
        codeTruncated = false;
        codeFileLine0 = 0;            // 窗口偏移/总行数随卸载复位（下次载入各自重新设置）
        codeFileTotal = 0;
        codeLoadedText = null;
        codeDiskText = null;
        codeMdPreview = false;       // md 预览态随卸载重置：清文档防下次打开闪现旧内容
        codeMdSourceText = null;
        if (codeMdView != null) codeMdView.Document = null;
        // 改动对比态随卸载复位：diff 文本/行模型清空（渲染器按行号取色，残留会串到下次打开的文件），对比按钮一并隐藏
        codeDiffVm = null;
        codeDiffOn = false;
        codeDiffText = null;
        codeDiffNote = "";
        codeDiffRows = null;
        codeDiffMargin?.SetRows(null);
        codeDiffBg?.SetRows(null);
        codeDiffColorizer?.SetRows(null);
        UpdateCodeDiffBtn();
        codeEdit.Text = "";           // codeEditing=false 已先行：TextChanged 不会误置脏
        codeEdit.IsReadOnly = true;
        codeEdit.TextArea.TextView.BackgroundRenderers.Clear();   // 卸载读取段高亮渲染器
        InstallCodeLineMargin(0, 0);   // 行号区同步清除读取段突显（自绘 margin 保留复用）
        codeTitle.Text = "";
        codeSrcText.Text = "";
        codeRange.Text = "";
        SyncCodeEditUi();
    }

    /// <summary>代码页有未保存修改时的三选拦截：是=先保存到磁盘（原文件自动备份，成功才放行）/ 否=放弃修改 / 取消=中止调用方动作。
    /// 非编辑态或无修改直接放行。返回 false=调用方应中止（用户取消或保存失败）。</summary>
    bool GuardCodeDirty(string actionText)
    {
        if (!codeEditing || !codeDirty || codeCard == null) return true;
        var r = MessageBox.Show(this,
            $"「{codeCard.FilePath}」有未保存的修改，{actionText}将丢失这些修改。\n\n" +
            "是(Y)：先保存到磁盘（原文件自动备份到 back/）\n" +
            "否(N)：放弃修改\n" +
            "取消：中止当前操作",
            "未保存的修改", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.No);
        if (r == MessageBoxResult.Yes) return SaveCodeToDisk();
        return r == MessageBoxResult.No;
    }

    /// <summary>在右侧代码查看器打开指定读文件卡的源码：后台按“会话快照→磁盘当前文件→Git HEAD”三层取文本（任一成功即用），
    /// 同时探测磁盘当前文本（供“⤓ 磁盘当前”切换与保存冲突比对）。载入 AvalonEdit 后定位到读取行段首行，
    /// 行段范围由 ReadRangeBgRenderer 淡黄底+金色左缘突显。seq 防抖：加载期间再次打开/关闭则丢弃过期结果；
    /// 新开卡片前若正编辑旧卡且有未保存修改，先三选拦截（保存/放弃/取消）。
    /// change 非空 = 从左栏“会话文件变动”行打开：默认进改动对比视图（基线 vs 当前的着色 diff），并显示“◧ 改动对比”切换钮。</summary>
    void OpenCodeViewer(ToolCall card, ChangedFileVm? change = null)
    {
        var rel = card.FilePath;
        if (string.IsNullOrEmpty(rel)) return;
        if (gitDiffOpen) CloseGitDiff();   // 提交详情浮层与代码查看互斥：开代码页前先收浮层（diff 只读无脏数据，直接关）
        if (ctxOpen) TeardownCtxView();    // 请求上下文页与代码查看互斥：ctx 只读直接卸载（SyncOrcTabs 随后切到代码分支）
        if (codeCard == card && codeOpen)
        {
            // 重复点同一入口：内容已在查看中，避免清场重载闪烁；但改动行再点一次要回到对比视图（用户可能刚切去看全文）
            if (change != null && !codeDiffOn) { codeDiffOn = true; ApplyCodeDiffView(); UpdateCodeDiffBtn(); }
            return;
        }
        if (!GuardCodeDirty("打开其他文件")) return;   // 当前卡片编辑未保存：保存/放弃后继续，取消则中止
        codeCard = card;
        codeOpen = true;
        codeKey = SnapDirKey();          // 打开时刻所属容器键：此后容器切换（键变化）时 UpdateOrcPanel 自动关闭本页
        var key = codeKey;               // 局部快照：后台线程读取不受后续字段变更影响
        var seq = ++codeSeq;
        var start = card.StartLine;
        var end = card.EndLine;
        // 改动对比入口状态：先落 vm 与视图开关（按钮立即可见），基线与 diff 文本后台算完再回填
        codeDiffVm = change;
        codeDiffOn = change != null;
        codeDiffText = null;
        codeDiffNote = "";
        codeDiffRows = null;
        UpdateCodeDiffBtn();
        // 展开右栏并切到“代码查看”页（普通会话也借编排面板宿主显示，页签条只留代码查看一项）；弹出宽度=会话区 50%
        var stickBottom = IsMsgAtBottom();   // 展开前记贴底状态：会话区将变窄、消息重新折行增高，原贴底需在布局稳定后补滚
        orcPanel.Visibility = Visibility.Visible;
        orcSplit.Visibility = Visibility.Visible;
        SetOrcHalfWidth();
        if (orcTabBar != null) orcTabBar.Visibility = Visibility.Visible;
        SyncOrcTabs();
        if (stickBottom) ScrollToBottomAfterLayout();   // 折行增高布局完成后再滚底：否则视口停在半途，观感为会话区回跳到上方
        codeTitle.Text = rel;
        codeSrcText.Text = "读取中…";
        codeRange.Text = start > 0 ? $"读取 {start}~{Math.Max(start, end)} 行" : "";
        // 内容替换前清场（保留 codeOpen/codeCard/codeKey）：退出旧编辑态、清空旧文本与行段渲染器
        codeEditing = false;
        codeDirty = false;
        codeTruncated = false;
        codeLoadedText = null;
        codeDiskText = null;
        codeMdPreview = false;       // 清场复位（上个文件可能为 .md）：Sync 出口 ApplyCodeMdLayout 不会误显旧预览
        codeMdSourceText = null;
        if (codeMdView != null) codeMdView.Document = null;
        codeEdit.Text = "";
        codeEdit.IsReadOnly = true;
        codeEdit.TextArea.TextView.BackgroundRenderers.Clear();
        codeDiffMargin?.SetRows(null);      // 上一个文件的 diff 行模型一律清空：渲染器/着色器按行号取色，残留会串色
        codeDiffBg?.SetRows(null);
        codeDiffColorizer?.SetRows(null);
        SyncCodeEditUi();
        // 改动对比入口的基线信息先取成局部量：ChangedFileVm 是 UI 绑定对象，后台线程只拿纯值不碰它
        var chgBase = change?.BasePath ?? "";
        var chgNew = change?.IsNew ?? false;
        var wantDiff = change != null;
        // 后台取源码（文件 IO 与 git 进程不进 UI 线程）
        Task.Run(() =>
        {
            string? text = null;
            string? disk = null;
            var src = "快照";
            if (!string.IsNullOrEmpty(key)) text = TryReadSnapshotText(key, rel);
            if (text == null) { src = "磁盘当前文件"; text = TryReadDiskText(rel); }
            if (text == null) { src = "Git HEAD"; text = TryReadGitText(rel); }
            if (src != "磁盘当前文件") disk = TryReadDiskText(rel);   // 快照/Git 层成功时另读磁盘：供保存冲突比对与磁盘版切换
            string? diffText = null;
            var note = "";
            if (wantDiff)
            {
                // 改动对比：展示与对比一律以“当前”为准（磁盘优先，快照/Git 仅兜底），基线取写前备份 → 会话快照 → 空（新建）
                var cur = disk ?? text;
                if (disk != null) { text = disk; src = "磁盘当前文件"; }
                var (baseText, baseLabel) = ResolveChangeBaseline(chgBase, chgNew, key ?? "", rel);
                diffText = CodeDiff.Unified(baseText, cur, out var added, out var removed);
                note = $"基线：{baseLabel} · 本次 +{added} −{removed} 行";
            }
            return (text ?? "", src, disk, diffText, note);
        }).ContinueWith(t =>
        {
            if (t.IsFaulted || !codeOpen || seq != codeSeq) return;   // 已关闭/已被新打开覆盖：过期结果直接丢弃
            var (text, src, diskText, diffText, diffNote) = t.Result;
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var total = lines.Length;
            while (total > 1 && lines[total - 1].Length == 0) total--;   // 文件尾换行产生的空串不计为行（行号与文件一致）
            codeSrcText.Text = src;
            codeSrcLabel = src;              // 记住来源原文：从“改动对比”切回“☰ 全文”时复原徽标
            codeDiskText = diskText;
            codeEditing = false;
            codeDirty = false;
            codeEdit.IsReadOnly = true;
            codeEdit.SyntaxHighlighting = CodeHighlightFor(rel);   // 按扩展名高亮（与主编辑窗同映射）
            // 窗口化载入：只取“读取段 ±CodeWinPad”窗口（上下不足取文件头/尾），替代旧“整文件+前 8000 行截断”——
            // 大文件读取段在 8000 行之后也能看到内容并高亮定位；窗口未覆盖全文（部分载入）禁止整文件覆盖式编辑
            var winStart = 1;
            var winEnd = total;
            if (start > 0)
            {
                winStart = Math.Max(1, start - CodeWinPad);
                winEnd = Math.Min(total, Math.Max(start, end) + CodeWinPad);
            }
            else if (total > 8000)
            {
                winEnd = 8000;   // 无读取段的全量查看：保留渲染上限（仅前 8000 行），partial 判定禁编辑
            }
            var line0 = winStart - 1;                       // 窗口偏移：编辑器首行对应文件行号-1
            var winRows = winEnd - winStart + 1;            // 窗口行数（start≤total ⇒ winStart≤winEnd 恒成立）
            var partial = winStart > 1 || winEnd < total;   // 未覆盖全文：整文件覆盖式保存会丢窗口外行，禁编辑
            codeTruncated = partial;
            codeFileLine0 = line0;
            codeFileTotal = total;
            codeLoadedText = winRows > 0 ? string.Join("\n", lines, line0, winRows) : "";
            codeRange.Text = (start > 0 ? $"读取 {start}~{Math.Max(start, end)} 行 · " : "")
                + $"显示 {winStart}~{winEnd} 行（文件共 {total} 行" + (partial ? "，部分载入禁止编辑）" : "）");
            codeEdit.Text = codeLoadedText;   // 程序载入：codeEditing=false 已先行，TextChanged 不会误置脏
            // .md 文件默认格式化预览（编辑态由 ApplyCodeMdLayout 自动回落源码视图）；缓存置空=首次 Sync 必然重建文档
            codeMdPreview = rel.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
            codeMdSourceText = null;
            codeEdit.TextArea.TextView.BackgroundRenderers.Clear();
            if (start > 0)
                codeEdit.TextArea.TextView.BackgroundRenderers.Add(new ReadRangeBgRenderer(start, end, line0));
            InstallCodeLineMargin(start, end);   // 行号区同步突显读取行段（金色行号 + 同系底色，与代码区 ReadRangeBgRenderer 配套）
            SyncCodeEditUi();
            // 定位到读取段首行（命中行段已淡黄高亮）：窗口内行号 = 文件行 - 窗口偏移
            if (start > 0 && codeEdit.Document.LineCount > 0)
            {
                var ln = Math.Min(start - line0, codeEdit.Document.LineCount);
                codeEdit.TextArea.Caret.Line = ln;
                codeEdit.ScrollToLine(ln);
            }
            // 改动对比入口：diff 文本回填后切到着色对比视图，头部来源徽标与副文本换成基线说明 + 增删行统计
            codeDiffText = diffText;
            codeDiffNote = diffNote;
            if (codeDiffOn)
            {
                codeSrcText.Text = "改动对比";
                codeRange.Text = diffNote;
                ApplyCodeDiffView();
            }
            UpdateCodeDiffBtn();
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion,
           System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>代码页工具栏与编辑器状态同步：查看态（✎ 编辑 / ⤓ 磁盘当前）⇄ 编辑态（💾 保存 / ✕ 放弃）。
    /// 任务运行中或部分载入（窗口化未覆盖全文 / 磁盘超限截断）文件禁用“✎ 编辑”（只读查看；点击入口另有
    /// IsTaskRunning/codeTruncated 兜底拦截）；保存按钮仅在真有修改时可点。SyncOrcTabs/载入/启动/关闭等路径统一调此方法刷新。</summary>
    void SyncCodeEditUi()
    {
        if (codeEdit == null || codeEditBtn == null || codeSaveBtn == null || codeUseDiskBtn == null) return;
        if (!codeOpen || codeCard == null)
        {
            codeEdit.IsReadOnly = true;
            codeEditBtn.Visibility = Visibility.Collapsed;
            codeSaveBtn.Visibility = Visibility.Collapsed;
            codeUseDiskBtn.Visibility = Visibility.Collapsed;
            ApplyCodeMdLayout();
            return;
        }
        if (codeDiffOn && codeDiffVm != null)
        {
            // 改动对比视图：codeEdit 装的是 diff 行流而非文件内容，编辑/保存/磁盘切换/md 预览一律停用
            //（保存会把 diff 文本写回源文件，必须堵死）；切回“☰ 全文”后按下方常规规则复原
            codeEditing = false;
            codeDirty = false;
            codeEdit.IsReadOnly = true;
            codeEditBtn.Visibility = Visibility.Collapsed;
            codeSaveBtn.Visibility = Visibility.Collapsed;
            codeUseDiskBtn.Visibility = Visibility.Collapsed;
            codeEdit.Visibility = Visibility.Visible;
            if (codeMdView != null) codeMdView.Visibility = Visibility.Collapsed;
            return;
        }
        codeEditBtn.Visibility = Visibility.Visible;
        if (codeEditing)
        {
            codeEdit.IsReadOnly = false;
            codeEditBtn.Content = "✕ 放弃";
            codeEditBtn.IsEnabled = true;
            codeEditBtn.ToolTip = "放弃未保存的修改，退出编辑模式（回到只读查看）";
            codeSaveBtn.Visibility = Visibility.Visible;
            codeSaveBtn.IsEnabled = codeDirty;
            codeUseDiskBtn.Visibility = Visibility.Collapsed;
        }
        else
        {
            codeEdit.IsReadOnly = true;
            codeEditBtn.Content = "✎ 编辑";
            // 部分载入态是内容属性（载入时同步设置）可安全禁用；运行态不置灰（任务结束无统一刷新点会永久禁）——
            // 运行中拦截放在 OnCodeEditClick 入口 toast，按钮始终可点无卡死
            codeEditBtn.IsEnabled = !codeTruncated;
            codeEditBtn.ToolTip = codeTruncated
                ? "当前按读取行段显示文件部分内容：整文件保存会丢失窗口外行，已禁止编辑（超大文件请在外部编辑器修改）"
                : "进入编辑模式（任务运行中禁止编辑）";
            codeSaveBtn.Visibility = Visibility.Collapsed;
            codeSaveBtn.IsEnabled = false;
            // 所见内容与磁盘不一致（快照/Git 源）且磁盘有该文件：提供“载入磁盘当前版本并直接进入编辑”入口
            codeUseDiskBtn.Visibility =
                (codeDiskText != null && codeLoadedText != null && codeDiskText != codeLoadedText)
                    ? Visibility.Visible : Visibility.Collapsed;
        }
        ApplyCodeMdLayout();   // 出口统一：md 查看态 ⇄ 编辑态切换后同步视图（codeMdView 格式化预览 / codeEdit 源码）
    }

    /// <summary>md 格式化预览布局同步：.md 文件在只读查看态时由 codeMdView（FlowDocument 渲染）代替 codeEdit 纯文本源码视图。
    /// 缓存比对重建：编辑中/放弃/保存后 codeEdit.Text 变化，下次回到预览态时自动刷新文档；编辑态恒显示源码视图。</summary>
    void ApplyCodeMdLayout()
    {
        if (codeMdView == null) return;
        // 改动对比视图独占 codeEdit（装的是 diff 行流，不是 md 源码）：预览一律不参与，切回全文后按扩展名重新判定
        if (codeDiffOn && codeDiffVm != null)
        {
            codeMdView.Visibility = Visibility.Collapsed;
            codeEdit.Visibility = Visibility.Visible;
            return;
        }
        var preview = codeMdPreview && !codeEditing && codeOpen && codeCard != null;
        if (preview && codeMdSourceText != codeEdit.Text)
        {
            codeMdSourceText = codeEdit.Text;
            codeMdView.Document = Markdown.ToDoc(codeMdSourceText ?? "", "Microsoft YaHei, SimSun, Segoe UI");
        }
        codeMdView.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
        codeEdit.Visibility = preview ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>✎ 编辑 / ✕ 放弃 按钮：查看态点击进入编辑（运行中/部分载入文件拦截）；编辑态点击=放弃未保存修改退出。</summary>
    void OnCodeEditClick(object sender, RoutedEventArgs e)
    {
        if (codeCard == null || !codeOpen) return;
        if (codeDiffOn)
        {
            // 对比视图里 codeEdit 装的是 diff 行流而非文件内容：编辑入口直接堵死，先切回全文视图
            ShowToast("当前为改动对比视图，点“☰ 全文”切回文件内容后再编辑");
            return;
        }
        if (IsTaskRunning())
        {
            ShowToast("任务运行中，代码页只读查看（避免编辑与 Agent 写入冲突），任务结束后可再编辑");
            return;
        }
        if (!codeEditing)
        {
            if (codeTruncated)
            {
                ShowToast("当前按读取行段显示文件部分内容，已禁止编辑：整文件保存会丢失窗口外行，超大文件请在外部编辑器修改");
                return;
            }
            codeEditing = true;
            codeDirty = false;
            codeEdit.IsReadOnly = false;
            SyncCodeEditUi();
            codeEdit.Focus();
            return;
        }
        // 编辑态再点 = 放弃未保存修改退出（回到只读查看）
        if (codeDirty)
        {
            var r = MessageBox.Show(this,
                $"放弃「{codeCard.FilePath}」未保存的修改？\n\n[是]=放弃并退出编辑  [否]=继续编辑",
                "放弃修改", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (r != MessageBoxResult.Yes) return;
        }
        codeEditing = false;
        codeDirty = false;
        codeEdit.IsReadOnly = true;
        if (codeLoadedText != null && codeEdit.Text != codeLoadedText)
            codeEdit.Text = codeLoadedText;   // 还原到基线（放弃的修改从编辑器移除，TextChanged 见 codeEditing=false 不置脏）
        SyncCodeEditUi();
        ShowToast("已退出编辑模式");
    }

    /// <summary>编辑器内容变化：仅编辑态参与脏标记（程序载入/还原前 codeEditing 已置 false，不会误报）。</summary>
    void OnCodeEditTextChanged(object sender, EventArgs e)
    {
        if (!codeEditing || codeLoadedText == null || codeCard == null) return;
        var t = codeEdit.Text;
        codeDirty = t.Length != codeLoadedText.Length || t != codeLoadedText;
        if (codeSaveBtn != null && codeSaveBtn.Visibility == Visibility.Visible)
            codeSaveBtn.IsEnabled = codeDirty;
    }

    /// <summary>💾 保存 按钮点击：走“保存五步”写回磁盘（锁→备份→UTF-8 无 BOM→换行符规范化→索引同步），
    /// 写前与磁盘当前强确认：载入后磁盘被外部改动（或文件被删/被新建）→ 三选：覆盖保存 / 取消 / 改为按磁盘当前编辑。</summary>
    void OnCodeSaveClick(object sender, RoutedEventArgs e) => SaveCodeToDisk();

    /// <summary>执行保存五步并维护编辑基线；返回 true=已写入（或无需写入）。内部强确认逻辑见方法注释。</summary>
    bool SaveCodeToDisk()
    {
        if (codeCard == null || !codeEditing) return false;
        var rel = codeCard.FilePath;
        if (string.IsNullOrEmpty(rel)) return false;
        // 文本与基线一致 = 没有实际修改：无需写盘（保存按钮仅在 dirty 时可点，此处兜底）
        if (codeLoadedText != null && codeEdit.Text == codeLoadedText)
        {
            ShowToast("没有需要保存的修改");
            return false;
        }
        string rootFull;
        string path;
        try
        {
            rootFull = System.IO.Path.GetFullPath(cfg.ProjectRoot ?? "");
            path = System.IO.Path.GetFullPath(System.IO.Path.Combine(rootFull, rel.Replace('/', System.IO.Path.DirectorySeparatorChar)));
            if (!path.StartsWith(rootFull + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                ShowToast("保存失败：路径超出项目根目录范围");
                return false;
            }
        }
        catch (Exception ex)
        {
            ShowToast("保存失败：路径无效（" + ex.Message + "）");
            return false;
        }
        var current = codeEdit.Text;   // AvalonEdit 文本恒为 \n 行分隔
        var diskNow = TryReadDiskText(rel);   // 磁盘当前（\n 归一）；null=不存在/不可读
        var fileExists = diskNow != null || System.IO.File.Exists(path);
        if (!fileExists)
        {
            // 磁盘无此文件（新建场景：源为快照/Git 或文件被删）：明确告知将新建
            if (MessageBox.Show(this,
                    $"磁盘上不存在「{rel}」，保存将新建该文件。\n\n[是]=保存新建  [否]=取消",
                    "新建文件确认", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes)
                != MessageBoxResult.Yes) return false;
        }
        else if (codeDiskText != null && diskNow != codeDiskText)
        {
            // 强确认：载入后磁盘内容被外部改动（Agent/他人/工具），直接覆盖会丢这些改动
            var r = MessageBox.Show(this,
                $"磁盘上的「{rel}」在载入后被外部修改，与当前编辑所基于的版本不一致。\n\n" +
                "[是]=覆盖磁盘上的新内容，按当前编辑保存\n" +
                "[否]=取消保存，保留编辑器内容\n" +
                "[取消]=改为载入磁盘当前版本，并直接进入编辑（丢弃本次修改）",
                "磁盘内容已被修改", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.No);
            if (r == MessageBoxResult.No) return false;
            if (r == MessageBoxResult.Cancel)
            {
                LoadDiskIntoEditor();   // 用户选择跟随磁盘：载入磁盘版并进入编辑
                return false;
            }
        }
        else if (codeDiskText == null && diskNow != null)
        {
            // 载入时磁盘无文件，现在出现了：同上按外部改动强确认（重建/覆盖语义）
            var r = MessageBox.Show(this,
                $"载入时磁盘上不存在「{rel}」，但当前磁盘已有该文件（可能由 Agent 或其他会话新建）。\n\n" +
                "[是]=覆盖磁盘上的新文件，按当前编辑保存\n" +
                "[否]=取消保存\n" +
                "[取消]=改为载入磁盘当前版本，并直接进入编辑",
                "磁盘内容已被修改", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.No);
            if (r == MessageBoxResult.No) return false;
            if (r == MessageBoxResult.Cancel)
            {
                LoadDiskIntoEditor();
                return false;
            }
        }
        // ---- 保存五步：与 Agent Write 工具同链路（FileLock → 备份 → UTF-8 无 BOM → 换行符规范化 → 索引同步） ----
        var content = GAIRR.Core.Phase2Tools.NormalizeLineEnding(path, current, "auto");
        try
        {
            var mutex = FileLock.For(path);
            if (!mutex.WaitOne(TimeSpan.FromSeconds(10)))
            {
                ShowToast("文件正被其他会话/进程占用（等待 10 秒超时），请稍后重试");
                return false;
            }
            try
            {
                var backup = journal.BackupBeforeWrite(path);
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                System.IO.File.WriteAllText(path, content, new UTF8Encoding(false));
                journal.Log("Write", path, backup, "UI 代码查看编辑保存：" + rel);
                IndexGuardian.NotifyFile(rootFull, rel);
                IndexGuardian.EnsureStarted(rootFull);
                // 保存成功：磁盘基线/编辑基线对齐当前内容（保存后留在编辑态可继续改，dirty 由 TextChanged 重新标记）
                codeDiskText = content.Replace("\r\n", "\n");
                codeLoadedText = current;
                codeDirty = false;
                codeSrcText.Text = "磁盘当前文件";
                codeSrcLabel = "磁盘当前文件";
                var bak = System.IO.Path.GetFileName(backup);
                ShowToast("已保存 " + rel + (bak.Length > 0 ? $"，原文件已备份到 back/{bak}" : "（新建文件）"));
                SyncCodeEditUi();   // 保存后刷新按钮态（dirty 已清 → 保存钮不可点）；GuardCodeDirty/窗口关闭等调用方同样受益
                return true;
            }
            finally { mutex.ReleaseMutex(); }
        }
        catch (Exception ex)
        {
            ShowToast("保存失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>按扩展名取语法高亮定义（与主编辑窗 OpenEditor 同一映射；无匹配返回 null 纯文本）。</summary>
    ICSharpCode.AvalonEdit.Highlighting.IHighlightingDefinition? CodeHighlightFor(string rel)
    {
        var ext = System.IO.Path.GetExtension(rel).ToLowerInvariant();
        var mgr = ICSharpCode.AvalonEdit.Highlighting.HighlightingManager.Instance;
        return DarkSyntax.Adapt(ext switch
        {
            ".cs" => mgr.GetDefinition("C#"),
            ".xaml" or ".xml" => mgr.GetDefinition("XML"),
            ".json" => mgr.GetDefinition("Json"),
            ".md" => mgr.GetDefinition("MarkDown"),
            ".js" => mgr.GetDefinition("JavaScript"),
            ".ts" => mgr.GetDefinition("TypeScript"),
            ".html" => mgr.GetDefinition("HTML"),
            ".css" => mgr.GetDefinition("CSS"),
            ".py" => mgr.GetDefinition("Python"),
            ".ini" => mgr.GetDefinition("Ini"),
            _ => null
        });
    }

    /// <summary>⤓ 磁盘当前 按钮：重新读磁盘并载入为编辑基线（丢弃快照/Git 显示内容），直接进入编辑模式。</summary>
    void OnCodeUseDiskClick(object sender, RoutedEventArgs e)
    {
        if (codeCard == null || !codeOpen) return;
        if (IsTaskRunning())
        {
            ShowToast("任务运行中，代码页只读查看，任务结束后再操作");
            return;
        }
        LoadDiskIntoEditor();
    }

    /// <summary>把磁盘当前文本载入编辑器（换行符按磁盘原文 \n 归一）：从文件第 1 行全量载入（窗口偏移 0，行号=文件行号）。
    /// 磁盘超 8000 行时因渲染上限仅载前 8000 行并保持只读（禁止整盘覆盖式保存丢尾部，提示超大文件用外部编辑器）；否则进入编辑模式。</summary>
    void LoadDiskIntoEditor()
    {
        if (codeCard == null) return;
        var rel = codeCard.FilePath;
        var disk = TryReadDiskText(rel);
        if (disk == null)
        {
            ShowToast("磁盘上没有可载入的文件：" + rel);
            return;
        }
        var start = codeCard.StartLine;
        var end = codeCard.EndLine;
        var lines = disk.Replace("\r\n", "\n").Split('\n');
        var total = lines.Length;
        while (total > 1 && lines[total - 1].Length == 0) total--;
        var truncated = total > 8000;   // 磁盘全量渲染上限：防 AvalonEdit 超大文件卡顿
        var n = truncated ? 8000 : total;
        codeEditing = false;   // 程序载入前退出编辑态：TextChanged 不误置脏
        codeTruncated = truncated;
        codeFileLine0 = 0;              // 磁盘版从文件第 1 行载入（无窗口偏移）
        codeFileTotal = total;
        codeDiskText = disk;
        codeLoadedText = string.Join("\n", lines, 0, n);
        codeEdit.IsReadOnly = true;
        codeEdit.SyntaxHighlighting = CodeHighlightFor(rel);   // 按扩展名高亮（与主编辑窗同映射）
        codeEdit.Text = codeLoadedText;
        codeEdit.TextArea.TextView.BackgroundRenderers.Clear();
        if (start > 0)
            codeEdit.TextArea.TextView.BackgroundRenderers.Add(new ReadRangeBgRenderer(start, end));
        InstallCodeLineMargin(start, end);   // 行号区同步突显读取行段（与代码区 ReadRangeBgRenderer 配套）
        codeSrcText.Text = "磁盘当前文件";
        codeSrcLabel = "磁盘当前文件";
        codeRange.Text = (start > 0 ? $"读取 {start}~{Math.Max(start, end)} 行 · " : "")
            + (truncated ? "磁盘文件超 8000 行，仅显示前 8000 行，禁止编辑（超大文件请在外部编辑器修改）"
                         : $"已载入磁盘当前版本（共 {total} 行）");
        if (truncated)
        {
            SyncCodeEditUi();   // 截断态保持只读查看：禁止整盘覆盖式保存丢尾部
            ShowToast("磁盘文件超 8000 行，仅载入前 8000 行显示且禁止编辑，超大文件请在外部编辑器修改");
            return;
        }
        codeEditing = true;   // 进入编辑（磁盘版载入即编辑态：保存目标明确为磁盘）
        codeDirty = false;
        codeEdit.IsReadOnly = false;
        SyncCodeEditUi();
        codeEdit.Focus();
        ShowToast("已载入磁盘当前版本，可直接编辑后保存");
    }

    /// <summary>任务启动前拦截：代码页编辑中且有未保存修改 → 提示先保存/放弃（任务运行中禁止编辑，避免 UI 与 Agent 写盘竞争）。
    /// 返回 false=已拦截，调用方应中止启动。挂在主任务发送/方案生成/计划执行三个启动收口点。</summary>
    bool EnsureNoCodeEditingBeforeRun()
    {
        if (!codeEditing || !codeDirty || codeCard == null) return true;
        MessageBox.Show(this,
            $"代码查看页正在编辑「{codeCard.FilePath}」且尚未保存。任务运行期间禁止编辑，\n" +
            "请先点 💾 保存或 ✕ 放弃修改后再启动任务。",
            "未保存的编辑", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    /// <summary>读磁盘当前文件文本（相对项目根、正斜杠；与 Read 工具同一解码与统一 \n 行分隔；不存在/解码失败返回 null）</summary>
    string? TryReadDiskText(string rel)
    {
        try
        {
            var abs = System.IO.Path.GetFullPath(System.IO.Path.Combine(cfg.ProjectRoot ?? "", rel.Replace('/', System.IO.Path.DirectorySeparatorChar)));
            if (!System.IO.File.Exists(abs)) return null;
            return GAIRR.Core.Phase1Tools.Decode(System.IO.File.ReadAllBytes(abs)).Text.Replace("\r\n", "\n");
        }
        catch { return null; }
    }

    /// <summary>读 Git HEAD 版本文本（快照与磁盘均不可用时的兜底：git show HEAD:rel）。
    /// 输出按原始字节经同一解码器解码，中文等非 UTF-8 编码文件不乱码；非仓库/无该文件/命令失败返回 null。</summary>
    string? TryReadGitText(string rel)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = cfg.ProjectRoot ?? "",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("show");
            psi.ArgumentList.Add("HEAD:" + rel);
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return null;
            using var ms = new System.IO.MemoryStream();
            p.StandardOutput.BaseStream.CopyTo(ms);
            _ = p.StandardError.ReadToEnd();   // 排空错误流防缓冲满死锁（成败只看退出码）
            p.WaitForExit();
            if (p.ExitCode != 0) return null;
            return GAIRR.Core.Phase1Tools.Decode(ms.ToArray()).Text.Replace("\r\n", "\n");
        }
        catch { return null; }
    }

    /// <summary>右侧编排面板 Tab 切换：编排方案 / 编排执行 / 代码查看（三页共用宿主区互斥；
    /// 尾部 SyncOrcTabs 统一页签样式）。切走查看页不销毁其内容（回切即见），仅置关闭标志（后台加载经 seq 自然失效）。
    /// git 提交详情已浮层化，不再占用右侧页签；浮层打开期间点这些页签不影响浮层。</summary>
    void OnOrcTabClick(object sender, RoutedEventArgs e)
    {
        if (sender == orcTabPlan)
        {
            codeOpen = false;
            if (ctxOpen) TeardownCtxView();   // 切回编排页：ctx 内容随页签隐藏一并卸载
            orcTabPlan.Tag = "cur"; orcTabExec.Tag = null;
            orcPlanContent.Visibility = Visibility.Visible;
            treeOrchExec.Visibility = Visibility.Collapsed;
        }
        else if (sender == orcTabExec)
        {
            codeOpen = false;
            if (ctxOpen) TeardownCtxView();   // 切回编排页：ctx 内容随页签隐藏一并卸载
            orcTabExec.Tag = "cur"; orcTabPlan.Tag = null;
            orcPlanContent.Visibility = Visibility.Collapsed;
            treeOrchExec.Visibility = Visibility.Visible;
            LoadOrchExecTree();
        }
        else if (sender == orcTabCode)
        {
            // 代码查看页回切：内容在首次打开时已加载且切页未清空（codeEdit.Text/编辑状态保留），仅做页签/内容区同步
            if (codeCard == null) return;
            codeOpen = true;
        }
        SyncOrcTabs();
    }

    /// <summary>编排执行树右键菜单打开：记录命中节点、计划 id 与命中子节点 id，并控制菜单项可见性。</summary>
    void OnOrchExecMenuOpening(object sender, ContextMenuEventArgs e)
    {
        archMenuNode = OrchExecNodeAtMouse();
        // 右键先选中命中节点（高亮），再弹菜单：用户先看清操作对象；菜单项状态随后按节点组装
        if (archMenuNode != null) SelectTreeItem(archMenuNode);
        // PlanId 已统一为整个计划 id；NodeRef 为分支（分组/叶子）自身 id，根节点为 null
        selPlanId = archMenuNode?.Kind == "plan" ? archMenuNode.PlanId : null;
        selLeafId = archMenuNode?.Kind == "plan" ? archMenuNode.NodeRef : null;
        // syncPlanForLeaf 决策：开始/暂停/停止/删分支针对 selLeafId 对应分支，整体执行用 selPlanId
    }

    /// <summary>返回编排树中给定节点的顶层根节点（整个计划的投影根），用于区分“命中计划根 vs 命中子节点”。</summary>
    ArchNode? TopOrchRoot(ArchNode n)
    {
        var cur = n;
        while (true)
        {
            ArchNode? parent = null;
            foreach (ArchNode x in treeOrchExec.ItemsSource is List<ArchNode> rs ? rs : new List<ArchNode>())
            {
                if (FindParent(x, cur) != null) { parent = x; break; }
            }
            if (parent == null) return cur;
            cur = parent;
        }
    }

    ArchNode? FindParent(ArchNode root, ArchNode child)
    {
        if (root.Children.Contains(child)) return root;
        foreach (var c in root.Children)
        {
            var found = FindParent(c, child);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>编排执行树右键菜单已打开：按命中节点类型 + 计划/叶子状态控制菜单项显隐。</summary>
    void OnOrchExecMenuOpened(object sender, RoutedEventArgs e)
    {
 foreach (var it in new[] { menuOrchPlanRepair, menuOrchPlanRelink, menuOrchPlanStart, menuOrchPlanResume, menuOrchPlanStop,
 menuOrchPlanPass, menuOrchPlanSkip, menuOrchPlanUndo, menuOrchPlanDelete })
 it.Visibility = Visibility.Collapsed;
        if (archMenuNode?.Kind != "plan" || selPlanId == null) return;
        var pr = GAIRR.AgentHost.PlanStore.GetById(cfg.ProjectRoot, selPlanId);
        var planStatus = pr?.Status;
        var isPaused = planStatus == "paused";

        // ---- 执行控制（分层语义）：叶子右键=开始/继续"本叶子"（单叶范围；失败重置配额重跑、中断残留续跑）；
        // 分组右键=自动遍历续跑本分支子树；根右键=整树执行。执行中仅 owner 可"停止"（作用整个执行器）。
        // 点击入口 OnPlanStartClick/OnPlanResumeClick 按 archMenuNode 命中对象与状态把 scopeId 传给 StartPlanExecution。
        var isLeafHit = archMenuNode.NodeType == 1 && selLeafId != null;
        var isRootHit = archMenuNode.NodeType == 0 && selLeafId == null;
        // runnerAlive 判定沿用下方裁决区统一变量（7647）；此处直接以 planRunner 判空表达
        if (planRunner != null && planStatus == "running")
        {
            menuOrchPlanStop.Visibility = Visibility.Visible;   // 执行中：只给 owner 停止入口
        }
        else if (planRunner != null && isPaused)
        {
            // 暂停的活跃实例（owner）：停止 + 继续（恢复执行器，作用整个计划执行；叶子命中仍以全局暂停为准）
            menuOrchPlanStop.Visibility = Visibility.Visible;
            menuOrchPlanResume.Visibility = Visibility.Visible;
            menuOrchPlanResume.Header = "继续执行";
        }
        else if (isLeafHit && planStatus is not ("running" or "pending" or "pendingConfirm"))
        {
            // 无活跃实例下叶子级开始/继续：按该叶单叶范围执行（failed 重置配额 / running·reviewing 残留续跑 /
            // pending 未开始=开始本叶 / pending 已进行=继续本叶）。pending*/后台 running 计划不提供（防绕过确认/防串扰）。
            var nd = pr?.Nodes.FirstOrDefault(x => x.Id == selLeafId);
            var lv = archMenuNode.Status ?? "";
            var started = (nd?.Attempts ?? 0) > 0 || !string.IsNullOrEmpty(archMenuNode.SessionRunId);
            if (lv == "failed")
            {
                menuOrchPlanResume.Visibility = Visibility.Visible;
                menuOrchPlanResume.Header = "重新开始本叶子（重置审查计数）";
            }
            else if (lv is "running" or "reviewing")
            {
                menuOrchPlanResume.Visibility = Visibility.Visible;
                menuOrchPlanResume.Header = "继续本叶子（重跑残留任务）";
            }
            else if (lv == "pending" && !started)
            {
                menuOrchPlanStart.Visibility = Visibility.Visible;
                menuOrchPlanStart.Header = "开始本叶子";
            }
            else if (lv == "pending")
            {
                menuOrchPlanResume.Visibility = Visibility.Visible;
                menuOrchPlanResume.Header = "继续本叶子";
            }
        }
        else if (!isLeafHit && isPaused)
        {
            // 无活跃实例的暂停残留（Stop 收口后/重启）：分支或整树续跑
            menuOrchPlanResume.Visibility = Visibility.Visible;
            menuOrchPlanResume.Header = isRootHit ? "继续执行计划" : "继续执行本分支";
        }
        else if (!isLeafHit && planStatus is "approved" or "pending" or null or "failed" or "done")
        {
            menuOrchPlanStart.Visibility = Visibility.Visible;
            menuOrchPlanStart.Header = isRootHit ? "开始执行计划" : "开始执行本分支";
        }
        // running 但当前会话无活跃实例（后台执行器在其它会话）：不提供执行控制（owner 才可停/续），仅可裁决叶子

        // 裁决/撤销：仅叶子参与；状态类别二选一显隐（可标记 ↔ 可撤销），并随状态自动切换
        var isLeafNode = archMenuNode.NodeType == 1;
        var leafStatus = isLeafNode ? archMenuNode.Status ?? "" : "";
        var canJudge = leafStatus is "failed" or "pending" or "running" or "reviewing";   // 可标记通过/跳过（含中断残留）
        var canUndo = leafStatus is "passed" or "skipped";                                  // 已标记 → 仅可撤销回待执行
        menuOrchPlanPass.Visibility = isLeafNode && canJudge ? Visibility.Visible : Visibility.Collapsed;
        menuOrchPlanSkip.Visibility = isLeafNode && canJudge ? Visibility.Visible : Visibility.Collapsed;
        menuOrchPlanUndo.Visibility = isLeafNode && canUndo ? Visibility.Visible : Visibility.Collapsed;
        // 运行实例活跃时禁用手工裁决：runner 内存态会覆盖落盘值，点了也不会生效
        var runnerAlive = planRunner != null;
        menuOrchPlanPass.IsEnabled = !runnerAlive;
        menuOrchPlanSkip.IsEnabled = !runnerAlive;
        menuOrchPlanUndo.IsEnabled = !runnerAlive;
        menuOrchPlanDelete.Visibility = (isLeafNode || archMenuNode.NodeType == 0) && selLeafId != null ? Visibility.Visible : Visibility.Collapsed;
        menuOrchPlanDelete.IsEnabled = !runnerAlive;
        // 复核编排树（整树级自检，不要求命中叶子）：执行器活跃时灰显（与裁决项一致，防内存态覆盖落盘值）；
        // 后台执行（计划 running 但本会话无活跃实例）与待确认（pendingConfirm）计划不提供入口，防干扰/防串扰。
        menuOrchPlanRepair.Visibility =
            (planStatus == "running" && planRunner == null) || planStatus == "pendingConfirm"
                ? Visibility.Collapsed : Visibility.Visible;
 menuOrchPlanRepair.IsEnabled = !runnerAlive;
 // 重新关联架构节点：与复核同口径（执行中灰显防内存态覆盖；后台执行/待确认不提供入口）
 menuOrchPlanRelink.Visibility = menuOrchPlanRepair.Visibility;
 menuOrchPlanRelink.IsEnabled = !runnerAlive;
 }

    /// <summary>沿鼠标位置命中编排执行树某一行对应的 ArchNode。</summary>
    ArchNode? OrchExecNodeAtMouse()
    {
        var hit = VisualTreeHelper.HitTest(treeOrchExec, Mouse.GetPosition(treeOrchExec));
        if (hit == null) return null;
        var dep = hit.VisualHit;
        while (dep != null && dep != treeOrchExec)
        {
            if (dep is FrameworkElement fe && fe.DataContext is ArchNode an) return an;
            dep = VisualTreeHelper.GetParent(dep);
        }
        return null;
    }

    /// <summary>取编排执行树中命中节点所属计划的 id（向上找计划根，根节点的 PlanId 即计划 id）。</summary>
    string? ArchPlanIdOfOrch(ArchNode n)
    {
        if (n.Kind != "plan") return null;
        var root = TopOrchRoot(n);
        return root?.PlanId;
    }

    /// <summary>新建编排会话对话框（8.3 修订）：选生成方案——interactive=单模型交互式（主持人问题卡片逐项收集）；
    /// multi=多模型决策（勾选参与模型 ≥2 + 可选参考资料，一次并行调研融合出方案）。模型清单仅列出已配 API Key 的。</summary>
    public class OrchSessionInputDialog : System.Windows.Window
    {
        public string ResultTitle { get; private set; } = "";
        public string ResultGoal { get; private set; } = "";
        /// <summary>生成方案：interactive（默认）| multi。</summary>
        public string ResultMode { get; private set; } = "interactive";
        /// <summary>multi 勾选的参与模型（"provider:modelId"，≥2）。</summary>
        public List<string> ResultModels { get; private set; } = new();
        /// <summary>multi 附带的参考资料（代码路径/已有方案/约束；可选，建会话后仍可继续补充）。</summary>
        public string ResultMaterials { get; private set; } = "";

        // ===== 暗色主题刷子（与 Theme.xaml 的 BgBrush/CardBrush/TextBrush/DimBrush/BorderBrush2 同色，保证与项目其它对话框一致）=====
        const int BASE_H = 448;      // 单模型视图内容高度
        const int MULTI_H = 640;     // multi 视图（多模型面板展开）内容高度
        const int TITLE_BAR_H = 40;  // 自绘标题栏高度
        static readonly Brush bgBrush = new SolidColorBrush(Color.FromRgb(0x07, 0x07, 0x0F));
        static readonly Brush cardBrush = new SolidColorBrush(Color.FromRgb(0x14, 0x15, 0x1F));
        static readonly Brush textBrush = new SolidColorBrush(Color.FromRgb(0xEE, 0xF0, 0xF6));
        static readonly Brush dimBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xB4));
        static readonly Brush borderBrush = new SolidColorBrush(Color.FromRgb(0x1C, 0x1D, 0x29));

        readonly System.Windows.Controls.TextBox tbTitle = new()
        {
            Height = 26,
            VerticalContentAlignment = VerticalAlignment.Center,
            MaxLength = 60,
            Background = cardBrush,               // 淡暗底色输入框，替代系统白底
            Foreground = textBrush,
            CaretBrush = textBrush,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 2, 6, 2),
        };
        readonly System.Windows.Controls.TextBox tbGoal = new()
        {
            MinHeight = 52,   // 占第 3 行 Star 弹性空间：中部自适应填充，窗口变高/变宽跟随伸缩
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = cardBrush,
            Foreground = textBrush,
            CaretBrush = textBrush,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 4, 6, 4),
        };
        readonly System.Windows.Controls.TextBox tbMaterials = new()
        {
            Height = 44,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = cardBrush,
            Foreground = textBrush,
            CaretBrush = textBrush,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 4, 6, 4),
        };
        readonly System.Windows.Controls.RadioButton rbInteractive = new()
        {
            GroupName = "orchMode",
            IsChecked = true,
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = textBrush,   // 浅色前景，修复"生成方案选项"标题在暗底上黑字看不清
            BorderBrush = dimBrush,   // 圆点描边用浅灰，暗底上可辨
        };
        readonly System.Windows.Controls.RadioButton rbMulti = new()
        {
            GroupName = "orchMode",
            Margin = new Thickness(0, 10, 0, 0),
            Foreground = textBrush,
            BorderBrush = dimBrush,
        };
        readonly Border multiPanel = new()
        {
            Visibility = Visibility.Collapsed,
            Background = new SolidColorBrush(Color.FromRgb(0x18, 0x1C, 0x2E)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x3A, 0x5C)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 10, 0, 0),
        };
        readonly List<(System.Windows.Controls.CheckBox Box, AppConfig.ModelOption Opt)> modelBoxes = new();

        static System.Windows.Controls.TextBlock RbText(string main, string sub) => new()
        {
            TextWrapping = TextWrapping.Wrap,
            Inlines =
            {
                new Run(main),
                new LineBreak(),
                new Run(sub) { Foreground = Brushes.Silver, FontSize = 11 },
            },
        };

        public OrchSessionInputDialog(string initTitle = "", string initGoal = "",
            List<AppConfig.ModelOption>? models = null)
        {
            models ??= new();
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Width = 560; Height = BASE_H + TITLE_BAR_H;
            // 无边框 + 自绘暗色标题栏（与项目其它深色对话框统一），替换系统默认浅色标题栏
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            Title = "新建编排会话 · 选择生成方案";
            Background = bgBrush;
            Foreground = textBrush;
            FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei, PingFang SC, sans-serif");
            tbTitle.Text = initTitle;
            tbGoal.Text = initGoal;
            rbInteractive.Content = RbText("单模型交互式生成（默认）", "主持人以问题卡片逐项收集需求，信息收齐后自动生成任务树；适合需求尚不明确");
            rbMulti.Content = RbText("多模型决策生成", "勾选多个模型 + 提供素材（目标/参考资料），一次并行调研、评估融合出候选方案；适合需求较明确");

            // multi 面板：模型勾选 + 参考资料
            var panel = new StackPanel();
            var modelTip = new System.Windows.Controls.TextBlock
            {
                Text = "参与决策的模型（勾选 2 个以上；仅列出已配置 API Key 的模型）",
                FontSize = 11,
                Foreground = Brushes.Silver,
                TextWrapping = TextWrapping.Wrap,
            };
            panel.Children.Add(modelTip);
            var wrap = new System.Windows.Controls.WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            foreach (var m in models)
            {
                var box = new System.Windows.Controls.CheckBox
                {
                    Content = m.Display,
                    FontSize = 12,
                    Margin = new Thickness(0, 3, 18, 3),
                    Foreground = textBrush,
                };
                modelBoxes.Add((box, m));
                wrap.Children.Add(box);
            }
            if (models.Count == 0)
                wrap.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = "（暂无已配置 API Key 的模型，请先到模型设置中添加）",
                    FontSize = 11,
                    Foreground = Brushes.Silver,
                });
            panel.Children.Add(wrap);
            var matTip = new System.Windows.Controls.TextBlock
            {
                Text = "参考资料（可选）：代码路径、已有方案或约束说明，也可建会话后在输入框继续补充",
                FontSize = 11,
                Foreground = Brushes.Silver,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 4),
            };
            panel.Children.Add(matTip);
            panel.Children.Add(tbMaterials);
            multiPanel.Child = panel;

            rbInteractive.Checked += (_, _) => { multiPanel.Visibility = Visibility.Collapsed; Height = BASE_H + TITLE_BAR_H; };
            rbMulti.Checked += (_, _) => { multiPanel.Visibility = Visibility.Visible; Height = MULTI_H + TITLE_BAR_H; };

            var grid = new System.Windows.Controls.Grid { Margin = new Thickness(16) };
            // 第 3 行（L0 目标框）占 Star 弹性空间：自适应填充中部；生成方案区(4-7)/按钮(9)按 Auto 排布 → 自动贴紧窗口底部
            for (int i = 0; i < 10; i++)
                grid.RowDefinitions.Add(new RowDefinition { Height = i == 3 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });

            var t1 = new System.Windows.Controls.TextBlock { Text = "标题", FontSize = 11, Foreground = Brushes.Silver };
            var t2 = new System.Windows.Controls.TextBlock
            {
                Text = "L0 目标（要做什么？先一句描述；交互式可后续对话补充，多模型决策请尽量写全）",
                FontSize = 11,
                Foreground = Brushes.Silver,
                Margin = new Thickness(0, 10, 0, 2),
                TextWrapping = TextWrapping.Wrap,
            };
            var t3 = new System.Windows.Controls.TextBlock
            {
                Text = "生成方案",
                FontSize = 11,
                Foreground = Brushes.Silver,
                Margin = new Thickness(0, 10, 0, 0),
            };
            System.Windows.Controls.Grid.SetRow(t1, 0);
            System.Windows.Controls.Grid.SetRow(tbTitle, 1);
            System.Windows.Controls.Grid.SetRow(t2, 2);
            System.Windows.Controls.Grid.SetRow(tbGoal, 3);
            System.Windows.Controls.Grid.SetRow(t3, 4);
            System.Windows.Controls.Grid.SetRow(rbInteractive, 5);
            System.Windows.Controls.Grid.SetRow(rbMulti, 6);
            System.Windows.Controls.Grid.SetRow(multiPanel, 7);

            var btns = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0),
            };
            var ok = new Button { Content = "创建会话", Width = 90, Height = 28, Margin = new Thickness(0, 0, 8, 0), IsDefault = true, Style = (Style)Application.Current.TryFindResource("OrchDialogPrimaryBtn") };
            var cancel = new Button { Content = "取消", Width = 70, Height = 28, IsCancel = true, Style = (Style)Application.Current.TryFindResource("OrchDialogSecondaryBtn") };
            ok.Click += (_, _) =>
            {
                var goal = tbGoal.Text.Trim();
                if (goal.Length == 0)
                {
                    MessageBox.Show(this, "请填写 L0 目标（要做什么）。", "新建编排会话");
                    return;
                }
                ResultTitle = tbTitle.Text.Trim();
                ResultGoal = goal;
                if (rbMulti.IsChecked == true)
                {
                    var sel = modelBoxes.Where(b => b.Box.IsChecked == true)
                                        .Select(b => $"{b.Opt.Provider}:{b.Opt.ModelId}").ToList();
                    if (sel.Count < 2)
                    {
                        MessageBox.Show(this, "多模型决策需要至少勾选 2 个参与模型（已配置 API Key 的）。", "新建编排会话");
                        return;
                    }
                    ResultMode = "multi";
                    ResultModels = sel;
                    ResultMaterials = tbMaterials.Text.Trim();
                }
                DialogResult = true;
            };
            cancel.Click += (_, _) => DialogResult = false;
            btns.Children.Add(ok);
            btns.Children.Add(cancel);
            System.Windows.Controls.Grid.SetRow(btns, 9);

            grid.Children.Add(t1);
            grid.Children.Add(tbTitle);
            grid.Children.Add(t2);
            grid.Children.Add(tbGoal);
            grid.Children.Add(t3);
            grid.Children.Add(rbInteractive);
            grid.Children.Add(rbMulti);
            grid.Children.Add(multiPanel);
            grid.Children.Add(btns);

            // ===== 自绘暗色标题栏 + 圆角暗色内容区外壳（参照 AutoTaskDialog 的深色对话框风格）=====
            var header = new System.Windows.Controls.TextBlock
            {
                Text = "新建编排会话 · 选择生成方案",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = textBrush,
                Margin = new Thickness(16, 0, 16, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var shell = new System.Windows.Controls.Grid();
            shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TITLE_BAR_H) });
            shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            System.Windows.Controls.Grid.SetRow(header, 0);
            System.Windows.Controls.Grid.SetRow(grid, 1);
            shell.Children.Add(header);
            shell.Children.Add(grid);
            shell.Background = Brushes.Transparent;  // 空白区可命中，便于拖拽整窗
            shell.MouseLeftButtonDown += (_, me) =>
            {
                if (me.ButtonState == MouseButtonState.Pressed) DragMove();
            };

            Content = new Border
            {
                Background = bgBrush,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Child = shell,
            };
        }
    }

    #region 编排执行（1-4：PlanRunner 串行执行 + 人工裁决）

    /// <summary>默认的"继续"触发词：用户在编排会话输入"继续"等即续跑暂停中的计划。</summary>
    static bool IsResumeCommand(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        string[] exact = { "继续", "继续执行", "继续吧", "接着跑", "续跑", "重启", "重新开始" };
        if (exact.Contains(t)) return true;
        return t.Contains("继续执行", StringComparison.OrdinalIgnoreCase)
            || t.Contains("继续", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>启动已确认计划的串行执行：PlanRunner 后台线程跑，日志实时追加到聊天区。
    /// scopeId=null 整树；指向 group=本分支子树自动遍历；指向 leaf=单叶执行（右键叶子开始/继续本叶子）。</summary>
    void StartPlanExecution(string planId, string? scopeId = null)
    {
        var pr = GAIRR.AgentHost.PlanStore.GetById(cfg.ProjectRoot, planId);
        if (pr == null) return;
        // 范围信息（供收尾文案/成功态判定）：scopeId 指向 leaf → 叶子级单叶；指向 group → 分支子树；null → 整树
        bool leafScope = false;
        string scopeNodeTitle = "";
        if (!string.IsNullOrEmpty(scopeId))
        {
            var sc = pr.Nodes.FirstOrDefault(n => n.Id == scopeId);
            leafScope = sc?.Type == "leaf";
            scopeNodeTitle = sc?.Title ?? "";
        }
        // 执行器全局单例 gate：任一会话的执行器在跑即拦（两会话并行下，本会话代理可能为 null——A 执行器在跑时
        // 切到 B 会话，B 的 planRunner 代理为空，若只按当前代理判据会漏拦并起第二个执行器 → 按记录全局判）。
        if (runTasks.Values.Any(r => r.PlanRunner != null))
        {
            var busy = orcExecActive && !string.IsNullOrEmpty(orcExecPlanTitle)
                ? $"计划「{orcExecPlanTitle}」正在执行中"
                : "已有计划正在执行";
            MessageBox.Show(this, busy + "，请先停止该计划或等其完成，再开始新的执行。", "编排执行");
            return;
        }
        if (!EnsureNoCodeEditingBeforeRun()) return;   // 任务启动锁：代码页编辑未保存 → 拦截（同主任务/方案生成收口）
        var ownerSession = currentSession;   // 计划叶子不产生主对话 Finished 事件，执行态归属用此捕获（事件上屏/收口提示以它为界）
        if (ownerSession == null) return;   // 无会话上下文不可编排（EnsureRec 对 null 抛错，此处显式防御）
        var orec = EnsureRec(ownerSession);   // 编排归属记录：整段编排的启动/收口都直写它（收口时刻前台可能已切走，不依赖 currentSession 代理）
        orec.GitLastKey = ownerSession?.Id;   // 计划叶子收尾提交的归属键：整段编排都归当前会话
        orchOwner = ownerSession;            // 供后台消费判定（危险确认分流、右栏执行页常驻）；收口置空（此刻前台=ownerSession，代理落账一致）
        var runner = new GAIRR.AgentHost.PlanRunner(cfg.ProjectRoot, line => Dispatcher.Invoke(() =>
        {
            // 执行状态行仅实时显示不写历史：切到其它会话期间不上屏（过程全量在叶子会话记录，可回看）
            if (currentSession == ownerSession)
                AddMessage(new ChatMessage { Kind = MsgKind.Cmd, Who = "编排执行", Text = line, NoPersist = true });
            ParseOrcExecLogLine(line);   // 需求 1/3：顶部计划/叶子行随日志即时同步（标题/阶段/用时）
        }));
        // 叶子会话实时事件桥接：把叶子的思考/工具/流式文本转发到主 Bus，UI 自然消费。
        // Key 覆盖为编排所属会话（ownerSession），使叶子事件归入本会话流程，避免跨会话串扰。
        runner.LeafEventBridge = ev => Dispatcher.BeginInvoke(() => uiBus.Post(ev with { SessionKey = ownerSession?.Id }));
        // 审查阶段信号（需求 2 顶部审核提示）：叶子执行完成、审查 Agent 启动 → true；审查结束/异常退出 → false
        runner.ReviewStageChanged = reviewing => Dispatcher.BeginInvoke(() =>
        {
            if (!orcExecActive) return;
            orcExecStage = reviewing ? "reviewing" : "executing";
            OrcStatusRefresh();   // 即时刷新顶部行：审查提示切琥珀、叶子行切"审查中"文案
        });
        // 每叶/审查执行前重新取样标题栏模型下拉的当前选中项：编排中用户换模型，从下一个叶子即生效（逐次重读，非编排启动时固定）
        runner.ModelSelector = () =>
        {
            try
            {
                var opt = Dispatcher.Invoke(() => modelSel.SelectedItem as AppConfig.ModelOption);
                if (opt != null) return (opt.Provider, opt.ModelId);
            }
            catch { /* UI 线程暂不可用时回退权威源 cfg.LastProvider/LastModel */ }
            return (cfg.LastProvider, cfg.LastModel);
        };
        orec.PlanRunner = runner;   // 执行器挂归属会话记录（取代经当前会话代理的实例写法：两会话并行收口时按记录自清，不串台）
        SetSendBusy(true);   // 执行编排时发送按钮进入中止状态（点击即停止编排）
        // 顶部常驻快照（需求 1-3）：窗口级字段不依赖 CurRec——执行中切到其它会话查看，顶部信息栏仍常驻同步
        orcExecActive = true;
        orcExecStage = "executing";
        orcExecPlanId = planId;
        orcExecPlanTitle = pr.Title;
        orcExecLeafDone = 0;
        orcExecLeafTotal = 0;
        orcExecLeaf = "";
        orcExecStart = DateTime.Now;
        topUserSig = null;   // 顶部用户区强制重建（左侧常驻行出现后高度预算变化，气泡条数/折行需按新预算重算）
        MarkRunStart(ownerSession);   // 会话状态：编排执行中 → 会话行置桔（叶子无独立 cts，归属会话统一标）
        orec.TaskActive = true;   // 待办进度随叶子执行常驻（与普通任务一致）：叶子事件桥接归本会话，RefreshTodoBar 才能把任务计划卡显示到顶部
        LoadArchTree();   // 启动即刷新功能架构
        LoadOrchExecTree();   // 刷新编排执行树状态
        OrcStatusRefresh();   // 顶部计划行/计划会话圆点首帧（随后由 1Hz 定时器 + 日志事件持续刷新）
        AddMessage(new ChatMessage { Kind = MsgKind.Cmd, Who = "编排执行", Text = $"[plan] 开始执行计划：{pr.Title}", NoPersist = true });
        // 执行期间周期性刷新编排执行树（原始指纹比对，状态变化才重建 + 联动定位当前叶子）
        StartOrcRefreshTimer();
        _ = Task.Run(async () =>
        {
            bool ok;
            try { ok = await runner.RunAsync(planId, scopeId); }
            catch (Exception ex)
            {
                ok = false;
                Dispatcher.Invoke(() => { if (currentSession == ownerSession) AddMessage(new ChatMessage { Kind = MsgKind.Cmd, Who = "编排执行", Text = "[plan-run] 执行异常：" + ex.Message, NoPersist = true }); });
            }
            Dispatcher.Invoke(() =>
            {
                if (orec.PlanRunner == runner) orec.PlanRunner = null;   // 直写归属记录：收口时刻前台可能已切走，也清自己的（防切回后残留"仍在执行"误锁）
                orec.GitLastKey = null;   // 编排收尾：归属键随执行结束清空（下一个任务启动点会重新记录）
                orec.TaskActive = false;   // 编排收口：待办进度条回落（同普通任务结束），防切回后残留旧叶子计划卡
                StopOrcRefreshTimer();
                orcExecActive = false;   // 顶部常驻收口：chatTopBar 回落滚动联动显隐，计划/叶子行隐藏（需求 1-3）
                orcExecStage = "";       // 归零复位，防旧状态残留到下次启动
                orcExecLeaf = "";
                OrcStatusRefresh();   // 顶部行随开关隐藏 + 各计划会话圆点按终态聚合（需求 7：绿/红即时生效）
                // 范围（叶子/分支）自然收口（范围内叶子已全部到终态但树仍有未执行叶）与整树完成同样视为本段执行成功（绿）；
                // 停止/叶子失败（plan failed）仍为未完成（红），避免正常范围收口被误标中断红。
                bool finalOk = ok;
                string? scopeState = null;   // 范围执行结果详情（供文案）
                if (!ok && scopeId != null)
                {
                    var cur2 = GAIRR.AgentHost.PlanStore.GetById(cfg.ProjectRoot, planId);
                    if (cur2 != null)
                    {
                        var scopes = GAIRR.AgentHost.PlanStore.SubtreeLeafIds(cur2, scopeId);
                        if (cur2.Status == "paused" && !cur2.Nodes.Any(n => scopes.Contains(n.Id) && n.Type == "leaf" && n.Status == "pending"))
                        {
                            finalOk = true;   // 范围内无待执行叶 → 自然收口（叶子通过/范围完成）
                            scopeState = "部分完成";
                        }
                        else if (cur2.Status == "failed") scopeState = "失败";
                        else if (cur2.Status == "paused") scopeState = "已停止";
                    }
                }
                MarkRunEnd(ownerSession, finalOk);   // 会话状态收口：执行通过→完成(绿)；停止/异常→中断(红)
                SetSendBusy(false);   // 编排结束恢复发送按钮（若主对话未在跑则显示发送）
                LoadArchTree();   // 刷新功能架构
                LoadOrchExecTree();   // 刷新编排执行树状态
                // 收尾提示分流：人还在归属会话 → 聊天区状态行；切到其它会话期间结束 → toast（会话行颜色/徽标/树状态已表达结果）
                string outcome;
                if (finalOk)
                    outcome = scopeState == null ? $"[计划完成] {pr.Title} 全部叶子执行通过。"
                        : (leafScope ? $"[叶子通过] 「{scopeNodeTitle}」已通过；计划仍有未执行叶（可右键该叶继续或整树续跑）。"
                                     : $"[范围完成] 「{scopeNodeTitle}」内叶子已全部处理；计划仍有未执行叶（可右键分支/叶子续跑）。");
                else
                    outcome = scopeState == "失败"
                        ? $"[叶子失败] {pr.Title} 叶子未通过审查（可右键该叶\"重新开始\"重置计数，或整树重新执行）。"
                        : $"[计划未完成] {pr.Title} 已停止（可右键目标叶子\"继续\"或整树续跑）。";
                if (currentSession == ownerSession)
                    AddMessage(new ChatMessage
                    {
                        Kind = MsgKind.Agent,
                        Who = "编排执行",
                        NoPersist = true,   // 完成/未完成状态提示：不写入会话历史
                        Text = outcome,
                    });
                else
                    ShowToast($"[编排] {outcome}");
                orec.OrcOwner = null;   // 收口：归属记录清空（经代理清会落到收口时刻的前台会话，故直写）
            });
        });
    }

    /// <summary>编排执行期间每秒刷新一次编排执行树与顶部同步行：树仅状态变化时重建并联动定位运行中的叶子；
    /// 顶部计划行/用时/叶子行与计划会话圆点每秒聚合（需求 1-3/7）。以窗口级 orcExecActive 为判据
    /// （planRunner 经 CurRec 代理，切到其它会话后会变 null，不能作定时器寿命判据）。</summary>
    void StartOrcRefreshTimer()
    {
        StopOrcRefreshTimer();
        orcRefreshTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        orcRefreshTimer.Tick += (_, _) =>
        {
            if (!orcExecActive || treeOrchExec == null) { StopOrcRefreshTimer(); return; }
            if (treeOrchExec.IsVisible) LoadOrchExecTree();
            OrcStatusRefresh();   // 顶部计划行 + 用时同步（需求 3）+ 圆点聚合（需求 7）
        };
        orcRefreshTimer.Start();
    }

    void StopOrcRefreshTimer()
    {
        if (orcRefreshTimer != null)
        {
            orcRefreshTimer.Stop();
            orcRefreshTimer = null;
        }
    }

    /// <summary>开始执行（分层）：命中叶子=单叶开始本叶；命中分组=自动遍历本分支子树；命中根=整树。
    /// failed 计划先重置目标范围（叶子/分支子树/全树）内的失败叶子为 pending（重试计数清零）再启动，范围外失败叶不动。</summary>
    void OnPlanStartClick(object sender, RoutedEventArgs e)
    {
        if (selPlanId == null) return;
        var pr = GAIRR.AgentHost.PlanStore.GetById(cfg.ProjectRoot, selPlanId);
        if (pr == null) { MessageBox.Show(this, "计划不存在", "编排执行"); return; }
        bool hitNode = archMenuNode?.Kind == "plan" && selLeafId != null;   // 命中叶子或分组节点
        string? scopeId = hitNode ? selLeafId : null;
        if (pr.Status == "failed")
        {
            var inScope = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (scopeId != null) inScope = GAIRR.AgentHost.PlanStore.SubtreeLeafIds(pr, scopeId);
            var failedLeaves = pr.Nodes.Where(n => n.Status == "failed" && (scopeId == null || inScope.Contains(n.Id))).ToList();
            if (failedLeaves.Count > 0)
            {
                if (MessageBox.Show(this, scopeId == null
                        ? "该计划已失败。重新开始将重置失败叶子的重试计数并继续执行，继续？"
                        : $"所选范围含 {failedLeaves.Count} 个失败叶子。重新开始将重置其重试计数并执行，继续？", "编排执行",
                        MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                foreach (var n in failedLeaves) { n.Status = "pending"; n.Attempts = 0; }   // 重置失败叶子：状态 + 重试计数（否则重跑必立即再失败）
                pr.Status = "approved";
                GAIRR.AgentHost.PlanStore.Save(cfg.ProjectRoot, pr);
                LoadOrchExecTree();
            }
        }
        StartPlanExecution(selPlanId, scopeId);
    }

    void OnPlanPauseClick(object sender, RoutedEventArgs e) => planRunner?.Pause();

    /// <summary>继续（分层）：有活跃实例（暂停态）则 Resume 整个执行器；
    /// 无实例时按命中对象续跑——叶子=单叶（failed 重置计数重跑/残留续跑，见 PlanRunner 归一）；
    /// 分组/根=本分支子树/整树自动遍历从待执行处续跑（跳过已失败/已完成，不误跑其它未开始叶）。</summary>
    void OnPlanResumeClick(object sender, RoutedEventArgs e)
    {
        if (planRunner != null) { planRunner.Resume(); return; }
        if (selPlanId == null) return;
        bool hitNode = archMenuNode?.Kind == "plan" && selLeafId != null;   // 叶子或分组节点
        StartPlanExecution(selPlanId, hitNode ? selLeafId : null);
    }

    void OnPlanStopClick(object sender, RoutedEventArgs e) => planRunner?.Stop();

    /// <summary>删除所选分支（含其所在节点与全部子孙节点）：从 plan.Nodes 删除并清理父节点 children 引用，落盘后刷新树。</summary>
    void OnPlanDeleteBranch(object sender, RoutedEventArgs e)
    {
        if (selPlanId == null || string.IsNullOrWhiteSpace(selLeafId)) { MessageBox.Show(this, "请先右键目标分支再删除", "删除分支"); return; }
        if (planRunner != null) { MessageBox.Show(this, "正在执行中，请先停止再删除分支", "删除分支"); return; }
        var pr = GAIRR.AgentHost.PlanStore.GetById(cfg.ProjectRoot, selPlanId);
        if (pr == null) return;
        var target = pr.Nodes.FirstOrDefault(n => n.Id == selLeafId);
        if (target == null) { MessageBox.Show(this, "所选节点不存在", "删除分支"); return; }
        if (MessageBox.Show(this, $"删除分支「{target.Title}」（含其全部子叶）？该计划将保留其余分支。\n此操作将连同 plan.json 中的任务数据一并删除。", "删除分支",
                            MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        // 收集要删除的全部节点 id（自身 + 递归子孙）
        var toRemove = new HashSet<string>(StringComparer.Ordinal);
        void Collect(GAIRR.AgentHost.PlanNodeDto node)
        {
            if (!toRemove.Add(node.Id)) return;
            foreach (var c in node.Children)
            {
                var child = pr.Nodes.FirstOrDefault(n => n.Id == c);
                if (child != null) Collect(child);
            }
        }
        Collect(target);
        pr.Nodes.RemoveAll(n => toRemove.Contains(n.Id));
        // 清理父节点 children 引用（含其它节点里对已删除 id 的引用）
        foreach (var n in pr.Nodes) n.Children.RemoveAll(c => toRemove.Contains(c));
        if (pr.Nodes.Count == 0 || pr.Nodes.All(n => toRemove.Contains(n.Id)))
        {
            // 全删：计划本身也清除视图（数据仅剩空计划，标记为 deleted 由调用侧忽略）
            pr.Status = "deleted";
        }
        GAIRR.AgentHost.PlanStore.Save(cfg.ProjectRoot, pr);
        LoadOrchExecTree();
        LoadArchTree();
        AddMessage(new ChatMessage { Kind = MsgKind.Cmd, Who = "编排执行", Text = $"[plan] 已删除分支「{target.Title}」（移除 {toRemove.Count} 个节点）。", NoPersist = true });
    }

    /// <summary>查看叶子会话历史：右键菜单点击后，以只读方式在对话区展示该叶子的完整 Agent 会话记录。</summary>
    void OnViewLeafSessionClick(object sender, RoutedEventArgs e)
    {
        var n = archMenuNode ?? treeArch.SelectedItem as ArchNode;
        if (n == null) return;
        OpenLeafSession(n);
    }

    /// <summary>架构树/编排执行树双击：编排叶子打开会话历史，分组节点切换展开/收起。
    /// 命中目标取当前可见的树（编排执行 Tab 用 treeOrchExec，架构栏用 treeArch）。</summary>
    void OnArchNodeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var tree = treeOrchExec != null && treeOrchExec.IsVisible ? (ItemsControl)treeOrchExec : treeArch;
        var hit = VisualTreeHelper.HitTest(tree, e.GetPosition(tree));
        ArchNode? node = null;
        for (var d = hit?.VisualHit; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is TreeViewItem tvi && tvi.DataContext is ArchNode n) { node = n; break; }
        if (node == null) return;

        if (node.Kind == "plan" && node.NodeType == 1)
        {
            // 编排叶子：打开会话历史
            OpenLeafSession(node);
            e.Handled = true;
        }
        // 其他节点（分组/功能节点）的双击由 TreeView 默认行为处理（展开/收起）
    }

    /// <summary>加载并只读展示叶子执行会话的完整历史（不切换 currentSession，不影响当前工作流）。
    /// 未执行过的叶子提示“尚未执行”。</summary>
    void OpenLeafSession(ArchNode node)
    {
        var leafName = node.Name;
        var sessionId = node.SessionRunId;
        if (string.IsNullOrEmpty(sessionId))
        {
            AddMessage(new ChatMessage { Kind = MsgKind.Agent, Who = "GAIRR", Text = $"叶子「{leafName}」尚未执行，暂无会话记录。" });
            return;
        }
        try
        {
            var ns = GAIRR.AgentHost.SessionStore.NormalizeNamespace(cfg.ProjectRoot);
            var store = new GAIRR.AgentHost.SessionStore(cfg, cfg.ProjectRoot);
            var rec = store.Load(ns, sessionId);
            if (rec == null || rec.Messages.Count == 0)
            {
                AddMessage(new ChatMessage { Kind = MsgKind.Agent, Who = "GAIRR", Text = $"叶子「{leafName}」的会话记录不存在或为空。" });
                return;
            }
            // 以分隔线 + 标题开头，明确标识这是只读回放
            AddMessage(new ChatMessage { Kind = MsgKind.Cmd, Who = "叶子会话", Text = $"━━━ 叶子会话回放：{leafName}（{sessionId}） ━━━" });
            foreach (var m in rec.Messages)
            {
                var kind = m.Role == "user" ? MsgKind.User : MsgKind.Agent;
                var who = m.Role == "user" ? "User" : "Agent";
                AddMessage(new ChatMessage { Kind = kind, Who = who, Text = m.Content ?? "" });
            }
            AddMessage(new ChatMessage { Kind = MsgKind.Cmd, Who = "叶子会话", Text = $"━━━ 回放结束（共 {rec.Messages.Count} 条消息） ━━━" });
        }
        catch (Exception ex)
        {
            AddMessage(new ChatMessage { Kind = MsgKind.Agent, Who = "GAIRR", Text = $"加载叶子会话失败：{ex.Message}" });
        }
    }

    /// <summary>人工裁决：按状态机把右键命中的叶子标记为 passed/skipped，或撤销回 pending（undo）并落盘。
    /// 运行实例活跃时禁止（runner 内存态会覆盖落盘值，即“点了没反应”的根因之一）；
    /// 状态合法性由 PlanRunner.MarkLeaf 权威判定，非法时给出针对性提示，避免静默失败。</summary>
    void MarkCurrentLeaf(string toStatus)
    {
        if (selPlanId == null) return;
        if (string.IsNullOrWhiteSpace(selLeafId)) { MessageBox.Show(this, "请先右键目标叶子节点再选择裁决", "编排执行"); return; }
        if (planRunner != null) { MessageBox.Show(this, "计划执行中（或已暂停待继续），请先停止编排再人工裁决，避免运行状态覆盖本次标记", "编排执行"); return; }
        var pr = GAIRR.AgentHost.PlanStore.GetById(cfg.ProjectRoot, selPlanId);
        if (pr == null) return;
        var leaf = pr.Nodes.FirstOrDefault(n => n.Id == selLeafId);
        if (leaf == null) { MessageBox.Show(this, "选中的节点不是可裁决的叶子", "编排执行"); return; }
        var from = leaf.Status ?? "";
        if (!GAIRR.AgentHost.PlanRunner.MarkLeaf(cfg.ProjectRoot, selPlanId, leaf.Id, toStatus))
        {
            // 状态机拒绝：菜单显隐已尽量保证合法，此处兜底提示并说明正确操作
            if (toStatus == "pending")
                MessageBox.Show(this, $"叶子状态为「{from}」，未被人工标记过，无需撤销；可直接「标记通过/标记跳过」", "编排执行");
            else
                MessageBox.Show(this, from is "passed" or "skipped"
                    ? "该叶子已标记完成，如标错请使用「撤销标记（恢复为待执行）」"
                    : "该叶子当前状态不能直接标记，请先使用「撤销标记（恢复为待执行）」", "编排执行");
            return;
        }
        var label = toStatus switch { "passed" => "通过", "skipped" => "跳过", "pending" => "待执行（已撤销标记）", _ => toStatus };
        ShowToast($"[plan] 人工裁决：叶子「{leaf.Title}」已标记为{label}");   // 轻量反馈：浮层 3 秒自动消失，不进会话历史
        LoadOrchExecTree();
        // 标记后树已重建、深层分支收起：主动展开并选中被标记叶子，保证反馈（圆点变色/绿字/删除线/后缀）可见
        if (treeOrchExec.Items.Count > 0 && treeOrchExec.Items[0] is ArchNode r)
            RevealOrchNode(r, selLeafId);
    }

    void OnPlanPassClick(object sender, RoutedEventArgs e) => MarkCurrentLeaf("passed");
    void OnPlanSkipClick(object sender, RoutedEventArgs e) => MarkCurrentLeaf("skipped");
    void OnPlanUndoClick(object sender, RoutedEventArgs e) => MarkCurrentLeaf("pending");

    /// <summary>重新关联架构节点：把计划从旧功能节点摘除后改挂到用户新选的节点（覆盖旧关联并刷新架构树）。
 /// 执行器活跃时禁点（runner 内存态会覆盖落盘值）；同时回写 plan.ArchRef 保证后续自愈有锚点。</summary>
 void OnPlanRelinkClick(object sender, RoutedEventArgs e)
 {
 if (selPlanId == null) return;
 if (planRunner != null) { MessageBox.Show(this, "计划执行中（或已暂停待继续），请先停止再重新关联，避免运行状态覆盖本次改动", "重新关联"); return; }
 var pr = GAIRR.AgentHost.PlanStore.GetById(cfg.ProjectRoot, selPlanId);
 if (pr == null) { MessageBox.Show(this, "计划不存在", "重新关联"); return; }
 var arch = ArchTreeStore.Load(cfg.ProjectRoot);
 var func = arch?.Roots.FirstOrDefault(r => r.Kind == "func");
 if (func == null) { MessageBox.Show(this, "当前项目没有功能架构树（.gairr/architecture.json）", "重新关联"); return; }
 var dlg = new ArchNodePickerDialog(func, pr.Title) { Owner = this };
 if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.ResultPath)) return;
 var removed = ArchTreeStore.DetachPlan(cfg.ProjectRoot, pr.Id); // 先摘旧挂点，防一个计划被两个功能叶同时引用
 var r = ArchTreeStore.AttachPlan(cfg.ProjectRoot, dlg.ResultPath!, pr.Id, pr.Title);
 pr.ArchRef = dlg.ResultPath;
 GAIRR.AgentHost.PlanStore.Save(cfg.ProjectRoot, pr);
 LoadArchTree();
 LoadOrchExecTree();
 var tip = r switch
 {
 2 => "已改挂到「" + dlg.ResultPath + "」下，并新增功能子叶「" + pr.Title + "」",
 1 => "已改挂到「" + dlg.ResultPath + "」",
 _ => "关联失败：节点「" + dlg.ResultPath + "」不存在或架构树不可写",
 };
 if (removed > 0) tip += "（已清除 " + removed + " 处旧关联）";
 AddMessage(new ChatMessage { Kind = MsgKind.Cmd, Who = "编排执行", Text = "[plan] 重新关联：" + tip, NoPersist = true });
 }

 /// <summary>复核编排树（自动修复）：一键整树一致性自检。修复口径与 PlanRunner.RunAsync 的“自动修复（自愈，幂等）”
    /// 及“单叶归一”保持一致，不产生新语义：
    /// ① running/reviewing 中断残留（进程强退/崩溃遗留；正常 Stop 已回 pending）→ 归 pending，保留审查计数续跑；
    /// ② pending 且 attempts&gt;MaxRetry（中断前已耗尽配额、未走 failed 收口的残留）→ 清空 attempts；
    /// ③ failed（重试用尽）：先弹确认，通过后重置审查计数/问题清单 → pending 重跑（与“重新开始本叶子”同口径）。
    /// 执行器活跃时入口灰显（OnOrchExecMenuOpened），此处仍兜底拦截：runner 内存态会覆盖落盘值，点了也不生效。
    /// 计划级状态收口：修复后仍有待执行叶 → 原 running/failed 置 paused 供“开始/继续执行”续跑；
    /// 全树无待执行叶（终态）→ 执行态/失败态收口为 done。approved/paused/pending 等正常计划状态不受影响。</summary>
    void OnPlanRepairClick(object sender, RoutedEventArgs e)
    {
        if (selPlanId == null) return;
        if (planRunner != null)
        {
            MessageBox.Show(this, "计划执行中（或已暂停待继续），请先停止编排再复核自动修复，避免运行状态覆盖本次修复结果", "复核编排树");
            return;
        }
        var pr = GAIRR.AgentHost.PlanStore.GetById(cfg.ProjectRoot, selPlanId);
        if (pr == null) { MessageBox.Show(this, "计划不存在", "复核编排树"); return; }
        var leaves = pr.Nodes.Where(n => n.Type == "leaf").ToList();
        var residual = leaves.Where(n => n.Status is "running" or "reviewing").ToList();   // ① 中断残留
        var overQuota = leaves.Where(n => n.Status == "pending" && n.Attempts > pr.MaxRetry).ToList();   // ② 配额耗尽残留
        var failed = leaves.Where(n => n.Status == "failed").ToList();                      // ③ 失败叶（须确认）
        if (residual.Count == 0 && overQuota.Count == 0 && failed.Count == 0)
        {
            ShowToast("复核完成：编排树状态一致，无需修复");
            return;
        }
        if (failed.Count > 0)
        {
            var tip = $"复核发现：失败叶子 {failed.Count} 个、中断残留 {residual.Count} 个、配额耗尽 {overQuota.Count} 个。\n\n" +
                      "自动修复将：中断残留/配额耗尽叶子归一为待执行（分别保留计数/清空计数），" +
                      "失败叶子重置审查计数与问题清单后按待执行重跑。\n\n确定执行自动修复？";
            if (MessageBox.Show(this, tip, "复核编排树（自动修复）", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        }
        int repResidual = 0, repQuota = 0, repFailed = 0;
        foreach (var n in residual) { n.Status = "pending"; repResidual++; }
        foreach (var n in overQuota) { n.Attempts = 0; repQuota++; }
        foreach (var n in failed)
        {
            n.Status = "pending";
            n.Attempts = 0;
            n.ReviewIssues = new List<string>();
            n.ReviewSummary = null;
            repFailed++;
        }
        var hasPending = pr.Nodes.Any(n => n.Type == "leaf" && n.Status == "pending");
        if (hasPending)
        {
            if (pr.Status is "running" or "failed") pr.Status = "paused";   // 解除失败/僵尸执行态，供续跑
        }
        else if (pr.Status is "running" or "paused" or "failed")
        {
            pr.Status = "done";                                             // 全树终态：执行态/失败态收口
        }
        GAIRR.AgentHost.PlanStore.Save(cfg.ProjectRoot, pr);
        LoadOrchExecTree();
        AddMessage(new ChatMessage
        {
            Kind = MsgKind.Cmd,
            Who = "编排执行",
            Text = $"[plan] 复核编排树：归一中断残留 {repResidual}、清配额 {repQuota}、重置失败叶 {repFailed}（计划状态 → {pr.Status}）。",
            NoPersist = true,
        });
    }

    #endregion

    /// <summary>点「+ 新会话」：会话区复位成新会话待发起态，并**立刻**在左侧列表顶部插入一条「新会话」占位项并高亮
    /// （给用户明确的视觉落点，也作为当前会话的归属锚点）。占位项只活在内存里（SessionItem.IsPending）：
    /// SaveSessionHistory 序列化时整体过滤、SaveCurrentSession 对它一律跳过，所以“只点了新会话没发消息”不会在历史文件里
    /// 留下空「新会话」记录；真正发送首条消息时由 EnsureSessionForSend 就地转正、随本次保存落盘。</summary>
    void OnNewSession(object sender, RoutedEventArgs e)
    {
        try
        {
            // 会话切换锁：SSE 流未绑定（发送后→首个回复 token 到达前）新建会话会把流带走（串台）→ 拦截并提示
            if (!SwitchEnabled)
            {
                mStatus.Text = SwitchLockTip ?? "AI 正在响应，首个回复生成前不能切换会话";
                return;
            }
            // 两会话并行 + 厂商并发闸：前台主任务（cts）运行中同样允许新建会话——切走后原任务照跑
            // （验收7：事件按 SessionKey 归属记录、后台收口直写其会话记录，不污染被查看视口）；
            // 多会话并行占位由 Agent 层 LlmConcurrencyGate 按厂商并发上限把关（占满时新任务启动即被拒并提示）。
            // 先保存当前会话
            openRunId = null;   // 离开任务回放视口：快照键回到会话 Id
            SaveCurrentSession();

            // P6②（两会话并行）：新建会话不再无条件取消任务/清空缓冲。前台主任务（cts）运行中同样放行——
            // 离开会话的任务（含本会话前台主任务）在切换后照跑、进度与收口直写其所属会话记录（P1③），
            // 故其记录上的运行缓冲一律保留、不取消任何 cts；
            // 任务取消统一走“停止”按钮的所属会话路径（OnStop）。新会话是全新记录本无运行缓冲可清；
            // TodoBar/顶栏刷新放到空白态建立后（见 EnterBlankNewSession 内 AddMessage 后），刷新的是新会话视角。
            EnterBlankNewSession();
            // 用户需求：点「+ 新会话」后左侧「新会话」列表项**立即落盘**——否则它只是内存占位项，
            // 点击其它会话项时被 DropPendingSession 摘掉且从未写盘，再切回就找不到了。
            // 就地转正（IsPending=false：SaveSessionHistory 序列化不再过滤它）并整体写盘，
            // 让这条空「新会话」持久存在；首条消息发出时由 EnsureSessionForSend 直接承接（Id 不变），无需二次转正。
            if (currentSession is { IsPending: true })
            {
                currentSession.IsPending = false;
                SaveSessionHistory();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Error] 新建会话失败: {ex.Message}");
        }
    }

    /// <summary>进入新会话待发起态（新会话按钮 / 切换项目共用）：清会话区消息与标题暂存、复位澄清序号/待办条/顶部信息栏、
    /// 恢复普通主持人提示词、顶部角色模式选择器回到「🤖 自动匹配」并给出提示消息；默认在左侧列表顶部**插入占位「新会话」项**
    /// 并置为当前/高亮（见 NewPendingSessionItem，IsPending 内存态、永不落盘）。
    /// <para>连点多次「新会话」复用同一条占位项，不会堆叠；未发送首条消息就离开（切历史会话/切项目/删除会话）
    /// 由 DropPendingSession 把它摘掉，历史里不残留空「新会话」。</para>
    /// <para>删除当前会话走 withPending=false：不插占位项、currentSession 置 null——左侧列表不残留任何项
    /// （直接删除后不再于顶部显示「新会话」占位，符合"删除即消失"预期）；首条消息发出时由 EnsureSessionForSend
    /// 新建会话项置顶落盘（与启动后欢迎态直接输入同一条路径）。</para>
    /// <para>两会话并行下各会话上下文归各自 Runner（历史/Key/阶段），此处不触碰任何 Runner（首任务 EnsureRunner
    /// 自按空会话装载），无需也不得复位历史。</para></summary>
    void EnterBlankNewSession(bool withPending = true)
    {
        pendingSessionTitle = null;
        sessionTitleSet = false;   // 标题回退“模型首句/用户首句自动摘要”：待真正发送后再定
        ResetMsgView();            // 清消息区并复位分段加载暂存（新会话无更早历史可装配）
        qCardSeq = 0;              // 新会话：澄清问题序号从 1 重新编号
        // 列表取消高亮：Selected 走 INPC 即时生效；IsCurrent 不绑定界面仅作标记 —— 都无需重建整树
        foreach (var s in sessionHistory) { s.Selected = false; s.IsCurrent = false; }

        // 默认：立刻给出左侧列表的占位落点并置为当前会话——占位项自带稳定 Id，发送时 EnsureRec/SetSessionKey/sendSession
        // 直接锚定它，事件归属与防串台链路和普通会话完全一致（不必等发送才建项）；
        // withPending=false（删除当前会话后）：不建占位、当前会话置空，等价于启动欢迎态，等首条消息再建项
        currentSession = withPending ? NewPendingSessionItem() : null;

        AddMessage(new ChatMessage
        {
            Kind = MsgKind.Agent,
            Who = "概尔 Agent",
            Text = $"新会话已开始。已接入真实模型（{ProviderLabel()}），把任务交给我就行。",
        });
        // 新会话无任何运行缓冲：显式把 TodoBar/顶栏刷新到空闲视角（离开会话的待办/顶栏不再残留到新会话）
        RefreshTodoBar();
        UpdateChatTopInfo();

        // 新会话是普通会话：恢复普通主持人 prompt、复位编排状态机并收起编排右栏/“生成任务树”按钮
        // （与 OpenSession 尾部同入口；缺它则从编排会话点“新会话”会残留编排面板与生成按钮）
        UpdateSessionPromptAndPanel();

        // 顶部角色/模式选择器重置为"自动匹配（角色-模式）"（tag=__auto__ 置顶项），作为首任务取样源
        var autoItem = cbMode.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => (i.Tag as string) == "__auto__");
        if (autoItem != null) cbMode.SelectedItem = autoItem;

        SyncViewportFlags();   // 新会话成为当前视口：旧会话若后台挂起转短静默补发
    }

    /// <summary>新建（或复用）左侧列表顶部的内存占位「新会话」项并置为当前/高亮。
    /// <para>IsPending=true 的占位项只在内存里存在：SaveSessionHistory 序列化时过滤、SaveCurrentSession 一律跳过，
    /// 所以“点了新会话但一条消息都没发”不会在历史文件里留下空记录。已有占位项时直接复用（连点多次「新会话」不堆叠）。</para>
    /// <para>不在此处 SaveSessionHistory：占位项本就不可落盘，而整体重写会把其它会话顺带写一遍，没必要。</para></summary>
    SessionItem NewPendingSessionItem()
    {
        // 复用已有占位项：只把高亮搬回来（Selected 走 INPC，无需重建整树）
        var projRoot = cfg.ProjectRoot ?? "";
        var existing = sessionHistory.FirstOrDefault(s => s.IsPending);
        // 跨项目的残留占位不可复用：RefreshSessionList 的 SessionMatch 按项目过滤，复用会得到一条
        // “已置当前/高亮却不在列表里”的隐形占位，先摘掉再按当前项目新建
        if (existing != null && existing.Project != projRoot) { DropPendingSession(); existing = null; }
        // 复用已落盘的空「新会话」项：上一点击「+ 新会话」已转正落盘（IsPending=false）的正式空项——
        // 判定本项目、标题仍是「新会话」、从未有过用户消息（欢迎提示语不算）。这样连点多次
        // 「+ 新会话」始终复用同一条，不会堆叠出多条空「新会话」记录。
        if (existing == null && currentSession != null
            && currentSession.Project == projRoot
            && currentSession.Title == "新会话"
            && !currentSession.IsOrchestration
            && !currentSession.Messages.Any(m => m.Kind == "User"))
        {
            existing = currentSession;
        }
        if (existing != null)
        {
            foreach (var x in sessionHistory) { x.Selected = false; x.IsCurrent = false; }
            existing.Selected = true;
            existing.IsCurrent = true;
            return existing;
        }

        var now = DateTime.Now;
        var s = new SessionItem
        {
            Title = "新会话",
            Project = projRoot,
            Time = now,
            Created = now,
            Messages = new(),
            IsPending = true,   // 内存占位：未发送首条消息不落盘（见 SaveSessionHistory 过滤 / SaveCurrentSession 跳过）
        };
        foreach (var x in sessionHistory) { x.Selected = false; x.IsCurrent = false; }
        s.Selected = true;
        s.IsCurrent = true;
        sessionHistory.Insert(0, s);
        // 新项刚插入还不在树中；SaveCurrentSession 对占位项一律跳过（不会走 Contains 命中那条重绘路径），
        // 这里必须显式重绘，否则左侧会话列表即时看不到"新会话"（重启加载后才显示）
        RefreshSessionList();
        return s;
    }

    /// <summary>丢弃内存占位「新会话」项（未发送首条消息就离开：切到历史会话 / 切项目 / 打开任务回放）。
    /// keep = 当前会话时不动它（占位项本就是当前会话，正等着首条消息）；其余占位项一律从列表摘掉并重绘，
    /// 保证历史里不会积累空「新会话」。正常会话不受影响（IsPending=false）。</summary>
    void DropPendingSession(SessionItem? keep = null)
    {
        var stale = sessionHistory.Where(s => s.IsPending && !ReferenceEquals(s, keep)).ToList();
        if (stale.Count == 0) return;
        foreach (var s in stale)
        {
            sessionHistory.Remove(s);
            runTasks.Remove(s.Id);   // 极端时序（占位期间被后台事件建过空记录）：记录随会话一起摘掉，避免按 Id 泄漏
        }
        RefreshSessionList();
    }

    /// <summary>发送首条消息前把会话准备好（必须在 EnsureRec / rec.Cts 创建 / 任务启动之前**同步**完成）：
    /// <list type="bullet">
    /// <item>任务回放视口（openRunId!=null，见 OpenTaskRun）：回放内容为只读历史，不属于新会话——先离开回放并复位成
    /// 新会话待发起态（清消息 + 补提示语 + 建占位项），否则回放消息会被 SaveCurrentSession 误存进新会话；
    /// 同时快照目录键 openRunId 归空，回到按会话 Id 取快照的口径。</item>
    /// <item>已有会话（含点「新会话」得到的内存占位项）：占位项就地**转正**（IsPending=false）并立即落盘——Id 不变，
    /// 左侧列表原条目直接变成真实会话，事件按 SessionKey 归属、sendSession 锚定发起会话的防串台链路完全不受影响。</item>
    /// <item>无会话（启动后直接在欢迎态输入、或占位项已被摘掉）：新建会话项置顶插入列表并落盘。</item>
    /// </list></summary>
    SessionItem? EnsureSessionForSend()
    {
        try
        {
            if (openRunId != null) { openRunId = null; EnterBlankNewSession(); }

            var s = currentSession;
            if (s != null && !s.IsPending) return s;   // 正常会话：原样发送

            var now = DateTime.Now;
            if (s != null)
            {
                // 占位项转正：Id/列表位置都不变，只摘掉“不落盘”标记，随后按正常会话保存
                s.IsPending = false;
                s.Project = cfg.ProjectRoot ?? "";
                s.Time = now;
            }
            else
            {
                s = new SessionItem
                {
                    Title = "新会话",
                    Project = cfg.ProjectRoot ?? "",
                    Time = now,
                    Created = now,
                    Messages = new()
                };
                foreach (var x in sessionHistory) { x.Selected = false; x.IsCurrent = false; }
                s.Selected = true;
                s.IsCurrent = true;
                currentSession = s;
                sessionHistory.Insert(0, s);
                // 新项刚插入，不在树中；SaveCurrentSession 因 Contains 命中不触发整树重建，
                // 这里必须显式刷新，否则左侧会话列表即时看不到"新会话"标题（重启加载后才显示）
                RefreshSessionList();
                SyncViewportFlags();   // 新会话成为当前视口：旧会话若后台挂起转短静默补发
            }
            SaveCurrentSession();   // 转正后立即落盘（含会话区提示消息），避免 Messages 为空、后续切换回来时被旧会话内容覆盖
            return s;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Error] 准备发送会话失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>单击会话项：选中高亮并在右侧对话区打开</summary>
    void OnSessionClick(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not SessionItem session) return;
            // 会话切换锁：SSE 流未绑定（发送后→首个回复 token 到达前）切会话会把流带进新会话（串台）→ 拦截并提示
            if (!SwitchEnabled)
            {
                mStatus.Text = SwitchLockTip ?? "AI 正在响应，首个回复生成前不能切换会话";
                return;
            }
            // 两会话并行 + 厂商并发闸：前台主任务（cts）运行中切到其它会话不再拦截——切走后原任务照跑
            // （验收7：事件按 SessionKey 归属记录、进度/收口直写其会话记录，不污染被查看视口）；只保留
            // “点击正在跑任务的当前会话行”不重开（前台流式容器正被任务驱动，避免 Save/messages.Clear 打断流式消息）
            if (cts != null && session == currentSession) return;
            // 选中高亮
            foreach (var s in sessionHistory) { s.Selected = false; s.IsCurrent = false; }
            session.Selected = true;
            session.IsCurrent = true;
            // 打开会话
            OpenSession(session);
            e.Handled = true;
        }
        catch (Exception ex)
        {
            AddMessage(new ChatMessage
            {
                Kind = MsgKind.Agent,
                Who = "GAIRR",
                Text = "会话打开失败：" + ex.Message + "\n" + ex.StackTrace,
            });

        }
    }

    /// <summary>验收7 切回恢复：目标会话的主任务（cts）切项目/切会话放行后在后台跑时，把活的工作消息接回视口尾部——
    /// 此后该会话事件回到前台路径（SessionKey=当前会话），卡片/流式进度实时可见，停止按钮即停该任务。
    /// 编排归属会话（PlanRunner!=null，cts 为 null）同此恢复：后台叶子/审查触发的危险确认卡挂在该记录的工作消息上，
    /// 切回编排会话时接回视口，卡片可见可裁决（跨会话查看不做模态打断的配套，验收10）。
    /// 离开时刻落盘的半成品快照（最后一条未完成的 Typing/无 Steps 的 Agent 记录）先摘除，避免与活消息重复。</summary>
    void RestoreBgWorkMsg(SessionItem session)
    {
        var rec = TaskOf(session);
        if (rec?.WorkMsg == null) return;   // 无活工作消息无需恢复
        if (rec.Cts == null && rec.PlanRunner == null) return;   // 无活动任务（主对话/编排）的记录：缓冲已在 ResetSessionBuffers 复位
        if (messages.LastOrDefault() is { } last
            && (last.Kind == MsgKind.Typing || (last.Kind == MsgKind.Agent && string.IsNullOrEmpty(last.Steps))))
            messages.Remove(last);
        if (!messages.Contains(rec.WorkMsg)) messages.Add(rec.WorkMsg);
    }

    /// <summary>把会话消息载入右侧对话区。防重入：连点两个会话时事件虽串行，批量填充后的布局/滚动回调仍可能
    /// 与其它路径交叉触发，重入标志把并发打开收敛为一次，避免清空/填充交错引发崩溃。</summary>
    void OpenSession(SessionItem session)
    {
        if (openingSession) return;   // 上一次打开尚未完成：忽略本次点击
        openingSession = true;
        try
        {
            // "点击查看后恢复"：已完成(4)/中断暂停(3)的会话被打开时回到空闲默认色（0=蓝点白字）
            if (session.RunState == 3 || session.RunState == 4)
            {
                session.RunState = 0;
                UpdateTabBadges();
            }
            // 先把当前会话的最新状态保存下来，避免旧会话内容残留在 messages 中
            SaveCurrentSession();
            // 未发送过首条消息的内存占位「新会话」：切到其它会话即摘除，左侧列表不残留空项
            //（点的正是占位项本身时保留，等价于原地重开）
            if (!session.IsPending) DropPendingSession();

            // P6②（两会话并行）：打开其它会话不再无条件取消任务/清空缓冲。离开会话的任务（前台主任务 cts /
            // 多模型决策 multiGenCts / 编排执行 planRunner）在切换后照跑：cts 经事件归属解析直写其会话记录并后台收口
            // （验收7，见 drain 循环/Finished case），后台任务进度与收口直写其所属会话记录（P1③），因此这里
            // 不取消任何 cts、也不清离开会话记录上的运行缓冲；任务取消统一走“停止”按钮的所属会话路径（OnStop）。
            // 目标会话自身记录的残留缓冲复位在下文 currentSession 切换后统一执行（ResetSessionBuffers，只影响目标会话）。
            pendingSessionTitle = null;
            sessionTitleSet = false;

            // 清空当前界面消息列表；各会话上下文归各自 Runner（历史/Key/标题/阶段），切走会话不清除其 Runner，
            // 目标会话 Runner（若已建）亦不清历史——再次发送基于其会话消息续上下文（历史已完成会话除外）。
            // 连分段加载暂存一起复位：目标会话的消息稍后由下文按回合分段重新装配
            ResetMsgView();

            currentSession = session;
            // P6② 目标会话记录缓冲复位：只影响刚打开的目标会话记录，且仅在其无活动任务（空闲/历史会话，残留缓冲
            // 无任务引用）时清空；目标会话若正有任务在后台跑（多模型决策/编排执行），运行缓冲原样保留——任务不中断、
            // 切回后运行进度与结果完整可查（打开正在后台运行的会话不误杀、不误清）。
            ResetSessionBuffers(TaskOf(session));
            qCardSeq = 0;   // 打开会话：澄清问题序号从 1 重新编号
            openRunId = null;   // 切回普通会话：快照键回到会话 Id
            // P 需求2：切换会话——顶部 角色/模式/模型 选择器回显该会话钉住的执行参数（未钉住的新会话保持顶部现状，作为首任务取样源）
            SyncTopSelectorsToSession(session);
            // 打开已有会话时，若已带标题则视为标题已确定，后续保存不再回退到用户首句话
            sessionTitleSet = !string.IsNullOrWhiteSpace(session.Title) && session.Title != "新会话";
            // 会话标题/标识同步给本会话已有的 Runner（若有）：钉钉通知用最新会话标题、快照按会话 Id 落盘；
            // 无 Runner 的会话跳过（首任务 EnsureRunner 建时按会话装配）。后台任务的归属 Runner 各持己标识，互不覆盖。
            var targetRunner = RunnerOf(session);
            if (targetRunner != null)
            {
                targetRunner.Loop.SetSessionTitle(sessionTitleSet ? session.Title : "");
                targetRunner.Loop.SetSessionKey(session.Id);
            }

            // SaveCurrentSession 以旧 currentSession 为“saved”会把它标为选中，
            // 所以切换完成后必须重新高亮真正要打开的会话。
            foreach (var s in sessionHistory) { s.Selected = false; s.IsCurrent = false; }
            session.Selected = true;
            session.IsCurrent = true;

            if (session.Messages.Count == 0)
            {
                // 空会话（新建编排会话/打开空历史）：仍须切换主持人 prompt 并刷新右侧编排面板，
                // 否则 orcPanel 残留上一编排会话的标题/文档/执行树，或编排会话不显示面板
                UpdateSessionPromptAndPanel();
                if (session.IsPending || session.Title == "新会话")
                {
                    // 点中的是「新会话」待发起态（内存占位还没发过首条消息、或已落盘的空「新会话」项）：
                    // 补上与 EnterBlankNewSession 一致的提示语，避免出现费解的“没有保存任何消息”，
                    // 之后正常发送仍由 EnsureSessionForSend 就地转正落盘
                    AddMessage(new ChatMessage
                    {
                        Kind = MsgKind.Agent,
                        Who = "概尔 Agent",
                        Text = $"新会话已开始。已接入真实模型（{ProviderLabel()}），把任务交给我就行。",
                    });
                }
                else
                {
                    AddMessage(new ChatMessage
                    {
                        Kind = MsgKind.Agent,
                        Who = "GAIRR",
                        Text = $"会话「{session.Title}」没有保存任何消息。",
                    });
                }

                return;
            }
            // 先构建完整消息列表，再一次性清空并批量填充，避免逐条触发 CollectionChanged 导致布局风暴
            var loaded = new List<ChatMessage>(session.Messages.Count);
            // P 需求1/2：头像标题 = 会话钉住的 角色+模式+模型（历史消息悬停头像可见）；已钉住会话的历史消息
            // 标题文本一并重写为钉住参数（该会话各轮本就按同一钉住参数执行）
            var loadedPinText = SessionPinText(session);
            foreach (var m in session.Messages)
            {
                var msg = new ChatMessage
                {
                    Kind = Enum.TryParse<MsgKind>(m.Kind, out var k) ? k : MsgKind.Agent,
                    Who = m.Who ?? "",
                    Text = m.Text ?? "",
                    Cmd = m.Cmd,
                    Note = m.Note,
                    Steps = m.Steps,
                    Finished = true,   // 历史会话已结束
                    ProcessOpen = false,   // 默认收缩
                };
                // P：带头像（Agent/工作中）历史消息的标题按【各消息自身落盘的执行参数快照】回填——
                // 历史各轮消息各自保留当时参数（顶部模型/会话中途改参不回溯已保存的旧消息，正是“头像右侧名称跟着顶部变”的根治）；
                // 旧历史无快照（RunXxx 全空）回退会话钉住参数文本（原统一重写口径，兼容老数据）
                if (msg.AgentVisible == Visibility.Visible)
                {
                    ApplyRecordSnapshot(msg, m);   // 六要素拷回消息（再次保存往返一致）
                    var snapText = SnapshotTextOf(m);
                    if (snapText != null)
                    {
                        msg.AvatarTitle = snapText;
                        // 头像右侧文本（Who 后缀）按该消息自身快照重建，而非全会话统一参数
                        if (msg.Who.Contains("概尔 Agent", StringComparison.OrdinalIgnoreCase))
                            msg.Who = "概尔 Agent · " + snapText;
                    }
                    else
                    {
                        msg.AvatarTitle = loadedPinText;
                        if (!string.IsNullOrWhiteSpace(session.PinProvider) &&
                            msg.Who.Contains("概尔 Agent", StringComparison.OrdinalIgnoreCase))
                            msg.Who = "概尔 Agent · " + loadedPinText;
                    }
                }
                // 惰性装配：时间线卡片不立即实例化，挂 PendingItems 待用户首次展开该轮时再建（OnStepsClick → MaterializePending）
                msg.PendingItems = ToPendingRecords(m);
                // 清理旧 Steps 中的展开/收缩后缀，重建基础文本和显示文本
                var baseText = (m.Steps ?? "").Replace(" · ▸ 展开", "").Replace(" · ▾ 收缩", "");
                msg.StepsBase = baseText;
                // 有过程内容（思考条/工具卡）时才提供展开/收缩交互
                msg.Steps = baseText + (msg.HasProcessContent ? StepsHint(msg.ProcessOpen) : "");
                RestoreQuestionCards(msg, m);   // 还原澄清卡片（题面/作答进度/折叠状态）：中断续选的基础
                RestoreRoundChanges(msg, m, session.Project);   // 还原本轮改动清单（消息底部条带）；存量历史无记录不动
                loaded.Add(msg);
            }
            // ===== 分段装配（长会话打开优化）=====
            // 先只装"最近一个回合"（最后一条 User 起），更早的消息暂存 pendingOlderMsgs；随后由 FillOlderUntilOneScreen
            // 按"不足一屏就继续向前补装"补齐。这样打开上百回合的富卡片会话时，首屏只需实例化/布局少量消息，不再全量渲染。
            ResetMsgView();
            int cut = LastTurnStart(loaded);
            for (int i = cut; i < loaded.Count; i++) messages.Add(loaded[i]);
            for (int i = 0; i < cut; i++) pendingOlderMsgs.Add(loaded[i]);
            RestoreBgWorkMsg(session);   // 验收7 切回恢复：本会话主任务在后台跑时把活的工作消息接回视口尾部
            FillOlderUntilOneScreen();   // 不足一屏则继续向前逐回合补装（布局完成后测量，超过一屏即停）
            RestoreSessionChanges(session);   // 会话累计清单：按各消息落盘的改动记录重建左栏“本会话改动”卡片

            // 历史已完成会话（无 Runner）：不在此重建任何大模型上下文——首任务 EnsureRunner 建 Runner 时按本
            // 会话消息装载历史；已有 Runner 的会话：Runner 历史即上次任务尾，视口切换不重载/不清空任何会话上下文。
            // 打开历史会话：内容一次性批量填充，立即滚动会与布局互相触发导致卡死，
            // 改为等本帧布局完成（UI 线程空闲）后再滚到底
            ScrollToBottomAfterLayout();
            // 编排会话切主持人 prompt（plan-orchestration.md）并显示右侧信息面板；普通会话回 agent-deep.md
            UpdateSessionPromptAndPanel();
            RefreshOrcQProgress();   // 澄清进度首刷：卡片已全部还原进消息列表（还原路径在此前只订阅不统计）
            // 两会话并行：切回某会话时若该会话的多模型方案曾在后台收口成功并挂起（当时前台在别的会话，
            // 见 OnMultiPlanDone），在此补弹确认框——复用前台收口的确认→approved 落盘全链路
            var sessionRec = TaskOf(session);
            var pendingPlan = sessionRec?.PendingPlan;
            if (pendingPlan != null && session.OrchPhase == "done")
            {
                sessionRec!.PendingPlan = null;   // 一次性消费：确认/取消后如需调整可重新生成并再次挂起
                ConfirmOrchestrationPlan(pendingPlan);
            }
            // 生成阶段遗留恢复（重启中断 / EventBg 后台 Finished 收口被跳过 / 前台收口异常未复位）：本会话无任何
            // 在跑任务却仍滞留 generating——若历史末条回复即已产出的任务树则补弹确认（与上面 PendingPlan 同口径，
            // 复用确认 → approved 落盘全链路），否则复位回 collecting，避免“任务树生成中”永久卡死本会话输入
            if (session.IsOrchestration && session.OrchPhase == "generating"
                && (sessionRec == null || !sessionRec.AnyRunning))
            {
                TryRestoreStaleGenerating(session);
            }
            // 两会话并行：主对话 Runner 计划审批（PlanMode）若在切走期间挂起（切走时后台不弹，见事件泵
            // PlanPending EventBg 分支），切回本会话在此补弹审批——批准/拒绝后挂起解除，角标经 finally 的
            // RefreshPendingBadges 即时熄灭（与上面多模型方案 PendingPlan 补弹互斥挂起、不会同时命中）
            var sRunner = sessionRec?.Runner;
            if (sRunner?.Loop.PlanPending == true && sRunner.Loop.PendingPlan is { } sp)
            {
                sRunner.Loop.ResolvePlan(PromptPlanApproval(sp));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Error] 打开会话失败: {ex.Message}");
            AddMessage(new ChatMessage
            {
                Kind = MsgKind.Agent,
                Who = "GAIRR",
                Text = "打开会话失败：" + ex.Message,
            });

        }
        finally
        {
            openingSession = false;   // 正常结束/空会话早退/异常均复位，防永久锁死后续点击
            // 两会话并行：切换后把发送区刷新到“当前会话视角”——后台多模型决策挂在发起会话的记录上，
            // 切到的其它会话不残留其 stop/隐藏发送按钮（前台主任务/编排执行仍在跑时由 SetSendBusy 的 isBusy 兜住保持中止态）
            SetSendBusy(false);
            // 验收7 切回恢复：目标会话的主任务（cts）在后台跑时，发送区恢复"中止"态（原复位会显示成可发送）；
            // SetSendBusy 代理此时锚当前会话（已切换）的归属记录，stop 即停该后台任务
            if (TaskOf(session) is { Cts: not null }) SetSendBusy(true);
            SyncViewportFlags();   // 会话切换完成：目标会话 Runner 置视口，切走会话挂起转后台短静默补发
            RefreshPendingBadges();   // 切回会话成为当前：其角标即时熄灭；切走会话若仍挂起则即时点亮
        }
    }

    /// <summary>删除会话（悬停时显示的 ✕ 图标）：目标会话有任务在跑时先弹确认——用户确认则一并中止其任务后删除，
    /// 拒绝则取消不删；中止只作用于目标会话自己的记录（TaskOf 定点），不影响其它会话任务与 runTasks。</summary>
    void OnSessionDelete(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;   // 阻止冒泡到 OnSessionClick
        if (sender is not FrameworkElement fe || fe.DataContext is not SessionItem session) return;
        // "当前会话"判定以实际绑定的 currentSession 为准、Selected 高亮为辅：
        // 防止 Selected 高亮标记漂移导致运行中的会话被误删；其他会话不受影响
        bool isCurrent = ReferenceEquals(session, currentSession) || session.Selected;
        // 会话自身有活动任务（前台主任务/后台多模型决策/编排执行——两会话并行下各自挂在会话记录上）。
        // 原实现直接禁止删除（删除会让后台收口/补弹确认写到孤儿对象）；现改为弹确认——确认后先中止其任务再删除：
        // 中止口径同 OnStop（编排→主对话/产树→多模型后台决策）；中止后的孤儿收口走两会话并行的"孤儿守卫"分流
        // （Bus 事件按会话 Key 过滤、直写归属记录/ShowToast），不会串写其它会话 UI 或记录
        var delRec = TaskOf(session);
        if (delRec?.AnyRunning == true)
        {
            if (MessageBox.Show(this,
                    "该会话仍有任务在运行（主对话任务/多模型决策/编排执行）。\n\n是否一并中止其任务并删除该会话？",
                    "中止任务并删除会话",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
                != MessageBoxResult.Yes)
            {
                mStatus.Text = "未删除：任务继续运行。可先停止任务或等待其完成后再删除。";
                return;
            }
            // 统一中止（本叶子）：停其全部任务源（编排执行器/主对话与产树/多模型决策）与打字机、危险卡定时器、
            // 释放记录运行字段（删除=用户丢弃该会话，不保存工作消息快照）；随后 rec 随 runTasks.Remove 摘出，
            // 孤儿收口事件按 drain 归属守卫丢弃，不影响其它会话记录/UI
            StopSessionRunner(delRec, persist: false);
        }
        sessionHistory.Remove(session);
        runTasks.Remove(session.Id);   // 连运行记录一并移除（中止后孤儿收口按守卫分流，无需再读本记录）
        SaveSessionHistory();
        RefreshSessionList();

        // 如果删除的是当前正在使用的会话，清空会话区并新建会话等待用户发消息
        if (isCurrent)
        {
            currentSession = null;   // 先摘除当前会话：其后各代理 setter（workMsg=null 等）不再 EnsureRec 重建已删会话记录，runTasks.Remove 才真正生效
            if (delRec?.Cts != null) { delRec.Cts.Dispose(); delRec.Cts = null; }   // 主任务已在中止分支 Cancel：经 delRec 引用直接释放记录字段（不走代理，防 EnsureRec 重建）
            SetSendBusy(false);   // 被删会话任务已中止：立即复位发送区供新会话使用（孤儿收口若被 Bus 事件过滤，此句兜底解锁）
            workMsg = null;
            pendingStream = null;      // 清掉打字机积压队列，防止残留文本串到新任务
            streamTicker?.Stop();      // currentSession 已摘除、CurRec=null：此句空转无害（真停用已由删除分支统一 delRec 完成）
            cardMap.Clear();
            stepCardMap.Clear();
            accTokens = 0;

            // 删掉当前会话后进入待发起态：清标题暂存（含 sessionTitleSet，否则下个会话的
            // 首句话不再自动定标题）/澄清序号/消息区，刷新 TodoBar/顶栏/提示词面板、选择器回「🤖 自动匹配」，
            // 末尾 SyncViewportFlags。withPending=false：**不**在左侧列表顶部插入「新会话」占位项——
            // 直接删除后不再显示到顶部（否则顶部凭空多出一条占位项，再点别的会话又被 DropPendingSession 摘掉，
            // 观感就像"删掉的会话标题移到顶部后又消失"）；首条消息发出时由 EnsureSessionForSend 新建会话项置顶落盘。
            // 必须放在上面这串运行态复位之后——本方法末尾会把 currentSession 置 null（占位模式则指向占位项），
            // 提前调用会让 workMsg/pendingStream 等代理 setter 重新锚到（占位）会话上（EnsureRec 又给已删会话建回记录）
            EnterBlankNewSession(withPending: false);
        }
        RefreshPendingBadges();   // 删除会话：其记录已随 runTasks.Remove 摘除，其余会话角标按各自挂起状态归正/即时点亮
    }

    /* ================= 自动任务 ================= */

    /// <summary>新增任务：打开编辑弹窗，保存到自动任务列表并启动调度。</summary>
    void OnAddTaskClick(object sender, RoutedEventArgs e)
    {
        EditTask(null);
    }

    /// <summary>单击任务卡片：选中该任务并筛选任务执行历史（只显示该任务的记录）；500ms 内重复单击同一任务视为双击，打开编辑弹窗。若点中开关则忽略，由开关 Click 处理。</summary>
    void OnTaskCardClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not TaskItem task) return;
        if (IsTaskSwitchClick(e.OriginalSource as DependencyObject, fe)) return;

        var now = DateTime.Now;
        if (ReferenceEquals(lastClickTask, task) && (now - lastClickTime).TotalMilliseconds <= 500)
        {
            // 双击：打开设置窗口；保留单击时已建立的选中态（编辑完仍停留在该任务的筛选视图）
            lastClickTask = null;
            EditTask(task);
        }
        else
        {
            // 单击：选中并筛选执行历史
            lastClickTask = task;
            lastClickTime = now;
            SetSelectedTask(task);
        }
        e.Handled = true;
    }

    /// <summary>设置当前选中的任务（单选互斥），并按选中态刷新任务执行历史列表。</summary>
    void SetSelectedTask(TaskItem? task)
    {
        if (ReferenceEquals(selectedTask, task)) return;
        if (selectedTask != null) selectedTask.IsSelected = false;
        selectedTask = task;
        if (task != null) task.IsSelected = true;
        RefreshTaskRuns();
    }

    /// <summary>判断点击来源是否在任务卡片的开关内部。</summary>
    static bool IsTaskSwitchClick(DependencyObject? source, FrameworkElement container)
    {
        while (source != null)
        {
            if (source is System.Windows.Controls.Primitives.ToggleButton tb && tb != container)
                return true;
            if (source == container)
                return false;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    /// <summary>右键任务卡片：显示"立即执行"和"删除"菜单。</summary>
    void OnTaskCardRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not TaskItem task) return;
        var menu = new ContextMenu();
        var run = new MenuItem { Header = "立即执行一次" };
        run.Click += (_, _) => _ = Task.Run(async () => await taskScheduler!.RunTaskNow(task));

        var editPrompt = new MenuItem { Header = "编辑自动任务提示词" };
        editPrompt.Click += (_, _) =>
        {
            AgentLoop.EnsureAutoTaskPrompt(cfg);
            var path = System.IO.Path.Combine(cfg.ProjectRoot, ".gairr", "prompts", "autotask-run.md");
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                bgStatus.Text = $"已打开: {path}";
            }
            catch (Exception ex)
            {
                bgStatus.Text = $"打开提示词失败: {ex.Message}";
            }
        };

        var del = new MenuItem { Header = "删除任务" };
        del.Click += (_, _) =>
        {
            autoTasks.Remove(task);
            var savedPath = taskScheduler?.SaveTasks();
            if (savedPath != null) bgStatus.Text = $"任务已删除: {savedPath}";
            else bgStatus.Text = "任务保存失败";
            // 删除的是选中任务时重置选中态，恢复显示全部历史
            if (ReferenceEquals(selectedTask, task)) SetSelectedTask(null);
        };

        menu.Items.Add(run);
        menu.Items.Add(editPrompt);
        menu.Items.Add(del);
        menu.PlacementTarget = fe;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>任务开关切换：双向绑定已更新 Enabled，这里重新计算下次执行时间并保存。</summary>
    void OnTaskToggle(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not TaskItem task) return;
        task.NextRun = task.Enabled ? task.GetNextRun() : null;
        task.Notify(nameof(TaskItem.DotBrush));
        var savedPath = taskScheduler?.SaveTasks();
        if (savedPath != null) bgStatus.Text = $"任务状态已更新: {savedPath}";
        else bgStatus.Text = "任务保存失败";
        e.Handled = true;
    }

    /// <summary>新增/编辑任务并保存。</summary>
    void EditTask(TaskItem? task)
    {
        AutoTaskViewModel? init = task == null ? null : new()
        {
            Title = task.Title,
            Prompt = task.Prompt,
            ExecutionPrompt = task.ExecutionPrompt,
            FlowMode = task.FlowMode,   // 工作模式：Flow 模板名，空=自主
            Type = task.ScheduleType,
            Hour = task.Hour,
            Minute = task.Minute,
            WeekDay = task.WeekDay,
            MonthDay = task.MonthDay,
            CronExpression = task.CronExpression,
        };
        var dlg = new AutoTaskDialog(init) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var r = dlg.Result;
        var item = task ?? new TaskItem();
        item.Title = r.Title;
        item.Prompt = r.Prompt;
        item.ExecutionPrompt = r.ExecutionPrompt;   // 任务执行步骤提示词（与任务目标描述分开维护）
        item.FlowMode = r.FlowMode;                 // 工作模式：Flow 模板名，空=自主
        item.ScheduleType = r.Type;
        item.Hour = r.Hour;
        item.Minute = r.Minute;
        item.WeekDay = r.WeekDay;  // AutoTaskDialog 已做 SelectedIndex+1 转换，直接使用模型值
        item.MonthDay = r.MonthDay;
        item.CronExpression = r.CronExpression;
        item.Enabled = true;
        item.NextRun = item.GetNextRun();
        if (task == null) autoTasks.Add(item);

        // 首次新建/编辑自动任务时，确保项目级 autotask-run.md 已预热，
        // 避免第一次“立即执行”因生成错误提示词而被浪费。
        AgentLoop.EnsureAutoTaskPrompt(cfg);

        var savedPath = taskScheduler?.SaveTasks();

        // 创建后立即运行一次（后台不阻塞 UI）：成功后自动总结生成执行步骤提示词
        if (task == null)
        {
            _ = Task.Run(async () => await taskScheduler!.RunTaskNow(item));
            if (savedPath != null) bgStatus.Text = $"任务已创建并立即执行: {savedPath}";
            else bgStatus.Text = "任务保存失败，无法立即执行";
            return;
        }

        if (savedPath != null) bgStatus.Text = $"任务已保存: {savedPath}";
        else bgStatus.Text = "任务保存失败";
    }

    /// <summary>自动任务调度器回调：用独立 AgentLoop 实例在后台执行任务，失败时按序换模型重试，记录完整过程。</summary>
    async Task RunAutoTaskAsync(TaskItem task, CancellationToken ct)
    {
        var recorder = new TaskRunRecorder();
        var record = new TaskRunRecord
        {
            TaskId = task.Id,
            TaskTitle = task.Title,
            Prompt = task.Prompt,
        };

        // 归属项目根（验收 8）：任务随项目隔离，运行中切项目后实时 cfg.ProjectRoot 已指向新项目；
        // 执行循环/落盘/收口统一用发起时捕获的根（tcfg），防工具执行与运行记录、git 提交串写新项目根。
        var tcfg = cfg.ViewForProjectRoot(cfg.ProjectRoot);

        // 任务开始即落盘"执行中"骨架记录（FinishedAt 为空）：即使中途程序关闭/崩溃，执行历史也有迹可循；
        // 任务结束后用完整记录覆盖同名文件（同 Id+StartedAt 路径一致）
        try
        {
            TaskRunStore.Save(tcfg, record);
            _ = Dispatcher.BeginInvoke(new Action(RefreshTaskRuns));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Error] 保存任务运行骨架记录失败: {ex.Message}");
        }

        // 执行话术：任务目标 + 已验证的执行步骤提示词（有则固定复用）
        var execText = string.IsNullOrWhiteSpace(task.ExecutionPrompt)
            ? task.Prompt
            : task.Prompt + "\n\n【已验证的执行步骤（请严格按以下步骤执行）】\n" + task.ExecutionPrompt;

        // 模型故障转移：按"记住的可用供应商优先"顺序依次尝试，首个成功的供应商写回记忆
        var order = tcfg.AutoTaskProviderOrder();
        AgentLoop? tloop = null;
        string? usedProvider = null;
        var failReasons = new List<string>();
        bool success = false;
        string result = "";

        using var autoAllow = AgentLoop.AutoAllowDangerScope();
        foreach (var provider in order)
        {
            ct.ThrowIfCancellationRequested();
            tloop = new AgentLoop(tcfg, registry, new ChangeJournal(tcfg), skillLoader, "autotask-run.md");
            // 工作模式：任务配置的 Flow 模板（工具级 flows/*.json）；空=自主模式（原逻辑不变）
            tloop.Flow = GAIRR.AgentHost.FlowTemplateStore.Find(task.FlowMode)?.Steps;
            tloop.SwitchProviderLocal(provider);
            recorder.Reset();
            if (failReasons.Count > 0)
                recorder.AddNote("上一模型尝试失败，自动切换：" + string.Join("；", failReasons));
            usedProvider = provider;

            using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _ = Task.Run(async () =>
            {
                while (!pollCts.Token.IsCancellationRequested)
                {
                    recorder.Drain(tloop!.Bus);
                    UpdateTaskProgressUi(task, recorder);   // 实时刷新任务卡片上的运行进度
                    try { await Task.Delay(200, pollCts.Token); }
                    catch (OperationCanceledException) { break; }
                }
            }, pollCts.Token);

            try
            {
                await tloop.RunAsync(execText, ct);
                if (tloop.LastRunSuccess)
                {
                    success = true;
                    result = "任务执行成功（模型：" + provider + "）";
                    break;
                }
                // 用户主动取消：不切模型重试，直接结束
                if (tloop.LastFailReason.Contains("取消"))
                {
                    result = "任务已取消";
                    break;
                }
                failReasons.Add($"{provider}：{tloop.LastFailReason}");
                LogAutoTask($"[AutoTask] 模型 {provider} 执行失败，尝试下一个：{tloop.LastFailReason}");
            }
            catch (OperationCanceledException)
            {
                result = "任务已取消";
                break;
            }
            catch (Exception ex)
            {
                failReasons.Add($"{provider}：{ex.Message}");
                LogAutoTask($"[AutoTask] 模型 {provider} 执行异常：{ex.Message}");
            }
            finally
            {
                pollCts.Cancel();
                recorder.Drain(tloop.Bus);
            }
        }

        if (!success && result == "")
            result = "所有模型均执行失败：\n" + string.Join("\n", failReasons);

        record.FinishedAt = DateTime.Now.ToString("O");
        record.Success = success;
        record.Result = result;
        record.Rounds = recorder.TotalRounds;
        record.Tokens = recorder.TotalTokens;
        recorder.Finish(result, success);
        record.Messages = recorder.Messages;

        // 记住本次成功的供应商，下次优先复用；全部失败则清空记忆，下次从头按序重试
        if (success)
        {
            cfg.AutoTaskProvider = usedProvider ?? "";
            try { cfg.Write(); } catch { }
        }
        else if (!result.Contains("取消"))
        {
            cfg.AutoTaskProvider = "";
            try { cfg.Write(); } catch { }
            _ = SendAutoTaskFailDingTalkAsync(task, result);   // 全部模型失败：钉钉通知（失败原因已记录在执行历史）
        }

        // 首次成功且尚无执行步骤提示词：自动总结生成（失败不影响本次"成功"状态，下次成功重试总结）
        if (success && string.IsNullOrWhiteSpace(task.ExecutionPrompt) && tloop != null)
            await SummarizeExecutionPromptAsync(task, tloop, ct);

        // 自动任务收尾同样落 git（[Git] 启用时）：key=运行记录短 id，可在左侧“项目跟踪”分组反查任务执行变更
        var goc = new GitCommitOutcome();
        try
        {
            goc = await GitMgr.AutoCommitAsync(tcfg, task.Prompt, recorder.TotalRounds, recorder.TotalTokens, record.Id);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Error] 自动任务 git 提交失败: {ex.Message}");
        }

        try
        {
            TaskRunStore.Save(tcfg, record);
            Dispatcher.Invoke(() => { RefreshTaskRuns(); });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Error] 保存任务运行历史失败: {ex.Message}");
        }
        // git 通知独立异步：ShowStatAsync 走进程调用不能占 UI 线程；拉变更清单分类后回 UI 插入/收纳
        if (goc.Created)
            _ = Task.Run(async () =>
            {
                var fl = new List<GitFileStat>();
                try { fl = await GitMgr.ShowStatAsync(tcfg, goc.Hash); } catch { }
                _ = Dispatcher.InvokeAsync(() => NotifyGitCommit(goc, fl));
            });
    }

    /// <summary>按后台记录器的轮次与待办完成度，在 UI 线程刷新任务卡片的进度文本与百分比。</summary>
    void UpdateTaskProgressUi(TaskItem task, TaskRunRecorder recorder)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (task.running == 0) return;   // 已结束不再更新
            var rounds = recorder.TotalRounds;
            if (recorder.TodoTotal > 0)
            {
                task.ProgressText = $"进行中 · 待办 {recorder.TodoDone}/{recorder.TodoTotal} · 第 {rounds} 轮";
                task.ProgressPercent = recorder.TodoDone * 100.0 / recorder.TodoTotal;
            }
            else
            {
                // 模型未建待办清单：按轮次估算进度（上限 90%，避免误显 100%）
                task.ProgressText = $"进行中 · 第 {rounds} 轮";
                task.ProgressPercent = rounds <= 0 ? 10 : Math.Min(90, 20 + rounds * 10.0);
            }
        });
    }

    /// <summary>自动任务执行日志（写 agent.log，便于排查模型故障转移过程）。</summary>
    void LogAutoTask(string msg)
    {
        try
        {
            System.IO.File.AppendAllText(Paths.AgentLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\n");
        }
        catch { }
    }

    /// <summary>首次成功后自动总结执行步骤提示词：复用同一 AgentLoop 上下文追加一轮总结请求，写入 task.ExecutionPrompt 并保存。</summary>
    async Task SummarizeExecutionPromptAsync(TaskItem task, AgentLoop tloop, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(5));
            await tloop.RunAsync(
                "本次任务已成功完成。请把本次实际采用的执行步骤总结为一份可复用的「任务执行步骤提示词」：用编号列表，每步说明做什么、用什么工具/命令、关键文件或参数；只输出步骤本身，不要背景与寒暄。后续将直接按此步骤固定执行。",
                cts.Token);
            if (!tloop.LastRunSuccess) return;

            var text = tloop.GetLastAssistantMessage();
            if (string.IsNullOrWhiteSpace(text)) return;
            text = CleanExecPrompt(text);
            if (text.Length == 0) return;

            task.ExecutionPrompt = text;
            var savedPath = taskScheduler?.SaveTasks();
            LogAutoTask($"[AutoTask] 已生成执行步骤提示词（任务：{task.Title}）保存到 {savedPath}");
        }
        catch (Exception ex)
        {
            LogAutoTask($"[AutoTask] 总结执行步骤提示词失败（不影响本次成功状态）: {ex.Message}");
        }
    }

    /// <summary>清洗模型输出的步骤提示词：去代码块围栏、首尾空白。</summary>
    static string CleanExecPrompt(string text)
    {
        text = text.Trim();
        if (text.StartsWith("```"))
        {
            var firstNl = text.IndexOf('\n');
            if (firstNl >= 0) text = text.Substring(firstNl + 1);
            text = text.TrimEnd();
            if (text.EndsWith("```")) text = text.Substring(0, text.Length - 3);
        }
        return text.Trim();
    }

    /// <summary>日志文本截断（防超长）。</summary>
    static string TruncForLog(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "…";

    /// <summary>自动任务所有模型均失败时发送钉钉通知（独立于 AutoDingTalk 开关，失败原因同时已记入执行历史）。</summary>
    async Task SendAutoTaskFailDingTalkAsync(TaskItem task, string failReason)
    {
        var webhook = cfg.DingTalkWebhook;
        if (string.IsNullOrEmpty(webhook))
        {
            LogAutoTask("[AutoTask] 未配置 DingTalkWebhook，跳过钉钉失败通知");
            return;
        }
        var title = "自动任务失败：" + task.Title;
        var content = $"**任务**：{task.Title}\n\n**时间**：{DateTime.Now:yyyy-MM-dd HH:mm}\n\n**失败原因**（所有模型均失败）：\n{failReason}";
        try
        {
            var url = webhook;
            var secret = cfg.DingTalkSecret;
            if (!string.IsNullOrEmpty(secret))
            {
                var ts = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToString();
                using var hmac = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(secret));
                var sign = Convert.ToBase64String(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(ts + "\n" + secret)));
                url += (webhook.Contains("?") ? "&" : "?") + "timestamp=" + ts + "&sign=" + Uri.EscapeDataString(sign);
            }
            var http = LLMClient.Http;
            var payload = System.Text.Json.JsonSerializer.Serialize(new { msgtype = "markdown", markdown = new { title, text = content } });
            var resp = await http.PostAsync(url, new StringContent(payload, System.Text.Encoding.UTF8, "application/json"));
            var body = await resp.Content.ReadAsStringAsync();
            LogAutoTask($"[AutoTask] 钉钉失败通知发送{(resp.IsSuccessStatusCode ? "成功" : "失败")}: {TruncForLog(body, 200)}");
        }
        catch (Exception ex)
        {
            LogAutoTask($"[AutoTask] 钉钉失败通知发送异常: {ex.Message}");
        }
    }

    /// <summary>加载并刷新任务执行历史列表：当前项目全部记录，选中任务时只显示该任务的记录。</summary>
    void RefreshTaskRuns()
    {
        try
        {
            taskRuns.Clear();
            var all = TaskRunStore.LoadAll(cfg);
            foreach (var r in all)
            {
                if (selectedTask != null && r.TaskId != selectedTask.Id) continue;
                // FinishedAt 为空 = 任务开始时落盘的骨架记录：可能仍在执行中，或上次异常退出未覆盖
                var running = string.IsNullOrWhiteSpace(r.FinishedAt);
                taskRuns.Add(new TaskRunItem
                {
                    Id = r.Id,
                    TaskId = r.TaskId,
                    Title = string.IsNullOrWhiteSpace(r.TaskTitle) ? r.TaskId : r.TaskTitle,
                    Result = running && string.IsNullOrWhiteSpace(r.Result) ? "执行中…" : r.Result,
                    Time = r.StartedTime,
                    Rounds = r.Rounds,
                    Tokens = r.Tokens,
                    Success = r.Success,
                    Running = running,
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Error] 加载任务运行历史失败: {ex.Message}");
        }
    }

    /// <summary>点击"任务执行历史"标题旁的刷新按钮：重新从磁盘读取全部执行记录（含运行中任务刚落盘的骨架记录）。</summary>
    void OnRefreshTaskRuns(object sender, RoutedEventArgs e)
    {
        RefreshTaskRuns();
        bgStatus.Text = "任务执行历史已刷新";
    }

    /// <summary>点击任务执行历史项：在右侧对话区回放完整过程；任一前台主任务在跑时禁止打开（后台任务放行，两会话并行）。</summary>
    void OnTaskRunClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not TaskRunItem run) return;
        // 两会话并行口径：只拦当前视口会话正在跑的主对话/产树任务（回放会把视口切走，其流式事件将失去归属
        // 视口，任务转为后台、收口直写会话记录——应等当前流式任务结束再看历史）。其它会话的后台主任务、多模型
        // 决策、编排执行均放行：回放视口按会话归属过滤把它们的事件挡在视口外，且回放不触碰任何会话 Runner。
        if (CurRec is { Cts: not null })
        {
            mStatus.Text = "任务进行中，请先等待任务完成或点击停止后再查看任务历史";
            return;
        }
        OpenTaskRun(run);
        e.Handled = true;
    }

    /// <summary>打开任务运行记录并在对话区回放。</summary>
    void OpenTaskRun(TaskRunItem run)
    {
        try
        {
            var list = TaskRunStore.LoadAll(cfg);
            var record = list.FirstOrDefault(r => r.Id == run.Id);
            if (record == null)
            {
                AddMessage(new ChatMessage
                {
                    Kind = MsgKind.Agent,
                    Who = "GAIRR",
                    Text = "未找到该任务执行记录。",
                });

                return;
            }
            openRunId = run.Id;   // 回放期间快照目录键指向本次运行；切回会话/新对话时置空

            // 从会话视口进入回放才保存当前会话；已在回放视口（currentSession==null）切到另一条记录时
            // 无需保存——回放消息为只读历史，既不写会话记录也不误建新会话
            if (currentSession != null) SaveCurrentSession();
            // P6②（两会话并行）口径：打开回放视口不取消/不触碰任何会话 Runner（本视口主任务已被 OnTaskRunClick
            // gate 拦下；后台多模型决策 multiGenCts / 编排执行 planRunner / 其它会话任务照跑，收口直写所属会话
            // 记录、被回放视口的会话归属过滤挡在视口外，不串台），也不清离开会话记录上的运行缓冲（后台任务进度/
            // 收口依赖它们，切回原会话后仍完整可查）；只复位视口级标题暂存与对话区消息，随后将 currentSession
            // 置空（回放视口无会话归属）。
            pendingSessionTitle = null;
            sessionTitleSet = false;
            ResetMsgView();   // 回放视口无会话历史（消息随后由回放记录单条填充），一并复位分段加载暂存
            currentSession = null;
            qCardSeq = 0;   // 退出会话：澄清问题序号复位
            // 回放视口无会话归属：把"点了新会话但没发消息"的内存占位项一并摘掉，
            // 否则左侧列表会留着一条已取消高亮却仍存在的空「新会话」（currentSession 已置空，DropPendingSession 不再保护它）
            DropPendingSession();
            SyncViewportFlags();   // 回放视为脱离视口：全部 Runner 置非视口，其后台挂起转短静默补发

            AddMessage(new ChatMessage
            {
                Kind = MsgKind.Agent,
                Who = "GAIRR",
                Text = BuildTaskRunSummary(record),
            });

            if (record.Messages.Count == 0)
            {
                AddMessage(new ChatMessage
                {
                    Kind = MsgKind.Agent,
                    Who = "GAIRR",
                    Text = "该记录没有保存执行过程内容。",
                });
            }
            else
            {
                var msgs = record.Messages;
                for (int i = 0; i < msgs.Count; i++)
                {
                    var m = msgs[i];
                    var text = m.Text ?? "";
                    // 最后一条且与记录结论一致：按格式符美化展示（存储内容保持不变）
                    if (i == msgs.Count - 1 && text.Length > 0 && text == record.Result)
                        text = BeautifyConclusion(text, record.Success);
                    var msg = new ChatMessage
                    {
                        Kind = Enum.TryParse<MsgKind>(m.Kind, out var k) ? k : MsgKind.Agent,
                        Who = m.Who ?? "GAIRR",
                        Text = text,
                        Cmd = m.Cmd,
                        Note = m.Note,
                        Steps = m.Steps,
                        Finished = true,
                        ProcessOpen = false,
                    };
                    // 惰性装配：时间线卡片不立即实例化，挂 PendingItems 待用户首次展开该轮时再建（OnStepsClick → MaterializePending）
                    msg.PendingItems = ToPendingRecords(m);
                    var baseText = (m.Steps ?? "").Replace(" · ▸ 展开", "").Replace(" · ▾ 收缩", "");
                    msg.StepsBase = baseText;
                    msg.Steps = baseText + (msg.HasProcessContent ? StepsHint(msg.ProcessOpen) : "");
                    messages.Add(msg);
                }
            }

            ScrollToBottom();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Error] 打开任务运行历史失败: {ex.Message}");
            AddMessage(new ChatMessage
            {
                Kind = MsgKind.Agent,
                Who = "GAIRR",
                Text = "打开任务运行历史失败：" + ex.Message,
            });

        }
    }

    /// <summary>按成功/失败/取消/执行中返回状态图标与文案。
    /// FinishedAt 为空 = 任务开始即落盘的骨架记录（尚未覆盖）：
    /// 1 小时内视为执行中；超过 1 小时视为上次异常退出留下的残留记录（不误导为"失败"）。</summary>
    static (string Icon, string Status) TaskRunStatus(TaskRunRecord record)
    {
        if (record.Success) return ("✅", "成功");
        if (record.Result.Contains("取消")) return ("⏹", "已取消");
        if (string.IsNullOrWhiteSpace(record.FinishedAt))
        {
            var elapsed = DateTime.Now - record.StartedTime;
            return elapsed > TimeSpan.FromHours(1)
                ? ("⚠️", "已中断（上次运行未正常结束）")
                : ("🔵", "执行中…");
        }
        return ("❌", "失败");
    }

    /// <summary>构建任务执行历史汇总消息（Markdown 格式符美化）</summary>
    static string BuildTaskRunSummary(TaskRunRecord record)
    {
        var (icon, status) = TaskRunStatus(record);
        var dur = "";
        if (record.FinishedAt is string fa && DateTime.TryParse(fa, out var f) && f >= record.StartedTime)
            dur = $"（{f - record.StartedTime:hh\\:mm\\:ss}）";
        var sb = new StringBuilder();
        sb.AppendLine($"## {icon} 任务执行历史");
        sb.AppendLine();
        sb.AppendLine($"**任务**：{record.TaskTitle}");
        sb.AppendLine($"**执行时间**：{record.StartedTime:yyyy-MM-dd HH:mm:ss} {dur}");
        sb.AppendLine($"**结果**：{icon} **{status}**");
        if (record.Prompt.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("> " + record.Prompt.Trim());
        }
        sb.AppendLine();
        sb.AppendLine($"---");
        // 未结束的骨架记录（FinishedAt 为空）不展示轮数/用量，避免把初始默认值 0 误认为执行结果
        if (string.IsNullOrWhiteSpace(record.FinishedAt))
        {
            sb.AppendLine("- 任务仍在执行中（或上次运行未正常落盘），结束后点击刷新查看结果");
            return sb.ToString();
        }
        sb.AppendLine($"- 执行轮数：{record.Rounds}");
        sb.AppendLine($"- Token 用量：{record.Tokens}");
        return sb.ToString();
    }

    /// <summary>结论消息按格式符美化：成功绿色勾 / 失败红色叉 / 取消中性图标，保留原文本</summary>
    static string BeautifyConclusion(string text, bool success)
    {
        var icon = success ? "✅" : text.Contains("取消") ? "⏹" : "❌";
        return $"**{icon} 结论**{Environment.NewLine}{text}";
    }

    void OnSkillItemClick(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not SkillDef skill) return;
            // 双击：把 路径+名称 插入会话输入框（供用户直接引用该技能）
            if (e.ClickCount >= 2)
            {
                InsertRefToInput(skill.Name, skill.Path);
                e.Handled = true;
                return;
            }
            // 单击切换启用/禁用
            skill.Enabled = !skill.Enabled;
            // 内置技能"危险命令拦截"：同步开关状态到 Phase1Tools
            if (skill.Name == "危险命令拦截")
                Phase1Tools.DangerInterceptionEnabled = skill.Enabled;
            // 持久化：目录技能写 config.ini [Skills] Disabled（重启后 SkillLoader 按黑名单过滤，Manifest 同步生效）
            if (skill.Path.Length > 0)
            {
                try { GAIRR.Core.SwitchStore.SaveSkill(skill.Name, skill.Enabled); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Error] 技能开关持久化失败: {ex.Message}"); }
            }
            e.Handled = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Error] 技能点击失败: {ex.Message}\n{ex.StackTrace}");
        }
    }

    void OnPluginItemClick(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not PluginDef plugin) return;
            // 双击：把 路径+名称 插入会话输入框
            if (e.ClickCount >= 2)
            {
                InsertRefToInput(plugin.Name, plugin.Path);
                e.Handled = true;
                return;
            }
            // 加载失败的插件不可切换启用/禁用
            if (!plugin.Loaded) return;
            plugin.Enabled = !plugin.Enabled;
            registry.SetEnabled(plugin.Name, plugin.Enabled);   // 修复：禁用后 Agent 不可见该插件工具
            try { GAIRR.Core.SwitchStore.SavePluginEnabled(plugin.Path, plugin.Enabled); }   // 持久化到插件目录 plugin.ini
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Error] 插件开关持久化失败: {ex.Message}"); }
            e.Handled = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Error] 插件点击失败: {ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>把 路径+名称 插入会话输入框当前光标处（路径为空时只插名称，如内置项）</summary>
    void InsertRefToInput(string name, string path)
    {
        var text = string.IsNullOrEmpty(path) ? name : path + " " + name;
        var pos = Math.Max(0, inputBox.SelectionStart);
        inputBox.Text = inputBox.Text.Insert(pos, text + " ");
        inputBox.SelectionStart = pos + text.Length + 1;
        inputBox.Focus();
    }

    void OnToolItemClick(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not ToolItemDef tool) return;
            // 切换启用/禁用状态
            tool.Enabled = !tool.Enabled;
            // 同步到 ToolRegistry（禁用后 Agent 不可见）
            registry.SetEnabled(tool.Name, tool.Enabled);
            // 持久化到 system.ini [Tools] Disabled（重启/CLI/Server 宿主同享，配置只写例外）
            try { GAIRR.Core.SwitchStore.SaveTool(tool.Name, tool.Enabled); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Error] 工具开关持久化失败: {ex.Message}"); }
            e.Handled = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Error] 工具点击失败: {ex.Message}\n{ex.StackTrace}");
        }
    }

    /* ---------- 加入到会话（各列表右键菜单） ---------- */

    // 右键命中的当前项缓存（PreviewMouseRightButtonDown 阶段沿视觉树上溯命中项预存，先于 ContextMenuOpening）
    object? _pendingRefItem = null;

    /// <summary>沿视觉树从右键命中点向上找 T 类型的 DataContext（ItemsControl 的 DataContext 是集合，必须上溯到行内元素）</summary>
    static T? FindItem<T>(DependencyObject el) where T : class
    {
        while (el != null)
        {
            if (el is FrameworkElement fe && fe.DataContext is T t) return t;
            el = VisualTreeHelper.GetParent(el);
        }
        return null;
    }

    /// <summary>工具行右键（按下）：沿视觉树上溯缓存 ToolItemDef，先于菜单打开。注意不要 Handled，否则 ContextMenu 不弹出</summary>
    void OnToolRightDown(object sender, MouseButtonEventArgs e)
    {
        _pendingRefItem = (e.OriginalSource as DependencyObject) is DependencyObject d ? FindItem<ToolItemDef>(d) : null;
    }

    /// <summary>工具右键菜单打开：未命中项时不弹菜单</summary>
    void OnToolMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_pendingRefItem is not ToolItemDef) e.Handled = true;
    }

    /// <summary>工具右键"加入到会话"：插入 @tool 名称 标记</summary>
    void OnToolAddToSession(object sender, RoutedEventArgs e)
    {
        if (_pendingRefItem is ToolItemDef t) InsertRefMarker("@tool " + t.Name);
        _pendingRefItem = null;
        e.Handled = true;
    }

    /// <summary>技能行右键（按下）：沿视觉树上溯缓存 SkillDef，先于菜单打开。注意不要 Handled，否则 ContextMenu 不弹出</summary>
    void OnSkillRightDown(object sender, MouseButtonEventArgs e)
    {
        _pendingRefItem = (e.OriginalSource as DependencyObject) is DependencyObject d ? FindItem<SkillDef>(d) : null;
    }

    /// <summary>技能右键菜单打开：未命中项时不弹菜单</summary>
    void OnSkillMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_pendingRefItem is not SkillDef) e.Handled = true;
    }

    /// <summary>技能右键"加入到会话"：插入 /技能名 标记（可强制触发 LoadSkill）</summary>
    void OnSkillAddToSession(object sender, RoutedEventArgs e)
    {
        if (_pendingRefItem is SkillDef s) InsertRefMarker("/" + s.Name);
        _pendingRefItem = null;
        e.Handled = true;
    }

    /// <summary>插件行右键（按下）：沿视觉树上溯缓存 PluginDef，先于菜单打开。注意不要 Handled，否则 ContextMenu 不弹出</summary>
    void OnPluginRightDown(object sender, MouseButtonEventArgs e)
    {
        _pendingRefItem = (e.OriginalSource as DependencyObject) is DependencyObject d ? FindItem<PluginDef>(d) : null;
    }

    /// <summary>插件右键菜单打开：未命中项时不弹菜单</summary>
    void OnPluginMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_pendingRefItem is not PluginDef) e.Handled = true;
    }

    /// <summary>插件右键"加入到会话"：插入 @plugin 名称 标记</summary>
    void OnPluginAddToSession(object sender, RoutedEventArgs e)
    {
        if (_pendingRefItem is PluginDef p) InsertRefMarker("@plugin " + p.Name);
        _pendingRefItem = null;
        e.Handled = true;
    }

    /// <summary>市场条目行右键（按下）：沿视觉树上溯缓存 MarketEntryDef，先于菜单打开。注意不要 Handled，否则 ContextMenu 不弹出</summary>
    void OnMarketRightDown(object sender, MouseButtonEventArgs e)
    {
        _pendingRefItem = (e.OriginalSource as DependencyObject) is DependencyObject d ? FindItem<MarketEntryDef>(d) : null;
    }

    /// <summary>市场条目右键菜单打开：未命中项时不弹菜单</summary>
    void OnMarketMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_pendingRefItem is not MarketEntryDef) e.Handled = true;
    }

    /// <summary>市场条目右键"加入到会话"：按类型插入 /技能名 或 @plugin 名称 标记</summary>
    void OnMarketAddToSession(object sender, RoutedEventArgs e)
    {
        if (_pendingRefItem is MarketEntryDef m)
        {
            var kind = (m.Entry.Kind ?? "").ToLowerInvariant();
            InsertRefMarker(kind == "plugin" ? "@plugin " + m.Entry.Name : "/" + m.Entry.Name);
        }
        _pendingRefItem = null;
        e.Handled = true;
    }

    /// <summary>把引用标记追加到输入框末尾（带空格分隔），并恢复焦点</summary>
    void InsertRefMarker(string marker)
    {
        var t = inputBox.Text;
        if (t.Length > 0 && !t.EndsWith(" ") && !t.EndsWith("\n")) t += " ";
        t += marker + " ";
        inputBox.Text = t;
        inputBox.CaretIndex = t.Length;
        inputBox.Focus();
    }

    /// <summary>项目根相对路径（反斜杠转正斜杠，与 Read 工具一致）</summary>
    string RelPath(string full)
    {
        try
        {
            return System.IO.Path.GetRelativePath(cfg.ProjectRoot, full).Replace('\\', '/');
        }
        catch { return full; }
    }

    static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}

/// <summary>代码查看页“读取行段”背景渲染器：把 start..end（文件真实行号）在可视区内整行绘淡黄全宽底 + 2px 金色左缘
/// （替代旧 ItemsControl 行 VM 的 RowBg 高亮；AvalonEdit 虚拟化渲染，只处理可视行）。窗口化载入时经 fileBase 把
/// 编辑器文档行号换算回文件真实行号判定，大文件读取段不在窗口首行也能正确高亮。</summary>
sealed class ReadRangeBgRenderer : ICSharpCode.AvalonEdit.Rendering.IBackgroundRenderer
{
    readonly int startLine;
    readonly int endLine;
    readonly int fileBase;   // 编辑器行号 → 文件行号偏移（窗口首行对应文件行号-1；全量载入=0）
    static readonly Brush RowBg = new SolidColorBrush(Color.FromArgb(0x26, 0xF5, 0xB9, 0x55));
    static readonly Brush Edge = new SolidColorBrush(Color.FromArgb(0xCC, 0xF5, 0xB9, 0x55));

    public ReadRangeBgRenderer(int startLine, int endLine, int fileBase = 0)
    {
        this.startLine = startLine;
        this.endLine = endLine;
        this.fileBase = fileBase;
    }

    public ICSharpCode.AvalonEdit.Rendering.KnownLayer Layer
        => ICSharpCode.AvalonEdit.Rendering.KnownLayer.Background;

    public void Draw(ICSharpCode.AvalonEdit.Rendering.TextView textView, DrawingContext drawingContext)
    {
        var w = textView.ActualWidth;   // 文本可视区宽（行号边距在 TextView 之外，此处坐标从文本区左缘起）
        foreach (var vl in textView.VisualLines)
        {
            var first = vl.FirstDocumentLine.LineNumber + fileBase;   // 编辑器行号 → 文件真实行号（窗口化偏移）
            if (first < startLine || first > endLine) continue;
            // 整行淡黄全宽底（含空行；横向超视口的行在滚动区其余部分无底，与原行容器观感一致）；
            // y 须换算视口坐标：VisualTop 是文档坐标，滚动（VerticalOffset 非 0）时必须减去，否则底色不随文本移动、相对行号错位
            if (w > 0) drawingContext.DrawRectangle(RowBg, null, new Rect(0, vl.VisualTop - textView.VerticalOffset, w, vl.Height));
        }
        foreach (var vl in textView.VisualLines)
        {
            var first = vl.FirstDocumentLine.LineNumber + fileBase;   // 编辑器行号 → 文件真实行号（窗口化偏移）
            if (first < startLine || first > endLine) continue;
            // 2px 金色左缘（半透明，叠于文本区最左列不影响字符可读性）：同随滚动偏移换算
            drawingContext.DrawRectangle(Edge, null, new Rect(0, vl.VisualTop - textView.VerticalOffset, 2, vl.Height));
        }
    }
}

/// <summary>把 AvalonEdit 内置浅色主题语法定义就地适配为暗底可读配色（VS Code 暗色系色板）。
/// 高亮定义由 HighlightingManager 共享、颜色对象未冻结，就地改色会同步影响本程序所有同定义编辑器——它们同为暗底，目标一致；
/// 按“引用”去重（命名色与规则/区间色共享同一实例），重复调用幂等。映射不到的语义色保留原前景（多为可读中性色）不动。</summary>
static class DarkSyntax
{
    // 名称语义片段 → RGB（按片段长度降序匹配：先具体后一般，避免“attributevalue”被“attribute”先抢等误伤）
    static readonly (string Frag, uint Rgb)[] Palette =
    {
        ("attributevalue", 0xCE9178),   // HTML/XML 属性值：橙
        ("verbatimstring", 0xCE9178),   // C# 逐字字符串
        ("stringinterpolation", 0xCE9178),
        ("thisorbasereference", 0x569CD6),
        ("operatorkeywords", 0x569CD6),
        ("getsetaddremove", 0x569CD6),
        ("referencetype", 0x4EC9B0),    // 引用类型：青绿
        ("numberliteral", 0xB5CEA8),    // 数字：淡绿
        ("propertyname", 0x9CDCFE),     // JSON 属性名/对象成员：亮蓝
        ("truefalse", 0x569CD6),
        ("elementname", 0x569CD6),      // XML 元素名
        ("valuetype", 0x4EC9B0),        // 值类型：青绿
        ("preprocessor", 0xC586C0),     // 预处理器/指令：紫
        ("methodcall", 0xDCDCAA),       // 方法调用：黄
        ("attribute", 0x9CDCFE),        // XML/HTML 属性名
        ("variable", 0x9CDCFE),
        ("parameter", 0x9CDCFE),
        ("interface", 0x4EC9B0),
        ("delegate", 0x4EC9B0),
        ("visibility", 0x569CD6),
        ("modifier", 0x569CD6),
        ("comment", 0x6A9955),          // 注释：绿
        ("constant", 0xB5CEA8),
        ("selector", 0xDCDCAA),
        ("boolean", 0x569CD6),
        ("keyword", 0x569CD6),          // 关键字：蓝
        ("control", 0x569CD6),
        ("important", 0x569CD6),
        ("function", 0xDCDCAA),
        ("heading", 0xDCDCAA),          // Markdown 标题
        ("enum", 0x4EC9B0),
        ("struct", 0x4EC9B0),
        ("class", 0x4EC9B0),
        ("entity", 0xCE9178),
        ("escape", 0xCE9178),
        ("cdata", 0xCE9178),
        ("string", 0xCE9178),            // 字符串：暖橙
        ("char", 0xCE9178),
        ("number", 0xB5CEA8),
        ("literal", 0xB5CEA8),
        ("hexcolor", 0xB5CEA8),
        ("method", 0xDCDCAA),
        ("header", 0xDCDCAA),
        ("null", 0x569CD6),
        ("regex", 0xCE9178),
        ("atrule", 0xC586C0),
        ("tag", 0x569CD6),              // HTML 标签
        ("operator", 0xD4D4D4),          // 符号类：中性浅灰
        ("delimiter", 0xD4D4D4),
        ("punctuation", 0xD4D4D4),
        ("bracket", 0xD4D4D4),
        ("symbol", 0xD4D4D4),
        ("codespan", 0xCE9178),         // Markdown 行内代码
        ("emphasis", 0x569CD6),
        ("bold", 0x569CD6),
        ("italic", 0x569CD6),
        ("link", 0x569CD6),
        ("image", 0x569CD6),
        ("url", 0x569CD6),
        ("text", 0xD4D4D4),
        ("plain", 0xD4D4D4),
    };

    static readonly (string Frag, uint Rgb)[] PaletteOrdered =
        Palette.OrderByDescending(m => m.Frag.Length).ToArray();   // 长片段（更具体）优先

    public static ICSharpCode.AvalonEdit.Highlighting.IHighlightingDefinition? Adapt(
        ICSharpCode.AvalonEdit.Highlighting.IHighlightingDefinition? def)
    {
        if (def == null) return null;
        try
        {
            var done = new HashSet<ICSharpCode.AvalonEdit.Highlighting.HighlightingColor>();
            foreach (var c in def.NamedHighlightingColors) Paint(c, done);
            var walked = new HashSet<ICSharpCode.AvalonEdit.Highlighting.HighlightingRuleSet>();
            Walk(def.MainRuleSet, done, walked);
        }
        catch { /* 个别自定义语言定义结构异常不影响显示：就地放弃本次适配 */ }
        return def;
    }

    /// <summary>递归遍历规则集（规则色/区间起止色 + 嵌套规则集），规则集按引用去重防环。</summary>
    static void Walk(ICSharpCode.AvalonEdit.Highlighting.HighlightingRuleSet rs,
        HashSet<ICSharpCode.AvalonEdit.Highlighting.HighlightingColor> done,
        HashSet<ICSharpCode.AvalonEdit.Highlighting.HighlightingRuleSet> walked)
    {
        if (rs == null || !walked.Add(rs)) return;
        foreach (var r in rs.Rules) Paint(r.Color, done);
        foreach (var sp in rs.Spans)
        {
            Paint(sp.StartColor, done);
            Paint(sp.SpanColor, done);
            Paint(sp.EndColor, done);
            if (sp.RuleSet != null) Walk(sp.RuleSet, done, walked);
        }
    }

    /// <summary>给单个颜色对象改暗底色：前景为空（继承文本色）不动；引用级去重防与命名色共享实例被改两次。</summary>
    static void Paint(ICSharpCode.AvalonEdit.Highlighting.HighlightingColor? c,
        HashSet<ICSharpCode.AvalonEdit.Highlighting.HighlightingColor> done)
    {
        if (c == null || c.Foreground == null) return;
        if (!done.Add(c)) return;
        var name = c.Name;
        if (string.IsNullOrEmpty(name)) return;   // 匿名规则色保持默认
        foreach (var (frag, rgb) in PaletteOrdered)
        {
            if (!name.Contains(frag, StringComparison.OrdinalIgnoreCase)) continue;
            c.Foreground = new ICSharpCode.AvalonEdit.Highlighting.SimpleHighlightingBrush(
                Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            return;
        }
    }
}

/// <summary>代码页行号区自绘版（替换内置 LineNumberMargin）：普通行号用窗口 DimBrush 灰，读取行段(start..end)内
/// 整条铺淡黄底 + 行号提亮金色，与代码区 ReadRangeBgRenderer 同一套突显语义；无读取段时退化为普通行号。</summary>
sealed class RangeLineNumberMargin : ICSharpCode.AvalonEdit.Editing.LineNumberMargin
{
    static readonly Brush HiNum = new SolidColorBrush(Color.FromRgb(0xFF, 0xD9, 0x8A));   // 突显行数字：亮金
    static readonly Brush HiBg = new SolidColorBrush(Color.FromArgb(0x26, 0xF5, 0xB9, 0x55));   // 突显行底：与代码区同系淡黄
    readonly Brush normalBrush;   // 普通行号色（窗口 DimBrush）
    int startLine, endLine;       // 读取段突显范围（文件真实行号，start<=0=无突显）
    int fileBase;                 // 编辑器首行对应文件行号-1（窗口化偏移）：行号显示与突显判定均换算为文件真实行号

    public RangeLineNumberMargin(Brush normalBrush) => this.normalBrush = normalBrush;

    /// <summary>设置窗口首行对应的文件行号-1（从文件第 1 行全量载入=0）：换文件载入时更新，行号随之按文件真实行号显示。</summary>
    public void SetFileBase(int fileFirstLineZero)
    {
        if (fileBase == fileFirstLineZero) return;
        fileBase = fileFirstLineZero;
        InvalidateVisual();
    }

    /// <summary>设置读取段突显范围（文件真实行号；start&lt;=0 清除突显）。</summary>
    public void SetRange(int start, int end)
    {
        if (startLine == start && endLine == end) return;
        startLine = start;
        endLine = end;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var tv = TextView;
        if (tv == null || !tv.VisualLinesValid) return;
        double ppd = 1.0;
        try { ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip; } catch { }
        var typeface = new Typeface("Consolas");
        foreach (var vl in tv.VisualLines)
        {
            if (vl.TextLines.Count == 0) continue;
            var fileNo = vl.FirstDocumentLine.LineNumber + fileBase;   // 编辑器行号 → 文件真实行号（窗口化偏移）
            if (fileNo < 1) continue;
            var inRange = startLine > 0 && fileNo >= startLine && fileNo <= endLine;
            // 数字基线对齐文本顶：内容行 Y 经滚动偏移换算为边距视口坐标
            double yBase = vl.GetTextLineVisualYPosition(vl.TextLines[0],
                    ICSharpCode.AvalonEdit.Rendering.VisualYPosition.TextTop) - tv.VerticalOffset;
            if (inRange)
            {
                // 整条行号区底块：按内容行上下沿铺同系淡黄（与代码区底块同行高，观感一体）
                double y0 = vl.VisualTop - tv.VerticalOffset;
                dc.DrawRectangle(HiBg, null, new Rect(0, y0, ActualWidth, vl.Height));
            }
            var text = new FormattedText(fileNo.ToString(),
                System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, 12, inRange ? HiNum : normalBrush, ppd);
            // 右对齐到行号区右缘（距文本区留 6px），与内置行号排版一致
            dc.DrawText(text, new Point(Math.Max(0, ActualWidth - text.Width - 6), yBase));
        }
    }
}

/// <summary>gitDiffEdit 文本前景着色器：单 TextEditor 无法逐行设前景，借 DocumentColorizingTransformer 按文档行
/// 读行模型整行着色（+ 新增绿 / - 删除红 / @@ 区块琥珀 / 头部与元信息灰蓝 / 上下文灰白），视觉同旧行级列表；
/// 只处理可视行，行文本仍为纯文本可拖选复制。行号 n 对应 rows[n-1]（尾空行/越界无色回退默认前景）。</summary>
sealed class DiffRowColorizer : ICSharpCode.AvalonEdit.Rendering.DocumentColorizingTransformer
{
    List<GitDiffRow>? rows;   // 当前行模型（与文档行一一对应，null=空）

    public void SetRows(List<GitDiffRow>? rows) => this.rows = rows;

    protected override void ColorizeLine(ICSharpCode.AvalonEdit.Document.DocumentLine line)
    {
        var row = rows != null && line.LineNumber >= 1 && line.LineNumber - 1 < rows.Count
            ? rows[line.LineNumber - 1] : null;
        if (row?.Fg == null) return;
        // 整行统一前景（含行首 + / - 标记同色）；无其他高亮规则，一次覆盖全行即可
        ChangeLinePart(line.Offset, line.EndOffset,
            e => e.TextRunProperties.SetForegroundBrush(row.Fg));
    }
}

/// <summary>gitDiffEdit 文本区行底色渲染器：按行模型 Bg（+ 淡绿 / - 淡红）在可视区整行铺全宽底，
/// 与行号区 DiffLineNumberMargin 同行高同色接缝连续（替代旧 ItemsControl 行容器背景；
/// AvalonEdit 虚拟化渲染，只处理可视行，行号 n 对应 rows[n-1]）。</summary>
sealed class DiffBgRenderer : ICSharpCode.AvalonEdit.Rendering.IBackgroundRenderer
{
    List<GitDiffRow>? rows;   // 当前行模型（与文档行一一对应，null=空）

    public void SetRows(List<GitDiffRow>? rows) => this.rows = rows;

    public ICSharpCode.AvalonEdit.Rendering.KnownLayer Layer
        => ICSharpCode.AvalonEdit.Rendering.KnownLayer.Background;

    public void Draw(ICSharpCode.AvalonEdit.Rendering.TextView textView, DrawingContext drawingContext)
    {
        if (rows == null || rows.Count == 0) return;
        var w = textView.ActualWidth;   // 文本可视区宽（行号边距在 TextView 之外，坐标从文本区左缘起）
        foreach (var vl in textView.VisualLines)
        {
            var n = vl.FirstDocumentLine.LineNumber;
            var row = n >= 1 && n - 1 < rows.Count ? rows[n - 1] : null;
            if (row?.Bg == null) continue;
            // 整行淡绿/淡红底（含空行；横向超视口的行在滚动区其余部分无底，与原行容器观感一致）；
            // y 须换算视口坐标（VisualTop 为文档坐标减滚动偏移），否则滚动时底色不随文本移动、与行号区底块错位
            if (w > 0) drawingContext.DrawRectangle(row.Bg, null, new Rect(0, vl.VisualTop - textView.VerticalOffset, w, vl.Height));
        }
    }
}

/// <summary>gitDiffEdit 行号区自绘版（替换内置 LineNumberMargin）：每行按行模型铺双列（左=旧文件行号 | 右=新文件行号），
/// 增删行在行号区铺与文本区同色整条底（接缝视觉连续），行号色沿用行前景（+ 绿 / - 红），上下文与无行号行灰；
/// 行号值由装载状态机预填（OldNo/NewNo，非内容行 null 留空）。行号 n 对应 rows[n-1]。</summary>
sealed class DiffLineNumberMargin : ICSharpCode.AvalonEdit.Editing.LineNumberMargin
{
    const double ColW = 44;      // 单列宽：右对齐留 4px 可容 6 位行号
    const double ColGap = 4;     // 新旧两列间距
    readonly Brush normalBrush;  // 普通行号色（窗口 DimBrush）
    List<GitDiffRow>? rows;      // 当前行模型（与文档行一一对应，null=空）

    public DiffLineNumberMargin(Brush normalBrush) => this.normalBrush = normalBrush;

    public void SetRows(List<GitDiffRow>? rows)
    {
        this.rows = rows;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var s = base.MeasureOverride(availableSize);
        // 双列保底宽（内置按单列行号文本测算，空文档期偏窄会挤压文本区）
        return new Size(Math.Max(s.Width, ColW * 2 + ColGap), s.Height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var tv = TextView;
        if (tv == null || !tv.VisualLinesValid) return;
        double ppd = 1.0;
        try { ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip; } catch { }
        var typeface = new Typeface("Consolas");
        foreach (var vl in tv.VisualLines)
        {
            if (vl.TextLines.Count == 0) continue;
            var n = vl.FirstDocumentLine.LineNumber;
            var row = rows != null && n >= 1 && n - 1 < rows.Count ? rows[n - 1] : null;
            if (row == null) continue;
            // 数字基线对齐文本顶：内容行 Y 经滚动偏移换算为边距视口坐标
            double yBase = vl.GetTextLineVisualYPosition(vl.TextLines[0],
                    ICSharpCode.AvalonEdit.Rendering.VisualYPosition.TextTop) - tv.VerticalOffset;
            if (row.Bg != null)
            {
                // 整条行号区底块：与文本区同色同高（左右无缝），增/删行视觉一体
                double y0 = vl.VisualTop - tv.VerticalOffset;
                dc.DrawRectangle(row.Bg, null, new Rect(0, y0, ActualWidth, vl.Height));
            }
            // 增删行行号沿用行前景同系色（绿/红），上下文与文件头行灰
            var brush = row.Bg != null ? row.Fg : normalBrush;
            DrawNo(dc, typeface, ppd, ColW, row.OldNo, yBase, brush);   // 旧文件行号列（左）
            DrawNo(dc, typeface, ppd, ActualWidth, row.NewNo, yBase, brush);   // 新文件行号列（右）
        }
    }

    /// <summary>单列行号绘制：右对齐列右缘（留 4px），字号 11 略小于文本 12 不致压行。</summary>
    void DrawNo(DrawingContext dc, Typeface tf, double ppd, double colRight, int? no, double yBase, Brush brush)
    {
        if (no == null || no.Value < 1 || colRight <= 0) return;
        var text = new FormattedText(no.Value.ToString(),
            System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            tf, 11, brush, ppd);
        dc.DrawText(text, new Point(Math.Max(0, colRight - text.Width - 4), yBase));
    }
}


