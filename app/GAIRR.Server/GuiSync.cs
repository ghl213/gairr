using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using GAIRR.AgentHost;
using GAIRR.Core;

// GUI 会话源桥接（桌面 GAIRR 的 data/session_history.json，33MB 级单文件）：
//  1. 列表/恢复/历史按需流式扫描会话头（Messages 数组用 Skip 跳过，不建全文件 DOM，内存零驻留）；
//  2. busy_<id>.json 标记 = 桌面端正在执行该会话（GUI 任务启动写/收口删），Server 端 resume/发消息前检查；
//  3. session_history.inbox.json = 手机侧续聊消息收件箱：桌面 GUI 下次启动时合并进 session_history.json。
// 全部读取以 FileShare.ReadWrite|Delete 打开，兼容 GUI 侧 File.Replace 原子落盘不互相阻塞。

sealed class GuiSessionHead
{
    public string Id = "";
    public string Project = "";
    public string Title = "";
    public DateTime Created;             // TryParse 结果（缺省/失败 → MinValue，列表排序垫底）
    public bool IsOrchestration;
    public DateTime? Time;               // GUI Time 字段（最后活动），做 updatedAt 展示
    public int RawMsgCount;              // GUI 文件该会话 Messages 数组长度（全 kind，收件箱合并基数）
}

/// <summary>单会话全量提取：对话正文 + GUI 侧基数</summary>
sealed class GuiSessionFull
{
    public GuiSessionHead Head = null!;
    /// <summary>对话正文（kind User/Agent 且 text 非空）→ (role, content) 可回灌 AgentLoop</summary>
    public List<(string Role, string Content)> Chats = new();
}

sealed class InboxMsg
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("who")] public string Who { get; set; } = "";
    [JsonPropertyName("text")] public string? Text { get; set; }
}

/// <summary>收件箱条目：GuiBaseCount = 条目创建时 GUI 文件该会话的消息数（桌面合并判定：桌面消息数未变才顺序追加）</summary>
sealed class InboxEntry
{
    public string SessionId { get; set; } = "";
    public int GuiBaseCount { get; set; }
    public List<InboxMsg> Messages { get; set; } = new();
}

static class GuiSync
{
    public const string InboxFileName = "session_history.inbox.json";

    /// <summary>GUI 会话源文件路径；未配置 GuiDataDir 或文件不存在 → null（调用方回退旧逻辑）</summary>
    public static string? ResolveGuiFile(string? guiDataDir)
    {
        if (string.IsNullOrWhiteSpace(guiDataDir)) return null;
        var f = Path.Combine(guiDataDir, "session_history.json");
        return File.Exists(f) ? f : null;
    }

    /// <summary>GUI 会话 id 形制：Guid N[..8] 8 位 hex 无前缀（区分 s-*/plan-/leaf-/review-/cli 执行记录）</summary>
    public static bool IsGuiSessionId(string id) => id.Length == 8 && id.All(char.IsAsciiHexDigit);

    /// <summary>副本中 user/assistant 正文条数（收件箱基线口径）</summary>
    public static int ChatCount(SessionRecord rec) => (rec.Messages ?? new List<SessionMessage>())
        .Count(m => m.Role is "user" or "assistant" && !string.IsNullOrWhiteSpace(m.Content));

    // ---- 会话头列表缓存：GUI 空闲（文件未变）时列表请求零扫描；GUI 任务运行中 mtime 每轮落盘都变，自动失效重扫 ----
    static readonly object CacheGate = new();
    static string? _headFile;
    static DateTime _headMtime;
    static long _headLen;
    static List<GuiSessionHead>? _heads;

    /// <summary>会话头列表（带文件指纹缓存）；null = 解析失败</summary>
    public static List<GuiSessionHead>? ListHeads(string guiFile)
    {
        lock (CacheGate)
        {
            var fi = new FileInfo(guiFile);
            if (!fi.Exists) return null;
            if (_heads != null && _headFile == guiFile && _headMtime == fi.LastWriteTimeUtc && _headLen == fi.Length)
                return _heads;
            var heads = ScanHeads(guiFile);
            if (heads == null) return _heads;   // 解析失败：沿用旧缓存兜底（不抛不空列表）
            _headFile = guiFile;
            _headMtime = fi.LastWriteTimeUtc;
            _headLen = fi.Length;
            _heads = heads;
            return _heads;
        }
    }

