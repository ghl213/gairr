using System.Text;

namespace GAIRR.Core;

/// <summary>BM25 轻量相关性打分器：对候选集（符号/注释标签/摘要文本）按查询词频排序。
/// 中文按 2-gram 滑动匹配，英文整词；纯内存无外部依赖。</summary>
public static class Bm25Scorer
{
    const double K1 = 1.2;
    const double B = 0.75;

    /// <summary>切词：连续中文为整串，连续字母数字为整词（SmartSearch 原逻辑外置共用）</summary>
    public static List<string> SplitWords(string text)
    {
        var result = new List<string>();
        var sb = new StringBuilder();        foreach (var ch in text)
        {
            if (IsCjk(ch))
            {
                if (sb.Length > 0 && IsCjk(sb[0])) sb.Append(ch);
                else { if (sb.Length > 0) result.Add(sb.ToString().Trim()); sb.Clear(); sb.Append(ch); }
            }
            else if (char.IsLetterOrDigit(ch))
            {
                if (sb.Length > 0 && !IsCjk(sb[0])) sb.Append(ch);
                else { if (sb.Length > 0) result.Add(sb.ToString().Trim()); sb.Clear(); sb.Append(ch); }
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

    static bool IsCjk(char ch) => ch >= '\u4e00' && ch <= '\u9fff';
    /// <summary>对候选文档打分：返回 key → 相关分。文档集合即统计域（N/df/avgLen 取自候选集），无命中词恒 0。</summary>
    public static Dictionary<string, double> Rank(string query, IEnumerable<KeyValuePair<string, string>> docs)
    {
        var pairs = docs.Where(kv => kv.Value.Length > 0).ToList();
        var result = pairs.ToDictionary(kv => kv.Key, _ => 0.0);
        if (pairs.Count == 0) return result;

        var words = SplitWords(query);
        if (words.Count == 0) return result;

        // 查询词到匹配单元：中文拆 2-gram（措辞差异时仍能命中），英文整词小写
        var grams = new List<string>();
        foreach (var w in words)
        {
            if (w.Length == 0) continue;
            if (IsCjk(w[0]))
            {
                if (w.Length <= 2) grams.Add(w);
                else for (int i = 0; i < w.Length - 1; i++) grams.Add(w.Substring(i, 2));
            }
            else if (w.Length > 2) grams.Add(w.ToLowerInvariant());
        }
        var uniq = grams.Distinct().ToList();
        if (uniq.Count == 0) return result;
        // 文档长度（切词数）与文档频率 df
        var dl = new int[pairs.Count];
        var df = new Dictionary<string, int>(uniq.Count);
        for (int i = 0; i < pairs.Count; i++)
        {
            var docText = pairs[i].Value;
            dl[i] = Math.Max(1, SplitWords(docText).Count);
            foreach (var g in uniq)
                if (docText.Contains(g, StringComparison.OrdinalIgnoreCase))
                    df[g] = df.GetValueOrDefault(g) + 1;
        }
        double avg = dl.Length > 0 ? dl.Average() : 1.0;
        double n = pairs.Count;

        for (int i = 0; i < pairs.Count; i++)
        {
            var kv = pairs[i];
            double norm = K1 * (1 - B + B * dl[i] / avg);
            double score = 0;
            foreach (var g in uniq)
            {
                int tf = 0, from = 0;
                while ((from = kv.Value.IndexOf(g, from, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    tf++;
                    from += Math.Max(1, g.Length);
                }
                if (tf == 0) continue;
                double d = df.GetValueOrDefault(g);
                double idf = Math.Log(1 + (n - d + 0.5) / (d + 0.5));
                score += tf * idf / (tf + norm);
            }
            result[kv.Key] = score;
        }
        return result;
    }
}
