using System.Net.Http;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GAIRR.Core;

public class LlmException(string message) : Exception(message);

public record LlmUsage(int Prompt, int Completion, int Total);

public record LlmToolCall(string Id, string Name, string Arguments);

/// <summary>FinishReason 为 null 表示流式响应未收到 finish_reason（SSE 中断/模型返回异常），调用方可据此判定"异常收尾"并追发继续指令</summary>
public record LlmResponse(string? Content, string? ReasoningContent, List<LlmToolCall> ToolCalls, LlmUsage Usage, string? FinishReason = null);

/// <summary>OpenAI 兼容协议客户端：支持非流式 + SSE 流式（打字机效果）</summary>
public class LLMClient
{
    public static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(180) };

    readonly string baseUrl;
    readonly string apiKey;
    readonly AppConfig? cfg;
    public string Model { get; set; }

    /// <summary>网页通道后端（非空时本客户端不再走 HTTP：ChatAsync/ChatStreamAsync 全量转交独立网页窗口），
    /// 由 AgentLoop.MakeClient 依 provider 前缀 web: 注入；对外调用签名不变，故 AgentLoop/UI/并发门零改动。</summary>
    public IWebChatBackend? WebBackend { get; set; }

    /// <summary>思考参数规则（来自 [ModelThinking] 配置；null=该模型不发思考参数，替代原 qwen3 硬编码判断）</summary>
    public ThinkingRule? ThinkingRule { get; set; }

    /// <summary>当前思考值：开/关模式为 "on"/"off"（空=on）；等级模式为某档值（空=默认档）。UI 切换时更新</summary>
    public string ThinkingValue { get; set; } = "";

    public LLMClient(string baseUrl, string apiKey, string model, AppConfig? cfg = null)
    {
        this.baseUrl = baseUrl;
        this.apiKey = apiKey;
        this.cfg = cfg;
        Model = model;
    }

    /// <summary>按规则把思考参数拼入请求体（参数名/值形态/写入层级全部由配置决定）</summary>
    void SetThinkingParam(JsonObject body)
    {
        if (ThinkingRule == null) return;
        var v = ThinkingRule.Resolve(ThinkingValue);
        if (v != null) ThinkingRule.Apply(body, v);
    }

    /* ---------- 非流式（工具调用场景保留） ---------- */

    public async Task<LlmResponse> ChatAsync(JsonArray messages, JsonArray tools, CancellationToken ct, bool forceWebFullContext = false)
    {
        // 网页通道后端：整轮请求转交独立网页窗口，HTTP 路径一概不走（上层对此无感知）
        if (WebBackend != null)
            return await WebBackend.ChatAsync(messages, tools, null, ct, null, forceWebFullContext);

        try
        {
            if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(apiKey))
                throw new LlmException("模型配置不完整：请检查 config.ini 的 BaseUrl / ApiKey");

            var body = new JsonObject
            {
                ["model"] = Model,
                // 归并非首位 system（vLLM/SGLang 要求 system 必须在首条）后做敏感信息掩码：密钥/密码/敏感URL 替换为 ENC 令牌，模型侧不可见
                ["messages"] = SensitiveGuard.MaskJsonMessages(messages.NormalizeMessages()),
                ["stream"] = false,
            };
            if (tools.Count > 0) body["tools"] = tools.CloneArray();
            // 思考参数按 [ModelThinking] 规则注入（未配置规则的模型不带，行为与不支持思考一致）
            SetThinkingParam(body);

            var delay = 2000;
            for (var attempt = 0; ; attempt++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/chat/completions");
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
                req.Headers.TryAddWithoutValidation("User-Agent", "GAIRR/1.0");
                req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

                // 60秒读取超时保护（大上下文模型处理可能较慢，但不应超过60秒）
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                HttpResponseMessage resp;
                try { resp = await Http.SendAsync(req, linkedCts.Token); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                {
                    if (attempt < 2)
                    {
                        await Task.Delay(delay, ct);
                        delay *= 2;
                        continue;
                    }
                    throw new LlmException("模型响应超时：连续3次请求超过60秒未收到响应");
                }
                catch (Exception ex) when (IsTransientNetworkError(ex) && attempt < 3)
                {
                    // 发送阶段网络类异常（连接失败/被重置/DNS/超时等）：退避后重发（与 429、读取超时共用 attempt 计数与退避序列），
                    // 修复"发送阶段网络错误一次即败"（原实现立即包 LlmException，被下方 124 行原样透传，无任何重试）
                    System.Diagnostics.Debug.WriteLine($"[Warn] ChatAsync网络异常，{delay}ms后第{attempt + 1}次重试: {ex.Message}");
                    await Task.Delay(delay, ct);
                    delay *= 2;
                    continue;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Error] ChatAsync网络异常: {ex.Message}");
                    throw new LlmException("网络异常：" + ex.Message);
                }

                using (resp)
                {
                    var text = await resp.Content.ReadAsStringAsync(ct);
                    if ((int)resp.StatusCode == 429 && attempt < 3)
                    {
                        await Task.Delay(delay, ct);
                        delay *= 2;
                        continue;
                    }
                    if (!resp.IsSuccessStatusCode)
                    {
                        var statusCode = (int)resp.StatusCode;
                        var detail = Trunc(text, 600);
                        // 先尝试配置驱动的错误码解析（config.ini [ErrorCodes] 节）
                        var configuredMsg = cfg?.ResolveError(statusCode, detail);
                        if (configuredMsg != null)
                            throw new LlmException(configuredMsg);
                        // 无配置匹配时回退到通用提示
                        throw new LlmException($"模型调用失败 {statusCode}：{detail}");
                    }
                    return SensitiveGuard.UnmaskResponse(Parse(text));   // 返回边界解密：ENC 令牌还原后才交工具执行/展示
                }
            }
        }
        catch (LlmException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }   // 用户取消原样传播：不计入失败，上层按“已中止”处理
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Error] ChatAsync异常: {ex.Message}");
            throw new LlmException("ChatAsync异常：" + ex.Message);
        }
    }

    /* ---------- SSE 流式（打字机效果，每轮单次请求） ---------- */

    /// <summary>流式对话：每轮只发一次请求；content 始终完整累积返回，onDelta 仅在未见 tool_calls 时逐字回调（气泡增量），完成后返回完整内容与工具调用</summary>
    public async Task<LlmResponse> ChatStreamAsync(JsonArray messages, JsonArray tools, Action<string> onDelta, Action? onToolCalls, CancellationToken ct, Action<string>? onReasoning = null, bool forceWebFullContext = false)
    {
        // 网页通道后端：转交网页窗口，逐段回调正文形成同样的打字机效果；
        // 网页版没有 function calling 接口，工具调用由后端按"输出格式约定"解析回复得到（ToolCalls 可能非空）；
        // onToolCalls 是流式 SSE 专用的提前通知，网页后端不触发（其结果在返回的 LlmResponse 里）
        if (WebBackend != null)
            return await WebBackend.ChatAsync(messages, tools, onDelta, ct, onReasoning, forceWebFullContext);

        // 网络类可恢复异常（连接被重置/中止/超时/流中断等）自动等待后重试，最多 3 次；3 次仍失败才向上抛 LlmException 由 AgentLoop 判定整轮失败
        const int MaxTransientRetries = 3;
        const int TransientRetryDelayMs = 10000;
        var transientRetries = 0;
        var dbgPath = Paths.StreamDebugLog;

        while (true)
        {
            try
            {
                if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(apiKey))
                    throw new LlmException("模型配置不完整：请检查 config.ini 的 BaseUrl / ApiKey");

                var body = new JsonObject
                {
                    ["model"] = Model,
                    // 归并非首位 system（vLLM/SGLang 要求 system 必须在首条）后做敏感信息掩码：密钥/密码/敏感URL 替换为 ENC 令牌，模型侧不可见
                    ["messages"] = SensitiveGuard.MaskJsonMessages(messages.NormalizeMessages()),
                    ["stream"] = true,
                    // OpenAI 兼容接口需在流式请求中显式要求，否则流末尾不返回 usage
                    ["stream_options"] = new JsonObject { ["include_usage"] = true },
                };
                if (tools.Count > 0) body["tools"] = tools.CloneArray();
                // 思考参数按 [ModelThinking] 规则注入（未配置规则的模型不带）
                SetThinkingParam(body);

                // 空流超时重试：60秒未收到任何数据行（模型卡住/连接异常）退避重发；已收到数据后不再重试，避免输出重复
                var delay = 2000;
                for (var attempt = 0; ; attempt++)
                {
                    var receivedAny = false;   // 本轮是否已收到任何数据行（含 SSE 空行）
                    var contentSb = new StringBuilder();
                    var reasoningSb = new StringBuilder();
                    var usage = new LlmUsage(0, 0, 0);
                    var toolIds = new Dictionary<int, string>();
                    var toolNames = new Dictionary<int, string>();
                    var toolArgs = new Dictionary<int, StringBuilder>();
                    var toolCallStarted = false;   // 已解析到 tool_calls 增量：后续 content 不再回调
                    var finishReason = "";   // 流式 finish_reason（stop/tool_calls/length/null）；空=未收到，供上层判定异常收尾
                    var dbgFrames = 0; var dbgContent = 0; var dbgReasoning = 0; var dbgTool = 0; var dbgErrors = 0; var dbgData = 0; var dbgSkip = 0;

                    using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/chat/completions");
                    req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
                    req.Headers.TryAddWithoutValidation("User-Agent", "GAIRR/1.0");
                    req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

                    HttpResponseMessage resp;
                    try { resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex) when (IsTransientNetworkError(ex))
                    {
                        // 发送阶段网络类异常（连接失败/被重置/DNS/超时等）不包 LlmException、原样上抛：
                        // 交由外层 IsTransientNetworkError 分支接管（最多 3 次×10s 自动重试），
                        // 修复"发送阶段网络错误被立即包成 LlmException 绕过重试、整轮直接失败"
                        System.Diagnostics.Debug.WriteLine($"[Warn] ChatStreamAsync发送网络异常: {ex.Message}");
                        throw;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Error] ChatStreamAsync网络异常: {ex.Message}");
                        throw new LlmException("网络异常：" + ex.Message);
                    }

                    using (resp)
                    {
                        if (!resp.IsSuccessStatusCode)
                        {
                            var err = await resp.Content.ReadAsStringAsync(ct);
                            if ((int)resp.StatusCode == 429 && attempt < 3)
                            {
                                await Task.Delay(delay, ct);
                                delay *= 2;
                                continue;
                            }
                            var statusCode = (int)resp.StatusCode;
                            var detail = Trunc(err, 400);
                            var configuredMsg = cfg?.ResolveError(statusCode, detail);
                            throw new LlmException(configuredMsg ?? $"模型调用失败 {statusCode}：{detail}");
                        }

                        var stream = await resp.Content.ReadAsStreamAsync(ct);
                        using var reader = new StreamReader(stream, Encoding.UTF8);
                        while (!reader.EndOfStream)
                        {
                            // 带超时的行读取：60秒没收到任何数据则中断
                            var readTask = reader.ReadLineAsync(ct).AsTask();
                            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(60), ct);
                            var completed = await Task.WhenAny(readTask, timeoutTask);
                            if (completed == timeoutTask)
                            {
                                if (attempt < 2 && !receivedAny)
                                {
                                    await Task.Delay(delay, ct);
                                    delay *= 2;
                                    continue;
                                }
                                throw new LlmException("模型流式响应超时：60秒内未收到数据，可能是模型接口卡住");
                            }

                            var line = await readTask;
                            if (line == null) break;
                            receivedAny = true;
                            var l = line.Trim();
                            if (string.IsNullOrEmpty(l)) continue;
                            if (!l.StartsWith("data: ")) { dbgSkip++; continue; }
                            dbgData++;
                            var json = l[6..];
                            if (json == "[DONE]") break;
                            try
                            {
                                using var doc = JsonDocument.Parse(json);
                                var root = doc.RootElement;
                                // 部分供应商在流末尾通过 choices 为空的 data 块推送 usage（usage 可能为 null，跳过避免解析异常）
                                if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                                {
                                    usage = new LlmUsage(
                                        u.TryGetProperty("prompt_tokens", out var p) ? p.GetInt32() : 0,
                                        u.TryGetProperty("completion_tokens", out var q) ? q.GetInt32() : 0,
                                        u.TryGetProperty("total_tokens", out var t) ? t.GetInt32() : 0);
                                }
                                // 空 choices 块可能是仅用来推送 usage 的结尾帧；usage 已提取，后续无需处理内容
                                if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
                                var delta = choices[0].GetProperty("delta");
                                // 工具调用增量：按 index 聚合 id/name/arguments（arguments 跨 chunk 拼接）
                                if (delta.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
                                {
                                    dbgTool++;
                                    toolCallStarted = true;
                                    onToolCalls?.Invoke();   // 通知调用方：本轮已确定为工具轮（调用方可据此决定气泡增量策略）
                                    foreach (var tc in tcs.EnumerateArray())
                                    {
                                        if (!tc.TryGetProperty("index", out var idxEl) || idxEl.ValueKind != JsonValueKind.Number) continue;
                                        var idx = idxEl.GetInt32();
                                        // DeepSeek 系流式增量帧会重复携带空串 id/name（结构带字段但值为空），
                                        // 无条件覆盖会把首帧的 id/name 清空导致聚合结果为空（toolCalls=0 误判纯文本回复）；
                                        // 只在非空时更新首帧值，arguments 空串追加无副作用
                                        if (tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                                        {
                                            var idv = idEl.GetString() ?? "";
                                            if (idv.Length > 0) toolIds[idx] = idv;
                                        }
                                        if (tc.TryGetProperty("function", out var fnEl))
                                        {
                                            if (fnEl.TryGetProperty("name", out var nmEl) && nmEl.ValueKind == JsonValueKind.String)
                                            {
                                                var nmv = nmEl.GetString() ?? "";
                                                if (nmv.Length > 0) toolNames[idx] = nmv;
                                            }
                                            if (fnEl.TryGetProperty("arguments", out var agEl) && agEl.ValueKind == JsonValueKind.String)
                                            {
                                                if (!toolArgs.TryGetValue(idx, out var ab)) { ab = new StringBuilder(); toolArgs[idx] = ab; }
                                                ab.Append(agEl.GetString() ?? "");
                                            }
                                        }
                                    }
                                }
                                dbgFrames++;
                                // content 增量：始终完整累积（与 tool_calls 交错也不丢尾巴/标点）；
                                // onDelta 仅在未见 tool_calls 时回调（工具轮文本是否上屏由调用方决定）
                                if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                                {
                                    var chunk = c.GetString() ?? "";
                                    if (chunk.Length > 0)
                                    {
                                        dbgContent++;
                                        contentSb.Append(chunk);
                                        if (!toolCallStarted) onDelta(chunk);
                                    }
                                }
                                if (delta.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
                                {
                                    var chunk = rc.GetString() ?? "";
                                    if (chunk.Length > 0)
                                    {
                                        dbgReasoning++;
                                        reasoningSb.Append(chunk);
                                        // 思考增量实时转发：调用方（AgentLoop）负责节流推送 UI 直播卡，
                                        // 供 high/深度档 30-60s+ 超长思考期间展示思维动向，避免界面长时间静止被误判卡死
                                        onReasoning?.Invoke(chunk);
                                    }
                                }
                                // 捕获 finish_reason：非空字符串即记录（stop/tool_calls/length 等）；null 值（ValueKind.Null）视为未收到，保持空串
                                // 用于上层判定"异常收尾"——模型本该执行工具却以纯文本 stop 结束、或流被截断
                                if (choices[0].TryGetProperty("finish_reason", out var fr))
                                {
                                    if (fr.ValueKind == JsonValueKind.String) finishReason = fr.GetString() ?? "";
                                    else if (fr.ValueKind == JsonValueKind.Null) { /* 显式 null：保持空，标记异常 */ }
                                }
                                // 检测 finish_reason 为 stop 时继续读取 usage 后结束
                                if (fr.ValueKind == JsonValueKind.String && fr.GetString() == "stop")
                                    continue;
                            }
                            catch (Exception ex)
                            {
                                dbgErrors++;
                                System.Diagnostics.Debug.WriteLine($"SSE解析异常: {ex.Message} 行: {l}");
                                try { File.AppendAllText(dbgPath, $"[{DateTime.Now:HH:mm:ss}] SSE异常 {ex.Message} 行: {Trunc(l, 400)}\n"); } catch { }
                            }
                        }

                        var toolCalls = new List<LlmToolCall>();
                        foreach (var kv in toolIds)
                        {
                            if (!toolNames.TryGetValue(kv.Key, out var name) || string.IsNullOrEmpty(name)) continue;
                            var args = toolArgs.TryGetValue(kv.Key, out var ab) ? ab.ToString() : "";
                            toolCalls.Add(new LlmToolCall(
                                string.IsNullOrEmpty(kv.Value) ? "call_" + kv.Key : kv.Value,
                                name,
                                string.IsNullOrEmpty(args) ? "{}" : args));
                        }
                        // 实际发送的思考参数值：按规则路径（@ 嵌套或顶层）取值，统一转字符串记录
                        var dbgThinking = ThinkingRule != null
                            ? ThinkingRule.Resolve(ThinkingValue)?.ToJsonString()
                            : null;
                        try
                        {
                            File.AppendAllText(dbgPath, $"[{DateTime.Now:HH:mm:ss}] model={Model} thinking={dbgThinking ?? "无"} data={dbgData} skip={dbgSkip} frames={dbgFrames} content增量={dbgContent} reasoning增量={dbgReasoning} tool帧={dbgTool} err={dbgErrors} | contentLen={contentSb.Length} reasoningLen={reasoningSb.Length} toolCalls={toolCalls.Count} usage={usage.Total}\n");
                        }
                        catch { }
                        // 返回边界解密：ENC 令牌还原后才交 AgentLoop 执行工具/落历史/展示；未收到 finish_reason → null，供 AgentLoop 判定异常收尾
                        return SensitiveGuard.UnmaskResponse(new LlmResponse(contentSb.ToString(),
                            reasoningSb.Length > 0 ? reasoningSb.ToString() : null,
                            toolCalls,
                            usage,
                            finishReason.Length > 0 ? finishReason : null));
                    }
                }
            }
            catch (LlmException) { throw; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Error] ChatStreamAsync异常: {ex.Message}");
                // 网络类可恢复异常（连接被重置/中止/超时/流中断等，含 SSE 传输中途断连）自动等待后重试，最多 3 次；
                // 其余类型（协议/解析/配置等）直接失败，避免无意义空转
                if (IsTransientNetworkError(ex) && transientRetries < MaxTransientRetries)
                {
                    transientRetries++;
                    System.Diagnostics.Debug.WriteLine($"[Warn] ChatStreamAsync网络异常，{TransientRetryDelayMs / 1000}秒后第{transientRetries}/{MaxTransientRetries}次重试: {ex.Message}");
                    try { File.AppendAllText(dbgPath, $"[{DateTime.Now:HH:mm:ss}] 网络类异常将自动重试 {transientRetries}/{MaxTransientRetries}: {Trunc(ex.Message, 200)}\n"); } catch { }
                    await Task.Delay(TransientRetryDelayMs, ct);
                    continue;
                }
                throw new LlmException("ChatStreamAsync异常：" + ex.Message);
            }
        }
    }

    /// <summary>判断异常是否属于"网络类可恢复"错误（连接被重置/中止/超时/流中断等），需自动重试。
    /// 沿 InnerException 链逐层检查：HttpClient 常把底层 SocketException（IOException 子类）包装进
    /// HttpRequestException，SSE 传输中断还可能表现为 OperationCanceledException（用户取消已由上层 when 先行过滤）。</summary>
    static bool IsTransientNetworkError(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is HttpRequestException || e is IOException || e is OperationCanceledException)
                return true;
        }
        return false;
    }

    static LlmResponse Parse(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var msg = root.GetProperty("choices")[0].GetProperty("message");

            string? content = msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() : null;
            // 思考模式（Kimi/Qwen 等）的思维链在 reasoning_content 字段，工具调用轮 content 常为空，供思考条展示
            string? reasoning = msg.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String
                ? rc.GetString() : null;

            var calls = new List<LlmToolCall>();
            if (msg.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
                foreach (var tc in tcs.EnumerateArray())
                    calls.Add(new LlmToolCall(
                        tc.GetProperty("id").GetString() ?? "",
                        tc.GetProperty("function").GetProperty("name").GetString() ?? "",
                        tc.GetProperty("function").GetProperty("arguments").GetString() ?? "{}"));

            var usage = new LlmUsage(0, 0, 0);
            if (root.TryGetProperty("usage", out var u))
                usage = new LlmUsage(
                    u.TryGetProperty("prompt_tokens", out var p) ? p.GetInt32() : 0,
                    u.TryGetProperty("completion_tokens", out var q) ? q.GetInt32() : 0,
                    u.TryGetProperty("total_tokens", out var t) ? t.GetInt32() : 0);

            return new LlmResponse(content, reasoning, calls, usage);
        }
        catch (Exception ex) { throw new LlmException("解析模型响应失败：" + ex.Message); }
    }

    internal static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}

