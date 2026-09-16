namespace GAIRR.AgentHost;

/// <summary>Flow 步骤推进机（纯状态机，不依赖 AgentLoop）：持有当前 FlowExecutor 与推进状态，
/// 由 AgentLoop 在轮次边界驱动：
/// - 启动：new FlowRunner(steps, onEvent) → 框架建计划卡 + 注入 CurrentInstruction()；
/// - 每轮：CurrentTools() 过滤工具 schema；RecordTool() 记录工具结果；
/// - 纯文本轮：StepConcluded(text) 判定推进（含硬锚点打回），返回要注入的指令。
/// 打回语义：步骤未满足锚点时返回打回指令（不推进），上限 1 次后强制放行。</summary>
public class FlowRunner
{
    readonly List<FlowStep> steps;
    readonly Action<string> onEvent;
    FlowExecutor? executor;
    int current;

    public FlowRunner(List<FlowStep> steps, Action<string> onEvent)
    {
        this.steps = steps;
        this.onEvent = onEvent ?? (_ => { });
        executor = new FlowExecutor(steps[0], 0, steps.Count);
        this.onEvent($"[Flow] 启用流程模式：{steps.Count} 步（{string.Join("→", steps.Select(s => s.Step))}）");
    }

    /// <summary>步骤总数</summary>
    public int StepCount => steps.Count;

    /// <summary>当前进度 (0 起下标, 总数)；全部完成后 Index == Count</summary>
    public (int Index, int Count) Progress() => (Math.Max(0, current), steps.Count);

    /// <summary>当前步骤指令（全部完成后为空）</summary>
    public string CurrentInstruction() => executor != null ? executor.StepInstruction() : "";

    /// <summary>当前步骤工具白名单（null=不限；空集=无工具 ask 步；全部完成后 null）</summary>
    public HashSet<string>? CurrentTools() => executor?.AllowedTools();

    /// <summary>当前步骤类型标签（全部完成后 null）</summary>
    public string? CurrentLabel() => executor != null ? steps[current].Step : null;

    /// <summary>记录一次工具执行结果（供硬锚点校验）</summary>
    public void RecordTool(string toolName, string result) => executor?.RecordTool(toolName, result);

    /// <summary>纯文本轮收尾判定。返回 (完成步骤下标, 是否全部完成, 需注入指令)：
    /// 打回= (-1, false, 打回指令)；正常推进= (下标, false, 下一步指令)；
    /// 最后一步完成= (下标, true, 收尾指令)；已处于收尾轮= (-1, true, null)。</summary>
    public (int CompletedIndex, bool AllDone, string? Inject) StepConcluded(string text)
    {
        if (executor == null)
            return (-1, true, null);   // 收尾轮：交给调用方正常收尾
        var (ok, kick) = executor.TryComplete(text);
        if (!ok)
            return (-1, false, kick);  // 打回：不推进
        var doneIdx = executor.Index;
        onEvent($"[Flow] 步骤 {doneIdx + 1}/{steps.Count}（{steps[doneIdx].Step}）完成");
        current++;
        if (current >= steps.Count)
        {
            executor = null;
            return (doneIdx, true, "[Flow] 全部步骤已完成。请把各步骤的执行结果汇总为一份简洁的最终结论（含各步骤要点与遗留问题），用于向用户汇报。");
        }
        executor = new FlowExecutor(steps[current], current, steps.Count);
        onEvent($"[Flow] 进入步骤 {current + 1}/{steps.Count}（{steps[current].Step}）");
        return (doneIdx, false, executor.StepInstruction());
    }
}
