using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GAIRR.Core;

namespace GAIRR.AgentHost;

/// <summary>
/// 计划执行器（P3 执行链）：把已确认（approved）计划中的叶子任务按声明顺序串行执行。
/// 每个叶子开独立 AgentSession（auto → agent-deep.md；flow:模板名 → 模板步骤驱动），
/// 执行后过门禁：硬检查点（accept 声明编译/build 且确有改动时先跑编译）+ 审查 Agent（plan-review.md，只读+编译验证）；
/// 通过则写交接摘要（handoff）进入下一叶；不通过按 MaxRetry 重试（审查问题清单并入下一轮 prompt）；
/// 超限则叶子置 failed、整个计划 failed 并发钉钉通知；支持 Pause/Resume/Stop 与人工 MarkLeaf 裁决。
/// 只依赖 AgentHost/Core 契约，CLI/Server/WPF 三宿主复用，不触碰 UI。
/// </summary>
public class PlanRunner
{
    readonly string projectRoot;
    readonly AppConfig cfg;
    readonly Action<string> log;
    readonly CancellationTokenSource cts = new();
    readonly object gate = new();
    volatile bool paused;
    bool running;
    /// <summary>当前叶子/审查会话（Stop 时立即取消：不等模型流/长命令自然结束）</summary>
    volatile AgentSession? currentLeafSession;

    /// <summary>当前叶子/审查会话（宿主 UI 危险决策路由用；未在跑为 null）。</summary>
    public AgentSession? CurrentLeafSession => currentLeafSession;

    /// <summary>审查 Agent 阶段变化事件（宿主 UI 顶部"审核提示"用）：true=叶子执行完成、审查 Agent 启动；false=审查结束。
    /// 仅 plan.ReviewGate=true 时触发；CLI/Server 不注入为 null，不影响执行。</summary>
    public Action<bool>? ReviewStageChanged { get; set; }

    /// <summary>叶子会话实时事件桥接（UI 宿主注入）：将叶子 AgentLoop 的 UiEvent 转发到主 UI。
    /// CLI/Server 不注入则为 null，不影响执行。</summary>
    public Action<UiEvent>? LeafEventBridge { get; set; }

    /// <summary>模型取样委托（UI 宿主注入）：每个叶子/审查会话启动前调用一次，
    /// 返回宿主当前想用的 (provider, model)（如标题栏模型下拉当前选中项）。
    /// 每次执行重新取样——编排过程中用户改模型，从下一个叶子（及其审查）即生效，
    /// 不再沿用编排启动时的固定配置；CLI/Server 不注入则为 null，
    /// 取样返回空/模型不可用（不在清单、缺 Key）时回退会话默认配置模型，不阻断执行。</summary>
    public Func<(string Provider, string Model)>? ModelSelector { get; set; }

    /// <summary>审查 Agent 工具白名单：只读工具 + Bash（plan-review.md：Bash 仅用于编译/测试验证，不执行改环境的操作）。</summary>
    static readonly List<string> ReviewTools = new()
    {
        "Map", "SmartSearch", "MapTrace", "MapSlice", "FindRefs",
        "Grep", "Glob", "ListDir", "Read", "Bash", "LoadSkill",
    };

    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    // 闭合围栏前容忍 } 后的换行/空白（模型常按 markdown 惯例在 JSON 后换行再闭合 ```，
    // 与其它协议解析器（plan-tree 等组内吞尾白再 Trim）保持同等的格式宽容度）
    static readonly Regex VerdictBlock = new(@"```review-verdict\s*(\{[\s\S]*?\})\s*```", RegexOptions.Compiled);

    /// <summary>叶子每次执行的交接摘要上限字符数（过长注入下一叶会挤占上下文）。</summary>
    const int HandoffMaxChars = 600;
    /// <summary>更早叶子交接摘要的压缩长度（每个一行）。</summary>
    const int EarlyHandoffMaxChars = 120;

    public PlanRunner(string projectRoot, Action<string>? log = null)
    {
        this.projectRoot = Path.GetFullPath(projectRoot);
        cfg = new AppConfig();
        cfg.ProjectRoot = this.projectRoot;
        this.log = log ?? (_ => { });
    }

    /// <summary>判断 accept 是否声明了编译/构建类硬检查点。</summary>
    static bool NeedHardCheck(PlanNodeDto leaf) =>
        !string.IsNullOrWhiteSpace(leaf.Accept) &&
        Regex.IsMatch(leaf.Accept, @"编译|build|构建|compile", RegexOptions.IgnoreCase);

