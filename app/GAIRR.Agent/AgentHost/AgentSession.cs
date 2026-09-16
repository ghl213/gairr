using System.Text.Json.Nodes;
using GAIRR.Core;
using GAIRR.Core.Lsp;

namespace GAIRR.AgentHost;

/// <summary>
/// 多会话共享的危险确认路由：任务执行期间按 AsyncLocal 标记"当前执行会话"，
/// 静态委托（Phase1Tools.DangerConfirmHandler）收到确认请求后分发到发起会话的策略决策器——
/// 避免多会话并存时危险确认为多播串台（各会话只决策自己的确认）。
/// </summary>
static class DangerHub
{
    static readonly AsyncLocal<AgentSession?> current = new();

    /// <summary>进入会话执行上下文（StartAsync 期间持有，await 链自动流动）</summary>
    public static IDisposable Scope(AgentSession s)
    {
        var prev = current.Value;
        current.Value = s;
        return new Token(prev);
    }

    sealed class Token : IDisposable
    {
        readonly AgentSession? prev;
        public Token(AgentSession? p) => prev = p;
        public void Dispose() => current.Value = prev;
    }

    public static async Task<bool> HandleAsync(string cmd, string pattern)
    {
        var s = current.Value;
        if (s != null) return await s.DecideDangerAsync(cmd, pattern);
        // 无本类会话上下文：交还给正在执行工具的宿主 loop 自证（与 SessionRunner.RouteDangerAsync 兜底同构，两槽位收敛）。
        // 本类在各宿主（编排叶子/审查/Server AutoAgent）ctor 无条件占全局槽，会顶掉更早自注册的宿主——如
        // MainWindow 两会话前台主对话直跑 RunAsync（不经 AgentSession/SessionRunner）与自动任务临时 loop。
        // 危险确认请求发生在其宿主 loop 的工具执行上下文内，CurrentToolLoop 即正确宿主：其 ConfirmDangerAsync
        // 已处理 AutoAllowDangerScope 自动放行与自身 Ask 挂起（SecurityAlert 经 loop.Bus 出 UI），等价该 loop
        // 当初自注册的效果——避免多宿主并存时确认被静默拒绝成 false（主对话危险被拦无提示、自动任务放行变拒绝）。
        var loop = AgentLoop.CurrentToolLoop;
        if (loop != null) return await loop.ConfirmDangerAsync(cmd, pattern);
        return false;   // 纯后台/插件上下文（无任何执行中 loop）：按安全底线拒绝
    }
}

/// <summary>
/// 一个会话的运行期：独立 AgentLoop + 独立 AppConfig 实例（多会话可并行，互不串扰）。
/// 构造序列与 MainWindow 同构（工具/插件/LSP 注册，零 UI 依赖）；提供任务级 API
/// Start/Cancel/DecideDanger/DrainEvents/GetResult 供 gairr-cli 与 gairr-agent-server 共用。
/// </summary>
public class AgentSession
{
    readonly AppConfig cfg;
    readonly ToolRegistry registry = new();
    SkillLoader skillLoader = null!;   // 在 RegisterProjectTools 中创建（与 MainWindow 组装序同）
    readonly object gate = new();
    readonly SessionRecord record;

    /// <summary>会话存储（CLI/Server 可直访：恢复历史、列会话等）</summary>
    public SessionStore Store { get; }

    CancellationTokenSource? cts;
    DateTime? taskStart;   // 当前任务开始时刻（ChangedFiles 时间窗起点，changelog 秒级精度；null=从未执行过任务）
    long roundStartSeq;    // 本轮任务开始时总线累计帧数（SSE 回放起点：只补本轮，不重放上一轮的收口帧）
    bool busy;
    /// <summary>当前是否有任务在跑（hub 空闲驱逐判定时跳过 busy 会话）</summary>
    public bool Busy => busy;
    string? taskId;

    /// <summary>最近一次活跃时刻（任务启动/结束、会话创建/恢复时刷新；Server 空闲驱逐用）</summary>
    public DateTime LastActiveUtc { get; private set; } = DateTime.Now;

    /// <summary>当前是否有任务在执行（含挂起决策态由 State 表达；驱逐仅针对 idle）</summary>
    public bool IsBusy { get { lock (gate) return busy; } }

