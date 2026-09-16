// SymbolWordMatcher —— 符号名匹配器（SymbolIndex 引用传播专用）
// 用途：替代 "\b(sym1|sym2|…|sym20000)\b" 这种 2 万词巨型 alternation 正则。
// 原因：该正则在 6MB 文本上回溯极慢（实测全量引用传播一趟占 61s 重建的绝大部分开销），
//       且每次 new 都要付 Compiled 编译税 —— 单文件增量路径每个文件都重建一次，白白吃掉几百毫秒。
// 方案：分词扫描 + 哈希查表（线性 O(文本长度)，无回溯、构造零成本），
//       语义严格对齐旧正则：\b 只在完整词边界命中，并按 alternation 顺序做 leftmost-first 非重叠消费
//       （如文本含 "GAIRR.Agent"，且 GAIRR 在 names 中排在 GAIRR.Agent 之前时，两者都算命中 ——
//        先消费 GAIRR 这 5 个字符，再从 '.' 之后继续，与旧正则逐项一致）。
// 兜底：含非词字符（$ . - 空格等）的符号名无法整词切出，用短正则单独匹配后与整词命中合并排序。
using System.Text.RegularExpressions;

namespace GAIRR.Core;

/// <summary>符号名匹配器：线性分词查表替代巨型 alternation 正则（详见文件头说明）。</summary>
public sealed class SymbolWordMatcher
{
    /// <summary>候选命中：位置 / 长度 / 在 names 中的序号（决定同位置优先级）/ 符号名</summary>
    struct Cand
    {
        public int Pos;
        public int Len;
        public int Order;
        public string Name;
    }

    static readonly Comparison<Cand> ByPosThenOrder =
        (x, y) => x.Pos != y.Pos ? x.Pos - y.Pos : x.Order - y.Order;

    readonly Dictionary<string, int> _plain = new(StringComparer.Ordinal);  // 全词字符符号名 → alternation 序号
    readonly Dictionary<string, int> _odd = new(StringComparer.Ordinal);    // 含非词字符符号名 → alternation 序号
    readonly HashSet<int> _lens = new();                                    // 长度预筛：滤掉绝大多数无关词，少建字符串
    readonly Regex? _oddRe;                                                 // 含非词字符的符号名：短正则兜底
    readonly List<Cand> _buf = new(64);                                     // 候选缓冲（仅存在兜底符号名时使用）

    /// <summary>用符号名集合构建匹配器（按序取前 max 个，与旧 BuildWordRe 的 Take 口径一致）。</summary>
    public SymbolWordMatcher(IEnumerable<string> names, int max = int.MaxValue)
    {
        var oddNames = new List<string>();
        var seen = 0;
        foreach (var n in names)
        {
            if (seen >= max) break;
            seen++;
            if (string.IsNullOrEmpty(n)) continue;   // 旧正则可匹配空串，这里主动跳过（Defs 无空键）
            if (IsAllWordChars(n))
            {
                if (!_plain.ContainsKey(n)) { _plain[n] = seen; _lens.Add(n.Length); }
            }
            else if (!_odd.ContainsKey(n)) { _odd[n] = seen; oddNames.Add(n); }
        }
        _oddRe = oddNames.Count > 0
            ? new Regex(@"\b(" + string.Join("|", oddNames.Select(Regex.Escape)) + @")\b",
                RegexOptions.Compiled | RegexOptions.CultureInvariant)
            : null;
    }

    /// <summary>扫描一行文本，按出现顺序回调命中的符号名（与旧正则 leftmost-first 非重叠顺序一致）。</summary>
    public void Match(string line, Action<string> onHit)
    {
        if (_oddRe == null) { ScanPlain(line, onHit); return; }

        _buf.Clear();
        CollectPlain(line);
        foreach (Match m in _oddRe.Matches(line))
        {
            var name = m.Groups[1].Value;
            if (_odd.TryGetValue(name, out var order))
                _buf.Add(new Cand { Pos = m.Index, Len = m.Length, Order = order, Name = name });
        }
        if (_buf.Count == 0) return;
        if (_buf.Count == 1) { onHit(_buf[0].Name); return; }

        _buf.Sort(ByPosThenOrder);          // leftmost-first：先比位置，同位置比 alternation 序号
        var cursor = 0;
        foreach (var c in _buf)
        {
            if (c.Pos < cursor) continue;   // 与已消费区间重叠 → 丢弃（等价于旧正则的非重叠推进）
            onHit(c.Name);
            cursor = c.Pos + c.Len;
        }
    }

    /// <summary>无兜底符号名时的快路径：整词扫描直接回调，零额外分配。</summary>
    void ScanPlain(string line, Action<string> onHit)
    {
        var n = line.Length;
        var i = 0;
        while (i < n)
        {
            if (!IsWordChar(line[i])) { i++; continue; }
            var start = i;
            while (i < n && IsWordChar(line[i])) i++;
            var len = i - start;
            if (_lens.Contains(len))
            {
                var w = line.Substring(start, len);
                if (_plain.ContainsKey(w)) onHit(w);
            }
        }
    }

    /// <summary>收集整词命中到候选缓冲（供与兜底正则命中合并排序）</summary>
    void CollectPlain(string line)
    {
        var n = line.Length;
        var i = 0;
        while (i < n)
        {
            if (!IsWordChar(line[i])) { i++; continue; }
            var start = i;
            while (i < n && IsWordChar(line[i])) i++;
            var len = i - start;
            if (_lens.Contains(len))
            {
                var w = line.Substring(start, len);
                if (_plain.TryGetValue(w, out var order))
                    _buf.Add(new Cand { Pos = start, Len = len, Order = order, Name = w });
            }
        }
    }

    /// <summary>是否全部由词字符构成（此类符号名可被分词整词切出）。</summary>
    static bool IsAllWordChars(string s)
    {
        foreach (var c in s)
            if (!IsWordChar(c)) return false;
        return true;
    }

    /// <summary>词字符判定：与 .NET 正则 \w 对齐（字母/数字/下划线，含 Unicode 字母如中文）。</summary>
    static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
