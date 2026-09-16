using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using GAIRR.Core;

/// <summary>A/B 评测：Enabled=1（含向量层）vs Enabled=0（纯四层），
/// 12 条查询各跑 3 次取 P50/P95，输出 Top-1/Top-5 命中与结果明细。</summary>
public static class EvalA
{
    /// <summary>评测查询集（12 条，覆盖危险命令/备份/索引/压缩/断点续行/流式/调用链/切片/重试/确认/超时/补注）</summary>
    static readonly string[] Queries =
    {
        "危险命令",
        "删除文件前 先让用户确认",
        "没有注释的代码 自动补说明",
        "接口失败 自动重试",
        "等待超时 就取消",
        "历史太长 压缩成摘要",
        "关掉程序 下次继续聊",
        "代码改动 索引自动更新",
        "查一个函数 在哪里被调用",
        "长文件 切成片 喂给模型",
        "改代码前 先备份",
        "回复效果 一字一字出现"
    };

    /// <summary>执行 A/B 评测并打印报告</summary>
    public static void Run(string projectRoot)
    {
        var cfg = new AppConfig();
        cfg.ProjectRoot = projectRoot;

        var sb = new StringBuilder();
        sb.AppendLine("=== A/B 评测报告 ===");
        sb.AppendLine($"项目: {projectRoot}");
        sb.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        // A 组：Enabled=1（含向量层）
        sb.AppendLine("--- A 组：Embed=1（含向量层）---");
        RunGroup(cfg, sb, true);

        // B 组：Enabled=0（纯四层）
        sb.AppendLine();
        sb.AppendLine("--- B 组：Embed=0（纯四层）---");
        RunGroup(cfg, sb, false);

        var report = sb.ToString();
        Console.WriteLine(report);

        // 写入文件
        var outPath = Path.Combine(AppContext.BaseDirectory, "eval-ab.txt");
        File.WriteAllText(outPath, report, new UTF8Encoding(false));
        Console.WriteLine($"\n报告已写入: {outPath}");
    }

    /// <summary>跑一组评测（指定 Embed 开关），输出每条查询的 Top-5 结果与延迟</summary>
    static void RunGroup(AppConfig cfg, StringBuilder sb, bool embedOn)
    {
        SystemCfg.Embed = embedOn;

        // 确保索引已建
        if (embedOn)
        {
            var n = EmbedIndexBuilder.Update(cfg);
            sb.AppendLine($"[索引状态] 增量更新 {n} 条变更，当前 {GetIndexCount(cfg)} 条");
            sb.AppendLine();
        }

        foreach (var q in Queries)
        {
            var times = new long[3];
            string result = "";
            for (int i = 0; i < 3; i++)
            {
                var sw = Stopwatch.StartNew();
                result = SmartSearch.Run(cfg, q, 5);
                sw.Stop();
                times[i] = sw.ElapsedMilliseconds;
            }
            Array.Sort(times);
            var p50 = times[1];
            var p95 = times[2];

            sb.AppendLine($"Q: {q}");
            sb.AppendLine($"  延迟: P50={p50}ms P95={p95}ms");
            sb.AppendLine($"  结果:");
            AppendTopHits(sb, result);
            sb.AppendLine();
        }
    }

    /// <summary>从 SmartSearch 输出文本中提取 Top 命中行</summary>
    static void AppendTopHits(StringBuilder sb, string result)
    {
        var lines = result.Split('\n');
        int count = 0;
        foreach (var line in lines)
        {
            var t = line.TrimStart();
            // 匹配 "1. [tag] ..." 或 "1. [vector] ..." 格式
            if (t.Length > 3 && char.IsDigit(t[0]) && t.Contains("] ") && !t.StartsWith("智能搜索"))
            {
                count++;
                sb.AppendLine($"    {t}");
                // 下一行是说明
                var idx = Array.IndexOf(lines, line);
                if (idx + 1 < lines.Length)
                {
                    var next = lines[idx + 1].TrimStart();
                    if (next.StartsWith("说明:") || next.StartsWith("位置:"))
                        sb.AppendLine($"      {next}");
                }
                if (count >= 5) break;
            }
        }
        if (count == 0) sb.AppendLine("    (无命中)");
    }

    /// <summary>读取当前索引条目数</summary>
    static int GetIndexCount(AppConfig cfg)
    {
        var idx = Path.Combine(cfg.ProjectRoot, ".gairr", EmbedIndex.FileName);
        if (!File.Exists(idx)) return 0;
        var ei = new EmbedIndex(idx);
        return ei.Load() ? ei.Count : 0;
    }
}
