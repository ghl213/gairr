/// <summary>后台任务执行过程记录器：订阅 AgentLoop 的 UiEventBus，把事件流转换成可回放的 MessageRecord 列表。</summary>
using GAIRR.Core;

namespace GAIRR;

public class TaskRunRecorder
{
    readonly List<MessageRecord> messages = new();
    readonly List<ProcessItemRecord> currentProcessItems = new();
    MessageRecord? currentMessage;
    int currentRound;
    string accumulatedText = "";
    string? currentToolTitle;
    int totalTokens;
    int totalRounds;
    int todoTotal, todoDone;   // 待办清单进度（供任务卡片运行进度展示）

    /// <summary>已收集的消息记录（按时间顺序）</summary>
    public List<MessageRecord> Messages => messages;

    /// <summary>任务累计 token 数</summary>
    public int TotalTokens => totalTokens;

    /// <summary>任务总轮数</summary>
    public int TotalRounds => totalRounds;

    /// <summary>待办步骤总数（来自模型 UpdateTodo create；未建清单为 0）</summary>
    public int TodoTotal => todoTotal;

    /// <summary>已完成待办步骤数（update done 取最大序号，done_all 视为全部完成）</summary>
    public int TodoDone => todoDone;

    /// <summary>消费 UiEventBus 中的待处理事件</summary>
    public void Drain(UiEventBus bus)
    {
        foreach (var ev in bus.Drain())
            Handle(ev);
    }

    void Handle(UiEvent ev)
    {
        switch (ev.Type)
        {
            case UiEventType.Started:
                StartAgentMessage("");
                break;

            case UiEventType.WaitingModel:
                EnsureAgentMessage("");
                break;

            case UiEventType.ToolStart:
                currentToolTitle = ev.Tool?.Title ?? "执行工具";
                AddProcessItem(new ProcessItemRecord
                {
                    Kind = "tool",
                    Title = ev.Tool?.Title ?? "执行工具",
                    Status = "…",
                    Round = ev.Round,
                    IsStep = ev.Tool?.IsStep ?? false,
                });
                break;

            case UiEventType.ToolUpdate:
                if (ev.Tool != null)
                    UpdateCurrentTool(ev.Tool);
                break;

            case UiEventType.Round:
                currentRound = ev.Round;
                totalRounds = ev.Round;
                if (ev.Usage != null) totalTokens += ev.Usage.Total;
                EnsureAgentMessage("");
                if (!string.IsNullOrWhiteSpace(ev.Intent) || !string.IsNullOrWhiteSpace(ev.Reasoning))
                {
                    AddProcessItem(new ProcessItemRecord
                    {
                        Kind = "thinking",
                        Title = !string.IsNullOrWhiteSpace(ev.Intent) ? $"第{ev.Round}轮对话 {ev.Intent}" : $"第{ev.Round}轮思考",
                        Content = ev.Reasoning ?? "",
                        Round = ev.Round,
                        DurationMs = ev.RoundDurationMs,
                        ContextTokens = ev.Usage?.Prompt ?? 0,
                    });
                }
                break;

            case UiEventType.StreamDelta:
                EnsureAgentMessage("");
                if (ev.Start) accumulatedText = ev.Delta ?? "";
                else accumulatedText += ev.Delta ?? "";
                if (currentMessage != null)
                    currentMessage.Text = accumulatedText;
                break;

            case UiEventType.SecurityAlert:
                AddProcessItem(new ProcessItemRecord
                {
                    Kind = "danger",
                    Title = "安全拦截",
                    Content = ev.Alert?.Command ?? "",
                    Summary = ev.Alert?.Pattern ?? "",
                    Round = currentRound,
                });
                break;

            case UiEventType.Todo:
                if (ev.Todo != null)
                {
                    // 跟踪待办完成度（供任务卡片运行时进度条/文本展示）
                    if (ev.Todo.Action == "create") { todoTotal = ev.Todo.Steps?.Count ?? 0; todoDone = 0; }
                    else if (ev.Todo.Action == "done_all") todoDone = todoTotal;
                    else if (ev.Todo.Action == "update" && ev.Todo.Done && ev.Todo.Index > todoDone) todoDone = ev.Todo.Index;

                    var steps = ev.Todo.Steps != null ? string.Join("\n", ev.Todo.Steps.Select((s, i) => $"{i + 1}. {s}")) : "";
                    AddProcessItem(new ProcessItemRecord
                    {
                        Kind = "todo",
                        Title = ev.Todo.Action switch { "done_all" => "待办完成", "update" => "更新待办", _ => "待办清单" },
                        Content = steps,
                        Round = currentRound,
                    });
                }
                break;

            case UiEventType.Finished:
            case UiEventType.Failed:
                FlushCurrentMessage();
                break;

            case UiEventType.Summary:
                // 任务摘要标题不影响回放过程，作为附加信息保存
                break;
        }
    }

