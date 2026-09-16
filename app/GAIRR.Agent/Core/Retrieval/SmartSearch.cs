using System.IO;
using System.Text;

namespace GAIRR.Core;

/// <summary>
/// 智能语义搜索：整合四层检索体系（积累库 → 注释标签 → 规则词典 → 摘要匹配），
/// 支持中文查询，返回最相关的代码位置。
/// </summary>
public static class SmartSearch
{
    /// <summary>搜索结果项</summary>
    public class Result
    {
        public string Key = "";           // 唯一标识
        public string Type = "";          // accumulated / tag / symbol / summary
        public int Score = 0;             // 相关性分数
        public string Title = "";         // 显示标题
        public string Description = "";   // 描述
        public int Line = 0;              // 行号
        public string RelPath = "";       // 相对路径
        public string Symbol = "";        // 符号名
        public string Text = "";          // 可打分文本（BM25 词频统计用）
    }

    /// <summary>
    /// 执行智能搜索：按优先级逐层查询，返回合并排序后的结果。
    /// 五层体系：积累库 → SymbolIndex 中文标签 → ZhEnMapping 规则词典 → ProjectMap 摘要 → 本地向量（可选）。
    /// </summary>
    public static string Run(AppConfig cfg, string query, int maxResults = 5)
    {
        var results = new List<Result>();

        // 词典展开（纯函数）先算好，供符号层并行任务内共享
        var expandedTerms = ZhEnMapping.ExpandQuery(query);

        // 最安稳的并行编排（不拆任何检索方法、不改任何锁）：
        //  1) 共享同一把锁/同一数据源的层合并进同一任务串行执行 → 任务间零锁竞争、零死锁风险；
        //  2) 冷启动时 symbols/map 构建自然在各任务串行段完成（即就绪预热），任务间并行让两处构建重叠；
        //  3) 各层方法内部已有 try/catch 兜底，任务体外再包一层，保证并行任务绝不外抛。
        var collected = new List<Result>[4];
        var tasks = new Task[4];

        // 任务 1：积累库查询（无锁只读，独立数据源 tag-refs.jsonl）
        tasks[0] = Task.Run(() =>
        {
            try { collected[0] = SearchAccumulated(cfg, query); }
            catch { collected[0] = new List<Result>(); }
        });

        // 任务 2：中文标签层 + 词典/符号名层（同依赖 SymbolIndex 的一把锁，合并串行最稳）
        tasks[1] = Task.Run(() =>
        {
            try
            {
                var merged = new List<Result>();
                merged.AddRange(SearchSymbolTags(cfg, query));
                merged.AddRange(SearchSymbols(cfg, query, expandedTerms));
                collected[1] = merged;
            }
            catch { collected[1] = new List<Result>(); }
        });

        // 任务 3：ProjectMap 摘要层（独立锁，单独任务并行）
        tasks[2] = Task.Run(() =>
        {
            try { collected[2] = SearchMapSummaries(cfg, query); }
            catch { collected[2] = new List<Result>(); }
        });

        // 任务 4：本地向量层（ONNX embedding + 余弦 TopK，开关控制，失败降级为空）
        tasks[3] = Task.Run(() =>
        {
            try { collected[3] = SystemCfg.Embed ? SearchEmbedding(cfg, query) : new List<Result>(); }
            catch { collected[3] = new List<Result>(); }
        });

        Task.WaitAll(tasks);
        // 向量层（collected[3]）不参与主合并：先限席、BM25 细排之后才追加，防 cos 高分挤掉确定性层
        for (int i = 0; i < 3; i++) results.AddRange(collected[i]);

        // ── 早停优化：确定性层已命中足够多高分结果时，跳过 BM25 细排（节省 ~5ms） ──
        // 判定：前 3 层去重后 ≥ maxResults 条且最低分 ≥ 20（强命中阈值），直接排序截取
        var preRanked = results
            .GroupBy(r => r.Key)
            .Select(g => g.OrderByDescending(r => r.Score).First())
            .OrderByDescending(r => r.Score)
            .ToList();
        if (preRanked.Count >= maxResults && preRanked.Take(maxResults).Min(r => r.Score) >= 20)
        {
            // 向量层限席追加
            if (collected[3] != null)
            {
                var embResults = collected[3]
                    .OrderByDescending(r => r.Score)
                    .Take(EmbedMaxPerLayer)
                    .ToList();
                preRanked.AddRange(embResults);
            }
            return FormatResults(preRanked.OrderByDescending(r => r.Score).Take(maxResults).ToList(), query);
        }

        // 去重后 BM25 细排：层基础分主导（语义层级优先），同层命中按词频相关性再排序
        var ranked = preRanked;
        var bm25 = Bm25Scorer.Rank(query,
            ranked.Select(r => new KeyValuePair<string, string>(r.Key, r.Text)));
        foreach (var r in ranked)
            if (bm25.TryGetValue(r.Key, out var bs)) r.Score += (int)Math.Round(bs * 10);

        // tag 层配额兜底：仅 gram 擦边的弱标签命中（10 分）最多保留 2 席，防 Program/Id 等高频符号占满结果；
        // 整串命中（15 分）全保留，其他层（accumulated/symbol/summary）结果自然补位
        var tags = ranked.Where(r => r.Type == "tag").ToList();
        if (tags.Count > 2 && ranked.Any(r => r.Type != "tag"))
        {
            var strong = tags.Where(t => t.Score >= 15).ToHashSet();
            var weakTop = tags.Where(t => t.Score < 15).OrderByDescending(t => t.Score).Take(2).ToHashSet();
            ranked = ranked.Where(r => r.Type != "tag" || strong.Contains(r) || weakTop.Contains(r)).ToList();
        }

        // 向量层限席（≤2）后追加：cos 阈值已在 SearchEmbedding 内过滤（≥EmbedMinScore），此处只限席
        if (collected[3] != null)
        {
            var embResults = collected[3]
                .OrderByDescending(r => r.Score)
                .Take(EmbedMaxPerLayer)
                .ToList();
            ranked.AddRange(embResults);
        }

        ranked = ranked.OrderByDescending(r => r.Score).Take(maxResults).ToList();

        return FormatResults(ranked, query);
    }