    /// <summary>提取单会话（含全量 Messages 与 GUI 侧基数）。找不到 → null。匹配元素才建 DOM，其余整元素 Skip。</summary>
    public static GuiSessionFull? FindSession(string guiFile, string sessionId)
    {
        try
        {
            var bytes = ReadGuiBytes(guiFile);
            if (bytes == null) return null;
            var reader = new Utf8JsonReader(bytes.AsSpan(BomLen(bytes)));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray) return null;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray) break;
                if (reader.TokenType != JsonTokenType.StartObject) continue;
                if (!ProbeId(ref reader, out var id) || id != sessionId) { reader.Skip(); continue; }
                var dom = JsonDocument.ParseValue(ref reader);
                using (dom)
                {
                    var full = FromElement(dom.RootElement);
                    return full.Head.Id.Length > 0 ? full : null;
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>桌面端任务运行标记检查（busy_&lt;id&gt;.json 存在于 GuiDataDir）</summary>
    public static bool IsGuiBusy(string? guiDataDir, string sessionId) =>
        !string.IsNullOrWhiteSpace(guiDataDir)
        && File.Exists(Path.Combine(guiDataDir, "busy_" + sessionId + ".json"));

    // ---- GUI 源会话运行追踪：resume 时记录副本对话条数基线，任务收口后把尾部新消息刷入收件箱 ----
    static readonly ConcurrentDictionary<string, int> RunBase = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>resume GUI 会话时记录基线（磁盘副本的 user/assistant 条数）；非 GUI 源会话无需调用。</summary>
    public static void NoteResumed(string sessionId, int userAssistantCount) =>
        RunBase[sessionId] = userAssistantCount;

    /// <summary>任务收口：磁盘副本比基线多出的 user/assistant 尾部 → 收件箱。
    /// 基线 = resume 时点副本全量 → 尾部天然包含"上次收口后、本次收口前"漏刷的所有轮次（重启丢字典也由下次 resume 补全）。</summary>
    public static void FlushInboxTail(string sessionId, string? guiDataDir, string? guiFile, SessionStore store, string ns)
    {
        if (!RunBase.TryGetValue(sessionId, out var prev)) return;   // 非 GUI 源会话/从未被手机 resume
        SessionRecord? rec;
        try { rec = store.Load(ns, sessionId); } catch { return; }
        if (rec == null) return;
        var msgs = (rec.Messages ?? new List<SessionMessage>())
            .Where(m => m.Role is "user" or "assistant" && !string.IsNullOrWhiteSpace(m.Content))
            .ToList();
        if (msgs.Count <= prev) { RunBase[sessionId] = msgs.Count; return; }
        var tail = new List<InboxMsg>();
        for (int i = prev; i < msgs.Count; i++)
            tail.Add(new InboxMsg
            {
                Kind = msgs[i].Role == "user" ? "User" : "Agent",
                Who = msgs[i].Role == "user" ? "你（手机端）" : "手机端 GAIRR",
                Text = msgs[i].Content,
            });
        RunBase[sessionId] = msgs.Count;
        if (tail.Count == 0) return;
        try { AppendInbox(guiDataDir, guiFile, sessionId, tail); }
        catch (Exception ex) { Console.WriteLine($"[inbox {sessionId}] 追加失败：{ex.Message}"); }
    }

    // ---- 收件箱：{ sessionId, guiBaseCount, messages[] } 数组；条目存在 → 追加且保持基数（条目在 = GUI 未合并过 = GUI 基数未变）----
    static readonly object InboxGate = new();

    static void AppendInbox(string? guiDataDir, string? guiFile, string sessionId, List<InboxMsg> tail)
    {
        if (string.IsNullOrWhiteSpace(guiDataDir)) return;
        var path = Path.Combine(guiDataDir, InboxFileName);
        lock (InboxGate)
        {
            var entries = ReadInbox(path);
            var hit = entries.FirstOrDefault(e => e.SessionId == sessionId);
            if (hit == null)
            {
                // 新条目需要“当前 GUI 文件该会话消息数”作合并基数；GUI 源文件不可用/会话被删除 → 放弃入箱（副本仍保留）
                if (guiFile == null) return;
                var full = FindSession(guiFile, sessionId);
                if (full == null) return;
                hit = new InboxEntry { SessionId = sessionId, GuiBaseCount = full.Head.RawMsgCount };
                entries.Add(hit);
            }
            hit.Messages.AddRange(tail);
            WriteInbox(path, entries);
        }
    }

    static List<InboxEntry> ReadInbox(string path)
    {
        if (!File.Exists(path)) return new List<InboxEntry>();
        try { return JsonSerializer.Deserialize<List<InboxEntry>>(File.ReadAllText(path)) ?? new List<InboxEntry>(); }
        catch { return new List<InboxEntry>(); }   // 损坏收件箱按空处理（不阻塞手机续聊，桌面合并会重来）
    }

    static void WriteInbox(string path, List<InboxEntry> entries)
    {
        if (entries.Count == 0) { if (File.Exists(path)) File.Delete(path); return; }
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(entries));
        if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
    }

