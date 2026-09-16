using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GAIRR.Core;

/// <summary>变更记录条目（宿主 UI“本会话改动”卡片 / “本轮改动”条带的数据源）：
/// File=写入时的绝对路径；IsNew=本次为新建文件（写前无备份）；Session=归属会话 Key（空=未标注，历史日志兼容）；
/// Backup=本次写前备份文件绝对路径（宿主据此取“改前”基线做改动对比着色；空=无基线，按整文件新增展示）。</summary>
public sealed record ChangeEntry(DateTime Time, string Tool, string File, string Summary, bool IsNew, string Session, string Backup = "");

/// <summary>
/// 阶段二框架级强制：写类工具执行前自动备份 + changelog.jsonl 结构化变更日志（开发方案 11.13）。
/// 备份与日志由代码逻辑触发，不依赖模型自觉。
/// </summary>
public class ChangeJournal
{
    readonly AppConfig cfg;

    /// <summary>当前任务文本（AgentLoop 每任务开始时设置），随变更落日志</summary>
    public string CurrentTask { get; set; } = "";

    /// <summary>当前任务归属会话 Key（AgentLoop 每任务开始时从 Loop.SessionId 同步），随变更落日志：
    /// 多会话并行写同一份 changelog 时，宿主按此字段把改动归到各自会话卡片，互不串台。</summary>
    public string CurrentSession { get; set; } = "";

    /// <summary>当前任务内成功写文件的次数（AgentLoop 每任务开始清零）：用于判定任务是否有真实修改动作（钉钉按需通知）</summary>
    public int WriteCount { get; set; }

    public ChangeJournal(AppConfig cfg) => this.cfg = cfg;

    /* ---------- 内存最近变更（宿主实时聚合免读盘） ---------- */

    const int RecentCap = 400;   // 内存窗口上限：超出丢最旧（更早的改动由 changelog.jsonl 读盘兜底）
    readonly List<ChangeEntry> recent = new();
    readonly object recentGate = new();

    /// <summary>内存窗口条目数（宿主做“有无新改动”的廉价判定，避免每个事件都全量聚合）</summary>
    public int RecentCount { get { lock (recentGate) return recent.Count; } }

    /// <summary>备份目录：项目根/back</summary>
    public string BackupDir => Path.Combine(cfg.ProjectRoot, "back");

    /// <summary>
    /// 修改前把原文件复制到 back/，命名 = 全路径下划线化_时间戳；每文件只保留最近 10 版。
    /// 文件不存在（新建场景）返回空串。
    /// </summary>
    public string BackupBeforeWrite(string filePath)
    {
        if (!File.Exists(filePath)) return "";
        Directory.CreateDirectory(BackupDir);
        var flat = FlatName(filePath);
        var dest = Path.Combine(BackupDir, flat + "_" + DateTime.Now.ToString("yyMMdd_HHmmss"));
        File.Copy(filePath, dest, overwrite: true);
        Prune(flat);
        return dest;
    }

