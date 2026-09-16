/// <summary>Agent 主循环：工具调度与流式事件分发、额度/超时控制、会话历史管理、标签-符号关联积累（TagRefAccumulator）与系统提示词（DefaultPrompt）。</summary>
using System.Diagnostics;
using System.Net.Http;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GAIRR.AgentHost;

namespace GAIRR.Core;

/// <summary>工具卡在 UI 上的增量状态（由 AgentLoop 产出，UI 订阅渲染）</summary>
public class ToolUi
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string Status { get; set; } = "…";
    public string Inner { get; set; } = "";
    public string Summary { get; set; } = "";
    public bool Warn { get; set; }
    /// <summary>工具图标（Unicode emoji），显示在卡片标题前</summary>
    public string Icon { get; set; } = "";
    /// <summary>工具图标颜色（Hex 字符串，如 #5C8AE6）</summary>
    public string IconColor { get; set; } = "#5C8AE6";
    /// <summary>工具图标字号（WPF FontSize，单位 px/点）</summary>
    public double IconFontSize { get; set; } = 13;
    /// <summary>卡片展开控制：默认收缩（工具卡只显示标题与状态），危险拦截等场景同样保持收起</summary>
    public bool Open { get; set; }
    /// <summary>是否计划步骤相关卡片（创建/开始/完成计划步骤），UI 标题使用橙色区分</summary>
    public bool IsStep { get; set; }
    /// <summary>所属轮次，供 UI 在工具条前显示 [n] 前缀</summary>
    public int Round { get; set; }
    /// <summary>文件工具目标路径（相对项目根，正斜杠；仅 Read 等文件工具成功时回填），供 UI 打开代码快照</summary>
    public string FilePath { get; set; } = "";
    /// <summary>实际读取行段起止（1 基含端点；0=未指定），供代码查看器定位并突显读取段</summary>
    public int StartLine { get; set; }
    /// <summary>实际读取行段起止（1 基含端点；0=未指定），供代码查看器定位并突显读取段</summary>
    public int EndLine { get; set; }
}

/// <summary>Agent 循环：模型决策 → 工具执行 → 结果回填，直到纯文本/超轮次/取消。
/// partial：Flow 模式（流程编排）的状态与方法在 FlowMode.cs</summary>
public partial class AgentLoop
{
    readonly AppConfig cfg;
    readonly ToolRegistry registry;
    readonly ChangeJournal journal;
    readonly SkillLoader? skills;
    string promptFile;   // 可经 SwitchPrompt 切换（编排会话用 plan-orchestration.md，8.3 决策 3）
    readonly bool isAutoTask;
    LLMClient client;