    /* ---------- 第四层：本地向量检索 ---------- */

    // 余弦阈值：实测 bge-small 语义等价对 ~0.97、相关对 ~0.39、漂移对 0.52~0.575、无关对 ~0.20；
    // 定位"语义等价联想"（方案核心），故 0.65 只放行近等价改写，防泛化词蹭分（如"自动修复"蹭"自动重试"）
    const float EmbedMinScore = 0.65f;
    const int EmbedMaxPerLayer = 2;    // 向量层限席

    /// <summary>获取向量索引：走 EmbedIndex 进程级共享实例（写侧同一份内存，落盘不再触发重载）；
    /// 仅当其他进程改写了磁盘文件时才重载</summary>
    static EmbedIndex GetCachedIndex(string indexPath)
    {
        var idx = EmbedIndex.For(indexPath);
        idx.ReloadIfChangedExternally();
        return idx;
    }

    static List<Result> SearchEmbedding(AppConfig cfg, string query)
    {
        var results = new List<Result>();
        if (!SystemCfg.Embed) return results;
        try
        {
            var root = cfg.ProjectRoot;
            if (root.Length == 0 || !Directory.Exists(root)) return results;
            var indexPath = Path.Combine(root, ".gairr", EmbedIndex.FileName);
            if (!File.Exists(indexPath)) return results;

            var index = GetCachedIndex(indexPath);
            if (index.Count == 0) return results;

            var engine = LocalEmbeddingEngine.Shared;
            if (!engine.IsAvailable || !engine.EnsureReady()) return results;

            var queryVec = engine.Embed(query);
            if (queryVec == null) return results;

            var hits = index.Search(queryVec, topK: 10);
            foreach (var (key, cos) in hits)
            {
                if (cos < EmbedMinScore) break;
                var entry = index.Get(key);
                if (entry == null) continue;
                // cos → Score 统一排序（0-1 → 0-100）
                var score = (int)(cos * 100);
                // 解析 key：symbol:rel:symbol 三段 / summary:rel 两段 / tag:标签 两段
                var parts = key.Split(':', 3);
                if (parts.Length < 2) continue;
                var layer = parts[0];
                string relPath, symbol, note;
                if (parts.Length == 3) { relPath = parts[1]; symbol = parts[2]; note = symbol; }
                else if (layer == "summary") { relPath = parts[1]; symbol = ""; note = "文件摘要"; }
                else { relPath = ""; symbol = parts[1]; note = parts[1]; } // tag: 标签文本
                results.Add(new Result
                {
                    Key = $"{key}",
                    Type = "vector",
                    Score = score,
                    Title = parts.Length == 3 ? $"{symbol} ({relPath})"
                        : layer == "summary" ? $"文件摘要 ({relPath})"
                        : $"标签「{symbol}」",
                    Description = $"向量相似：cos={cos:F3} {entry.Text}",
                    Line = 0,
                    RelPath = relPath,
                    Symbol = symbol,
                    Text = $"{entry.Text} {symbol}"
                });
            }
        }
        catch { }
        return results;
    }

