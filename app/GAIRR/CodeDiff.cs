// 会话改动对比：把「改前基线文本 vs 当前文本」算成统一 diff（unified diff）文本，供代码查看器复用
// git 提交详情同款的行号状态机与着色渲染（+ 绿底 / - 红底 / @@ 区块头 / 上下文灰）。
// 之所以本地算而不走 git：会话改动的基线来自 back/ 写前备份（尚未提交），git 里没有这份「改前」。
// 算法 = 首尾公共行裁剪 + 中段 LCS 动态规划（行文本先哈希成整数 id 再比，避免大文件逐字符比较）；
// 中段规模超上限时退化为「整段删 + 整段增」——结果仍正确，只是差异定位不够精细。
using System;
using System.Collections.Generic;
using System.Text;

namespace GAIRR;

/// <summary>行级 diff 与统一 diff 文本生成（纯函数，无 IO：文本由调用方读好后传入）</summary>
public static class CodeDiff
{
    /// <summary>LCS 动态规划单元格上限：中段行数乘积超过即退化为整段替换（约 16MB int 数组，防大文件卡顿/爆内存）</summary>
    const int DpCellLimit = 4_000_000;

    /// <summary>
    /// 按行切分并统一换行符；去掉文件尾换行产生的末尾空串（与「文件行数」口径一致，
    /// 也避免仅行尾换行差异被算成一增一删的噪声）。
    /// </summary>
    public static string[] Split(string? text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var n = lines.Length;
        while (n > 1 && lines[n - 1].Length == 0) n--;
        if (n == lines.Length) return lines;
        var res = new string[n];
        Array.Copy(lines, res, n);
        return res;
    }

    /// <summary>
    /// 生成统一 diff 文本（含 ---/+++ 头与 @@ 区块头），并回传增/删行数供头部提示文案使用。
    /// 两侧内容一致时返回空串。ctx = 每处改动上下保留的上下文行数（git 默认 3）。
    /// </summary>
    public static string Unified(string? oldText, string? newText, out int added, out int removed, int ctx = 3)
    {
        added = removed = 0;
        var seq = DiffSeq(Split(oldText), Split(newText));
        if (seq.Count == 0) return "";
        // 预算每行的旧/新文件行号前缀和（区块头 @@ -a,b +c,d @@ 由此差值直接得出，无需二次遍历）
        var oldNo = new int[seq.Count + 1];
        var newNo = new int[seq.Count + 1];
        var chg = new List<int>();
        int o = 0, nw = 0;
        for (int i = 0; i < seq.Count; i++)
        {
            oldNo[i] = o; newNo[i] = nw;
            switch (seq[i].k)
            {
                case '=': o++; nw++; break;
                case '-': o++; removed++; chg.Add(i); break;
                default: nw++; added++; chg.Add(i); break;
            }
        }
        oldNo[seq.Count] = o; newNo[seq.Count] = nw;
        if (chg.Count == 0) { added = removed = 0; return ""; }

        var sb = new StringBuilder();
        sb.Append("--- 改前（基线）\n+++ 改后（当前）\n");
        var hi = 0;   // 已输出到的行下标：保证相邻区块不重叠
        for (int g = 0; g < chg.Count;)
        {
            var first = chg[g];
            var last = first;
            // 相邻改动间隔不超过 2*ctx 则并入同一区块（否则两块之间只剩纯上下文，git 同样合并）
            while (g + 1 < chg.Count && chg[g + 1] - last <= ctx * 2) { g++; last = chg[g]; }
            g++;
            var lo = Math.Max(hi, first - ctx);
            var to = Math.Min(seq.Count - 1, last + ctx);
            var oldLen = oldNo[to + 1] - oldNo[lo];
            var newLen = newNo[to + 1] - newNo[lo];
            // 空侧行号按 git 约定取「前一行号」（长度为 0 时起始号不 +1）
            sb.Append("@@ -").Append(oldLen == 0 ? oldNo[lo] : oldNo[lo] + 1).Append(',').Append(oldLen)
              .Append(" +").Append(newLen == 0 ? newNo[lo] : newNo[lo] + 1).Append(',').Append(newLen)
              .Append(" @@\n");
            for (int i = lo; i <= to; i++)
                sb.Append(seq[i].k == '=' ? " " : seq[i].k.ToString()).Append(seq[i].s).Append('\n');
            hi = to + 1;
        }
        return sb.ToString();
    }

