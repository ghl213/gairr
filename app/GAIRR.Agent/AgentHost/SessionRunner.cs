using System.Text.Json.Nodes;
using GAIRR.Core;

namespace GAIRR.AgentHost;

/// <summary>
/// 会话运行器（多前台会话方案 10）：实例级包装 AgentLoop，为桌面等宿主提供"每会话一个前台运行器"。
/// 与 AgentSession 的分工：AgentSession 自带全套组装（工具/插件/市场/LSP/项目工具）与会话持久化，供 CLI/Server 宿主；
/// SessionRunner 不重造组装栈——cfg/registry/journal/skills 由宿主注入（宿主共享同一注册表与技能目录即可），
/// 零 UI/存储依赖：任务事件经 Loop.Bus（UiEventBus，带会话归属 Key）流出，由宿主消费。
/// 危险确认按 DangerHub 同款模式：静态全局槽位挂本类路由委托 + AsyncLocal 标记"当前执行中的运行器"单播分发，
/// 多运行器并行互不串台；无运行器上下文时路由回退给"正在执行工具的宿主 loop 自证"，不顶掉宿主自建 loop（见 RouteDangerAsync）。
/// 宿主典型用法：
///   var runner = new SessionRunner("main", cfg, registry, journal, skills, "agent-deep.md");
///   // 可选自检：Debug.Assert(SessionRunner.RouteInstalled);
///   var r = await runner.StartAsync(new TaskRequest { Text = "……" });   // 危险命令挂起时 State=pendingDecision
///   runner.DecideDanger(allow: true);
/// </summary>
public sealed class SessionRunner
{
    /* ---------- 危险确认路由（DangerHub 模式：静态槽位 + AsyncLocal 单播分发） ---------- */

    static readonly AsyncLocal<SessionRunner?> current = new();

    /// <summary>本类确认路由：静态缓存单一委托实例（安装/自检按引用比较，避免方法组每次生成新委托）</summary>
    static readonly Func<string, string, Task<bool>> route = RouteDangerAsync;

    /// <summary>外部日志入口（宿主可注入写文件/控制台；null=静默）。</summary>
    public static Action<string>? Log;

    static bool warned;

    /// <summary>全局危险确认槽位当前是否由本类路由持有（宿主构造后自检/断言的入口）。</summary>
    public static bool RouteInstalled => ReferenceEquals(Phase1Tools.DangerConfirmHandler, route);

    /// <summary>
    /// 本类路由：分发到"正在执行任务的 SessionRunner"（AsyncLocal 单播，跨运行器确认请求不串台）。
    /// 无本类上下文（宿主自建 loop / 自动任务 loop / 遗留单 loop 等本进程内其他 loop 正在执行工具）时，
    /// 交还给正在执行工具的宿主 loop 自证——其 ConfirmDangerAsync 内部已处理 AutoAllowDangerScope 自动放行
    /// 与自身 Ask 挂起，等价于该 loop 当初自注册的效果：本类路由在位上也不顶掉任何宿主 loop。
    /// 无任何执行中 loop（纯后台/插件上下文）：按安全底线拒绝（Tools.cs:386 语义保留）。
    /// 注：与 DangerHub（AgentSession 体系）各占一个静态槽位、最后构造者持槽。两路由兜底现已同构收敛：
    /// 无各自 AsyncLocal 上下文（current）时都回退 AgentLoop.CurrentToolLoop 自证（见本方法 46-55 与
    /// AgentSession.DangerHub.HandleAsync），持槽者是谁不再影响分发正确性——多宿主并存（前台两会话主对话 +
    /// PlanRunner 叶子/审查会话 + 自动任务临时 loop + Server AutoAgent）危险确认互不串台、不回退被顶宿主。
    /// </summary>
    static async Task<bool> RouteDangerAsync(string cmd, string pattern)
    {
        var runner = current.Value;
        if (runner != null) return await runner.DecideDangerAsync(cmd, pattern);

        var loop = AgentLoop.CurrentToolLoop;
        if (loop != null) return await loop.ConfirmDangerAsync(cmd, pattern);

        return false;
    }