    /// <summary>结束记录并整理最终消息</summary>
    public void Finish(string result, bool success)
    {
        FlushCurrentMessage();
        messages.Add(new MessageRecord
        {
            Kind = "Agent",
            Who = "GAIRR",
            Text = result,
            ProcessItems = new List<ProcessItemRecord>(),
        });
    }

    /// <summary>追加一条纯文本记录（如模型故障转移时的失败尝试说明），不打断当前消息结构</summary>
    public void AddNote(string text)
    {
        FlushCurrentMessage();
        messages.Add(new MessageRecord
        {
            Kind = "Agent",
            Who = "系统",
            Text = text,
            ProcessItems = new List<ProcessItemRecord>(),
        });
    }

    /// <summary>模型切换重试时重置过程状态（保留已记录消息，清掉未固化的当前消息与计数）</summary>
    public void Reset()
    {
        currentMessage = null;
        currentProcessItems.Clear();
        accumulatedText = "";
        currentToolTitle = null;
        currentRound = 0;
        totalTokens = 0;
        totalRounds = 0;
        todoTotal = 0;
        todoDone = 0;
    }

    void StartAgentMessage(string text)
    {
        FlushCurrentMessage();
        currentMessage = new MessageRecord
        {
            Kind = "Agent",
            Who = "GAIRR",
            Text = text,
            ProcessItems = new List<ProcessItemRecord>(),
        };
        currentProcessItems.Clear();
    }

    void EnsureAgentMessage(string text)
    {
        if (currentMessage == null)
            StartAgentMessage(text);
    }

    void AddProcessItem(ProcessItemRecord item)
    {
        EnsureAgentMessage("");
        currentProcessItems.Add(item);
        currentMessage!.ProcessItems.Add(item);
    }

    void UpdateCurrentTool(ToolUi tool)
    {
        var target = currentProcessItems.LastOrDefault(p => p.Kind == "tool" && p.Title == (currentToolTitle ?? tool.Title));
        if (target == null)
        {
            AddProcessItem(new ProcessItemRecord
            {
                Kind = "tool",
                Title = tool.Title,
                Status = tool.Status,
                Inner = tool.Inner,
                Summary = tool.Summary,
                Warn = tool.Warn,
                Round = tool.Round,
                IsStep = tool.IsStep,
            });
            return;
        }
        target.Status = tool.Status;
        target.Inner = tool.Inner;
        target.Summary = tool.Summary;
        target.Warn = tool.Warn;
        target.IsStep = tool.IsStep;
        target.Round = tool.Round;
    }

    void FlushCurrentMessage()
    {
        if (currentMessage == null) return;
        currentMessage.Text = accumulatedText;
        currentMessage.Steps = $"[第 {currentRound} 轮完成]";
        messages.Add(currentMessage);
        currentMessage = null;
        currentProcessItems.Clear();
        accumulatedText = "";
    }
}
