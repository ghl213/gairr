using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using GAIRR.Core;
using Microsoft.Web.WebView2.Core;

namespace GAIRR;

/// <summary>网页模型通道的独立宿主窗口：不依赖主窗口，自带 WebView2 页面与工具条，
/// 站点适配逻辑全部由 WebModel/adapters/&lt;适配器&gt;.js 承担（C# 侧只调五个 JS 入口，含 __gairrNewChatVerify）。
/// 登录态用固定 UserDataFolder 持久化（data/webmodel/&lt;适配器&gt;），一次登录长期有效。
/// 关窗默认只隐藏（后台继续供 Agent 调用），需要真关由 WebChannelHost.Shutdown 触发。
/// 提问协议：C# 先保证"适配脚本在页面里 + 页面可对话"，需要重置对话时**由本类在提问前**完成
/// （页面内点"新对话"，点不到或切换未确认则回到站点首页重建会话），然后才调 __gairrAsk —— 绝不在提问途中让页面跳转，
/// 否则正在等待的脚本会被文档销毁，表现为"页面什么都没收到"。
/// 重置协议补充：点击"新对话"不等于生效，必须在 800ms 内轮询 __gairrNewChatVerify 确认会话确实切换
/// （URL 变化/消息节点归零/输入框清空）才按 'clicked' 处理，否则按 'none' 降级走"回到站点首页"路径
/// （提示词尚未投递，当前轮不被打断）；候选入口在 JS 侧已排除带 href 的 a 标签，杜绝整页导航。</summary>
public partial class WebModelWindow : Window
{
    readonly WebChannelSpec spec;
    readonly SemaphoreSlim gate = new(1, 1);                        // 串行：一个网页窗口同时只跑一轮
    readonly TaskCompletionSource<bool> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly object sync = new();
 readonly Random rnd = new(); // 投递节流：3~5s 随机间隔用
 DateTime lastDeliverAt = DateTime.UtcNow; // 上次向页面投递提示词的时刻（节流基准）；初始视为"刚投递过"，避免首轮 (int)TotalMilliseconds 溢出成负数导致 Task.Delay 抛异常

    TaskCompletionSource<string>? pending;      // 当前轮完成信号
    Action<string>? pendingDelta;               // 当前轮正文增量回调
    Action<string>? pendingReasoning;           // 当前轮思考（深度思考块）增量回调
    Action? pendingDelivered;                   // 当前轮"页面确证已送达"回调（收到 sent 消息时触发一次后摘除）
    string lastFull = "";                       // JS 回传的是累积全文，这里差分出增量
    string lastReasoning = "";                  // 当前轮思考累积全文（同样累积制，差分出增量；轮结束后随 LlmResponse 交还上层）
    string currentRequestId = "";
    bool initialized, allowClose, initInFlight;   // initInFlight：防止并发重入（首调仍在 await 时第二个入口进来）

    public WebChannelSpec Spec => spec;

    /// <summary>站点适配脚本文件（exe/WebModel/adapters/&lt;适配器&gt;.js，随程序发布；改脚本无需重新编译，
    /// 每次注入都从磁盘重读 → 自动修复侧车落盘新脚本后，页面重载/下次注入即生效）</summary>
    internal string AdapterJsFile => Path.Combine(AppContext.BaseDirectory, "WebModel", "adapters", spec.Adapter + ".js");

    /// <summary>本轮问答是否空闲（gate 无人持锁）：自动修复侧车只在空闲时重载页面，绝不打断进行中的轮次</summary>
    internal bool IdleNow => gate.CurrentCount > 0;

    /// <summary>修复信号（自动修复侧车 WebHealer 订阅）：适配脚本报错 / 页面导航打断 / 注入失败等。
    /// 只带可读原因；侧车在后台旁路处理（只读取证 + 离路修补），不阻塞也不打断当前问答。</summary>
    internal event Action<string>? RepairSignal;

    /// <summary>发出修复信号（无订阅者时为空操作；订阅者异常不影响主路）</summary>
    internal void RaiseRepairSignal(string reason)
    {
        try { RepairSignal?.Invoke(reason); } catch { }
    }

    /// <summary>自动修复侧车用：只读采样活动页面（输入框/按钮/正文类节点），返回 JSON 文本；页面不可达返回空串</summary>
    internal Task<string?> ProbePageAsync() => ExecJsAsync(WebHealer.ProbeJs);

    /// <summary>自动修复侧车用：补丁落盘后重载页面（仅无进行中轮次时），使下次提问从磁盘重新注入新脚本；返回是否已重载</summary>
    internal async Task<bool> ReloadForHealAsync()
    {
        if (!IdleNow) return false;
        try { await ReloadAndWaitAsync(CancellationToken.None); return true; }
        catch { return false; }
    }

    /// <summary>自动修复侧车用：日志（webmodel.log tag=heal + 窗口底栏可见）</summary>
    internal void LogHeal(string msg) => Log("[heal] " + msg);

    public WebModelWindow(WebChannelSpec spec)
    {
        this.spec = spec;
        InitializeComponent();
        Title = $"网页模型通道 - {spec.Display}";
        SiteTitle.Text = spec.Display;
        Loaded += async (_, __) => await InitAsync();
    }

    /* ==================== 初始化：WebView2 环境 + 注入站点脚本 ==================== */