    /// <summary>探测硬检查点的构建目标（返回相对项目根路径，直接拼进 dotnet build）：
    /// ① 根目录唯一 .sln ② 根目录唯一 .csproj ③ 子目录唯一 .sln（跳过 obj/bin/node_modules 等垃圾目录）。
    /// 无目标或多目标（歧义）返回 null——此时裸 dotnet build 要么报 MSB1003 要么建错项目，宁可跳过交审查 Agent 验证，不猜。
    /// 背景：多项目仓库（如本项目，工程全在 app\* 子目录、根无 sln）裸跑 dotnet build 必失败，
    /// 会把"工作区布局问题"误判成"叶子编译失败"空烧重试。</summary>
    static string? FindBuildTarget(string projectRoot)
    {
        try
        {
            var sln = Directory.GetFiles(projectRoot, "*.sln");
            if (sln.Length == 1) return Path.GetRelativePath(projectRoot, sln[0]);
            var csproj = Directory.GetFiles(projectRoot, "*.csproj");
            if (csproj.Length == 1) return Path.GetRelativePath(projectRoot, csproj[0]);
            if (sln.Length > 1 || csproj.Length > 1) return null;   // 根目录多目标：歧义不猜

            var junk = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "obj", "bin", "node_modules", ".git", "publish", "back" };
            List<string>? sub = null;   // 惰性收集子目录 .sln，>1 即止
            foreach (var f in Directory.EnumerateFiles(projectRoot, "*.sln", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(projectRoot, f);
                if (junk.Contains(rel.Split(Path.DirectorySeparatorChar)[0])) continue;
                (sub ??= new()).Add(rel);
                if (sub.Count > 1) break;
            }
            return sub is { Count: 1 } ? sub[0] : null;
        }
        catch { return null; }   // 探测失败按"无目标"处理：跳过检查点，不阻断叶子执行
    }

    /// <summary>按 id 加载计划（不存在返回 null）。</summary>
    PlanDto? Load(string planId)
    {
        try { return PlanStore.GetById(projectRoot, planId); }
        catch (Exception ex) { log($"[plan-run] 加载计划失败：{ex.Message}"); return null; }
    }

    /// <summary>落盘计划（带日志与异常保护）。</summary>
    void Save(PlanDto plan)
    {
        try { PlanStore.Save(projectRoot, plan); }
        catch (Exception ex) { log($"[plan-run] 保存计划失败：{ex.Message}"); }
    }

    /// <summary>暂停：当前叶子执行完（或未开始）后停在叶间，等待 Resume。</summary>
    public void Pause() { paused = true; log("[plan-run] 已请求暂停（当前叶子结束后停）"); }

    /// <summary>继续执行。</summary>
    public void Resume() { paused = false; stopping = false; log("[plan-run] 已恢复执行"); }

    volatile bool stopping;   // 已请求停止（防重复日志；暂停后再停止也须生效，不能以 paused 判定）

    /// <summary>立即停止：取消当前叶子/审查会话与危险等待（不等模型流/长命令自然结束），计划置 paused 供续跑。</summary>
    public void Stop()
    {
        if (!running) { log("[plan-run] 未在运行，忽略停止请求"); return; }
        if (stopping) return;   // 重复点击：取消操作幂等，仅日志去重
        stopping = true;
        paused = true;
        var s = currentLeafSession;
        if (s != null)
        {
            s.Loop.ResolveDanger(false);   // 叶子卡在危险确认时立即拒绝，解除挂起
            s.Cancel();                    // 立即取消模型流/长命令/审查会话
        }
        cts.Cancel();
        log("[plan-run] 已停止（当前叶子已取消，计划置 paused）");
    }

