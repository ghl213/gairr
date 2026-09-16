using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GAIRR.Core;

/// <summary>压缩所需的会话状态快照（AgentLoop 每轮组装）</summary>
public class CompressCtx
{
    public string TaskText = "";                       // 当前任务 user 消息全文（主题锚点）
    public List<string>? TodoSteps;                    // 计划步骤文本
    public HashSet<int>? TodoDoneSteps;                // 已完成步骤索引集合（供摘要展示勾选状态）
    public HashSet<string> ModifiedFiles = new(StringComparer.OrdinalIgnoreCase); // 本会话已修改文件
    public bool ContinueReply;                         // 最新 user 消息是承接上文的短消息（≤40 字，如"好，开始"、"再查查xxx"）：不切分新任务边界，跳过判定①保留上轮完整上下文
    public int RoundBase;                              // 已归档回合数（摘要轮号从 RoundBase+1 起，跨多次压缩保持轮号连续）
    public Action<int, List<JsonObject>>? OnArchive;   // 旧轮按"回合"（一次 user 请求→完整执行→结论）切分后回调（轮号, 该回合消息原文）：模型可经 RecallHistory 按轮号取回
    public int MaxTokens;                              // 模型上下文上限
    public double UsageCalib = 1.0;                    // usage 校准系数（服务端实际 prompt token / 本地估算，>1 说明本地偏低）：软/硬阈值按此比例收紧
    public int KeepRecentRounds = 3;                   // 当前任务最近 N 轮永不压缩
    public double TopicOverlapKeep = 0.30;             // 与锚点词重叠率超此值的轮次豁免剔除
    public double HardRatio = 0.85;                    // token 兜底阈值比例
    public double SoftRatio = 0.50;                    // 压力驱动：估算 token 超过 MaxTokens×此比例才启动判定②③④（窗口富余时不擦除上下文）
    public int ResultBudget = 2000;                    // 判定④：旧轮读取结果降级后的保留字符上限（0=关闭）
    public int BudgetKeepRounds = 1;                   // 判定④：最近 N 轮读取结果不降级（Edit 需从最新 Read 逐字抄 old_text）
    public int TotalRounds;                            // 当前任务总轮数（供自适应调参）
    /// <summary>P2: LLM 压缩回调（可选）。输入待压缩文本，返回 ≤800 字结构化摘要；null/空/超时则退回硬裁剪。</summary>
    public Func<string, Task<string>>? LlmCompressAsync;
    public bool LlmCompressEnabled = true;             // P2: 是否允许 LLM 压缩（0=只用确定性压缩，离线/省钱场景）
    /// <summary>P6: 压缩前由 AgentLoop 询问 LLM 得出的"必用文件"集合（已规范化路径，含修改标记）；null=未询问（纯确定性压缩）；空集=模型明确回答"无必须文件"，非必用文件的最新读取也可降级（更省 token）</summary>
    public HashSet<string>? EssentialFiles;
    /// <summary>P6: 必用文件是否含已修改文件：含修改文件时保底"已修改文件的最新读取"，防模型拿着过期内容改文件（已修改文件本就不豁免，此标记只防误压保底）</summary>
    public bool EssentialHasModified = false;
}

/// <summary>主题式上下文压缩器：按"任务边界 + 主题相关性"整理发给模型的历史。
/// 压力驱动：判定②③④仅在估算 token 超过 MaxTokens×SoftRatio 时启动，窗口富余时保留完整上下文（防"遗忘循环"）。
/// 四个确定性判定（零 LLM 成本）：①旧任务轮次整体摘要化；②当前任务内失败轮/白读轮/重复轮剔除；
/// ③已修改文件的旧读取结果压成一行；④旧轮读取结果按预算降级（保留头尾+重取提示，每路径最新读取豁免）。
/// 所有操作只落在轮边界/消息 content，保证 tool_calls 配对完整。
/// 详见 doc/GAIRR-调用链取码与主题压缩方案.md。</summary>
public class ContextCompressor
{
    /// <summary>最近一次压缩的动作报告（供 agent.log 观测）</summary>
    public string LastReport { get; private set; } = "";
    /// <summary>最近一次是否真的发生了压缩（AgentLoop 据此决定是否替换 history）</summary>
    public bool Changed { get; private set; }