    /// <summary>进入运行器执行上下文：AsyncLocal 标记当前运行器（await 链自动流动，作用域结束恢复前值）。</summary>
    static IDisposable Scope(SessionRunner r)
    {
        var prev = current.Value;
        current.Value = r;
        return new ScopeToken(prev);
    }

    sealed class ScopeToken : IDisposable
    {
        readonly SessionRunner? prev;
        public ScopeToken(SessionRunner? p) => prev = p;
        public void Dispose() => current.Value = prev;
    }

    /* ---------- 运行器实例 ---------- */

    readonly AppConfig cfg;
    readonly object gate = new();
    CancellationTokenSource? cts;
    DateTime? taskStart;   // 当前任务开始时刻（ChangedFiles 时间窗起点，changelog 秒级精度；null=从未执行过任务）
    bool busy;

    public string Id { get; }
    public string ProjectRoot => cfg.ProjectRoot ?? "";

    /// <summary>本运行器的 Agent 主循环（宿主可直接消费 Bus 事件 / 读状态）。</summary>
    public AgentLoop Loop { get; }

    /// <summary>变更日志（注入或本类新建；GetResult 按时间窗聚合 ChangedFiles）。</summary>
    public ChangeJournal Journal { get; }

    /// <summary>运行器配置（宿主注入实例的引用，勿在运行期整体替换）。</summary>
    public AppConfig Config => cfg;

    /// <summary>任务事件总线（= Loop.Bus，事件带会话归属 Key，宿主按需消费）。</summary>
    public UiEventBus Bus => Loop.Bus;

    /// <summary>会话状态：idle=空闲 / busy=任务执行中 / pendingDecision=等待危险决策 / pendingPlan=等待计划审批。</summary>
    public string State => busy
        ? (Loop.PlanPending ? "pendingPlan" : (Loop.DangerPending ? "pendingDecision" : "busy"))
        : "idle";

    /// <summary>当前会话是否处于计划审批等待中。</summary>
    public bool PlanPending => Loop.PlanPending;

    /// <summary>当前挂起的计划（仅 State=pendingPlan 时有效）。</summary>
    public Plan? PendingPlan => Loop.PendingPlan;

    /// <summary>审批计划：true=继续执行；false=拒绝/停止。</summary>
    public void ResolvePlan(bool allow) => Loop.ResolvePlan(allow);

    /// <summary>
    /// 构造运行器：先挂本类路由再建 loop（AgentLoop 构造见 DangerConfirmHandler 非空自动跳过自注册），
    /// 构造结束后自检一次——若并发构造的 AgentSession 等宿主在此窗口覆盖了槽位，重挂本类路由并告警。
    /// 注意：registry 为宿主共享时本类不做工具白名单开关（按会话禁用会误伤其他运行器）；需要白名单的宿主应自建独立 registry 注入。
    /// </summary>
    public SessionRunner(string id, AppConfig cfg, ToolRegistry registry,
        ChangeJournal? journal = null, SkillLoader? skills = null, string promptFile = "agent-deep.md")
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("SessionRunner.Id 不能为空", nameof(id));
        Id = id;
        this.cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
        if (registry == null) throw new ArgumentNullException(nameof(registry));

        Phase1Tools.DangerConfirmHandler = route;   // 挂本类路由在前：避免本 loop 构造时自注册被后续覆盖产生窗口
        Journal = journal ?? new ChangeJournal(cfg);
        Loop = new AgentLoop(cfg, registry, Journal, skills, promptFile);
        Loop.SessionId = id;