    /// <summary>入口：串行执行计划的 pending 叶子；返回 true=执行范围内计划全部完成（done），false=未完成/失败/中止。
    /// scopeId=null 整树执行（原语义）；scopeId 指向 group = 该分组子树自动遍历续跑；指向 leaf = 只执行这一片叶子
    /// （叶子显式操作：失败/残留态归一后重跑，跑完按全树剩余叶子收口，不擅自续跑其它分支）。</summary>
    public async Task<bool> RunAsync(string planId, string? scopeId = null, CancellationToken externCt = default)
    {
        lock (gate) { if (running) throw new InvalidOperationException("该 PlanRunner 实例已在执行中（每条计划请用独立实例）"); running = true; }
        try
        {
            var plan = Load(planId);
            if (plan == null) { log($"[plan-run] 计划不存在：{planId}"); return false; }

            // 范围解析：scopeId 非空时确定目标节点类型；单叶 = 用户右键叶子显式操作。
            bool leafScope = false;
            if (!string.IsNullOrEmpty(scopeId))
            {
                var sc = plan.Nodes.FirstOrDefault(n => n.Id == scopeId);
                if (sc == null) { log($"[plan-run] 范围节点不存在：{scopeId}"); return false; }
                leafScope = sc.Type == "leaf";
            }
            if (plan.Status is not ("approved" or "running" or "paused"))
            {
                // 范围执行（叶子/分支）是用户显式右键操作：failed 计划放行（供续跑/重试指定位置）；
                // 整树执行仍须 approved/running/paused（pendingConfirm 请先确认执行）。
                if (scopeId == null || plan.Status != "failed")
                {
                    log($"[plan-run] 计划状态 {plan.Status} 不可执行（需 approved；pendingConfirm 请先确认执行）");
                    return false;
                }
                log("[plan-run] 计划处于 failed，按范围执行放行续跑");
            }

            // 单叶归一：failed → 重置审查配额/问题清单（用户选择重新开始/重新审查该叶）；
            // running/reviewing 残留（进程中断/强退）→ 回 pending 保留计数续跑；passed/skipped → 无事可做直接收口。
            if (leafScope)
            {
                var sl = plan.Nodes.FirstOrDefault(n => n.Id == scopeId);
                if (sl != null)
                {
                    if (sl.Status == "failed")
                    {
                        sl.Attempts = 0;
                        sl.ReviewIssues = new List<string>();
                        sl.ReviewSummary = null;
                        log($"[plan-run] 叶子 {sl.Title} 已失败：重置审查计数后重新执行");
                    }
                    else if (sl.Status is "running" or "reviewing")
                    {
                        log($"[plan-run] 叶子 {sl.Title} 残留 {sl.Status} 态，归一回 pending 续跑（保留审查计数）");
                    }
                    else if (sl.Status is "passed" or "skipped")
                    {
                        var remain = PendingAnywhere(plan);
                        plan.Status = remain ? "paused" : "done";
                        Save(plan);
                        log(remain ? $"[plan-run] 叶子 {sl.Title} 已完成，计划仍有未执行叶（置 paused）" : "[plan-run] 指定叶子已完成，计划全部完成（done）");
                        return !remain;
                    }
                    if (sl.Status != "pending") { sl.Status = "pending"; Save(plan); }
                }
            }

            plan.Status = "running";
            Save(plan);

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, externCt);
            var ct = linked.Token;
            await WaitWhilePausedAsync(ct);

            var scopeOrder = PlanStore.LeafOrder(plan, scopeId);   // 范围内执行顺序（null=整树；group=子树；leaf=单叶）

            // 自动修复（自愈，幂等）：把范围内的可重跑残留归一，保证"开始/继续执行"不会被卡死或立即判失败。
            // 1) running/reviewing = 进程强退/中断遗留的僵尸态（正常 Stop 已回 pending，此处分担强退场景）：归 pending 续跑；
            // 2) pending 且 attempts>MaxRetry = 上次执行中断前已耗尽配额、没走 failed 收口的残留
            //    （如用户暂停/强退恰在重试边界）：清空 attempts 后重新执行，否则 RunLeafWithRetryAsync 一进来就超限失败。
            // 不影响：passed/skipped（跳过）、failed（单叶由用户显式归一时处理）、attempts 未超限的 pending（保留计数续跑）。
            int repaired = 0;
            foreach (var id in scopeOrder)
            {
                var n = plan.Nodes.FirstOrDefault(x => x.Id == id);
                if (n == null || n.Type != "leaf") continue;
                if (n.Status is "running" or "reviewing")
                {
                    log($"[plan-run] 自动修复：叶子 {n.Title} 中断残留 {n.Status} 态 → 归一回 pending 续跑");
                    n.Status = "pending";
                    repaired++;
                }
                else if (n.Status == "pending" && n.Attempts > plan.MaxRetry)
                {
                    log($"[plan-run] 自动修复：叶子 {n.Title} 配额耗尽残留（attempts={n.Attempts} > maxRetry={plan.MaxRetry}）→ 清空计数重新执行");
                    n.Attempts = 0;
                    repaired++;
                }
            }
            if (repaired > 0)
            {
                Save(plan);
                log($"[plan-run] 自动修复 {repaired} 个叶子，继续执行");
            }

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                await WaitWhilePausedAsync(ct);
                PlanNodeDto? leaf = null;
                foreach (var id in scopeOrder)
                {
                    var n = plan.Nodes.FirstOrDefault(x => x.Id == id);
                    if (n != null && n.Type == "leaf" && n.Status == "pending") { leaf = n; break; }
                }
                if (leaf == null) break;   // 范围内无待执行叶子 = 范围跑完（跳过已 failed/已完成）

                log($"[plan-run] 执行叶子：{leaf.Title}（id={leaf.Id}）");
                leaf.Status = "running";
                Save(plan);

                bool ok;
                try { ok = await RunLeafWithRetryAsync(plan, leaf, ct); }
                catch (OperationCanceledException)
                {
                    // Stop 触发：StartAsync 吞掉取消后重试边界/命令/审查抛 OCE，
                    // 同样落盘 叶子回 pending + 计划 paused，避免残留 reviewing/running 僵尸状态导致续跑漏叶
                    if (leaf.Status is not ("passed" or "skipped")) leaf.Status = "pending";
                    plan.Status = "paused";
                    Save(plan);
                    log("[plan-run] 已停止，计划置 paused，可稍后重跑续执行");
                    return false;
                }
                if (ct.IsCancellationRequested)
                {
                    // 用户停止：叶子留在半途状态，计划置 paused 供续跑
                    if (leaf.Status is not ("passed" or "skipped")) leaf.Status = "pending";
                    plan.Status = "paused";
                    Save(plan);
                    log("[plan-run] 已停止，计划置 paused，可稍后重跑续执行");
                    return false;
                }

                if (!ok)   // 重试用尽 → 叶子失败 + 计划失败 + 钉钉（单叶/范围执行同样如此，交用户裁决）
                {
                    leaf.Status = "failed";
                    plan.Status = "failed";
                    Save(plan);
                    log($"[plan-run] 叶子 {leaf.Title} 重试用尽，计划失败");
                    await SendDingTalkAsync(false, plan, leaf, ct);
                    return false;
                }
                Save(plan);
                if (leafScope) break;   // 单叶模式：这一片到终态即收口，不继续跑范围外叶子
            }