    static readonly string[] FailPrefix = { "错误", "拦截", "超时", "工具执行异常" };
    static readonly string[] ReadOnlyTools = { "Read", "Grep", "Glob", "ListDir", "MapTrace", "SmartSearch", "MapSlice", "Map", "FindRefs" };
    static readonly Regex WordRe = new(@"[A-Za-z_][A-Za-z0-9_]{2,}", RegexOptions.Compiled);

    /// <summary>压缩入口：返回整理后的新 JsonArray（不原地改调用方数组，避免父节点冲突）</summary>
    public JsonArray Compress(JsonArray history, CompressCtx ctx)
    {
        var report = new List<string>();
        var h = Clone(history);
        var taskStart = FindTaskStart(h);          // 最后一条 user 消息索引 = 当前任务起点

        // 判定①：当前任务之前的旧任务轮次 → 一条摘要（user 首消息全文保留在摘要内）
        // 承接上文的短消息（确认/追问/追加检索）不作任务边界：保留上轮完整上下文，模型才能看到被确认的询问/方案全文
        if (taskStart > 1 && !ctx.ContinueReply)
        {
            var summary = SummarizeOldTasks(h, taskStart, ctx);
            var rebuilt = new JsonArray { Clone(h[0]!), summary };
            for (var i = taskStart; i < h.Count; i++) rebuilt.Add(Clone(h[i]!));
            h = rebuilt;
            report.Add($"旧任务摘要化(-{taskStart - 1}条)");
            taskStart = 1;
        }
        else if (taskStart > 1 && ctx.ContinueReply)
        {
            report.Add("承接上文短消息，跳过旧任务摘要化(保上轮上下文)");
        }

        // 压力驱动门控：窗口富余（估算 token 未超软阈值）时跳过②③④，避免无谓擦除导致模型反复重读
        // P4: 自适应调参——长任务(>20轮)自动下调 SoftRatio 至 0.40，短任务(<5轮)上调至 0.60
        var adaptiveSoft = ctx.SoftRatio;
        if (ctx.TotalRounds > 20) adaptiveSoft = Math.Min(adaptiveSoft, 0.40);
        else if (ctx.TotalRounds < 5) adaptiveSoft = Math.Max(adaptiveSoft, 0.60);
        // usage 校准：服务端实际 prompt token 系统性大于本地估算（中文 chars/4 低估约 1.5~2 倍）时，
        // 软/硬阈值按校准系数成比例收紧（限幅 [1,3]），防软阈值过晚触发（过度擦除）与硬阈值超窗（服务端 400/截断）
        var cal = ctx.UsageCalib;
        if (cal < 1.0) cal = 1.0; else if (cal > 3.0) cal = 3.0;
        var softLimit = (int)(ctx.MaxTokens * adaptiveSoft / cal);
        var hardLimit = (int)(ctx.MaxTokens * ctx.HardRatio / cal);
        var rounds = SplitRounds(h, taskStart + 1);
        if (EstimateTokens(h) > softLimit)
        {
            // 判定②③：当前任务内按轮过滤（最近 KeepRecentRounds 轮豁免）
            if (rounds.Count > ctx.KeepRecentRounds)
            {
                var anchors = AnchorWords(ctx);
                var filt = FilterRounds(h, rounds.Take(rounds.Count - ctx.KeepRecentRounds).ToList(), ctx, anchors);
                if (filt.Count > 0) report.Add(string.Join("、", filt));
            }

            // 判定④：旧轮读取结果按预算降级（与②③互补：②③管"可整行压死"的轮，④管其余仍全文驻留的大结果）
            var dg = DowngradeOldReads(h, rounds, ctx);
            if (dg.Length > 0) report.Add(dg);
        }
        else
        {
            report.Add("窗口富余，跳过②③④");
        }

        // token 兜底：仍超阈值 → 旧轮逐条压首行，保最近 2 轮（超限回落 1 轮，配对安全）
        if (EstimateTokens(h) > hardLimit)
        {
            h = FinalTrim(h, taskStart, ctx, report);
        }

        LastReport = report.Count > 0 ? string.Join("；", report) : "无需压缩";
        // P4: 观测增强——追加自适应参数与 token 变化到报告
        if (report.Count > 0)
            LastReport += $"；soft={adaptiveSoft:F2} cal={cal:F1} rounds={ctx.TotalRounds}";
        Changed = report.Count > 0;
        return h;
    }

