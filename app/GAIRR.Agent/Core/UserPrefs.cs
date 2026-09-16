using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace GAIRR.Core;

/// <summary>用户偏好自动沉淀：检测用户消息中的长期偏好语义（"以后/下次/记住…"类表述），
/// 自动把原句追加到项目 .gairr/prompts/prefs.md（所有角色提示词已统一要求模型先 Read 该文件并全程遵循，
/// 可 Edit 增删改/合并/清理，故自动沉淀只负责追写、不代劳删除，避免与模型维护互相冲突）。
/// 只沉淀"长期语义"信号句：一次性任务指令不含未来词，不会误入文件。</summary>
public static class UserPrefs
{
    const string PrefsName = "prefs.md";
    const int MaxLen = 200;        // 单条截断长度

    /// <summary>强信号词：直接宣告"这是规则"（记住/偏好/习惯/一向），无需额外指令词确认</summary>
    static readonly string[] StrongFutureWords = { "记住", "偏好", "习惯", "一向" };

    /// <summary>一般信号词：需与指令词同时命中，规避"下次再说"类寒暄误报</summary>
    static readonly string[] FutureWords = { "以后", "下次", "今后", "特别注意" };

    /// <summary>指令/主题词：与信号词同时命中才确认，规避"我们下次再说"类寒暄误报</summary>
    static readonly string[] ActionWords = { "要", "不要", "别用", "只", "就", "必须", "应", "注意", "影响", "保持", "保留", "避免", "显示", "隐藏", "高亮", "配色", "命名", "风格", "遵循", "统一", "一律" };

    /// <summary>撤销/删除偏好句：不沉淀（由模型通道按既有规则用 Edit 维护 prefs.md，框架不代劳删除）</summary>
    static readonly Regex RemovePattern = new(
        @"(删除|撤销|忘掉?|取消|去掉)\S{0,8}(偏好|要求|规则|条目)|不再需要(那条|这条|之前)",
        RegexOptions.Compiled);

    /// <summary>从用户消息中检测可作为长期偏好沉淀的候选；非偏好消息返回 null。
    /// 产出清洗后的原文（超长截断 200 字），是否写入由 Append 决定。</summary>
    public static string? Detect(string userText)
    {
        if (string.IsNullOrWhiteSpace(userText)) return null;
        var t = userText.Replace("\r\n", "\n").Replace('\r', ' ').Trim();
        if (t.Length < 8 || RemovePattern.IsMatch(t)) return null;
        var isStrong = StrongFutureWords.Any(t.Contains);   // 强信号："记住/偏好/习惯"本身已宣告这是规则
        if (!isStrong && !FutureWords.Any(t.Contains)) return null;
        if (!isStrong && !ActionWords.Any(t.Contains)) return null;
        if (t.Length <= MaxLen) return t;
        // 超长消息：从首个信号词向前找最近断句处，只截取"信号句"到末尾，避免任务上下文混入偏好条目
        var sig = int.MaxValue;
        foreach (var w in StrongFutureWords.Concat(FutureWords))
        {
            var i = t.IndexOf(w, StringComparison.Ordinal);
            if (i >= 0 && i < sig) sig = i;
        }
        if (sig < int.MaxValue)
        {
            var cut = 0;
            for (var i = sig - 2; i >= 0; i--)
                if ("。！？；，、.,".IndexOf(t[i]) >= 0) { cut = i + 1; break; }
            if (cut > 0) t = t[cut..].Trim();
        }
        return t.Length > MaxLen ? t[..MaxLen] : t;
    }

    /// <summary>追加一条偏好到 .gairr/prompts/prefs.md（自动建目录与文件头；全文含同文本/同前 30 字即视为已沉淀，跳过）。
    /// 返回是否新增；任何失败静默返回 false，不阻断任务。</summary>
    public static bool Append(AppConfig cfg, string text)
    {
        try
        {
            var root = cfg.ProjectRoot;
            if (root.Length == 0) return false;
            var norm = text.Trim();
            if (norm.Length == 0) return false;
            var dir = Path.Combine(root, ".gairr", "prompts");
            var path = Path.Combine(dir, PrefsName);
            Directory.CreateDirectory(dir);
            var existing = File.Exists(path) ? File.ReadAllText(path) : "";
            var key = norm.Length > 30 ? norm[..30] : norm;
            if (existing.Contains(key, StringComparison.OrdinalIgnoreCase)) return false;   // 已沉淀过，不重复追加
            var line = "- " + norm + "（" + DateTime.Now.ToString("yyyy-MM-dd") + " 用户原话）";
            var content = existing.Length == 0
                ? "# GAIRR 用户长期偏好\n\n（每行一条。框架自动沉淀用户原话，也可手工维护；模型每次任务先 Read 并全程遵循，可 Edit 增删改/合并/清理；用户当前指令与旧偏好冲突时以当前指令为准。）\n\n" + line + "\n"
                : existing.TrimEnd() + "\n" + line + "\n";
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return true;
        }
        catch { return false; }
    }
}