        // 构造后自检：被并发宿主（如 AgentSession ctor 无条件挂 DangerHub）顶掉则重挂 + 一次性告警
        if (!RouteInstalled)
        {
            Phase1Tools.DangerConfirmHandler = route;
            WarnOnce($"SessionRunner({id})：构造时 DangerConfirmHandler 被其他宿主覆盖，已重挂本类路由");
        }
    }

    /// <summary>执行一个任务（内部 RunAsync；危险/计划挂起时由宿主经 DecideDanger/ResolvePlan 决策）。会话忙时抛异常。</summary>
    public async Task<TaskResult> StartAsync(TaskRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Text)) throw new ArgumentException("TaskRequest.Text 不能为空");
        lock (gate)
        {
            if (busy) throw new InvalidOperationException($"会话 {Id} 忙：上一个任务未结束（可 Cancel 或等待）");
            busy = true;
        }
        try
        {
            // 任务启动前重挂本类路由（可能被先构造的 AgentSession/PlanRunner 槽位顶掉），保证本次任务确认落到本运行器
            if (!RouteInstalled)
            {
                Phase1Tools.DangerConfirmHandler = route;
                WarnOnce($"SessionRunner({Id})：StartAsync 前检测到路由被其他宿主覆盖，已重挂");
            }
            Loop.Flow = req.Flow;   // Flow 模式：框架建计划卡并按步驱动（null=自主模式，行为不变）
            Loop.PlanMode = req.PlanMode;
            taskStart = DateTime.Now;
            cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                using var scope = Scope(this);   // 本任务全程标记当前运行器（AsyncLocal，await 链自动流动）
                await Loop.RunAsync(req.Text, cts.Token);
            }
            catch (OperationCanceledException)
            {
                Loop.Emit(new UiEvent { Type = UiEventType.Failed });   // 走统一出口：自动附带会话 Key（若 Loop 已 SetSessionKey）
            }
            return GetResult();
        }
        finally
        {
            lock (gate) busy = false;
            cts?.Dispose();
            cts = null;
        }
    }

    /// <summary>取消当前任务（RunAsync 收到取消后发 Failed 事件收尾）。</summary>
    public void Cancel() => cts?.Cancel();

    /// <summary>危险决策入口（Ask 策略下宿主 UI 经此决策；Allow/Deny 策略下由策略决策器自动处理）。</summary>
    public void DecideDanger(bool allow) => Loop.ResolveDanger(allow);

    /// <summary>危险策略决策（路由分发入口，语义与 AgentSession.DecideDangerAsync 对齐）：Allow=放行 / Deny=拒绝 / Ask=挂起等宿主决策。</summary>
    internal async Task<bool> DecideDangerAsync(string cmd, string pattern) =>
        SystemCfg.DangerPolicy.ToUpperInvariant() switch
        {
            "ALLOW" => true,
            "DENY" => false,
            _ => await Loop.ConfirmDangerAsync(cmd, pattern),   // Ask：SecurityAlert 事件经 Bus 出给宿主；不自动超时，随任务取消令牌释放
        };

    /// <summary>任务结果汇总：最后助手回复 + changelog 时间窗内修改文件 + 累计 token + 会话标题。</summary>
    public TaskResult GetResult()
    {
        var files = new List<string>();
        if (taskStart is DateTime ts)   // 从未执行过任务：不出任何 ChangedFiles（避免默认时刻穿透全量历史）
        {
            var logPath = Path.Combine(Journal.BackupDir, "changelog.jsonl");
            if (File.Exists(logPath))
                foreach (var line in File.ReadLines(logPath))
                {
                    try
                    {
                        if (JsonNode.Parse(line) is not JsonObject rec) continue;
                        var t = rec["time"]?.GetValue<string>() ?? "";
                        if (string.CompareOrdinal(t, ts.ToString("yyyy-MM-dd HH:mm:ss")) < 0) continue;
                        var f = rec["file"]?.GetValue<string>() ?? "";
                        if (f.Length > 0 && !files.Contains(f)) files.Add(f);
                    }
                    catch { }
                }
        }
        return new TaskResult
        {
            Reply = Loop.GetLastAssistantMessage() ?? "",
            ChangedFiles = files,
            Tokens = Loop.LastTaskTokens,
            Title = Loop.SessionTitle,
        };
    }

    /// <summary>一次性告警（同进程只提示一次，避免反复重挂时刷屏）。</summary>
    static void WarnOnce(string msg)
    {
        if (warned) return;
        warned = true;
        Log?.Invoke(msg);
    }
}