    /* ---------- 第一层：积累库查询 ---------- */

    static List<Result> SearchAccumulated(AppConfig cfg, string query)
    {
        var results = new List<Result>();
        try
        {
            var hits = TagRefAccumulator.Query(cfg, query);
            foreach (var (entry, r, score) in hits)
            {
                var key = $"{r.Rel}:{r.Line}";
                results.Add(new Result
                {
                    Key = key,
                    Type = "accumulated",
                    Score = score,
                    Title = $"{r.Symbol} ({r.Rel})",
                    Description = $"历史关联：{string.Join("、", entry.Tags.Take(3))}",
                    Line = r.Line,
                    RelPath = r.Rel,
                    Symbol = r.Symbol,
                    Text = $"{string.Join("、", entry.Tags)} {r.Symbol}"
                });
            }
        }
        catch { /* 积累库查询失败不影响其他层 */ }
        return results;
    }

    /* ---------- 第二层：SymbolIndex 中文标签 ---------- */

    static List<Result> SearchSymbolTags(AppConfig cfg, string query)
    {
        var results = new List<Result>();
        try
        {
            // 直接调用 SymbolIndex，利用其已有的中文标签匹配能力
            var output = SymbolIndex.Run(cfg, query, 10, true);
            if (output.StartsWith("符号索引：未找到")) return results;

            // 解析 SymbolIndex 输出文本（简化解析：提取定义行；“标签: ”行紧跟定义行，用于区分“整串命中/仅 gram 擦边”并供给 BM25 细排）
            var lines = output.Split('\n');
            string? currentSymbol = null;
            foreach (var line in lines)
            {
                if (line.StartsWith("▸ "))
                {
                    currentSymbol = line[2..].Split('（')[0].Trim();
                }
                else if (line.StartsWith("  定义: ") && currentSymbol != null)
                {
                    var parts = line[6..].Split('（')[0].Split(':');
                    if (parts.Length >= 2 && int.TryParse(parts[1], out var lineNum))
                    {
                        var rel = parts[0];
                        var key = $"{rel}:{lineNum}";
                        results.Add(new Result
                        {
                            Key = key,
                            Type = "tag",
                            Score = 15,  // 中文标签整串命中分数（仅 gram 擦边在标签行解析后降为 10）
                            Title = $"{currentSymbol} ({rel})",
                            Description = "SymbolIndex 中文标签命中",
                            Line = lineNum,
                            RelPath = rel,
                            Symbol = currentSymbol,
                            Text = currentSymbol
                        });
                    }
                }
                else if (line.StartsWith("    标签: ") && results.Count > 0)
                {
                    var note = line[8..].Trim();
                    if (note.Length == 0) continue;
                    var last = results[^1];
                    last.Text = last.Symbol + " " + note;   // 中文标签进 BM25 打分文本，层内细排生效
                    var qNorm = query.Replace(" ", "").Replace("　", "");
                    var nNorm = note.Replace(" ", "").Replace("　", "");
                    if (qNorm.Length > 0 && nNorm.Contains(qNorm, StringComparison.OrdinalIgnoreCase))
                    {
                        last.Score = 15;                       // 标签整串命中：强相关
                        last.Description = "SymbolIndex 中文标签命中（整串）";
                    }
                    else
                    {
                        last.Score = 10;                       // 仅 2-gram 擦边：弱相关，层配额兜底控制席位数
                        last.Description = "SymbolIndex 中文标签命中（近似）";
                    }
                }
            }
        }
        catch { /* SymbolIndex 查询失败不影响其他层 */ }
        return results;
    }

    /* ---------- 第三层：规则词典 + 符号名检索 ---------- */