/// <summary>.NET 8 的 JsonNode 没有 DeepCopy，用序列化回环替代</summary>
internal static class Jsonx
{
    public static JsonArray CloneArray(this JsonArray a) => (JsonArray)JsonNode.Parse(a.ToJsonString())!;
    public static JsonObject CloneObj(this JsonObject o) => (JsonObject)JsonNode.Parse(o.ToJsonString())!;

    /// <summary>克隆并剔除消息中值为 null 的属性（如 reasoning_content:null）：
    /// 云端 API 容忍 null 字段，但本地 llama.cpp 系服务端的严格解析会直接 500
    /// （type must be string, but is null）；字段缺失与 null 语义等价，统一在发送边界剔除最稳。</summary>
    public static JsonObject StripNull(this JsonObject o)
    {
        var c = o.CloneObj();
        foreach (var k in c.Where(kv => kv.Value != null && kv.Value.GetValueKind() == JsonValueKind.Null).Select(kv => kv.Key).ToList())
            c.Remove(k);
        return c;
    }

    /// <summary>整体克隆数组并逐条剔除 null 值属性（非对象元素原样保留）</summary>
    static JsonArray SanitizeArray(JsonArray a)
    {
        var result = new JsonArray();
        foreach (var item in a)
            result.Add(item is JsonObject o ? o.StripNull() : (JsonNode?)item);
        return result;
    }