    /* ---------- 判定①：旧任务摘要 ---------- */

    /// <summary>把索引 1..taskStart-1 的旧任务轮次压成一条 system 摘要（按"回合"切分：每回合保留 user 首消息与结尾结论，带"轮N"标注供 RecallHistory 指定取回）</summary>
    static JsonObject SummarizeOldTasks(JsonArray h, int taskStart, CompressCtx ctx)
    {
        var sb = new StringBuilder("[历史任务摘要] 以下是此前对话记录（含讨论与未完成事项），仅作背景线索，不是当前需求授权；某轮完整原文需要时可用 RecallHistory(rounds=轮号) 取回。禁止把这里的历史主题、结论或关键词自动扩展成新的开发需求；若当前任务目标无法从用户当前原话确认，必须先询问用户。\n");
        // 建议4：把上一轮旧任务摘要折叠为紧凑线索（每任务一行"轮N 任务→结论"），长会话中 2 轮前的任务线索不随滚动替换丢失
        var prev = "";
        for (var i = 1; i < taskStart; i++)
        {
            if (h[i] is not JsonObject pm2) continue;
            var pc = (pm2["role"]?.GetValue<string>() ?? "") == "system" ? pm2["content"]?.GetValue<string>() ?? "" : "";
            if (pc.StartsWith("[历史任务摘要]")) { prev = pc; break; }
        }
        if (prev.Length > 0)
        {
            var folded = FoldPrevSummary(prev);
            if (folded.Length > 0) sb.Append("〔更早任务(仅存线索)〕\n").Append(folded).Append('\n');
        }
        var no = 0;   // 本批已见 user 消息数（相对轮号，绝对号 = ctx.RoundBase + no）
        var batch = new List<JsonObject>();
        void Flush()
        {
            if (batch.Count > 0)
            {
                ctx.OnArchive?.Invoke(ctx.RoundBase + no, batch);
                batch = new List<JsonObject>();
            }
        }
        for (var i = 1; i < taskStart; i++)
        {
            if (h[i] is not JsonObject m) continue;
            var role = m["role"]?.GetValue<string>() ?? "";
            var content = m["content"]?.GetValue<string>() ?? "";
            if (role == "user")
            {
                if (IsFrameworkMsg(m))
                {
                    // 框架干预消息不计新回合，归入当前回合原文（否则旧任务里的干预消息会被当成独立“轮N”，轮号错位）
                    if (batch.Count > 0) batch.Add(Clone(m));
                    continue;
                }
                Flush();   // 上一回合结束 → 归档原文
                no++;
                sb.Append("◆ 轮").Append(ctx.RoundBase + no).Append(" 任务: ").Append(LLMClient.Trunc(content, 500)).Append('\n');
                batch.Add(Clone(m));
            }
            else
            {
                if (batch.Count == 0) continue;   // 无 user 前缀的孤立消息不属于任何回合
                batch.Add(Clone(m));
                if (role == "assistant" && content.Length > 0)
                    sb.Append("  轮").Append(ctx.RoundBase + no).Append(" 结论: ").Append(LLMClient.Trunc(content.Replace("\n", " "), 300)).Append('\n');
            }
        }
        Flush();
        // P1: 注入结构化事实源（TodoSteps + ModifiedFiles），防失忆
        AppendStructuredFacts(sb, ctx);
        return new JsonObject { ["role"] = "system", ["content"] = LLMClient.Trunc(sb.ToString(), 3000) };
    }

    /// <summary>P1: 把 TodoSteps 勾选状态 + 已修改文件清单追加到摘要尾部，让模型看到结构化事实而非靠回忆</summary>
    static void AppendStructuredFacts(StringBuilder sb, CompressCtx ctx)
    {
        if (ctx.TodoSteps != null && ctx.TodoSteps.Count > 0)
        {
            sb.Append("\n## 当前计划\n");
            for (var i = 0; i < ctx.TodoSteps.Count; i++)
            {
                var done = ctx.TodoDoneSteps?.Contains(i + 1) == true;
                sb.Append(done ? "☑ " : "☐ ").Append(ctx.TodoSteps[i]).Append('\n');
            }
        }
        if (ctx.ModifiedFiles.Count > 0)
        {
            sb.Append("\n## 已修改文件\n");
            foreach (var f in ctx.ModifiedFiles)
                sb.Append("- ").Append(f).Append('\n');
        }
    }