    async Task InitAsync()
    {
        if (initialized || initInFlight) return;   // 并发初始化进行中/已完成：直接返回（防两个入口并发二次调用 EnsureCoreWebView2Async）
        // 线程边界：本方法直接读写 WebView2 控件（WPF 依赖对象，带 Dispatcher 线程校验）。
        // 首次调用可能来自 Agent 后台线程（AskAsync → ReloadAndWaitAsync → InitAsync），
        // 非 UI 线程触碰 Browser 会抛“调用线程无法访问此对象，因为另一个线程拥有该对象”。
        if (!Dispatcher.CheckAccess())
        {
            // 调度本身也可能失败（窗口已销毁 / Dispatcher 已关闭）：异常不能留在后台线程裸冒泡，
            // 更不能让 ready 永远悬着——否则并发的 AskAsync 会白等满 2 分钟初始化超时才失败
            try { await Dispatcher.InvokeAsync(() => InitAsync()).Task.Unwrap(); }
            catch (Exception ex)
            {
                Log("[init] 调度网页环境初始化失败：" + ex.Message);
                ready.TrySetException(new LlmException($"网页模型窗口「{spec.Display}」初始化失败：{ex.Message}"));
            }
            return;
        }
        initInFlight = true;                        // 在 UI 线程置位：重入路径经 Dispatcher 串行化，并发入口只能看到已置位的标志
        try
        {
            var userData = Path.Combine(Paths.DataDir, "webmodel", spec.Adapter);
            Directory.CreateDirectory(userData);
            // 独立 UserDataFolder：登录态/缓存与主程序隔离，且长期保留（扫码一次即可）
            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            await Browser.EnsureCoreWebView2Async(env);

            var core = Browser.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.AreDevToolsEnabled = true;                 // 站点改版时可在窗口内 F12 排查
            core.Settings.AreBrowserAcceleratorKeysEnabled = true;
            core.WebMessageReceived += OnWebMessage;
            core.NavigationCompleted += (_, e) => Log($"页面加载{(e.IsSuccess ? "完成" : "失败")}：{core.Source}");
            core.NavigationStarting += OnNavigationStarting;         // 等待回复期间页面跳转 → 立即失败，不空等 20 分钟

            var script = LoadAdapterScript();
            if (script != null) await core.AddScriptToExecuteOnDocumentCreatedAsync(script);

            if (!string.IsNullOrWhiteSpace(spec.Url)) core.Navigate(spec.Url);
            initialized = true;
            ready.TrySetResult(true);
            Log("网页环境就绪，请在该窗口内登录后即可在模型下拉选用本通道");
        }
        catch (Exception ex)
        {
            initInFlight = false;                   // 初始化失败清标志：保留"重载页面"按钮的手工重试路径
            Log("网页环境初始化失败：" + ex.Message);
            ready.TrySetException(new LlmException($"网页模型窗口「{spec.Display}」初始化失败：{ex.Message}"));
        }
    }

    /// <summary>读取站点适配脚本（exe/WebModel/adapters/&lt;适配器&gt;.js，随程序发布；改脚本无需重新编译）</summary>
    string? LoadAdapterScript()
    {
        try
        {
            var file = Path.Combine(AppContext.BaseDirectory, "WebModel", "adapters", spec.Adapter + ".js");
            if (File.Exists(file)) return File.ReadAllText(file, Encoding.UTF8);
            Log("未找到站点适配脚本：" + file);
            return null;
        }
        catch (Exception ex) { Log("读取站点适配脚本失败：" + ex.Message); return null; }
    }

