using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GAIRR.Core;

/// <summary>向量语料收集器：从符号索引、项目摘要、历史标签三层来源生成条目列表，供 EmbedIndex 建库。</summary>
public static class EmbedCorpus
{
    /// <summary>语料条目：Key 唯一、Text 参与 embedding 的文本</summary>
    public sealed class Item
    {
        public string Key = "";
        public string Text = "";
        public string Type = ""; // symbol / summary / tag
    }

    /// <summary>收集项目全量语料（符号+摘要+标签）</summary>
    public static List<Item> Collect(AppConfig cfg)
    {
        var items = new List<Item>();
        var root = cfg.ProjectRoot;
        if (root.Length == 0 || !Directory.Exists(root)) return items;

        // 第一层：符号索引（symbols.json + symbol-notes.json）
        items.AddRange(CollectSymbols(root));

        // 第二层：项目摘要（notes.json）
        items.AddRange(CollectSummaries(root));

        // 第三层：历史标签（tag-refs.jsonl）
        items.AddRange(CollectTags(root));

        // 出口统一处理：去重 + 过滤纯英文符号（无中文的条目对中文查询无价值）+ 512 截断
        var result = new List<Item>();
        foreach (var item in items.DistinctBy(x => x.Key))
        {
            if (!HasChinese(item.Text)) continue;
            result.Add(new Item
            {
                Key = item.Key,
                Text = item.Text.Length > 512 ? item.Text[..512] : item.Text,
                Type = item.Type
            });
        }
        return result;
    }

    /// <summary>只收集单个文件的语料（单文件增量更新用）：避免为改一个文件重解析全项目 symbols/notes/tag-refs。
    /// 出口规则与 Collect 一致（含中文才入库 + 512 截断）；标签语料非文件维度，此处不涉及。</summary>
    public static List<Item> CollectFile(string root, string rel)
    {
        var items = new List<Item>();
        var relN = (rel ?? "").Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(root) || relN.Length == 0 || !Directory.Exists(root)) return items;

        items.AddRange(CollectSymbolsOfFile(root, relN));
        items.AddRange(CollectSummariesOfFile(root, relN));