    static readonly string LogPath = Paths.AgentLog;
    void Log(string msg)
    {
        try
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
            File.AppendAllText(LogPath, line + Environment.NewLine, System.Text.Encoding.UTF8);
            Emit(new UiEvent { Type = UiEventType.Log, LogLine = line });
        }
        catch { }
    }

    /// <summary>切换提示词文件：编排会话用 plan-orchestration.md、普通会话用 agent-deep.md（8.3 决策 3）；
    /// 下次 RunAsync 生效（提示词按任务重新加载）。</summary>
    public void SwitchPrompt(string file) => promptFile = file;

    /// <summary>编排会话阶段（框架级工作模式，8.3 决策 3）：null/空=普通会话；"collecting"/"generating"/"done"=编排阶段。
    /// 非空时：① 编排会话注入完整工具集（执行需 UpdateTodo/Edit/Bash，用户决策 2026-09-01）；② Finished/发送的收口由 UI 按此判定。
    /// 切换会话时由 UI 调用，下次 RunAsync 生效；切回普通会话传 null 即复位白名单（非全局 registry 开关，无残留污染）。</summary>
    public string? OrchMode { get; set; }

    /// <summary>是否编排会话（OrchMode 非空即编排，供 UI/log 判定）。</summary>
    public bool IsOrchestration => !string.IsNullOrEmpty(OrchMode);

    // 网页模型「强制新会话+整段重投完整上下文」：由 RunAsync 透传、只作用于本次任务的首个模型调用（首个 ChatStreamAsync），
    // 消费后立即复位——后续工具轮与下轮任务沿用网页侧上下文，不再强制（避免把增量轮也整段重投造成 token 膨胀）
    private bool _forceWebFullContext;

    /// <summary>切换编排阶段（同 SwitchPrompt：切会话时由 UI 调用）。传 null 复位为普通会话。</summary>
    public void SwitchOrch(string? phase) => OrchMode = phase;

    /// <summary>会话级消息历史（跨任务保留，阶段四再做裁剪）</summary>
    readonly JsonArray history = new();

    /// <summary>主题式上下文压缩器（任务边界摘要+无关轮剔除+token兜底，压力驱动，见 Core/ContextCompressor.cs）</summary>
    readonly ContextCompressor compressor = new();
    /// <summary>只读工具名单（与 ContextCompressor.ReadOnlyTools 保持一致）：重复读熔断判定用</summary>
    static readonly HashSet<string> ReadOnlyToolNames = new() { "Read", "Grep", "Glob", "ListDir", "MapTrace", "SmartSearch", "MapSlice", "Map", "FindRefs" };
    /// <summary>任务级重复读计数（签名=工具名+参数）：同一读取反复执行时在结果里附注提醒，打断"重读确认"循环（工具并发执行，访问需加锁）</summary>
    readonly Dictionary<string, (int Count, long Tick)> readSigCount = new(StringComparer.Ordinal);
    /// <summary>会话级文件修改时刻表（Write/Edit 成功时记录）：供重复读熔断判定"上次读后文件是否被改过"</summary>
    readonly Dictionary<string, long> modifiedStamp = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>会话级已读文件集合（Read/MapSlice 成功后登记）：供"先回读再改动"护栏判定 Write 前是否已读；跨任务保留，与 readSigCount 同锁访问</summary>
    readonly HashSet<string> sessionReadPaths = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>全局单调时刻计数：修改/读取打点用（与 readSigCount/modifiedStamp 同锁访问）</summary>
    static long tickSeq;
    /// <summary>P6: 当前任务文件清单（文件+位置+意图），压缩前询问 LLM 必用文件用（任务级，新任务清空；见 Core/FileRefs.cs）</summary>
    readonly FileRefTracker fileRefs = new();
    /// <summary>P6: 上一轮必用文件查询对应的清单指纹：清单未变（无新读/新改）时跳过重复 LLM 查询</summary>
    long lastEssentialFp;
    /// <summary>当前任务文本缓存（压缩锚点词来源）</summary>
    string curTaskText = "";
    /// <summary>当前用户消息是否为"承接上文"的短消息（≤40 字，如"好，开始"、"再查查xxx"）：不作为新任务边界；压缩器据此跳过旧任务摘要化</summary>
    bool curTaskIsShortMsg;
    /// <summary>usage 校准（建议3）：上一轮请求的本地估算与服务端实际 prompt token，比值反馈给压缩器收紧压缩阈值（中文 chars/4 偏低时防触发过晚/超窗）</summary>
    int lastUsagePrompt, lastRequestEstimate;

    /// <summary>回合快照：一次用户请求 → GAIRR 完整执行 → 最终结论 的全过程为一回合（区别于执行过程中的模型调用轮次），
    /// 被压缩器摘要化的旧回合在此归档，供 RecallHistory 工具按轮号取回完整原文</summary>
    sealed class RoundSnap { public int No; public string UserText = ""; public List<JsonObject> Msgs = new(); }
    readonly List<RoundSnap> roundSnaps = new();
    const int RoundSnapMax = 30;   // 最多保留最近 30 回合原文（防内存膨胀），超出丢最旧

    /// <summary>任务级计划步骤文本缓存：UpdateTodo(update) 卡片标题需要展示对应步骤文本（create 预扫描时缓存，跨轮保留）</summary>
    List<string>? todoSteps;
    /// <summary>已完成步骤索引集合（模型显式 UpdateTodo 或框架自动补全），用于自动补全"计划步骤"完成卡片</summary>
    readonly HashSet<int> todoDoneSteps = new();
    /// <summary>当前正在执行的计划步骤序号（1 起）</summary>
    int currentStep;
    /// <summary>待显示的开始卡片步骤序号：步骤切换信号检测到后延迟到下一轮思考条前展示（保证"开始"卡在思考条之前）</summary>
    int pendingStepStart;
    
        /// <summary>修改类步骤关键词：Write/Edit 等修改动作执行时命中，其前置计划项自动批量打勾（"修改"及近义表述）</summary>
        static readonly string[] ModifyStepKeywords = { "修改", "编辑", "实现", "调整" };
        /// <summary>验证类步骤关键词：Bash 编译/构建等动作执行时命中，其前置计划项自动批量打勾（"验证"及近义表述）</summary>
        static readonly string[] VerifyStepKeywords = { "验证", "编译", "构建", "测试" };
    /// <summary>危险命令确认挂起：Tools 层在会话内等待用户允许/取消（不设自动超时；用户停止任务时经 _runCt 取消回调释放为拒绝）</summary>
    TaskCompletionSource<bool>? dangerTcs;
    /// <summary>最近一次 RunAsync 的运行取消令牌：危险确认挂起时注册跟随回调（用户点停止/宿主中止→挂起释放为拒绝，
    /// 使"停止"能真正中断正挂起的危险确认，且不依赖旧的 5 分钟自动拒绝）</summary>
    CancellationToken _runCt;
    /// <summary>当前轮工具阶段的取消源（工具任务创建时赋值、阶段结束置空）：危险命令确认挂起除跟随 _runCt 外也注册本令牌——
    /// 工具阶段硬超时会 Cancel 它，让挂起中的危险确认随工具阶段一并释放（此前只挂 _runCt，工具超时救不了确认挂起，loop 仍会卡死）</summary>
    CancellationTokenSource? curToolCts;
    /// <summary>当前任务是否出现过危险命令确认窗（需人工处理）：任务级钉钉按需通知的判定信号之一</summary>
    bool taskHadDanger;
    /// <summary>本轮模型意图缓存（危险命令确认提示展示“要做什么事情”）</summary>
    string? curIntent;
    /// <summary>计划审批挂起：Plan 模式下生成计划后等待外部/UI 批准/拒绝</summary>
    TaskCompletionSource<bool>? planTcs;
    /// <summary>当前待审批的计划（保存路径与状态）</summary>
    Plan? pendingPlan;
    /// <summary>当前会话标题缓存（首条消息生成/打开会话时由 UI 同步）：任务结束钉钉通知用会话标题代替原始任务文本</summary>
    string sessionTitle = "";

    /// <summary>当前会话标识（UI 打开/新建会话时注入）：请求上下文快照按会话归档 data/rounds/&lt;key&gt;/，
    /// 空=无归属会话（自动任务/CLI/Server）跳过快照落盘</summary>
    string sessionKey = "";
    const int RoundSnapFilesMax = 120;   // 单会话请求快照文件数上限（防长期会话磁盘膨胀），超出丢最旧（连带删配对回应快照）
    /// <summary>本轮请求快照的实际落盘路径（SnapshotRoundContext 写入，回应快照据此同名配对 roundN[-k].json ↔ roundN[-k]-resp.json）；
    /// 空=本轮未落请求快照（无会话归属/落盘失败），回应快照回退 roundN-resp.json 命名</summary>
    string lastReqSnapPath = "";

    /// <summary>Flow 模式步骤序列（RunAsync 首次调用即消费、消费后置 null）：非空时框架经 FlowRunner 逐步驱动
    /// （模板文件 &lt;工具目录&gt;/flows/*.json，TaskRequest.Flow 同理）；null=自主模式，行为不变</summary>
    public List<FlowStep>? Flow;
    /// <summary>Flow 模式执行控制器（null=自主模式）：RunAsync 首次调用创建，流程结束置 null</summary>
    FlowRunner? flowRunner;

    /// <summary>计划模式：首轮只生成计划并写入 plans/，等待外部/UI 审批后再执行工具。</summary>
    /// <summary>本会话是否处于 UI 视口（多会话宿主切换会话时由宿主置位；默认 true=UI 单前台会话即视口）。
    /// 决定"等人工"挂起的钉钉通道：视口会话由 UI 弹卡就近处理，非视口/切走会话走短静默钉钉（见 RunHangNotifierAsync）。</summary>
    public bool InViewport { get; set; } = true;

    public bool PlanMode { get; set; }

    /// <summary>自动任务上下文标记：命中危险命令时无需人工确认，直接放行。</summary>
    static readonly AsyncLocal<bool> autoAllowDanger = new();
    /// <summary>进入自动任务危险命令自动放行作用域（using 包裹后台任务执行）。</summary>
    public static IDisposable AutoAllowDangerScope()
    {
        var prev = autoAllowDanger.Value;
        autoAllowDanger.Value = true;
        return new AutoAllowScope(prev);
    }

    sealed class AutoAllowScope : IDisposable
    {
        readonly bool prev;
        public AutoAllowScope(bool prev) => this.prev = prev;
        public void Dispose() => autoAllowDanger.Value = prev;
    }

    /// <summary>当前执行工具的 AgentLoop（工具执行作用域内有效，AsyncLocal 随 Task.Run 流进工具线程）：
    /// 写类工具经它上送文件锁等待事件（loop.Emit 自动注入会话归属 Key），直写/无 Loop 路径为 null（不产出事件）。</summary>
    static readonly AsyncLocal<AgentLoop?> currentToolLoop = new();
    /// <summary>工具执行链路上当前生效的 Loop（Write/Edit 排队等文件锁时上报等待事件用）。</summary>
    public static AgentLoop? CurrentToolLoop => currentToolLoop.Value;
    /// <summary>当前会话标识（无归属会话为空）：写类工具登记文件锁占用者描述用。</summary>
    public string SessionKey => sessionKey;
    /// <summary>进入"当前执行 Loop"作用域：using 须包住工具执行（Task.Run 之前建立，执行上下文才会流进工具线程）。</summary>
    IDisposable ToolLoopScope()
    {
        var prev = currentToolLoop.Value;
        currentToolLoop.Value = this;
        return new ToolLoopScopeGuard(prev);
    }

    sealed class ToolLoopScopeGuard : IDisposable
    {
        readonly AgentLoop? prev;
        public ToolLoopScopeGuard(AgentLoop? prev) => this.prev = prev;
        public void Dispose() => currentToolLoop.Value = prev;
    }

    public string Provider { get; private set; }
    public string ModelName => client.Model;

    /// <summary>UI 状态总线（只传递轻量级状态信号）</summary>
    public UiEventBus Bus { get; } = new();

    /// <summary>统一事件出口：本 Loop 会话内全部 UI 事件一律经此发送，自动附带当前会话 Key（UiEvent.SessionKey），
    /// 供多会话宿主按 Key 分流归属、按会话归档快照；sessionKey 为空=无归属会话
    /// （自动任务/CLI/Server/叶子会话）保持全局语义不注入。编排叶子事件不依赖本封装，
    /// 由 PlanRunner 桥接转发时统一覆盖为编排主会话 Key。MainWindow 直发的全局状态事件
    /// （DocStatus/LspStatus）不经此方法，保持无 Key（=全会话可见）。宿主层（AgentSession 等）
    /// 发会话内收尾事件时也应经此出口，避免绕过统一 Key 注入。</summary>
    public void Emit(UiEvent e) => Bus.Post(string.IsNullOrEmpty(sessionKey) ? e : e with { SessionKey = sessionKey });

    /// <summary>最近一次任务的累计 token（服务宿主结果汇总用；UI 走事件流实时累计 accTokens）</summary>
    public int LastTaskTokens { get; private set; }

    /// <summary>最近一次 RunAsync 是否成功完成（内部捕获了模型异常/取消时也置 false，供自动任务判定真实成败）</summary>
    public bool LastRunSuccess { get; private set; } = true;

    /// <summary>最近一次 RunAsync 的失败原因（成功时为空；模型 API 报错/用户取消等，供自动任务重试与钉钉通知）</summary>
    public string LastFailReason { get; private set; } = "";

    /// <summary>最近一次 RunAsync 实际执行的轮次计数（供 TaskRunner 写入运行记录）</summary>
    public int RoundCount { get; private set; }

    /// <summary>当前会话 ID（Plan 模式保存/查找计划时使用）</summary>
    public string SessionId { get; set; } = "";

    /// <summary>当前 Agent 工作模式（agile=敏捷/deep=严谨），供 UI 显示与切换。</summary>
    public string AgentMode => SystemCfg.AgentMode;

    /// <summary>切换 Agent 工作模式（UI 调用，下次 RunAsync 生效）。</summary>
    public void SwitchAgentMode(string mode)
    {
        var m = mode.Trim().ToLowerInvariant();
        if (m != "agile" && m != "deep") return;
        SystemCfg.AgentMode = m;
        // 非自动任务/编排会话时，同步切换提示词文件
        if (!isAutoTask && !IsOrchestration)
            promptFile = m == "deep" ? "agent-deep.md" : "agent-agile.md";
    }

    public AgentLoop(AppConfig cfg, ToolRegistry registry, ChangeJournal journal, SkillLoader? skills = null, string promptFile = "")
    {
        this.cfg = cfg;
        this.registry = registry;
        this.journal = journal;
        this.skills = skills;
        // 默认提示词按 AgentMode 选择：agile→agent-agile.md，deep→agent-deep.md；显式传入时优先用传入值
        this.promptFile = string.IsNullOrEmpty(promptFile)
            ? (SystemCfg.AgentMode == "deep" ? "agent-deep.md" : "agent-agile.md")
            : promptFile;
        this.isAutoTask = string.Equals(promptFile, "autotask-run.md", StringComparison.OrdinalIgnoreCase);
        thinkingRules = ThinkingRules.Load(cfg);   // 思考参数规则全表（[ModelThinking] 配置驱动）
        Provider = cfg.Provider;
        client = MakeClient(Provider);
        // 危险命令确认改为会话内提示 + 允许/取消按钮（不再弹 MessageBox）。
        // 单播委托：UI 模式（单 loop）首个注册者即本 loop；多会话宿主（gairr-agent-server）
        // 在创建 loop 前注册 DangerHub 路由（AsyncLocal 分发到发起会话），本分支自动跳过。
        if (Phase1Tools.DangerConfirmHandler == null)
            Phase1Tools.DangerConfirmHandler = ConfirmDangerAsync;
        // RecallHistory：按回合号取回历史完整原文（回合=一次用户请求→完整执行→结论；上下文被压缩省略后模型主动按需取回）
        if (!registry.IsRegistered("RecallHistory"))
            registry.Register("RecallHistory", ToolRegistry.Fn(
                "RecallHistory",
                SystemCfg.Desc("RecallHistory",
                    "按回合号取回历史对话（用户请求+执行+结论），供之前回合上下文被压缩省略后补细节。mode 三档：talk=只取所有用户原话+GAIRR纯文本结论（默认最省，不占预算）；brief=过程关键信息+结论（调用了哪些工具+结果摘要，不含工具卡片展开全文与长思维链）；full=指定轮完整执行过程+结论（含工具参数与思维链截断，量大易截断，仅单轮深挖用）。轮号见\"轮N\"标注；不记得可只传 last。"),
                new JsonObject
                {
                    ["rounds"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "integer" }, ["description"] = "要取回的回合号列表，如 [1,3]（与 last 二选一）" },
                    ["last"] = ToolRegistry.Int("取回最近 N 个回合的原文（与 rounds 二选一）"),
                    ["mode"] = new JsonObject { ["type"] = "string", ["description"] = "取回粒度：talk(默认=用户原话+结论) / brief(过程要点+工具+结果摘要) / full(完整过程+结论)" },
                },
                new JsonArray()),
                (a, ct) => Task.FromResult(RecallHistoryText(a)));
    }

    readonly ThinkingRules thinkingRules;

    LLMClient MakeClient(string provider)
    {
        // 网页模型通道（provider 带 web: 前缀）：不走 HTTP，转交 UI 层独立网页窗口的后端实现。
        // 思考参数挂接、切换/持久化等后续逻辑与真实厂商共用，上层调用方式完全一致。
        if (WebChannelRegistry.TryGet(provider, out var webSpec))
        {
            var backend = WebChannelRegistry.Get(provider)
                ?? throw new LlmException($"网页通道「{webSpec.Display}」不可用：当前环境未注册网页后端（CLI 模式不支持网页通道，请用桌面版）");
            return new LLMClient("", "", webSpec.ModelId, cfg) { WebBackend = backend };
        }
        var (url, key, model) = cfg.ProviderCfg(provider);
        var c = new LLMClient(url, key, model, cfg);
        // 思考参数规则按模型挂接（[ModelThinking] 配置驱动，未配置的模型不带思考参数）
        c.ThinkingRule = thinkingRules.For(model);
        c.ThinkingValue = cfg.ThinkingValueFor(model);
        return c;
    }

    /// <summary>当前模型思考规则（UI 据此决定显示开关/等级下拉/隐藏；null=该模型不支持思考参数）</summary>
    public ThinkingRule? ThinkingRule => client.ThinkingRule;

    /// <summary>当前模型持久化的思考值（开/关或等级档），供 UI 恢复显示</summary>
    public string ThinkingValue => client.ThinkingValue;

    /// <summary>切换思考值（标题栏开关/等级下拉）：立即作用于当前客户端</summary>
    public void SwitchThinking(string value) => client.ThinkingValue = value;

    public void SwitchProvider(string provider)
    {
        try
        {
            Provider = provider;
            cfg.Provider = provider;
            client = MakeClient(provider);
        }
        catch (Exception ex)
        {
            Log($"[Error] 切换供应商失败: {ex.Message}");
            throw;
        }
    }

    /// <summary>同供应商内切换模型（标题栏下拉）</summary>
    public void SwitchModel(string model) => client.Model = model;

    /// <summary>单下拉切换：同时改变厂商和模型；端点变化时重建 LLMClient，否则仅更新 model id</summary>
    public void SwitchModelFull(string provider, string model)
    {
        try
        {
            var needRebuild = Provider != provider;
            Provider = provider;
            cfg.Provider = provider;
            if (needRebuild || client == null)
                client = MakeClient(provider);
            client.Model = model;
            // 换模型即换思考规则与持久化值（规则按模型不同：参数名/值形态/层级/等级都随模型变）
            client.ThinkingRule = thinkingRules.For(model);
            client.ThinkingValue = cfg.ThinkingValueFor(model);
        }
        catch (Exception ex)
        {
            Log($"[Error] 切换模型失败: {ex.Message}");
            throw;
        }
    }

    /// <summary>仅切换本 loop 的供应商客户端，不改全局 cfg.Provider（自动任务模型故障转移用，避免影响用户主会话选择）</summary>
    public void SwitchProviderLocal(string provider)
    {
        Provider = provider;
        client = MakeClient(provider);
    }

    /// <summary>重置会话历史及相关状态（新会话）</summary>
    public void ResetHistory()
    {
        try
        {
            history.Clear();
            todoSteps = null;
            todoDoneSteps.Clear();
            currentStep = 0;
            pendingStepStart = 0;
            curIntent = null;
            dangerTcs = null;
            sessionTitle = "";
            sessionKey = "";   // 会话已重建：快照归属一并清空，等待 UI 注入新会话 Id
            lastReqSnapPath = "";   // 配对基准一并清空：防新会话的回应快照误配到旧会话文件
            lock (roundSnaps) roundSnaps.Clear();
        }
        catch (Exception ex)
        {
            Log($"[Error] 重置历史失败: {ex.Message}");
        }
    }

    /// <summary>由 UI 同步当前会话标题（打开历史会话时调用）：任务结束钉钉通知用会话标题代替原始任务文本</summary>
    public void SetSessionTitle(string? title) => sessionTitle = title?.Trim() ?? "";

    /// <summary>由 UI 同步当前会话标识（打开/新建会话时调用）：请求上下文快照按会话落盘 data/rounds/&lt;key&gt;/roundN.json，空=不落盘</summary>
    public void SetSessionKey(string? key) => sessionKey = key?.Trim() ?? "";

    /// <summary>请求前上下文快照：把即将发给模型的完整 history 落盘为可读文本（系统提示/工具结果/思维链原文都在内），
    /// 供思考卡「查看」复盘“模型这一轮看到了什么”。序号=轮号（与 Round 事件同源，1 基）；
    /// 同会话同序号再次出现时自动追加 -2/-3… 后缀防覆盖，历史快照文件得以保留。</summary>
    void SnapshotRoundContext(int roundNo)
    {
        lastReqSnapPath = "";   // 每轮先清配对基准：本轮落盘失败时回应快照回退默认命名，绝不误配上一轮文件
        if (string.IsNullOrEmpty(sessionKey)) return;
        try
        {
            var dir = Path.Combine(Paths.RoundsDir, SnapKey(sessionKey));
            Directory.CreateDirectory(dir);
            var head = $"第 {roundNo} 轮请求上下文 · 会话 {sessionKey} · {DateTime.Now:yyyy-MM-dd HH:mm:ss} · 共 {history.Count} 条消息";
            var sb = new StringBuilder();
            sb.Append(head).Append('\n').Append(new string('─', Math.Min(head.Length, 90))).Append('\n');
            var idx = 0;
            foreach (var h in history)
            {
                if (h is not JsonObject m) continue;
                idx++;
                sb.Append("\n◆ [").Append(idx).Append("] ").Append(SnapRoleLabel(m)).Append('\n');
                sb.Append(SnapMsgBody(m)).Append('\n');
            }
            var path = Path.Combine(dir, $"round{roundNo}.json");
            for (var k = 2; File.Exists(path); k++) path = Path.Combine(dir, $"round{roundNo}-{k}.json");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            lastReqSnapPath = path;   // 本轮请求快照实际路径：回应快照据此同名配对（同轮重跑的 roundN-2 版本各自配对）
            TrimRoundSnaps(dir);
        }
        catch (Exception ex) { Log($"[Error] 请求上下文快照落盘失败: {ex.Message}"); }
    }

    /// <summary>本轮回应快照：请求快照的配对文件，落盘模型本轮返回的**完整回应原文**
    /// （思维链 reasoning_content + 答复正文 content + 工具调用名与参数原文 + finish_reason），
    /// 供请求上下文查看器「回应内容」子页复盘“模型这一轮答了什么”。
    /// 与请求快照严格同基名配对（roundN[-k].json ↔ roundN[-k]-resp.json），落盘时机在 resp 到手、
    /// 任何兜底打回（continue）之前，故被打回的轮次同样留档。工具**执行结果**不在此文件内
    /// （结果作为 tool 消息进下一轮请求上下文，已由请求快照覆盖）。</summary>
    void SnapshotRoundResponse(int roundNo, LlmResponse resp)
    {
        if (string.IsNullOrEmpty(sessionKey)) return;
        try
        {
            var dir = Path.Combine(Paths.RoundsDir, SnapKey(sessionKey));
            Directory.CreateDirectory(dir);
            var thinkLen = resp.ReasoningContent?.Length ?? 0;
            var bodyLen = resp.Content?.Length ?? 0;
            var head = $"第 {roundNo} 轮回应内容 · 会话 {sessionKey} · {DateTime.Now:yyyy-MM-dd HH:mm:ss} · "
                + $"思维链 {thinkLen} 字 · 正文 {bodyLen} 字 · 工具调用 {resp.ToolCalls.Count} 个 · {resp.Usage.Total} tokens";
            var sb = new StringBuilder();
            sb.Append(head).Append('\n').Append(new string('─', Math.Min(head.Length, 90))).Append('\n');
            sb.Append("\n◆ 思维链（reasoning_content）\n");
            sb.Append(string.IsNullOrWhiteSpace(resp.ReasoningContent) ? "（本轮无思维链：非思考模式或模型未输出）\n" : resp.ReasoningContent!.TrimEnd() + "\n");
            sb.Append("\n◆ 答复正文（content）\n");
            sb.Append(string.IsNullOrWhiteSpace(resp.Content) ? "（本轮无正文：纯工具调用轮，意图见下方工具调用）\n" : resp.Content!.TrimEnd() + "\n");
            var idx = 0;
            foreach (var tc in resp.ToolCalls)
            {
                idx++;
                // 参数独占一行且行首即 JSON 起始符：查看器 FormatCtxSnapshot 能整行缩进美化并把 \uXXXX 还原成中文
                sb.Append("\n◆ 工具调用 [").Append(idx).Append("] ").Append(tc.Name).Append('\n');
                sb.Append(string.IsNullOrWhiteSpace(tc.Arguments) ? "{}" : tc.Arguments.Trim()).Append('\n');
            }
            sb.Append("\n◆ 收尾（finish_reason）: ").Append(string.IsNullOrEmpty(resp.FinishReason) ? "null（未收到，流可能被截断）" : resp.FinishReason).Append('\n');
            File.WriteAllText(RespSnapPath(dir, roundNo), sb.ToString(), new UTF8Encoding(false));
            TrimRoundSnaps(dir);
        }
        catch (Exception ex) { Log($"[Error] 回应内容快照落盘失败: {ex.Message}"); }
    }

    /// <summary>回应快照路径：优先与本轮请求快照同基名配对（重跑版本互不覆盖），
    /// 本轮无请求快照时回退 round{N}-resp.json（同轮已有则追加 -2/-3… 防覆盖）。</summary>
    string RespSnapPath(string dir, int roundNo)
    {
        if (!string.IsNullOrEmpty(lastReqSnapPath))
        {
            var baseName = Path.GetFileNameWithoutExtension(lastReqSnapPath);
            if (baseName.StartsWith("round", StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetDirectoryName(lastReqSnapPath), dir, StringComparison.OrdinalIgnoreCase))
                return Path.Combine(dir, baseName + "-resp.json");
        }
        var path = Path.Combine(dir, $"round{roundNo}-resp.json");
        for (var k = 2; File.Exists(path); k++) path = Path.Combine(dir, $"round{roundNo}-{k}-resp.json");
        return path;
    }

    /// <summary>单会话快照数封顶：请求快照与回应快照**各自独立计数**（各 RoundSnapFilesMax 个），
    /// 超限按修改时间丢最旧。不做“必须配对”的强删——请求侧落盘失败时回应侧会走回退命名（roundN-resp.json 无配对请求），
    /// 该文件仍有复盘价值，只按数量上限自然淘汰，避免刚写就被当孤立文件删掉。</summary>
    void TrimRoundSnaps(string dir)
    {
        try
        {
            foreach (var resp in new[] { false, true })   // 先清请求侧再清回应侧
            {
                var files = Directory.GetFiles(dir, "round*.json")
                    .Where(f => IsRespSnap(f) == resp)
                    .OrderBy(f => File.GetLastWriteTimeUtc(f)).ToList();
                if (files.Count <= RoundSnapFilesMax) continue;
                foreach (var old in files.Take(files.Count - RoundSnapFilesMax))
                    try { File.Delete(old); } catch { }
            }
        }
        catch { }
    }

    /// <summary>是否回应快照文件（基名以 -resp 结尾，如 round3-resp.json / round3-2-resp.json）</summary>
    static bool IsRespSnap(string path) =>
        Path.GetFileNameWithoutExtension(path).EndsWith("-resp", StringComparison.OrdinalIgnoreCase);

    /// <summary>会话标识转目录名：只保留字母/数字，杜绝路径分隔符等特殊字符</summary>
    static string SnapKey(string key) => new string(key.Where(char.IsLetterOrDigit).ToArray());

    /// <summary>快照消息行首的角色标注：历史 role 映射为中文名 + 原文 role，便于一眼区分</summary>
    static string SnapRoleLabel(JsonObject m)
    {
        var role = m["role"]?.GetValue<string>() ?? "?";
        var tag = role switch
        {
            "system" => "系统提示",
            "user" => "用户",
            "assistant" => "模型",
            "tool" => "工具结果",
            _ => "其他"
        };
        return $"{tag} ({role})";
    }

    /// <summary>快照消息正文：content 原文 + reasoning_content 思维链 + tool_calls 调用清单，逐段展开可读文本</summary>
    static string SnapMsgBody(JsonObject m)
    {
        var sb = new StringBuilder();
        if (SnapContent(m["content"] as JsonNode) is { Length: > 0 } c) sb.Append(c).Append('\n');
        if (m["reasoning_content"]?.GetValue<string>() is { Length: > 0 } rc)
            sb.Append("┄ 思维链:\n").Append(rc).Append('\n');
        if (m["tool_calls"] is JsonArray tcs)
        {
            var i = 0;
            foreach (var t in tcs.OfType<JsonObject>())
            {
                var fn = t["function"] as JsonObject;
                var nm = fn?["name"]?.GetValue<string>() ?? "?";
                var args = fn?["arguments"]?.GetValue<string>() ?? "";
                sb.Append("┄ 工具调用 #").Append(i++).Append(": ").Append(nm);
                if (args.Length > 0) sb.Append('\n').Append(args);
                sb.Append('\n');
            }
        }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>content 字段归一为文本：OpenAI 兼容多段数组时逐段取 text/input_text 拼合</summary>
    static string? SnapContent(JsonNode? cn) => cn switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonArray arr => string.Join("\n", arr.OfType<JsonObject>()
            .Select(o => o["text"]?.GetValue<string>() ?? o["input_text"]?.GetValue<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))),
        _ => null
    };


    /// <summary>当前会话标题（首次任务模型生成摘要，可被 SetSessionTitle 覆盖；会话存储用）</summary>
    public string SessionTitle => sessionTitle;

    /// <summary>从历史会话消息重建上下文（载入会话时恢复对话记忆）。
    /// 注意：会话存储只保留 role/content，无 tool_calls/tool_call_id 结构，
    /// 工具结果消息（role=tool）随会话恢复会形成孤立 tool 消息，被 OpenAI 兼容 API 判 400；
    /// 故载入时过滤 tool 消息，只恢复 user/assistant 文本（与 UI 打开历史会话行为一致）。</summary>
    public void LoadHistory(IEnumerable<(string Role, string Content)> msgs)
    {
        try
        {
            history.Clear();
            history.Add(SysMessage());
            foreach (var (role, content) in msgs)
            {
                if (string.IsNullOrWhiteSpace(content)) continue;
                if (role == "tool") continue;   // 工具中间过程不随会话恢复（无 tool_call_id 结构的孤立消息会被 API 拒收 400）
                history.Add(new JsonObject { ["role"] = role, ["content"] = content });
            }
        }
        catch (Exception ex)
        {
            Log($"[Error] 加载历史失败: {ex.Message}");
        }
    }

    /// <summary>从历史记录中获取最后一条助手消息的内容（跳过空占位，避免显示“本轮无正文”等内部文案）</summary>
    public string? GetLastAssistantMessage()
    {
        try
        {
            for (int i = history.Count - 1; i >= 0; i--)
            {
                if (history[i] is JsonObject msg && msg["role"]?.GetValue<string>() == "assistant")
                {
                    var c = msg["content"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(c) && !c.StartsWith("（本轮")) return c;
                }
            }
        }
        catch (Exception ex)
        {
            Log($"[Error] 获取助手消息失败: {ex.Message}");
        }
        return null;
    }

    /// <summary>导出完整会话历史（会话存储用，不含系统提示）：LoadHistory 可原样回灌</summary>
    public List<(string Role, string Content)> DumpHistory()
    {
        var list = new List<(string Role, string Content)>();
        foreach (var h in history)
        {
            if (h is not JsonObject m) continue;
            var role = m["role"]?.GetValue<string>() ?? "";
            if (role == "system") continue;
            list.Add((role, m["content"]?.GetValue<string>() ?? ""));
        }
        return list;
    }

    /// <summary>统一任务入口：普通会话 / 自动任务 / 编排执行 / 计划子会话 / 服务端多会话等全部会话任务均经此启动。
    /// 启动前依次检查两道闸：① 系统资源闸 —— 主机 CPU 或内存占用 ≥ 80% 时不再放行新任务（只拦新任务，不打断在跑任务）；
    /// ② 厂商并发闸 LlmConcurrencyGate —— 该厂商并发是否已满（本地默认上限1、在线默认2，可配置覆盖）。
    /// 任一不满足直接拒启并给出明确提示，不进入运行也不占用上下文。</summary>
    public async Task RunAsync(string userText, CancellationToken ct, bool forceWebFullContext = false)
    {
        // 记录「强制网页新会话+完整上下文」标志：仅作用于本次任务首个模型调用（见 RunAsyncCore），消费后立即复位
        _forceWebFullContext = forceWebFullContext;
        if (HostLoadMonitor.IsOverloaded())
        {
            LastRunSuccess = false;
            RoundCount = 0;
            var cpu = HostLoadMonitor.CpuPercent;
            var mem = HostLoadMonitor.MemPercent;
            LastFailReason = $"主机资源占用过高（CPU {(cpu >= 0 ? $"{cpu:0}%" : "未知")}、内存 {(mem >= 0 ? $"{mem:0}%" : "未知")}，阈值 {HostLoadMonitor.GatePct:0}%），新任务未启动。请等待正在进行的任务结束、或释放主机资源后再试。";
            Log("[资源限制] " + LastFailReason);
            Emit(new UiEvent { Type = UiEventType.Failed, Reasoning = LastFailReason });
            Emit(new UiEvent { Type = UiEventType.Finished, Round = 0, TotalTokens = 0 });
            return;
        }
        var provider = string.IsNullOrEmpty(Provider) ? cfg.Provider : Provider;
        if (!string.IsNullOrWhiteSpace(provider))
        {
            var max = cfg.MaxConcurrencyFor(provider);
            // 占用位登记"本次在跑的具体模型"（活引用）：中途切模型 / 故障转移后下拉里的金色标注自动跟随；
            // 若已迁移到别的厂商，本占用位不再认领原厂商下的模型（返回空串），避免金色错挂到同厂商其它模型
            string RunModel()
            {
                var curProv = string.IsNullOrEmpty(Provider) ? cfg.Provider : Provider;
                return string.Equals(curProv, provider, StringComparison.OrdinalIgnoreCase) ? client.Model : "";
            }
            var slot = LlmConcurrencyGate.TryEnter(provider, max, RunModel);
            if (slot == null)
            {
                LastRunSuccess = false;
                RoundCount = 0;
                LastFailReason = $"厂商 {provider} 并发已达上限（当前 {LlmConcurrencyGate.Current(provider).Cur}/{max}），新任务未启动。请等待正在进行的任务结束，或改用其它模型后再试。";
                Log("[并发限制] " + LastFailReason);
                Emit(new UiEvent { Type = UiEventType.Failed, Reasoning = LastFailReason });
                Emit(new UiEvent { Type = UiEventType.Finished, Round = 0, TotalTokens = 0 });
                return;
            }
            try
            {
                await RunAsyncCore(userText, ct);
            }
            finally
            {
                slot.Dispose();   // 任务结束（含取消/异常/计划拒批等一切提前返回）释放并发位
            }
        }
        else
        {
            await RunAsyncCore(userText, ct);
        }
    }

    private async Task RunAsyncCore(string userText, CancellationToken ct)
    {
        _runCt = ct;   // 记录本次运行的取消令牌：危险确认挂起跟随（用户停止即释放）
        journal.CurrentTask = userText;
        journal.CurrentSession = SessionId;   // 变更日志标注归属会话：宿主按此把改动归到各会话卡片（并行不串台）
        journal.WriteCount = 0;   // 任务级写计数清零（钉钉按需通知判定用）
        Verify.ResetRepairCount(); // 任务级连续验证失败计数清零（修复轮数上限按任务重置）
        taskHadDanger = false;    // 任务级危险确认标记清零
        curTaskIsShortMsg = false; // 承接上文短消息标记清零
        lock (readSigCount) readSigCount.Clear();  // 任务级重复读计数清零
        // 首轮拦截计数：模型未先建待办清单时打回重写（上限 2 次，防死循环）；
        // 空文本收尾计数：模型返回空 content + 空 tool_calls 时打回重写（上限 1 次）
        var createBlocked = 0;
        var finalEmpty = 0;
        var askRetried = 0;    // 模型"停下要需求"自动找回上轮历史重试计数（上限 1 次，防死循环）
        var abnormalStopRetried = 0;  // 模型异常收尾（finish_reason 缺失且无 tool_calls）追发继续指令计数（上限 2 次，防死循环）
        var intentRetried = 0;      // 模型"意图宣布未行动"（纯文本只说要做没调工具）追发执行指令计数（上限 2 次，防死循环）
        // 清单已建标记：模型 create 成功后所有轮不再拦截（拦截只针对"从未建过清单"的任务开头）
        var todoCreated = false;
        // 只读豁免标记：出现过纯只读工具轮（查看/问答类任务）后放行；后续轮含写工具时恢复拦截补建清单
        var readOnlyPassed = false;
        todoSteps = null;   // 任务开始清空计划步骤缓存
        todoDoneSteps.Clear();
        fileRefs.Clear(); lastEssentialFp = 0;   // P6: 任务级文件清单与必用文件缓存随新任务清空
        currentStep = 0;
        pendingStepStart = 0;
        if (history.Count == 0) history.Add(SysMessage());
        // 网页模型「强制新会话+完整上下文」：只作用于本次任务首个模型调用（下方 round==0 的 ChatStreamAsync），捕获后立即复位
        var forceWebFullContext = _forceWebFullContext;
        _forceWebFullContext = false;

        // 首次请求时生成会话摘要标题（15 字内），用于会话历史列表展示
        if (IsFirstUserMessage())
        {
            var summary = await GenerateTitleSummaryAsync(userText, ct);
            if (!string.IsNullOrWhiteSpace(summary))
            {
                sessionTitle = summary;   // 缓存会话标题：任务结束钉钉通知用会话标题代替任务原文
                Emit(new UiEvent { Type = UiEventType.Summary, Reasoning = summary });
            }
        }

        // 现状快照：附带工作区未提交改动清单，供模型尽早发现"功能可能已实现"，避免重复探索
        try
        {
            var snapshot = await GitMgr.StatusSnapshotAsync(cfg);
            if (snapshot.StartsWith("跳过"))
            {
                Log("[Snapshot] " + snapshot);   // 诊断日志：快照不可用时记录原因，便于定位
            }
            else
            {
                history.Add(new JsonObject
                {
                    ["role"] = "system",
                    ["content"] = "[现状快照] 工作区未提交改动：" + snapshot +
                        "。若需求涉及的功能似乎已存在，先核实这些改动再决定是否需要重新实现，避免重复探索。"
                });
                Log("[Snapshot] " + LLMClient.Trunc(snapshot, 120));
            }
        }
        catch (Exception ex) { Log("[Snapshot] 异常: " + ex.Message); /* 快照失败不阻断主流程 */ }

        // 用户偏好自动沉淀：检测用户消息中的长期语义（"以后/下次/记住…"类表述），
        // 自动把原句追加到 .gairr/prompts/prefs.md（角色提示词要求模型先 Read 该文件并全程遵循）；同句不重复；失败不阻断
        try
        {
            if (UserPrefs.Detect(userText) is { } pref && UserPrefs.Append(cfg, pref))
                Log("[Pref] 已自动沉淀用户偏好: " + LLMClient.Trunc(pref, 60));
        }
        catch (Exception ex) { Log("[Pref] 沉淀异常: " + ex.Message); }

        // 短消息（≤40 字）大概率是承接上文的确认/追问/追加检索（如"好，开始"、"再查查xxx"），
        // 不能当作新任务边界——否则压缩器判定①会把上轮压成"历史任务摘要"，模型看不到询问全文
        // 而停下来要求补需求。不枚举话术（枚举必然漏），一律按"短消息保留上轮上下文"处理；
        // 长消息即使实为确认也无妨：模型若仍停下要需求，由下方"停下确认兜底"自动找回上轮原文重试。
        List<JsonObject>? lastRoundRaw = null;   // 上轮交互原文缓存：压缩会把它摘要化，模型停下要需求时自动找回补发（用户无需手动复制）
        curTaskIsShortMsg = GetLastAssistantMessage() != null && userText.Trim().Length <= 40;
        if (curTaskIsShortMsg) Log($"[Short] 承接上文短消息：{LLMClient.Trunc(userText, 40)}");
        curTaskText = userText;   // 缓存当前任务文本：压缩锚点词来源（缺了它 AnchorWords 只剩 todoSteps，只读/简单任务锚点为空，判定②③失去主题豁免）
        history.Add(new JsonObject { ["role"] = "user", ["content"] = userText });

        // ── 计划审批模式：首轮只生成计划并等待外部批准，批准后再进入工具执行循环 ──
        if (PlanMode)
        {
            var approvedPlan = await GenerateAndWaitPlanAsync(userText, ct);
            if (approvedPlan == null)
            {
                LastRunSuccess = false;
                LastFailReason = "计划被拒绝、超时或生成失败";
                RoundCount = 0;
                Emit(new UiEvent { Type = UiEventType.Failed, Reasoning = LastFailReason });
                return;
            }
            history.Add(new JsonObject
            {
                ["role"] = "system",
                ["content"] = $"[已批准计划] {approvedPlan.Summary}\n" +
                    string.Join("\n", approvedPlan.Steps.Select((s, i) => $"{i + 1}. [{s.Action}] {s.Target}: {s.Description}"))
            });
        }

        // ── Flow 模式：任务级创建 runner（null=自主模式，下方全部行为不变）；框架接管建卡/步骤推进/工具过滤 ──
        if (Flow is { Count: > 0 } flowSteps)
        {
            flowRunner = new FlowRunner(flowSteps, Log);
            TodoCreate(flowSteps.Count, flowSteps.Select(s => s.Step).ToList());   // 框架建计划卡（模型不管理清单）
            InjectUserStep(flowRunner.CurrentInstruction());                        // 注入第 1 步指令
            Flow = null;                                                            // 消费后置 null（字段注释约定）
        }
        // 缓存"上一条 user 消息起的完整上轮交互"（含 assistant/tool 结果），供停下确认兜底找回
        for (var i = history.Count - 2; i >= 1; i--)
        {
            if (history[i] is JsonObject m && (m["role"]?.GetValue<string>() ?? "") == "user")
            {
                lastRoundRaw = new List<JsonObject>();
                for (var j = i; j < history.Count - 1; j++) lastRoundRaw.Add(CloneJson(history[j]!));
                break;
            }
        }
        // 承接提示：短消息是对上回合的延续，把上回合结论要点预置给模型（结论原文虽在上下文中，
        // 但模型易忽略/被摘要化），引导它直接复用其中已确认的文件路径/行号/位置，而不是凭惯例猜路径或重新定位。
        // 注意：上回合结论只是“定位线索”与“已确认结论”，不能覆盖用户当前原话；若用户当前消息只是追问/质疑/分析，
        // 禁止把它当成新的开发需求继续实现。
        if (curTaskIsShortMsg && GetLastAssistantMessage() is { } prevSummary)
        {
            history.Add(new JsonObject
            {
                ["role"] = "system",
                ["content"] = "（框架提示）本条用户消息是对上一回合的承接（" + LLMClient.Trunc(userText, 40) + "）。\n" +
                    "上一回合结论：" + LLMClient.Trunc(prevSummary, 1500) + "\n" +
                    "请优先复用该结论中已确认的文件路径/行号/位置/方案，直接继续；确需更多细节时再用 Read/Grep/RecallHistory 补充；不要凭项目命名惯例猜测文件路径。\n" +
                    "硬性边界：上一回合结论不能自动扩展成新的需求主题。若用户当前消息是在质疑、追问、要求分析或要求确认，必须先按当前消息回答/核实，不能继续按上一回合结论实施新改动；若无法从用户原话确认当前任务目标，必须先询问用户。"
            });
        }

        var totalTokens = 0;
        var round = 0;
        var success = true;
        try
        {
            while (round < cfg.MaxRounds)
            {
                ct.ThrowIfCancellationRequested();
                string? roundFallback = null;   // 本轮若触发框架兜底，记录类型随 Round 事件透给 UI

                await CompressHistoryAsync();

                Log($"[Round {round + 1}] 调用模型...");
                Emit(new UiEvent { Type = UiEventType.WaitingModel });
                // 一次流式请求拿完整响应（含 tool_calls）：思考轮文本不直播进气泡，
                // 思考内容统一由 Round 事件的思考卡展示，气泡打字机只保留给最终结论
                var roundSw = System.Diagnostics.Stopwatch.StartNew();
                // 建议3：记录本轮本地估算（含工具 schema，与服务端 prompt 同口径），与服务端实际 prompt token 校准
                var flowWl = flowRunner?.CurrentTools()?.ToList();
                flowWl?.Remove("UpdateTodo");   // Flow 模式计划卡归框架，模型不管理清单
                // 编排会话：注入完整工具集（编排执行需 UpdateTodo/Edit/Bash，用户决策 2026-09-01），不再只读受限。
                var schemaWhitelist = flowWl;
                var flowSchemas = schemaWhitelist != null ? FilterSchemasByWhitelist(registry.Schemas(), schemaWhitelist) : registry.Schemas();
                lastRequestEstimate = ContextCompressor.EstimateTokens(history) + ContextCompressor.EstimateTokens(flowSchemas);
                // 请求前快照：压缩后、发送前，把将发给模型的完整上下文落盘（会话 Key 由 UI 注入；思考卡「查看」复盘用）
                SnapshotRoundContext(round + 1);
                // 深度思考渐进反馈：思考档（high/thinking on）单轮可能 30-60s+，reasoning_content 增量
                // 在此节流（约 1.2s 一次）推送 UI 直播卡，思考期间实时显示思维动向；轮结束仍由 Round 事件统一上卡/转正
                var liveSb = new StringBuilder();
                var liveSw = System.Diagnostics.Stopwatch.StartNew();
                var resp = await client.ChatStreamAsync(history, flowSchemas, _delta => { }, null, ct, onReasoning: delta =>
                {
                    liveSb.Append(delta);
                    if (liveSw.ElapsedMilliseconds >= 1200)
                    {
                        liveSw.Restart();
                        Emit(new UiEvent { Type = UiEventType.ThinkingLive, LiveText = liveSb.ToString() });
                    }
                }, forceWebFullContext: forceWebFullContext && round == 0);
                roundSw.Stop();
                totalTokens += resp.Usage.Total;
                lastUsagePrompt = resp.Usage.Prompt;   // 建议3：服务端实际 prompt token，校准本地估算偏差
                round++;
                // 回应快照：与本轮请求快照（上方 SnapshotRoundContext）同基名配对落盘，记录模型本轮完整回应原文。
                // 位置在所有兜底打回（continue）之前：被打回重写的轮次同样留档，复盘时能看到"模型当时答了什么、为何被拦"
                SnapshotRoundResponse(round, resp);
                var reasoningPreview = resp.ToolCalls.Count > 0 ? (resp.Content ?? "[null]") : "[纯文本]";
                Log($"[Round {round}] 模型返回，tool_calls={resp.ToolCalls.Count}，usage={resp.Usage.Total} tokens，content={reasoningPreview[..Math.Min(reasoningPreview.Length, 100)]}");
                // ── 任务复杂度分级：L0 级快速通道 ──
                // 模型在第 1 轮直接调用写工具且仅涉及单文件时，视为 L0 级简单任务，免清单放行。
                // 判定条件：首轮 + 含写工具 + 写工具目标文件数 ≤ 1 + 无跨文件操作。
                // 这避免了"改一个配置值也要先建清单再读地图再定位"的冗余流程。
                var hasCreate = resp.ToolCalls.Any(tc => IsTodoCreate(tc.Arguments));
                if (hasCreate)
                {
                    todoCreated = true;
                    // 预扫描阶段缓存步骤文本（工具并发执行前，保证同轮 update 卡片标题能取到步骤）
                    foreach (var tc in resp.ToolCalls)
                        if (IsTodoCreate(tc.Arguments) && ParseTodoArgs(tc.Arguments) is { } td && td.Steps != null)
                            todoSteps = td.Steps;
                    // 计划创建后先只发送 create 事件，把"计划步骤1"延迟到步骤1真正启动前展示，
                    // 避免"创建-计划"信息条与步骤1的工具卡混在一起，造成创建计划被包在步骤1执行过程中的错觉。
                    if (todoSteps is { Count: > 0 } && currentStep == 0)
                    {
                        pendingStepStart = 1;
                        Log("[StepStart] 创建计划完成，延迟展示计划步骤1");
                    }
                }
                // L0 快速通道：首轮直接修改单文件 → 免清单放行（提示词已引导模型按分级策略执行）
                var l0FastPass = false;
                if (!todoCreated && round == 1 && flowRunner == null && resp.ToolCalls.Count > 0 && !hasCreate)
                {
                    var writeTools = resp.ToolCalls.Where(tc => tc.Name == "Write" || tc.Name == "Edit").ToList();
                    if (writeTools.Count > 0)
                    {
                        // 提取写工具的目标文件路径，判断是否单文件
                        var targetFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var tc in writeTools)
                        {
                            try
                            {
                                var args = JsonNode.Parse(tc.Arguments) as JsonObject;
                                var fp = args?["file_path"]?.GetValue<string>() ?? args?["path"]?.GetValue<string>();
                                if (!string.IsNullOrEmpty(fp)) targetFiles.Add(fp);
                            }
                            catch { /* JSON 解析失败忽略 */ }
                        }
                        if (targetFiles.Count <= 1)
                        {
                            l0FastPass = true;
                            readOnlyPassed = true;   // 同时标记只读豁免，后续轮不再拦截
                            Log($"[L0-FastPass] 第 {round} 轮检测到 L0 级任务（单文件修改），免清单放行");
                        }
                    }
                }
                if (!todoCreated && !l0FastPass && flowRunner == null && resp.ToolCalls.Count > 0 && !hasCreate)
                {
                    var allReadOnly = resp.ToolCalls.All(tc => ReadOnlyToolSet.Contains(tc.Name));
                    if (allReadOnly)
                    {
                        if (!readOnlyPassed)
                        {
                            readOnlyPassed = true;
                            Log($"[TodoExempt] 第 {round} 轮全部只读工具，豁免强制清单（后续若改文件会补拦截）");
                        }
                    }
                    else if (createBlocked < 2)
                    {
                        createBlocked++;
                        roundFallback = "[未建清单兜底]";
                        Log($"[Blocked] 第 {round} 轮未调用 UpdateTodo(create)，已打回重写（第 {createBlocked} 次）" +
                            (readOnlyPassed ? "，只读豁免结束：本轮含写工具" : ""));
                        // 探索成果保留：打回会形成新任务边界，旧轮将被压缩摘要化；把此前 assistant 正文里的探索结论
                        // 汇成摘要附在打回指令里，避免模型建完清单后从零重新探索
                        var digest = ExploreDigest();
                        // 思考模式下 content 常留空：写入历史的 assistant 消息必须非空（空字符串会被 API 拒收 400）
                        history.Add(new JsonObject { ["role"] = "assistant", ["content"] = string.IsNullOrWhiteSpace(resp.Content) ? "（本轮缺少任务拆分，正在重新规划）" : resp.Content });
                        history.Add(new JsonObject
                        {
                            ["role"] = "user",
                            ["content"] = (readOnlyPassed
                                ? $"（系统提示）本任务已开始修改文件但未建清单：请先调用 UpdateTodo（action=create，steps 为 {SystemCfg.PlanMinSteps}~8 条中文步骤，按任务复杂度定步数）补建待办清单，再继续执行。"
                                : $"（系统提示）本轮缺少任务拆分：请先调用 UpdateTodo（action=create，steps 为 {SystemCfg.PlanMinSteps}~8 条中文步骤，按任务复杂度定步数）创建待办清单，再执行其它工具。")
                                + digest,
                        });
                        // 兜底触发时即时向 UI 暴露本轮状态，避免工具条/气泡中看不到本轮被拦截
                        Emit(new UiEvent
                        {
                            Type = UiEventType.Round,
                            Round = round,
                            Usage = resp.Usage,
                            Reasoning = resp.Content ?? "本轮被框架拦截：缺少任务拆分",
                            Intent = null,
                            IsFinal = false,
                            RoundDurationMs = (int)roundSw.ElapsedMilliseconds,
                            Fallback = roundFallback
                        });
                        continue;
                    }
                }
                // 意图说明回退：模型未输出 content 时（思考模式常见留空），客户端补一句并写入历史，保证每轮可见“下一步做什么”
                var tcNames = resp.ToolCalls.Select(tc => tc.Name).Distinct().Take(3).ToList();
                var intentMsg = tcNames.Count > 0 && string.IsNullOrWhiteSpace(resp.Content)
                    ? "执行" + KindOf(tcNames[0]) + ": " + string.Join("、", tcNames)
                    : null;
                curIntent = intentMsg ?? resp.Content;   // 缓存本轮意图，供危险命令确认提示展示
                // 推送每轮思考到 UI：content 为空时回退 reasoning_content（思维链），供思考条展示
                // 最后一轮纯文本回复的思考内容会展示在结果气泡中，标记为 IsFinal 供 UI 去重
                var isFinal = resp.ToolCalls.Count == 0;
                // 空文本收尾保护：模型偶发返回“空 content + 空 tool_calls”（思维链占满输出被截断或中途放弃），
                // 此时若直接结束，任务显示“本轮无正文”且实际未完成；先打回重写 1 次，仍空才按完成处理
                if (isFinal && string.IsNullOrWhiteSpace(resp.Content) && finalEmpty == 0)
                {
                    finalEmpty++;
                    roundFallback = "[空文本收尾兜底]";
                    Log($"[Retry] 第 {round} 轮空文本收尾，已打回重写（第 {finalEmpty} 次）");
                    history.Add(new JsonObject { ["role"] = "assistant", ["content"] = "（本轮未给出结论）" });
                    // 自动任务无人值守，必须结束而不是继续调用工具；交互式任务允许继续执行工具。
                    history.Add(new JsonObject { ["role"] = "user", ["content"] = isAutoTask
                        ? "（系统提示）本轮未给出结论。你是自动任务执行器，当前无人值守。请直接给出最终结论并输出 <autotask-result> 结束标记，不要继续调用工具，也不要向用户确认需求。"
                        : "（系统提示）请继续：给出最终结论，或继续执行工具直到完成任务。" });
                    Emit(new UiEvent
                    {
                        Type = UiEventType.Round,
                        Round = round,
                        Usage = resp.Usage,
                        Reasoning = "本轮被框架拦截：空文本收尾",
                        Intent = null,
                        IsFinal = false,
                        RoundDurationMs = (int)roundSw.ElapsedMilliseconds,
                        Fallback = roundFallback
                    });
                    continue;
                }
                // 异常收尾兜底：模型未收到 finish_reason（SSE 中断/流被截断），且本轮无 tool_calls、content 为半截文本
                // （非空、非最终结论）。非思考模式下偶发：模型"要执行工具"时流被截断，既无 tool_calls 也无 finish_reason，
                // 若按最终文本存盘则任务半途而废。此时追发"继续"指令让模型接着干，带重试上限防死循环。
                if (isFinal && !string.IsNullOrWhiteSpace(resp.Content) && resp.FinishReason == null && abnormalStopRetried < 2)
                {
                    abnormalStopRetried++;
                    roundFallback = "[异常收尾兜底]";
                    Log($"[Resume] 第 {round} 轮模型异常收尾（finish_reason 缺失、无 tool_calls），追发继续指令（第 {abnormalStopRetried} 次）");
                    history.Add(new JsonObject { ["role"] = "assistant", ["content"] = resp.Content });
                    history.Add(new JsonObject { ["role"] = "user", ["content"] = isAutoTask
                        ? "（系统提示）上一轮模型返回异常中断（未正常结束）。请从上次中断处继续执行：若需调用工具则直接调用，若已接近完成则直接给出最终结论并输出 <autotask-result> 结束标记。不要重复已完成的步骤。"
                        : "（系统提示）上一轮模型返回异常中断（未正常结束、未执行工具）。请从上次中断处继续工作：若需调用工具则直接调用，若已接近完成则直接给出最终结论。不要重复已完成的步骤，也不要向用户确认需求。" });
                    Emit(new UiEvent
                    {
                        Type = UiEventType.Round,
                        Round = round,
                        Usage = resp.Usage,
                        Reasoning = "本轮被框架拦截：异常收尾",
                        Intent = null,
                        IsFinal = false,
                        RoundDurationMs = (int)roundSw.ElapsedMilliseconds,
                        Fallback = roundFallback
                    });
                    continue;
                }
                // 意图宣布兜底：模型纯文本收尾（无 tool_calls、finish_reason 正常）却只是口头宣布下一步动作
                // （"现在建清单，然后继续改"这类），没真正执行工具——典型成因是上一轮被框架打回要求补建清单后，
                // 模型复述了系统提示但没实际调用 UpdateTodo，直接结束会让任务半途而废。追发"请立即实际调用工具"指令，
                // 带重试上限防死循环。放在异常收尾兜底之后（finish_reason 缺失优先走异常路径），停下确认兜底之前。
                if (isFinal && !isAutoTask && intentRetried < 2 && LooksLikeIntentAnnouncement(resp.Content))
                {
                    intentRetried++;
                    roundFallback = "[意图宣布兜底]";
                    var intentText = resp.Content ?? "";
                    Log($"[Intent] 第 {round} 轮模型只宣布意图未调用工具（\"{LLMClient.Trunc(intentText, 40)}\"），追发执行指令（第 {intentRetried} 次）");
                    history.Add(new JsonObject { ["role"] = "assistant", ["content"] = intentText });
                    history.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = "（系统提示）你上一条回复只是用文字描述了接下来要做什么，但实际没有调用任何工具，任务尚未完成。请立即真正执行：该调用工具时直接调用工具（如建清单先调 UpdateTodo action=create），不要只用文字复述计划；接近完成时再给出最终结论。不要向用户确认需求。"
                    });
                    Emit(new UiEvent
                    {
                        Type = UiEventType.Round,
                        Round = round,
                        Usage = resp.Usage,
                        Reasoning = "本轮被框架拦截：意图宣布",
                        Intent = resp.Content,
                        IsFinal = false,
                        RoundDurationMs = (int)roundSw.ElapsedMilliseconds,
                        Fallback = roundFallback
                    });
                    continue;
                }
                // 停下确认兜底：模型纯文本收尾却向用户要需求（如"我没有拿到具体的需求描述，无法确定要做什么"），
                // 用户无法手动补历史（复制粘贴）；自动把找回的上一轮完整对话补进上下文重试一次，还停才如实转达。
                // 自动任务模式下禁止此行为：无人值守，直接按最合理假设继续并给出结论。
                if (!isAutoTask && isFinal && askRetried == 0 && lastRoundRaw is { Count: > 0 } && LooksLikeAskForInput(resp.Content))
                {
                    askRetried++;
                    Log($"[Recall] 第 {round} 轮模型停下要需求，找回上轮历史补发重试（第 {askRetried} 次）");
                    history.Add(new JsonObject { ["role"] = "assistant", ["content"] = string.IsNullOrWhiteSpace(resp.Content) ? "（本轮尝试确认需求）" : resp.Content });
                    var sb = new StringBuilder("[框架找回的上一轮完整对话（原上下文已被压缩，用户无法手动提供）]\n");
                    foreach (var m in lastRoundRaw)
                    {
                        var role = m["role"]?.GetValue<string>() ?? "";
                        var content = m["content"]?.GetValue<string>() ?? "";
                        if (role == "tool")
                            content = "(工具结果 " + content.Length + " 字符) " + LLMClient.Trunc(content, 1500);
                        sb.Append(role).Append(": ").Append(LLMClient.Trunc(content, 4000)).Append('\n');
                    }
                    history.Add(new JsonObject { ["role"] = "system", ["content"] = LLMClient.Trunc(sb.ToString(), 12000) });
                    history.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = "（系统提示）以上是找回的上一轮完整对话（此前被压缩省略）。用户无法操作补充历史，请直接基于以上内容继续执行；仅当信息确实仍不足时才可停下询问。"
                    });
                    continue;
                }
                // 思考模式兜底：最终轮 content 为空但思维链非空时，用思维链充当结论正文
                // （IsFinal 轮 UI 不会重复添加思考条，结论仍进气泡）
                if (isFinal && string.IsNullOrWhiteSpace(resp.Content) && !string.IsNullOrWhiteSpace(resp.ReasoningContent))
                    resp = resp with { Content = resp.ReasoningContent };
                // 自动任务结论清理：删除结束标记/错误围栏，保证气泡与历史都按 Markdown 正常渲染
                if (isAutoTask && isFinal && !string.IsNullOrWhiteSpace(resp.Content))
                    resp = resp with { Content = CleanAutoTaskConclusion(resp.Content!) };
                // 气泡打字机只服务最终结论：流结束且确认无 tool_calls 才把全文交给气泡打字机；
                // 思考轮文本只进思考卡、不再实时上屏，彻底避免工具条打断气泡、尾部标点丢失
                if (isFinal && !string.IsNullOrWhiteSpace(resp.Content))
                    Emit(new UiEvent { Type = UiEventType.StreamDelta, Delta = resp.Content!, Start = true });
                var thinking = string.IsNullOrWhiteSpace(resp.Content) ? resp.ReasoningContent : resp.Content;
                // 步骤切换卡片延迟展示：在思考条之前插入"计划步骤N"，并自动勾选上一步
                if (pendingStepStart > 0)
                {
                    ShowStepStartCard(pendingStepStart);
                    pendingStepStart = 0;
                }
                Emit(new UiEvent { Type = UiEventType.Round, Round = round, Usage = resp.Usage, Reasoning = thinking, Intent = intentMsg, IsFinal = isFinal, RoundDurationMs = (int)roundSw.ElapsedMilliseconds, Fallback = roundFallback });

                if (isFinal)
                {
                    Log("[Round " + round + "] 纯文本回复...");
                    if (flowRunner is { } fr)
                    {
                        // Flow 模式：纯文本 = 当前步骤结论 → 框架判定推进（计划卡归框架，不走模型清单收尾）
                        var (doneIdx, allDone, inj) = fr.StepConcluded(resp.Content ?? "");
                        if (doneIdx >= 0) TodoStepDone(doneIdx);
                        if (allDone && inj == null)
                        {
                            // 收尾轮：继续下方既有收尾输出最终结论（步骤已全勾）
                        }
                        else
                        {
                            if (allDone) TodoAllDone();
                            if (inj != null) InjectUserStep(inj);
                            continue;   // 打回/下一步/收尾指令已注入，继续循环
                        }
                    }
                    else
                    {
                        // 收尾：先展示挂起的步骤开始卡片，再自动完成所有未标记完成的计划步骤
                        if (pendingStepStart > 0) { ShowStepStartCard(pendingStepStart); pendingStepStart = 0; }
                        AutoCompleteRemainingSteps();
                    }
                    // 提取并积累标签-符号关联（对话语义沉淀）
                    var assistantContent = resp.Content ?? "";
                    // 经验沉淀：解析收尾正文中的【经验沉淀】段（"标签1,标签2 | 经验"格式），
                    // 同时写入项目经验与全局经验库（跨项目共享、带技术栈/项目/前后端/数据库等标签，下次任务按项目命中优先注入）
                    foreach (var (text, tags) in ExtractLessons(assistantContent))
                    {
                        // 文件引用校验：经验若引用了不存在的文件路径（多因错位读码产生的幻觉），
                        // 打上"待验证"标记后再沉淀，防止错误经验永久注入后续所有会话
                        var checkedText = CheckFileRefsInLesson(cfg, text);
                        ProjectMapAuto.RecordLesson(cfg, checkedText);
                        ProjectMapAuto.RecordGlobalLesson(cfg, checkedText, tags);
                        Log($"[Lesson] 已沉淀经验: {(checkedText.Length > 60 ? checkedText[..60] + "…" : checkedText)}");
                    }
                    var accumulated = TagRefAccumulator.Extract(userText, assistantContent);
                    if (accumulated != null)
                    {
                        TagRefAccumulator.Append(cfg, accumulated);
                        Log($"[TagRef] 积累 {accumulated.Tags.Count} 个标签，{accumulated.Refs.Count} 个符号关联");
                    }
                    // 主动分词关联（后台异步入队，不阻塞收尾；不依赖模型自觉输出 JSON）
                    TagRefEnricher.Enqueue(userText, assistantContent);
                    // 词典自动学习：从积累库提取高频模式，闲时触发不阻塞
                    ZhEnMappingAutoLearner.LearnFromTask(cfg);
                    // 同上：避免空 assistant 消息污染跨任务历史（下个任务请求报 400）
                    // 非思考模式 ReasoningContent 为 null：写出 null 字段会让本地 llama.cpp 严格解析报 500
                    // （type must be string, but is null），仅在有值时携带
                    var finalMsg = new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = string.IsNullOrWhiteSpace(resp.Content) ? "（本轮无正文）" : resp.Content,
                    };
                    if (!string.IsNullOrEmpty(resp.ReasoningContent)) finalMsg["reasoning_content"] = resp.ReasoningContent;
                    history.Add(finalMsg);
                    break;
                }

                var tcs = new JsonArray();
                foreach (var tc in resp.ToolCalls)
                    tcs.Add(new JsonObject
                    {
                        ["id"] = tc.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = tc.Name, ["arguments"] = tc.Arguments },
                    });
                // reasoning_content 非思考模式为 null：仅在有值时携带（null 字段本地 llama.cpp 报 500）；
                // 发送边界 Jsonx.NormalizeMessages 另有统一剔除 null 字段的兜底
                var tcMsg = new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = intentMsg ?? resp.Content,
                    ["tool_calls"] = tcs,
                };
                if (!string.IsNullOrEmpty(resp.ReasoningContent)) tcMsg["reasoning_content"] = resp.ReasoningContent;
                history.Add(tcMsg);

                // 限制并发：最多同时执行 2 个工具，避免 UI 过载
                var semaphore = new SemaphoreSlim(2);
                bool toolPhaseTimedOut = false;
                // 工具执行阶段硬超时：防止单个/组合工具长时间卡住导致 UI 停留在“执行工具:...”异常停止
                var toolTimeout = TimeSpan.FromSeconds(Math.Max(cfg.CommandTimeout + 30, 90));
                using var toolCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                curToolCts = toolCts;   // 危险确认挂起跟随用（ConfirmDangerAsync 注册其令牌）
                var toolTasks = resp.ToolCalls.Select(async tc =>
                {
                    try
                    {
                        await semaphore.WaitAsync(toolCts.Token);
                    }
                    catch (OperationCanceledException) when (toolPhaseTimedOut && !ct.IsCancellationRequested)
                    {
                        Log($"[ToolResume] {tc.Name} 因工具阶段超时被框架中断，向模型追加继续指令");
                        return (tc.Id, "（系统提示）工具执行阶段被框架判定为超时/卡死，已中断。请从当前进度继续工作：若仍需调用工具则直接调用，若已接近完成请直接给出最终结论。不要重复已完成的步骤，也不要向用户确认需求。");
                    }
                    try
                    {
                        toolCts.Token.ThrowIfCancellationRequested();
                        var ui = new ToolUi
                        {
                            Title = tc.Name == "UpdateTodo" ? TodoTitle(tc) : TitleOf(tc),
                            IsStep = tc.Name == "UpdateTodo",
                            Round = round,
                            Icon = ToolIcon(tc.Name),
                            IconColor = ToolIconColor(tc.Name),
                            IconFontSize = ToolIconFontSize(tc.Name)
                        };
                        Emit(new UiEvent { Type = UiEventType.ToolStart, Tool = ui });

                        // 参数完整性护栏：模型流截断会产出半截工具参数 JSON（如 {"path":"），此前被 ExecuteAsync
                        // 静默降级成空参数执行，产生"错误：path 为空"式误导错误并让任务卡死。执行前校验不过：
                        // 不执行工具，直接回错让模型重发完整调用（协议上每个 tool_call 仍需一条 tool 结果）。
                        if (!IsValidArgsJson(tc.Arguments))
                        {
                            Log($"[Guard] {tc.Name} 工具参数 JSON 截断（流中断），不执行: {LLMClient.Trunc(tc.Arguments, 120)}");
                            ui.Warn = true;
                            ui.Status = "✗ 参数截断";
                            ui.Inner = "工具参数 JSON 不完整（模型流被截断），本次调用未执行。";
                            Emit(new UiEvent { Type = UiEventType.ToolUpdate, Tool = ui });
                            return (tc.Id, "错误：工具调用参数 JSON 不完整（模型流被截断），本次调用未执行。请重新发出完整的工具调用。");
                        }

                        // 修改/编译类工具动作 → 计划步骤关键词自动补勾（写文件时"修改"类步骤之前的计划项视为已完成，编译时"验证"类同理）
                        AutoCheckStepsByTool(tc);
                
                        Log($"[Tool] {tc.Name} 开始执行...");
                        var sw = Stopwatch.StartNew();
                        string result;
                        // 写前护栏：Write 覆盖已有文件而本会话未读过 → 拦截（不执行工具、不触发验证），提示先 Read 后重试
                        var guardBlock = GuardBlockWrite(tc, resp.ToolCalls);
                        if (guardBlock.Length > 0)
                            Log($"[Guard] 拦截 {tc.Name}：会话未读取过目标文件");
                        try
                        {
                            // using 在 Task.Run 之前建立：AsyncLocal 执行上下文随 Task.Run 流进工具线程，
                            // Write/Edit 排队等文件锁时经 CurrentToolLoop.Emit 上送锁等待事件（带会话归属）
                            using var toolLoopScope = ToolLoopScope();
                            // 同步工具在后台线程执行，避免阻塞；护栏命中时不执行
                            result = guardBlock.Length > 0
                                ? guardBlock
                                : await Task.Run(() => registry.ExecuteAsync(tc.Name, tc.Arguments, toolCts.Token), toolCts.Token);
                        }
                        catch (OperationCanceledException) when (toolPhaseTimedOut && !ct.IsCancellationRequested)
                        {
                            // 工具阶段整体超时：向模型返回“继续”提示，避免 UI 卡在“执行工具:...”后任务中止
                            result = "（系统提示）工具执行阶段被框架判定为超时/卡死，已中断。请从当前进度继续工作：若仍需调用工具则直接调用，若已接近完成请直接给出最终结论。不要重复已完成的步骤，也不要向用户确认需求。";
                            Log($"[ToolResume] {tc.Name} 因工具阶段超时被框架中断，向模型追加继续指令");
                        }
                        catch (Exception ex)
                        {
                            result = $"工具执行异常：{ex.Message}";
                            Log($"[Error] {tc.Name} 执行失败: {ex.Message}");
                        }
                        // 修复-验证循环：写代码文件后自动构建，结果附带回喂 Agent（system.ini [Verify] 可关；被护栏拦截不触发）
                        if (guardBlock.Length == 0 && SystemCfg.AutoVerify && (tc.Name == "Write" || tc.Name == "Edit"))
                        {
                            result = await Verify.AppendResult(cfg, result, tc.Arguments, ct);
                            // 证据链：验证结论落 changelog.jsonl（改了→验过），失败不影响主流程
                            var vbrief = Verify.Brief(result);
                            if (vbrief.Length > 0)
                                journal.Log(tc.Name, ExtractArg(tc.Arguments, "path"), "", "验证: " + vbrief);
                        }
                        // 会话级已修改文件追踪：供上下文压缩判定"已改文件的旧读取可作废"（仅成功写入计入）
                        if ((tc.Name == "Write" || tc.Name == "Edit") && !result.StartsWith("错误") && !result.StartsWith("拦截"))
                        {
                            var mp = ExtractArg(tc.Arguments, "path");
                            if (mp.Length > 0) lock (readSigCount) modifiedStamp[mp.Replace('/', '\\')] = ++tickSeq;
                            if (mp.Length > 0) fileRefs.MarkModified(mp);   // P6: 文件清单同步标注"已修改"（旧读取作废，压缩保底时参考）
                        }
                        // 重复读熔断：同一只读调用（工具+参数）反复执行且目标未变时，在结果附注提醒，打断"再确认一次"循环
                        if (ReadOnlyToolNames.Contains(tc.Name))
                        {
                            var sig = tc.Name + "|" + tc.Arguments;
                            var rp = ExtractArg(tc.Arguments, "path").Replace('/', '\\');
                            int n;
                            lock (readSigCount)
                            {
                                var prev = readSigCount.TryGetValue(sig, out var p) ? p : default;
                                // 上次读后目标文件又被修改过 → 旧计数作废，本次是新事实的读取
                                if (prev.Count > 0 && rp.Length > 0 &&
                                    modifiedStamp.TryGetValue(rp, out var st) && st > prev.Tick)
                                    prev = default;
                                n = prev.Count + 1;
                                readSigCount[sig] = (n, ++tickSeq);
                            }
                            if (!result.StartsWith("错误") && n == 2)
                                result += "\n[提示] 相同的读取已执行过一次且目标未变，如信息已充分请直接推进下一步。";
                            else if (!result.StartsWith("错误") && n >= 3)
                                result += $"\n[警告] 你已重复执行相同读取 {n} 次，信息应已充分；请停止重复确认，直接进入 Edit/Write 实施修改。";
                        }
                        // 先回读登记：Read/MapSlice 成功后把目标文件记入会话级已读集合（含区域读取；MapSlice 按 spec 各段解析文件）
                        if ((tc.Name == "Read" || tc.Name == "MapSlice") && !result.StartsWith("错误") && !result.StartsWith("拦截"))
                        {
                            foreach (var gp in GuardPathsOf(tc.Name, tc.Arguments))
                                if (gp.Length > 0) lock (readSigCount) sessionReadPaths.Add(gp);
                            // P6: 登记文件清单（文件+位置+意图）：压缩触发时询问 LLM 哪些文件必用，必用文件豁免压缩
                            fileRefs.NoteRead(tc.Arguments, curIntent, ++tickSeq);
                        }
                        sw.Stop();
                        Log($"[Tool] {tc.Name} 执行完成，耗时 {sw.Elapsed.TotalSeconds:F1}s，结果长度={result.Length}");
                        flowRunner?.RecordTool(tc.Name, result);   // Flow 模式：记录工具结果（硬锚点校验）
                
                        ui.Status = "✓ " + Math.Round(sw.Elapsed.TotalSeconds, 1) + "s";
                        // Bash 卡展开显示完整执行命令与结果：命令原文回填（模型参数里本就有）+ 结果上限放宽到 OutputMaxChars。
                        // Inner 仅供 UI 展示不进模型上下文（截断只为会话存档体积），与 Edit diff 卡同款放宽先例；
                        // 危险命令拦截分支在下方另行处理（安全设计：有意不展示被拦命令内容）
                        if (string.Equals(tc.Name, "Bash", StringComparison.OrdinalIgnoreCase))
                        {
                            var rawCmd = ExtractArg(tc.Arguments, "command");
                            ui.Inner = (rawCmd.Length > 0 ? "执行命令：" + rawCmd + "\n\n" : "")
                                + LLMClient.Trunc(result, SystemCfg.OutputMaxChars);
                        }
                        else
                            ui.Inner = LLMClient.Trunc(result, SystemCfg.CardMaxChars);
                        ui.Summary = SummarizeToolResult(tc.Name, result);
                        // Read 成功：回填目标文件相对路径与实际读取行段（结果首行 meta 含"返回 a-b"），
                        // 供 UI 打开该文件的读取快照并自动定位突显读取段（卡片保存后仍可在代码查看栏查看）
                        if (tc.Name == "Read" && !result.StartsWith("错误") && !result.StartsWith("拦截"))
                        {
                            var abs = NormPath(ExtractArg(tc.Arguments, "path"));
                            var root = NormPath(cfg.ProjectRoot);
                            ui.FilePath = (root.Length > 0 && abs.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
                                ? abs[(root.Length + 1)..].Replace('\\', '/')
                                : abs.Replace('\\', '/');
                            var mRng = Regex.Match(result, @"返回 (\d+)-(\d+)\]");
                            if (mRng.Success && int.TryParse(mRng.Groups[1].Value, out var s) && int.TryParse(mRng.Groups[2].Value, out var e))
                            {
                                ui.StartLine = s;
                                ui.EndLine = e;
                            }
                        }
                        // Edit 成功：卡片内容改用 diff 段（UI 按 diff 前缀红绿渲染），不受 CardMaxChars 预览截断
                        if (tc.Name == "Edit" && result.Contains(Differ.Marker))
                            ui.Inner = LLMClient.Trunc(Differ.ExtractSection(result), SystemCfg.OutputMaxChars);
                        // 工具名根本没注册（模型臆造/拼错，如把 Read 的参数名 "path" 当成工具名）：这是模型下一轮
                        // 拿着纠错反馈就能自己改好的误用，不算失败——不画红卡、不沉淀伪经验，只留一条中性提示；
                        // 完整纠错文本（含可用工具清单）照常回给模型，它下一轮改用正确工具即可
                        if (!registry.IsRegistered(tc.Name))
                        {
                            ui.Warn = false;
                            ui.Open = false;
                            ui.Status = "⚠ 工具名无效，已提示模型纠正";
                            ui.Summary = "未知工具 " + tc.Name + "：已回传可用工具清单，模型将改用正确工具重试";
                            ui.Inner = "模型调用了不存在的工具「" + tc.Name + "」（多为把参数名当成工具名，或工具名拼错）。"
                                     + "框架已把可用工具清单回传给它，下一轮会改用正确工具重新调用，本步骤不算失败、不影响任务继续。";
                        }
                        else if (result.StartsWith("错误") || result.StartsWith("拦截") || result.StartsWith("超时") || result.StartsWith("工具执行异常"))
                        {
                            ui.Warn = true;
                            ui.Status = "✗ " + (result.StartsWith("超时") ? "超时" : "失败");
                            // 失败模式沉淀为项目经验（去路径/行号等易变细节），下次任务注入提示词避免重犯；
                            // 工具名白名单校验：模型臆造/拼错工具名（未注册→"未知工具"）属幻觉误用，不是可复用经验，不沉淀
                            if (registry.IsRegistered(tc.Name))
                                ProjectMapAuto.RecordLesson(cfg, "工具失败(" + tc.Name + "): " + NormalizeError(result));
                        }
                        // 危险命令被拦截：向 UI 发送安全警报，确保用户能在会话中看到红色提示。
                        // 判定仅限命令类工具（Bash）：拦截文本只由 RunCmd 产生，但 Read 等工具返回的文件内容
                        // 可能恰好包含"操作已取消/已被统一审查拒绝"字样，若按内容子串判断会误伤大文件分段读取
                        if (string.Equals(tc.Name, "Bash", StringComparison.OrdinalIgnoreCase)
                            && result.StartsWith("拦截：危险命令"))
                        {
                            var rawCmd = ExtractArg(tc.Arguments, "command");
                            var (_, pattern) = ExtractDangerInfo(result);
                            // 工具卡收起且不展示命令内容：详情放会话提示区，避免展开区暴露修改内容
                            ui.Open = false;
                            ui.Inner = "危险命令已拦截，请查看会话安全提示";
                            ui.Summary = "拦截：" + pattern.Trim();
                            // 操作已取消（用户刚点了取消/确认超时）不再发警报：原提示消息已就地更新，避免重复
                            if (!result.Contains("操作已取消"))
                                Emit(new UiEvent
                                {
                                    Type = UiEventType.SecurityAlert,
                                    Tool = ui,
                                    Alert = new SecurityAlert
                                    {
                                        Level = "Block",
                                        Command = rawCmd,
                                        Pattern = pattern,
                                        Message = result,
                                        Intent = curIntent,
                                    }
                                });
                        }
                        // 待办清单：模型经 UpdateTodo 显式管理（create/update/done_all 原样转发事件）
                        if (tc.Name == "UpdateTodo")
                        {
                            var todo = ParseTodoArgs(tc.Arguments);
                            if (todo != null)
                            {
                                if (todo.Action == "update" && todo.Done && todo.Index >= 1)
                                {
                                    // 模型显式标记步骤完成：记录并延迟到下一轮展示"计划步骤(N+1)"卡片
                                    todoDoneSteps.Add(todo.Index);
                                    if (todo.Index < (todoSteps?.Count ?? 0))
                                        pendingStepStart = todo.Index + 1;
                                }
                                else if (todo.Action == "done_all" && todoSteps != null)
                                {
                                    for (int i = 1; i <= todoSteps.Count; i++) todoDoneSteps.Add(i);
                                }
                                // create 动作：发送 create 事件创建计划卡，同时发送 step_start 事件展示步骤1开始
                                Emit(new UiEvent { Type = UiEventType.Todo, Todo = todo });
                            }
                        }
                        Emit(new UiEvent { Type = UiEventType.ToolUpdate, Tool = ui });
                
                        return (tc.Id, result);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }).ToList();

                // 工具执行阶段总超时检测：避免 UI 长期停留在“执行工具:...”异常停止
                var allToolsTask = Task.WhenAll(toolTasks);
                var timeoutTask = Task.Delay(toolTimeout, toolCts.Token);
                var finishedTask = await Task.WhenAny(allToolsTask, timeoutTask);
                List<(string Id, string Result)> results;
                if (finishedTask == timeoutTask)
                {
                    toolPhaseTimedOut = true;
                    Log($"[ToolPhaseTimeout] 工具执行阶段超过 {toolTimeout.TotalSeconds:F0}s，框架强制中断并追发继续");
                    toolCts.Cancel();
                    // 给取消传播最多 5s 缓冲；宽限后仍不结束的任务按“卡死”合成 tool 结果——
                    // 绝不无限等待（此前的无条件 WhenAll 会被死不响应取消的工具永久挂住，loop 停在“执行工具:...”不出事件）
                    try { await Task.WhenAny(allToolsTask, Task.Delay(TimeSpan.FromSeconds(5), ct)); } catch { }
                    results = new List<(string, string)>(resp.ToolCalls.Count);
                    foreach (var (tc, task) in resp.ToolCalls.Zip(toolTasks))
                    {
                        if (task.Status == TaskStatus.RanToCompletion)
                            results.Add(await task);   // 正常完成：取返回值（不会抛）
                        else if (task.IsCompleted)
                            await task;                // 已结束但取消/异常（如用户点停止）：原样抛出交外层收尾
                        else
                        {
                            Log($"[ToolKill] {tc.Name} 宽限期内仍未响应取消，按卡死处理并合成中断结果");
                            results.Add((tc.Id, "（系统提示）工具执行阶段被框架判定为超时/卡死，已中断。请从当前进度继续工作：若仍需调用工具则直接调用，若已接近完成请直接给出最终结论。不要重复已完成的步骤，也不要向用户确认需求。"));
                        }
                    }
                }
                else
                    results = (await allToolsTask).ToList();
                foreach (var (id, result) in results)
                {
                    history.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = result });
                }
                curToolCts = null;   // 工具阶段结束（在 using 释放前置空，防 ConfirmDangerAsync 注册到已释放源）
            }
            if (round >= cfg.MaxRounds)
            {
                Log($"[Stop] 达到最大轮次 {cfg.MaxRounds}");
                LastFailReason = $"达到最大轮次 {cfg.MaxRounds}";
                Emit(new UiEvent { Type = UiEventType.Failed, Reasoning = LastFailReason, TotalTokens = totalTokens });
                success = false;
            }
            Emit(new UiEvent { Type = UiEventType.Finished, Round = round, TotalTokens = totalTokens });
        }
        catch (OperationCanceledException)
        {
            Log("[Cancel] 任务已取消");
            LastFailReason = "任务已取消";
            success = false;
            Emit(new UiEvent { Type = UiEventType.Failed, Reasoning = LastFailReason, TotalTokens = totalTokens });
            Emit(new UiEvent { Type = UiEventType.Finished, Round = round, TotalTokens = totalTokens });
        }
        catch (LlmException ex)
        {
            Log($"[Error] LLM异常: {ex.Message}");
            LastFailReason = "模型异常：" + ex.Message;
            success = false;
            Emit(new UiEvent { Type = UiEventType.Failed, Reasoning = LastFailReason, TotalTokens = totalTokens });
            Emit(new UiEvent { Type = UiEventType.Finished, Round = round, TotalTokens = totalTokens });
        }
        catch (Exception ex)
        {
            Log($"[Error] Agent循环异常: {ex}\n{ex.StackTrace}");
            LastFailReason = "Agent循环异常：" + ex.Message;
            success = false;
            Emit(new UiEvent { Type = UiEventType.Failed, Reasoning = LastFailReason, TotalTokens = totalTokens });
            Emit(new UiEvent { Type = UiEventType.Finished, Round = round, TotalTokens = totalTokens });
        }
        finally
        {
            LastTaskTokens = totalTokens;   // 服务宿主结果汇总（UI 走事件流实时累计）
            LastRunSuccess = success;
            RoundCount = round;             // 供 TaskRunner / 外部宿主记录实际轮次
            if (success) LastFailReason = "";
            RunHooks(success, round, totalTokens, userText);
        }
    }

    /* ---------- 上下文压缩：主题式压缩（压力驱动+任务边界摘要+无关轮剔除+token兜底）；ThemeCompress=0 回退旧裁剪 ---------- */

    /// <summary>P6: 压缩前询问 LLM"文件清单上哪些文件对未完成工作是必须的"（必用文件豁免压缩判定②③④）。
    /// 触发条件：EssentialFiles 开关开 + 软阈值确实触发（窗口富余不查）+ 清单 ≥2 个文件 + 清单指纹变化（缓存）。
    /// 10s 超时/失败返回 null：压缩器退回纯确定性行为（不劣化）。空集=模型明确回答"无"，允许更激进压缩。</summary>
    async Task<HashSet<string>?> QueryEssentialFilesAsync(CompressCtx ctx)
    {
        if (cfg.GetForProvider("EssentialFiles", "1") != "1") return null;
        // 软阈值（与 Compress 内同公式：自适应调参+usage校准）：窗口富余时不压缩也不查询
        var cal = ctx.UsageCalib;
        if (cal < 1.0) cal = 1.0; else if (cal > 3.0) cal = 3.0;
        var soft = ctx.SoftRatio;
        if (ctx.TotalRounds > 20) soft = Math.Min(soft, 0.40);
        else if (ctx.TotalRounds < 5) soft = Math.Max(soft, 0.60);
        if (ContextCompressor.EstimateTokens(history) <= (int)(ctx.MaxTokens * soft / cal)) return null;
        if (fileRefs.Count < 2) return null;
        var fp = fileRefs.Fingerprint();
        if (fp == lastEssentialFp) return null;      // 清单未变：沿用上一轮查询结果（缓存）
        var refs = fileRefs.Snapshot();
        var prompt = FileRefTracker.BuildPrompt(refs, ctx.TaskText, ctx.TodoSteps, ctx.TodoDoneSteps);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var messages = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = prompt } };
            var resp = await client.ChatAsync(messages, new JsonArray(), cts.Token);
            var set = FileRefTracker.ParseResponse(resp.Content ?? "", refs);
            if (set == null) { Log("[Compress] 必用文件询问无有效结果，退回确定性压缩"); return null; }
            lastEssentialFp = fp;
            if (set.Count == 0) { Log("[Compress] 必用文件：无（允许更激进压缩）"); return set; }
            Log("[Compress] 必用文件(" + set.Count + "/" + refs.Count + ")：" + string.Join("、", set.Select(p => p.Split('\\')[^1])));
            return set;
        }
        catch { return null; }   // 超时/失败：不劣化，压缩器退回确定性判定
    }

    /// <summary>从修改时刻表导出已修改文件集合（供压缩判定"已改文件的旧读取可作废"）</summary>
    HashSet<string> ModifiedFileSet()
    {
        lock (readSigCount) return new HashSet<string>(modifiedStamp.Keys, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>把当前任务至今的 assistant 正文汇成探索摘要（打回指令附带）：
    /// 打回会形成新任务边界，旧轮将被压缩摘要化，附上探索结论可避免模型建完清单后从零重新探索</summary>
    string ExploreDigest()
    {
        var start = 0;
        for (var i = history.Count - 1; i >= 0; i--)
            if (history[i] is JsonObject m && (m["role"]?.GetValue<string>() ?? "") == "user") { start = i; break; }
        var parts = new List<string>();
        for (var i = start + 1; i < history.Count; i++)
        {
            if (history[i] is not JsonObject m || (m["role"]?.GetValue<string>() ?? "") != "assistant") continue;
            var c = (m["content"]?.GetValue<string>() ?? "").Trim();
            if (c.Length == 0 || c.StartsWith("（")) continue;   // 跳过"（本轮无正文）"等占位正文
            parts.Add(LLMClient.Trunc(c.Replace("\n", " "), 150));
        }
        if (parts.Count == 0) return "";
        return "\n[此前探索结论摘要（已确认的信息不必重新探索）]\n- " + string.Join("\n- ", parts.Take(12));
    }

    /// <summary>每轮模型调用前压缩 history：优先主题式压缩器；开关关闭或压缩异常时回退旧版裁剪</summary>
    async Task CompressHistoryAsync()
    {
        if (cfg.Get("Agent", "ThemeCompress", "1") != "1") { TrimHistoryLegacy(); return; }
        try
        {
            // 压缩可见化：压缩可能含 LLM 必用文件询问/LLM 压缩兜底，耗时数秒至十几秒且期间无其它 UI 事件，
            // 发状态信号让等待提示条切"正在整理上下文"（压缩结束由 WaitingModel 接替切"正在思考"），
            // 避免工具轮完成后界面长时间静止被误判"卡死/停止"（快速路径事件与 WaitingModel 同批合并，几乎不可见）
            Emit(new UiEvent { Type = UiEventType.CompressStart });
            var ctx = new CompressCtx
            {
                TaskText = curTaskText,
                TodoSteps = todoSteps,
                TodoDoneSteps = todoDoneSteps,
                ModifiedFiles = ModifiedFileSet(),
                MaxTokens = cfg.MaxTokens,
                UsageCalib = lastRequestEstimate > 1000 && lastUsagePrompt > 0 ? (double)lastUsagePrompt / lastRequestEstimate : 1.0,
                ContinueReply = curTaskIsShortMsg,  // 承接上文短消息：不切任务边界，保留上轮上下文
                RoundBase = roundSnaps.Count,       // 已归档回合数：摘要"轮N"从 RoundBase+1 起，跨压缩连续
                OnArchive = (no, msgs) =>
                {
                    lock (roundSnaps)
                    {
                        // 同轮号不重复归档（同一轮不会被两次压缩），超上限丢最旧
                        if (roundSnaps.Count == 0 || roundSnaps[^1].No != no)
                        {
                            var ut = msgs.Count > 0 && msgs[0] is JsonObject u && u["content"]?.GetValue<string>() is { } uc ? uc : "";
                            roundSnaps.Add(new RoundSnap { No = no, UserText = ut, Msgs = msgs });
                            Log($"[Archive] 归档回合 {no}（{msgs.Count} 条消息）");
                        }
                        while (roundSnaps.Count > RoundSnapMax) roundSnaps.RemoveAt(0);
                    }
                },
                // 压缩参数按供应商两套配置：[供应商] 节（如 [Local]）可覆盖，未配置回退 [Agent] 全局默认
                KeepRecentRounds = cfg.GetIntForProvider("KeepRecentRounds", 3),
                TopicOverlapKeep = cfg.GetIntForProvider("TopicOverlapKeepPct", 30) / 100.0,
                HardRatio = cfg.GetIntForProvider("CompressHardPct", 85) / 100.0,
                SoftRatio = cfg.GetIntForProvider("CompressSoftPct", 50) / 100.0,
                ResultBudget = cfg.GetIntForProvider("ResultBudget", 2000),
                BudgetKeepRounds = cfg.GetIntForProvider("BudgetKeepRounds", 1),
                TotalRounds = history.Count(h => h is JsonObject m && (m["role"]?.GetValue<string>() ?? "") == "assistant"),
            };
            ctx.EssentialFiles = await QueryEssentialFilesAsync(ctx);   // P6: 软阈值触发后询问 LLM 必用文件（null=未询问/失败，压缩器退回确定性行为）
            var before = ContextCompressor.EstimateTokens(history);
            var result = compressor.Compress(history, ctx);
            if (compressor.Changed)
            {
                history.Clear();
                while (result.Count > 0) { var it = result[0]!; result.RemoveAt(0); history.Add(it); }
            }
            // P2: LLM 压缩兜底——当 FinalTrim 触发时，尝试用 LLM 生成结构化摘要替换硬裁剪摘要
            // LlmCompress 按供应商解析：本地推理慢，[Local] LlmCompress=0 关闭兜底走确定性裁剪
            if (compressor.LastReport.Contains("兜底裁剪") && cfg.GetForProvider("LlmCompress", "1") == "1")
            {
                try
                {
                    var llmSummary = await TryLlmCompressAsync(history, curTaskText);
                    if (llmSummary.Length > 0)
                    {
                        // 找到 [早期轮次兜底压缩] 的 system 消息并替换
                        for (var i = 0; i < history.Count; i++)
                        {
                            if (history[i] is JsonObject m && (m["role"]?.GetValue<string>() ?? "") == "system"
                                && (m["content"]?.GetValue<string>() ?? "").StartsWith("[早期轮次兜底压缩]"))
                            {
                                history[i] = new JsonObject { ["role"] = "system", ["content"] = llmSummary };
                                Log($"[Compress] LLM压缩兜底成功，替换硬裁剪摘要（{llmSummary.Length}字）");
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex) { Log($"[Warn] LLM压缩兜底失败，保留硬裁剪: {ex.Message}"); }
            }
            var after = ContextCompressor.EstimateTokens(history);
            Log($"[Compress] {compressor.LastReport}；token {before}→{after}" + (ctx.UsageCalib > 1.05 ? $"（usage校准×{ctx.UsageCalib:0.00}）" : ""));
        }
        catch (Exception ex)
        {
            Log($"[Error] 主题压缩失败，回退旧裁剪: {ex.Message}");
            TrimHistoryLegacy();
        }
    }

    /// <summary>旧版裁剪（超 MaxTokens 时保 system + 尾部 5 条，中间压 200 字符摘要）；ThemeCompress=0 回退路径</summary>
    void TrimHistoryLegacy()
    {
        try
        {
            var threshold = cfg.MaxTokens;
            var total = EstimateTokens(history);
            if (total <= threshold) return;

            // 保留 system 消息（索引 0）和最近 2 轮（user + assistant/tool）
            var keep = 1; // system
            for (int i = history.Count - 1; i >= 0 && keep < 5; i--)
                keep++;

            if (history.Count <= keep) return;

            // 把中间旧消息压缩成一条摘要
            var sb = new StringBuilder("[历史对话摘要] ");
            for (int i = 1; i < history.Count - keep + 1; i++)
            {
                var msg = history[i] as JsonObject;
                var role = msg?["role"]?.GetValue<string>() ?? "";
                var content = msg?["content"]?.GetValue<string>() ?? "";
                sb.Append($"{role}: {LLMClient.Trunc(content, 200)}; ");
            }

            // 构建新的 history 数组（全新节点，无父节点冲突）
            var trimmed = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = ((history[0] as JsonObject)?["content"]?.GetValue<string>() ?? "") },
                new JsonObject { ["role"] = "system", ["content"] = sb.ToString() }
            };
            for (int i = history.Count - keep + 1; i < history.Count; i++)
            {
                var msg = history[i] as JsonObject;
                if (msg != null)
                {
                    // 深拷贝完整消息，保留 tool_calls / tool_call_id 等全部字段
                    trimmed.Add(CloneJson(msg));
                }
            }

            // 替换 history：必须从 trimmed 移除后再添加到 history，避免父节点冲突
            history.Clear();
            while (trimmed.Count > 0)
            {
                var item = trimmed[0]!;
                trimmed.RemoveAt(0);
                history.Add(item);
            }
        }
        catch (Exception ex)
        {
            Log($"[Error] 裁剪历史失败: {ex.Message}");
        }
    }

    static JsonObject CloneJson(JsonNode source)
    {
        // 深拷贝 JsonNode，避免父节点冲突
        return JsonObject.Parse(source.ToJsonString()) as JsonObject ?? new JsonObject();
    }

    /// <summary>停下要需求意图判定：模型纯文本收尾且内容明确在"向用户要需求/要求确认"（如"我没拿到具体需求，无法确定要做什么"），
    /// 而非正常结论。命中时框架自动找回上轮原文补发重试，替代用户手动复制粘贴补上下文。
    /// 判定会先排除明显已完成/成功的简短结论，避免正常结果触发无意义重试。</summary>
    /// <summary>意图宣布未行动判定：模型纯文本收尾（无 tool_calls、finish_reason=stop）却只是口头宣布下一步动作
    /// （"现在建清单…然后继续改"、"接下来我读一下 X 文件"这类），而非真正执行工具，也不是向用户提问要需求。
    /// 典型成因：框架打回要求补建清单后，模型复述了系统提示内容但没实际调用 UpdateTodo，
    /// 被当成最终结论 → 任务半途而废。命中时框架追发"请立即实际调用工具执行"指令。
    /// 只匹配前瞻动作词（现在/接下来/我先/下一步 + 建清单/继续/执行/读/改…），
    /// 并排除已完成态（已成功/已完成/已保存…）与正常收尾（完成总结/经验沉淀/结论），避免误杀正常结论。</summary>
    static bool LooksLikeIntentAnnouncement(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        var c = content.Trim();
        if (c.Length > 160) return false;   // 正常结论通常较长；只有短"预告"才触发
        // 排除已完成态/正常收尾：这类文本虽短但是结论，不是预告
        var donePhrases = new[] { "已成功", "已完成", "已保存", "已更新", "已生成", "已发送", "已执行",
            "已验证", "已结束", "已修复", "完成总结", "经验沉淀", "验证结果", "遗留问题", "### ", "## " };
        if (donePhrases.Any(p => c.Contains(p))) return false;
        // 用户自定义关键词优先匹配：system.ini [Agent] IntentRetryKeywords=逗号分隔正则片段
        foreach (var pat in SystemCfg.IntentRetryKeywords)
        {
            try
            {
                if (pat.Length > 0 && Regex.IsMatch(c, pat, RegexOptions.None, TimeSpan.FromMilliseconds(200)))
                    return true;
            }
            catch
            {
                // 用户写的正则错误不能导致主循环崩溃；静默忽略，后续继续用内置规则
                // （system.ini 配置期问题，启动时已在读取位置记录；主循环每次判定不必刷屏）
            }
        }
        // 内置兜底：前瞻动作预告词（现在/接下来/我先/下一步 + 建清单/继续/执行/读取/修改/调用…）
        return Regex.IsMatch(c,
            "(?:现在|接下来|下一步|下面|我先|先).{0,6}(?:建(?:个|待办)?清单|创建.*清单|继续(?:改|执行|做|修改)|执行|读取|读一下|读文件|修改|改一下|调用|开始(?:执行|修改|改))" +
            "|(?:建|创建|补建)(?:个|待办)?清单.{0,12}继续");   // "建清单…继续" 类（如"现在建清单，然后继续改"）
    }

    static bool LooksLikeAskForInput(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        var c = content.Trim();
        if (c.Length > 400) return false;   // 正常结论通常较长；只有简短"确认请求"才触发找回
        // 先排除常见的已完成/成功类简短结论，避免误判
        var donePhrases = new[] { "成功", "完成", "已发送", "已执行", "已生成", "已保存", "已更新", "已验证", "已结束", "任务", "结论" };
        if (donePhrases.Any(p => c.Contains(p)) &&
            (c.Contains("已") || c.Contains("成功") || c.Contains("完成") || c.Contains("结束")))
            return false;
        return Regex.IsMatch(c,
            "请(?:先)?(?:提供|补充|告知|告诉|确认|说明|给出).{0,12}|" +
            "需要(?:你|您|我)?(?:提供|补充|确认|告诉我|了解|知道)|" +
            "需要先.{0,8}确认|" +   // "需要先停下来确认一下" 类
            "无法(?:确定|判断|继续|执行).{0,12}|" +
            "没有拿到|未收到|拿不到|缺少(?:需求|描述|信息|任务相)|" +
            "不清楚(?:要|该|怎么)|本轮消息里.*(?:为空|是空的)|正文只有");
    }

    /// <summary>自动任务结论清理：删除 <autotask-result></autotask-result> 结束标记（模型偶发写成
    /// ```autotask-result 代码围栏并把整段结论包进去，导致 Markdown 渲染器按代码块显示、格式化全失效），
    /// 还原内部 Markdown，保证结论按原格式上屏与入历史。</summary>
    static string CleanAutoTaskConclusion(string content)
    {
        var s = content.Replace("\r\n", "\n");
        // 1) 拆掉错误围栏：整段被单个 ```autotask-result … ``` 包裹时还原内部内容
        var lines = s.Split('\n');
        var first = -1; var last = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (first < 0 && t.StartsWith("```") && t.Substring(3).Trim().Equals("autotask-result", StringComparison.OrdinalIgnoreCase)) first = i;
            else if (first >= 0 && t == "```") last = i;   // 只认纯结束围栏，避免误伤结论内正常代码块
        }
        if (first >= 0 && last > first)
            s = string.Join("\n", lines[(first + 1)..last]);
        // 2) 删除结束标记行（含变体：独占一行的 <autotask-result>、</autotask-result> 及同行开闭组合，可多个）
        s = Regex.Replace(s, @"(?m)^\s*(?:<\s*/?\s*autotask-result\s*>\s*)+$", "");
        return s.Trim();
    }

    /// <summary>RecallHistory 工具实现：按回合号/最近 N 回合取回历史（压缩器归档的旧回合），供模型主动按需补上下文。
    /// mode: talk=仅用户原话+GAIRR结论（默认最省）；brief=过程关键信息+结论（工具只留名与结果摘要，无卡片全文/长思维链）；full=完整执行过程+结论（量大，单轮深挖用）</summary>
    string RecallHistoryText(JsonObject a)
    {
        var mode = (a["mode"]?.GetValue<string>()?.Trim().ToLowerInvariant()) switch
        {
            "brief" or "key" or "要点" or "精简" => 1,
            "full" or "detail" or "过程" or "完整" => 2,
            _ => 0,   // 默认 talk（最省），防过程内容占爆预算
        };
        string body;
        lock (roundSnaps)
        {
            if (roundSnaps.Count == 0)
                return "无历史回合可取回（会话暂无已归档回合）。";
            var picks = new List<int>();
            if (a["rounds"] is JsonArray arr)
                foreach (var r in arr)
                    if (r?.GetValue<int>() is { } n && n > 0) picks.Add(n);
            if (picks.Count == 0 && a["last"]?.GetValue<int>() is { } ln && ln > 0)
                for (var i = Math.Max(0, roundSnaps.Count - ln); i < roundSnaps.Count; i++) picks.Add(roundSnaps[i].No);
            if (picks.Count == 0)
                return "参数需指定 rounds=[回合号列表] 或 last=最近N回合；当前可用轮号：" + string.Join(",", roundSnaps.Select(s => s.No));
            var sb = new StringBuilder();
            foreach (var p in picks.Distinct())
            {
                var snap = roundSnaps.Find(s => s.No == p);
                if (snap == null)
                {
                    sb.Append("（轮").Append(p).Append(" 不存在，可用：").Append(string.Join(",", roundSnaps.Select(s => s.No))).Append("）\n");
                    continue;
                }
                sb.Append("==== 轮").Append(snap.No).Append("（")
                  .Append(mode == 0 ? "对话结论" : mode == 1 ? "过程要点" : "完整过程")
                  .Append("，用户: ").Append(LLMClient.Trunc(snap.UserText, 60)).Append("）====\n");
                AppendRoundBody(sb, snap, mode);
                if (sb.Length > 24000) { sb.Append("（内容过长已截断，请缩小取回范围）\n"); break; }
            }
            body = sb.ToString();
        }
        Log($"[RecallHistory] 取回回合(mode={mode})，全文 {body.Length} 字符");
        return body;
    }

    /// <summary>按模式把单个回合正文写入 sb：talk 保留所有用户原话+段末纯文本结论；brief 增加过程说明/工具名/结果摘要；full 再展开参数与思维链</summary>
    static void AppendRoundBody(StringBuilder sb, RoundSnap snap, int mode)
    {
        // 结论 = 段内最后一条"assistant 纯文本"（无 tool_calls 且 content 非空）
        int concl = -1;
        for (var i = snap.Msgs.Count - 1; i >= 0; i--)
            if (snap.Msgs[i]["role"]?.GetValue<string>() == "assistant"
                && !HasToolCalls(snap.Msgs[i])
                && SnapContent(snap.Msgs[i]["content"]) is { Length: > 0 }) { concl = i; break; }
        var toolName = new Dictionary<string, string>();   // tool_call_id → 工具名
        for (var i = 0; i < snap.Msgs.Count; i++)
        {
            var m = snap.Msgs[i];
            var role = m["role"]?.GetValue<string>() ?? "";
            var content = SnapContent(m["content"]) ?? "";
            if (m["tool_calls"] is JsonArray tcs)           // 记录 id→工具名，供后续 tool 结果命名
                foreach (var t in tcs.OfType<JsonObject>())
                    if (t["function"] is JsonObject fn
                        && t["id"]?.GetValue<string>() is { Length: > 0 } tid
                        && fn["name"]?.GetValue<string>() is { Length: > 0 } tname)
                        toolName[tid] = tname;
            switch (role)
            {
                case "user":
                    if (content.Length > 0) sb.Append("用户: ").Append(LLMClient.Trunc(content, 4000)).Append('\n');
                    break;
                case "assistant":
                    if (HasToolCalls(m))                    // 带工具调用的执行轮
                    {
                        if (mode == 0) break;               // talk 只看对话，跳过过程
                        if (content.Length > 0)
                            sb.Append(mode == 1 ? "说明: " : "过程: ").Append(LLMClient.Trunc(content, mode == 1 ? 600 : 3000)).Append('\n');
                        var calls = new List<string>();
                        foreach (var t in (m["tool_calls"] as JsonArray)!.OfType<JsonObject>())
                            if (t["function"] is JsonObject fn)
                            {
                                var nm = fn["name"]?.GetValue<string>() ?? "?";
                                var args = fn["arguments"]?.GetValue<string>() ?? "";
                                var arg1 = args.Split('\n')[0].Trim();
                                calls.Add(arg1.Length > 0 ? $"{nm}({LLMClient.Trunc(arg1, mode == 1 ? 60 : 200)})" : $"{nm}()");
                            }
                        sb.Append("调工具: ").Append(string.Join("、", calls)).Append('\n');
                    }
                    else if (i == concl)                    // 段末纯文本 = 最终结论
                        sb.Append("结论: ").Append(LLMClient.Trunc(content, mode == 0 ? 3000 : 6000)).Append('\n');
                    else if (mode > 0 && content.Length > 0) // 段内过程性说明
                        sb.Append("过程: ").Append(LLMClient.Trunc(content, mode == 1 ? 600 : 3000)).Append('\n');
                    if (mode == 2 && m["reasoning_content"]?.GetValue<string>() is { Length: > 0 } rc)
                        sb.Append("┄ 思维链: ").Append(LLMClient.Trunc(rc, 1500)).Append('\n');
                    break;
                case "tool":                                // 工具返回：brief 摘要、full 展开
                    if (mode == 0) break;
                    var tn = m["tool_call_id"]?.GetValue<string>() is { Length: > 0 } cid && toolName.TryGetValue(cid, out var t0) ? t0 : "工具";
                    sb.Append(mode == 1 ? $"结果({tn}): " : $"工具返回({tn}): ").Append(LLMClient.Trunc(content, mode == 1 ? 400 : 6000)).Append('\n');
                    break;
                case "system":                              // 压缩器摘要等，一般不进归档；full 才简短带出
                    if (mode == 2 && content.Length > 0)
                        sb.Append("(摘要: ").Append(LLMClient.Trunc(content, 400)).Append(")\n");
                    break;
            }
        }
    }

    /// <summary>该消息是否携带工具调用（tool_calls 字段存在且非空）</summary>
    static bool HasToolCalls(JsonObject m) => m["tool_calls"] is JsonArray tcs && tcs.Count > 0;

    /// <summary>判断当前是否为该会话的第一条用户消息</summary>
    bool IsFirstUserMessage()
    {
        for (int i = 0; i < history.Count; i++)
        {
            if (history[i] is JsonObject msg && msg["role"]?.GetValue<string>() == "user")
                return false;
        }
        return true;
    }

    /// <summary>异步生成会话标题摘要（≤15 字），失败返回空字符串</summary>
    async Task<string> GenerateTitleSummaryAsync(string userText, CancellationToken ct)
    {
        try
        {
            var prompt = $"请用不超过15个汉字的简短标题概括以下用户问题，不要加标点、不要解释：\n{userText}";
            var messages = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = prompt } };
            var resp = await client.ChatAsync(messages, new JsonArray(), ct);
            var summary = resp.Content?.Trim() ?? "";
            // 清理标点并限制长度
            summary = Regex.Replace(summary, @"[\p{P}]", "");
            summary = summary.Replace("\n", " ").Replace("\r", " ").Trim();
            if (summary.Length > 15)
                summary = summary[..15];
            return summary;
        }
        catch (Exception ex)
        {
            Log($"[Warn] 生成会话摘要失败: {ex.Message}");
            return "";
        }
    }

    /// <summary>P2: LLM 压缩兜底——把早期轮次内容发给模型生成 ≤800 字结构化摘要（任务/已做/结论/待办/约束）。
    /// 超时 10s 或失败返回空字符串，调用方保留硬裁剪结果。</summary>
    async Task<string> TryLlmCompressAsync(JsonArray history, string taskText)
    {
        // 收集被压缩的早期轮次内容（system 摘要消息之后的 user/assistant 文本）
        var sb = new StringBuilder();
        foreach (var item in history)
        {
            if (item is not JsonObject m) continue;
            var role = m["role"]?.GetValue<string>() ?? "";
            var content = m["content"]?.GetValue<string>() ?? "";
            if (role == "system" && content.StartsWith("[早期轮次兜底压缩]"))
                sb.Append(content).Append('\n');
            else if (role == "user" || role == "assistant")
                sb.Append(role).Append(": ").Append(LLMClient.Trunc(content.Replace("\n", " "), 500)).Append('\n');
        }
        if (sb.Length == 0) return "";

        var prompt = $"请将以下对话历史压缩为不超过800字的结构化摘要，格式固定为：\n" +
            $"## 任务\n{{用户原始需求}}\n## 已完成\n{{已做的修改/操作}}\n## 关键结论\n{{重要发现/决策}}\n## 待办\n{{未完成事项}}\n## 约束\n{{用户约定/注意事项}}\n\n" +
            $"当前任务：{LLMClient.Trunc(taskText, 300)}\n\n历史内容：\n{LLMClient.Trunc(sb.ToString(), 6000)}";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var messages = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = prompt } };
            var resp = await client.ChatAsync(messages, new JsonArray(), cts.Token);
            var summary = resp.Content?.Trim() ?? "";
            if (summary.Length > 800) summary = summary[..800];
            return summary.Length > 50 ? "[LLM压缩摘要] " + summary : "";
        }
        catch { return ""; }
    }

    static int EstimateTokens(JsonArray arr)
    {
        // 简易估算：1 token ≈ 4 字符（中英混合近似）
        int chars = 0;
        foreach (var item in arr)
        {
            if (item is JsonObject msg)
            {
                chars += (msg["content"]?.GetValue<string>() ?? "").Length;
                chars += (msg["role"]?.GetValue<string>() ?? "").Length;
            }
        }
        return chars / 4;
    }

    /* ---------- [Hooks] 框架级固定动作：不经过模型判断，任务结束自动跑 ---------- */
    void RunHooks(bool success, int rounds, int tokens, string task)
    {
        try
        {
            var onOk = cfg.Get("Hooks", "TestScript", "");
            var onFin = cfg.Get("Hooks", "RunScript", "");
            var autoDing = cfg.Get("Hooks", "AutoDingTalk", "0") == "1";
            if (onOk.Length == 0 && onFin.Length == 0 && !autoDing) return;

            var safeTask = task.Replace('"', '\'').Replace('\r', ' ').Replace('\n', ' ');
            // 钉钉通知"任务"字段用会话标题（会话首条消息时缓存），无标题时截断原文兜底，避免推送整段用户消息
            var title = string.IsNullOrWhiteSpace(sessionTitle)
                ? (task.Length > 50 ? task[..50] + "…" : task)
                : sessionTitle;
            var dingTask = title.Replace('"', '\'').Replace('\r', ' ').Replace('\n', ' ');
            string Fill(string s) => s.Replace("{task}", safeTask)
                .Replace("{status}", success ? "成功" : "失败")
                .Replace("{rounds}", rounds.ToString())
                .Replace("{tokens}", tokens.ToString());
            _ = Task.Run(async () =>
            {
                try
                {
                    if (success && onOk.Length > 0) await Phase1Tools.RunCmd(cfg, Fill(onOk), cfg.CommandTimeout, CancellationToken.None);
                    if (onFin.Length > 0) await Phase1Tools.RunCmd(cfg, Fill(onFin), cfg.CommandTimeout, CancellationToken.None);
                    // 钉钉按需通知：有真实文件修改 / 弹过危险确认窗（需人工处理） / 任务失败 才发；纯聊天查询类任务不打扰
                    var worthNotifying = journal.WriteCount > 0 || taskHadDanger || !success;
                    if (autoDing && worthNotifying)
                    {
                        // 再等 3 分钟：期间用户仍有操作（人还盯着）就不发，无操作（人走开了）才发
                        if (await IdleGate.WaitSilentAsync())
                            await SendDingTalk(success, rounds, tokens, dingTask);
                        else
                            Log("[Hook] 等待期间用户仍有操作，跳过任务结束钉钉通知");
                    }
                    else if (autoDing) Log("[Hook] 任务无文件修改/危险确认且执行成功，跳过钉钉通知");
                }
                catch (Exception ex)
                {
                    Log($"[Error] Hook执行失败: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Log($"[Error] RunHooks失败: {ex.Message}");
        }
    }

    async Task SendDingTalk(bool success, int rounds, int tokens, string task)
    {
        var webhook = cfg.DingTalkWebhook;
        if (string.IsNullOrEmpty(webhook)) return;
        try
        {
            var ts = DateTimeOffset.Now.ToUnixTimeMilliseconds().ToString();
            var sign = "";
            if (!string.IsNullOrEmpty(cfg.DingTalkSecret))
            {
                var key = Encoding.UTF8.GetBytes(cfg.DingTalkSecret);
                var msg = Encoding.UTF8.GetBytes(ts + "\n" + cfg.DingTalkSecret);
                using var hmac = new System.Security.Cryptography.HMACSHA256(key);
                sign = Convert.ToBase64String(hmac.ComputeHash(msg));
            }
            var url = webhook + (webhook.Contains('?') ? "&" : "?") + $"timestamp={ts}&sign={Uri.EscapeDataString(sign)}";
            var body = new JsonObject
            {
                ["msgtype"] = "markdown",
                ["markdown"] = new JsonObject
                {
                    ["title"] = "GAIRR 任务通知",
                    ["text"] = $"### GAIRR 任务通知\n\n- **状态**：{(success ? "✅ 成功" : "❌ 失败")}\n- **任务**：{task}\n- **轮次**：{rounds}\n- **Token**：{tokens}\n- **时间**：{DateTime.Now:yyyy-MM-dd HH:mm:ss}"
                }
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            await LLMClient.Http.SendAsync(req, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log($"[Error] 钉钉通知失败: {ex.Message}");
        }
    }

    /// <summary>钉钉 markdown 推送通用出口（无 webhook 静默跳过；异常仅记日志，不干扰主线任务）</summary>
    async Task SendDingMarkdownAsync(string title, string text)
    {
        var webhook = cfg.DingTalkWebhook;
        if (string.IsNullOrEmpty(webhook)) return;
        try
        {
            var ts = DateTimeOffset.Now.ToUnixTimeMilliseconds().ToString();
            var sign = "";
            if (!string.IsNullOrEmpty(cfg.DingTalkSecret))
            {
                var key = Encoding.UTF8.GetBytes(cfg.DingTalkSecret);
                var msg = Encoding.UTF8.GetBytes(ts + "\n" + cfg.DingTalkSecret);
                using var hmac = new System.Security.Cryptography.HMACSHA256(key);
                sign = Convert.ToBase64String(hmac.ComputeHash(msg));
            }
            var url = webhook + (webhook.Contains('?') ? "&" : "?") + $"timestamp={ts}&sign={Uri.EscapeDataString(sign)}";
            var body = new JsonObject
            {
                ["msgtype"] = "markdown",
                ["markdown"] = new JsonObject { ["title"] = title, ["text"] = text }
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            await LLMClient.Http.SendAsync(req, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log($"[Error] 钉钉通知失败: {ex.Message}");
        }
    }

    /// <summary>等人工挂起（危险确认/计划审批）的钉钉通知路由：
    /// IdleGate 未启用（CLI/服务宿主，无 UI 视口概念）→ 立即发一条；
    /// UI 宿主启用后按"挂起会话是否仍在视口"分通道——
    /// ① 视口会话 → 不发钉钉（UI 已弹卡就近处理；用户走开 ≥3 分钟才走 ③ 兜底）；
    /// ② 非视口/后台会话挂起 → 切出视口持续满短静默窗口（[Notify]ShortSilentSec，默认 60s）仍挂起即发一条。
    ///    不要求用户静默：人在其它会话忙碌同样补发，避免后台挂起失联；切回视口清零、再切走重新计时；
    /// ③ 任一会话挂起且人不在 ≥3 分钟（IdleGate.SilentThreshold）→ 兜底发一条（视口卡无人处理等走开场景）。
    /// 同一挂起点只发一条（②③ 先到先发、不双发）；视口挂起中被切走 → InViewport=false 后自动转 ② 补发。</summary>
    async Task SendDingTalkAlert(string cmd, string pattern, string? intent)
        => await RunHangNotifierAsync(() => dangerTcs != null, () => BuildHangText("危险命令拦截", BuildDangerHangText(cmd, pattern, intent)));

    /// <summary>计划审批挂起通知：与危险确认同一套视口弹卡/后台短静默/人不在兜底路由</summary>
    async Task NotifyPlanPendingAsync(Plan plan)
        => await RunHangNotifierAsync(() => planTcs != null, () => BuildHangText("计划审批", BuildPlanHangText(plan)));

    /// <summary>挂起提醒通知循环（通道语义见 SendDingTalkAlert 注释）；fire-and-forget 调用，异常不外抛</summary>
    async Task RunHangNotifierAsync(Func<bool> stillPending, Func<string> buildText)
    {
        if (!stillPending()) return;                       // 已解决/已取消 → 不发
        try
        {
            if (!IdleGate.Enabled)                         // CLI/服务宿主：立即发（保持原有语义）
            {
                await SendDingMarkdownAsync("GAIRR 挂起提醒", buildText());
                return;
            }
            var shortSilent = TimeSpan.FromSeconds(SystemCfg.NotifyShortSilentSec);
            long offViewSinceMs = -1;   // 连续"非视口"起始时刻（TickCount64 毫秒）；-1=当前在视口（卡可见，由 UI 就近处理）
            while (true)
            {
                if (!stillPending()) return;
                // ② 非视口/后台会话挂起 → 切出视口持续满短静默窗口仍挂起即补发：
                //    不要求用户静默——人在其它会话忙碌同样补发，避免后台挂起失联；切回视口清零、再切走重新计时
                if (InViewport) offViewSinceMs = -1;
                else if (offViewSinceMs < 0) offViewSinceMs = Environment.TickCount64;
                if (offViewSinceMs >= 0
                    && Environment.TickCount64 - offViewSinceMs >= (long)shortSilent.TotalMilliseconds)
                {
                    await SendDingMarkdownAsync("GAIRR 挂起提醒", buildText());
                    return;
                }
                var idle = TimeSpan.FromMilliseconds(IdleGate.IdleMs);
                if (idle >= IdleGate.SilentThreshold)      // ③ 人不在 ≥3 分钟 → 兜底（视口/非视口都适用）
                {
                    await SendDingMarkdownAsync("GAIRR 挂起提醒", buildText());
                    return;
                }
                await Task.Delay(3000);
            }
        }
        catch (Exception ex)
        {
            Log($"[Hook] 挂起提醒通知异常: {ex.Message}");
        }
    }

    /// <summary>带会话归属的挂起提醒正文：会话名 + 会话标识 + 角色/类型 + 明细</summary>
    string BuildHangText(string kindTitle, string detail)
    {
        var title = !string.IsNullOrEmpty(sessionTitle) ? sessionTitle
            : !string.IsNullOrEmpty(curTaskText) ? LLMClient.Trunc(curTaskText, 40) : "（未命名会话）";
        var id = !string.IsNullOrEmpty(sessionKey) ? sessionKey
            : !string.IsNullOrEmpty(SessionId) ? SessionId : "—";
        var role = IsOrchestration ? "编排会话"
            : isAutoTask ? "自动任务会话"
            : PlanMode ? "主会话（计划审批模式）" : "主会话";
        return $"### ⚠️ GAIRR {kindTitle}\n\n"
             + $"- **会话**：{title}\n- **会话标识**：{id}\n- **角色**：{role}\n"
             + $"- **时间**：{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n\n{detail}\n\n请回 GAIRR 对应会话处理（允许/取消）。";
    }

    /// <summary>危险确认明细：危险命令 / 命中规则 / 模型意图（危险挂起不自动超时拒绝，等人工）</summary>
    string BuildDangerHangText(string cmd, string pattern, string? intent)
    {
        var text = $"- **命令**：`{cmd}`\n- **命中规则**：{pattern}";
        if (!string.IsNullOrEmpty(intent))
            text += $"\n- **模型意图**：{intent}";
        text += "\n\n请核对后点击【允许】放行，或【取消】拒绝。";
        return text;
    }

    /// <summary>计划审批明细：计划摘要 + 步骤数 + 计划号</summary>
    string BuildPlanHangText(Plan plan)
    {
        var summary = string.IsNullOrEmpty(plan.Summary) ? "（无摘要）" : LLMClient.Trunc(plan.Summary, 120);
        return $"- **计划**：{summary}\n- **步骤数**：{plan.Steps?.Count ?? 0}\n- **计划号**：{plan.Id}";
    }

    /* ---------- system prompt 外置：prompts/agent-deep.md 热加载（每任务重读，改 prompt 免重编译） ---------- */
    JsonObject SysMessage()
    {
        try
        {
            return new()
            {
                ["role"] = "system",
                ["content"] = LoadPrompt() + ExtraPromptSuffix() + (skills?.Manifest() ?? "") + ProjectMapAuto.Lessons(cfg),
            };
        }
        catch (Exception ex)
        {
            Log($"[Error] 生成系统消息失败: {ex.Message}");
            return new() { ["role"] = "system", ["content"] = DefaultPrompt() };
        }
    }

    /// <summary>工具级（与项目无关）提示词：统一存程序目录 prompts/（csproj 打包、全项目共享、可热改）；
    /// 仅 prefs.md 等项目专属文件留在 {ProjectRoot}/.gairr/prompts/。</summary>
    static readonly HashSet<string> ToolLevelPrompts = new(StringComparer.OrdinalIgnoreCase)
    {
        "agent-agile.md", "agent-deep.md", "autotask-run.md", "plan-orchestration.md",
        "plan-planner.md", "plan-judge.md", "plan-review.md",
    };

    /// <summary>提示词路径解析：绝对路径（角色包私有提示词等已解析路径）直用 →
    /// 工具级白名单 → 程序目录 prompts/；其余（角色提示词 ba/pm/qa/ops/refactor/bugfix/review.md、prefs.md 等）
    /// 按「项目级已存在 → 程序目录共享池已存在 → 项目级（留给 EnsurePromptFile 落默认）」三档解析。
    /// 注意：不能无条件返回项目级路径——角色提示词只打包在程序目录 prompts/，
    /// 项目 .gairr/prompts/ 下没有同名文件时会被 EnsurePromptFile 写成兜底默认文本，导致角色提示词整体失效。</summary>
    string PromptPath(string file)
    {
        if (Path.IsPathRooted(file)) return file;   // 角色包内私有提示词：RoleModeStore.ResolvePromptFile 已给出绝对路径
        var toolLevel = Path.Combine(AppContext.BaseDirectory, "prompts", file);
        if (ToolLevelPrompts.Contains(file)) return toolLevel;
        if (!string.IsNullOrWhiteSpace(cfg.ProjectRoot))
        {
            var proj = Path.Combine(cfg.ProjectRoot, ".gairr", "prompts", file);
            if (File.Exists(proj)) return proj;      // ① 项目级自定义覆盖（确实放了同名文件才生效）
            if (File.Exists(toolLevel)) return toolLevel;   // ② 回退程序目录共享池（角色提示词的正常落点）
            return proj;                             // ③ 两处都没有：仍按项目级路径，由 EnsurePromptFile 落默认模板
        }
        return toolLevel;
    }

    /// <summary>首次缺失时按内置默认模板生成提示词文件，方便用户后续自定义。</summary>
    void EnsurePromptFile(string path, string file)
    {
        var defaultText = string.Equals(file, "autotask-run.md", StringComparison.OrdinalIgnoreCase)
            ? DefaultAutoTaskPromptText
            : DefaultPrompt();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, defaultText, new UTF8Encoding(false));
    }

    string LoadPrompt()
    {
        var path = PromptPath(promptFile);
        try
        {
            if (!File.Exists(path))
                EnsurePromptFile(path, promptFile);
            return File.ReadAllText(path)
                .Replace("{ProjectRoot}", cfg.ProjectRoot)
                .Replace("{MaxFileLines}", cfg.MaxFileLines.ToString())
                .Replace("{MaxFuncLines}", cfg.MaxFuncLines.ToString());
        }
        catch (Exception ex)
        {
            Log($"[Error] 加载prompt失败: {ex.Message}");
            return DefaultPrompt();
        }
    }

    /// <summary>会话级附加提示词（编排会话等场景注入主持人 prompt），非空时追加在基础 prompt 之后、skills 之前。</summary>
    public string? ExtraSystemPrompt { get; set; }

    /// <summary>附加提示词追加段：非空时以空行分隔拼到基础 prompt 后。</summary>
    string ExtraPromptSuffix() => ExtraSystemPrompt == null ? "" : "\n\n" + ExtraSystemPrompt;

    /// <summary>DefaultPrompt 已外置为程序目录 prompts/agent-deep.md（csproj 打包）热加载；此处仅保留极简兜底（文件缺失时用）。
    /// 实际提示词由 LoadPrompt() 从 prompts/agent-deep.md 读取，改 prompt 免重编译。</summary>
    string DefaultPrompt() =>
        "你是 GAIRR，运行在用户 Windows 电脑上的专属自动化开发 Agent。\n" +
        "工作环境：项目根目录 {ProjectRoot}；shell 为 cmd（支持 &&，禁止 PowerShell 语法）。\n" +
        "## ⚡ 任务复杂度分级\n" +
        "L0级（配置/文案/单文件修复）：免清单、免SmartSearch，直接 Grep→Read→Edit→验证。\n" +
        "L1级（函数级修改/Bug修复）：1-3步精简清单，SmartSearch定位→Edit→验证。\n" +
        "L2级（跨模块/架构调整）：完整清单+四层检索+方案对比。\n" +
        "80%任务是L0/L1，不要默认走L2流程。\n" +
        "修改文件前必须先 Read；Edit 失败时重新 Read 复制原文重试。\n" +
        "改完必须验证（编译/GetProblems）；收尾前确认已实际 Write/Edit 并回读。\n" +
        "每轮 content 以一句中文说明意图；并行发起无依赖工具调用减少轮数。\n" +
        "（agent-deep.md 缺失时的极简兜底，请检查程序目录 prompts/agent-deep.md 是否存在。）";

    const string DefaultAutoTaskPromptText =
        "你是 GAIRR 的自动任务执行器，运行在用户 Windows 电脑上，负责在无人值守背景下完成用户预设的自动化任务。\n" +
        "\n" +
        "## 核心定位\n" +
        "- 当前是**自动任务（无人值守）**，不是交互式开发任务。\n" +
        "- 目标是按照用户 Prompt 的描述，自主收集信息、处理数据、生成结论并按需发送钉钉。\n" +
        "- 你**拥有完整的工具调用权限**，危险命令已被系统自动放行，不需要向用户确认。\n" +
        "\n" +
        "## 网络与数据访问规则\n" +
        "- **禁止直接通过 URL 抓取网页内容**（例如不能依赖模型自身访问互联网、生成 pseudo-URL 请求、或在脑海中模拟 HTTP 请求）。\n" +
        "- **所有网络访问必须通过可用工具完成**：\n" +
        "  - 收集科技热门新闻：使用 `NewsDigest` 工具；\n" +
        "  - 通用 HTTP/HTTPS 请求、下载、API 调用：使用 `Bash` 运行 `curl`（cmd 语法，支持 `&&`）；\n" +
        "  - 读取网页或文件内容：使用 `Read` 工具读取本地文件，或使用 `Bash` 将结果写入本地文件后再 `Read`。\n" +
        "- 若任务需要访问 URL 但没有明确给出请求细节，先用 `Bash` 运行 `curl -L -s <URL>` 获取内容，再基于返回结果分析。\n" +
        "\n" +
        "## 执行流程\n" +
        "1. **理解任务**：复述任务目标与预期输出格式（写入本轮 content）。\n" +
        "2. **制定计划**：若任务可一步完成则直接执行；若需要多步骤，在心中规划执行顺序。\n" +
        "3. **调用工具执行**：优先使用最合适的工具，必要时组合多个工具。每轮 content 以一句简体中文说明本轮操作意图。\n" +
        "4. **验证**：如果任务结果可验证（如命令返回值、文件内容、钉钉发送结果），在最终输出前做必要验证。\n" +
        "5. **生成结论**：按用户 Prompt 要求的格式整理最终结论；若用户未指定格式，默认使用 Markdown 列表/摘要。\n" +
        "6. **发送结果**：如果用户 Prompt 明确要求发送钉钉，在生成结论后调用 `DingTalk` 工具发送；否则只在结论末尾提供可发送的内容，不主动发送。\n" +
        "\n" +
        "## 输出与结论\n" +
        "- 最终结论必须包含：任务目标、关键结果、执行摘要。\n" +
        "- **强制结束标记**：最终结论末尾必须独占一行输出 `<autotask-result></autotask-result>`，表示任务已结束，后续不再调用任何工具。\n" +
        "- 若发生错误，说明失败原因、已尝试的补救措施以及下一步建议，同样以 `<autotask-result></autotask-result>` 结束。\n" +
        "- 不要输出与任务无关的代码规范、开发流程、待办清单等内容。\n" +
        "\n" +
        "## 工具使用偏好\n" +
        "- **禁止使用 `UpdateTodo`**：自动任务无人值守，不要创建/更新待办清单，直接执行即可。\n" +
        "- `Bash`：cmd 语法，支持 `&&`，禁止 PowerShell。\n" +
        "- `DingTalk`：仅在任务明确要求发送消息时使用，消息内容使用 Markdown 格式。\n" +
        "- `NewsDigest`：用于收集 Reddit / GitHub / Hacker News 的热门科技内容并生成中文摘要（只返回摘要结果，不自动发钉钉；需要发送时用 `DingTalk` 发送该结果）。\n" +
        "- `Read` / `ListDir` / `Glob` / `Grep`：用于读取本地文件、查看目录、搜索内容。\n" +
        "\n" +
        "## 安全与效率\n" +
        "- 自动任务已开启危险命令自动放行，执行 `curl` 等命令时不要请求用户确认。\n" +
        "- 避免无意义的反复调用，若某一步已得到足够信息，应及时收敛并生成结论。\n" +
        "- 若任务含糊或缺少必要参数（如钉钉 token、目标 URL、API key），按最合理假设继续执行；确实无法继续时，在结论中说明原因并结束任务，不要等待用户输入。\n" +
        "- 一旦输出 `<autotask-result></autotask-result>`，必须停止调用工具，任务视为已完成。\n" +
        "\n" +
        "（本文件为自动任务提示词模板，可直接编辑，下次任务生效，免重编译；{ProjectRoot} 等占位符自动替换。）";

    /// <summary>确保工具级 autotask-run.md 已存在于程序目录 prompts/（csproj 已打包，此处为缺失兜底）；缺失时写入默认自动任务提示词。</summary>
    public static void EnsureAutoTaskPrompt(AppConfig cfg)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "prompts", "autotask-run.md");
        if (File.Exists(path)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, DefaultAutoTaskPromptText, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Error] 生成默认 autotask-run.md 失败: {ex.Message}");
        }
    }

    /// <summary>错误文本归一化：取首个非空行并抹掉路径/行号等易变细节，保留可复现的失败模式</summary>
    static string NormalizeError(string result)
    {
        var s = result.Replace("\r\n", "\n").Split('\n').FirstOrDefault(l => l.Length > 0) ?? result;
        s = Regex.Replace(s, @"[A-Za-z]:\\[^\s:;,，。）)]*", "<path>");
        s = Regex.Replace(s, @"\b[\w\.\-]+\.(?:cs|ts|js|py|go|java|csproj|xaml|json)(?:\:\d+)?\b", "<file>");
        return s.Length > 160 ? s[..160] : s;
    }
    
    /// <summary>从收尾正文提取【经验沉淀】段条目：每行"标签1,标签2 | 经验"格式（无"|"时整行视为经验、标签为空），
    /// 最多 3 条；遇后续其他【】标题即结束。正文 8-200 字、标签部分 ≤60 字才收录，过短噪音/超长段落丢弃</summary>
    static List<(string Text, string Tags)> ExtractLessons(string content)
    {
        var list = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(content)) return list;
        var inSection = false;
        foreach (var raw in content.Replace("\r\n", "\n").Split('\n'))
        {
            var t = raw.Trim();
            if (!inSection)
            {
                if (t.StartsWith("【经验沉淀】", StringComparison.Ordinal)) inSection = true;
                continue;
            }
            if (t.StartsWith("【", StringComparison.Ordinal)) break;   // 后续其它标题段：经验段到此为止
            if (t.Length == 0) continue;
            var item = t.TrimStart('-', '*', '•', ' ', '\t', '·').Trim();
            if (item.Length < 8 || item.Length > 200) continue;       // 过滤过短噪音与超长段落
            var sep = item.IndexOf('|');
            if (sep <= 0 || sep >= item.Length - 1)
            {
                list.Add((item, ""));
                if (list.Count >= 3) break;
                continue;
            }
            var tagsPart = item[..sep].Trim().Trim('【', '】');   // 兼容"【标签】| 经验"写法
            var body = item[(sep + 1)..].Trim();
            if (tagsPart.Length > 0 && tagsPart.Length <= 60 && body.Length >= 8)
                list.Add((body, tagsPart));
            if (list.Count >= 3) break;
        }
        return list;
    }

    /// <summary>经验沉淀前的文件引用校验：提取经验文本中的"文件路径:行号/符号"引用
    /// （如 Tools.cs:200、Core/Tools.cs:Read），分两级确认——文件不存在时用新鲜索引按符号/文件名兜底；
    /// 文件存在且带符号名时再查符号是否仍存在于该文件（防符号已删除/改名）。
    /// 确认不了的引用在条目末尾追加"（⚠引用待验证: xxx）"标记——不丢弃整条（经验主体可能有效），
    /// 但让后续会话的模型看到标记后不盲信行号/符号。行号本身不校验（索引会漂移，文件存在即可信）。</summary>
    static string CheckFileRefsInLesson(AppConfig cfg, string text)
    {
        var bad = new List<string>();
        // 匹配：可选目录前缀 + 文件名（带代码扩展名），冒号后可跟行号或符号名
        foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(text, @"([\w\-./\\]*[\w\-]+\.(?:cs|ts|tsx|js|jsx|py|java|go|kt|vue|csproj))\s*[:：]\s*([A-Za-z0-9_]+)?",
                         System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            var path = m.Groups[1].Value;
            var sym = m.Groups[2].Value;
            var fileOk = Phase1Tools.Locate(cfg, path, out _).Length > 0;
            if (fileOk)
            {
                // 文件存在：若带符号名，再确认符号仍存在于该文件（防符号已删除/改名后经验引用失效）
                if (sym.Length > 0)
                {
                    var loc = SymbolIndex.LocateDef(cfg, sym, Path.GetFileName(path));
                    if (loc.start <= 0)
                    {
                        if (!bad.Contains(path + ":" + sym, StringComparer.OrdinalIgnoreCase))
                            bad.Add(path + ":" + sym);
                    }
                }
                continue;
            }
            // 文件不存在：若带符号名且新鲜索引里能按"符号@同名文件"定位到，也视为可信（路径写法差异）
            if (sym.Length > 0 && SymbolIndex.LocateDef(cfg, sym, Path.GetFileName(path)).start > 0) continue;
            if (!bad.Contains(path, StringComparer.OrdinalIgnoreCase)) bad.Add(path);
        }
        if (bad.Count == 0) return text;
        var marked = text + "（⚠引用待验证: " + string.Join("、", bad) + "）";
        return marked.Length > 220 ? text : marked;   // 超长截断保护：宁可不标也不破坏格式
    }

    /// <summary>判断工具参数是否为 UpdateTodo 的 create 动作（决定是否切换模型主导清单模式）</summary>
    static bool IsTodoCreate(string arguments)
    {
        try { return (JsonNode.Parse(arguments) as JsonObject)?["action"]?.GetValue<string>() == "create"; }
        catch { return false; }
    }

    /// <summary>解析 UpdateTodo 参数为待办事件（解析失败返回 null，参数不合规时由工具校验文本回喂模型）</summary>
    static TodoUpdate? ParseTodoArgs(string arguments)
    {
        try
        {
            var args = JsonNode.Parse(arguments) as JsonObject;
            if (args == null) return null;
            switch (args["action"]?.GetValue<string>())
            {
                case "create":
                    var steps = new List<string>();
                    if (args["steps"] is JsonArray arr)
                        foreach (var s in arr)
                            steps.Add((s?.GetValue<string>() ?? "").Trim());
                    return new TodoUpdate { Action = "create", Steps = steps };
                case "update":
                    return new TodoUpdate { Action = "update", Index = args["index"]?.GetValue<int>() ?? 0, Done = args["done"]?.GetValue<bool>() ?? true };
                case "done_all":
                    return new TodoUpdate { Action = "done_all" };
                default:
                    return null;
            }
        }
        catch { return null; }
    }

    /// <summary>从工具参数字符串提取指定字段值（简单 JSON 解析，失败返回空字符串）</summary>
    static string ExtractArg(string arguments, string key)
    {
        try
        {
            var args = JsonNode.Parse(arguments) as JsonObject;
            return args?[key]?.GetValue<string>() ?? "";
        }
        catch { return ""; }
    }

    /// <summary>工具参数字符串是否为完整合法 JSON（模型流截断产出半截参数会解析失败，供执行前完整性校验）</summary>
    static bool IsValidArgsJson(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return false;
        try { return JsonNode.Parse(arguments) != null; }
        catch { return false; }
    }

    /// <summary>解析为规范化绝对路径（相对路径按项目根拼接；统一斜杠与尾分隔符，供护栏与已读登记比较）</summary>
    string NormPath(string p)
    {
        p = (p ?? "").Trim().Trim('"');
        if (p.Length == 0) return "";
        var full = Path.IsPathRooted(p) ? p : Path.Combine(cfg.ProjectRoot, p);
        return Path.GetFullPath(full).TrimEnd('\\', '/');
    }

    /// <summary>写前护栏判定：Write 目标已存在且会话未读过 → 返回拦截提示；否则空串放行。Edit 不强拦（old_text 失配兜底已防改错）。</summary>
    string GuardBlockWrite(LlmToolCall tc, List<LlmToolCall> allCalls)
    {
        if (tc.Name != "Write" || !SystemCfg.FirstReadCheck) return "";
        var wp = NormPath(ExtractArg(tc.Arguments, "path"));
        if (wp.Length == 0 || !File.Exists(wp)) return "";   // 新文件无需回读
        lock (readSigCount) { if (sessionReadPaths.Contains(wp)) return ""; }
        // 同轮已计划 Read 该文件：工具并发时顺序不定（先写后读语义错误），明确要求分两轮，避免"读过了怎么还拦"式重试
        var planned = allCalls.Any(t => t.Name == "Read" && NormPath(ExtractArg(t.Arguments, "path")) == wp);
        return "拦截：先回读再改动——目标文件 " + wp + " 本会话未被 Read 读取过，直接 Write 覆盖可能丢失已有内容"
             + (planned
                 ? "；你本轮虽计划了 Read，但工具并发时执行顺序不定，请先单独 Read 该文件确认现状，下一步再 Write。"
                 : "；请先用 Read 读取该文件（或目标区域）确认现状，再重试 Write。");
    }

    /// <summary>从工具参数解析目标文件列表：Read 取 path；MapSlice 取 spec 各段"最后冒号前"的文件部分（定位冒号是文件与行号/符号名的分隔；MapSlice 的 spec 必带定位段）</summary>
    List<string> GuardPathsOf(string name, string args)
    {
        var paths = new List<string>();
        if (name == "Read")
        {
            var p = NormPath(ExtractArg(args, "path"));
            if (p.Length > 0) paths.Add(p);
        }
        else if (name == "MapSlice")
        {
            var spec = ExtractArg(args, "spec");
            foreach (var seg in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var ci = seg.LastIndexOf(':');
                var p = NormPath(ci > 0 ? seg[..ci] : seg);
                if (p.Length > 0 && !paths.Contains(p)) paths.Add(p);
            }
        }
        return paths;
    }

    /// <summary>从危险命令拦截提示中提取命令与命中模式，用于 UI 安全警报展示</summary>
    static (string command, string pattern) ExtractDangerInfo(string result)
    {
        // "拦截：危险命令（del ）已被统一审查拒绝"
        var m = Regex.Match(result, @"危险命令[（(]([^)）]+)[）)]");
        var pattern = m.Success ? m.Groups[1].Value.Trim() : "";
        // 命令本身不在 result 中，由调用处通过工具参数补充；这里兜底返回 result 首行
        var command = result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? result;
        return (command, pattern);
    }

    /// <summary>从工具返回文本提取一行结果摘要（供工具条标题旁展示）：按工具名分派专配规则，未识别时走通用统计短语</summary>
    public static string SummarizeToolResult(string tool, string result)
    {
        if (string.IsNullOrWhiteSpace(result)) return "";
        // 失败类结果统一展示首行（状态位已标 ✗，摘要补充原因）
        var t0 = result.TrimStart();
        if (t0.StartsWith("错误") || t0.StartsWith("拦截") || t0.StartsWith("超时") || t0.StartsWith("✗") || t0.StartsWith("工具执行异常"))
            return FirstLine(result, 40);

        Match m;
        switch (tool)
        {
            case "Read":   // 首行 [编码 · 共 76 行 · 返回 1-76]
                m = Regex.Match(result, @"共\s*\d+\s*行\s*·\s*返回\s*[\d-]+");
                return m.Success ? m.Value : "";
            case "MapSlice":   // 每段头 【文件 · 编码 · 行 22-322 / 共 716】；多段时汇总段数
                var heads = Regex.Matches(result, @"【[^】\n]*行\s*(\d+)-(\d+)\s*/\s*共\s*(\d+)】");
                if (heads.Count == 0) return "";
                var one = $"行 {heads[0].Groups[1].Value}-{heads[0].Groups[2].Value} / 共 {heads[0].Groups[3].Value}";
                return heads.Count == 1 ? one : $"{heads.Count} 段：{one} 等";
            case "MapTrace":   // 符号索引："q" 命中 N 个符号
                m = Regex.Match(result, @"命中\s*\d+\s*个符号");
                return m.Success ? m.Value : result.Contains("未找到包含") ? "未命中" : "";
            case "FindRefs":   // 编译器引用：符号 X 的 N 处引用（编译器解析）
                m = Regex.Match(result, @"的\s*\d+\s*处引用");
                return m.Success ? "命中 " + m.Value[1..] : FirstLine(result, 40);
            case "SmartSearch":   // 智能搜索："q" 命中 N 个结果
                m = Regex.Match(result, @"命中\s*\d+\s*个结果");
                return m.Success ? m.Value : result.Contains("未找到与") ? "未命中" : "";
            case "Map":   // 【项目地图】d:\root :: focus（文件 116 个）
                m = Regex.Match(result, @"【项目地图】(.+?)（文件\s*\d+\s*个）");
                if (m.Success) return m.Groups[1].Value.Trim('/') + " 共 " + Regex.Match(m.Value, @"\d+").Value + " 个文件";
                return "";
            case "MapAuto":   // 进度日志（N% · 消息）或启动提示，取首行
                return FirstLine(result, 40);
            case "Bash":   // 退出码 N
                m = Regex.Match(result, @"退出码\s*(-?\d+)");
                return m.Success ? "退出码 " + m.Groups[1].Value : FirstLine(result, 40);
            case "Write":   // 已写入 path（N 行，…）/ （新建文件）
                m = Regex.Match(result, @"（(\d+)\s*行");
                if (m.Success) return (result.Contains("（新建文件）") ? "新建 · " : "") + m.Groups[1].Value + " 行";
                return "";
            case "Edit":   // 编辑成功：path（行 X-Y）…【diff】行 X-Y（+A -D）
                if (!result.StartsWith("编辑成功")) return "";
                m = Regex.Match(result, @"行\s*(\d+)-(\d+)");
                var ad = Regex.Match(result, @"（\+(\d+)\s+-(\d+)）");
                var loc = m.Success ? $"行{m.Groups[1].Value}-{m.Groups[2].Value}" : "";
                return ad.Success ? $"{loc} · +{ad.Groups[1].Value} -{ad.Groups[2].Value}" : loc;
            case "DbQuery":   // SELECT 表格数行数；写操作"影响 N 行"
                if (result.StartsWith("|"))
                {
                    var rows = result.Split('\n').Count(l => l.StartsWith("|")) - 2;   // 去掉表头与分隔行
                    return result.Contains("仅显示前 100 行") ? "返回 100+ 行" : $"返回 {Math.Max(rows, 0)} 行";
                }
                m = Regex.Match(result, @"影响\s*(\d+)\s*行");
                return m.Success ? "影响 " + m.Groups[1].Value + " 行" : "";
            case "LoadSkill":   // 技能全文，按行数摘要
                return result.Split('\n').Length + " 行";
        }
        // 通用统计（Grep/Glob/ListDir 等）：共 N 项 / 共 N 处匹配(+表示超上限) / 共 N 个文件
        m = Regex.Match(result, @"共\s*\d+\+?\s*(?:项|处匹配|个文件)");
        if (m.Success) return m.Value;
        var t = result.Trim();
        return t == "无匹配" || t == "无匹配文件" ? t : "";
    }

    /// <summary>取文本首个非空行并截断，供摘要位展示失败原因等</summary>
    static string FirstLine(string text, int max)
    {
        var line = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return LLMClient.Trunc(line.Trim(), max);
    }

    static readonly Dictionary<string, string> ToolCn = new()
    {
        ["Map"] = "项目地图",
        ["MapAuto"] = "地图自动化",
        ["MapTrace"] = "符号检索",
        ["SmartSearch"] = "语义检索",
        ["MapSlice"] = "提取代码块",
        ["FindRefs"] = "引用检索",
        ["Read"] = "读取文件",
        ["ListDir"] = "列出目录",
        ["Bash"] = "执行命令",
        ["DbQuery"] = "数据库查询",
        ["Write"] = "写入文件",
        ["Edit"] = "编辑文件",
        ["Grep"] = "搜索内容",
        ["Glob"] = "查找文件",
        ["LoadSkill"] = "加载技能",
        ["UpdateTodo"] = "更新待办",
        ["RecallHistory"] = "取回历史",
    };

    /// <summary>工具分类：命令（Bash）/内置工具/插件（动态注册名不在内置清单内）</summary>
    static string KindOf(string name) =>
        string.Equals(name, "Bash", StringComparison.OrdinalIgnoreCase) ? "命令"
        : ToolCn.ContainsKey(name) ? "工具"
        : "插件";

    /// <summary>只读工具集：本轮全部命中时视为查看/问答行为，豁免首轮强制建清单。
    /// Bash/DbQuery 可写不列入；插件行为未知不列入（宁可拦截不漏放）</summary>
    static readonly HashSet<string> ReadOnlyToolSet = new(StringComparer.OrdinalIgnoreCase)
        { "Read", "Grep", "Glob", "ListDir", "Map", "MapTrace", "SmartSearch", "MapSlice", "RecallHistory" };

    /// <summary>各工具条标题优先展示的"主参数"（按优先级排列）：让标题直接体现这次调用在做什么</summary>
    static readonly Dictionary<string, string[]> ToolMainArgs = new()
    {
        ["Read"] = new[] { "path" },
        ["Write"] = new[] { "path" },
        ["Edit"] = new[] { "path" },
        ["Grep"] = new[] { "pattern", "path" },
        ["Glob"] = new[] { "pattern", "path" },
        ["Bash"] = new[] { "command" },
        ["DbQuery"] = new[] { "sql", "db" },
        ["Map"] = new[] { "path" },
        ["MapTrace"] = new[] { "q" },
        ["SmartSearch"] = new[] { "q" },
        ["MapSlice"] = new[] { "spec", "chain" },
        ["ListDir"] = new[] { "path" },
        ["LoadSkill"] = new[] { "name" },
    };

    /// <summary>工具图标映射：检索类用 🔍，文件操作用 📄，修改用 ✏️，命令用 ⚡，地图用 🗺️</summary>
    static string ToolIcon(string name) => name switch
    {
        "SmartSearch" => "🔍",
        "MapTrace" => "🔍",
        "FindRefs" => "🔍",
        "Grep" => "🔍",
        "Glob" => "🔍",
        "Map" => "🗺️",
        "MapAuto" => "🗺️",
        "MapSlice" => "📖",
        "DbQuery" => "🗄️",
        "Read" => "📄",
        "ListDir" => "📄",
        "Write" => "✏️",
        "Edit" => "✏️",
        "Bash" => "⚡",
        "LoadSkill" => "🧩",
        "UpdateTodo" => "✅",
        "RecallHistory" => "📝",
        _ => "🔧"
    };

    /// <summary>工具图标字号：编辑类偏小避免笔尖显笨，命令类偏大增强执行感，其余保持默认 13</summary>
    static double ToolIconFontSize(string name) => name switch
    {
        "Write" or "Edit" => 11,
        "Bash" => 16,
        _ => 13
    };

    /// <summary>工具图标颜色：检索类蓝色，文件类绿色，修改类橙色，命令类紫色，地图类青色，其他灰色</summary>
    static string ToolIconColor(string name) => name switch
    {
        "SmartSearch" or "MapTrace" or "FindRefs" or "Grep" or "Glob" => "#5C8AE6",  // 蓝
        "Read" or "ListDir" => "#3ECF8E",                                              // 绿
        "Write" or "Edit" => "#F0A13E",                                               // 橙
        "Bash" => "#A855F7",                                                           // 紫
        "Map" or "MapAuto" or "MapSlice" => "#22D3EE",                                // 青
        "DbQuery" => "#F472B6",                                                        // 粉
        "LoadSkill" => "#A78BFA",                                                      // 浅紫
        "UpdateTodo" => "#34D399",                                                     // 翠绿
        "RecallHistory" => "#94A3B8",                                                  // 灰
        _ => "#94A3B8"
    };

    /// <summary>工具条标题 = 中文名 + 主参数摘要；查询类参数（q/spec/sql/name）加引号便于辨认</summary>
    static string TitleOf(LlmToolCall tc)
    {
        var cn = ToolCn.GetValueOrDefault(tc.Name, tc.Name);
        try
        {
            var args = JsonNode.Parse(tc.Arguments) as JsonObject;
            if (args == null) return cn;
            var keys = ToolMainArgs.GetValueOrDefault(tc.Name) ?? new[] { "path", "command", "pattern" };
            foreach (var k in keys)
            {
                var v = args[k]?.GetValue<string>()?.Trim() ?? "";
                if (v.Length == 0) continue;
                var quoted = k is "q" or "name" ? $"\"{LLMClient.Trunc(v, 32)}\"" : LLMClient.Trunc(v, 48);
                return cn + " " + quoted;
            }
            return cn;
        }
        catch { return cn; }
    }

    /// <summary>UpdateTodo 卡片标题：按动作展示语义（创建-计划（N 步）/ 计划步骤N / 全完成）；步骤切换的"开始"卡由框架延迟展示</summary>
    string TodoTitle(LlmToolCall tc)
    {
        var t = ParseTodoArgs(tc.Arguments);
        if (t == null) return "更新待办";
        switch (t.Action)
        {
            case "create":
                return $"创建-计划（{t.Steps?.Count ?? 0} 步）";
            case "update":
                var step = todoSteps != null && t.Index >= 1 && t.Index <= todoSteps.Count ? todoSteps[t.Index - 1] : "";
                if (t.Index >= 1)
                    return t.Done
                        ? $"计划步骤{t.Index}：{LLMClient.Trunc(step, 24)}"
                        : $"更新-计划步骤{t.Index}：{LLMClient.Trunc(step, 24)}";
                return t.Done ? "计划" : "更新-计划";
            case "done_all":
                return "全部完成-计划";
            default:
                return "更新待办";
        }
    }

    /// <summary>展示"计划步骤N"卡片：步骤文本含"修改/验证"类关键词时，该步骤之前的计划项全部视为已完成（批量补勾）；
    /// 否则自动完成上一步（N-1）。在思考条之前调用保证正确顺序</summary>
    void ShowStepStartCard(int stepIndex)
    {
        if (todoSteps == null || stepIndex < 1 || stepIndex > todoSteps.Count) return;
        currentStep = stepIndex;
        var text = todoSteps[stepIndex - 1];
        // 步骤文本含"修改/验证"类关键词 → 该步骤之前的计划项全部批量补勾（框架自动认为前置步骤已完成）；
        // 否则保持兜底：仅自动完成上一步（N-1）
        if (ContainsAny(text, ModifyStepKeywords) || ContainsAny(text, VerifyStepKeywords))
            AutoDoneUpTo(stepIndex - 1, $"进入计划步骤{stepIndex}：{LLMClient.Trunc(text, 12)}");
        else if (stepIndex > 1 && todoDoneSteps.Add(stepIndex - 1))
            Emit(new UiEvent { Type = UiEventType.Todo, Todo = new TodoUpdate { Action = "update", Index = stepIndex - 1, Done = true } });
        Emit(new UiEvent { Type = UiEventType.Todo, Todo = new TodoUpdate { Action = "step_start", Index = stepIndex, Text = text } });
        Log($"[StepStart] 计划步骤{stepIndex}：{LLMClient.Trunc(text, 24)}");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Flow 模式钩子（FlowRunner 调用）：框架接管建卡/打勾/工具过滤/步骤指令注入。
    // Flow 模式下计划归框架独占（UpdateTodo 不在白名单内），模型不管理清单，避免双源冲突。
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Flow 模式：框架建计划卡（替代模型 UpdateTodo(create)），步骤标签来自 Flow 模板</summary>
    public void TodoCreate(int count, List<string> labels)
    {
        todoSteps = labels;
        todoDoneSteps.Clear();
        currentStep = 1;
        pendingStepStart = 0;
        Emit(new UiEvent { Type = UiEventType.Todo, Todo = new TodoUpdate { Action = "create", Steps = labels } });
        Log($"[Flow] 框架建计划卡（{count} 步）");
    }

    /// <summary>Flow 模式：标记第 idx0（0 起）步完成，推进 currentStep 到下一步（幂等，重复调用只补一次）</summary>
    public void TodoStepDone(int idx0)
    {
        var i = idx0 + 1;
        if (todoDoneSteps.Add(i))
            Emit(new UiEvent { Type = UiEventType.Todo, Todo = new TodoUpdate { Action = "update", Index = i, Done = true } });
        currentStep = i + 1;
    }

    /// <summary>Flow 模式：全部步骤完成（流程收尾）</summary>
    public void TodoAllDone()
    {
        todoDoneSteps.Clear();
        if (todoSteps != null)
            for (int i = 1; i <= todoSteps.Count; i++) todoDoneSteps.Add(i);
        Emit(new UiEvent { Type = UiEventType.Todo, Todo = new TodoUpdate { Action = "done_all" } });
    }

    /// <summary>Flow 模式：注入步骤指令/打回/收尾用户消息（进入本轮历史）</summary>
    public void InjectUserStep(string text) => history.Add(new JsonObject { ["role"] = "user", ["content"] = text });

    /// <summary>按白名单过滤工具 schema（忽略大小写）。
    /// 注意：JsonNode.Add 会移动节点，不能把原 schema 节点直接加入新数组（会掏空原数组），需浅拷贝。</summary>
    JsonArray FilterSchemasByWhitelist(JsonArray all, List<string> allowed)
    {
        var allow = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);
        var arr = new JsonArray();
        foreach (var node in all)
            if (node is JsonObject o)
            {
                var name = o["function"]?["name"]?.GetValue<string>();
                if (name != null && allow.Contains(name))
                {
                    // 深拷贝：避免子节点仍持有原始 schema 的 Parent 引用，
                    // 否则 ChatStreamAsync 序列化时可能触发 "The node already has a parent"
                    var copy = (JsonObject)JsonNode.Parse(o.ToJsonString())!;
                    arr.Add(copy);
                }
            }
        return arr;
    }

    /// <summary>收尾补全：模型给出最终回复时，自动完成所有未标记完成的计划步骤</summary>
    void AutoCompleteRemainingSteps()
    {
        if (todoSteps == null) return;
        for (int i = 1; i <= todoSteps.Count; i++)
        {
            if (todoDoneSteps.Add(i))
            {
                Emit(new UiEvent { Type = UiEventType.Todo, Todo = new TodoUpdate { Action = "update", Index = i, Done = true } });
                Log($"[AutoTodo] 收尾补全计划步骤{i}：{LLMClient.Trunc(todoSteps[i - 1], 24)}");
            }
        }
    }

    /// <summary>修改/编译类工具动作 → 计划步骤关键词自动补勾：Write/Edit 执行时命中含"修改"类词的步骤、
    /// Bash 构建命令执行时命中含"验证"类词的步骤，将该步骤之前的计划项全部标记完成（幂等，重复触发只补新增项）。</summary>
    void AutoCheckStepsByTool(LlmToolCall tc)
    {
        if (todoSteps == null || todoSteps.Count == 0) return;
        if (tc.Name == "Write" || tc.Name == "Edit")
            AutoDonePrefixByKeyword(ModifyStepKeywords, tc.Name + " 修改动作");
        else if (tc.Name == "Bash" && IsBuildCommand(ExtractArg(tc.Arguments, "command")))
            AutoDonePrefixByKeyword(VerifyStepKeywords, "编译构建动作");
    }

    /// <summary>把清单中第一个含任一关键词的步骤之前的计划项全部标记完成（框架自动补勾）</summary>
    void AutoDonePrefixByKeyword(string[] kws, string reason)
    {
        if (todoSteps == null) return;
        for (int i = 0; i < todoSteps.Count; i++)
        {
            if (ContainsAny(todoSteps[i], kws))
            {
                AutoDoneUpTo(i, reason);
                return;
            }
        }
    }

    /// <summary>将计划步骤 1..lastIdx（0 基，不含自身）全部标记完成；幂等：已勾的不重复补发事件</summary>
    void AutoDoneUpTo(int lastIdx, string reason)
    {
        if (todoSteps == null) return;
        for (int k = 0; k < lastIdx; k++)
        {
            if (todoDoneSteps.Add(k + 1))
            {
                Emit(new UiEvent { Type = UiEventType.Todo, Todo = new TodoUpdate { Action = "update", Index = k + 1, Done = true } });
                Log($"[AutoTodo] {reason}：前置计划步骤{k + 1}自动打勾（{LLMClient.Trunc(todoSteps[k], 16)}）");
            }
        }
    }

    /// <summary>步骤文本是否含任一关键词（忽略大小写）</summary>
    static bool ContainsAny(string text, string[] kws)
    {
        foreach (var k in kws)
        {
            if (text.Contains(k, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Bash 命令是否为编译/构建类动作（dotnet build/test、mvn、gradle、javac、tsc、build.bat、中文"编译/构建"等）</summary>
    public static bool IsBuildCommand(string cmd)
    {
        if (cmd.Length == 0) return false;
        var s = cmd.ToLowerInvariant();
        return s.Contains("dotnet build") || s.Contains("dotnet publish") || s.Contains("dotnet test")
            || s.Contains("dotnet run") || s.Contains("mvn ") || s.Contains("gradle")
            || s.Contains("npm run build") || s.Contains("npm run test") || s.Contains("tsc ")
            || s.Contains("javac ") || s.Contains("go build") || s.Contains("make ")
            || s.Contains("build.bat") || s.Contains("编译") || s.Contains("构建");
    }

    /// <summary>会话内危险命令确认：挂起等待用户决策（允许重放 / 取消回退）。不自动超时拒绝——
    /// 用户点停止（任务取消）时经 _runCt 回调释放为拒绝；否则一直挂起等用户在会话内决策，
    /// 相当于"会话暂停"，配合钉钉提醒用户回来处理。服务宿主策略 Ask 模式直接复用本入口（SecurityAlert 事件经 Bus 出 SSE）。</summary>
    public async Task<bool> ConfirmDangerAsync(string cmd, string pattern)
    {
        // 自动任务上下文：危险命令无需人工确认，直接放行
        if (autoAllowDanger.Value) return true;

        if (dangerTcs != null) return false;   // 已有挂起中的确认：杜绝重复提示
        taskHadDanger = true;                  // 出现过危险确认窗：任务结束时需发钉钉提醒人工处理
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        dangerTcs = tcs;
        PendingDangerInfo = (cmd, pattern, curIntent);   // 挂起详情供服务宿主上报/历史重放过期过滤
        try
        {
            // 危险拦截挂起 → 钉钉提醒路由（视口弹卡不发/后台短静默/人不在兜底，同一挂起点只发一条；见 RunHangNotifierAsync）
            _ = SendDingTalkAlert(cmd, pattern, curIntent);
            Emit(new UiEvent
            {
                Type = UiEventType.SecurityAlert,
                Alert = new SecurityAlert
                {
                    Level = "Confirm",   // 等待用户确认：工具卡下方显示危险提示 + 允许/取消按钮
                    Command = cmd,
                    Pattern = pattern,
                    Message = "等待用户确认",
                    Intent = curIntent,
                }
            });
            // 永不自动超时拒绝：跟随两类取消——任务级 _runCt（用户点停止/宿主中止→TrySetResult(false) 释放挂起，
            // 使点停止能真正中断正挂起的危险确认，不依赖自动拒绝）与本轮工具阶段取消源（工具阶段硬超时会 Cancel 它；
            // 此前只挂 _runCt，工具超时救不了确认挂起，会撞上工具阶段的无界等待把 loop 永久卡住）；
            // 均无取消令牌的宿主（CLI 直跑）则纯等用户决策。
            var runCt = _runCt;
            var toolCts = curToolCts;
            using var regRun = runCt.CanBeCanceled ? runCt.Register(() => tcs.TrySetResult(false)) : default;
            using var regTool = toolCts != null ? toolCts.Token.Register(() => tcs.TrySetResult(false)) : default;
            return await tcs.Task;
        }
        finally
        {
            // 置空仅在无人引用后执行；取消回调捕获的是局部 tcs，不读字段，无竞态
            if (ReferenceEquals(dangerTcs, tcs)) { dangerTcs = null; PendingDangerInfo = null; }
        }
    }

    /// <summary>UI 决策入口：允许/拒绝当前挂起的危险命令（MainWindow 按钮点击调用）</summary>
    public void ResolveDanger(bool allow) => dangerTcs?.TrySetResult(allow);

    /// <summary>是否有挂起中的危险确认（服务宿主状态上报：pendingDecision）</summary>
    public bool DangerPending => dangerTcs != null;

    /// <summary>当前挂起中的危险确认详情（仅 DangerPending 时非空）：供服务宿主 resume 状态上报与
    /// 历史事件重放的过期过滤——只补送"仍挂起"的 SecurityAlert 帧，已决策的帧不再重放，避免手机端重进会话时重显旧卡。</summary>
    public (string Cmd, string Pattern, string? Intent)? PendingDangerInfo { get; private set; }

    /// <summary>UI / 外部审批入口：允许/拒绝当前挂起的计划（MainWindow / Server / CLI 调用）</summary>
    public void ResolvePlan(bool allow) => planTcs?.TrySetResult(allow);

    /// <summary>是否有挂起中的计划审批（服务宿主状态上报：pendingDecision 或 pendingPlan）</summary>
    public bool PlanPending => planTcs != null;

    /// <summary>当前挂起的计划（仅 PlanPending=true 时有效）</summary>
    public Plan? PendingPlan => pendingPlan;

    /// <summary>生成计划并等待外部审批。批准后返回 Plan；失败/拒绝/超时返回 null。</summary>
    async Task<Plan?> GenerateAndWaitPlanAsync(string userText, CancellationToken ct)
    {
        const string planPrompt = """
            请根据用户需求制定一份执行计划。只输出 JSON，不要解释。
            格式：
            {
              "summary": "一句话概述计划",
              "steps": [
                { "action": "read|edit|write|build|other", "target": "文件或对象", "description": "具体要做什么" }
              ]
            }
            """;
        var planMessages = new JsonArray();
        foreach (var msg in history)
            if (msg is not null) planMessages.Add(msg.DeepClone());
        planMessages.Add(new JsonObject { ["role"] = "system", ["content"] = planPrompt });

        Emit(new UiEvent { Type = UiEventType.WaitingModel });
        // 计划生成失败即整轮中止，代价高：LLMClient 内部瞬时网络重试之外，再做 1 次任务级重试（总 2 次尝试）；
        // 用户取消（OCE）不视为失败，原样上抛由 RunAsync 统一置"任务已取消"
        LlmResponse? resp = null;
        string? lastPlanErr = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            if (attempt > 1) Log($"[Plan] 生成计划失败，2秒后第{attempt}次重试：{lastPlanErr}");
            try { resp = await client.ChatAsync(planMessages, new JsonArray(), ct); break; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { lastPlanErr = ex.Message; }
            if (attempt == 1)
                try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { throw; }
        }
        if (resp == null)
        {
            Log($"[Plan] 生成计划失败：{lastPlanErr}");
            return null;
        }

        var plan = ParsePlanFromResponse(resp.Content ?? "");
        if (plan == null)
        {
            Log("[Plan] 模型返回无法解析为计划");
            return null;
        }

        plan.Id = TaskStore.NewId();
        plan.SessionId = SessionId;
        plan.Status = "pending";
        PlanStore.Save(cfg, plan);

        planTcs = new TaskCompletionSource<bool>();
        pendingPlan = plan;
        Emit(new UiEvent { Type = UiEventType.PlanPending, Plan = plan });
        Log($"[Plan] 已生成计划 {plan.Id}，等待审批…");
        _ = NotifyPlanPendingAsync(plan);   // 计划审批挂起通知（视口弹卡不发/后台短静默/人不在兜底，与危险确认同一套路由）

        try
        {
            using var cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts2.CancelAfter(TimeSpan.FromMinutes(30));
            bool approved;
            try { approved = await planTcs.Task.WaitAsync(cts2.Token); }
            catch (OperationCanceledException) { approved = false; }

            if (approved)
            {
                plan.Status = "approved";
                plan.DecidedAt = DateTime.Now;
                PlanStore.Save(cfg, plan);
                Log($"[Plan] 计划 {plan.Id} 已批准，继续执行");
                return plan;
            }
            else
            {
                plan.Status = "rejected";
                plan.DecidedAt = DateTime.Now;
                PlanStore.Save(cfg, plan);
                Log($"[Plan] 计划 {plan.Id} 被拒绝或超时");
                return null;
            }
        }
        finally
        {
            planTcs = null;
            pendingPlan = null;
        }
    }

    static Plan? ParsePlanFromResponse(string content)
    {
        var raw = content.Trim();
        if (raw.StartsWith("```"))
        {
            var start = raw.IndexOf('\n');
            var end = raw.LastIndexOf("```", StringComparison.Ordinal);
            if (start > 0 && end > start) raw = raw[start..end].Trim();
        }
        var json = raw.Trim();
        if (!json.StartsWith("{")) return null;
        try { return JsonSerializer.Deserialize<Plan>(json); }
        catch { return null; }
    }
}
