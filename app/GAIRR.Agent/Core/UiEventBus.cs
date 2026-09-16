using System.Collections.Concurrent;

namespace GAIRR.Core;

/// <summary>UI 事件类型（轻量级状态信号，不含实际内容数据）</summary>
public enum UiEventType
{
    Started,        // 任务开始（提示 UI 准备接收）
    WaitingModel,   // 正在等待模型回复：UI 显示“正在思考...”提示
    ToolStart,      // 工具开始执行
    ToolUpdate,     // 工具状态更新
    SecurityAlert,  // 危险命令/敏感操作被拦截或需要用户确认
    DangerTimeout,  // 危险确认超时（5 分钟未决策）：UI 隐藏确认卡片并中止任务
    Todo,           // 待办清单变更（模型经 UpdateTodo 管理，或框架分步兜底）
    Round,          // 轮次完成
    StreamDelta,    // 流式增量文本（打字机效果：新一轮首段带 Start 标记，UI 先替换旧文本）
    Finished,       // 任务完成
    Failed,         // 任务失败
    Summary,        // 首次请求模型生成的会话摘要标题
    Log,            // 日志行
    DocStatus,      // 项目地图自动化状态（不参与任务流程）
    LspStatus,      // 语言服务器生命周期状态（不参与任务流程，显示于左侧后台状态栏）
    PlanPending,    // 计划待审批：UI 显示计划卡，等待用户批准/拒绝/修改
    CompressStart,  // 上下文压缩整理开始（可能含 LLM 必用文件询问/LLM 兜底，耗时数秒至十几秒）：等待提示条切"正在整理上下文"
    ThinkingLive,   // 深度思考流式增量（节流推送）：UI 创建/刷新"深度思考中"直播卡，轮结束由 Round 事件转正
    LockWait,       // 写文件排队等锁（节流推送）：UI 把所属会话置等待文件锁态(黄点)并提示占用者；锁到手自动恢复，不参与任务流程收尾
}

/// <summary>UI 状态事件（仅传递提示信息，实际内容从 AgentLoop 读取）
/// record：支持 with 表达式（AgentLoop.Emit 注入/PlanRunner 桥接覆盖 SessionKey 时用）</summary>
public record UiEvent
{
    public UiEventType Type { get; init; }
    public ToolUi? Tool { get; init; }
    public int Round { get; init; }
    public LlmUsage? Usage { get; init; }
    public string? Reasoning { get; init; }
    /// <summary>本轮意图说明（模型未输出 content 时由 AgentLoop 兜底生成）</summary>
    public string? Intent { get; init; }
    /// <summary>是否为最终纯文本回复轮（该轮思考内容会与结果气泡重复，UI 可选择不显示）</summary>
    public bool IsFinal { get; init; }
    /// <summary>流式输出增量文本（每轮单次流式请求的实时推送，非空时由 UI 追加到气泡）</summary>
    public string? Delta { get; init; }
    /// <summary>本轮流式首段增量：UI 收到后先清空上一轮残留文本与积压，再显示新文本（工具轮无新文本时不触发清除）</summary>
    public bool Start { get; init; }
    /// <summary>深度思考累积文本（Type=ThinkingLive）：UI 在直播卡中只取最新尾部预览，思考轮结束后由 Round 事件就地转正为正式思考卡</summary>
    public string? LiveText { get; init; }
    /// <summary>本轮是否命中 AgentLoop 兜底（如空文本收尾、异常收尾、意图宣布等），非空时 UI 在思考条/气泡标题中标识</summary>
    public string? Fallback { get; init; }
    public int TotalTokens { get; init; }
    public string? LogLine { get; init; }
    /// <summary>安全警报详情：被拦截的命令、原因、命中模式等</summary>
    public SecurityAlert? Alert { get; init; }
    /// <summary>待办清单变更详情（全部来自模型的 UpdateTodo 调用）</summary>
    public TodoUpdate? Todo { get; init; }
    /// <summary>本轮模型调用耗时（毫秒）：UI 用于在思考卡右上角显示</summary>
    public int RoundDurationMs { get; init; }
    /// <summary>锁等待状态（Type=LockWait）：true=正在排队等文件锁；false=锁已到手（UI 把所属会话从等待态恢复运行态）。新增字段默认 false，旧事件不受影响（向后兼容）</summary>
    public bool Waiting { get; init; }
    /// <summary>锁等待目标文件路径（Type=LockWait）：排队写入的文件</summary>
    public string? FilePath { get; init; }