        var result = new List<Item>();
        foreach (var item in items.DistinctBy(x => x.Key))
        {
            if (!HasChinese(item.Text)) continue;
            result.Add(new Item
            {
                Key = item.Key,
                Text = item.Text.Length > 512 ? item.Text[..512] : item.Text,
                Type = item.Type
            });
        }
        return result;
    }

    /// <summary>单文件符号语料：用 JsonDocument 流式扫 symbols.json（比 DOM 快一个量级），只取 rel 匹配的定义</summary>
    static List<Item> CollectSymbolsOfFile(string root, string relN)
    {
        var items = new List<Item>();
        try
        {
            var p = Path.Combine(root, ".gairr", "symbols.json");
            if (!File.Exists(p)) return items;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(p));
            if (!doc.RootElement.TryGetProperty("symbols", out var syms) ||
                syms.ValueKind != JsonValueKind.Object) return items;
            foreach (var prop in syms.EnumerateObject())
            {
                var name = prop.Name;
                if (!prop.Value.TryGetProperty("def", out var defs) || defs.ValueKind != JsonValueKind.Array) continue;
                foreach (var d in defs.EnumerateArray())
                {
                    if (d.ValueKind != JsonValueKind.Object) continue;
                    var r = d.TryGetProperty("rel", out var rv) ? rv.GetString() ?? "" : "";
                    if (!string.Equals(r.Replace('\\', '/'), relN, StringComparison.OrdinalIgnoreCase)) continue;
                    var note = d.TryGetProperty("note", out var nv) ? nv.GetString() ?? "" : "";
                    var text = string.IsNullOrWhiteSpace(note) ? name : $"{name} {note}";
                    items.Add(new Item { Key = $"symbol:{r}:{name}", Text = text, Type = "symbol" });
                }
            }
        }
        catch { }
        return items;
    }

    /// <summary>单文件摘要语料：从 notes.json 取该 rel 的 head 与方法注释（键形态不一致时按归一路径回退匹配）</summary>
    static List<Item> CollectSummariesOfFile(string root, string relN)
    {
        var items = new List<Item>();
        try
        {
            var p = Path.Combine(root, ".gairr", "notes.json");
            if (!File.Exists(p)) return items;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(p));
            var rootEl = doc.RootElement;
            if (rootEl.ValueKind != JsonValueKind.Object) return items;

            // notes.json 的键可能带反斜杠：先精确取，取不到再按归一路径找一次
            string key = relN;
            if (!rootEl.TryGetProperty(key, out var entry))
            {
                key = "";
                foreach (var kv in rootEl.EnumerateObject())
                {
                    if (!string.Equals(kv.Name.Replace('\\', '/'), relN, StringComparison.OrdinalIgnoreCase)) continue;
                    key = kv.Name; entry = kv.Value; break;
                }
                if (key.Length == 0) return items;
            }
            if (entry.ValueKind != JsonValueKind.Object) return items;

            var head = entry.TryGetProperty("head", out var hv) ? hv.GetString() ?? "" : "";
            if (!string.IsNullOrWhiteSpace(head))
                items.Add(new Item { Key = $"summary:{key}", Text = head, Type = "summary" });

            if (entry.TryGetProperty("methods", out var methods) && methods.ValueKind == JsonValueKind.Object)
            {
                var sb = new StringBuilder();
                foreach (var m in methods.EnumerateObject())
                {
                    var t = m.Value.ValueKind == JsonValueKind.String ? m.Value.GetString() ?? "" : "";
                    if (!string.IsNullOrWhiteSpace(t)) sb.Append(t).Append('；');
                }
                if (sb.Length > 0)
                {
                    var mtext = sb.ToString().TrimEnd('；');
                    items.Add(new Item { Key = $"summary:{key}:m", Text = mtext.Length > 400 ? mtext[..400] : mtext, Type = "summary" });
                }
            }
        }
        catch { }
        return items;
    }

    /// <summary>判断文本是否含至少一个中文字符</summary>
    static bool HasChinese(string text)
    {
        foreach (var c in text)
            if (c >= 0x4E00 && c <= 0x9FFF) return true;
        return false;
    }

    /// <summary>符号语料：符号名 + 中文标签（symbol-notes）</summary>
    static List<Item> CollectSymbols(string root)
    {
        var items = new List<Item>();
        try
        {
            var indexPath = Path.Combine(root, ".gairr", "symbols.json");
            if (!File.Exists(indexPath)) return items;
            using var fs = File.OpenRead(indexPath);
            var json = JsonSerializer.Deserialize<JsonObject>(fs);
            if (json == null) return items;
            // symbols.json 结构：symbols.<name>.def[] = { rel, line, kind, note, ... }
            var syms = json["symbols"]?.AsObject();
            if (syms == null) return items;
            foreach (var (name, symNode) in syms)
            {
                var defs = (symNode as JsonObject)?["def"]?.AsArray();
                if (defs == null) continue;
                foreach (var d in defs.OfType<JsonObject>())
                {
                    var rel = d["rel"]?.GetValue<string>() ?? "";
                    var note = d["note"]?.GetValue<string>() ?? "";
                    if (rel.Length == 0) continue;
                    var key = $"symbol:{rel}:{name}";
                    var text = string.IsNullOrWhiteSpace(note) ? name : $"{name} {note}";
                    items.Add(new Item { Key = key, Text = text, Type = "symbol" });
                }
            }
        }
        catch { }
        return items;
    }

    /// <summary>摘要语料：文件头注释/方法注释（notes.json）</summary>
    static List<Item> CollectSummaries(string root)
    {
        var items = new List<Item>();
        try
        {
            var notesPath = Path.Combine(root, ".gairr", "notes.json");
            if (!File.Exists(notesPath)) return items;
            using var fs = File.OpenRead(notesPath);
            var json = JsonSerializer.Deserialize<JsonObject>(fs);
            if (json == null) return items;
            foreach (var kv in json.OfType<KeyValuePair<string, JsonNode?>>())
            {
                if (kv.Value is not JsonObject entry) continue;
                var rel = kv.Key;
                // head：文件级摘要（可能为空）
                var head = entry["head"]?.GetValue<string>() ?? "";
                if (!string.IsNullOrWhiteSpace(head))
                    items.Add(new Item { Key = $"summary:{rel}", Text = head, Type = "summary" });
                // methods：方法级中文注释（head 大多为空时这是真正语料），合并为一条
                var methods = entry["methods"]?.AsObject();
                if (methods != null && methods.Count > 0)
                {
                    var sb2 = new StringBuilder();
                    foreach (var mv in methods)
                    {
                        var t = mv.Value?.GetValue<string>() ?? "";
                        if (!string.IsNullOrWhiteSpace(t)) sb2.Append(t).Append('；');
                    }
                    if (sb2.Length > 0)
                    {
                        var mtext = sb2.ToString().TrimEnd('；');
                        items.Add(new Item { Key = $"summary:{rel}:m", Text = mtext.Length > 400 ? mtext[..400] : mtext, Type = "summary" });
                    }
                }
            }
        }
        catch { }
        return items;
    }

    /// <summary>历史标签语料：tag-refs.jsonl 中累计的意图标签</summary>
    static List<Item> CollectTags(string root)
    {
        var items = new List<Item>();
        try
        {
            var path = Path.Combine(root, ".gairr", "tag-refs.jsonl");
            if (!File.Exists(path)) return items;
            using var sr = new StreamReader(path, Encoding.UTF8);
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.Length == 0) continue;
                var obj = JsonSerializer.Deserialize<JsonObject>(line);
                if (obj == null) continue;
                // 真实结构：tags 是字符串数组（可能含英文符号名），逐个收集
                var tags = obj["tags"]?.AsArray();
                if (tags == null) continue;
                foreach (var tn in tags.OfType<JsonObject>())
                {
                    var tag = tn.GetValue<string>() ?? "";
                    if (tag.Length == 0) continue;
                    var key = $"tag:{tag}";
                    items.Add(new Item { Key = key, Text = tag, Type = "tag" });
                }
            }
        }
        catch { }
        return items.DistinctBy(x => x.Key).ToList();
    }
}