    // ---- GUI 方案确认挂起（手机端"确认计划"场景）：pending_<id>.json 标记"该会话有待决策方案"，
    // 桌面弹 PlanConfirmDialog 前写、确认框收口（桌面本地确认/手机回写）后清；
    // decisions.inbox.json = 手机端决策回写收件箱：桌面 GUI 弹窗轮询消费，命中 planId 即以编程方式注入结果。----

    public const string PendingFilePrefix = "pending_";
    public const string DecisionsInboxFileName = "decisions.inbox.json";

    /// <summary>写入方案确认挂起标记 pending_&lt;id&gt;.json（内容 = plan 确认帧载荷，供手机端订阅时兜底查询）。</summary>
    public static void WritePending(string? guiDataDir, string sessionId, PlanConfirmBody body)
    {
        if (string.IsNullOrWhiteSpace(guiDataDir)) return;
        try
        {
            var tmp = Path.Combine(guiDataDir, PendingFilePrefix + sessionId + ".json.tmp");
            File.WriteAllText(tmp, JsonSerializer.Serialize(body));
            var target = Path.Combine(guiDataDir, PendingFilePrefix + sessionId + ".json");
            if (File.Exists(target)) File.Replace(tmp, target, null); else File.Move(tmp, target);
        }
        catch (Exception ex) { Console.WriteLine($"[pending {sessionId}] 写入失败：{ex.Message}"); }
    }

    /// <summary>桌面弹窗等待期间轮询的挂起判定：pending_&lt;id&gt;.json 存在 = 有待决策方案（手机端订阅不提前关流）。</summary>
    public static bool HasPending(string? guiDataDir, string sessionId) =>
        !string.IsNullOrWhiteSpace(guiDataDir)
        && File.Exists(Path.Combine(guiDataDir, PendingFilePrefix + sessionId + ".json"));