    /// <summary>本轮任务在事件总线上的起点序号（仅 IsBusy=true 时有意义）：
    /// Server 的 /events 用它做回放起点，只补发本轮已产生的帧，不重放上一轮的 Finished/Failed。</summary>
    public long RoundStartSeq { get { lock (gate) return roundStartSeq; } }

    /// <summary>距最近一次活跃的分钟数（执行中视为 0；与 LastActiveUtc 同用本地时钟，Server hub 空闲驱逐用）</summary>
    public double IdleMinutesUtc
    {
        get
        {
            lock (gate) return busy ? 0 : (DateTime.Now - LastActiveUtc).TotalMinutes;
        }
    }

    /// <summary>会话归属用户（多用户 Server：会话创建/认领时经 SetOwner 写入 Meta["owner"] 落盘；空=旧会话未归属）</summary>
    public string Owner => MetaVal("owner");

    /// <summary>归属标记：立即进内存记录并落盘（创建会话、resume 认领旧会话时调用）</summary>
    public void SetOwner(string user) => SaveMeta(("owner", user));

    /* ---------- 会话执行参数：角色模式 + 模型（手机端详情页"切换角色模型"，随会话记录 Meta 持久化） ---------- */

    /// <summary>本会话锁定的角色 name（空=未指定，走引擎默认提示词）</summary>
    public string RoleName { get; private set; } = "";

    /// <summary>本会话锁定的模式 name（空=未指定，自主模式）</summary>
    public string ModeName { get; private set; } = "";

    /// <summary>锁定模式引用的 Flow 模板：StartAsync 未显式传 flow 时按它建计划卡（角色包私有流回退共享池）</summary>
    FlowTemplate? pinnedFlow;

    public string Id { get; }
    public string ProjectRoot { get; }
    public AgentLoop Loop { get; }
    public ChangeJournal Journal { get; }

    /// <summary>会话配置（供 Server/CLI 创建任务、计划存储等）</summary>
    public AppConfig Config => cfg;

    /// <summary>会话状态：idle=空闲 / busy=任务执行中 / pendingDecision=等待危险决策 / pendingPlan=等待计划审批</summary>
    public string State => busy
        ? (Loop.PlanPending ? "pendingPlan" : (Loop.DangerPending ? "pendingDecision" : "busy"))
        : "idle";

    /// <summary>当前会话是否处于计划审批等待中。</summary>
    public bool PlanPending => Loop.PlanPending;

    /// <summary>当前挂起的计划（仅 State=pendingPlan 时有效）。</summary>
    public Plan? PendingPlan => Loop.PendingPlan;

    /// <summary>审批计划：true=继续执行；false=拒绝/停止。</summary>
    public void ResolvePlan(bool allow) => Loop.ResolvePlan(allow);

