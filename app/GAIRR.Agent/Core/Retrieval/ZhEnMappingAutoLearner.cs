using System.Diagnostics;

namespace GAIRR.Core;

/// <summary>
/// ZhEnMapping 自动学习器：从 tag-refs.jsonl 积累库提取高频"标签→符号"模式，
/// 自动补充到运行时学习缓存，并支持持久化到项目级配置。
/// 闲时触发，不阻塞主流程；每 10 分钟自动持久化一次。
/// </summary>
public static class ZhEnMappingAutoLearner
{
    const int MinOccurrence = 3;       // 最少出现次数才学习
    const double MinConfidence = 0.7;  // 最低平均置信度
    const int MaxLearnPerRun = 10;     // 单次最多学习条数（防膨胀）
    static readonly TimeSpan PersistInterval = TimeSpan.FromMinutes(10); // 自动持久化间隔

    static DateTime _lastPersist = DateTime.MinValue;
    static readonly object PersistSync = new();

    /// <summary>任务后触发：从积累库学习新映射，并按需自动持久化</summary>
    public static void LearnFromTask(AppConfig cfg)
    {
        if (!SystemCfg.EnrichTagRefs) return;
        try
        {
            var patterns = ExtractPatterns(cfg);
            if (patterns.Count > 0)
            {
                ZhEnMapping.LearnMany(patterns);
                Debug.WriteLine($"[ZhEnLearner] 从积累库学习 {patterns.Count} 条新映射");
            }

            // 自动持久化：每 10 分钟一次，避免频繁写盘
            TryAutoPersist(cfg);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ZhEnLearner] 学习或持久化失败: {ex.Message}");
        }
    }

    /// <summary>尝试自动持久化：距上次持久化超 10 分钟且学习缓存非空时执行</summary>
    static void TryAutoPersist(AppConfig cfg)
    {
        var now = DateTime.Now;
        if (now - _lastPersist < PersistInterval) return;

        lock (PersistSync)
        {
            if (now - _lastPersist < PersistInterval) return;
            ZhEnMapping.PersistToProject(cfg.ProjectRoot);
            _lastPersist = DateTime.Now;
            Debug.WriteLine("[ZhEnLearner] 自动持久化完成");
        }
    }

    /// <summary>从积累库提取高频一致的模式</summary>
    static Dictionary<string, string[]> ExtractPatterns(AppConfig cfg)
    {
        var result = new Dictionary<string, string[]>();
        var entries = TagRefAccumulator.LoadAll(cfg)
            .Where(e => !e.Invalidated && e.Refs.Count > 0)
            .ToList();

        if (entries.Count < MinOccurrence) return result;

        // 按标签分组统计
        var tagGroups = entries
            .SelectMany(e => e.Tags.Select(t => new
            {
                Tag = t,
                Refs = e.Refs,
                Task = e.Task
            }))
            .GroupBy(x => x.Tag, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= MinOccurrence)
            .ToList();

        foreach (var g in tagGroups)
        {
            var tag = g.Key;
            if (tag.Length < 2 || tag.Length > 20) continue;

            // 统计该标签下各符号的出现次数与平均置信度
            var symbolStats = g.SelectMany(x => x.Refs)
                .GroupBy(r => r.Symbol, StringComparer.OrdinalIgnoreCase)
                .Select(sg => new
                {
                    Symbol = sg.Key,
                    Count = sg.Count(),
                    AvgConf = sg.Average(r => r.Confidence),
                    FirstRef = sg.First()
                })
                .Where(s => s.Count >= 2 && s.AvgConf >= MinConfidence)
                .OrderByDescending(s => s.Count)
                .ThenByDescending(s => s.AvgConf)
                .ToList();

            if (symbolStats.Count == 0) continue;

            // 取 Top-3 符号
            var symbols = symbolStats.Take(3).Select(s => s.Symbol).ToArray();
            if (symbols.Length > 0)
                result[tag] = symbols;

            if (result.Count >= MaxLearnPerRun) break;
        }

        return result;
    }

    /// <summary>批量学习并持久化（闲时任务调用）</summary>
    public static async Task LearnAndPersistAsync(AppConfig cfg)
    {
        await Task.Run(() =>
        {
            LearnFromTask(cfg);
        });
    }
}