    /// <summary>计划确认卡数据（Type=PlanPending 时使用）</summary>
    public Plan? Plan { get; init; }
    /// <summary>事件归属会话 Key：空=全局/无归属会话事件（DocStatus/LspStatus 等状态类、无会话自动任务、CLI/Server 直发），
    /// 由 AgentLoop.Emit 自动注入当前会话 Key；编排叶子事件经 PlanRunner 桥接时覆盖为编排主会话 Key。
    /// 多会话宿主按它分流归属：不属于当前会话的事件不进当前 UI 处理流程。</summary>
    public string? SessionKey { get; init; }
}

/// <summary>待办清单变更信息</summary>
public class TodoUpdate
{
    /// <summary>create=模型创建清单；update=更新某步状态；done_all=全部完成</summary>
    public string Action { get; set; } = "";
    public List<string>? Steps { get; set; }
    /// <summary>步骤序号（1 起）</summary>
    public int Index { get; set; }
    public bool Done { get; set; }
    /// <summary>步骤文本（备用）</summary>
    public string Text { get; set; } = "";
}

/// <summary>安全警报信息（危险命令拦截/敏感操作提示）</summary>
public class SecurityAlert
{
    /// <summary>警报级别：Block=已拦截，Confirm=等待用户确认</summary>
    public string Level { get; set; } = "Block";
    public string Command { get; set; } = "";
    public string Pattern { get; set; } = "";
    public string Message { get; set; } = "";
    /// <summary>模型本轮意图说明（提示“要做什么事情”）</summary>
    public string? Intent { get; set; }
}

/// <summary>线程安全的 UI 状态总线：支持广播模式（多客户端同时接收）</summary>
public class UiEventBus
{
    readonly ConcurrentQueue<UiEvent> queue = new();
    readonly List<Action<UiEvent>> subscribers = new();
    readonly object subLock = new();
    readonly List<UiEvent> history = new();
    const int MaxHistory = 1000; // 最多保留1000条历史事件
    long posted;   // 累计发布帧数（单调递增，不随 history 滚动淘汰回退）：GetHistorySince 的定位序号

    public void Post(UiEvent e)
    {
        queue.Enqueue(e);
        // 保存到历史
        lock (history)
        {
            history.Add(e);
            posted++;
            if (history.Count > MaxHistory)
                history.RemoveAt(0);
        }
        // 广播给所有订阅者
        lock (subLock)
        {
            foreach (var sub in subscribers.ToList())
                sub(e);
        }
    }

    /// <summary>订阅实时事件（新客户端连接时调用）</summary>
    public IDisposable Subscribe(Action<UiEvent> handler)
    {
        lock (subLock)
            subscribers.Add(handler);
        return new Subscription(() =>
        {
            lock (subLock)
                subscribers.Remove(handler);
        });
    }

    /// <summary>获取历史事件（新客户端连接时先推送历史）</summary>
    public List<UiEvent> GetHistory() { lock (history) return history.ToList(); }

    /// <summary>当前累计发布帧数：任务开始时记下它，之后用 GetHistorySince(seq) 只取"这一轮"的帧</summary>
    public long Posted { get { lock (history) return posted; } }

    /// <summary>取累计序号 ≥ seq 的历史帧（seq 取自 Posted）。history 滚动淘汰后按 posted 总数换算偏移，
    /// seq 早于当前窗口时退化为全量。用途：SSE 只回放进行中这一轮——上一轮的 Finished/Failed 不再重放，
    /// 否则新连接刚建好就被误判"任务已收口"而立即关流（前端表现为"连接中断，正在重连"）。</summary>
    public List<UiEvent> GetHistorySince(long seq)
    {
        lock (history)
        {
            long first = posted - history.Count;   // history[0] 对应的累计序号
            if (seq <= first) return history.ToList();
            long over = seq - first;
            int skip = over >= history.Count ? history.Count : (int)over;
            return history.GetRange(skip, history.Count - skip).ToList();
        }
    }

    /// <summary>批量取出全部待处理状态事件（向后兼容：原有单客户端模式）</summary>
    public List<UiEvent> Drain() => Drain(int.MaxValue);

    public List<UiEvent> Drain(int max)
    {
        var list = new List<UiEvent>(Math.Min(max, 256));
        for (int i = 0; i < max && queue.TryDequeue(out var e); i++)
            list.Add(e);
        return list;
    }

    public bool HasEvents => !queue.IsEmpty;

    sealed class Subscription : IDisposable
    {
        readonly Action unsubscribe;
        public Subscription(Action unsub) => unsubscribe = unsub;
        public void Dispose() => unsubscribe();
    }
}
