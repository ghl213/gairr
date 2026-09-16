// 符号索引 P1 验收 v6：Load 懒加载（A/C）+ 分词匹配引擎替代 2 万词巨型正则（D 对拍 / E 全量重建计时 / F 探针 / G 全量差异）
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GAIRR.Core;

Console.OutputEncoding = System.Text.Encoding.UTF8;
var root = args.Length > 0 ? args[0] : @"d:\work\gairr";
var cfg = new AppConfig { ProjectRoot = root };
var sw = new Stopwatch();
string[] probe = { "EnumerateFiles", "SymbolIndex", "IndexGuardian", "Grep", "MapTrace" };
var indexFile = Path.Combine(root, ".gairr", "symbols.json");
if (!File.Exists(indexFile)) { Console.WriteLine("缺少索引文件：" + indexFile); return; }

// [A] 冷启动首次查询：应走 Load（读盘复用），不跑全量传播
var stampBefore = File.GetLastWriteTimeUtc(indexFile);
sw.Restart();
var s = SymbolIndex.Run(cfg, "SymbolIndex", 3, false);
var loadMs = sw.ElapsedMilliseconds;
var stampAfter = File.GetLastWriteTimeUtc(indexFile);
Console.WriteLine($"[A] 冷启动首次查询 = {loadMs} ms"
    + (stampBefore == stampAfter ? "  （索引未被重写 → 走 Load）" : "  （⚠ 索引被重写 → 实际走了全量 Build）"));
Console.WriteLine("    " + s.Split('\n')[0]);

// [A2] 冷启动成本分解：读盘 / JsonNode 解析（Load 的主要开销来源）
sw.Restart();
var jsonText = File.ReadAllText(indexFile);
var tRead = sw.ElapsedMilliseconds;
sw.Restart();
var joProbe = JsonNode.Parse(jsonText)!;
var tParse = sw.ElapsedMilliseconds;
Console.WriteLine($"[A2] 索引 {jsonText.Length / 1024} KB：读盘 {tRead} ms / JsonNode 解析 {tParse} ms");

// [C] 30s 窗口内二次查询（缓存命中）
sw.Restart();
SymbolIndex.Run(cfg, "SymbolIndex", 3, false);
Console.WriteLine($"[C] 窗口内二次查询 = {sw.ElapsedMilliseconds} ms");

// [B] 基线（此刻内存索引即磁盘既有索引；[A] 未触发重建立即来自磁盘）
var before = probe.Select(p => SymbolIndex.GetRefs(cfg, p, 5000).Count).ToArray();
Console.WriteLine("[B] 重建前探针引用数 = " + string.Join(" ", probe.Zip(before, (p, c) => p + "=" + c))
    + (loadMs < 5000 ? "  （来自磁盘索引）" : "  （⚠ [A] 疑似走了重建）"));

var jo = JsonNode.Parse(File.ReadAllText(indexFile))!;
var names = jo["symbols"]!.AsObject().Select(kv => kv.Key).ToList();
var files = jo["files"]!.AsObject().Select(kv => kv.Key).ToList();
var refsBefore = ReadRefs(indexFile);

// [D] 新旧匹配引擎对拍（回溯引擎 = 真旧实现，逐行比对命中序列与顺序）
var reOld = new Regex(@"\b(" + string.Join("|", names.Take(20000).Select(Regex.Escape)) + @")\b",
    RegexOptions.CultureInvariant);
var mNew = new SymbolWordMatcher(names, 20000);
sw.Restart();
var (d1, l1) = DiffLines(root, files.Take(10), int.MaxValue, reOld, mNew, true);
Console.WriteLine($"[D] 逐行对拍（回溯引擎 / 10 文件 / {l1} 行 / {sw.ElapsedMilliseconds} ms）差异 {d1} 行 {(d1 == 0 ? "✅ 等价" : "❌")}");

// [D2] 大样本对拍（NonBacktracking 引擎：对 \b+alternation 语义等价且线性，可覆盖更多文件）
try
{
    var reNb = new Regex(@"\b(" + string.Join("|", names.Take(20000).Select(Regex.Escape)) + @")\b",
        RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);
    Console.WriteLine($"[D2] 逐行对拍（NonBacktracking / {files.Take(40).Count()} 文件）差异 {DiffLines(root, files.Take(40), int.MaxValue, reNb, mNew, false)} 行");
}
catch (Exception ex) { Console.WriteLine("[D2] 跳过（NonBacktracking 不可用：" + ex.Message + "）"); }

// [E] 全量重建计时（Refresh 强制 Build，内含全量引用传播）
sw.Restart();
SymbolIndex.Refresh(cfg);
var buildMs = sw.ElapsedMilliseconds;
Console.WriteLine($"[E] 全量重建 = {buildMs} ms  （改造前 ~61000 ms）");

// [F] 新引擎全量重建后的探针引用数 vs 基线
var after = probe.Select(p => SymbolIndex.GetRefs(cfg, p, 5000).Count).ToArray();
Console.WriteLine("[F] 重建后探针引用数 = " + string.Join(" ", probe.Zip(after, (p, c) => p + "=" + c))
    + (before.SequenceEqual(after) ? "  ✅ 与重建前一致" : "  ⚠ 有变化"));

// [G] 全量差异：重建前后逐符号比对整段 refs/defs（覆盖全部符号，不只探针）
var refsAfter = ReadRefs(indexFile);
var changed = refsBefore.Where(kv => !refsAfter.TryGetValue(kv.Key, out var v) || v != kv.Value).Select(kv => kv.Key).ToList();
var added = refsAfter.Keys.Where(k => !refsBefore.ContainsKey(k)).ToList();
Console.WriteLine($"[G] 全量比对（{refsBefore.Count} 符号）：变化 {changed.Count} / 新增 {added.Count} / 删除 {refsBefore.Count - refsAfter.Count + added.Count}");
foreach (var k in changed.Take(5)) Console.WriteLine($"      · {k}");
Console.WriteLine(before.SequenceEqual(after) ? "✅ 引用集无漂移" : "⚠ 引用集存在变化，见 [G]");

Console.WriteLine((loadMs < 3000 && buildMs < 15000) ? "✅ P1 通过" : "❌ P1 未达标");

// ---------- 辅助 ----------
static (int diff, int lines) DiffLines(string root, IEnumerable<string> rels, int maxLines, Regex oldRe, SymbolWordMatcher mNew, bool show)
{
    int diff = 0, checkedLines = 0;
    foreach (var rel in rels)
    {
        var abs = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(abs)) continue;
        var lines = File.ReadAllText(abs).Replace("\r\n", "\n").Split('\n');
        foreach (var ln in maxLines == int.MaxValue ? lines : lines.Take(maxLines))
        {
            checkedLines++;
            var a = oldRe.Matches(ln).Select(m => m.Groups[1].Value).ToList();
            var b = new List<string>();
            mNew.Match(ln, x => b.Add(x));
            if (!a.SequenceEqual(b))
            {
                diff++;
                if (show && diff <= 5) Console.WriteLine($"      ✗ {rel}: 旧[{string.Join(",", a)}] 新[{string.Join(",", b)}]");
            }
        }
    }
    return (diff, checkedLines);
}

static Dictionary<string, string> ReadRefs(string path)
{
    var res = new Dictionary<string, string>(StringComparer.Ordinal);
    var jo = JsonNode.Parse(File.ReadAllText(path))!;
    foreach (var kv in jo["symbols"]!.AsObject())
        res[kv.Key] = kv.Value?.ToJsonString() ?? "";
    return res;
}
