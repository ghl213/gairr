namespace GAIRR.AgentHost;

/// <summary>Flow 单步执行器：步骤指令构建、工具集、硬锚点校验、打回提醒（纯状态机，不回调外部）。
/// 步骤类型→工具集：search 只读；map=Map/MapTrace/SmartSearch；edit 只读+写类；
/// build=只读+Bash；commit=Bash；ask=无工具（纯文本回复）。
/// 硬锚点：edit/build/commit 步必须出现成功的 Write/Edit/Bash 才允许收尾（防"未行动就声明完成"）。</summary>
public class FlowExecutor
{
    /// <summary>各步骤类型的工具集（只读=定位类；写类=Edit/Write；执行=Bash）</summary>
    public static readonly string[] ReadOnly = { "Map", "MapTrace", "SmartSearch", "Read", "Grep", "Glob", "ListDir", "RecallHistory" };
    public static readonly string[] Write = { "Edit", "Write" };

    readonly FlowStep step;
    readonly int index;
    readonly int count;
    bool sawAnchor;      // 本步是否已出现成功的锚点动作
    int kickbacks;       // 本步打回次数（上限 1，防死循环）
    const int MaxKickbacks = 1;

    public FlowExecutor(FlowStep step, int index, int count)
    {
        this.step = step;
        this.index = index;
        this.count = count;
    }

    /// <summary>当前步骤序号（0 起）</summary>
    public int Index => index;
    /// <summary>步骤类型（search|edit|build|commit|map|ask）</summary>
    public string StepKind => step.Step;

    /// <summary>本步工具白名单（null=不限；空集=无工具，ask 步）。</summary>
    public HashSet<string>? AllowedTools()
    {
        // 显式 tools 覆盖优先（模板里写死该步工具）
        if (step.Tools is { Count: > 0 })
            return new HashSet<string>(step.Tools, StringComparer.OrdinalIgnoreCase);
        switch (step.Step)
        {
            case "search":
                return new HashSet<string>(ReadOnly, StringComparer.OrdinalIgnoreCase);
            case "map":
                return new HashSet<string> { "Map", "MapTrace", "SmartSearch" };
            case "edit":
                return new HashSet<string>(ReadOnly.Concat(Write), StringComparer.OrdinalIgnoreCase);
            case "build":
                return new HashSet<string>(ReadOnly, StringComparer.OrdinalIgnoreCase) { "Bash" };
            case "commit":
                return new HashSet<string> { "Bash" };
            case "ask":
                return new HashSet<string>();
            default:
                return null;
        }
    }

    /// <summary>该步做什么的指令文本（注入当轮用户消息/追加到任务文本）。</summary>
    public string StepInstruction()
    {
        var focus = string.IsNullOrWhiteSpace(step.Query) ? "" : $" 焦点：{step.Query}";
        var what = string.IsNullOrWhiteSpace(step.Message) ? "" : $" 要求：{step.Message}";
        return $"[Flow 步骤 {index + 1}/{count}] 类型：{step.Step}{focus}{what}";
    }

    /// <summary>记录一次工具调用结果，更新硬锚点（Write/Edit/Bash 成功即算）。</summary>
    public void RecordTool(string toolName, string result)
    {
        if (!IsOk(result) && !IsOkToolDone(result)) return;
        if (toolName is "Write" or "Edit" or "Bash")
            sawAnchor = true;
    }

    /// <summary>尝试以纯文本 text 收尾当前步。
    /// 返回 (ok, kickback)：ok=true 步骤完成（kickback=null）；
    /// ok=false 需打回继续（kickback=追发指令），打回次数达上限后强制放行。</summary>
    public (bool Ok, string? Kickback) TryComplete(string text)
    {
        // 非锚点步（search/map/ask）：纯文本结论即完成
        if (step.Step is "search" or "map" or "ask")
            return (true, null);
        // 锚点步：必须出现过成功的 Write/Edit/Bash
        if (!sawAnchor)
        {
            if (kickbacks < MaxKickbacks)
            {
                kickbacks++;
                var hint = step.Step == "commit" ? "成功执行 git commit" : step.Step == "build" ? "实际运行构建/验证命令" : "出现成功的 Write/Edit 调用";
                return (false, $"[Flow] 步骤 {index + 1}/{count}（{step.Step}）尚未{hint}，请继续执行当前步骤；若该步骤确实无需此操作，请在回复中说明理由后收尾。");
            }
        }
        return (true, null);
    }

    /// <summary>工具结果是否"成功"（错误/拦截/超时开头均不算）。</summary>
    static bool IsOk(string result) =>
        !string.IsNullOrEmpty(result)
        && !result.StartsWith("错误")
        && !result.StartsWith("拦截")
        && !result.StartsWith("超时")
        && !result.StartsWith("工具执行异常");

    /// <summary>空结果视为完成（部分工具成功时无输出）。</summary>
    static bool IsOkToolDone(string result) => string.IsNullOrEmpty(result);
}
