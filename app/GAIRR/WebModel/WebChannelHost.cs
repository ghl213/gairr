using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using GAIRR.Core;

namespace GAIRR;

/// <summary>网页模型通道宿主：把 [WebChannels] 配置的通道注册进 Agent 侧注册表，并实现后端契约。
/// 职责边界：窗口与页面驱动交给 WebModelWindow（站点适配在 adapters/*.js），
/// 本类只管"Agent 消息 → 网页一轮提问"的翻译与上下文复用策略。</summary>
public static class WebChannelHost
{
    static readonly Dictionary<string, WebModelWindow> windows = new(StringComparer.OrdinalIgnoreCase);
    static AppConfig? cfg;

    /// <summary>当前配置（自动修复侧车 WebHealer 读取 AutoRepair 开关与模型列表用）</summary>
    internal static AppConfig? Cfg => cfg;

    /// <summary>任一通道窗口"创建/显示/隐藏"后触发（UI 侧据此刷新标题栏「显示/隐藏」按钮的文案与可见性）</summary>
    public static event Action? ChannelsChanged;

    static void Notify() { try { ChannelsChanged?.Invoke(); } catch { } }

    /// <summary>桌面版启动时调用：按配置注册网页通道 + 绑定后端工厂。
    /// 窗口懒创建（第一次真要调用模型时才弹），未配置通道时等同未启用。</summary>
    public static void Install(AppConfig config)
    {
        try
        {
            cfg = config;
            WebChannelRegistry.Clear();
            var list = config.WebChannels();
            foreach (var spec in list)
                WebChannelRegistry.Register(spec, s => new WebChatBackend(s, config));
            if (list.Count > 0)
                AppendLog($"已注册网页通道 {list.Count} 条：{string.Join("、", list.Select(s => s.Display))}");
        }
        catch (Exception ex)
        {
            AppendLog("注册网页通道失败：" + ex.Message);
        }
    }

    /// <summary>写运行日志（log/webmodel.log，带来源标签），失败不打扰主流程</summary>
    internal static void AppendLog(string msg, string tag = "host")
    {
        try
        {
            System.IO.File.AppendAllText(System.IO.Path.Combine(Paths.LogDir, "webmodel.log"),
                $"[{DateTime.Now:HH:mm:ss}] [{tag}] {msg}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>选中指定通道（模型下拉选中该通道时调用）：确保窗口在后台建好，
    /// **仅当站点未登录时**才弹出来让用户登录；已登录则全程后台静默（Agent 直接调用，不打扰用户）。</summary>
    public static void ShowChannel(WebChannelSpec spec)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        if (!dispatcher.CheckAccess()) { dispatcher.BeginInvoke(() => ShowChannel(spec)); return; }
        var win = EnsureWindow(spec);
        win.UserToggled = false;   // 重新选中：本次由登录态说了算（用户此前的隐藏不跨选择生效）
        _ = AutoShowIfNotLoggedInAsync(win, spec);
    }

    /// <summary>按 provider key 选中通道窗口（供 UI 侧调用，key 形如 web:deepseek-expert）</summary>
    public static void ShowChannelByProvider(string provider)
    {
        if (WebChannelRegistry.TryGet(provider, out var spec)) ShowChannel(spec);
    }

    /// <summary>等页面加载完判定登录态：未登录 → 弹出窗口让用户登录；已登录 → 保持后台静默。
    /// 判定不出（页面异常/超时）也弹出，避免"选了网页模型却什么都没看到"；用户已手动接管显隐时不打扰。</summary>
    static async Task AutoShowIfNotLoggedInAsync(WebModelWindow win, WebChannelSpec spec)
    {
        try
        {
            var logged = await win.IsLoggedInAsync();
            if (win.UserToggled) return;   // 判定期间用户自己点了显示/隐藏：以用户操作为准
            if (logged)
            {
                AppendLog($"[{spec.Display}] 站点已登录：窗口保持后台静默（需要查看时点标题栏「显示」）");
                return;
            }
            AppendLog($"[{spec.Display}] 站点未登录：弹出窗口让用户登录");
            win.ShowForUser();
        }
        catch (Exception ex)
        {
            if (win.UserToggled) return;
            AppendLog($"[{spec.Display}] 登录态判定失败（{ex.Message}）：按未登录处理并弹出窗口");
            win.ShowForUser();
        }
    }

    /// <summary>显示/隐藏通道窗口（标题栏「显示/隐藏」按钮）：隐藏态 → 显示并前置；可见态 → 隐藏到后台。
    /// 返回切换后的可见性；通道未注册返回 false。</summary>
    public static bool ToggleVisible(string provider)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return false;
        if (!dispatcher.CheckAccess()) return dispatcher.Invoke(() => ToggleVisible(provider));
        if (!WebChannelRegistry.TryGet(provider, out var spec)) return false;
        var win = EnsureWindow(spec);   // 还没建过窗口：首次点击即建（后台建好再显示）
        win.UserToggled = true;         // 用户接管显隐：自动弹窗逻辑不再与用户操作打架
        if (win.IsVisible && !win.SilentOffscreen) // 离屏静默态用户看不见 → 视为“未显示”，走显示
        {
            win.HideToBackground();
            return false;
        }
        win.ShowForUser();
        return true;
    }

    /// <summary>该通道窗口当前是否可见（未注册/未创建 → false），供 UI 侧刷新按钮文案</summary>
    public static bool IsVisible(string provider)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return false;
        if (!dispatcher.CheckAccess()) return dispatcher.Invoke(() => IsVisible(provider));
        return windows.TryGetValue(provider, out var w) && w.IsVisible && !w.SilentOffscreen;
    }