            if (scopeId != null)
            {
                // 范围（叶子/分支）收口：只把"范围内全部叶子到终态"视为范围完成；
                // 全树若仍有待执行叶 → paused（等用户继续右键/整树执行），否则 done。
                var remain = PendingAnywhere(plan);
                plan.Status = remain ? "paused" : "done";
                Save(plan);
                log(remain ? "[plan-run] 范围执行完毕，计划仍有未执行叶（置 paused 待续）" : "[plan-run] 范围执行完毕，计划全部完成（done）");
                return !remain;
            }
            plan.Status = "done";
            Save(plan);
            log("[plan-run] 全部叶子完成，计划 done");
            return true;
        }
        catch (OperationCanceledException)
        {
            // 由上方取消分支处理（已置 paused 返回 false）；兜底日志
            log("[plan-run] 执行被取消");
            return false;
        }
        finally { lock (gate) running = false; }
    }

    /// <summary>全树是否仍有待执行（pending）叶子（范围收口判定用）。</summary>
    static bool PendingAnywhere(PlanDto plan) =>
        PlanStore.LeafOrder(plan).Any(id =>
            plan.Nodes.Any(n => n.Id == id && n.Type == "leaf" && n.Status == "pending"));

    /// <summary>暂停点：paused 时原地等待（仅首次打日志），直到 Resume 或取消。</summary>
    async Task WaitWhilePausedAsync(CancellationToken ct)
    {
        var printed = false;
        while (paused)
        {
            if (!printed) { log("[plan-run] 暂停中…"); printed = true; }
            await Task.Delay(700, ct);
        }
    }

    /// <summary>执行叶子（审查 Agent 判定不通过时按 Attempts ≤ MaxRetry 打回重改，带审查问题清单）；true=通过。
    /// 编译类失败（硬检查点/审查编译验证）打回不计审查配额——叶子修复编译即可重试，不消耗有限的审查重试次数；
    /// 但设独立连击上限防死循环（同 MaxRetry，达限视为失败退出）。</summary>
    async Task<bool> RunLeafWithRetryAsync(PlanDto plan, PlanNodeDto leaf, CancellationToken ct)
    {
        var issues = new List<string>();
        int compileStrikes = 0;   // 编译失败连击（不计 Attempts，独立上限防死循环）
 int incompleteStrikes = 0; // 审查未完成连击（不计 Attempts，独立上限防死循环）
        while (leaf.Attempts <= plan.MaxRetry)
        {
            ct.ThrowIfCancellationRequested();
            leaf.Status = "reviewing";
            Save(plan);

            log($"[plan-run] 叶子执行（审查计数 {leaf.Attempts}/{plan.MaxRetry}）");
            currentLeafSession = null;   // 进入新叶子/审查会话前复位（RunLeafOnceAsync 内部会赋值）
            var exec = await RunLeafOnceAsync(plan, leaf, issues, ct);
            currentLeafSession = null;
            issues = exec.Issues;
            leaf.ReviewIssues = issues;
            if (!exec.Ok)   // 执行级失败（起会话/门禁异常）按不通过计配额
            {
                log($"[plan-run] 叶子执行异常/失败：{exec.Err}");
                if (++leaf.Attempts > plan.MaxRetry) break;
                continue;
            }
            if (exec.Passed)   // 门禁通过（含硬检查点+审查）
            {
                leaf.Status = "passed";
                leaf.Handoff = Trim(exec.Handoff, HandoffMaxChars);
                leaf.ChangedFiles = exec.ChangedFiles;
                leaf.ReviewSummary = exec.ReviewSummary;
                leaf.ReviewIssues = exec.Issues;
                return true;
            }

            if (IsCompileFailure(exec))
            {
                // 编译错误打回重改：不计审查计数（编译属可客观修复问题，不占用审查重试配额）
                if (++compileStrikes > plan.MaxRetry)
                {
                    log($"[plan-run] 叶子连续 {compileStrikes} 次编译未通过，重试终止（不计审查配额仍达编译连击上限）");
                    break;
                }
                log($"[plan-run] 编译错误打回重改（第 {compileStrikes} 次，不计审查计数）：{string.Join("；", issues)}");
                continue;
            }

 // 审查会话未完成（中断/无结论）：不计审查配额，独立连击上限防死循环
 if (exec.Incomplete)
 {
 if (++incompleteStrikes > plan.MaxRetry)
 {
 log("[plan-run] 叶子连续 " + incompleteStrikes + " 次审查未完成，重试终止（不计审查配额仍达连击上限）");
 break;
 }
 log("[plan-run] 审查未完成打回重试（第 " + incompleteStrikes + " 次，不计审查计数）：" + string.Join("；", issues));
 continue;
 }

 // 审查 Agent 判定不通过：累计审查计数（配额内可继续打回重改）
 leaf.Attempts++;
            log($"[plan-run] 审查不通过（第 {leaf.Attempts} 次）：{string.Join("；", issues)}");
        }
        return false;
    }

    /// <summary>判断打回原因是否编译类错误（不计审查配额）：硬检查点编译失败（Err 固定值）或审查 issues 含编译失败关键词。</summary>
    static bool IsCompileFailure(LeafRun exec)
    {
        if (exec.Err == "硬检查点编译失败") return true;
        foreach (var s in exec.Issues)
        {
            if (Regex.IsMatch(s, @"编译未通过|编译失败|编译不通过|dotnet build.*(失败|failed)|build failed|构建失败",
                              RegexOptions.IgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>单次叶子执行结果。</summary>
    sealed class LeafRun
    {
        public bool Ok;                       // 执行链整体无异常
        public bool Passed;                   // 门禁是否通过
        public string Err = "";               // 失败原因（Ok=false 时）
        public string Handoff = "";           // 执行者交接摘要
        public List<string> ChangedFiles = new();
        public List<string> Issues = new();   // 审查问题清单
 public string ReviewSummary = "";
 public bool Incomplete; // 审查会话未完成（中断/未产出结论）：不计审查配额，独立上限防死循环
 }

    /// <summary>叶子/审查会话启动前重新取样宿主所选模型（标题栏下拉）：取样成功则切换本会话 LLM 客户端
    /// （含思考规则随模型重挂）；宿主未注入取样、取样为空/异常、模型不在可选清单或缺 ApiKey 时，
    /// 保留会话默认配置模型（不阻断执行，仅日志留痕）。</summary>
    void ApplyHostModel(AgentSession session)
    {
        if (ModelSelector == null) return;
        (string provider, string model)? sel;
        try { sel = ModelSelector(); }
        catch (Exception ex) { log($"[plan-run] 宿主模型取样失败，用默认配置：{ex.Message}"); return; }
        if (sel == null || string.IsNullOrWhiteSpace(sel.Value.provider) || string.IsNullOrWhiteSpace(sel.Value.model)) return;
        var opt = session.Config.ModelOptions().FirstOrDefault(o =>
            string.Equals(o.Provider, sel.Value.provider, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(o.ModelId, sel.Value.model, StringComparison.OrdinalIgnoreCase));
        if (opt == null) { log($"[plan-run] 宿主模型 {sel.Value.provider}/{sel.Value.model} 不在可选清单，用默认配置"); return; }
        if (!opt.HasKey) { log($"[plan-run] 宿主模型 {opt.ProviderDisplay} 未配 ApiKey，用默认配置"); return; }
        try { session.Loop.SwitchModelFull(sel.Value.provider, sel.Value.model); }
        catch (Exception ex) { log($"[plan-run] 切换宿主模型失败，用默认配置：{ex.Message}"); return; }
        log($"[plan-run] 叶子使用标题栏所选模型：{opt.Display}");
    }

    /// <summary>执行一次叶子：起会话 → 硬检查点 → 审查 Agent；返回门禁结果。</summary>
    async Task<LeafRun> RunLeafOnceAsync(PlanDto plan, PlanNodeDto leaf, List<string> priorIssues, CancellationToken ct)
    {
        var r = new LeafRun();
        try
        {
            var prompt = BuildLeafPrompt(plan, leaf, priorIssues);
            var sid = "leaf-" + Guid.NewGuid().ToString("N")[..8];
            var session = new AgentSession(sid, projectRoot, null, "agent-deep.md");
            currentLeafSession = session;   // 注册到 runner：Stop 时可立即取消本叶子会话
            ApplyHostModel(session);        // 每叶执行前重新取样标题栏所选模型（编排中途换模型即从本叶生效）

            // mode=flow:模板名 → Flow 模板步骤驱动；auto/null → 自主
            TaskRequest req = new() { Text = prompt };
            if (leaf.Mode is string m && m.StartsWith("flow:", StringComparison.OrdinalIgnoreCase))
            {
                var tpl = FlowTemplateStore.Find(m[5..].Trim());
                if (tpl != null)
                {
                    req.Flow = tpl.Steps;
                    log($"[plan-run] Flow 模式：{tpl.Name}");
                }
                else log("[plan-run] Flow 模板未找到，回退自主模式：" + m);
            }

            var result = await RunLeafSessionWithBridgeAsync(session, req, leaf.Title, ct);
            leaf.SessionRunId = sid;
            leaf.ChangedFiles = result.ChangedFiles ?? new();
            r.ChangedFiles = result.ChangedFiles ?? new();
            r.Handoff = result.Reply ?? "";

            // 门禁 1：硬检查点（accept 声明编译/build 且确有改动 → 实际编译）
            if (NeedHardCheck(leaf) && r.ChangedFiles.Count > 0)
            {
                var target = FindBuildTarget(projectRoot);
                if (target == null)
                {
                    log("[plan-run] 硬检查点跳过：项目根无唯一 .sln/.csproj（无目标/多目标歧义不猜），编译验证交审查 Agent");
                }
                else
                {
                    var cmd = "dotnet build -v q \"" + target + "\"";   // 引号防路径含空格被 cmd 拆词
                    log("[plan-run] 硬检查点：" + cmd);
                    var outText = await Phase1Tools.RunCmd(cfg, cmd, 300, ct);
                    if (outText.StartsWith("超时：", StringComparison.Ordinal))
                    {
                        // 超时 ≠ 编译失败（大项目冷构建可能超 300s）：记不确定、不计编译连击，交审查 Agent 定夺
                        log("[plan-run] 硬检查点不确定：构建超 300s 超时（不计编译连击），交审查 Agent 验证");
                    }
                    else if (!outText.StartsWith("退出码 0\n"))
                    {
                        // 按退出码判成败：MSBuild 成功文案随系统语言变化（中文"已成功生成。"/英文"Build succeeded."），字符串匹配不可靠
                        r.Ok = true; r.Passed = false; r.Err = "硬检查点编译失败";
                        r.Issues = new List<string> { $"编译未通过（硬检查点 {cmd} 失败，请重跑该命令查看具体错误并修复）" };
                        return r;
                    }
                }
            }

            // 门禁 2：审查 Agent（plan-review.md：只读 + 编译/测试验证）
            if (plan.ReviewGate)
            {
                ReviewStageChanged?.Invoke(true);   // 审查启动：宿主顶部切"审核中"提示（叶子执行阶段结束）
                ReviewVerdict verdict;
                try { verdict = await RunReviewAsync(plan, leaf, r.Handoff, ct); }
                finally { ReviewStageChanged?.Invoke(false); }   // 审查结束（含异常路径）：宿主切回执行态
                r.Ok = true;
 r.Passed = verdict.Passed;
 r.Issues = verdict.Issues;
 r.ReviewSummary = verdict.Summary ?? "";
 r.Incomplete = verdict.Incomplete;
 return r;
            }

            r.Ok = true; r.Passed = true;
            return r;
        }
        catch (Exception ex)
        {
            r.Ok = false; r.Err = ex.Message;
            r.Issues = new List<string> { "叶子执行异常：" + Trim(ex.Message, 100) };
            return r;
        }
    }

    /// <summary>执行叶子会话并实时转发 Bus 事件到 LeafEventBridge（无桥接时等同直接 StartAsync）。</summary>
    async Task<TaskResult> RunLeafSessionWithBridgeAsync(AgentSession session, TaskRequest req, string leafTitle, CancellationToken ct)
        => await RunSessionWithBridgeAsync(session, req, leafTitle, ct);

    /// <summary>执行 Agent 会话并实时转发 Bus 事件到 LeafEventBridge（无桥接时等同直接 StartAsync）。
    /// tag 用作日志行前缀标识（叶子=叶子标题；审查=“审查·叶子标题”），其余事件原样转发。</summary>
    async Task<TaskResult> RunSessionWithBridgeAsync(AgentSession session, TaskRequest req, string tag, CancellationToken ct)
    {
        var bridge = LeafEventBridge;
        if (bridge == null) return await session.StartAsync(req, ct);

        // 启动后台转发：每 100ms Drain 会话 Bus 并转发到主 UI
        using var fwdCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var fwdTask = Task.Run(async () =>
        {
            while (!fwdCts.Token.IsCancellationRequested)
            {
                foreach (var ev in session.Loop.Bus.Drain())
                {
                    // 在日志行前加会话标识，方便区分
                    if (ev.Type == UiEventType.Log && ev.LogLine != null)
                        bridge(new UiEvent { Type = ev.Type, LogLine = $"[{tag}] {ev.LogLine}" });
                    else
                        bridge(ev);
                }
                try { await Task.Delay(100, fwdCts.Token); }
                catch (OperationCanceledException) { break; }
            }
        }, fwdCts.Token);

        try
        {
            return await session.StartAsync(req, ct);
        }
        finally
        {
            fwdCts.Cancel();
            try { await fwdTask; } catch { /* ignore */ }
            // 最终排空残余事件
            foreach (var ev in session.Loop.Bus.Drain()) bridge(ev);
        }
    }

    /// <summary>拼接叶子执行 prompt：目标 + 验收 + 前序交接（上一叶全文 + 更早压缩一行）。</summary>
    static string BuildLeafPrompt(PlanDto plan, PlanNodeDto leaf, List<string> priorIssues)
    {
        var sb = new StringBuilder();
        sb.AppendLine("你是 GAIRR 的编码执行 Agent（任务树叶子单元）。当前只做本叶子的目标，不要越界做其他叶子的工作。");
        sb.AppendLine();
        sb.AppendLine("## 叶子目标");
        sb.AppendLine(leaf.Goal ?? leaf.Title);
        if (!string.IsNullOrEmpty(leaf.Accept))
        {
            sb.AppendLine();
            sb.AppendLine("## 验收标准");
            sb.AppendLine(leaf.Accept);
        }
        var ctx = PlanContextLines(plan);
        if (ctx.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## 项目上下文（前序叶子交接）");
            sb.Append(ctx);
        }
        if (priorIssues is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("## 上一轮审查未通过，请逐条修复后再自行核对");
            foreach (var i in priorIssues) sb.AppendLine("- " + i);
        }
        sb.AppendLine();
        sb.AppendLine("## 要求");
        sb.AppendLine("1. 完成目标并满足验收标准；只允许改动本叶子范围内文件；");
        sb.AppendLine("2. 结束时输出简短交接摘要（做了什么 / 遗留问题 / 对下一叶的建议），供下一叶子使用；");
        sb.AppendLine("3. 若目标无法达成，如实说明原因，不要假装完成。");
        return sb.ToString();
    }

    /// <summary>从计划中汇总前序已通过叶子的交接：最近一叶全文，更早每叶压缩一行。</summary>
    static string PlanContextLines(PlanDto plan)
    {
        var sb = new StringBuilder();
        var passed = new List<PlanNodeDto>();
        foreach (var id in PlanStore.LeafOrder(plan))
        {
            var n = plan.Nodes.FirstOrDefault(x => x.Id == id);
            if (n != null && (n.Status == "passed" || n.Status == "skipped") && n.Handoff is { Length: > 0 })
                passed.Add(n);
        }
        for (var i = 0; i < passed.Count; i++)
        {
            var n = passed[i];
            var isLast = i == passed.Count - 1;
            var title = string.IsNullOrWhiteSpace(n.Title) ? n.Id : n.Title;
            if (isLast) sb.AppendLine($"【{title}（上一叶，全文）】\n{Trim(n.Handoff ?? "", HandoffMaxChars)}");
            else sb.AppendLine($"- {title}：{Trim(n.Handoff ?? "", EarlyHandoffMaxChars)}");
        }
        return sb.ToString();
    }

    /// <summary>跑审查会话；返回审查结论对象（解析失败按不通过处理）。</summary>
    async Task<ReviewVerdict> RunReviewAsync(PlanDto plan, PlanNodeDto leaf, string handoff, CancellationToken ct)
    {
        try
        {
            var sid = "review-" + Guid.NewGuid().ToString("N")[..8];
            var session = new AgentSession(sid, projectRoot, null, "plan-review.md");
            currentLeafSession = session;   // 审查会话同样注册：Stop 时立即取消审查
            ApplyHostModel(session);        // 审查同样重取样标题栏所选模型（与所属叶子执行一致）
            var text = new StringBuilder();
            text.AppendLine("## 叶子目标");
            text.AppendLine(leaf.Goal ?? leaf.Title);
            text.AppendLine();
            text.AppendLine("## 验收标准");
            text.AppendLine(string.IsNullOrWhiteSpace(leaf.Accept) ? "（未指定，按目标完成实现且不引入明显问题为准）" : leaf.Accept);
            text.AppendLine();
            text.AppendLine("## 改动文件清单");
            var files = leaf.ChangedFiles ?? new();
            text.AppendLine(files.Count == 0 ? "（无文件改动）" : string.Join("\n", files));
            text.AppendLine();
            text.AppendLine("## 交接摘要");
            text.AppendLine(string.IsNullOrWhiteSpace(handoff) ? "（执行者未给出交接摘要）" : handoff);
            // 审查过程同样经 LeafEventBridge 实时转发到主 UI（与叶子一致）：
            // 审查的思考/工具/结论与危险确认卡可见可决策，避免审查长时间无过程展示被误判卡死
            var req = new TaskRequest { Text = text.ToString(), Tools = ReviewTools };
            var result = await RunSessionWithBridgeAsync(session, req, "审查·" + (leaf.Title ?? leaf.Id), ct);
            return ParseVerdict(result.Reply ?? "");
        }
        catch (Exception ex)
        {
            log($"[plan-run] 审查会话异常：{ex.Message}");
            return new ReviewVerdict { Passed = false, Issues = new List<string> { "审查会话异常：" + Trim(ex.Message, 80) }, Summary = "审查异常" };
        }
    }

    /// <summary>从正文抠裸 verdict JSON（首个含 passed 的配对花括号段，忽略字符串内括号）；找不到返回空串。</summary>
 static string ExtractBareVerdict(string text)
 {
 int at = text.IndexOf("passed", StringComparison.OrdinalIgnoreCase);
 if (at < 0) return "";
 int start = text.LastIndexOf((char)123, at);
 if (start < 0) return "";
 int depth = 0; bool inStr = false; bool esc = false;
 for (int i = start; i < text.Length; i++)
 {
 char c = text[i];
 if (inStr) { if (esc) esc = false; else if (c == (char)92) esc = true; else if (c == (char)34) inStr = false; continue; }
 if (c == (char)34) { inStr = true; continue; }
 if (c == (char)123) depth++;
 else if (c == (char)125) { if (--depth == 0) return text.Substring(start, i - start + 1); }
 }
 return "";
 }

 /// <summary>解析 review-verdict 块；无块/JSON 损坏按不通过。</summary>
    static ReviewVerdict ParseVerdict(string reply)
    {
 var text = reply ?? "";
 var m = VerdictBlock.Match(text);
 string body = m.Success ? m.Groups[1].Value : ExtractBareVerdict(text); // 回退：漏围栏但正文含裸 verdict JSON
 if (body.Length == 0)
 {
 // 通篇无 passed 键 → 审查尚未产出结论（会话被中断/工具循环截断），非格式问题，不计配额
 if (!text.Contains("passed", StringComparison.OrdinalIgnoreCase))
 return new ReviewVerdict { Passed = false, Incomplete = true, Issues = new List<string> { "审查会话未产出结论（疑似中断/超时，非格式问题），已自动重试" }, Summary = "审查未完成" };
            // 附格式样例：该问题清单会并入下一轮审查 prompt，必须让审查 Agent 能据此自纠格式
            return new ReviewVerdict { Passed = false, Issues = new List<string> { "审查输出缺少 review-verdict 块（输出须含恰好一个 ```review-verdict 代码块，块内为 {\"passed\":true/…} JSON，块后不再写其它内容）" }, Summary = "解析失败" };
        }
        try
        {
            var v = JsonSerializer.Deserialize<ReviewVerdict>(body, JsonOpts);
            return v ?? new ReviewVerdict { Passed = false, Issues = new List<string> { "审查 verdict 为空" }, Summary = "解析失败" };
        }
        catch (Exception)
        {
            return new ReviewVerdict { Passed = false, Issues = new List<string> { "审查 verdict JSON 损坏" }, Summary = "解析失败" };
        }
    }

    /// <summary>审查结论 DTO（与 plan-review.md 输出契约一致）。</summary>
    sealed class ReviewVerdict
    {
        public bool Passed { get; set; }
 public List<string> Issues { get; set; } = new();
 public string? Summary { get; set; }
 /// <summary>审查会话未完成（无结论产出）：调用方据此不计审查配额。</summary>
 [System.Text.Json.Serialization.JsonIgnore]
 public bool Incomplete { get; set; }
 }

    /// <summary>人工裁决状态机：把非终态叶子标记为 passed（通过）/skipped（跳过），或把已标记叶子撤销回 pending。
    /// 可裁决来源：failed（失败待重审）/ pending（跳过某叶或直接认定完成）/ running·reviewing（执行中断残留，
    /// 如运行中关程序后叶子停在 reviewing，须先裁决成终态或撤销后才能被续跑拾取）。
    /// 合法迁移：failed|pending|running|reviewing → passed|skipped；passed|skipped → pending（撤销）。
    /// 非法组合返回 false（不落盘）；成功后计划置 paused 供用户重新 RunAsync 续跑。</summary>
    public static bool MarkLeaf(string projectRoot, string planId, string leafId, string toStatus)
    {
        var plan = PlanStore.GetById(projectRoot, planId);
        if (plan == null) return false;
        var leaf = plan.Nodes.FirstOrDefault(n => n.Id == leafId);
        if (leaf == null) return false;
        var from = leaf.Status ?? "";
        bool allowed = toStatus switch
        {
            "passed" or "skipped" => from is "failed" or "pending" or "running" or "reviewing",
            "pending" => from is "passed" or "skipped",   // 撤销人工裁决
            _ => false,
        };
        if (!allowed) return false;
        leaf.Status = toStatus;
        if (toStatus == "skipped")
            leaf.Handoff = "（人工裁决：跳过本叶子）" + (string.IsNullOrEmpty(leaf.Handoff) ? "" : "\n原交接：" + Trim(leaf.Handoff, HandoffMaxChars));
        if (toStatus == "pending")
        {
            // 撤销标记：清掉裁决残留交接/审查问题，恢复为干净的待执行态
            leaf.Handoff = "";
            leaf.ReviewIssues = new();
        }
        plan.Status = "paused";   // 人工裁决后由用户重新 RunAsync 续跑
        PlanStore.Save(projectRoot, plan);
        return true;
    }

    /// <summary>钉钉通知（webhook 未配置时静默跳过；仅失败/中止时调用）。</summary>
    async Task SendDingTalkAsync(bool success, PlanDto plan, PlanNodeDto leaf, CancellationToken ct)
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
            var body = new System.Text.Json.Nodes.JsonObject
            {
                ["msgtype"] = "markdown",
                ["markdown"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["title"] = "GAIRR 计划执行通知",
                    ["text"] = $"### GAIRR 计划执行通知\n\n- **状态**：{(success ? "✅ 完成" : "❌ 失败/中止")}\n- **计划**：{plan.Title}（{plan.Id}）\n- **叶子**：{leaf.Title}\n- **问题**：{string.Join("；", leaf.ReviewIssues ?? new())}\n- **时间**：{DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                },
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            await LLMClient.Http.SendAsync(req, ct);
        }
        catch (Exception ex)
        {
            log($"[plan-run] 钉钉通知失败：{ex.Message}");
        }
    }

    /// <summary>截断字符串到上限字符（含省略号）。</summary>
    static string Trim(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";
        return s[..max] + "…";
    }
}