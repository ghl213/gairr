using System;
using System.IO;

namespace GAIRR.Core;

/// <summary>config.ini 读取器（exe 同目录），节结构照开发方案第十章</summary>
public class AppConfig
{
    readonly Dictionary<string, Dictionary<string, string>> data = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>默认 config.ini 路径（exe 同目录）：供无实例上下文处（如开关持久化）定位</summary>
    public static string ConfigPathStatic => Path.Combine(AppContext.BaseDirectory, "config.ini");

    public string ConfigPath { get; }
    public bool Exists { get; }

    public AppConfig(string? path = null)
    {
        ConfigPath = path ?? Path.Combine(AppContext.BaseDirectory, "config.ini");
        Exists = File.Exists(ConfigPath);
        if (!Exists) return;

        var section = "";
        foreach (var raw in File.ReadAllLines(ConfigPath))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var ci = line.IndexOf(';');                 // 去注释（整行或行内）
            if (ci == 0) continue;
            if (ci > 0) line = line[..ci].TrimEnd();
            if (line.Length == 0) continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                if (!data.ContainsKey(section)) data[section] = new(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            var ei = line.IndexOf('=');
            if (ei <= 0) continue;
            var key = line[..ei].Trim();
            var val = line[(ei + 1)..].Trim();
            if (data.TryGetValue(section, out var kv)) kv[key] = val;
        }
    }

    public string Get(string section, string key, string def = "") =>
        data.TryGetValue(section, out var kv) && kv.TryGetValue(key, out var v) ? v : def;

    public int GetInt(string section, string key, int def) =>
        int.TryParse(Get(section, key), out var v) ? v : def;

    /// <summary>列出某节全部原始"键=值"行（跳过注释/空行），供 [ModelThinking] 这类"一行一条记录"的节解析</summary>
    public IEnumerable<string> GetLines(string section)
    {
        if (!data.TryGetValue(section, out var kv)) yield break;
        foreach (var pair in kv) yield return pair.Key + "=" + pair.Value;
    }

    /* ---------- 厂商配置（配置驱动，不再写死 Bailian/Kimi/Local） ---------- */

    /// <summary>厂商清单：从 [Providers] List 读取，格式"显示名|key"；未配则回退旧三厂商</summary>
    public List<(string Display, string Key)> ProviderList()
    {
        var raw = Get("Providers", "List", "");
        if (!string.IsNullOrWhiteSpace(raw))
        {
            return raw.Split(',')
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .Select(s =>
                {
                    var idx = s.IndexOf('|');
                    return idx > 0 && idx < s.Length - 1
                        ? (s[..idx].Trim(), s[(idx + 1)..].Trim())
                        : (s, s);   // 没有"|"时显示名与 key 相同
                })
                .ToList();
        }
        // 向后兼容：未配 [Providers] 时回退三厂商
        return new List<(string, string)>
        {
            ("百炼", "Bailian"),
            ("Kimi", "Kimi"),
            ("本地", "Local"),
        };
    }

    /// <summary>根据 key 反查厂商显示名；找不到则返回 key 本身</summary>
    public string ProviderDisplayName(string key)
    {
        var p = ProviderList().FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrEmpty(p.Display) ? key : p.Display;
    }

    /// <summary>取某供应商的三件套（BaseUrl/ApiKey/Model）：节名即 provider key</summary>
    public (string BaseUrl, string ApiKey, string Model) ProviderCfg(string provider) =>
        (Get(provider, "BaseUrl"), Get(provider, "ApiKey"), Get(provider, "Model"));