    /// <summary>取（必要时**静默创建**）通道窗口；必须在 UI 线程调用，内部已做线程切换。
    /// 复用时刻意不动显隐：Agent 调用需要它待在后台时，绝不把窗口抬到用户面前。
    /// 首次创建用"离屏 + 全透明 + 不入任务栏"建窗（WebView2 需窗口句柄才能初始化），
    /// 是否显示交由登录态判定（未登录才 ShowForUser）或用户点「显示」决定。</summary>
    internal static WebModelWindow EnsureWindow(WebChannelSpec spec)
    {
        var dispatcher = Application.Current?.Dispatcher
            ?? throw new LlmException("没有可用的 UI 线程，无法打开网页模型窗口");
        if (!dispatcher.CheckAccess()) return dispatcher.Invoke(() => EnsureWindow(spec));

        if (windows.TryGetValue(spec.ProviderKey, out var exist) && !exist.ShutdownDone)
            return exist;

        var win = new WebModelWindow(spec);
        win.PrepareSilentStart();
        win.IsVisibleChanged += (_, __) => Notify();   // 显隐变化（含窗口内「隐藏到后台」）→ 刷新标题栏按钮
        WebHealer.Attach(win);   // 挂自动修复侧车（旁路：适配脚本报错/超时 → 自动采样页面并修补适配脚本；AutoRepair=0 时为空操作）
        windows[spec.ProviderKey] = win;
        win.Show();   // 离屏建窗：拿到窗口句柄让 WebView2 开始加载（用户看不见）
        return win;
    }

    /// <summary>程序退出时真正关闭全部网页窗口</summary>
    public static void ShutdownAll()
    {
        foreach (var w in windows.Values.ToList())
        {
            try { w.Shutdown(); } catch { }
        }
        windows.Clear();
    }
}