    /// <summary>写类工具成功后追加一条 JSONL 变更记录（失败不影响主流程），并压入内存窗口供宿主实时聚合</summary>
    public void Log(string tool, string file, string backup, string summary)
    {
        try
        {
            Directory.CreateDirectory(BackupDir);
            var now = DateTime.Now;
            var rec = new JsonObject
            {
                ["time"] = now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["session"] = CurrentSession,
                ["task"] = LLMClient.Trunc(CurrentTask, 200),
                ["tool"] = tool,
                ["file"] = file,
                ["backup"] = backup,
                ["summary"] = summary,
            };
            File.AppendAllText(Path.Combine(BackupDir, "changelog.jsonl"),
                rec.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n", new UTF8Encoding(false));   // changelog 中文直存
            Push(new ChangeEntry(now, tool, file, summary, backup.Length == 0, CurrentSession, backup));
        }
        catch { /* 日志失败不打断任务 */ }
    }

    /// <summary>压入内存窗口（超上限丢最旧；写工具线程与宿主 UI 线程并发安全）</summary>
    void Push(ChangeEntry e)
    {
        lock (recentGate)
        {
            recent.Add(e);
            if (recent.Count > RecentCap) recent.RemoveRange(0, recent.Count - RecentCap);
        }
    }

    /// <summary>
    /// 时间窗内变更条目（宿主“本会话改动/本轮改动”聚合入口）：
    /// 1) 起点回退 1 秒——changelog 仅秒级精度，防同秒改动被边界漏掉；
    /// 2) sessionId 非空时只取该会话（未标注会话的历史条目一律计入）——多会话并行不串台；
    /// 3) 同一文件多次改动只保留最后一次状态，但 Backup 取窗口内最早一次的写前备份（= 本会话“改前”基线）；结果按时间升序；
    /// 4) 内存窗口无命中时回落读 changelog.jsonl（重启后恢复历史会话场景）。
    /// </summary>
    public List<ChangeEntry> EntriesSince(DateTime since, string sessionId = "")
    {
        var cutoff = since.AddSeconds(-1);
        var list = new List<ChangeEntry>();
        lock (recentGate)
            foreach (var e in recent)
                if (e.Time >= cutoff && SessionMatch(e.Session, sessionId)) list.Add(e);
        if (list.Count == 0)
            list.AddRange(ReadLogSince(cutoff, sessionId));
        var byFile = new Dictionary<string, ChangeEntry>(StringComparer.OrdinalIgnoreCase);
        var baseBk = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in list.OrderBy(x => x.Time))
        {
            // 同文件多次改动：状态取最后一次，但“改前基线”取窗口内最早一次的写前备份
            // （否则改动对比只体现最后一次编辑的差异，看不到本会话累计改了什么）
            if (!baseBk.ContainsKey(e.File)) baseBk[e.File] = e.Backup;
            byFile[e.File] = e;
        }
        return byFile.Values.OrderBy(x => x.Time).Select(e => e with { Backup = baseBk[e.File] }).ToList();
    }

    /// <summary>会话归属匹配：目标为空（不限会话）或条目未标注（历史日志/CLI）→ 计入；否则须一致</summary>
    static bool SessionMatch(string entrySession, string want) =>
        want.Length == 0 || entrySession.Length == 0 ||
        string.Equals(entrySession, want, StringComparison.OrdinalIgnoreCase);

    /// <summary>读盘兜底：changelog.jsonl 中 time ≥ cutoff 且会话匹配的条目（坏行跳过不抛）</summary>
    List<ChangeEntry> ReadLogSince(DateTime cutoff, string sessionId)
    {
        var outp = new List<ChangeEntry>();
        try
        {
            var logPath = Path.Combine(BackupDir, "changelog.jsonl");
            if (!File.Exists(logPath)) return outp;
            foreach (var line in File.ReadLines(logPath))
            {
                try
                {
                    if (JsonNode.Parse(line) is not JsonObject rec) continue;
                    if (!DateTime.TryParse(rec["time"]?.GetValue<string>(), out var t) || t < cutoff) continue;
                    var file = rec["file"]?.GetValue<string>() ?? "";
                    if (file.Length == 0) continue;
                    var sess = rec["session"]?.GetValue<string>() ?? "";
                    if (!SessionMatch(sess, sessionId)) continue;
                    var bk = rec["backup"]?.GetValue<string>() ?? "";
                    outp.Add(new ChangeEntry(t, rec["tool"]?.GetValue<string>() ?? "", file,
                        rec["summary"]?.GetValue<string>() ?? "", bk.Length == 0, sess, bk));
                }
                catch { /* 单行坏数据跳过 */ }
            }
        }
        catch { /* 读盘失败按空清单处理 */ }
        return outp;
    }

    /// <summary>全路径下划线化：d:\work\x.txt → d_work_x.txt。
    /// 公开给宿主：GUI 按此规则扫 back/ 找“改前基线”备份，两侧命名口径必须同源，不能各写一份。</summary>
    public static string FlatName(string filePath) =>
        Path.GetFullPath(filePath)
            .Replace(':', '_').Replace('\\', '_').Replace('/', '_')
            .TrimStart('_');

    /// <summary>同一文件的备份只留最近 10 版（文件名含时间戳，字典序即时间序）</summary>
    void Prune(string flat)
    {
        foreach (var f in Directory.GetFiles(BackupDir, flat + "_*")
                     .OrderByDescending(f => f).Skip(10))
        {
            try { File.Delete(f); } catch { /* 占用则跳过 */ }
        }
    }
}
