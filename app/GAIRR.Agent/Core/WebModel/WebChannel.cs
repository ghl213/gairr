using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace GAIRR.Core;

/// <summary>网页模型通道描述：一条"网页-xxx"通道的元数据。
/// ProviderKey 统一带 web: 前缀（与 config.ini 真实厂商 key 天然隔离，不会误走 HTTP）；
/// Adapter 为站点适配器名，决定用哪份站点脚本驱动页面 —— 扩展新网页模型 = 加一份站点脚本 + 配置加一行，不改本文件与 AgentLoop。</summary>
public record WebChannelSpec(string ProviderKey, string Display, string ModelId, string Url, string Adapter)
{
    /// <summary>网页通道 provider key 前缀（config.ini 中真实厂商 key 不带此前缀）</summary>
    public const string Prefix = "web:";

    /// <summary>UI 展示用厂商名：下拉模板按此着强调色，把"网页"与真实厂商区分开</summary>
    public const string ProviderDisplay = "网页";

    /// <summary>该 provider key 是否网页通道</summary>
    public static bool IsWebProvider(string? provider) =>
        !string.IsNullOrEmpty(provider) && provider.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>由适配器名生成 provider key（如 deepseek-expert → web:deepseek-expert）</summary>
    public static string KeyOf(string adapter) => Prefix + adapter;

    /// <summary>解析 [WebChannels] 的一行配置："显示名|适配器|网址"（适配器可省，缺省时用显示名小写）。
    /// 解析失败返回 null（调用方跳过该行，不影响其它通道）。</summary>
    public static WebChannelSpec? ParseLine(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var parts = raw.Split('|').Select(s => s.Trim()).ToArray();
        if (parts.Length < 2 || parts[0].Length == 0) return null;
        var adapter = parts[1].Length > 0 ? parts[1] : parts[0].ToLowerInvariant();
        var url = parts.Length >= 3 ? parts[2] : "";
        return new WebChannelSpec(KeyOf(adapter), parts[0], adapter, url, adapter);
    }
}

/// <summary>网页模型后端统一契约：把一轮对话（messages+tools）投递到网页版模型页面，取回回复。
/// 实现方在 UI 程序集（需要 WebView2/WPF 与独立网页窗口），Agent 侧只依赖本接口，
/// 从而保持 LLMClient / AgentLoop / 并发门 / 下拉逻辑与 UI 完全解耦。
/// 注意：网页版没有 function calling 接口，工具调用靠"输出格式约定 + 回复解析"（UI 侧 WebReplyParser）实现：
/// 工具清单写进【系统设定】，模型按 OpenAI 响应结构（role/content/tool_calls）回 JSON，解析成功即填充 ToolCalls；
/// 解析不出协议结构时 ToolCalls 为空，Agent 侧仍按"纯文本模型"工作（原有路径不变）。</summary>
public interface IWebChatBackend
{
    /// <summary>通道显示名（用于日志与异常提示）</summary>
    string Display { get; }

    /// <summary>一轮对话：onDelta 逐段回调正文（打字机效果），onReasoning 回调思考过程（无则传 null）；
    /// 返回完整正文与收尾原因（网页判定异常收尾时 FinishReason 置空，交由上层按异常处理）。
    /// forceNewSession=true 时强制重置网页对话并整段重投完整上下文（「发送继续 / 格式纠正 / 发送完整上下文」
    /// 等需从零重建网页侧会话的场景）；否则默认首轮整段投完整上下文建会话、后续轮只发本轮新增。</summary>
    Task<LlmResponse> ChatAsync(JsonArray messages, JsonArray tools,
        Action<string>? onDelta, CancellationToken ct, Action<string>? onReasoning = null, bool forceNewSession = false);
}

/// <summary>网页通道注册表：运行期由 UI 层注入"provider key → 后端工厂"，Agent 侧按 key 取用（同一通道复用同一后端实例，
/// 保证一个网页窗口只服务一条会话线、上下文连续）。
/// 未注册场景（CLI 无 UI 运行、或该通道未启用）取用返回 null，交由 AgentLoop 抛出可读提示。</summary>
public static class WebChannelRegistry
{
    static readonly object gate = new();
    static readonly Dictionary<string, WebChannelSpec> specs = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, Func<WebChannelSpec, IWebChatBackend>> factories = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, IWebChatBackend> instances = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>注册一条网页通道：spec 为元数据，factory 由 UI 层提供（创建后端实例，内部负责窗口/页面）。</summary>
    public static void Register(WebChannelSpec spec, Func<WebChannelSpec, IWebChatBackend> factory)
    {
        lock (gate)
        {
            specs[spec.ProviderKey] = spec;
            factories[spec.ProviderKey] = factory;
        }
    }

    /// <summary>注销全部通道（配置热重载时先清再注册）</summary>
    public static void Clear()
    {
        lock (gate) { specs.Clear(); factories.Clear(); instances.Clear(); }
    }

    /// <summary>已注册的通道清单</summary>
    public static List<WebChannelSpec> All()
    {
        lock (gate) return specs.Values.ToList();
    }

    /// <summary>按 provider key 取通道元数据</summary>
    public static bool TryGet(string provider, out WebChannelSpec spec)
    {
        spec = null!;
        if (!WebChannelSpec.IsWebProvider(provider)) return false;
        lock (gate) return specs.TryGetValue(provider, out spec!);
    }

    /// <summary>按 provider key 取后端实例（首次调用创建并缓存）；未注册返回 null。</summary>
    public static IWebChatBackend? Get(string provider)
    {
        if (!WebChannelSpec.IsWebProvider(provider)) return null;
        lock (gate)
        {
            if (instances.TryGetValue(provider, out var cached)) return cached;
            if (!factories.TryGetValue(provider, out var factory)) return null;
            var created = factory(specs[provider]);
            instances[provider] = created;
            return created;
        }
    }
}