    /// <summary>建议4：把上一轮旧任务摘要折叠为紧凑线索（每任务一行"◆ 轮N 任务: xxx → 结论: yyy"）。
    /// 上一摘要的任务都早于本批重新摘要的任务，二者不相交、无需剔除，全部保留以维持长会话线索；
    /// 超 1200 字时保留最新任务（尾部，向上对齐行首防半行），最旧的先省略</summary>
    static string FoldPrevSummary(string prev)
    {
        var tasks = new List<string>();
        string? cur = null;
        foreach (var raw in prev.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("◆ 轮"))
            {
                cur = line;
                tasks.Add(cur);
            }
            else if (cur != null && line.Contains("结论:"))
            {
                var c = line.Substring(line.IndexOf("结论:")).Trim();
                tasks[^1] = LLMClient.Trunc(cur, 90) + " → " + LLMClient.Trunc(c, 60);
                cur = null;
            }
        }
        var joined = string.Join("\n", tasks);
        if (joined.Length > 1200)
        {
            var cut = joined[^1200..];
            var nl = cut.IndexOf('\n');
            if (nl >= 0) cut = cut[(nl + 1)..];
            joined = "…(更早任务略)\n" + cut;
        }
        return joined;
    }

    /* ---------- 轮划分 ---------- */

    sealed class Round
    {
        public int Start;                              // assistant 消息索引
        public int End;                                // 本轮最后一条消息索引（含）
        public readonly List<(string Tool, string Path)> Calls = new();
        public bool AllToolFail = true;
        public bool HasToolMsg;
    }

    /// <summary>从 from 起按轮切分：assistant(tool_calls)+其后连续 tool 消息=一轮；纯文本 assistant=单消息轮</summary>
    static List<Round> SplitRounds(JsonArray h, int from)
    {
        var list = new List<Round>();
        var i = from;
        while (i < h.Count)
        {
            if (h[i] is not JsonObject m || (m["role"]?.GetValue<string>() ?? "") != "assistant") { i++; continue; }
            var r = new Round { Start = i, End = i };
            foreach (var tc in m["tool_calls"]?.AsArray() ?? new JsonArray())
            {
                var name = tc?["function"]?["name"]?.GetValue<string>() ?? "";
                r.Calls.Add((name, ArgPath(tc?["function"]?["arguments"]?.GetValue<string>() ?? "")));
            }
            i++;
            while (i < h.Count && h[i] is JsonObject t && (t["role"]?.GetValue<string>() ?? "") == "tool")
            {
                r.End = i; r.HasToolMsg = true;
                var c = t["content"]?.GetValue<string>() ?? "";
                if (!FailPrefix.Any(c.StartsWith)) r.AllToolFail = false;
                i++;
            }
            if (r.HasToolMsg) r.AllToolFail &= true; else r.AllToolFail = false;
            list.Add(r);
        }
        return list;
    }

    /// <summary>从 tool_calls.arguments JSON 提取 path 参数（提取不到返回空）</summary>
    static string ArgPath(string argsJson)
    {
        try { return JsonNode.Parse(argsJson)?["path"]?.GetValue<string>() ?? ""; }
        catch { return ""; }
    }

    /* ---------- 判定②③：轮内过滤 ---------- */

    /// <summary>对旧轮逐个处理：失败轮压一行；读取轮的文件已修改/白读/重复时压一行；锚点词重叠高则豁免。
    /// P6: 必用文件豁免——轮内读取路径全部在 LLM 必用集内时跳过压缩（与主题豁免并列）。
    /// 只替换 tool 消息 content（保留 tool_call_id 配对），不删消息。</summary>
    List<string> FilterRounds(JsonArray h, List<Round> rounds, CompressCtx ctx, HashSet<string> anchors)
    {
        var done = new List<string>();
        // 每路径的"最新读取轮"：重复读取时保留最新一次，旧的可压
        var latest = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rounds)
            foreach (var (tool, path) in r.Calls)
                if (path.Length > 0 && ReadOnlyTools.Contains(tool)) latest[NormPath(path, ctx)] = r.Start;

        foreach (var r in rounds)
        {
            if (r.AllToolFail && r.HasToolMsg)
            {
                SquashTools(h, r, "失败轮已压缩（工具报错记录，如需详情重新执行）");
                done.Add($"失败轮@{r.Start}");
                continue;
            }
            var readOnly = r.Calls.Count > 0 && r.Calls.All(c => ReadOnlyTools.Contains(c.Tool));
            if (!readOnly) continue;
            foreach (var (tool, path) in r.Calls)
            {
                if (path.Length == 0) continue;
                var np = NormPath(path, ctx);
                var modified = ctx.ModifiedFiles.Contains(np) || ctx.ModifiedFiles.Contains(path);
                var dup = latest.TryGetValue(np, out var lst) && lst != r.Start;  // 有更新的轮读过它 → 本次是重复读取
                if (!modified && !dup) continue;        // 未修改且最新的读取保留（可能仍相关）
                if (ExemptByTopic(h, r, anchors, ctx)) continue;
                if (EssentialSet(ctx) is { } ess && ess.Contains(np)) continue;   // P6: 必用文件豁免（LLM 判定对剩余工作必须用）
                SquashTools(h, r, $"读取结果已压缩（{tool} {path}" + (modified ? "，该文件已修改，旧内容作废）" : "，重复读取，见后续轮）"));
                // P5: 增强缓存提示，告知模型可用 RecallHistory 取回或重新读取
                if (!modified)
                    AppendCacheHint(h, r, tool, path);
                done.Add(modified ? $"已改文件旧读@{r.Start}" : $"重复读@{r.Start}");
                break;
            }
        }
        return done.Distinct().ToList();
    }

    /// <summary>P5: 在重复读压缩后的 tool 消息中追加缓存提示，减少无效重读</summary>
    static void AppendCacheHint(JsonArray h, Round r, string tool, string path)
    {
        for (var i = r.Start + 1; i <= r.End; i++)
        {
            if (h[i] is not JsonObject t || (t["role"]?.GetValue<string>() ?? "") != "tool") continue;
            var c = t["content"]?.GetValue<string>() ?? "";
            if (!c.StartsWith("[压缩]")) continue;
            var n = Clone(t);
            n["content"] = c + $"；如需确认请用 RecallHistory 取回或用 {tool} 重新获取 {path}";
            h[i] = n;
        }
    }

    /// <summary>把一轮内所有 tool 消息 content 替换为一行摘要（保留 tool_call_id 等字段，配对不破）</summary>
    static void SquashTools(JsonArray h, Round r, string note)
    {
        for (var i = r.Start + 1; i <= r.End; i++)
        {
            if (h[i] is JsonObject t && (t["role"]?.GetValue<string>() ?? "") == "tool")
            {
                var n = Clone(t);
                n["content"] = "[压缩] " + note;
                h[i] = n;
            }
        }
    }

    /// <summary>主题豁免：轮内全部文本与任务锚点词重叠率 ≥ 阈值时不压缩（防误杀间接相关上下文）</summary>
    static bool ExemptByTopic(JsonArray h, Round r, HashSet<string> anchors, CompressCtx ctx)
    {
        if (anchors.Count == 0) return false;
        var sb = new StringBuilder();
        for (var i = r.Start; i <= r.End; i++)
            sb.Append(h[i] is JsonObject m ? (m["content"]?.GetValue<string>() ?? "") : "");
        var hit = anchors.Count(a => sb.ToString().Contains(a, StringComparison.OrdinalIgnoreCase));
        return (double)hit / anchors.Count >= ctx.TopicOverlapKeep;
    }

    /* ---------- 判定④：旧轮读取结果预算降级 ---------- */

    /// <summary>把轮龄超过 BudgetKeepRounds 的读取类结果按预算降级：保留头尾各一段+重取提示。
    /// 只替换 tool 消息 content（保留 tool_call_id 配对）；最近 N 轮保全文，
    /// 因 Edit 的 old_text 必须与文件逐字符一致，模型要从最新 Read 结果里抄。
    /// 另豁免"每路径的最新读取"（且文件未被修改）：保证压缩提示"见后续轮"的承诺不落空。
    /// P6: 必用文件的读取永不降级；模型明确回答"无必须文件"（空集）时，非必用文件的最新读取也可降级（更省 token）。</summary>
    static string DowngradeOldReads(JsonArray h, List<Round> rounds, CompressCtx ctx)
    {
        if (ctx.ResultBudget <= 0 || rounds.Count <= ctx.BudgetKeepRounds) return "";
        // 每路径的"最新读取轮"：该轮的结果永不降级，模型始终能看到每个文件的最新全文
        var latest = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rounds)
            foreach (var (tool, path) in r.Calls)
                if (path.Length > 0 && ReadOnlyTools.Contains(tool)) latest[NormPath(path, ctx)] = r.Start;
        var headLen = ctx.ResultBudget * 7 / 10;   // 头部多留：文件头注释/声明多在开头
        var tailLen = ctx.ResultBudget - headLen;
        var count = 0;
        foreach (var r in rounds.Take(rounds.Count - ctx.BudgetKeepRounds))
        {
            // 本轮 tool_call_id → 工具名与路径（仅读取类工具参与降级）
            var idTool = new Dictionary<string, (string Tool, string Path)>(StringComparer.Ordinal);
            if (h[r.Start] is JsonObject am)
                foreach (var tc in am["tool_calls"]?.AsArray() ?? new JsonArray())
                {
                    var id = tc?["id"]?.GetValue<string>() ?? "";
                    var name = tc?["function"]?["name"]?.GetValue<string>() ?? "";
                    if (id.Length > 0 && ReadOnlyTools.Contains(name))
                        idTool[id] = (name, ArgPath(tc?["function"]?["arguments"]?.GetValue<string>() ?? ""));
                }
            for (var i = r.Start + 1; i <= r.End; i++)
            {
                if (h[i] is not JsonObject t || (t["role"]?.GetValue<string>() ?? "") != "tool") continue;
                var c = t["content"]?.GetValue<string>() ?? "";
                if (c.Length <= ctx.ResultBudget || c.StartsWith("[压缩]")) continue;
                var id = t["tool_call_id"]?.GetValue<string>() ?? "";
                if (!idTool.TryGetValue(id, out var call)) continue;
                // 豁免：未修改文件的最新读取保全文（其余旧读照常降级）
                var np = call.Path.Length > 0 ? NormPath(call.Path, ctx) : "";
                var modified = np.Length > 0 && (ctx.ModifiedFiles.Contains(np) || ctx.ModifiedFiles.Contains(call.Path));
                var isLatest = np.Length > 0 && latest.TryGetValue(np, out var lst) && lst == r.Start;
                // P6: 必用文件的读取永不降级；模型明确回答"无必须文件"（空集）时，非必用文件的最新读取也可降级（更省 token）
                if (np.Length > 0 && EssentialSet(ctx) is { } ess)
                {
                    if (ess.Contains(np)) continue;
                    if (isLatest && !modified && ess.Count == 0) continue;
                }
                else if (isLatest && !modified) continue;
                var n = Clone(t);
                n["content"] = c[..headLen] +
                    $"\n…（{call.Tool} 结果已按预算降级：{c.Length} 字符 → 约 {ctx.ResultBudget} 字符；需全文请用 {call.Tool} 重新获取）…\n" +
                    c[^tailLen..];
                h[i] = n;
                count++;
            }
        }
        return count > 0 ? $"旧读取按预算降级({count}处)" : "";
    }

    /// <summary>P6: 必用文件豁免集（ctx.EssentialFiles 非空时生效）；null=未询问/失败，退回纯确定性行为</summary>
    static HashSet<string>? EssentialSet(CompressCtx ctx) => ctx.EssentialFiles;

    /* ---------- 锚点词 ---------- */

    /// <summary>从当前任务文本+计划步骤提取锚点词：英文标识符 + 中文二字组</summary>
    static HashSet<string> AnchorWords(CompressCtx ctx)
    {
        var text = ctx.TaskText + "\n" + string.Join("\n", ctx.TodoSteps ?? new List<string>());
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in WordRe.Matches(text)) set.Add(m.Value);
        var cn = Regex.Replace(text, @"[^一-龥]", "");
        for (var i = 0; i + 2 <= cn.Length; i++) set.Add(cn.Substring(i, 2));
        return set;
    }

    /* ---------- token 兜底 ---------- */

    /// <summary>兜底裁剪：保 system + 旧任务摘要 + 最近 2 轮完整（仍超硬阈值回落 1 轮），其余轮逐条压成首行（轮边界切割保配对）</summary>
    static JsonArray FinalTrim(JsonArray h, int taskStart, CompressCtx ctx, List<string> report)
    {
        var rounds = SplitRounds(h, taskStart + 1);
        if (rounds.Count < 2) return h;
        JsonArray Build(int keepFrom, string note)
        {
            var sb = new StringBuilder("[早期轮次兜底压缩] ");
            for (var i = taskStart + 1; i < keepFrom; i++)
            {
                if (h[i] is JsonObject m)
                    sb.Append(m["role"]?.GetValue<string>()).Append(": ")
                      .Append(LLMClient.Trunc((m["content"]?.GetValue<string>() ?? "").Replace("\n", " "), 100)).Append("; ");
            }
            var rebuilt = new JsonArray();
            for (var i = 0; i <= taskStart; i++) rebuilt.Add(Clone(h[i]!));
            if (sb.Length > "[早期轮次兜底压缩] ".Length)
                rebuilt.Add(new JsonObject { ["role"] = "system", ["content"] = LLMClient.Trunc(sb.ToString(), 2000) });
            for (var i = keepFrom; i < h.Count; i++) rebuilt.Add(Clone(h[i]!));
            report.Add(note);
            return rebuilt;
        }
        // 建议5：优先保最近 2 轮（多留一轮中间探索，减少模型重新定位/重读）；仍超校准硬阈值（与 Compress 触发口径一致）则回落保 1 轮
        var cal = ctx.UsageCalib;
        if (cal < 1.0) cal = 1.0; else if (cal > 3.0) cal = 3.0;
        var hardLimit = (int)(ctx.MaxTokens * ctx.HardRatio / cal);
        if (rounds.Count >= 3)
        {
            var two = Build(rounds[rounds.Count - 2].Start, "兜底裁剪(保最近2轮)");
            if (EstimateTokens(two) <= hardLimit) return two;
            report.RemoveAt(report.Count - 1);
        }
        return Build(rounds[^1].Start, "兜底裁剪(保最近1轮" + (rounds.Count >= 3 ? ",2轮仍超限" : "") + ")");
    }

    /* ---------- 公共工具 ---------- */

    /// <summary>token 估算：按整条消息 JSON 序列化长度计（含 tool_calls），中文加权修正。
    /// P3: 中文字符按 1.5 token/字 估算（chars/4 对中文低估约 2 倍），英文仍按 chars/4。</summary>
    public static int EstimateTokens(JsonArray arr)
    {
        var tokens = 0.0;
        foreach (var item in arr)
        {
            if (item == null) continue;
            var s = item.ToJsonString();
            var cnCount = 0;
            foreach (var ch in s)
                if (ch >= '一' && ch <= '龥') cnCount++;
            // 中文部分按 1.5 token/字，其余按 chars/4
            tokens += cnCount * 1.5 + (s.Length - cnCount) / 4.0;
        }
        return (int)Math.Ceiling(tokens);
    }

    /// <summary>最后一条“真实”user 消息索引（跳过框架注入的“（系统提示）”干预消息）；无则返回 1（仅 system）</summary>
    static int FindTaskStart(JsonArray h)
    {
        for (var i = h.Count - 1; i >= 1; i--)
        {
            if (h[i] is JsonObject m && (m["role"]?.GetValue<string>() ?? "") == "user"
                && !IsFrameworkMsg(m)) return i;
        }
        return 1;
    }

    /// <summary>框架注入的干预消息：role=user 且 content 以“（系统提示）”开头（清单打回/空文本重试/停下确认兜底）。
    /// 这类消息不是真实用户请求，不能当任务边界、不能计为新回合，否则真实任务原文会被当成“旧任务”压进摘要。</summary>
    static bool IsFrameworkMsg(JsonObject m)
        => (m["role"]?.GetValue<string>() ?? "") == "user"
           && (m["content"]?.GetValue<string>() ?? "").StartsWith("（系统提示）");

    static string NormPath(string p, CompressCtx ctx) => p.Replace('/', '\\').TrimStart('.', '\\');

    static JsonArray Clone(JsonArray a) => (JsonArray)JsonNode.Parse(a.ToJsonString())!;
    static JsonObject Clone(JsonNode o) => (JsonObject)JsonNode.Parse(o.ToJsonString())!;
}