    /// <summary>页面发生跳转时：若正有一轮在等待回复，说明该轮被销毁（脚本上下文没了）——
    /// 立刻以可读原因失败，避免上层白等 20 分钟。
    /// 抛 WebContextLostException 而非普通 LlmException：跳转会重建页面上下文，
    /// 即便"本条已送出"（sent 已回调）也不能按"已同步"推进——否则下一轮会往重建后的空对话里只发一条。</summary>
    void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        TaskCompletionSource<string>? t = null;
        lock (sync)
        {
            if (pending != null) { t = pending; pending = null; pendingDelta = null; pendingReasoning = null; pendingDelivered = null; currentRequestId = ""; }
        }
        if (t == null) return;
        Log($"等待回复期间页面发生跳转（{e.Uri}）：本轮已中止，网页上下文已重建");
        RaiseRepairSignal($"等待回复期间页面发生跳转（{e.Uri}）：疑似「新对话」入口误中导航链接或站点导航形态变化");
        t.TrySetException(new WebContextLostException(
            $"网页模型「{spec.Display}」本轮被页面跳转打断（{e.Uri}），网页上下文已重建，内容可能未送达：请重发；" +
            "若反复出现请把 log/webmodel.log 反馈（站点改版）"));
    }

    /* ==================== 对外：一轮问答 ==================== */

    /// <summary>把一段提示词送进页面并等回复（由 WebChatBackend 调用；内部串行，自动切 UI 线程）。
    /// deepThink/webSearch 为站点开关；newChat 表示本轮先重置网页对话——正常发消息传 false（沿用网页侧上下文），
    /// 仅模型异常重试时传 true：重置网页对话 + 整段重投完整上下文，避免与网页侧残留历史重复累积。
    /// onDelivered：页面确证"本条已送出"（收到适配脚本的 sent 消息）时回调一次，
    /// 供调用方区分"投递前失败"与"已投递后异常"（只作日志判据：首轮整段投递，后续轮增量投递）。
    /// onReasoning：页面"深度思考"块的累积文本差分后的增量（无思考块则从不触发）；
    /// 本轮抓到的思考全文随返回的 LlmResponse.ReasoningContent 一并交还，供上层落思考卡。</summary>
    public async Task<LlmResponse> AskAsync(string prompt, bool deepThink, bool webSearch, bool newChat,
        CancellationToken ct, Action<string>? onDelta, Action? onDelivered = null, Action<string>? onReasoning = null)
    {
        try { await ready.Task.WaitAsync(TimeSpan.FromMinutes(2), ct); }
        catch (TimeoutException)
        {
            throw new LlmException($"网页模型窗口「{spec.Display}」初始化超时（WebView2 运行库缺失或页面打不开），详见 log/webmodel.log");
        }

        await gate.WaitAsync(ct);
        try
        {
            Log($"[ask] 开始本轮：重置对话={newChat} 深度思考={deepThink} 联网搜索={webSearch} 提示词={prompt.Length}字");
            await EnsureAdapterAsync(ct);
            if (newChat) await ResetConversationAsync(ct);

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var id = Guid.NewGuid().ToString("N");
            lock (sync) { pending = tcs; pendingDelta = onDelta; pendingReasoning = onReasoning; pendingDelivered = onDelivered; lastFull = ""; lastReasoning = ""; currentRequestId = id; }

            using var reg = ct.Register(() => FireAndForget($"window.__gairrCancel && window.__gairrCancel('{id}')"));

            var opts = new JsonObject
            {
                ["id"] = id,
                ["deepThink"] = deepThink,
                ["webSearch"] = webSearch,
                ["timeoutMs"] = 15 * 60 * 1000,   // 页面侧自报错要早于下面的 20 分钟等待超时：否则 C# 先超时，页面侧更有价值的错因（含 DOM 采样）会被丢掉
            };
            var script = "window.__gairrAsk ? window.__gairrAsk(" + JsonValue.Create(prompt)!.ToJsonString() + ", " + opts.ToJsonString() + ")"
                       + " : window.chrome.webview.postMessage({type:'error',msg:'站点适配脚本未注入（可能页面仍在加载）'})";
            // 投递节流：两次向页面投递提示词之间保持 3~5s 随机间隔，避免发太快被站点风控封禁
 var gapMs = 3000 + rnd.Next(0, 2001);
 var sinceMs = (int)(DateTime.UtcNow - lastDeliverAt).TotalMilliseconds;
 if (sinceMs < gapMs)
 {
 var waitMs = gapMs - sinceMs;
 Log($"[ask] 投递节流：等待 {waitMs}ms（距上次投递 {sinceMs}ms，目标间隔 {gapMs}ms）");
 await Task.Delay(waitMs, ct);
 }
 lastDeliverAt = DateTime.UtcNow;
 var ret = await ExecJsAsync(script);
            Log($"[ask] 已投递页面（脚本返回 {Trunc(ret, 120)}）");

            string text;
            try
            {
                text = await tcs.Task.WaitAsync(TimeSpan.FromMinutes(20), ct);
            }
            catch (TimeoutException)
            {
                throw new LlmException($"网页模型「{spec.Display}」等待回复超时（20 分钟）：请确认页面仍处于可对话状态（详见 log/webmodel.log）");
            }
            finally { lock (sync) { pending = null; pendingDelta = null; pendingReasoning = null; pendingDelivered = null; currentRequestId = ""; } }

            if (string.IsNullOrWhiteSpace(text))
                throw new LlmException($"网页模型「{spec.Display}」返回空内容");
            string reasoning;
            lock (sync) reasoning = lastReasoning;   // 本轮页面抓到的思考全文（无思考块则为空串）
            Log($"[ask] 本轮完成，正文 {text.Length} 字，思考 {reasoning.Length} 字");
            return new LlmResponse(text, reasoning.Length > 0 ? reasoning : null,
                new List<LlmToolCall>(), new LlmUsage(0, 0, 0), "stop");
        }
        finally { gate.Release(); }
    }

    /* ==================== 提问前准备：脚本在场 + 对话重置 ==================== */

    /// <summary>确保站点适配脚本已在当前页面上下文里（脚本若被站点导航/清理掉，现场补注入）</summary>
    async Task EnsureAdapterAsync(CancellationToken ct)
    {
        if (await EvalBoolAsync("!!window.__gairrAsk")) return;
        var script = LoadAdapterScript();
        if (script == null)
            throw new LlmException($"网页模型「{spec.Display}」找不到站点适配脚本 WebModel/adapters/{spec.Adapter}.js（随程序发布，勿漏拷）");
        Log("[prep] 页面内没有适配脚本，现场补注入");
        await ExecJsAsync(script);
        if (!await EvalBoolAsync("!!window.__gairrAsk"))
        {
            RaiseRepairSignal("适配脚本注入失败（页面未就绪或脚本报错）");
            throw new LlmException($"网页模型「{spec.Display}」适配脚本注入失败（页面未就绪或脚本报错），详见 log/webmodel.log");
        }
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>重置网页对话（只在提问**之前**做，仅模型异常重试时由 AskAsync(newChat:true) 调用）：
    /// 优先页面内点"新对话"（不重载文档），且点击后必须在 800ms 内验证会话确实切换（URL 变化/消息节点归零/输入框清空）才算生效；
    /// 页面内找不到入口或切换无法确认才回到站点首页（spec.Url，不带会话 id）重建会话并等就绪
    /// ——此时尚未投递提示词，不会丢内容。注意不能用 Reload()：SPA 的会话 id 就在 URL 里，重载只会回到同一个旧会话。</summary>
    async Task ResetConversationAsync(CancellationToken ct)
    {
        Log("[reset] 重置网页对话（仅异常重试时触发：完整上下文由提示词携带，网页侧不留历史以免重复累积）");
        var st = await ExecJsAsync("(window.__gairrNewChat ? window.__gairrNewChat() : 'none')");
        var s = (st ?? "").Trim().Trim('"');
        var clickedInPage = s == "clicked";   // 页面内确实点到了入口（与"压根没入口"是两条不同的排查路径）
        if (clickedInPage) s = await VerifyNewChatSwitchAsync(ct);   // 点击≠生效：验证不过按误点处理，走重载路径
        if (s == "clicked")
        {
            Log("[reset] 已在页面内新建对话（未重载文档）");
            await WaitPageReadyAsync(ct, "新建对话后");
            await Task.Delay(500, ct);
            return;
        }
        // 两条日志按实际路径分开：验收/排查时一眼区分"页面内根本没有新建入口"与"点了但切换未确认"
 Log(clickedInPage
 ? "[reset] 页面内新对话点击后切换未确认 → 回到站点首页重建会话（提示词尚未投递，不会丢）"
 : "[reset] 页面内没有新建入口 → 回到站点首页重建会话（提示词尚未投递，不会丢）");
 await NavigateHomeAndWaitAsync(ct);
 await WaitPageReadyAsync(ct, "回到站点首页后");
        await Task.Delay(500, ct);
    }

    /// <summary>页面内点"新对话"后，在 800ms 窗口内（100ms 间隔）轮询 JS 入口 __gairrNewChatVerify，
    /// 验证会话确实切换：URL 变化 / 消息节点归零 / 输入框清空，任一即可（JS 侧只认点击前就有量的信号）。
    /// 返回 'clicked' = 已验证切换；'none' = 窗口内未确认（调用方降级走重载路径，提示词尚未投递，当前轮不被打断）。
    /// 轮询节奏由 C# 驱动、JS 入口保持同步字符串：SPA 路由切换跑在页面事件循环上，页面内同步等待反而会阻塞切换，
    /// 且 ExecuteScriptAsync 对 Promise 存在序列化歧义（同适配器 __gairrAsk 的注释）。</summary>
    async Task<string> VerifyNewChatSwitchAsync(CancellationToken ct)
    {
        for (var i = 0; i < 8; i++)
        {
            await Task.Delay(100, ct);
            var r = (await ExecJsAsync("(window.__gairrNewChatVerify ? window.__gairrNewChatVerify() : 'pending')") ?? "").Trim().Trim('"');
            if (r == "ok")
            {
                Log($"[reset] 已验证新对话切换生效（约 {(i + 1) * 100}ms：URL 变化/消息节点归零/输入框清空）");
                return "clicked";
            }
        }
        Log("[reset] 新对话点击后 800ms 内未确认切换（URL 未变、消息节点未归零、输入框未清空）→ 按误点处理");
        return "none";
    }

    /// <summary>重载当前页面并等本次导航结束（超时也不抛，交由就绪轮询兜底）</summary>
    async Task ReloadAndWaitAsync(CancellationToken ct)
    {
        // 线程边界：CoreWebView2 的每个成员（取内核、事件订阅、Reload、Source）都带 UI 线程校验，
        // 本方法由 Agent 后台线程经 AskAsync 调用，故“取内核 + 订阅事件 + 触发重载”整段放进一次
        // Dispatcher.InvokeAsync 里，避免后台线程触碰 CoreWebView2 抛
        // “CoreWebView2 members can only be accessed from the UI thread”
        var navStart = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var navDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStart(object? sender, CoreWebView2NavigationStartingEventArgs e) => navStart.TrySetResult(true);
        void OnDone(object? sender, CoreWebView2NavigationCompletedEventArgs e) => navDone.TrySetResult(e.IsSuccess);

        CoreWebView2? core = null;
        var needInit = false;
        try
        {
            await Dispatcher.InvokeAsync(() =>
            {
                var c = Browser.CoreWebView2;
                if (c == null) { needInit = true; return; }
                c.NavigationStarting += OnStart;
                c.NavigationCompleted += OnDone;
                try { c.Reload(); }
                catch (Exception ex)
                {
                    // 触发失败：先退订（不留悬挂订阅），并落日志——由调用方后续的就绪轮询兜底，不向调用方冒泡
                    c.NavigationStarting -= OnStart;
                    c.NavigationCompleted -= OnDone;
                    Log("[reset] 触发页面重载失败：" + ex.Message);
                    return;
                }
                core = c;
            });
        }
        catch (Exception ex)
        {
            // 窗口已销毁 / Dispatcher 已关闭等调度失败：同样不向调用方冒泡，交由外层就绪轮询兜底
            Log("[reset] 调度页面重载失败：" + ex.Message);
            return;
        }
        if (core == null)
        {
            if (needInit) await InitAsync();     // 内核还没起来才初始化；重载触发失败则交给就绪轮询兜底
            return;
        }

        try
        {
            await navStart.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            var ok = await navDone.Task.WaitAsync(TimeSpan.FromSeconds(60), ct);
            var url = "";
            try { url = await Dispatcher.InvokeAsync(() => core.Source); } catch { }   // Source 同样是 UI 线程成员
            Log($"[reset] 页面重载{(ok ? "完成" : "失败")}：{url}");
        }
        catch (TimeoutException) { Log("[reset] 等待页面重载超时，继续按就绪状态轮询兜底"); }
        finally
        {
            try
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    core.NavigationStarting -= OnStart;   // 退订同样必须回 UI 线程
                    core.NavigationCompleted -= OnDone;
                });
            }
            catch (Exception ex) { Log("[reset] 退订导航事件失败：" + ex.Message); }
        }
    }

    /// <summary>回到站点首页并等页面就绪——重置网页对话的兜底路径。
 /// 不能用 Reload()：deepseek/kimi 都是 SPA，会话 id 就在当前 URL 里，重载只会回到同一个旧会话
 /// （旧会话已到长度上限时依旧发不出去，这正是"重置不生效"的根因）；这里显式导航到配置里的站点首页
 /// （spec.Url，不带会话 id），页面加载后即停在新建对话态。首页地址缺失时退回 ReloadAndWaitAsync（保持原行为）。
 /// 超时不抛，交由就绪轮询兜底；此时提示词尚未投递，当前轮不被打断。</summary>
 async Task NavigateHomeAndWaitAsync(CancellationToken ct)
 {
 var home = spec.Url ?? "";
 if (string.IsNullOrWhiteSpace(home)) { await ReloadAndWaitAsync(ct); return; }
 var needInit = false;
 try
 {
 await Dispatcher.InvokeAsync(() =>
 {
 var c = Browser.CoreWebView2;
 if (c == null) { needInit = true; return; }
 try { c.Navigate(home); }
 catch (Exception ex) { Log("[reset] 导航到站点首页失败：" + ex.Message); }
 });
 }
 catch (Exception ex) { Log("[reset] 调度站点首页导航失败：" + ex.Message); return; }
 if (needInit) { await InitAsync(); return; }
 Log("[reset] 已导航到站点首页（不带会话 id）：" + home);
 }

 /// <summary>轮询等页面"可对话"（输入框可用且适配脚本在场）；超时不抛，仍继续尝试提问</summary>
    async Task<bool> WaitPageReadyAsync(CancellationToken ct, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(40);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await EvalBoolAsync("!!(window.__gairrAsk && window.__gairrReady && window.__gairrReady())"))
            {
                Log($"[prep] {what}页面已就绪（输入框可用）");
                return true;
            }
            await Task.Delay(400, ct);
        }
        Log($"[prep] {what}等待页面就绪超时：可能未登录或站点结构变化（仍继续尝试提问）");
        return false;
    }

    /* ==================== 页面消息回传 ==================== */

    void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonObject? o = null;
        try { o = JsonNode.Parse(e.WebMessageAsJson) as JsonObject; } catch { }
        if (o == null) return;

        var type = o["type"]?.GetValue<string>() ?? "";
        switch (type)
        {
            case "log":
            case "phase":
                Log(o["msg"]?.GetValue<string>() ?? "");
                break;

            case "sent":
                {
                    // 页面确证本条已送出（输入框已清空/已出现新回复节点）：回调一次即摘除，
                    // 供 WebChatBackend 把"投递前失败"与"已投递后异常"分开处置
                    Action? cb;
                    lock (sync) { cb = pendingDelivered; pendingDelivered = null; }
                    Log("[ask] 页面确证本条已送出（sent）");
                    try { cb?.Invoke(); } catch (Exception ex) { Log("已送达回调异常：" + ex.Message); }
                    break;
                }

            case "delta":
                {
                    var full = o["text"]?.GetValue<string>() ?? "";
                    Action<string>? cb;
                    string inc;
                    lock (sync)
                    {
                        cb = pendingDelta;
                        if (cb == null) return;
                        inc = full.StartsWith(lastFull, StringComparison.Ordinal) ? full.Substring(lastFull.Length) : full;
                        lastFull = full;
                    }
                    if (inc.Length > 0) cb(inc);
                    break;
                }

            case "reasoning":
                {
                    // 页面"深度思考"块的累积文本 → 差分出增量回调（进"思考中"直播卡）
                    var full = o["text"]?.GetValue<string>() ?? "";
                    Action<string>? cb;
                    string inc;
                    lock (sync)
                    {
                        if (pending == null) return;   // 本轮已结束：迟到的思考消息直接丢弃，避免污染下一轮
                        cb = pendingReasoning;
                        inc = full.StartsWith(lastReasoning, StringComparison.Ordinal) ? full.Substring(lastReasoning.Length) : full;
                        lastReasoning = full;          // 回调为空也要留存：轮结束时随 LlmResponse.ReasoningContent 交还
                    }
                    if (cb != null && inc.Length > 0) cb(inc);
                    break;
                }

            case "done":
                {
                    var text = o["text"]?.GetValue<string>() ?? "";
                    TaskCompletionSource<string>? t;
                    lock (sync) t = pending;
                    t?.TrySetResult(text);
                    break;
                }

            case "error":
                {
                    var msg = o["msg"]?.GetValue<string>() ?? "未知错误";
                    // 站点报错但本轮已收正文能按协议 JSON 正常解析时：不判「模型异常」中止，按正常回复收尾（解析交上层）
 {
 TaskCompletionSource<string>? ts;
 string salvaged;
 lock (sync) { ts = pending; salvaged = lastFull; }
 if (ts != null && !string.IsNullOrWhiteSpace(salvaged) && WebReplyParser.Parse(salvaged).Parsed)
 {
 Log("站点报错但本轮已收正文可正常解析（JSON 完整）：按正常回复收尾，不判模型异常");
 ts.TrySetResult(salvaged);
 return; // 抢救成功：按正常回复收尾，不再上抛「模型异常」（也避免侧车误判本轮失败）
 }
 }
 Log("站点报错：" + msg);
                    RaiseRepairSignal("站点报错：" + msg);   // 自动修复侧车旁路取证（不打断主路：异常照常上抛）
                    TaskCompletionSource<string>? t;
                    lock (sync) t = pending;
                    t?.TrySetException(new LlmException($"网页模型「{spec.Display}」失败：{msg}"));
                    break;
                }
        }
    }

    /* ==================== 工具条 ==================== */

    /// <summary>工具条：隐藏到后台。事件处理器由 WPF 在 UI 线程调用，Hide() 与 Log() 均安全。</summary>
    void OnHideClick(object sender, RoutedEventArgs e)
    {
        Left = -32000; Top = -32000; // 移出屏幕外（不调 Hide）：WebView2 仍判定页面可见、不节流定时器，Agent 抓取照常
 SilentOffscreen = true; // 标记离屏静默：对用户不可见，点「显示」再搬回屏幕中央
        Log("窗口已隐藏：网页仍在后台运行，Agent 可继续调用；重新显示请在模型下拉再选一次本通道");
    }

    async void OnReloadClick(object sender, RoutedEventArgs e)
    {
        try
        {
            // 统一经 GetCoreAsync / InitAsync 通道取内核（本处理器虽在 UI 线程，也走同一入口，防止日后被非 UI 线程复用）；
            // CoreWebView2 成员带 UI 线程校验，await 之后不依赖 SynchronizationContext 回跳，显式在 Dispatcher.InvokeAsync 内执行
            var core = await GetCoreAsync();
            if (core == null) { await InitAsync(); return; }
            await Dispatcher.InvokeAsync(() => core.Reload());
            Log("已重载页面");
        }
        catch (Exception ex) { Log("重载失败：" + ex.Message); }
    }

    async void OnDevToolsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var core = await GetCoreAsync();
            if (core == null) { Log("网页内核尚未就绪，无法打开开发者工具"); return; }
            await Dispatcher.InvokeAsync(() => core.OpenDevToolsWindow());   // CoreWebView2 成员带 UI 线程校验，显式回 UI 线程执行
            Log("已打开开发者工具（排查站点改版用）");
        }
        catch (Exception ex) { Log("打开开发者工具失败：" + ex.Message); }
    }

    async void OnResetLoginClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var core = await GetCoreAsync();
            if (core == null) return;
            if (MessageBox.Show("将清除本通道的登录态（Cookie 与本地存储）并回到首页，确认继续？", "清登录态",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            // CookieManager/ExecuteScriptAsync/Navigate 均为 CoreWebView2 成员，整段显式回 UI 线程执行；
            // 委托显式声明为 Func<Task>（若写成无返回值的 async lambda 会被绑定成 InvokeAsync(Action) 的
            // async void，外层 await 会在动作"开始"时就返回，而非等 UI 工作真正完成），.Task.Unwrap() 等到底
            Func<Task> doReset = async () =>
            {
                core.CookieManager.DeleteAllCookies();
                await core.ExecuteScriptAsync("try{localStorage.clear();sessionStorage.clear();}catch(e){}");
                core.Navigate(string.IsNullOrWhiteSpace(spec.Url) ? "about:blank" : spec.Url);
            };
            await Dispatcher.InvokeAsync(doReset).Task.Unwrap();
            Log("已清除登录态并回首页，请重新登录");
        }
        catch (Exception ex) { Log("清登录态失败：" + ex.Message); }
    }

    /* ==================== 生命周期 ==================== */

    /// <summary>被用户手动接管过显隐（标题栏「显示/隐藏」点过）：自动弹窗逻辑不再与用户操作打架。
    /// 每次重新选中该通道时由宿主清零，重新按登录态决定是否弹出。</summary>
    internal bool UserToggled { get; set; }

 /// <summary>是否处于“静默离屏”态（建窗后尚未搬回屏幕中央）：此时窗口 IsVisible 为 true
 /// 但用户根本看不见；标题栏「显示/隐藏」判断必须把它当作“未显示给用户”，否则首次点击会被误判为隐藏。</summary>
 internal bool SilentOffscreen { get; private set; }

    /// <summary>静默首建：离屏 + 不入任务栏 + 不抢焦点建窗。WebView2 必须拿到窗口句柄才能真正加载，
    /// 而"默认隐藏、只在未登录时显示"又要求它别闪现在用户面前 —— 故先这样把窗口建在屏幕外，
    /// 等 __gairrLoggedIn 判完登录态再由 ShowForUser() 搬回屏幕中央（已登录则一直待在屏外静默）。
    /// 注意：这里不用 Opacity=0 —— WPF 遇到半透明祖先会停掉 HwndHost 渲染，WebView2 会跟着不渲染。
    /// 必须在 Show() 之前调用。</summary>
    internal void PrepareSilentStart()
    {
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000; Top = -32000;   // 屏幕外：用户看不见，但窗口"处于显示态"，WebView2 照常加载与渲染
        ShowInTaskbar = false;
        ShowActivated = false;         // 建窗不抢焦点
 SilentOffscreen = true; // 标记离屏静默：对用户“不可见”，直到 ShowForUser 搬回屏幕
    }

    /// <summary>把窗口显示给用户（未登录需要登录、或用户点「显示」）：搬回屏幕中央并前置一次。
    /// 可从任意线程调用（宿主可能在 UI 线程外的 Continuation 上决定弹窗）。</summary>
    internal void ShowForUser()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(ShowForUser)); return; }
        SilentOffscreen = false; // 已搬回屏幕中央：不再算离屏静默态
 ShowInTaskbar = true;
        ShowActivated = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        var area = SystemParameters.WorkArea;
        var w = Width > 0 ? Width : 1160;
        var h = Height > 0 ? Height : 880;
        Left = area.Left + Math.Max(0, (area.Width - w) / 2);
        Top = area.Top + Math.Max(0, (area.Height - h) / 2);
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true; Topmost = false;   // 前置一次即取消置顶，避免长期压住其它窗口
        Log("窗口已显示：可在其中登录站点（登录后再在模型下拉选用本通道）");
    }

    /// <summary>隐藏到后台（网页仍在后台运行，Agent 可继续调用）：窗口内「隐藏到后台」与标题栏「隐藏」共用</summary>
    internal void HideToBackground()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(HideToBackground)); return; }
        OnHideClick(this, new RoutedEventArgs());
    }

    /// <summary>判定站点登录态（等页面加载完，最多 timeoutSeconds 秒）：true=已登录可对话，false=未登录/判定超时。
    /// 判据由适配脚本的 __gairrLoggedIn 给出（'in' 已登录 / 'out' 未登录 / 'loading' 还在加载）：
    /// 只有"页面已加载完却没有输入框"才算未登录（未登录页面根本没有对话输入框）。
    /// 判定超时按未登录处理——宁可弹出窗口让用户自己看，也不要"选了网页模型却一片安静"。</summary>
    internal async Task<bool> IsLoggedInAsync(int timeoutSeconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        var firstOut = DateTime.MinValue;
        while (DateTime.UtcNow < deadline)
        {
            try { await ready.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None); }
            catch (TimeoutException) { continue; }   // 环境还在初始化：等下一轮（初始化失败会抛异常，由调用方兜底弹窗）
            var st = await LoginStateAsync();
            if (st == "in") return true;
            if (st == "out")
            {
                // SPA 首屏渲染稍慢也可能一时看不到输入框：连续 ≥2s 都是"未登录"才下结论，避免把已登录用户误弹
                if (firstOut == DateTime.MinValue) firstOut = DateTime.UtcNow;
                else if ((DateTime.UtcNow - firstOut).TotalSeconds >= 2) return false;
            }
            await Task.Delay(500, CancellationToken.None);
        }
        Log("[login] 登录态判定超时，按未登录处理（弹出窗口让用户确认）");
        return false;
    }

    /// <summary>取页面登录态原始结论（'in'/'out'/'loading'）；适配脚本未提供该入口时退回 __gairrReady 兜底</summary>
    async Task<string> LoginStateAsync()
    {
        const string js = "(window.__gairrLoggedIn ? window.__gairrLoggedIn() "
                        + ": (window.__gairrReady && window.__gairrReady() ? 'in' : 'out'))";
        return (await ExecJsAsync(js) ?? "").Trim().Trim('"');
    }

    /// <summary>标题栏「最小化」拦截：WPF 默认把窗口缩到任务栏，这里立刻还原为 Normal 并移到屏幕外，
 /// 使最小化与隐藏行为一致（用户看不见，但 WebView2 仍判定可见、不节流定时器，Agent 抓取照常）。</summary>
 protected override void OnStateChanged(EventArgs e)
 {
 base.OnStateChanged(e);
 if (WindowState != WindowState.Minimized) return;
 WindowState = WindowState.Normal;
 Left = -32000; Top = -32000; // 移到屏幕外
 SilentOffscreen = true; // 标记离屏静默：对用户不可见，点「显示」再搬回屏幕中央
 Log("窗口已最小化到屏幕外：网页仍在后台运行，Agent 可继续调用；重新显示请在模型下拉再选一次本通道");
 }

 protected override void OnClosing(CancelEventArgs e)
    {
        // 默认只隐藏：窗口是"独立后台浏览器"，关掉会中断 Agent 调用；真关由 Shutdown 放行
        // （OnClosing 由 WPF 在 UI 线程触发，这里读控件/Application 状态天然安全；Application 可能已销毁故做空值防御）
        if (!allowClose && Application.Current?.Dispatcher?.HasShutdownStarted != true)
        {
            e.Cancel = true;
            OnHideClick(this, new RoutedEventArgs());
            return;
        }
        base.OnClosing(e);
    }

    /// <summary>窗口是否已真正销毁（宿主据此决定复用还是重建）</summary>
    public bool ShutdownDone { get; private set; }

    /// <summary>真正销毁窗口（程序退出或通道热重载时调用）。
    /// 线程边界：调用方可能是后台线程（WebChannelHost.ShutdownAll / 通道热重载），而 Browser、Close()
    /// 都是 UI 线程专属，故整段销毁动作统一经 Dispatcher 通道**回 UI 线程**执行；ShutdownDone 只在
    /// UI 线程体内置位，调度失败（UI 线程不可用/超时）时不置位，避免"标记已销毁但窗口其实还活着"
    /// 导致宿主误判复用/重建。</summary>
    public void Shutdown()
    {
        if (!Dispatcher.CheckAccess())
        {
            // 后台线程调用：仍统一经 Dispatcher 通道回 UI 线程，但有界等待——
            // UI 线程若已无响应（Dispatcher 已关闭/正阻塞），不能让调用方（App.Exit / 通道热重载）无限挂住
            try
            {
                if (!Dispatcher.InvokeAsync(Shutdown).Task.Wait(TimeSpan.FromSeconds(5)))
                    Log("[exit] 调度窗口销毁超时（UI 线程无响应），已放行");
            }
            catch (Exception ex) { Log("[exit] 调度窗口销毁失败（UI 线程不可用）：" + ex.Message); }
            return;
        }
        ShutdownDone = true;
        allowClose = true;
        // 让正等初始化完成的 AskAsync 立刻失败，而不是白等 2 分钟初始化超时
        if (!ready.Task.IsCompleted)
            ready.TrySetException(new LlmException($"网页模型窗口「{spec.Display}」已关闭"));
        try { Browser.Dispose(); } catch { }
        try { Close(); } catch (Exception ex) { Log("[exit] 关闭窗口失败：" + ex.Message); }
    }

    /* ==================== 辅助 ==================== */

    /// <summary>在 UI 线程安全取当前 WebView2 内核（未就绪返回 null）。
    /// CoreWebView2 是 WPF 依赖对象属性，后台线程直接取会抛“调用线程无法访问此对象”，故统一经此方法取。</summary>
    Task<CoreWebView2?> GetCoreAsync()
    {
        var dispatcher = Dispatcher;
        if (dispatcher.CheckAccess()) return Task.FromResult<CoreWebView2?>(Browser.CoreWebView2);
        return dispatcher.InvokeAsync(() => (CoreWebView2?)Browser.CoreWebView2).Task;
    }

    /// <summary>在 UI 线程执行页面脚本并取回结果（ExecuteScriptAsync 的返回值是 JSON 文本）。
    /// 页面脚本自身报错会以 "Uncaught …" 文本返回，这里落日志，便于定位站点改版。</summary>
    internal Task<string?> ExecJsAsync(string js)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    var core = Browser.CoreWebView2;
                    if (core == null) { Log("网页内核尚未就绪，脚本未执行"); tcs.TrySetResult(null); return; }
                    var s = await core.ExecuteScriptAsync(js);
                    if (!string.IsNullOrEmpty(s) && s.Contains("Uncaught", StringComparison.OrdinalIgnoreCase))
                        Log("页面脚本报错：" + Trunc(s, 300));
                    tcs.TrySetResult(s);
                }
                catch (Exception ex) { Log("执行页面脚本失败：" + ex.Message); tcs.TrySetResult(null); }
            }));
        }
        catch (Exception ex) { Log("调度页面脚本失败：" + ex.Message); tcs.TrySetResult(null); }
        return tcs.Task;
    }

    /// <summary>求值页面布尔表达式（失败按 false 处理）</summary>
    async Task<bool> EvalBoolAsync(string js)
    {
        var raw = (await ExecJsAsync(js) ?? "").Trim();
        return raw.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    void FireAndForget(string js) => _ = ExecJsAsync(js);

    static string Trunc(string? s, int n) => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n) + "…");

    /// <summary>运行日志：窗口底栏同步显示 + 追加 log/webmodel.log（站点改版排查用）</summary>
    void Log(string msg)
    {
        try
        {
            File.AppendAllText(Path.Combine(Paths.LogDir, "webmodel.log"),
                $"[{DateTime.Now:HH:mm:ss}] [{spec.Adapter}] {msg}{Environment.NewLine}", Encoding.UTF8);
        }
        catch { }
        try
        {
            if (Dispatcher.CheckAccess()) StatusText.Text = msg;
            else Dispatcher.BeginInvoke(() => StatusText.Text = msg);
        }
        catch { }
    }
}

/// <summary>本轮被页面跳转打断（文档被销毁 → 脚本上下文与网页侧对话都已重建）。
/// 与普通 LlmException 的区别在于：即便适配脚本已回报过 sent（本条确实投递过），
/// 页面重建后该轮也不在网页上下文里，调用方必须按"未同步"处理，不能推进同步计数。</summary>
public sealed class WebContextLostException(string message) : LlmException(message);