    public AgentSession(string id, string projectRoot, string? configPath = null, string promptFile = "agent-deep.md")
    {
        Id = id;
        ProjectRoot = Path.GetFullPath(projectRoot);
        // 浅拷：每会话独立 AppConfig 实例（ProjectRoot/模型参数可并行覆盖，不静态串扰）
        cfg = string.IsNullOrEmpty(configPath) ? new AppConfig() : new AppConfig(configPath);
        cfg.ProjectRoot = ProjectRoot;

        // 组装序列（与 MainWindow 构造同构）：
        SystemCfg.Init();                               // system.ini 集中配置（幂等，首跑生成模板）
        Phase1Tools.RegisterAll(registry, cfg);         // Bash/Read/Write/Edit/Grep/Glob/ListDir/DbQuery 等
        Journal = new ChangeJournal(cfg);
        Phase2Tools.RegisterAll(registry, cfg, Journal);
        RegisterProjectTools();                         // Map/MapAuto/MapTrace/MapSlice/FindRefs/LoadSkill/SmartSearch
        PluginLoader.Load(registry, cfg);
        PluginLoader.Watch(registry, cfg);   // 插件热加载：plugins 目录 ini 变化自动重扫重注册，模型当轮写好的插件当轮即可调用
        MarketTools.RegisterAll(registry, cfg);   // 市场工具：MarketList/MarketInstall/MarketUninstall（CLI/Server 会话同样可用）
        GitHubTools.RegisterAll(registry, cfg);   // GitHub 技能源：GhSearch/GhInstallSkill/GhUninstall（CLI/Server 会话同样可用）
        Loop = new AgentLoop(cfg, registry, Journal, skillLoader, promptFile);
        Loop.SessionId = id;
        LspManager.Init(cfg, Loop.Bus);                 // 幂等单例：首个会话负责初始化，多项目切换留后续
        LspManager.Instance?.Probe();

        // 危险确认单播路由：挂全局 Hub（AsyncLocal 分发到发起会话）；AgentLoop 构造已识别非空跳过自注册
        Phase1Tools.DangerConfirmHandler = DangerHub.HandleAsync;

        // 会话存储：恢复本身份分区（namespace = 项目路径归一化）的历史
        Store = new SessionStore(cfg, ProjectRoot);
        var ns = SessionStore.NormalizeNamespace(ProjectRoot);
        record = Store.Load(ns, Id) ?? new SessionRecord
        {
            Id = Id,
            Namespace = ns,
            Project = ProjectRoot,
            CreatedAt = DateTime.Now.ToString("s"),
            UpdatedAt = DateTime.Now.ToString("s"),
        };
        if (record.Messages is { Count: > 0 })
            Loop.LoadHistory(record.Messages.Select(m => (m.Role, m.Content)));
        if (record.Title.Length > 0) Loop.SetSessionTitle(record.Title);
        RestoreExecParams();   // 恢复本会话锁定的角色模式/模型（resume、服务重启后仍生效）
    }

