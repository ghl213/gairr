/// <summary>压缩前必用文件询问的文件清单（P6）：维护当前任务 Read/MapSlice 的"文件+位置+意图"，
/// 触发压缩时由 AgentLoop 先询问 LLM"清单上哪些文件对未完成工作必须用"，必用文件豁免压缩判定②③④（见 ContextCompressor）。
/// 护栏：LLM 只能缩小豁免范围——确定性保底（最近 N 轮读取、已修改文件最新读取）始终生效；
/// 查询失败/超时返回 null，压缩器退回纯确定性行为（不劣化）。</summary>
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GAIRR.Core;

/// <summary>文件清单条目：本任务读取/引用过的单个文件（含位置与读取时意图）</summary>
public sealed class FileRef
{
    public string Path = "";      // 规范化路径（与 ContextCompressor.NormPath 同规则）
    public string Lines = "";     // 读取位置（L10-50 / L10起 / 全文），重复读取时刷新
    public string Intent = "";    // 意图片段（读取时 assistant 消息文本，≤60 字）
    public bool Modified;         // 本会话是否被修改过（Write/Edit）
    public long Tick;             // 最近一次读取时刻（全局单调计数）
}

/// <summary>任务级文件清单追踪器（AgentLoop 持一个实例，新任务开始 Clear）。
/// Read/MapSlice 成功 → NoteRead；Write/Edit 成功 → MarkModified；压缩前 → Snapshot/Fingerprint。
/// 工具并发执行，内部加锁。</summary>
public sealed class FileRefTracker
{
    readonly Dictionary<string, FileRef> map = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> order = new();          // 首次读取顺序（提示词叙述序）
    readonly HashSet<string> modified = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>登记/更新一次读取（仅 Read/MapSlice 调用；argsJson 含 path，MapSlice 含 spec）</summary>
    public void NoteRead(string argsJson, string? intent, long tick)
    {
        var (path, lines) = ParseReadArgs(argsJson);
        if (path.Length == 0) return;
        var np = NormPath(path);
        lock (map)
        {
            if (!map.TryGetValue(np, out var r)) { r = new FileRef { Path = np }; map[np] = r; order.Add(np); }
            if (lines.Length > 0) r.Lines = lines;
            if (intent != null && intent.Length > 0)
                r.Intent = LLMClient.Trunc(intent.Replace("\n", " "), 60);
            r.Modified = modified.Contains(np);
            r.Tick = tick;
        }
    }

    /// <summary>登记文件修改（Write/Edit 成功）：清单条目与提示词均标注"已修改"，旧读取视为作废</summary>
    public void MarkModified(string path)
    {
        var np = NormPath(path);
        if (np.Length == 0) return;
        lock (map) { modified.Add(np); if (map.TryGetValue(np, out var r)) r.Modified = true; }
    }

    /// <summary>新任务开始清空（与 readSigCount 同节奏清理）</summary>
    public void Clear() { lock (map) { map.Clear(); order.Clear(); modified.Clear(); } }

    /// <summary>清单文件数</summary>
    public int Count { get { lock (map) return map.Count; } }

    /// <summary>清单快照（按首次读取顺序），供构建提示词与传给压缩器</summary>
    public List<FileRef> Snapshot()
    {
        lock (map)
        {
            var list = new List<FileRef>(order.Count);
            foreach (var p in order) list.Add(map[p]);
            return list;
        }
    }

    /// <summary>清单指纹（任一文件新增读取/修改都会变化）：AgentLoop 据此跳过重复 LLM 查询</summary>
    public long Fingerprint()
    {
        lock (map)
        {
            var fp = (long)modified.Count * 1000003 + order.Count;
            foreach (var p in order) fp = fp * 31 + map[p].Tick + p.GetHashCode();
            return fp;
        }
    }

    /// <summary>构建 LLM 询问提示词：文件清单（序号+文件+位置+意图+已修改标记）+当前任务+未完成步骤，要求只输出必用文件序号</summary>
    public static string BuildPrompt(List<FileRef> refs, string taskText, List<string>? steps, HashSet<int>? done)
    {
        var sb = new StringBuilder();
        sb.Append("以下是本任务读取过的文件清单（序号、文件、位置、当时用途）。\n");
        sb.Append("当前任务：").Append(LLMClient.Trunc(taskText, 300)).Append('\n');
        if (steps != null && steps.Count > 0)
        {
            var pend = new List<string>();
            for (var i = 0; i < steps.Count; i++)
                if (done?.Contains(i + 1) != true) pend.Add((i + 1) + "." + LLMClient.Trunc(steps[i], 40));
            sb.Append("未完成计划步骤：").Append(pend.Count > 0 ? string.Join("；", pend) : "无（计划已全部完成）").Append('\n');
        }
        for (var i = 0; i < refs.Count; i++)
        {
            var r = refs[i];
            sb.Append(i + 1).Append(". ").Append(r.Path);
            if (r.Lines.Length > 0) sb.Append(" [").Append(r.Lines).Append(']');
            if (r.Modified) sb.Append(" [已修改]");
            if (r.Intent.Length > 0) sb.Append(" // ").Append(r.Intent);
            sb.Append('\n');
        }
        sb.Append("\n请判断：哪些文件对【当前未完成的剩余工作】是必须的（接下来要编辑该文件，或需要引用其内容/行号才能推进）。");
        sb.Append("只输出必须文件的序号，每行一个纯数字；若没有必须文件，只输出：无");
        return sb.ToString();
    }

    /// <summary>解析模型回答为必用文件路径集合（已规范化）。
    /// 返回 null：回答为空/解析不出有效序号（调用方回退确定性压缩）；返回空集：模型明确回答"无"（可更激进压缩）。</summary>
    public static HashSet<string>? ParseResponse(string text, List<FileRef> refs)
    {
        var idx = new HashSet<int>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            foreach (var part in Regex.Split(line, "[、,，;；\\s]+"))
            {
                var t = part.TrimStart('#', '-', '·').Trim().TrimEnd('.', '。');
                if (int.TryParse(t, out var n) && n >= 1 && n <= refs.Count) idx.Add(n);
            }
        }
        if (idx.Count == 0)
            return text.Contains("无") ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : null;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in idx) set.Add(refs[n - 1].Path);
        return set;
    }

    /// <summary>从 Read/MapSlice 参数提取 (path, 位置)：Read 用 path+start_line/end_line；MapSlice 取 spec 首段文件名；提取不到返回空</summary>
    static (string Path, string Lines) ParseReadArgs(string argsJson)
    {
        try
        {
            var n = JsonNode.Parse(argsJson);
            var path = n?["path"]?.GetValue<string>() ?? "";
            var lines = "";
            var sl = n?["start_line"]?.GetValue<int?>();
            var el = n?["end_line"]?.GetValue<int?>();
            if (sl is int s && el is int e && e > s) lines = $"L{s}-{e}";
            else if (sl is int s2) lines = $"L{s2}起";
            else if (n?["spec"] is { } sp)
            {
                var first = (sp.GetValue<string>() ?? "").Split(',')[0].Trim();
                var ci = first.IndexOf(':');
                path = ci > 0 ? first[..ci] : first;   // MapSlice spec 形如 "文件:行号"
            }
            return (path, lines);
        }
        catch { return ("", ""); }
    }

    /// <summary>路径规范化（与 ContextCompressor.NormPath 同规则，保证两侧路径可匹配）</summary>
    static string NormPath(string p) => p.Replace('/', '\\').TrimStart('.', '\\');
}