/// <summary>网页通道后端实现（IWebChatBackend）：把 Agent 的 messages 翻译成网页版能接受的一段提问。
/// 上下文策略：**首轮投递完整上下文**（系统设定 + 协议 + 历史对话 + 本轮新增）建立网页侧会话，
/// 后续轮沿用网页侧已有上下文（只发本轮新增：用户消息或工具结果），避免重复累积；
/// 需重置网页对话并整段重投完整上下文的场景：① 模型异常中止重试 ② 回复未按协议的格式纠正
/// ③ 上层强制（发送「继续」类命令 / 用户点「发送完整上下文」，传 forceNewSession=true）。
/// 工具闭环：网页版没有 function calling 接口，靠 WebReplyParser 的「输出格式约定」把工具清单写进
/// 【系统设定】，再解析模型回的 JSON 拿 tool_calls；解析失败则开新会话并整段重投完整上下文 + 格式纠正段
/// （附上认不出的原文、要求按协议只重答一个标准 JSON 对象），最多纠 3 次，仍认不出才按纯文本回复（不破坏原路径）。
/// 思考展示：站点"深度思考"块的文本由适配脚本上报（reasoning），经窗口差分回调进"思考中"直播卡，
/// 并随 LlmResponse.ReasoningContent 交还上层落卡；模型在 JSON 里写 reasoning 字段时同样解析出来。</summary>
sealed class WebChatBackend : IWebChatBackend
{
    readonly WebChannelSpec spec;
    readonly AppConfig cfg;

    public string Display => spec.Display;

    public WebChatBackend(WebChannelSpec spec, AppConfig cfg)
    {
        this.spec = spec;
        this.cfg = cfg;
    }