    static List<Result> SearchSymbols(AppConfig cfg, string query, List<string> expandedTerms)
    {
        var results = new List<Result>();
        var allTerms = new List<string> { query };
        allTerms.AddRange(expandedTerms);

        try
        {
            foreach (var term in allTerms)
            {
                if (string.IsNullOrWhiteSpace(term)) continue;
                var output = SymbolIndex.Run(cfg, term, 5, true);
                if (output.StartsWith("符号索引：未找到")) continue;

                var lines = output.Split('\n');
                string? currentSymbol = null;
                foreach (var line in lines)
                {
                    if (line.StartsWith("▸ "))
                    {
                        currentSymbol = line[2..].Split('（')[0].Trim();
                    }
                    else if (line.StartsWith("  定义: ") && currentSymbol != null)
                    {
                        var parts = line[6..].Split('（')[0].Split(':');
                        if (parts.Length >= 2 && int.TryParse(parts[1], out var lineNum))
                        {
                            var rel = parts[0];
                            var key = $"{rel}:{lineNum}";
                            // 检查是否已存在（去重）
                            if (results.Any(r => r.Key == key)) continue;
                            results.Add(new Result
                            {
                                Key = key,
                                Type = "symbol",
                                Score = term == query ? 12 : 8,  // 原始查询分数更高
                                Title = $"{currentSymbol} ({rel})",
                                Description = $"符号名匹配：{term}",
                                Line = lineNum,
                                RelPath = rel,
                                Symbol = currentSymbol,
                                Text = currentSymbol
                            });
                        }
                    }
                }
            }
        }
        catch { /* 符号检索失败不影响其他层 */ }
        return results;
    }

    /* ---------- 第四层：ProjectMap 摘要匹配 ---------- */

    static List<Result> SearchMapSummaries(AppConfig cfg, string query)
    {
        var results = new List<Result>();
        try
        {
            var mapText = ProjectMap.Build(cfg);
            var queryWords = Bm25Scorer.SplitWords(query);
            if (queryWords.Count == 0) return results;

            // 中文整串常被摘要措辞拆散：拆 2-gram 提升召回（整串命中权重更高）
            var grams = new List<string>();
            foreach (var qw in queryWords)
            {
                if (qw.Length > 0 && qw[0] >= '\u4e00' && qw[0] <= '\u9fff')
                {
                    if (qw.Length <= 2) grams.Add(qw);
                    else for (int i = 0; i < qw.Length - 1; i++) grams.Add(qw.Substring(i, 2));
                }
            }

            var lines = mapText.Split('\n');
            foreach (var line in lines)
            {
                if (!line.StartsWith("· ")) continue;
                var fileName = line[2..].Split("  [")[0].Trim();
                var score = 0;
                foreach (var qw in queryWords)
                {
                    if (line.Contains(qw, StringComparison.OrdinalIgnoreCase))
                        score += 3;
                }
                foreach (var g in grams.Distinct())
                {
                    if (line.Contains(g, StringComparison.OrdinalIgnoreCase))
                        score += 1;
                }
                if (score > 0)
                {
                    var key = fileName;
                    results.Add(new Result
                    {
                        Key = key,
                        Type = "summary",
                        Score = score,
                        Title = fileName,
                        Description = "ProjectMap 摘要匹配",
                        Line = 0,
                        RelPath = fileName,
                        Symbol = "",
                        Text = line
                    });
                }
            }
        }
        catch { /* 摘要匹配失败不影响其他层 */ }
        return results;
    }

    /* ---------- 结果格式化 ---------- */

    static string FormatResults(List<Result> results, string query)
    {
        if (results.Count == 0)
            return $"智能搜索：未找到与 \"{query}\" 相关的结果（试试更简短的关键词）";

        var sb = new StringBuilder();
        sb.Append($"智能搜索：\"{query}\" 命中 {results.Count} 个结果\n");
        sb.Append("（按相关性排序；accumulated=历史积累、tag=中文标签、symbol=符号名、summary=文件摘要、vector=语义向量）\n\n");

        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            sb.Append($"{i + 1}. [{r.Type}] {r.Title}\n");
            if (r.Line > 0)
                sb.Append($"   位置: {r.RelPath}:{r.Line}\n");
            else
                sb.Append($"   文件: {r.RelPath}\n");
            if (r.Description.Length > 0)
                sb.Append($"   说明: {r.Description}\n");
            sb.Append('\n');
        }

        sb.Append("（取完整代码块用 MapSlice spec=文件:行号）");
        return sb.ToString();
    }

    /* ---------- 工具方法 ---------- */
}
