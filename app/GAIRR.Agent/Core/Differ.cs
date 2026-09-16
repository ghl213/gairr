using System;
using System.Collections.Generic;
using System.Text;

namespace GAIRR.Core;

/// <summary>行级 LCS diff 生成器：供 Edit 工具对比 old/new 替换段，产出带 +/- 前缀的 diff 文本（工具卡渲染 + 回喂模型自审）。
/// 段超大时退化为"整段删 + 整段增"，避免 DP 内存爆炸。</summary>
public static class Differ
{
    /// <summary>diff 段起始标记（UI/摘要/持久化解析共用）</summary>
    public const string Marker = "【diff】";

    /// <summary>old+new 总行数超过该值时放弃 LCS，退化为全删全增</summary>
    const int MaxLines = 500;

    /// <summary>生成 diff 文本：首行为统计头「【diff】行 start-end（+adds -dels）」，正文每行以 ' '(保留)/'-'(删)/'+'(增) 开头</summary>
    public static string Make(string oldText, string newText, int startLine, out int adds, out int dels)
    {
        var a = SplitLines(oldText);
        var b = SplitLines(newText);
        adds = 0; dels = 0;

        List<(char op, string line)> ops;
        if (a.Length + b.Length > MaxLines)
        {
            ops = new List<(char, string)>();
            foreach (var l in a) { ops.Add(('-', l)); dels++; }
            foreach (var l in b) { ops.Add(('+', l)); adds++; }
        }
        else
        {
            ops = Lcs(a, b);
            foreach (var (op, _) in ops) { if (op == '+') adds++; else if (op == '-') dels++; }
        }

        var endLine = startLine + Math.Max(a.Length - 1, 0);
        var sb = new StringBuilder();
        sb.Append(Marker).Append("行 ").Append(startLine).Append('-').Append(endLine)
          .Append("（+").Append(adds).Append(" -").Append(dels).Append("）\n");
        foreach (var (op, line) in ops)
            sb.Append(op).Append(line).Append('\n');
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>从工具结果文本中截取 diff 段（去掉其后的 [自动验证] 等追加内容）；无标记时原样返回</summary>
    public static string ExtractSection(string result)
    {
        var i = result.IndexOf(Marker, StringComparison.Ordinal);
        if (i < 0) return result;
        var section = result[i..];
        var v = section.IndexOf("[自动验证]", StringComparison.Ordinal);
        if (v >= 0) section = section[..v];
        return section.TrimEnd();
    }

    static string[] SplitLines(string s) =>
        s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    /// <summary>LCS 动态规划回溯：产出 ' '(保留)/'-'(删)/'+'(增) 操作序列（经典最短编辑脚本）</summary>
    static List<(char op, string line)> Lcs(string[] a, string[] b)
    {
        int n = a.Length, m = b.Length;
        var dp = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                dp[i, j] = a[i] == b[j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);

        var ops = new List<(char, string)>();
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[x] == b[y]) { ops.Add((' ', a[x])); x++; y++; }
            else if (dp[x + 1, y] >= dp[x, y + 1]) { ops.Add(('-', a[x])); x++; }
            else { ops.Add(('+', b[y])); y++; }
        }
        while (x < n) { ops.Add(('-', a[x])); x++; }
        while (y < m) { ops.Add(('+', b[y])); y++; }
        return ops;
    }
}