    /// <summary>执行一个任务（内部 RunAsync）。会话忙时抛异常：可 Cancel/等待后重试，或另开会话。</summary>
    public async Task<TaskResult> StartAsync(TaskRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Text)) throw new ArgumentException("TaskRequest.Text 不能为空");
        lock (gate)
        {
            if (busy) throw new InvalidOperationException($"会话 {Id} 忙：上一个任务未结束（可 Cancel 或等待）");
            busy = true;
            roundStartSeq = Loop.Bus.Posted;   // 本轮起点：SSE 只回放这之后的帧（上一轮 Finished 不再重放）
        }
        try
        {
            ApplyToolWhitelist(req.Tools);
            // Flow 模式：请求显式给了 flow 就用它；否则沿用本会话锁定角色模式的引用流（手机端详情页切换后逐轮生效）
            Loop.Flow = req.Flow ?? pinnedFlow?.Steps;
            Loop.PlanMode = req.PlanMode;
            taskStart = DateTime.Now;
            taskId = Guid.NewGuid().ToString("N")[..8];
            LastActiveUtc = DateTime.Now;
            SyncRecord(status: "busy");

            cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                using var scope = DangerHub.Scope(this);
                await Loop.RunAsync(req.Text, cts.Token);
            }
            catch (OperationCanceledException)
            {
                Loop.Emit(new UiEvent { Type = UiEventType.Failed });   // 走统一出口：自动附带会话 Key（若 Loop 已 SetSessionKey）
            }
            finally
            {
                SyncRecord(status: "idle");   // 结束即落盘：对话历史 + 状态（异常/崩溃前也不丢已完成轮次）
            }
            return GetResult();
        }
        finally
        {
            lock (gate) busy = false;
            LastActiveUtc = DateTime.Now;
            cts?.Dispose();
            cts = null;
        }
    }

    /// <summary>工具白名单：先全部启用，再按清单禁用（null/空 = 全部启用），最后叠加全局 [Tools] Disabled 黑名单</summary>
    void ApplyToolWhitelist(List<string>? tools)
    {
        foreach (var name in registry.ToolNames) registry.SetEnabled(name, true);
        if (tools is { Count: > 0 })
            foreach (var name in registry.ToolNames)
                if (!tools.Contains(name, StringComparer.OrdinalIgnoreCase))
                    registry.SetEnabled(name, false);
        // 全局工具开关（system.ini [Tools] Disabled，UI 切换写回）：请求白名单不能"复活"已禁用工具
        foreach (var name in SwitchStore.ToolsDisabled())
            registry.SetEnabled(name, false);
    }

    /// <summary>取消当前任务（RunAsync 收到取消后发 Failed 事件收尾）</summary>
    public void Cancel() => cts?.Cancel();

    /// <summary>危险决策入口（Ask 策略下客户端经 /decisions 调用；Allow/Deny 策略下由策略决策器自动处理）</summary>
    public void DecideDanger(bool allow) => Loop.ResolveDanger(allow);

    /// <summary>危险策略决策（DangerHub 分发入口）：Allow=放行 / Deny=拒绝 / Ask=挂起等外部决策</summary>
    internal async Task<bool> DecideDangerAsync(string cmd, string pattern) =>
        SystemCfg.DangerPolicy.ToUpperInvariant() switch
        {
            "ALLOW" => true,
            "DENY" => false,
            _ => await Loop.ConfirmDangerAsync(cmd, pattern),   // Ask：SecurityAlert 事件出 SSE + 5 分钟超时默认拒绝
        };

    /// <summary>取走全部待发事件（SSE/CLI 轮询用；映射为 JSON 友好 DTO）—— 向后兼容单客户端模式</summary>
    public List<SessionEventDto> DrainEvents() => Loop.Bus.Drain()
        .Select(UiEventMapper.ToDto).ToList();

    /// <summary>订阅实时事件广播（支持多客户端同时接收）。历史重放对 SecurityAlert 帧做过期过滤：
    /// 只补送"当前仍挂起"的那一张（与 Loop.PendingDangerInfo 匹配），已决策的旧帧不再重放——
    /// 避免手机端重进会话时把已点击过的危险卡重显出来（客户端仅靠墓碑无法跨重进判重）。
    /// 本重载不回放任何历史帧（等价 sinceSeq=null）。</summary>
    public IDisposable SubscribeEvents(Action<SessionEventDto> handler) => SubscribeEvents(handler, null);

    /// <summary>同上，并可指定回放起点：sinceSeq=总线累计序号（取自 RoundStartSeq），只回放它之后的帧；
    /// null=完全不回放，只发订阅之后的实时帧。Server 的 /events 靠它避免重放上一轮的 Finished/Failed——
    /// 那一帧会让新连接刚建好就被判定任务已收口而立即关闭（前端表现为反复重连）。</summary>
    public IDisposable SubscribeEvents(Action<SessionEventDto> handler, long? sinceSeq)
    {
        var pend = Loop.PendingDangerInfo;
        // 先推送历史事件（sinceSeq=null 时为空集：只发实时帧）
        foreach (var e in sinceSeq == null ? new List<UiEvent>() : Loop.Bus.GetHistorySince(sinceSeq.Value))
        {
            if (e.Type == UiEventType.SecurityAlert)
            {
                if (pend == null) continue;   // 无挂起确认：历史帧均为已决策，跳过
                if (e.Alert == null || e.Alert.Command != pend.Value.Cmd || e.Alert.Pattern != pend.Value.Pattern) continue;
            }
            handler(ToDto(e));
        }
        // 订阅新事件
        return Loop.Bus.Subscribe(e => handler(ToDto(e)));
    }

    /// <summary>当前挂起中的危险确认详情（pendingDecision 状态上报用，无挂起返回 null）：
    /// 客户端 resume 拿到后可直接建卡，不依赖事件重放是否还带该帧。</summary>
    public SecurityAlert? CurrentPendingDanger
    {
        get
        {
            var p = Loop.PendingDangerInfo;
            if (p == null) return null;
            return new SecurityAlert { Level = "Confirm", Command = p.Value.Cmd, Pattern = p.Value.Pattern, Message = "等待用户确认", Intent = p.Value.Intent };
        }
    }

    static SessionEventDto ToDto(UiEvent e) => new()
    {
        Type = e.Type.ToString(),
        Round = e.Round,
        Tool = e.Tool,
        Delta = e.Delta,
        Start = e.Start,
        IsFinal = e.IsFinal,
        Todo = e.Todo,
        Alert = e.Alert,
        Intent = e.Intent,
        Reasoning = e.Reasoning,
        TotalTokens = e.TotalTokens,
        LogLine = e.LogLine,
        Usage = e.Usage,
        Plan = e.Plan,
    };

    /// <summary>任务结果汇总：最后助手回复 + changelog 时间窗内修改文件 + 累计 token</summary>
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

    /// <summary>同步会话记录并落盘（状态/标题/对话历史）</summary>
    void SyncRecord(string status)
    {
        record.Status = status;
        record.Title = Loop.SessionTitle.Length > 0 ? Loop.SessionTitle : record.Title;
        record.UpdatedAt = DateTime.Now.ToString("s");
        record.Messages = Loop.DumpHistory().Select(m => new SessionMessage { Role = m.Role, Content = m.Content }).ToList();
        Store.Save(record);
    }

    /* ---------- 角色模式 / 模型切换（手机端详情页"切换角色模型"面板；会话级，不串扰其它会话） ---------- */

    /// <summary>当前执行参数快照：角色模式显示名 + 当前厂商/模型 + 一行摘要（面板回显与顶栏提示用）</summary>
    public ExecParams ExecSnapshot()
    {
        var role = RoleName.Length > 0 ? RoleModeStore.FindRole(RoleName) : null;
        var mode = ModeName.Length > 0 ? RoleModeStore.FindMode(RoleName, ModeName) : null;
        var roleDisp = role == null ? "" : Labeled(role.Icon, role.DisplayName.Length > 0 ? role.DisplayName : role.Name);
        var modeDisp = mode == null ? "" : Labeled(mode.Icon, mode.DisplayName.Length > 0 ? mode.DisplayName : mode.Name);
        var prov = Loop.Provider;
        var provDisp = cfg.ProviderDisplayName(prov);
        return new ExecParams
        {
            Role = RoleName,
            RoleDisplay = roleDisp,
            Mode = ModeName,
            ModeDisplay = modeDisp,
            Provider = prov,
            ProviderDisplay = provDisp,
            Model = Loop.ModelName,
            Summary = string.Join(" · ", new[] { roleDisp, modeDisp, provDisp, Loop.ModelName }.Where(s => s.Length > 0)),
        };
    }

    /// <summary>切换本会话执行参数：role+mode 成对（切提示词/锁定引用流），provider+model 成对（切本会话 LLM 客户端）；
    /// 任一组留空=该项保持不变。返回错误说明，null=成功；成功后写入会话记录 Meta（resume/服务重启后仍生效）。</summary>
    public string? SetExecParams(string? role, string? mode, string? provider, string? model)
    {
        var wantRole = !string.IsNullOrWhiteSpace(role) || !string.IsNullOrWhiteSpace(mode);
        var wantModel = !string.IsNullOrWhiteSpace(provider) || !string.IsNullOrWhiteSpace(model);
        if (!wantRole && !wantModel) return "role/mode 与 provider/model 至少给一组";
        if (wantRole && (string.IsNullOrWhiteSpace(role) || string.IsNullOrWhiteSpace(mode))) return "role 与 mode 必须同时给出";
        if (wantModel && (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model))) return "provider 与 model 必须同时给出";

        if (wantModel)
        {
            var err1 = ApplyModel(provider!.Trim(), model!.Trim(), persist: true);
            if (err1 != null) return err1;
        }
        if (wantRole)
        {
            var err2 = ApplyRoleMode(role!.Trim(), mode!.Trim(), persist: true);
            if (err2 != null) return err2;
        }
        return null;
    }

    /// <summary>应用角色模式：会话内切提示词（不动全局 SystemCfg.AgentMode，避免串扰其它会话/桌面端）+ 锁定引用流</summary>
    string? ApplyRoleMode(string role, string mode, bool persist)
    {
        var m = RoleModeStore.FindMode(role, mode);
        if (m == null) return $"角色模式不存在：{role} · {mode}";
        var pf = RoleModeStore.ResolvePromptFile(m);
        // 内置敏捷/深度模式常不写 promptFile：按模式名回落到工具级提示词，等效桌面端 SwitchAgentMode 但不改全局
        if (pf.Length == 0 && mode is "agile" or "deep")
            pf = mode == "deep" ? "agent-deep.md" : "agent-agile.md";
        if (pf.Length > 0) Loop.SwitchPrompt(pf);
        pinnedFlow = RoleModeStore.ResolveFlow(m);
        if (!busy) Loop.Flow = pinnedFlow?.Steps;   // 执行中不改在跑的 flow，下一轮 StartAsync 自动带上
        RoleName = role;
        ModeName = mode;
        if (persist) SaveMeta(("role", role), ("mode", mode));
        return null;
    }

    /// <summary>应用模型：校验在 config.ini 可选清单内且厂商已配 ApiKey，再切本会话 LLM 客户端（含思考强度规则）</summary>
    string? ApplyModel(string provider, string model, bool persist)
    {
        var opt = cfg.ModelOptions().FirstOrDefault(o =>
            string.Equals(o.Provider, provider, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(o.ModelId, model, StringComparison.OrdinalIgnoreCase));
        if (opt == null) return $"模型不在可选清单：{provider}/{model}";
        if (!opt.HasKey) return $"厂商 {opt.ProviderDisplay} 未配置 ApiKey，该模型不可用";
        try { Loop.SwitchModelFull(provider, model); }
        catch (Exception ex) { return $"切换模型失败：{ex.Message}"; }
        if (persist) SaveMeta(("provider", provider), ("model", model));
        return null;
    }

    /// <summary>从会话记录 Meta 恢复角色模式/模型：角色包被删或模型配置变更导致失效时静默忽略（仍用引擎默认）</summary>
    void RestoreExecParams()
    {
        var prov = MetaVal("provider");
        var model = MetaVal("model");
        if (prov.Length > 0 && model.Length > 0) ApplyModel(prov, model, persist: false);
        var role = MetaVal("role");
        var mode = MetaVal("mode");
        if (role.Length > 0 && mode.Length > 0) ApplyRoleMode(role, mode, persist: false);
    }

    string MetaVal(string key) => record.Meta != null && record.Meta.TryGetValue(key, out var v) ? v ?? "" : "";

    /// <summary>执行参数写入会话记录 Meta 并落盘（角色模式/模型锁定随会话持久化）</summary>
    void SaveMeta(params (string Key, string Value)[] kv)
    {
        record.Meta ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in kv) record.Meta[k] = v;
        record.UpdatedAt = DateTime.Now.ToString("s");
        try { Store.Save(record); }
        catch (Exception ex) { Console.WriteLine($"[Exec] 会话执行参数落盘失败：{ex.Message}"); }
    }

    /// <summary>"图标 显示名"拼接（图标缺省时只留显示名）</summary>
    static string Labeled(string icon, string display) =>
        string.IsNullOrWhiteSpace(icon) ? display.Trim() : (icon.Trim() + " " + display.Trim()).Trim();

    /* ---------- 项目工具注册（与 MainWindow 构造同构；DocStatus 事件经 Bus 出 SSE，不进对话流） ---------- */

    void RegisterProjectTools()
    {
        registry.Register("Map", ToolRegistry.Fn(
            "Map",
            SystemCfg.Desc("Map",
                "获取项目目录结构与各文件功能摘要（自动提取文件头注释和顶层声明，.gairr/map.json 缓存可手工补充语义描述）。任务开始先调用它了解项目全貌，再精准定位文件，避免盲目扫目录/逐个读文件。"),
            new JsonObject
            {
                ["path"] = ToolRegistry.Str("聚焦的子目录或文件路径（相对项目根），可为空（空=整个项目）"),
                ["refresh"] = new JsonObject { ["type"] = "boolean", ["description"] = "true=强制重建所有自动摘要（默认 false 增量更新）" },
            },
            new JsonArray()),
            (a, ct) => Task.FromResult(ProjectMap.Build(cfg,
                a["path"]?.GetValue<string>(),
                a["refresh"]?.GetValue<bool>() ?? false)));
        registry.Register("MapAuto", ToolRegistry.Fn(
            "MapAuto",
            SystemCfg.Desc("MapAuto",
                "运行项目地图自动化：增量更新项目地图、刷新文件级调用图谱、为缺注释文件/方法调大模型补说明（写 .gairr/notes.json 不改源码）、首次生成架构/功能文档（结构变化时按旧文增量刷新）。默认后台执行，进度见 /events 的 docStatus。"),
            new JsonObject
            {
                ["wait"] = new JsonObject { ["type"] = "boolean", ["description"] = "true=等待执行完成并返回全程进度（默认 false 立即返回，后台执行）" },
            },
            new JsonArray()),
            async (a, ct) =>
            {
                if (!a["wait"]?.GetValue<bool>() ?? false)
                {
                    // 后台火并忘：同项目 10 秒内重复调度自动跳过（与启动/切项目自动触发共用节流）
                    ProjectMapAuto.Schedule(cfg, OnDocStatus);
                    return "项目地图自动化已后台启动（同项目 10 秒内重复调用会跳过；进度见 /events 的 docStatus 事件）";
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
                "按符号名检索代码：输入类名/方法名/文件名（支持部分匹配），返回定义位置（文件:行号）与引用文件列表。命中 2 个以上符号时自动附带各符号首定义完整代码块（auto 可调），一次拿全上下文。也支持中文查询：按文件头注释/方法注释里的中文标签定位（如 q=备份、危险命令）。找代码定义优先用 MapTrace，比 Grep 语义更准、比 Map 更细。"),
            new JsonObject
            {
                ["q"] = ToolRegistry.Str("符号名：类名/方法名/文件名（支持部分匹配，如 AgentLoop、BuildAsync）"),
                ["def"] = new JsonObject { ["type"] = "boolean", ["description"] = "true=只返回定义位置不问代码块（默认 false）" },
                ["max"] = ToolRegistry.Int("最多返回符号数，默认 15"),
                ["auto"] = ToolRegistry.Int("自动附带完整代码块的符号数（每符号取首个定义；0=关闭，默认 3；命中不足时按实际）"),
            },
            new JsonArray { "q" }),
            (a, ct) =>
            {
                var outText = SymbolIndex.Run(cfg,
                    a["q"]?.GetValue<string>() ?? "",
                    a["max"]?.GetValue<int>() ?? 15,
                    a["def"]?.GetValue<bool>() ?? false);
                if (!(a["def"]?.GetValue<bool>() ?? false) && outText.StartsWith("符号索引："))
                {
                    var auto = a["auto"]?.GetValue<int>() ?? 3;
                    if (auto > 0)
                    {
                        var specs = GetDefSpecs(outText, auto);
                        if (specs.Count >= 2)
                            outText += "\n【自动切片 " + specs.Count + " 个符号定义】\n" + SliceExtractor.Run(cfg, string.Join(",", specs), false);
                        else if (specs.Count == 1)
                            outText += "\n【自动切片】\n" + SliceExtractor.Run(cfg, specs[0], false);
                    }
                }
                return Task.FromResult(outText);
            });
        registry.Register("MapSlice", ToolRegistry.Fn(
            "MapSlice",
            SystemCfg.Desc("MapSlice",
                "按 MapTrace 定位结果直接提取方法/类完整代码块（自动含前置注释与块边界）：spec 形如 \"Core/Tools.cs:165\"、\"Tools.cs:165-185\"、\"Tools.cs:Read\"（符号名），多段逗号分隔（≤5）。定位到行号后用它一次拿到源码，比 Read 猜范围更快更省。chain=1 时自动附带该符号被引用处的上下文（所属方法+引用行±3行），跨文件修改/调用关系时用它一次拿全，免去多次 MapTrace+Read 往返。"),
            new JsonObject
            {
                ["spec"] = ToolRegistry.Str("切片定位：文件:起始行 或 文件:起-止 或 文件:符号名，多段逗号分隔，如 Core/Tools.cs:165, WriteTools.cs:Grep"),
                ["chain"] = new JsonObject { ["type"] = "boolean", ["description"] = "true=附带主块符号的被引用处上下文（跨文件调用链），默认 false" },
            },
            new JsonArray { "spec" }),
            (a, ct) => Task.FromResult(SliceExtractor.Run(cfg, a["spec"]?.GetValue<string>() ?? "", a["chain"]?.GetValue<bool>() ?? false)));
        registry.Register("FindRefs", ToolRegistry.Fn(
            "FindRefs",
            SystemCfg.Desc("FindRefs",
                "编译器级引用检索：查询符号（方法/类/字段）的全部真实引用位置（含定义位置），返回 文件:行:上下文。支持 .cs（Roslyn）/ .java（jdtls）/ .vue/.ts/.js（volar），按文件扩展名自动选语言后端，首次调用某语言会自动下载运行环境（约 1~5 分钟）。能区分重载/同名符号、只算真实调用，比 MapTrace 文本搜索精确。file 与 line 先用 MapTrace 定位获得。改动公共方法/类之前先查它确认影响范围，避免改坏调用方。"),
            new JsonObject
            {
                ["file"] = ToolRegistry.Str("符号所在文件（相对项目根，如 Core/AgentLoop.cs）"),
                ["line"] = ToolRegistry.Int("符号所在行号（1 起，MapTrace 返回的 :行号）"),
                ["symbol"] = ToolRegistry.Str("符号名（类名/方法名/字段名），用于精确定位列，可为空"),
                ["max"] = ToolRegistry.Int("最多返回引用数，默认 20"),
            },
            new JsonArray { "file", "line" }),
            async (a, ct) => await (LspManager.Instance?.FindReferencesAsync(
                a["file"]?.GetValue<string>() ?? "",
                a["line"]?.GetValue<int>() ?? 1,
                a["symbol"]?.GetValue<string>() ?? "",
                a["max"]?.GetValue<int>() ?? 20,
                ct) ?? Task.FromResult("错误：语言服务器未初始化（重启应用后重试）")));
        skillLoader = new SkillLoader(cfg);
        skillLoader.Watch();   // 技能热加载：skills/*.md 变化自动重扫，下个任务清单可见
        registry.Register("LoadSkill", ToolRegistry.Fn(
            "LoadSkill",
            SystemCfg.Desc("LoadSkill",
                "加载指定技能的完整指令。当任务与 system prompt 技能清单中某技能描述匹配、或用户用 /技能名 强制触发时，先调本工具取全文再按技能步骤执行。"),
            new JsonObject { ["name"] = ToolRegistry.Str("技能名称") },
            new JsonArray { "name" }),
            (a, ct) => Task.FromResult(skillLoader.LoadFull(a["name"]?.GetValue<string>() ?? "")));
        registry.Register("SmartSearch", ToolRegistry.Fn(
            "SmartSearch",
            SystemCfg.Desc("SmartSearch",
                "智能语义检索：支持中文查询（如 q=数据持久化），按四层体系匹配（历史关联积累→代码注释中文标签→中英规则词典→文件摘要），返回相关符号定义与文件位置，中文找不到时可换更短关键词/英文符号名再试。"),
            new JsonObject
            {
                ["q"] = ToolRegistry.Str("查询词，支持中文（如：断点续行、持久化、注册工具）"),
                ["max"] = ToolRegistry.Int("最多返回结果数，默认 5"),
            },
            new JsonArray { "q" }),
            (a, ct) => Task.FromResult(SmartSearch.Run(cfg,
                a["q"]?.GetValue<string>() ?? "",
                a["max"]?.GetValue<int>() ?? 5)));
    }

    /// <summary>MapTrace 自动切片：解析输出中的定义位置（每符号只取首个定义，去重），按需取前 max 个</summary>
    static List<string> GetDefSpecs(string mapOutput, int max)
    {
        var specs = new List<string>();
        var seenSym = new HashSet<string>();
        string? sym = null;
        foreach (var line in mapOutput.Split('\n'))
        {
            if (line.StartsWith("▸ "))
            {
                sym = line[2..].Split('（')[0].Trim();
                continue;
            }
            if (!line.StartsWith("  定义: ") || sym == null || !seenSym.Add(sym)) continue;
            var loc = line[6..].Split('（')[0].Trim();
            if (loc.Length > 0) specs.Add(loc);
            if (specs.Count >= max) break;
        }
        return specs;
    }

    /// <summary>项目地图自动化进度 → DocStatus 事件（SSE 客户端可订阅；不参与任务对话流）</summary>
    void OnDocStatus(MapProgress p) =>
        Loop.Bus.Post(new UiEvent { Type = UiEventType.DocStatus, LogLine = $"{p.Percent}% · {p.Message}" });
}