    /// <summary>安全读取消息字符串字段：缺失或 null 值返回 null（GetValue 直接作用于 null 值会抛异常）</summary>
    static string? ReadText(JsonObject m, string key)
    {
        var n = m[key];
        return n != null && n.GetValueKind() == JsonValueKind.String ? (string)n! : null;
    }

    /// <summary>发送前归并消息：把非首位的 system 消息内容追加到首条 system（保持其 role 不变），
    /// 其余 system 消息移除，并剔除各消息中的 null 值属性（本地 llama.cpp 系服务端不接受，见 StripNull）。
    /// vLLM/SGLang 等网关的 Jinja 模板要求 system 消息必须位于第一条，
    /// 否则直接 500；AgentLoop/压缩器会在历史中插入多条 system（快照、承接提示、历史摘要等），
    /// 统一在此收口归并，调用方无需各自处理。返回新数组（入参不修改）。</summary>
    public static JsonArray NormalizeMessages(this JsonArray a)
    {
        JsonArray result;
        if (a.Count <= 1)
        {
            result = SanitizeArray(a);
        }
        else
        {
            bool IsSystem(int i) => a[i] is JsonObject m && (m["role"]?.GetValue<string>() ?? "") == "system";
            if (!IsSystem(0) || !Enumerable.Range(1, a.Count - 1).Any(IsSystem))
            {
                result = SanitizeArray(a);   // 无需归并：快速路径
            }
            else
            {
                var head = ((JsonObject)a[0]!).StripNull();
                var headText = ReadText(head, "content") ?? "";
                var extra = new StringBuilder();
                for (var i = 1; i < a.Count; i++)
                {
                    if (IsSystem(i))
                    {
                        extra.Append('\n').Append('\n');
                        extra.Append(ReadText((JsonObject)a[i]!, "content") ?? "");
                    }
                }
                head["content"] = extra.Length > 0 ? headText + extra.ToString() : headText;
                result = new JsonArray { head };
                for (var i = 1; i < a.Count; i++)
                    if (!IsSystem(i)) result.Add(((JsonObject)a[i]!).StripNull());
            }
        }
        // 发送前兜底：无论是否触发 system 归并，统一修复 tool_calls 与 tool 响应的配对残缺（见 RepairDanglingToolCalls）
        return RepairDanglingToolCalls(result);
    }

