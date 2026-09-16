using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GAIRR.Core;

/// <summary>
/// 标签-符号关联积累器：从模型回复中提取"用户意图标签→代码符号"关联，追加到 .gairr/tag-refs.jsonl。
/// 与 SymbolIndex 互补：SymbolIndex 处理代码注释中的中文标签，TagRefAccumulator 处理对话中沉淀的语义关联。
/// </summary>
public static class TagRefAccumulator
{
    const string FileName = "tag-refs.jsonl";
    const int MaxTagLength = 20;      // 单标签最大长度
    const int MaxTags = 5;            // 最大标签数
    const int MinConfidence = 60;     // 最低置信度（百分制，低于此值不记录）

    // 积累库压缩阈值：jsonl 只追加，不清理会无界增长（每轮对话都可能写入）
    const int MaxEntries = 2000;                 // 条数上限，超出按"未失效优先 + Ts 最新"保留
    const long MaxBytes = 2 * 1024 * 1024;       // 体积阈值（2MB），超出即使条数未超限也压缩
    const int InvalidatedKeepDays = 30;          // 软删条目保留天数：期内可回查，期满物理清除

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // tag-refs.jsonl 中文标签直存
    };

    public record TagRefEntry(
        DateTime Ts,
        string Task,
        List<string> Tags,
        List<SymbolRef> Refs,
        string? Note = null,
        bool Invalidated = false,          // 软删标记：关联已失效（符号删除/改名/文件删除），保留原文可回查
        string? InvalidateReason = null,
        DateTime? InvalidatedAt = null
    );

    public record SymbolRef(
        string Rel,
        string Symbol,
        int Line,
        string Kind,
        double Confidence,
        string Source = "model"    // model|user|auto
    );

    /// <summary>从模型回复中提取关联表（返回 null 表示无有效关联）</summary>
    public static TagRefEntry? Extract(string userTask, string assistantReply)
    {
        try
        {
            // 匹配最后一个 ```json ... ``` 代码块（通常是关联表）
            var matches = Regex.Matches(assistantReply, @"```json\s*\n([\s\S]*?)\n\s*```", RegexOptions.Singleline);
            if (matches.Count == 0) return null;

            var lastMatch = matches[^1];
            var json = lastMatch.Groups[1].Value.Trim();

            // 快速过滤：不是对象格式直接跳过
            if (!json.StartsWith("{")) return null;

            var node = JsonNode.Parse(json) as JsonObject;
            if (node == null) return null;

            // 必须有 tags 或 refs 字段才认为是关联表
            if (node["tags"] == null && node["refs"] == null) return null;

            var tags = new List<string>();
            if (node["tags"] is JsonArray tagArr)
            {
                foreach (var t in tagArr)
                {
                    var s = t?.GetValue<string>()?.Trim() ?? "";
                    if (s.Length >= 2 && s.Length <= MaxTagLength && ContainsCjk(s))
                        tags.Add(s);
                }
            }

            var refs = new List<SymbolRef>();
            if (node["refs"] is JsonArray refArr)
            {
                foreach (var r in refArr)
                {
                    if (r is not JsonObject obj) continue;
                    var conf = obj["confidence"]?.GetValue<double>() ?? 0.5;
                    if (conf * 100 < MinConfidence) continue;  // 过滤低置信度

                    refs.Add(new SymbolRef(
                        Rel: obj["rel"]?.GetValue<string>() ?? "",
                        Symbol: obj["symbol"]?.GetValue<string>() ?? "",
                        Line: obj["line"]?.GetValue<int>() ?? 0,
                        Kind: obj["kind"]?.GetValue<string>() ?? "method",
                        Confidence: conf,
                        Source: obj["source"]?.GetValue<string>() ?? "model"
                    ));
                }
            }

            // 无有效数据则不记录
            if (tags.Count == 0 && refs.Count == 0) return null;

            // 标签去重限制
            tags = tags.Distinct().Take(MaxTags).ToList();

            return new TagRefEntry(DateTime.Now, userTask, tags, refs);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TagRef] 提取失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>主动分词关联解析（TagRefEnricher 用）：解析 {tokens:[...], refs:[...]} 输出，
    /// refs 校验 rel 文件存在与 line&gt;0（不依赖模型填 confidence，主动关联本身即事后确认）。</summary>
    public static TagRefEntry? ExtractAuto(AppConfig cfg, string? assistantReply, string userTask)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(assistantReply)) return null;
            var body = assistantReply.Trim();
            // 容忍 ```json 包裹
            if (body.StartsWith("```", StringComparison.Ordinal))
            {
                var start = body.IndexOf('\n');
                var end = body.LastIndexOf("```", StringComparison.Ordinal);
                if (start < 0 || end <= start) return null;
                body = body[(start + 1)..end].Trim();
            }
            if (!body.StartsWith('{')) { var lb = body.IndexOf('{'); if (lb < 0) return null; body = body[lb..]; }
            var node = JsonNode.Parse(body) as JsonObject;
            if (node == null) return null;
            if (node["tokens"] == null && node["refs"] == null) return null;

            var tags = new List<string>();
            if (node["tokens"] is JsonArray tkArr)
                foreach (var t in tkArr)
                {
                    var s = t?.GetValue<string>()?.Trim() ?? "";
                    if (s.Length >= 2 && s.Length <= MaxTagLength && !tags.Contains(s)) tags.Add(s);
                }
            tags = tags.Take(MaxTags).ToList();

            var refs = new List<SymbolRef>();
            if (node["refs"] is JsonArray refArr)
                foreach (var r in refArr)
                {
                    if (r is not JsonObject obj) continue;
                    var rel = obj["rel"]?.GetValue<string>() ?? "";
                    var line = obj["line"]?.GetValue<int>() ?? 0;
                    var symbol = obj["symbol"]?.GetValue<string>() ?? "";
                    if (rel.Length == 0 || symbol.Length == 0 || line <= 0) continue;
                    var abs = Path.IsPathRooted(rel) ? rel
                        : Path.Combine(cfg.ProjectRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(abs)) continue;   // 校验：文件必须真实存在
                    refs.Add(new SymbolRef(rel.Replace('\\', '/'), symbol, line,
                        obj["kind"]?.GetValue<string>() ?? "method", 0.9, "auto"));
                }
            if (tags.Count == 0 && refs.Count == 0) return null;
            return new TagRefEntry(DateTime.Now, userTask, tags, refs);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TagRef] 主动关联解析失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>校验并清理积累库（符号索引全量重建后由 SymbolIndex.CleanupAfterBuild 调用）：
    /// 1) 引用的符号已不存在/文件已删除 → 软删（invalidated 标记，保留原文可回查）；
    /// 2) 符号存活但定义行漂移 → 行号重定位；3) 顺带清理 notes.json 中已删除文件的孤儿键。
    /// 返回清理统计文本（无变化时返回空串）。</summary>
    public static string ValidateAndClean(string root, List<string> goneSymbols,
        Dictionary<string, Dictionary<string, int>> livePos, HashSet<string> liveFiles)
    {
        var path = Path.Combine(root, ".gairr", FileName);
        if (!File.Exists(path)) return "";
        var gone = new HashSet<string>(goneSymbols, StringComparer.OrdinalIgnoreCase);
        var lines = File.ReadAllLines(path);
        var invalidated = 0; var relocated = 0; var changed = false; var alreadyInvalidated = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<TagRefEntry>(lines[i], JsonOpts);
                if (entry == null) continue;
                // 历史软删条目不做校验/重定位，但计数用作压缩触发条件（由压缩阶段物理清除）
                if (entry.Invalidated) { alreadyInvalidated++; continue; }
                var reason = "";
                var newRefs = new List<SymbolRef>();
                var relocating = false;
                foreach (var r in entry.Refs)
                {
                    if (string.IsNullOrEmpty(r.Rel) || string.IsNullOrEmpty(r.Symbol)) continue;
                    var relNorm = r.Rel.Replace('\\', '/');
                    if (!liveFiles.Contains(relNorm)) { reason = "文件已删除"; break; }
                    if (!livePos.TryGetValue(relNorm, out var m)) { reason = "文件已删除"; break; }
                    if (m.TryGetValue(r.Symbol, out var nl))
                    {
                        if (nl != r.Line && nl > 0) { newRefs.Add(r with { Line = nl }); relocating = true; }
                        else newRefs.Add(r);
                        continue;
                    }
                    if (gone.Contains(r.Symbol)) { reason = "符号已删除或改名"; break; }
                    reason = "符号不存在"; break;
                }
                if (reason.Length > 0)
                {
                    lines[i] = JsonSerializer.Serialize(entry with
                    {
                        Invalidated = true,
                        InvalidateReason = reason,
                        InvalidatedAt = DateTime.Now,
                    }, JsonOpts);
                    invalidated++; changed = true;
                }
                else if (relocating)
                {
                    lines[i] = JsonSerializer.Serialize(entry with { Refs = newRefs }, JsonOpts);
                    relocated++; changed = true;
                }
            }
            catch { /* 单行损坏跳过 */ }
        }
        // 无损压缩：物理清除过期软删条目 + 合并重复关联 + 超上限截断（触发才做，二次解析成本只在全量维护时付一次）
        var compactStat = "";
        var needCompact = invalidated > 0 || alreadyInvalidated > 0 || lines.Length > MaxEntries;
        if (!needCompact)
        {
            try { needCompact = new FileInfo(path).Length > MaxBytes; } catch { }
        }
        if (needCompact)
        {
            var (newLines, compactChanged, stat) = Compact(lines);
            if (compactChanged) { lines = newLines; changed = true; }
            compactStat = stat;
        }

        // 与上面的校验改动合并为一次写盘（同一文件不写两遍）
        if (changed) File.WriteAllLines(path, lines, Encoding.UTF8);

        // 顺带：清理 notes.json 中已删除文件的孤儿键（键归一化 '/' 比较）
        try
        {
            var notesPath = Path.Combine(root, ".gairr", "notes.json");
            if (File.Exists(notesPath) && JsonNode.Parse(File.ReadAllText(notesPath)) is JsonObject no)
            {
                var dirty = false;
                foreach (var rel in no.Select(x => x.Key.Replace('\\', '/')).Where(k => !liveFiles.Contains(k)).ToList())
                { no.Remove(rel); dirty = true; }
                if (dirty) File.WriteAllText(notesPath, no.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(false));   // 保持原紧凑格式，中文直存
            }
        }
        catch { }

        var msg = invalidated == 0 && relocated == 0
            ? ""
            : $"积累库清理：失效 {invalidated} 条，行号重定位 {relocated} 条";
        if (compactStat.Length > 0) msg = msg.Length == 0 ? compactStat : msg + "；" + compactStat;
        return msg;
    }

    /// <summary>无损压缩积累库（全量维护时由 ValidateAndClean 调用，一次解析一次写盘）：
    /// ① 物理清除已过保留期的软删条目——Query 本就跳过它们，属纯死重；保留期内的留着可回查；
    /// ② 合并重复关联：同 (标签集 + rel:symbol) 签名只留一条，未失效优先、Ts 最新优先；
    /// ③ 条数超上限时截断：未失效优先 + Ts 倒序保留 MaxEntries 条。
    /// 返回 (压缩后的行, 是否有变化, 统计说明)；无变化时原样返回，调用方不写盘。</summary>
    static (string[] Lines, bool Changed, string Stat) Compact(string[] lines)
    {
        var entries = new List<TagRefEntry>(lines.Length);
        var broken = 0;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var e = JsonSerializer.Deserialize<TagRefEntry>(line, JsonOpts);
                // 标签与 refs 全空（或反序列化失败）视为无效行，压缩时直接丢弃
                if (e == null || ((e.Tags?.Count ?? 0) == 0 && (e.Refs?.Count ?? 0) == 0)) { broken++; continue; }
                entries.Add(e);
            }
            catch { broken++; }
        }

        // ① 过期软删条目物理清除
        var keepFrom = DateTime.Now.AddDays(-InvalidatedKeepDays);
        var live = new List<TagRefEntry>(entries.Count);
        var droppedInvalid = 0;
        foreach (var e in entries)
        {
            if (!e.Invalidated) { live.Add(e); continue; }
            if ((e.InvalidatedAt ?? e.Ts) >= keepFrom) live.Add(e);   // 保留期内：仍可回查
            else droppedInvalid++;
        }

        // ② 合并重复关联（签名相同 = 同一批标签指向同一组符号）
        var merged = new Dictionary<string, TagRefEntry>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var e in live)
        {
            var sig = Sig(e);
            if (!merged.TryGetValue(sig, out var old))
            {
                merged[sig] = e;
                order.Add(sig);
                continue;
            }
            // 未失效的胜过已失效的；同状态则 Ts 更新的胜出（行号更准）
            if (!e.Invalidated && (old.Invalidated || e.Ts > old.Ts)) merged[sig] = e;
        }
        var deduped = order.Select(k => merged[k]).ToList();
        var droppedDup = live.Count - deduped.Count;

        // ③ 条数上限兜底：保留"未失效 + 最近"的经验
        var final = deduped;
        var droppedOld = 0;
        if (final.Count > MaxEntries)
        {
            final = final.OrderBy(e => e.Invalidated ? 1 : 0)
                         .ThenByDescending(e => e.Ts)
                         .Take(MaxEntries).ToList();
            droppedOld = deduped.Count - final.Count;
        }

        if (droppedInvalid + droppedDup + droppedOld + broken == 0) return (lines, false, "");

        var parts = new List<string>();
        if (droppedInvalid > 0) parts.Add($"清除失效 {droppedInvalid} 条");
        if (droppedDup > 0) parts.Add($"合并重复 {droppedDup} 条");
        if (droppedOld > 0) parts.Add($"超限截断 {droppedOld} 条");
        if (broken > 0) parts.Add($"丢弃无效 {broken} 条");
        var outLines = final.Select(e => JsonSerializer.Serialize(e, JsonOpts)).ToArray();
        return (outLines, true, $"积累库压缩：{string.Join("，", parts)}，余 {final.Count} 条");
    }

    /// <summary>重复关联签名：标签集（排序）+ 各 ref 的 rel:symbol（排序）。
    /// 行号不参与签名——行号漂移正是 ValidateAndClean 要修的，不能拿来当"是否重复"的区分依据。</summary>
    static string Sig(TagRefEntry e)
    {
        var tags = string.Join("|", (e.Tags ?? new List<string>()).OrderBy(t => t, StringComparer.Ordinal));
        var refs = string.Join("|", (e.Refs ?? new List<SymbolRef>())
            .Select(r => (r.Rel ?? "").Replace('\\', '/') + ":" + r.Symbol)
            .OrderBy(s => s, StringComparer.Ordinal));
        return tags + "#" + refs;
    }

    /// <summary>手动触发积累库无损压缩（读全量 → Compact → 一次写盘）；返回统计说明，无变化返回空串。
    /// 常规路径由 ValidateAndClean 在全量维护时自动触发，此处供手动维护与验证使用。</summary>
    public static string CompactNow(AppConfig cfg)
    {
        try
        {
            var path = Path.Combine(cfg.ProjectRoot, ".gairr", FileName);
            if (!File.Exists(path)) return "";
            var lines = File.ReadAllLines(path);
            var (newLines, changed, stat) = Compact(lines);
            if (!changed) return "";
            File.WriteAllLines(path, newLines, Encoding.UTF8);
            return stat;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TagRef] 压缩失败: {ex.Message}");
            return "";
        }
    }

    /// <summary>追加保存到库表</summary>
    public static void Append(AppConfig cfg, TagRefEntry entry)
    {
        try
        {
            var dir = Path.Combine(cfg.ProjectRoot, ".gairr");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, FileName);

            var line = JsonSerializer.Serialize(entry, JsonOpts);
            File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TagRef] 保存失败: {ex.Message}");
        }
    }

    // 积累库进程级缓存：tag-refs.jsonl 只追加不清理（已数百 KB），每次查询重解析会随历史累积线性变慢；
    // 按 mtime+长度 判定失效，未变则复用上次结果（返回浅拷贝，避免调用方改动污染缓存）
    static List<TagRefEntry>? _allCache;
    static string _allCachePath = "";
    static DateTime _allCacheMtime;
    static long _allCacheLen;
    static readonly object AllCacheSync = new();

    /// <summary>加载所有积累记录（带 mtime 缓存，文件未变则不重新解析）</summary>
    public static List<TagRefEntry> LoadAll(AppConfig cfg)
    {
        var result = new List<TagRefEntry>();
        try
        {
            var path = Path.Combine(cfg.ProjectRoot, ".gairr", FileName);
            if (!File.Exists(path))
            {
                lock (AllCacheSync) { _allCache = null; _allCachePath = ""; }
                return result;
            }

            var fi = new FileInfo(path);
            lock (AllCacheSync)
            {
                if (_allCache != null && _allCachePath == path &&
                    _allCacheMtime == fi.LastWriteTimeUtc && _allCacheLen == fi.Length)
                    return new List<TagRefEntry>(_allCache);
            }

            result = ParseAll(path);
            lock (AllCacheSync)
            {
                _allCache = result;
                _allCachePath = path;
                _allCacheMtime = fi.LastWriteTimeUtc;
                _allCacheLen = fi.Length;
                return new List<TagRefEntry>(result);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TagRef] 加载失败: {ex.Message}");
        }
        return result;
    }

    /// <summary>逐行解析 jsonl（仅缓存未命中时调用）</summary>
    static List<TagRefEntry> ParseAll(string path)
    {
        var list = new List<TagRefEntry>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<TagRefEntry>(line, JsonOpts);
                if (entry != null) list.Add(entry);
            }
            catch { /* 单行损坏跳过 */ }
        }
        return list;
    }

    /// <summary>按标签查询积累记录（支持部分匹配）</summary>
    public static List<(TagRefEntry Entry, SymbolRef Ref, int Score)> Query(AppConfig cfg, string query)
    {
        var results = new List<(TagRefEntry, SymbolRef, int Score)>();
        var entries = LoadAll(cfg);
        var queryWords = SplitWords(query);

        foreach (var entry in entries)
        {
            if (entry.Invalidated) continue;   // 软删条目不进检索
            var tagScore = 0;
            // 标签匹配（权重最高）
            foreach (var tag in entry.Tags)
            {
                foreach (var qw in queryWords)
                {
                    if (tag.Contains(qw, StringComparison.OrdinalIgnoreCase) ||
                        qw.Contains(tag, StringComparison.OrdinalIgnoreCase))
                    {
                        tagScore += 10;
                    }
                }
            }

            // 任务描述匹配
            var taskScore = 0;
            foreach (var qw in queryWords)
            {
                if (entry.Task.Contains(qw, StringComparison.OrdinalIgnoreCase))
                    taskScore += 3;
            }

            var totalScore = tagScore + taskScore;
            if (totalScore > 0)
            {
                foreach (var r in entry.Refs)
                {
                    results.Add((entry, r, totalScore + (int)(r.Confidence * 5)));
                }
            }
        }

        return results.OrderByDescending(r => r.Score).ToList();
    }

    static bool ContainsCjk(string s) => s.Any(ch => ch >= 0x4E00 && ch <= 0x9FFF);

    static List<string> SplitWords(string text)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            if (ch >= '\u4e00' && ch <= '\u9fff')
            {
                if (sb.Length > 0 && sb[0] >= '\u4e00' && sb[0] <= '\u9fff')
                    sb.Append(ch);
                else
                {
                    if (sb.Length > 0) result.Add(sb.ToString().Trim());
                    sb.Clear(); sb.Append(ch);
                }
            }
            else if (char.IsLetterOrDigit(ch))
            {
                if (sb.Length > 0 && !(sb[0] >= '\u4e00' && sb[0] <= '\u9fff'))
                    sb.Append(ch);
                else
                {
                    if (sb.Length > 0) result.Add(sb.ToString().Trim());
                    sb.Clear(); sb.Append(ch);
                }
            }
            else
            {
                if (sb.Length > 0) result.Add(sb.ToString().Trim());
                sb.Clear();
            }
        }
        if (sb.Length > 0) result.Add(sb.ToString().Trim());
        return result.Where(s => s.Length >= 2).ToList();
    }
}