    /// <summary>某供应商是否为本地大模型：key 为 Local，或 BaseUrl 指向本机（localhost/127.0.0.1/[::1]/file://）</summary>
    public bool IsLocalProvider(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider)) return false;
        if (string.Equals(provider, "Local", StringComparison.OrdinalIgnoreCase)) return true;
        var baseUrl = ProviderCfg(provider).BaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl)) return false;
        if (baseUrl.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return true;
        if (Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri))
        {
            var host = uri.Host.Trim('[', ']');
            // 无主机（本地端口别名）也按本地处理，避免绕过限制
            return host.Length == 0 || host is "localhost" or "127.0.0.1" or "::1" or "0.0.0.0";
        }
        return false;
    }

    /// <summary>某厂商最大并发任务数：config.ini [厂商] 节 MaxConcurrency 显式配置优先；
    /// 未配置时默认：本地大模型 1、在线厂商 2（供 LlmConcurrencyGate 启动前检查与 UI Tooltip 展示）。
    /// 网页通道固定 1：一个网页窗口串行服务一条对话线，多路并发会串台。</summary>
    public int MaxConcurrencyFor(string provider)
    {
        if (WebChannelSpec.IsWebProvider(provider)) return 1;
        var v = GetInt(provider, "MaxConcurrency", 0);
        return v > 0 ? v : IsLocalProvider(provider) ? 1 : 2;
    }

    public string ProjectRoot
    {
        get => Get("Agent", "ProjectRoot", "");
        set
        {
            if (!data.ContainsKey("Agent")) data["Agent"] = new(StringComparer.OrdinalIgnoreCase);
            data["Agent"]["ProjectRoot"] = value;
        }
    }
    public int MaxRounds => GetInt("Agent", "MaxRounds", 50);
    public int CommandTimeout => GetInt("Agent", "CommandTimeout", 60);

    /// <summary>上下文裁剪阈值（估算值，非 API 参数）：按当前供应商解析，供应商节 MaxTokens 可覆盖全局 [Agent] MaxTokens</summary>
    public int MaxTokens => MaxTokensFor(Provider);

    /// <summary>某供应商的上下文裁剪阈值：先查 [供应商] MaxTokens，再回退 [Agent] MaxTokens/60000</summary>
    public int MaxTokensFor(string provider) => GetIntForProvider(provider, "MaxTokens", GetInt("Agent", "MaxTokens", 60000));

    /// <summary>压缩参数按供应商解析（本地/网络两套配置）：先查 [供应商] 节同名键，未配置则回退 [Agent] 默认；供应商节留空=用全局值（网络模型行为零变化）</summary>
    public int GetIntForProvider(string provider, string key, int agentDef) =>
        Get(provider, key).Length > 0 ? GetInt(provider, key, agentDef) : GetInt("Agent", key, agentDef);

    /// <summary>压缩参数按当前 Provider 解析（用法见 GetIntForProvider，Provider 每轮实时读取，切换供应商即跟随）</summary>
    public int GetIntForProvider(string key, int agentDef) => GetIntForProvider(Provider, key, agentDef);

    /// <summary>压缩开关按当前 Provider 解析：先查 [Provider] 键，未配置回退 [Agent] 默认（如 LlmCompress 本地关闭）</summary>
    public string GetForProvider(string key, string agentDef) =>
        Get(Provider, key).Length > 0 ? Get(Provider, key) : Get("Agent", key, agentDef);
    public int MaxFileLines => GetInt("CodeStyle", "MaxFileLines", 300);
    public int MaxFuncLines => GetInt("CodeStyle", "MaxFuncLines", 50);

    /// <summary>当前供应商：任意 key（来自 [Providers] List）</summary>
    public string Provider
    {
        get => Get("LLM", "Provider", ProviderList().FirstOrDefault().Key ?? "Bailian");
        set
        {
            if (!data.ContainsKey("LLM")) data["LLM"] = new(StringComparer.OrdinalIgnoreCase);
            data["LLM"]["Provider"] = value;
        }
    }

    /// <summary>某模型持久化的思考值（[ModelThinking] 节 Values 键，形如 "kimi-for-coding:high"；
    /// 开/关模式存 on/off，等级模式存等级值；未存返回空=用规则默认（开 或 levels 首档）</summary>
    public string ThinkingValueFor(string model)
    {
        foreach (var kv in Get("ModelThinking", "Values", "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = kv.Trim();
            var ci = t.IndexOf(':');
            if (ci > 0 && string.Equals(t[..ci], model, StringComparison.OrdinalIgnoreCase))
                return t[(ci + 1)..].Trim();
        }
        return "";
    }

    /// <summary>持久化某模型的思考值到 [ModelThinking] Values（逗号分隔 模型:值；空值=删除该项）</summary>
    public void SetThinkingValue(string model, string value)
    {
        var items = Get("ModelThinking", "Values", "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .ToList();
        var key = model + ":";
        items.RemoveAll(s => s.StartsWith(key, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(value)) items.Add(model + ":" + value);
        if (!data.ContainsKey("ModelThinking")) data["ModelThinking"] = new(StringComparer.OrdinalIgnoreCase);
        data["ModelThinking"]["Values"] = string.Join(", ", items);
    }

    /// <summary>本地模型监控端点（标题栏 GPU 三色柱）：返回 cpu/gpu JSON，监控取 gpu 数组第一卡；空=不显示</summary>
    public string LocalMonitorUrl => Get("Local", "MonitorUrl", "");

    /// <summary>钉钉 Webhook 地址（框架级内置通知，无需外部脚本）</summary>
    public string DingTalkWebhook => Get("Hooks", "DingTalkWebhook", "");
    /// <summary>钉钉加签密钥（可选）</summary>
    public string DingTalkSecret => Get("Hooks", "DingTalkSecret", "");
    public static string ProjectRootStatic { get; set; } = "";

    /// <summary>派生一份 ProjectRoot 指向指定项目的配置视图（其余配置键从同一 config.ini 读取）：
    /// 后台任务收口/落盘（git 提交、运行记录、计划存储）必须用发起时归属项目根，而非实时 ProjectRoot ——
    /// 切项目后实时根已指向新项目，继续用它会串写新项目根（验收 8：不串写旧根）。</summary>
    public AppConfig ViewForProjectRoot(string projectRoot)
    {
        var v = new AppConfig(ConfigPath);
        if (!string.IsNullOrWhiteSpace(projectRoot)) v.ProjectRoot = projectRoot;
        return v;
    }

    /// <summary>后台补注释/文档生成：记住的供应商（故障转移成功后写入，后续优先复用）</summary>
    string _notesProvider = "";

    /// <summary>后台补注释/文档生成的供应商：优先返回记住的（有 Key 的），否则按配置顺序找第一个有 Key 的；均无 Key 则返回 Local 兜底</summary>
    public string NotesProvider
    {
        get
        {
            var order = ProviderList().Select(x => x.Key).ToList();
            if (!order.Contains("Local", StringComparer.OrdinalIgnoreCase))
                order.Add("Local");
            // 记住的供应商优先
            if (!string.IsNullOrEmpty(_notesProvider))
            {
                var c = ProviderCfg(_notesProvider);
                if (!string.IsNullOrEmpty(c.ApiKey)) return _notesProvider;
            }
            foreach (var p in order)
            {
                var c = ProviderCfg(p);
                if (!string.IsNullOrEmpty(c.ApiKey)) return p;
            }
            return "Local";
        }
    }

    /// <summary>记住当前补注释供应商（故障转移成功后调用）</summary>
    public void SetNotesProvider(string provider) => _notesProvider = provider;

    /// <summary>返回补注释供应商优先级列表（有 Key 的在前，无 Key 的也保留用于 Local 兜底）</summary>
    public List<string> NotesProvidersList()
    {
        var order = ProviderList().Select(x => x.Key).ToList();
        if (!order.Contains("Local", StringComparer.OrdinalIgnoreCase))
            order.Add("Local");
        var withKey = order.Where(p => !string.IsNullOrEmpty(ProviderCfg(p).ApiKey)).ToList();
        var withoutKey = order.Where(p => string.IsNullOrEmpty(ProviderCfg(p).ApiKey)).ToList();
        var result = new List<string>(withKey);
        result.AddRange(withoutKey);
        return result;
    }

    /* ---------- 自动任务模型故障转移：记住可用供应商，失败时按序切换重试 ---------- */

    /// <summary>记住的可用供应商（持久化到 [LLM] AutoTaskProvider）：自动任务优先用，失效后重新按序尝试</summary>
    public string AutoTaskProvider
    {
        get => Get("LLM", "AutoTaskProvider", "");
        set
        {
            if (!data.ContainsKey("LLM")) data["LLM"] = new(StringComparer.OrdinalIgnoreCase);
            data["LLM"]["AutoTaskProvider"] = value;
        }
    }

    /// <summary>自动任务供应商尝试顺序：记住的可用供应商（有 Key 的）优先，其余按 Providers 清单去重排列，Local 始终兜底</summary>
    public List<string> AutoTaskProviderOrder()
    {
        var order = ProviderList().Select(x => x.Key).ToList();
        if (!order.Contains("Local", StringComparer.OrdinalIgnoreCase))
            order.Add("Local");
        var list = new List<string>();
        var remembered = AutoTaskProvider;
        if (!string.IsNullOrEmpty(remembered)
            && order.Contains(remembered, StringComparer.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(ProviderCfg(remembered).ApiKey))
            list.Add(remembered);
        foreach (var p in order)
            if (!list.Contains(p, StringComparer.OrdinalIgnoreCase)) list.Add(p);
        return list;
    }

    /// <summary>单一下拉列表模型选项：显示名 = "厂商显示名-模型名"，携带真实 provider key 与 model id；
    /// ProviderDisplay 为厂商显示名（不含模型名），供 UI 模板单独着强调色突出厂商；
    /// HasKey 表示该供应商是否已配置 ApiKey（有 key 的排在上面且可选，无 key 的排在下面且不可选）</summary>
    public record ModelOption(string Display, string Provider, string ModelId, string ProviderDisplay, bool HasKey)
    {
        public override string ToString() => Display;

        /// <summary>并发满额标记（仅 HasKey 选项有意义）：该厂商当前运行任务已达上限，下拉中该项整行桔色并标注"（满额）"、
        /// 禁选。运行时由 MainWindow 依 LlmConcurrencyGate 实时占用刷新列表时写入，非持久化配置。</summary>
        public bool Full { get; set; }

        /// <summary>正在被使用标记（仅 HasKey 选项有意义）：这个具体模型当前正被任务占用（精确到模型，
        /// 不按厂商铺开——同厂商其它空闲模型不着色），下拉中该项模型名称显示金色以示"在用"，仍可选。
        /// 与 Full（厂商级并发已满 → 禁选 +（满额））相互独立：正在用的那一项若恰好也满额，则金色与（满额）同现。
        /// 由 MainWindow 依 LlmConcurrencyGate.IsModelInUse 实时刷新写入，非持久化配置。</summary>
        public bool InUse { get; set; }

        /// <summary>模型调用异常标记：最近一次使用该模型的任务以"模型异常：…"收尾（额度用尽/网络/鉴权等，重试无效），
        /// 下拉中该模型名标红并带"⚠异常"提示。仍可选——用户点选该模型再次发送即重试，重试任务正常完成后自动消除。
        /// 由 MainWindow 依最近失败/成功收尾记录维护，非持久化配置。</summary>
        public bool Faulted { get; set; }

        /// <summary>该项下拉是否可选：未配 ApiKey 或厂商并发已满 → 不可选（灰显/桔色只读）</summary>
        public bool Selectable => HasKey && !Full;
    }

    /// <summary>返回单一下拉列表所需的全量选项：遍历 [Providers] 中每个厂商 × [Models] 中该厂商的候选模型；
    /// 有 ApiKey 的选项排在前面且标记 HasKey=true，无 ApiKey 的排在后面且标记 HasKey=false</summary>
    public List<ModelOption> ModelOptions()
    {
        var opts = new List<ModelOption>();
        foreach (var (display, key) in ProviderList())
        {
            var models = ModelsFor(key);
            if (models.Count == 0) continue;
            var hasKey = !string.IsNullOrEmpty(ProviderCfg(key).ApiKey);
            foreach (var m in models)
                opts.Add(new ModelOption($"{display}-{m}", key, m, display, hasKey));
        }
        // 网页模型通道（[WebChannels] 配置）：与真实厂商同列在一个下拉里，Provider 带 web: 前缀，
        // 始终可选（登录态由网页窗口自身维护，与 ApiKey 无关）
        foreach (var ch in WebChannels())
            // 第3位(展示用 ModelId)传 ch.Display（含"-专家"等后缀），使下拉显示与 [WebChannels] 显示名一致；
 // 实际调用走 WebChannelSpec.ModelId（适配器名，见 AgentLoop.cs），不受此处影响
 opts.Add(new ModelOption(ch.Display, ch.ProviderKey, ch.Display, WebChannelSpec.ProviderDisplay, true));
        // 有 key 的排在前面，无 key 的排在后面；同组内保持原顺序
        return opts.OrderByDescending(o => o.HasKey).ToList();
    }

    /// <summary>网页通道默认开启站点"深度思考"开关（[WebChannels] DeepThink，缺省 1=开）</summary>
    public bool WebChannelDeepThink => Get("WebChannels", "DeepThink", "1") != "0";

    /// <summary>网页通道默认开启站点"联网搜索"开关（[WebChannels] Search，缺省 1=开）</summary>
    public bool WebChannelWebSearch => Get("WebChannels", "Search", "1") != "0";

    /// <summary>网页通道自动修复侧车开关（[WebChannels] AutoRepair，缺省 1=开）：
    /// 适配脚本报错/超时时，旁路侧车自动采样页面 DOM 并调非网页模型生成补丁落盘（带冷却/配额/自动回滚）</summary>
    public bool WebChannelAutoRepair => Get("WebChannels", "AutoRepair", "1") != "0";

    /// <summary>网页模型通道清单（[WebChannels] 节 List：逗号分隔，每项"显示名|适配器|网址"）。
    /// 未配置返回空表 —— 桌面版据此注入网页后端并让模型下拉出现"网页-xxx"项，CLI 模式不注册。</summary>
    public List<WebChannelSpec> WebChannels()
    {
        var raw = Get("WebChannels", "List", "");
        if (string.IsNullOrWhiteSpace(raw)) return new List<WebChannelSpec>();
        return raw.Split(',')
            .Select(WebChannelSpec.ParseLine)
            .Where(s => s != null)
            .Select(s => s!)
            .ToList();
    }

    /// <summary>某供应商的模型候选清单：[Models] 节逗号分隔；未配则回退 [供应商] Model 单项</summary>
    public List<string> ModelsFor(string provider)
    {
        var list = Get("Models", provider, "");
        if (list.Length > 0)
            return list.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        var single = Get(provider, "Model", "");
        return single.Length > 0 ? new List<string> { single } : new List<string>();
    }

    /// <summary>最近使用的供应商（启动时优先恢复）</summary>
    public string LastProvider
    {
        get => Get("LLM", "LastProvider", "");
        set
        {
            if (!data.ContainsKey("LLM")) data["LLM"] = new(StringComparer.OrdinalIgnoreCase);
            data["LLM"]["LastProvider"] = value;
        }
    }

    /// <summary>最近使用的模型名（启动时优先恢复）</summary>
    public string LastModel
    {
        get => Get("LLM", "LastModel", "");
        set
        {
            if (!data.ContainsKey("LLM")) data["LLM"] = new(StringComparer.OrdinalIgnoreCase);
            data["LLM"]["LastModel"] = value;
        }
    }

    /// <summary>将内存中的修改写回 config.ini，保留注释与未改动行</summary>
    public void Write()
    {
        if (!Exists) return;

        var lines = File.ReadAllLines(ConfigPath).ToList();
        // 键去重忽略大小写：文件行的键名与内存键名大小写可能不同（如 lastprovider vs LastProvider），
        // 否则更新后追加阶段会把同一键再插一行
        var updated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string section = "";
        for (int i = 0; i < lines.Count; i++)
        {
            var raw = lines[i];
            var trimmed = raw.TrimStart();
            if (trimmed.Length == 0) continue;
            var ci = trimmed.IndexOf(';');
            if (ci == 0) continue; // 整行注释
            var content = ci > 0 ? trimmed[..ci].TrimEnd() : trimmed;
            if (content.Length == 0) continue;

            if (content.StartsWith('[') && content.EndsWith(']'))
            {
                section = content[1..^1].Trim();
                continue;
            }

            var ei = content.IndexOf('=');
            if (ei <= 0) continue;
            var key = content[..ei].Trim();
            var fullKey = $"{section}:{key}";
            if (data.TryGetValue(section, out var kv) && kv.TryGetValue(key, out var val))
            {
                if (!updated.Contains(fullKey))
                {
                    updated.Add(fullKey);
                    // 只替换值：保留前导空白、键名原样、= 后到值前之间的原样空格，行内注释（; 之后）原样不动
                    var leadLen = raw.Length - trimmed.Length;      // 前导空白长度
                    var eqInRaw = leadLen + ei;                     // = 在整行中的位置
                    var tailStart = leadLen + content.Length;       // 值尾（去尾随空白后）在整行中的位置
                    var vs = eqInRaw + 1;                           // 值起点：= 后第一个非空白字符
                    while (vs < tailStart && char.IsWhiteSpace(raw[vs])) vs++;
                    lines[i] = raw[..(eqInRaw + 1)] + raw[(eqInRaw + 1)..vs] + val + raw[tailStart..];
                }
            }
        }

        // 为缺失的键追加到对应节末尾
        foreach (var sec in data)
        {
            foreach (var kv in sec.Value)
            {
                var fullKey = $"{sec.Key}:{kv.Key}";
                if (updated.Contains(fullKey)) continue;

                var secLine = $"[{sec.Key}]";
                var idx = lines.FindIndex(l => l.Trim().Equals(secLine, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                {
                    lines.Insert(idx + 1, $"{kv.Key}={kv.Value}");
                }
                else
                {
                    lines.Add("");
                    lines.Add(secLine);
                    lines.Add($"{kv.Key}={kv.Value}");
                }
                updated.Add(fullKey);
            }
        }

        File.WriteAllLines(ConfigPath, lines, System.Text.Encoding.UTF8);
    }

    /* ---------- 错误码配置（配置驱动，替代硬编码的厂商专属错误映射） ---------- */

    /// <summary>错误码规则：从 [ErrorCodes] 节读取，格式"状态码:关键词=提示信息"；
    /// 关键词为空=匹配该状态码所有情况；关键词支持逗号分隔多个匹配词</summary>
    public List<ErrorCodeRule> ErrorCodeRules()
    {
        var rules = new List<ErrorCodeRule>();
        foreach (var line in GetLines("ErrorCodes"))
        {
            var rule = ErrorCodeRule.Parse(line);
            if (rule != null) rules.Add(rule);
        }
        return rules;
    }

    /// <summary>解析错误信息：按配置规则匹配，无匹配返回 null（调用方回退到通用提示）</summary>
    public string? ResolveError(int statusCode, string detail)
    {
        var lower = detail.ToLowerInvariant();
        foreach (var rule in ErrorCodeRules())
        {
            if (rule.StatusCode != statusCode) continue;
            if (rule.Keywords.Count == 0) return rule.Message; // 空关键词=通配该状态码
            if (rule.Keywords.Any(k => lower.Contains(k))) return rule.Message;
        }
        return null;
    }
}

/// <summary>错误码规则：配置驱动，一行一条</summary>
public sealed class ErrorCodeRule
{
    public int StatusCode { get; init; }
    public List<string> Keywords { get; init; } = new(); // 空=通配
    public string Message { get; init; } = "";

    /// <summary>解析配置行："状态码:关键词=提示信息"；关键词可逗号分隔；空关键词=通配</summary>
    public static ErrorCodeRule? Parse(string line)
    {
        var ci = line.IndexOf(':');
        if (ci <= 0) return null;
        if (!int.TryParse(line[..ci].Trim(), out var statusCode)) return null;

        var rest = line[(ci + 1)..];
        var ei = rest.IndexOf('=');
        if (ei <= 0) return null;

        var keywordsRaw = rest[..ei].Trim();
        var message = rest[(ei + 1)..].Trim();
        if (message.Length == 0) return null;

        var keywords = string.IsNullOrEmpty(keywordsRaw)
            ? new List<string>()
            : keywordsRaw.Split(',').Select(k => k.Trim().ToLowerInvariant()).Where(k => k.Length > 0).ToList();

        return new ErrorCodeRule { StatusCode = statusCode, Keywords = keywords, Message = message };
    }
}
