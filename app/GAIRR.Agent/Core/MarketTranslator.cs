using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GAIRR.Core;

/// <summary>
/// 市场条目描述批量翻译服务：后台调大模型把英文摘要翻成简体中文，
/// 结果写本地缓存（项目根 .gairr/market_translations.json：名称→中文）并返回调用方写回模型。
/// 翻译失败静默（界面继续显示英文原文），任何异常不外抛；同批并发由一个信号量闸住，避免重复请求。
/// </summary>
public static class MarketTranslator
{
    const int BatchSize = 20;                          // 单次批量条数（描述都很短，一次多翻几条省请求）

    static readonly SemaphoreSlim Gate = new(1, 1);    // 并发闸：全局同时只跑一个批量翻译任务

    static string CachePath(AppConfig cfg) => Path.Combine(cfg.ProjectRoot, ".gairr", "market_translations.json");

    /// <summary>翻译一批条目描述；返回 名称→中文 映射（含缓存命中与本次新译）。失败/未配置模型时仅返回命中的部分，不抛异常。</summary>
    public static async Task<Dictionary<string, string>> TranslateAsync(AppConfig cfg, IReadOnlyList<MarketEntryDef> items)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (items.Count == 0) return result;

        var cache = ReadCache(cfg);
        foreach (var m in items)
            if (cache.TryGetValue(m.Name, out var zh) && zh.Length > 0 && !result.ContainsKey(m.Name))
                result[m.Name] = zh;

        var todo = items
            .Where(m => !result.ContainsKey(m.Name) && m.Entry.Desc.Length > 0 && !HasCjk(m.Entry.Desc))
            .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First()).ToList();
        if (todo.Count == 0) return result;

        await Gate.WaitAsync();
        try
        {
            cache = ReadCache(cfg);   // 排队期间可能已被别的任务翻译完，复查一遍
            var fresh = new List<MarketEntryDef>();
            foreach (var m in todo)
                if (cache.TryGetValue(m.Name, out var zh) && zh.Length > 0 && !result.ContainsKey(m.Name))
                    result[m.Name] = zh;
                else fresh.Add(m);
            if (fresh.Count == 0) return result;

            for (var i = 0; i < fresh.Count; i += BatchSize)
            {
                var batch = fresh.Skip(i).Take(BatchSize).ToList();
                var map = await TranslateBatchAsync(cfg, batch);
                foreach (var (n, zh) in map) { result[n] = zh; cache[n] = zh; }
            }
            SaveCache(cfg, cache);
        }
        finally { Gate.Release(); }
        return result;
    }

    /// <summary>单个批量：调主配置模型翻译一批描述；解析失败或网络异常返回空映射（静默）。</summary>
    static async Task<Dictionary<string, string>> TranslateBatchAsync(AppConfig cfg, List<MarketEntryDef> batch)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var (baseUrl, apiKey, model) = cfg.ProviderCfg(cfg.NotesProvider);
            if (baseUrl.Length == 0 || apiKey.Length == 0) return map;   // 未配置模型跳过（与 TagRefEnricher 同策略）
            // 翻译只要结果不要思考链：不挂思考规则=请求不带任何思考参数（模型默认不思考或走自身默认档位）
            var client = new LLMClient(baseUrl, apiKey, model);

            var arr = new JsonArray();
            for (var i = 0; i < batch.Count; i++)
                arr.Add(new JsonObject
                {
                    ["i"] = i,
                    ["name"] = batch[i].Name,
                    ["desc"] = LLMClient.Trunc(batch[i].Entry.Desc, 300),
                });
            var messages = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = "你是专业翻译，把市场条目英文描述翻译成简体中文，保留专有名词原文。只输出 JSON，不要任何多余文字。" },
                new JsonObject { ["role"] = "user", ["content"] = "翻译以下条目描述，输出形如 [{\"i\":0,\"zh\":\"中文\"}] 的 JSON 数组：\n" + arr.ToJsonString() },
            };
            var resp = await client.ChatAsync(messages, new JsonArray(), CancellationToken.None);
            var content = resp.Content ?? "";
            var s = content.IndexOf('['); var e = content.LastIndexOf(']');
            if (s >= 0 && e > s) content = content[s..(e + 1)];   // 剥 ```json 等包裹
            if (JsonNode.Parse(content) is JsonArray ja)
                foreach (var node in ja)
                {
                    if (node is not JsonObject o) continue;
                    var idx = o["i"]?.GetValue<int>();
                    var zh = o["zh"]?.GetValue<string>() ?? "";
                    if (idx.HasValue && idx.Value >= 0 && idx.Value < batch.Count && zh.Length > 0)
                        map[batch[idx.Value].Name] = zh.Trim();
                }
        }
        catch { /* 翻译失败静默：界面继续显示英文原文 */ }
        return map;
    }

    /// <summary>判断字符串是否已含中文字符（含中文视为无需翻译）</summary>
    static bool HasCjk(string s)
    {
        foreach (var c in s)
            if (c >= 0x4E00 && c <= 0x9FFF) return true;
        return false;
    }

    /// <summary>读本地翻译缓存；损坏/缺失返回空字典</summary>
    static Dictionary<string, string> ReadCache(AppConfig cfg)
    {
        try
        {
            var p = CachePath(cfg);
            if (!File.Exists(p)) return new(StringComparer.OrdinalIgnoreCase);
            if (JsonNode.Parse(File.ReadAllText(p)) is not JsonObject root) return new(StringComparer.OrdinalIgnoreCase);
            var r = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in root)
                if (v is JsonValue jv && jv.GetValue<string>() is { Length: > 0 } zh)
                    r[k] = zh;
            return r;
        }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }

    /// <summary>同步查单条缓存中文摘要（列表条目重建时预填用；未命中/无缓存返回空串）</summary>
    public static string Peek(AppConfig cfg, string name)
        => ReadCache(cfg).TryGetValue(name, out var zh) ? zh : "";

    /// <summary>写本地翻译缓存（失败不影响界面）</summary>
    static void SaveCache(AppConfig cfg, Dictionary<string, string> cache)
    {
        try
        {
            var p = CachePath(cfg);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));   // 翻译缓存中文直存
        }
        catch { /* 缓存写失败不打扰 */ }
    }
}