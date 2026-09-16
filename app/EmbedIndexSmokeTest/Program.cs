using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using GAIRR.Core;

/// <summary>向量检索 A/B 评测：Enabled=0（纯四层）vs Enabled=1（四层+向量），
/// 输出每条查询 Top-1/Top-5 命中、向量层是否出现、cos 值、查询延迟 P50/P95。
/// 用法：dotnet run -- ab    （跑 A/B 对照）
///       dotnet run -- smoke （单条冒烟，默认）</summary>
class Program
{
    static int Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0] : "smoke";
        if (mode == "ab") return RunAb();
        if (mode == "vec") return RunVec();
        if (mode == "compact") return RunCompact();
        return RunSmoke();
    }

    /// <summary>积累库无损压缩验证：打印压缩前后条数/体积与耗时，确认 Compact 结果符合预期（compact 模式）</summary>
    static int RunCompact()
    {
        var cfg = Cfg();
        var p = Path.Combine(cfg.ProjectRoot, ".gairr", "tag-refs.jsonl");
        if (!File.Exists(p)) { Console.WriteLine("FAIL: 无积累库文件 " + p); return 1; }

        var before = File.ReadAllLines(p).Count(l => !string.IsNullOrWhiteSpace(l));
        var sizeBefore = new FileInfo(p).Length;
        var sw = Stopwatch.StartNew();
        var msg = TagRefAccumulator.CompactNow(cfg);
        sw.Stop();

        var after = File.ReadAllLines(p).Count(l => !string.IsNullOrWhiteSpace(l));
        Console.WriteLine($"压缩前 {before} 条 / {sizeBefore:N0} B  →  压缩后 {after} 条 / {new FileInfo(p).Length:N0} B");
        Console.WriteLine($"耗时 {sw.ElapsedMilliseconds}ms");
        Console.WriteLine(msg.Length == 0 ? "（无需压缩）" : msg);
        return 0;
    }

    /// <summary>中英文向量直查：绕过五层合并，直接看向量层对各查询的 cos 分布与过阈值条数；
    /// 末尾再走完整 SmartSearch（Embed=1）确认 [vector] 席位实际出现情况。</summary>
    static int RunVec()
    {
        var cfg = Cfg();
        var idxPath = Path.Combine(cfg.ProjectRoot, ".gairr", EmbedIndex.FileName);
        if (!File.Exists(idxPath)) { Console.WriteLine("FAIL: 无索引文件"); return 1; }
        var idx = new EmbedIndex(idxPath);
        if (!idx.Load()) { Console.WriteLine("FAIL: 索引加载失败"); return 1; }
        Console.WriteLine($"索引 {idx.Count} 条，dim={EmbedIndex.ModelVersion}");

        var engine = LocalEmbeddingEngine.Shared;
        if (!engine.IsAvailable || !engine.EnsureReady()) { Console.WriteLine("FAIL: 引擎不可用"); return 1; }
        Console.WriteLine("引擎 OK\n");

        var queries = new (string Q, string Note)[]
        {
            ("钉钉消息通知", "中文"),
            ("接口失败自动重试", "中文"),
            ("危险命令拦截", "中文"),
            ("dingtalk message notification", "英文"),
            ("auto retry on api failure", "英文"),
            ("send dingtalk alert", "英文"),
        };

        foreach (var (q, note) in queries)
        {
            var qv = engine.Embed(q);
            if (qv == null) { Console.WriteLine($"[{note}] 「{q}」 embed 失败"); continue; }
            var tops = idx.Search(qv, topK: 6);
            var over = tops.Count(t => t.Item2 >= 0.65f);
            Console.WriteLine($"[{note}] 「{q}」 Top6（过阈值≥0.65: {over} 条）:");
            foreach (var (k, cos) in tops)
                Console.WriteLine($"    cos={cos:F3}  {k}");
            Console.WriteLine();
        }

        // 走完整 SmartSearch（含向量层），确认 [vector] 席位
        SystemCfg.Embed = true;
        foreach (var q in new[] { "钉钉消息通知", "dingtalk message notification" })
        {
            var r = SmartSearch.Run(cfg, q, 5);
            var vecLines = r.Split('\n').Where(l => l.Contains("[vector]")).ToList();
            Console.WriteLine($"[SmartSearch] 「{q}」 vector席位={vecLines.Count}");
            foreach (var l in vecLines) Console.WriteLine("   " + l.Trim());
        }
        return 0;
    }

    static AppConfig Cfg()
    {
        var cfg = new AppConfig();
        cfg.ProjectRoot = @"D:\work\gairr";
        return cfg;
    }

    static int RunSmoke()
    {
        var cfg = Cfg();
        var root = cfg.ProjectRoot;
        Console.WriteLine("=== 向量索引冒烟测试 ===");
        Console.WriteLine($"Model: {LocalEmbeddingEngine.ModelPath} (exists={File.Exists(LocalEmbeddingEngine.ModelPath)})");

        using var engine = new LocalEmbeddingEngine();
        if (!engine.IsAvailable) { Console.WriteLine("FAIL: 模型/词表不存在"); return 1; }
        if (!engine.EnsureReady()) { Console.WriteLine("FAIL: 引擎初始化失败"); return 1; }
        var vec = engine.Embed("接口失败自动重试");
        Console.WriteLine($"引擎 OK，向量维度={vec?.Length}");

        // 测 Shared 引擎冷加载耗时
        var swLoad = Stopwatch.StartNew();
        var shared = LocalEmbeddingEngine.Shared;
        shared.EnsureReady();
        Console.WriteLine($"[diag] Shared 引擎冷加载 {swLoad.ElapsedMilliseconds}ms");

        var n = EmbedIndexBuilder.Rebuild(cfg);
        Console.WriteLine($"索引构建 {n} 条 -> {Path.Combine(root, ".gairr", EmbedIndex.FileName)}");

        // 直查向量 TopK：确认 cos 分布（判断 [vector] 缺席是被挤出还是未过阈值）
        var idx = new EmbedIndex(Path.Combine(root, ".gairr", EmbedIndex.FileName));
        if (idx.Load())
        {
            var qv = shared.Embed("接口失败自动重试");
            if (qv != null)
            {
                var tops = idx.Search(qv, topK: 6);
                Console.WriteLine($"[diag] 向量 Top6：");
                foreach (var (k, cos) in tops) Console.WriteLine($"    cos={cos:F3}  {k}");
            }
        }

        // 逐层计时（反射调 internal 层方法，精确名）：定位 20s 延迟来源
        Console.WriteLine("\n[diag] 逐层计时：");
        TimeLayer(cfg, "SearchAccumulated");
        TimeLayer(cfg, "SearchSymbolTags");
        TimeLayer(cfg, "SearchSymbols", warmup: true);
        TimeLayer(cfg, "SearchMapSummaries");
        TimeLayer(cfg, "SearchEmbedding", warmup: true);

        // 连查 2 次 SmartSearch：第一次含索引缓存建立，第二次应显著更快
        for (int i = 1; i <= 2; i++)
        {
            var sw = Stopwatch.StartNew();
            var r = SmartSearch.Run(cfg, "接口失败自动重试", 5);
            sw.Stop();
            Console.WriteLine($"\n=== SmartSearch #{i}「接口失败自动重试」 {sw.ElapsedMilliseconds}ms ===");
            if (i == 1) Console.WriteLine(r);
            var vecHits = r.Split('\n').Count(l => l.Contains("[vector]"));
            Console.WriteLine($"    [vector] 命中席位数: {vecHits}");
        }
        return 0;
    }

    /// <summary>反射调 SmartSearch 的 internal 层方法计时（layer=accumulated/symbolTags/symbols/mapSummaries/embedding）</summary>
    static void TimeLayer(AppConfig cfg, string layer, bool warmup = false)
    {
        const string q = "接口失败自动重试";
        var m = typeof(GAIRR.Core.SmartSearch).GetMethod(
            layer,
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        if (m == null) { Console.WriteLine($"    {layer}: 找不到方法"); return; }
        var args = m.GetParameters().Length == 3
            ? new object[] { cfg, q, new List<string>() }
            : new object[] { cfg, q };
        if (warmup) m.Invoke(null, args); // 预热（引擎冷加载等一次性开销）
        var sw = Stopwatch.StartNew();
        var res = (List<GAIRR.Core.SmartSearch.Result>?)m.Invoke(null, args);
        sw.Stop();
        Console.WriteLine($"    {layer}: {sw.ElapsedMilliseconds}ms ({res?.Count ?? 0} 条)");
    }

    static int RunAb()
    {
        var cfg = Cfg();

        // 确保索引已建（Enabled=1 才用得到）
        if (EmbedIndexBuilder.NeedsRebuild(cfg))
        {
            var n = EmbedIndexBuilder.Rebuild(cfg);
            Console.WriteLine($"[prep] 索引重建 {n} 条\n");
        }

        var baseQ = ReadQueries(@"D:\work\gairr\temp\_eval_q.txt");      // 12 条功能查询
        var rewriteQ = ReadQueries(@"D:\work\gairr\temp\_eval_q2.txt"); // 5 条语义改写
        if (baseQ.Count == 0) { Console.WriteLine("FAIL: 读不到查询集"); return 1; }

        Console.WriteLine($"[查询集] 功能={baseQ.Count} 改写={rewriteQ.Count}\n");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== 向量检索 A/B 评测（Enabled=0 纯四层 vs Enabled=1 四层+向量）===");
        sb.AppendLine($"功能查询 {baseQ.Count} 条 + 语义改写 {rewriteQ.Count} 条\n");

        var offStats = new EvalStats();
        var onStats = new EvalStats();

        // A 组：Enabled=0
        SystemCfg.Embed = false;
        foreach (var q in baseQ)
        {
            var (top, hit, embCount, swMs) = RunOnce(cfg, q, sb, "OFF");
            offStats.Add(top, hit, embCount, swMs);
        }
        foreach (var q in rewriteQ)
        {
            var (top, hit, embCount, swMs) = RunOnce(cfg, q, sb, "OFF-rewrite");
            offStats.Add(top, hit, embCount, swMs);
        }

        // B 组：Enabled=1
        SystemCfg.Embed = true;
        foreach (var q in baseQ)
        {
            var (top, hit, embCount, swMs) = RunOnce(cfg, q, sb, "ON");
            onStats.Add(top, hit, embCount, swMs);
        }
        foreach (var q in rewriteQ)
        {
            var (top, hit, embCount, swMs) = RunOnce(cfg, q, sb, "ON-rewrite");
            onStats.Add(top, hit, embCount, swMs);
        }

        // 汇总
        sb.AppendLine("\n=== 汇总 ===");
        sb.AppendLine(string.Format("{0,-16} {1,12} {2,12}", "指标", "Enabled=0", "Enabled=1"));
        sb.AppendLine(string.Format("{0,-16} {1,10}% {2,10}%", "Top-1命中", offStats.Top1, onStats.Top1));
        sb.AppendLine(string.Format("{0,-16} {1,10}% {2,10}%", "Top-5命中", offStats.Top5, onStats.Top5));
        sb.AppendLine(string.Format("{0,-16} {1,10} {2,10}", "向量层出现", offStats.EmbTimes, onStats.EmbTimes));
        sb.AppendLine(string.Format("{0,-16} {1,10} {2,10}", "向量席位总数", offStats.EmbSeats, onStats.EmbSeats));
        sb.AppendLine(string.Format("{0,-16} {1,10} {2,10}", "延迟P50(ms)", offStats.P50, onStats.P50));
        sb.AppendLine(string.Format("{0,-16} {1,10} {2,10}", "延迟P95(ms)", offStats.P95, onStats.P95));
        sb.AppendLine(string.Format("{0,-16} {1,10} {2,10}", "平均(ms)", offStats.Avg, onStats.Avg));

        var text = sb.ToString();
        Console.WriteLine(text);
        File.WriteAllText(@"D:\work\gairr\temp\_embed_ab_result.txt", text);
        Console.WriteLine("\n[saved] temp/_embed_ab_result.txt");

        // 结论
        var noRegress = onStats.Top1 >= offStats.Top1 - 5 && onStats.Top5 >= offStats.Top5 - 5;
        Console.WriteLine(noRegress ? "[PASS] 不回退（Top-1/Top-5 未下降>5%）" : "[WARN] 出现回退，请检查阈值/席位");
        return 0;
    }

    static List<string> ReadQueries(string path)
    {
        var list = new List<string>();
        if (!File.Exists(path)) return list;
        foreach (var line in File.ReadAllLines(path))
        {
            var t = line.Trim();
            if (t.Length > 0) list.Add(t);
        }
        return list;
    }

    static (List<string> top, int embCount, bool hit, int swMs) RunOnce(AppConfig cfg, string q, System.Text.StringBuilder sb, string tag)
    {
        var sw = Stopwatch.StartNew();
        var r = SmartSearch.Run(cfg, q, 5);
        sw.Stop();

        // 解析 SmartSearch 输出：行首 "N. [type] Symbol (path)"
        var top = new List<string>();
        var embCount = 0;
        foreach (var line in r.Split('\n'))
        {
            var l = line.TrimStart();
            if (!System.Text.RegularExpressions.Regex.IsMatch(l, @"^\d+\. \[")) continue;
            var m = System.Text.RegularExpressions.Regex.Match(l, @"^\d+\. \[(\w+)\] (\S+)");
            if (m.Success)
            {
                top.Add(m.Groups[1].Value + ":" + m.Groups[2].Value);
                if (m.Groups[1].Value == "vector") embCount++;
            }
        }
        var hit = top.Count > 0;

        sb.AppendLine($"[{tag}] 「{q}」 {sw.ElapsedMilliseconds}ms  top{Math.Min(top.Count,3)}={string.Join(", ", top.Take(3))}  向量席={embCount}");
        return (top, embCount, hit, (int)sw.ElapsedMilliseconds);
    }

    sealed class EvalStats
    {
        public int Count;
        public int Top1Hits;
        public int Top5Hits;
        public int EmbTimes; // 向量层出现的查询数
        public int EmbSeats; // 向量席位总数
        public readonly List<long> Ms = new();

        public void Add(List<string> top, int embCount, bool hit, int ms)
        {
            Count++;
            if (top.Count >= 1) Top1Hits++;
            if (top.Count >= 5) Top5Hits++;
            if (embCount > 0) EmbTimes++;
            EmbSeats += embCount;
            Ms.Add(ms);
        }

        public int Top1 => Count == 0 ? 0 : (int)Math.Round(Top1Hits * 100.0 / Count);
        public int Top5 => Count == 0 ? 0 : (int)Math.Round(Top5Hits * 100.0 / Count);
        public long P50 => Percentile(50);
        public long P95 => Percentile(95);
        public int Avg => Ms.Count == 0 ? 0 : (int)(Ms.Average() + 0.5);
        long Percentile(int p)
        {
            if (Ms.Count == 0) return 0;
            var s = Ms.OrderBy(x => x).ToList();
            return s[Math.Min(s.Count - 1, (int)(s.Count * p / 100.0))];
        }
    }
}
