using System.Text.Json.Serialization;
using GAIRR.Core;

namespace GAIRR.AgentHost;

/// <summary>任务请求：text 必填；tools 为工具白名单；flow 非空时进入 Flow 模式（框架驱动步骤）</summary>
public class TaskRequest
{
    public string Text { get; set; } = "";

    /// <summary>工具白名单（null/空 = 全部启用）。不在清单内的工具对 Agent 不可见。</summary>
    public List<string>? Tools { get; set; }

    /// <summary>流程编排：Flow 模式步骤序列（非空时 AgentLoop 框架建计划卡并逐步驱动；null=自主模式）。</summary>
    public List<FlowStep>? Flow { get; set; }

    /// <summary>Plan 模式：true 时首轮只生成计划，经外部/人工审批后再继续执行工具。</summary>
    public bool PlanMode { get; set; }
}

/// <summary>流程编排步骤（Flow 模式，见 TaskRequest.Flow；模板文件 &lt;工具目录&gt;/flows/*.json）</summary>
public class FlowStep
{
    /// <summary>步骤类型：search|edit|build|commit|map|ask</summary>
    [JsonPropertyName("step")] public string Step { get; set; } = "";

    /// <summary>步骤焦点（注入该步指令的关键词/对象）。</summary>
    [JsonPropertyName("query")] public string? Query { get; set; }

    /// <summary>预留：危险命令放行（P1 未用）。</summary>
    [JsonPropertyName("allow")] public bool? Allow { get; set; }

    /// <summary>预留：危险命令拦截（P1 未用）。</summary>
    [JsonPropertyName("deny")] public bool? Deny { get; set; }

    /// <summary>该步做什么（自然语言，注入步骤指令）。</summary>
    [JsonPropertyName("message")] public string? Message { get; set; }

    /// <summary>该步工具白名单（可选，覆盖步骤类型默认集；空=用默认）。</summary>
    [JsonPropertyName("tools")] public List<string>? Tools { get; set; }
}

/// <summary>会话事件（DrainEvents 输出，JSON 友好）：直接映射 UiEventBus 的 15 种 UiEventType</summary>
public class SessionEventDto
{
    /// <summary>UiEventType 名：Started/WaitingModel/ToolStart/ToolUpdate/SecurityAlert/DangerTimeout/Todo/Round/
    /// StreamDelta/Finished/Failed/Summary/Log/DocStatus/LspStatus</summary>
    public string Type { get; set; } = "";
    public int Round { get; set; }
    public ToolUi? Tool { get; set; }
    public string? Delta { get; set; }
    public bool Start { get; set; }
    public bool IsFinal { get; set; }
    public TodoUpdate? Todo { get; set; }
    public SecurityAlert? Alert { get; set; }
    public string? Intent { get; set; }
    public string? Reasoning { get; set; }
    public int TotalTokens { get; set; }
    public string? LogLine { get; set; }
    public LlmUsage? Usage { get; set; }

    /// <summary>Plan 模式待审批计划（Type=PlanPending 时）</summary>
    public Plan? Plan { get; set; }

    /// <summary>GUI 方案确认挂起/结果（Type=PlanConfirm/PlanResolved 时）：手机端展示与决策所需 plan 摘要。
    /// PlanConfirm=桌面已弹确认框等待决策；PlanResolved=决策已定（桌面已处理），手机端应关闭确认 UI。</summary>
    public PlanConfirmInfo? PlanConfirm { get; set; }
}

/// <summary>GUI 方案确认帧载荷（手机端"确认计划"场景）：PlanConfirm 事件=待决策摘要，PlanResolved=决策结果。
/// PlanId/Title/Goal 等由桌面弹框前推送；Allow 仅在 PlanResolved 帧有效（true=已批准，false=已取消）。</summary>
public class PlanConfirmInfo
{
    public string PlanId { get; set; } = "";
    public string? Title { get; set; }
    public string? Goal { get; set; }
    public string? PlanMode { get; set; }
    public string? SelectedCandidateId { get; set; }
    public int NodeCount { get; set; }
    /// <summary>PlanResolved 时的决策结果：true=已批准执行，false=已取消</summary>
    public bool Allow { get; set; }
}

/// <summary>UiEvent → SessionEventDto 统一映射（GUI 实时镜像 / AgentSession.DrainEvents / 历史回放共用同一套字段规则）</summary>
public static class UiEventMapper
{
    public static SessionEventDto ToDto(UiEvent e) => new()
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
}

/// <summary>任务结果汇总（任务结束后从引擎状态聚合）</summary>
public class TaskResult
{
    /// <summary>最终助手文本回复（可能为空：纯工具任务）</summary>
    public string Reply { get; set; } = "";

    /// <summary>本次任务实际修改的文件清单（change log 时间窗去重，相对项目根）</summary>
    public List<string> ChangedFiles { get; set; } = new();

    /// <summary>Git 提交号（第一步恒空：是否提交由调用方决定）</summary>
    public string? Commit { get; set; }

    /// <summary>任务累计 token</summary>
    public int Tokens { get; set; }

    /// <summary>目标会话标题（首次任务时模型生成摘要）</summary>
    public string Title { get; set; } = "";
}

/// <summary>会话执行参数快照（手机端详情页"切换角色模型"面板回显用）：
/// 角色模式（role/mode，空=未指定，走默认提示词与自主模式）+ 当前生效的厂商与模型。</summary>
public class ExecParams
{
    /// <summary>角色 name（roles/*.json 的 name；空=未指定）</summary>
    public string Role { get; set; } = "";

    /// <summary>角色显示名（带图标，UI 直接可显示）</summary>
    public string RoleDisplay { get; set; } = "";

    /// <summary>模式 name（agile/deep/自定义；空=未指定）</summary>
    public string Mode { get; set; } = "";

    /// <summary>模式显示名（带图标）</summary>
    public string ModeDisplay { get; set; } = "";

    /// <summary>当前厂商 key（config.ini [Providers] 的 key）</summary>
    public string Provider { get; set; } = "";

    /// <summary>厂商显示名</summary>
    public string ProviderDisplay { get; set; } = "";

    /// <summary>当前模型 id</summary>
    public string Model { get; set; } = "";

    /// <summary>一行摘要：角色 · 模式 · 模型（顶栏/提示用）</summary>
    public string Summary { get; set; } = "";
}