    /// <summary>全文件行级 diff 序列：'=' 上下文 / '-' 改前独有 / '+' 改后独有（首尾公共行裁剪后只对中段跑 LCS）</summary>
    static List<(char k, string s)> DiffSeq(string[] a, string[] b)
    {
        var res = new List<(char, string)>();
        int n = a.Length, m = b.Length, p = 0;
        while (p < n && p < m && a[p] == b[p]) p++;
        int ea = n, eb = m;
        while (ea > p && eb > p && a[ea - 1] == b[eb - 1]) { ea--; eb--; }
        for (int i = 0; i < p; i++) res.Add(('=', a[i]));
        res.AddRange(Lcs(a, p, ea, b, p, eb));
        for (int i = ea; i < n; i++) res.Add(('=', a[i]));   // 尾段两侧相同，取改前侧文本即可
        return res;
    }

    /// <summary>中段 LCS diff：a[a0..a1) 与 b[b0..b1) 比对；单侧为空直接全增/全删，规模超限退化为整段替换</summary>
    static List<(char k, string s)> Lcs(string[] a, int a0, int a1, string[] b, int b0, int b1)
    {
        var res = new List<(char, string)>();
        int n = a1 - a0, m = b1 - b0;
        if (n == 0 && m == 0) return res;
        if (n == 0) { for (int j = 0; j < m; j++) res.Add(('+', b[b0 + j])); return res; }
        if (m == 0) { for (int i = 0; i < n; i++) res.Add(('-', a[a0 + i])); return res; }
        if ((long)n * m > DpCellLimit)
        {
            for (int i = 0; i < n; i++) res.Add(('-', a[a0 + i]));
            for (int j = 0; j < m; j++) res.Add(('+', b[b0 + j]));
            return res;
        }
        // 行文本 → 整数 id（同 id 即同内容）：DP 内层只比整数，比逐字符比字符串快一个量级
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var ai = new int[n];
        var bi = new int[m];
        for (int i = 0; i < n; i++) ai[i] = IdOf(ids, a[a0 + i]);
        for (int j = 0; j < m; j++) bi[j] = IdOf(ids, b[b0 + j]);
        var w = m + 1;
        var dp = new int[(n + 1) * w];
        for (int i = n - 1; i >= 0; i--)
        {
            var row = i * w;
            var next = row + w;
            for (int j = m - 1; j >= 0; j--)
                dp[row + j] = ai[i] == bi[j] ? dp[next + j + 1] + 1 : Math.Max(dp[next + j], dp[row + j + 1]);
        }
        // 回溯：相等走对角线；否则取 LCS 更长的一侧（相等优先删，与 git 观感一致）
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (ai[x] == bi[y]) { res.Add(('=', a[a0 + x])); x++; y++; }
            else if (dp[(x + 1) * w + y] >= dp[x * w + y + 1]) { res.Add(('-', a[a0 + x])); x++; }
            else { res.Add(('+', b[b0 + y])); y++; }
        }
        while (x < n) { res.Add(('-', a[a0 + x])); x++; }
        while (y < m) { res.Add(('+', b[b0 + y])); y++; }
        return res;
    }

    /// <summary>取/建行文本的整数 id（同字典内同文本必得同 id）</summary>
    static int IdOf(Dictionary<string, int> ids, string s)
    {
        if (ids.TryGetValue(s, out var v)) return v;
        v = ids.Count;
        ids[s] = v;
        return v;
    }
}