    /// <summary>发送前配对校验兜底：OpenAI 兼容服务端要求带 tool_calls 的 assistant 消息之后必须紧跟覆盖其
    /// 每个 tool_call_id 的 role=tool 响应，否则整请求被 400 拒收（"an assistant message with 'tool_calls'
    /// must be followed by tool messages responding to each 'tool_call_id'"）。
    /// AgentLoop 在工具执行阶段被取消/异常/超时强制中断时（catch 收尾直接结束），会遗留"已入历史但无结果"的
    /// 悬空 assistant(tool_calls)；该残缺上下文在会话再次发起请求时被原样上送即触发此 400。此处统一兜底：
    /// 1) 悬空 assistant(tool_calls) 后缺响应的 tool_call，补一条说明中断的占位 tool 消息——保留"本轮调过工具"
    ///    的上下文，模型可据占位提示决定重调或忽略，比直接剔除更不丢信息；
    /// 2) 没有对应 assistant(tool_calls) 的孤立 tool 消息直接剔除（同样会被 400 拒收）。
    /// 返回新数组（入参不修改）。</summary>
    static JsonArray RepairDanglingToolCalls(JsonArray a)
    {
        var result = new JsonArray();
        var i = 0;
        while (i < a.Count)
        {
            var m = a[i] as JsonObject;
            var role = m?["role"]?.GetValue<string>() ?? "";
            if (role == "tool")
            {
                i++;   // 孤立 tool（其前不是含对应 tool_call 的 assistant，前条配对块已被上方分支吞掉）：剔除
                continue;
            }
            if (role == "assistant" && m!["tool_calls"] is JsonArray tcs && tcs.Count > 0)
            {
                result.Add(a[i]!.DeepClone());   // 深拷贝：源元素仍挂在入参数组（NormalizeMessages 产物）上，直接搬移会触发 "The node already has a parent"
                // 收集本轮期望的 tool_call_id（保持模型返回的原始顺序）
                var expected = new List<string>();
                foreach (var t in tcs.OfType<JsonObject>())
                {
                    var id = t["id"];
                    if (id != null && id.GetValueKind() == JsonValueKind.String) expected.Add((string)id!);
                }
                // 吞掉紧跟其后的连续 tool 响应块（AgentLoop 在 :1293 按结果逐条追加），记录已覆盖的 id
                var responded = new HashSet<string>(StringComparer.Ordinal);
                var j = i + 1;
                while (j < a.Count && a[j] is JsonObject tm && (tm["role"]?.GetValue<string>() ?? "") == "tool")
                {
                    var tid = tm["tool_call_id"];
                    if (tid != null && tid.GetValueKind() == JsonValueKind.String) responded.Add((string)tid!);
                    result.Add(a[j]!.DeepClone());   // 同上：源元素带父，需深拷贝后再挂入
                    j++;
                }
                // 缺响应的 tool_call 补占位（中断时整个工具块常全缺），保证每个 id 都有配对响应
                foreach (var id in expected)
                    if (!responded.Contains(id))
                        result.Add(new JsonObject
                        {
                            ["role"] = "tool",
                            ["tool_call_id"] = id,
                            ["content"] = "（系统提示）该工具调用未收到执行结果（会话在此中断）。如仍需其结果请重新调用该工具；否则请忽略此记录并继续。",
                        });
                i = j;
                continue;
            }
            result.Add(a[i]!.DeepClone());   // 普通消息同样深拷贝：NormalizeMessages 产物元素均有父数组，直接复用必抛异常
            i++;
        }
        return result;
    }
}