    /// <summary>清除方案确认挂起标记（桌面收口 / 手机回写后）。</summary>
    public static void ClearPending(string? guiDataDir, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(guiDataDir)) return;
        try
        {
            var f = Path.Combine(guiDataDir, PendingFilePrefix + sessionId + ".json");
            var tmp = f + ".tmp";
            if (File.Exists(f)) File.Delete(f);
            if (File.Exists(tmp)) File.Delete(tmp);
        }
        catch (Exception ex) { Console.WriteLine($"[pending {sessionId}] 清除失败：{ex.Message}"); }
    }

    // ---- 桌面危险确认挂起标记：danger_&lt;id&gt;.json（内容 = SecurityAlert 帧载荷）。
    // 桌面危险卡挂起期间写、裁决/收起/超时各出口清；Server /gui-events 订阅回放据此：
    //  ① 标记在 → 即使事件缓冲已丢帧（Server 重启/溢出）也补送合成 SecurityAlert 帧，手机进会话必见待处理卡；
    //  ② 标记无 → 回放过滤掉 SecurityAlert 帧（均为已决策），手机进会话不再重显已点击过的旧卡。----

    public const string DangerPendingFilePrefix = "danger_";

    /// <summary>读取危险挂起标记（无标记/损坏 → null）</summary>
    public static SecurityAlert? ReadDangerPending(string? guiDataDir, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(guiDataDir)) return null;
        try
        {
            var f = Path.Combine(guiDataDir, DangerPendingFilePrefix + sessionId + ".json");
            if (!File.Exists(f)) return null;
            return JsonSerializer.Deserialize<SecurityAlert>(File.ReadAllText(f));
        }
        catch { return null; }
    }

    /// <summary>清除危险挂起标记（桌面收口 / 手机回写决策后调用）。</summary>
    public static void ClearDangerPending(string? guiDataDir, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(guiDataDir)) return;
        try
        {
            var f = Path.Combine(guiDataDir, DangerPendingFilePrefix + sessionId + ".json");
            if (File.Exists(f)) File.Delete(f);
        }
        catch { /* 尽力而为 */ }
    }

    /// <summary>手机端决策回写 → 追加进 decisions.inbox.json（数组 { sessionId, planId, allow, at }）。</summary>
    public static void AppendDecision(string? guiDataDir, string sessionId, string planId, bool allow)
    {
        if (string.IsNullOrWhiteSpace(guiDataDir)) return;
        lock (DecisionGate)
        {
            try
            {
                var path = Path.Combine(guiDataDir, DecisionsInboxFileName);
                var entries = ReadDecisions(path);
                entries.Add(new GuiDecisionEntry
                {
                    SessionId = sessionId,
                    PlanId = planId,
                    Allow = allow,
                    At = DateTime.Now.ToString("s"),
                });
                WriteDecisions(path, entries);
            }
            catch (Exception ex) { Console.WriteLine($"[decision {sessionId}] 追加失败：{ex.Message}"); }
        }
    }

    /// <summary>读取决策收件箱全量（不消费）。</summary>
    public static List<GuiDecisionEntry> ReadDecisionsInbox(string? guiDataDir)
    {
        if (string.IsNullOrWhiteSpace(guiDataDir)) return new List<GuiDecisionEntry>();
        try { return ReadDecisions(Path.Combine(guiDataDir, DecisionsInboxFileName)); }
        catch { return new List<GuiDecisionEntry>(); }
    }

    /// <summary>消费 decisions.inbox.json 中命中指定会话+plan 的条目：读→过滤→原子写回（读改写同一批，防并发覆盖）。</summary>
    public static int ConsumeDecision(string? guiDataDir, string sessionId, string planId)
    {
        if (string.IsNullOrWhiteSpace(guiDataDir)) return 0;
        lock (DecisionGate)
        {
            var path = Path.Combine(guiDataDir, DecisionsInboxFileName);
            var entries = ReadDecisions(path);
            var hit = entries.FindAll(e => e.SessionId == sessionId && e.PlanId == planId);
            if (hit.Count == 0) return 0;
            entries.RemoveAll(e => e.SessionId == sessionId && e.PlanId == planId);
            WriteDecisions(path, entries);
            return hit.Count;
        }
    }

    static readonly object DecisionGate = new();

    static List<GuiDecisionEntry> ReadDecisions(string path)
    {
        if (!File.Exists(path)) return new List<GuiDecisionEntry>();
        try { return JsonSerializer.Deserialize<List<GuiDecisionEntry>>(File.ReadAllText(path)) ?? new List<GuiDecisionEntry>(); }
        catch (Exception ex) { Console.WriteLine($"[decision] 收件箱损坏按空处理：{ex.Message}"); return new List<GuiDecisionEntry>(); }
    }

    static void WriteDecisions(string path, List<GuiDecisionEntry> entries)
    {
        if (entries.Count == 0) { if (File.Exists(path)) File.Delete(path); return; }
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(entries));
        if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
    }

    // ---- 桌面新增回复合并（/history 只读视图）：副本头部 = resume 时刻桌面快照（GuiChatBase 条），
    // 尾部 = 手机续聊轮次；桌面文件里后新增的回复以只读视图接在副本后返回，不回写 rec/GUI 文件。
    // 语义：手机 resume 过的 GUI 源会话，桌面 gairr.exe 后续新回复只写 session_history.json、不追副本，
    // 若 /history 只回副本则手机端永远看不到——这里用基线+内容对齐把桌面增量补进返回视图 ----
    public static List<SessionMessage> MergeGuiTail(SessionRecord rec, List<(string Role, string Content)> guiChats)
    {
        var r = ChatMsgs(rec);
        var g = guiChats;
        if (g.Count == 0) return r;
        var baseN = rec.GuiChatBase;
        if (baseN > 0 && baseN <= r.Count)
        {
            // 快照区一致性校验（桌面未压缩/改写旧轮）通过才可安全追加；否则保守回副本视图（不丢不重）
            if (g.Count < baseN || !PrefixEqual(g, 0, r, 0, baseN)) return r;
            // 跳过与手机续聊轮次（R[baseN..]）相同的前段：桌面启动合并过收件箱时该段已并入 GUI 文件
            var k = PrefixEqualLen(g, baseN, r, baseN, Math.Min(g.Count - baseN, r.Count - baseN));
            var tail = g.Skip(baseN + k).ToList();
            return tail.Count == 0 ? r : r.Concat(ToMsgs(tail)).ToList();
        }
        // 旧副本（无基线）启发：① GUI 以副本为前缀且更长 → GUI 即最新全量；
        // ② 副本 = GUI 头部 p 条 + 尾部 q 条拼接（中段被桌面侧整理/重放改写）→ 同样以 GUI 全量为准
        if (g.Count >= r.Count && PrefixEqual(g, 0, r, 0, r.Count)) return ToMsgs(g);
        var p = PrefixEqualLen(g, 0, r, 0, Math.Min(g.Count, r.Count));
        var q = r.Count - p;
        if (p > 0 && q > 0 && q <= g.Count && g.Count > r.Count && PrefixEqual(g, g.Count - q, r, p, q))
            return ToMsgs(g);
        return r;
    }

    /// <summary>旧副本补基线（resume 时调用）：桌面当前对话与副本逐条相同 → 副本即纯桌面快照（无手机轮），
    /// 记录基线后桌面后续新增即可被 MergeGuiTail 追踪。返回是否已补齐。</summary>
    public static bool TryBackfillGuiBase(SessionRecord rec, List<(string Role, string Content)> guiChats)
    {
        if (rec.GuiChatBase > 0) return false;
        var r = ChatMsgs(rec);
        if (guiChats.Count != r.Count || !PrefixEqual(guiChats, 0, r, 0, r.Count)) return false;
        rec.GuiChatBase = r.Count;
        return true;
    }

    /// <summary>副本对话序列（与 ChatCount 同口径过滤：user/assistant 且正文非空）</summary>
    static List<SessionMessage> ChatMsgs(SessionRecord rec) => (rec.Messages ?? new List<SessionMessage>())
        .Where(m => m.Role is "user" or "assistant" && !string.IsNullOrWhiteSpace(m.Content))
        .ToList();

    static bool PrefixEqual(List<(string Role, string Content)> g, int go, List<SessionMessage> r, int ro, int n)
    {
        for (int i = 0; i < n; i++)
            if (g[go + i].Role != r[ro + i].Role || g[go + i].Content != r[ro + i].Content) return false;
        return true;
    }

    static int PrefixEqualLen(List<(string Role, string Content)> g, int go, List<SessionMessage> r, int ro, int max)
    {
        int i = 0;
        while (i < max && g[go + i].Role == r[ro + i].Role && g[go + i].Content == r[ro + i].Content) i++;
        return i;
    }

    static List<SessionMessage> ToMsgs(IEnumerable<(string Role, string Content)> list) =>
        list.Select(t => new SessionMessage { Role = t.Role, Content = t.Content }).ToList();

    // ---- 底层：字节读取（share ReadWrite|Delete 兼容 GUI File.Replace）/ BOM / 流式元素 ----

    static byte[]? ReadGuiBytes(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            return ms.ToArray();
        }
        catch
        {
            return null;   // 读取瞬间被替换/占用：调用方走缓存或失败兜底
        }
    }

    static int BomLen(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;

    // ---- 会话头列表：整文件轻扫（Messages 等大值一律 Skip，不建 DOM）----
    static List<GuiSessionHead>? ScanHeads(string guiFile)
    {
        try
        {
            var bytes = ReadGuiBytes(guiFile);
            if (bytes == null) return null;
            var reader = new Utf8JsonReader(bytes.AsSpan(BomLen(bytes)));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray) return null;
            var list = new List<GuiSessionHead>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray) break;
                if (reader.TokenType != JsonTokenType.StartObject) continue;
                var h = ParseHeadOnly(ref reader);
                if (h != null) list.Add(h);
            }
            return list;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>浅层读对象头部字段（Messages/其它嵌套一律 Skip）：reader 位于 StartObject，读完停在 EndObject</summary>
    static GuiSessionHead? ParseHeadOnly(ref Utf8JsonReader reader)
    {
        string id = "", project = "", title = "", created = "", time = "";
        bool isOrch = false;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            var name = reader.GetString();
            if (!reader.Read()) return null;   // 读到属性名无值：损坏元素
            switch (name)
            {
                case "Id": id = reader.GetString() ?? ""; break;
                case "Project": project = reader.GetString() ?? ""; break;
                case "Title": title = reader.GetString() ?? ""; break;
                case "Created": created = reader.GetString() ?? ""; break;
                case "Time": time = reader.GetString() ?? ""; break;
                case "IsOrchestration":
                    if (reader.TokenType == JsonTokenType.True) isOrch = true;
                    break;
                default: reader.Skip(); break;   // Messages / MultiModels / 其它：值整体跳过
            }
        }
        if (id.Length == 0) return null;
        if (string.IsNullOrEmpty(created) || created.StartsWith("0001-01-01"))
            created = time;   // 旧会话无 Created → GUI 侧同规则用 Time 兜底
        return new GuiSessionHead
        {
            Id = id,
            Project = project,
            Title = title,
            Created = DateTime.TryParse(created, out var dt) ? dt : DateTime.MinValue,
            IsOrchestration = isOrch,
            Time = DateTime.TryParse(time, out var t) ? t : null,
        };
    }

    /// <summary>探测元素头部 Id（复制 reader 只读 Id 属性，不推进主 reader）；异常结构 → false</summary>
    static bool ProbeId(ref Utf8JsonReader reader, out string id)
    {
        id = "";
        var probe = reader;   // Utf8JsonReader 是 struct：复制即独立游标
        try
        {
            while (probe.Read())
            {
                if (probe.TokenType == JsonTokenType.PropertyName && probe.ValueSpan.SequenceEqual("Id"u8))
                {
                    if (!probe.Read()) return false;
                    id = probe.GetString() ?? "";
                    return true;
                }
                if (probe.TokenType == JsonTokenType.PropertyName && probe.ValueSpan.SequenceEqual("Messages"u8))
                    return false;   // Id 应在 Messages 前（GUI 投影顺序固定），越过仍未见 → 异常结构
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>匹配元素（DOM 单元素，含 Messages 全量）→ 完整提取</summary>
    static GuiSessionFull FromElement(JsonElement root)
    {
        var h = new GuiSessionHead
        {
            Id = Prop(root, "Id") ?? "",
            Project = Prop(root, "Project") ?? "",
            Title = Prop(root, "Title") ?? "",
            IsOrchestration = root.TryGetProperty("IsOrchestration", out var o) && o.ValueKind == JsonValueKind.True,
        };
        var created = Prop(root, "Created") ?? "";
        var time = Prop(root, "Time") ?? "";
        if (string.IsNullOrEmpty(created) || created.StartsWith("0001-01-01")) created = time;
        h.Created = DateTime.TryParse(created, out var dt) ? dt : DateTime.MinValue;
        h.Time = DateTime.TryParse(time, out var t) ? t : null;
        var full = new GuiSessionFull { Head = h };
        if (root.TryGetProperty("Messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array)
        {
            h.RawMsgCount = msgs.GetArrayLength();
            foreach (var m in msgs.EnumerateArray())
            {
                var kind = Prop(m, "kind");
                var text = Prop(m, "text");
                if (kind is "User" or "Agent" && !string.IsNullOrWhiteSpace(text))
                    full.Chats.Add((kind == "User" ? "user" : "assistant", text!));
            }
        }
        return full;
    }

    static string? Prop(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