    public async Task<LlmResponse> ChatAsync(JsonArray messages, JsonArray tools, Action<string>? onDelta,
        CancellationToken ct, Action<string>? onReasoning = null, bool forceNewSession = false)
    {
        var system = new StringBuilder();
        var turns = new List<(string Role, string Text, string Id)>();
        var toolNames = new Dictionary<string, string>(StringComparer.Ordinal);   // tool_call_id → 工具名（渲染工具结果要标名字）
        foreach (var node in messages)
        {
            if (node is not JsonObject m) continue;
            if (m["tool_calls"] is JsonArray tcs)
                foreach (var t in tcs)
                {
                    if (t is not JsonObject to) continue;
                    var cid = WebReplyParser.Str(to["id"]);
                    var cname = WebReplyParser.Str((to["function"] as JsonObject)?["name"] ?? to["name"]);
                    if (cid.Length > 0 && cname.Length > 0) toolNames[cid] = cname;
                }
            var role = WebReplyParser.Str(m["role"]);
            var text = TextOf(m["content"]);
            // 助手发起工具调用的那轮 content 通常为空：补一行调用摘要，重发上下文时模型才知道自己调过什么（含参数）
            if (role == "assistant" && m["tool_calls"] is JsonArray own)
            {
                var line = ToolCallsLine(own);
                if (line.Length > 0) turns.Add(("assistant", line, ""));
            }
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (role == "system") { if (system.Length > 0) system.Append('\n'); system.Append(text.Trim()); }
            else turns.Add((role, text.Trim(), WebReplyParser.Str(m["tool_call_id"])));
        }

        var userTexts = turns.Where(t => t.Role == "user").Select(t => t.Text).ToList();
        if (userTexts.Count == 0)
            throw new LlmException($"网页模型「{spec.Display}」调用失败：没有可发送的用户消息");

        // 尾部工具结果：工具调用轮结束后 history 只追加 tool 消息（没有新的 user 轮），
        // 网页模型看不到 tool 角色，必须显式回传（带工具名渲染），否则模型无从判断结果对应哪次调用。
        var tailTools = new List<(string Role, string Text, string Id)>();
        for (var i = turns.Count - 1; i >= 0 && turns[i].Role == "tool"; i--) tailTools.Insert(0, turns[i]);

        // 首轮：完整上下文建立网页侧会话；后续轮：只发本轮新增（用户消息或工具结果）。
        // toolOnly 判据：尾部这批工具结果之外没有新的用户提问 → 本轮要交付的就是这批工具结果；
        // 否则交付最后一条用户消息。首轮之外的历史不进提示词，避免与网页侧已有上下文重复。
        int lastUser = LastUserIndex(turns);
        bool toolOnly = tailTools.Count > 0 && lastUser < turns.Count - tailTools.Count;

        // 协议启用条件：本轮带工具清单（无工具的会话维持原纯文本路径不变）
        var protocol = WebReplyParser.ProtocolSection(tools);

        // 增量提示词（只发本轮新增：用户消息或工具结果）——后续轮沿用网页侧已有上下文，避免重复累积
        string increment = toolOnly ? ToolsSection(tailTools, toolNames) : "【本轮请求】\n" + userTexts[^1];
        // 完整上下文（系统设定 + 协议 + 全部历史 + 本轮交付）：首轮建会话、以及「发送继续 / 格式纠正 / 发送完整上下文」
        // 等强制新会话场景整段重投用（重置后的网页侧从空历史起步，必须拿到全部上下文才能续上）
        int tailStart = toolOnly ? turns.Count - tailTools.Count : lastUser;
        bool isFirstTurn = tailStart == 0 && !toolOnly;   // 首轮：无历史、无工具结果，只有本轮用户消息
        var fullSb = new StringBuilder();
        if (system.Length > 0) fullSb.Append("【系统设定】\n").Append(system).Append("\n\n");
        if (protocol.Length > 0) fullSb.Append(protocol).Append("\n\n");
        if (tailStart > 0)
        {
            fullSb.Append("【历史对话】\n");
            for (var i = 0; i < tailStart; i++)
                fullSb.Append(RoleLabel(turns[i].Role)).Append(turns[i].Text).Append('\n');
            fullSb.Append('\n');
        }
        fullSb.Append(increment);
        string fullPrompt = fullSb.ToString();
        // 首轮或强制新会话：整段投递完整上下文；否则沿用网页侧已有上下文只发增量
        string prompt = (isFirstTurn || forceNewSession) ? fullPrompt : increment;

        // 站点开关：[WebChannels] DeepThink/Search 控制（专家通道默认都开）
        bool deepThink = cfg.WebChannelDeepThink;
        bool webSearch = cfg.WebChannelWebSearch;

        // 验收/排查用：记下本轮投递内容的构成（历史段数 / 尾部工具结果 / 协议段 / 总字数）
        WebChannelHost.AppendLog($"[{spec.Display}] 本轮投递网页：{(isFirstTurn || forceNewSession ? "完整上下文" : "本轮新增")}{(forceNewSession ? "（强制新会话）" : "")}（用户消息 {userTexts.Count} 条、历史 {tailStart} 段、尾部工具结果 {tailTools.Count} 条、协议段 {protocol.Length} 字）→ {(isFirstTurn || forceNewSession ? "建/重置会话" : "后续轮增量投递")}，共 {prompt.Length} 字", "chat");
        var gate = new WebReplyParser.DeltaGate(onDelta);   // 协议模式下抑制 JSON 原文流，只放行解析后的正文
        var win = WebChannelHost.EnsureWindow(spec);
        var delivered = false;   // 页面确证本条已送出（适配脚本的 sent 消息）才置位：投递前失败 与 已投递后异常 的判据
        try
        {
            // newChat：强制新会话（forceNewSession）或异常重试（attempt>0）时重置网页对话开新会话，否则沿用网页侧上下文
            LlmResponse resp = null!;
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    // 强制新会话或异常重试时重置网页对话并整段重投完整上下文；正常发消息沿用网页侧上下文
                    bool newChat = attempt > 0 || forceNewSession;
                    resp = await win.AskAsync(prompt, deepThink, webSearch, newChat: newChat, ct, gate.Feed, () => delivered = true, onReasoning);
                    break;
                }
                catch (LlmException ex) when (attempt < 2 && IsAbortRetryable(ex, ct))
                {
                    var waitSec = attempt == 0 ? 3 : 8; // 间隔递增：3s → 8s
                    WebChannelHost.AppendLog("[" + spec.Display + "] 网页模型异常中止（" + Short(ex.Message) + "）：" + waitSec + "s 后开新会话重试（第 " + (attempt + 1) + "/2 次）", "chat");
                    delivered = false; // 重投前清标记：本轮是否送达以重试那次为准
                    gate = new WebReplyParser.DeltaGate(onDelta); // 换新闸门，避免把上一次的缓冲重复补推
                    await Task.Delay(TimeSpan.FromSeconds(waitSec), ct);
                }
            }
            // 按接口协议把回复解析成 content + tool_calls（+ 可选思考）：网页通道由此也能真正发起工具调用
            var (finalResp, parsed) = WebReplyParser.Apply(resp, gate, onReasoning);
            // 格式纠正：协议模式下模型回复未能识别为标准协议 JSON 时，开新会话并整段重投完整上下文 + 格式纠正段
            // （重置网页对话后从空历史起步，必须重投系统设定/协议/全部历史，模型才能按约定重答），最多纠正 3 次；
            // 仍失败则按下方纯文本回退。
            for (int retry = 1; !parsed && protocol.Length > 0 && retry <= 3; retry++)
            {
                WebChannelHost.AppendLog($"[{spec.Display}] 回复未按协议：开新会话并整段重投完整上下文+格式纠正（第 {retry}/3 次）", "chat");
                var retryGate = new WebReplyParser.DeltaGate(onDelta);
                // 完整上下文 + 格式纠正段（附认不出的原文、要求按【输出格式约定】只重答一个标准 JSON 对象）：
                // 新开会话从空历史起步，整段重投后模型既知道要答什么、也知道格式要求与上一条错在哪
                var correctionPrompt = fullPrompt + CorrectionSuffix(resp.Content);
                var retryResp = await win.AskAsync(correctionPrompt, deepThink, webSearch, newChat: true, ct, retryGate.Feed, () => delivered = true, onReasoning);
                (finalResp, parsed) = WebReplyParser.Apply(retryResp, retryGate, onReasoning);
            }
            if (protocol.Length > 0)
                WebChannelHost.AppendLog(parsed
                    ? $"[{spec.Display}] 回复按协议解析成功：正文 {finalResp.Content?.Length ?? 0} 字，思考 {finalResp.ReasoningContent?.Length ?? 0} 字，tool_calls {finalResp.ToolCalls.Count} 个（{string.Join("、", finalResp.ToolCalls.Take(5).Select(c => c.Name))}）"
                    : $"[{spec.Display}] 回复未按协议（{resp.Content?.Length ?? 0} 字）：按纯文本处理（原有路径不变）", "chat");
            return finalResp;
        }
        catch (Exception ex)
        {
            // 首轮建会话后网页侧保留跨轮状态；异常时按是否已投递区分日志，便于排查。
            WebChannelHost.AppendLog(delivered
                ? $"[{spec.Display}] 本轮已投递后异常（{Short(ex.Message)}）：下一轮沿用网页侧上下文，只发本轮新增"
                : $"[{spec.Display}] 本轮投递前失败（{Short(ex.Message)}）：网页上下文未变，下一轮仍整段重发完整上下文", "chat");
            win.RaiseRepairSignal($"本轮问答异常：{Short(ex.Message, 200)}");   // 自动修复侧车旁路取证（与站点 error 消息可能重复，冷却去重）
            throw;
        }
    }

    /// <summary>判定网页模型异常是否值得「开新会话重试」：只对站点侧异常中止（模型异常、内容可能未送达等）重试；
 /// 用户主动取消（OperationCanceledException）与前置校验失败（未登录/未找到输入框等）不重试，避免无谓等待。
 /// 已投递后异常与投递前失败都可重试：首轮本来就是「重置网页对话 + 整段投递完整上下文」，重投不会重复历史。</summary>
 static bool IsAbortRetryable(Exception ex, CancellationToken ct)
 {
 if (ct.IsCancellationRequested) return false;
 if (ex is OperationCanceledException) return false;
 var msg = ex.Message ?? "";
 // 前置校验类失败：页面未就绪、未登录、输入框缺失、发送未生效 —— 重试也拿不到结果
 if (msg.Contains("未找到输入框") || msg.Contains("未登录") || msg.Contains("已取消")
 || msg.Contains("写入失败") || msg.Contains("发送未生效") || msg.Contains("适配脚本未注入"))
 return false;
 return true;
 }

 /// <summary>格式纠偏追加段：上一轮回复无法识别为协议 JSON 时，拼在本轮完整上下文之后整段重投——
    /// 附上模型那条认不出的原文，要求它严格按【输出格式约定】只重答一个标准协议 JSON 对象
    /// （role/content/tool_calls），并明确禁止 XML/DSML 标签等其它包裹形态。
    /// 原文截到 3000 字，避免撑爆网页输入框。</summary>
    static string CorrectionSuffix(string? badReply)
    {
        var bad = (badReply ?? "").Trim();
        if (bad.Length > 3000) bad = bad.Substring(0, 3000) + "…（过长已截断）";
        if (bad.Length == 0) bad = "（空回复）";
        return "\n\n【格式纠正】你上一条回复不符合上面的【输出格式约定】，系统无法识别为协议 JSON。\n"
            + "请现在重新回复：整条回复必须是且仅是这一个 JSON 对象（顶层只有 role / content / tool_calls 三个字段，role 固定为 \"assistant\"），\n"
            + "不要用 XML、DSML 标签或 Markdown 代码块等任何其它形态包裹，也不要在 JSON 之外输出解释、寒暄或任何文字。\n"
            + "需要调用工具时把调用放进 tool_calls（function.name 必须取自【可用工具清单】，arguments 是一个 JSON 字符串）；\n"
            + "不需要调用工具时写 \"tool_calls\":[] 并把完整答复写进 content。\n\n"
            + "【你上一条无法识别的回复】\n" + bad + "\n\n"
            + "请严格按【输出格式约定】重新回复。";
    }

    /// <summary>把助手一轮里的工具调用压成一行摘要（重发上下文时给模型"自己调过什么、参数是什么"的线索）</summary>
    static string ToolCallsLine(JsonArray calls)
    {
        var parts = new List<string>();
        foreach (var node in calls)
        {
            if (node is not JsonObject o) continue;
            var fn = o["function"] as JsonObject ?? o;
            var name = WebReplyParser.Str(fn["name"]);
            if (name.Length == 0) continue;
            var args = WebReplyParser.Str(fn["arguments"]);
            parts.Add(args.Length == 0 ? name : $"{name}({Short(args, 200)})");
        }
        return parts.Count == 0 ? "" : "调用工具：" + string.Join("；", parts);
    }

    /// <summary>尾部工具结果的网页可读渲染：网页模型没有 tool 角色，必须显式标注工具名与结果，
    /// 否则模型无从判断"这条结果对应哪次调用"（toolNames 由 assistant 消息里的 tool_calls 建立）</summary>
    static string ToolsSection(List<(string Role, string Text, string Id)> tail, Dictionary<string, string> names)
    {
        var sb = new StringBuilder("【工具执行结果】\n");
        foreach (var (_, text, id) in tail)
            sb.Append("◆ ").Append(names.TryGetValue(id, out var n) ? n : "工具").Append("：\n").Append(text).Append('\n');
        return sb.ToString();
    }

    /// <summary>历史消息的角色标签（tool 结果单独标注，利于模型区分"自己说过的话"与"工具返回"）</summary>
    static string RoleLabel(string role) => role switch
    {
        "user" => "用户：",
        "tool" => "工具结果：",
        _ => "助手：",
    };

    /// <summary>最后一条 user 消息在 turns 里的下标（本轮请求的起点；找不到返回 0）</summary>
    static int LastUserIndex(List<(string Role, string Text, string Id)> turns)
    {
        for (var i = turns.Count - 1; i >= 0; i--)
            if (turns[i].Role == "user") return i;
        return 0;
    }

    /// <summary>异常信息压成单行短文本（异常消息可能很长或含换行，日志里只留可读的一段）</summary>
    static string Short(string? msg, int n = 160)
    {
        if (string.IsNullOrEmpty(msg)) return "(无消息)";
        var one = msg.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return one.Length <= n ? one : one.Substring(0, n) + "…";
    }

    /// <summary>取出 content 的纯文本（兼容字符串与多模态数组两种形态）</summary>
    static string TextOf(JsonNode? node)
    {
        if (node == null) return "";
        if (node is JsonValue v && v.TryGetValue<string>(out var s)) return s ?? "";
        if (node is JsonArray arr)
        {
            var sb = new StringBuilder();
            foreach (var item in arr)
            {
                if (item is JsonObject o && o["text"] is JsonNode t) sb.Append(TextOf(t));
                else sb.Append(TextOf(item));
            }
            return sb.ToString();
        }
        return node.ToJsonString();
    }
}
