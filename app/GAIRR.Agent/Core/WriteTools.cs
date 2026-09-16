using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GAIRR.Core;

/// <summary>阶段二写类/检索工具：Write / Edit / Grep / Glob（写操作强制走 ChangeJournal 备份+日志）</summary>
public static class Phase2Tools
{
    public static void RegisterAll(ToolRegistry reg, AppConfig cfg, ChangeJournal journal)
    {
        reg.Register("Write", ToolRegistry.Fn(
            "Write",
            SystemCfg.Desc("Write",
                "创建新文件或整文件覆盖写入（UTF-8 无 BOM）。已有文件会先自动备份。大段修改优先用 Edit，全文重写才用 Write。"),
            new JsonObject
            {
                ["path"] = ToolRegistry.Str("文件路径（绝对或相对项目根）"),
                ["content"] = ToolRegistry.Str("完整文件内容"),
                ["summary"] = ToolRegistry.Str("必填：一句话说明这次改动的目的"),
                ["line_ending"] = ToolRegistry.Str("换行符：crlf / lf / auto，默认 auto。auto 时会保留原文件风格，或按扩展名自动选择"),
            },
            new JsonArray { "path", "content", "summary" }),
            (a, ct) => Task.FromResult(Write(cfg, journal, a)));

        reg.Register("Edit", ToolRegistry.Fn(
            "Edit",
            SystemCfg.Desc("Edit",
                "对已有文件做局部替换：old_text 必须与文件内容逐字符一致且全文唯一（先 Read 再 Edit）。框架自动备份并记变更日志。"),
            new JsonObject
            {
                ["path"] = ToolRegistry.Str("文件路径"),
                ["old_text"] = ToolRegistry.Str("要被替换的原文（含足够上下文保证唯一）"),
                ["new_text"] = ToolRegistry.Str("替换后的新文本"),
                ["summary"] = ToolRegistry.Str("必填：一句话说明这次改动的目的"),
            },
            new JsonArray { "path", "old_text", "new_text", "summary" }),
            (a, ct) => Task.FromResult(Edit(cfg, journal, a)));

        reg.Register("Grep", ToolRegistry.Fn(
            "Grep",
            SystemCfg.Desc("Grep",
                "用正则表达式在文件或目录中搜索内容，返回 路径:行号:行内容。定位代码位置优先用它。path 参数不确定时，先用 Map 浏览目录、SmartSearch q=关键词 语义搜索、或 MapTrace q=类名/方法名 符号检索确认正确路径，不要凭记忆猜测。"),
            new JsonObject
            {
                ["pattern"] = ToolRegistry.Str("正则表达式，如 OnSend|class\\s+\\w+"),
                ["path"] = ToolRegistry.Str("文件或目录路径，可为空（空=项目根目录）"),
                ["ignore_case"] = new JsonObject { ["type"] = "boolean", ["description"] = "忽略大小写，默认 false" },
            },
            new JsonArray { "pattern" }),
            (a, ct) => Task.FromResult(Grep(cfg, a)));

        reg.Register("Glob", ToolRegistry.Fn(
            "Glob",
            SystemCfg.Desc("Glob",
                "按通配符（* ?）查找文件路径清单，如 *.cs 或 src/*.js。只返回路径，不读内容。path 参数不确定时，先用 Map 浏览目录结构确认。"),
            new JsonObject
            {
                ["pattern"] = ToolRegistry.Str("通配符模式，如 *.md 或 Core/*.cs"),
                ["path"] = ToolRegistry.Str("搜索起始目录，可为空（空=项目根目录）"),
            },
            new JsonArray { "pattern" }),
            (a, ct) => Task.FromResult(Glob(cfg, a)));

        // 待办清单工具：模型任务开始时拆分步骤并逐项勾选（UI 端 AgentLoop 另行转发 Todo 事件）
        reg.Register("UpdateTodo", ToolRegistry.Fn(
            "UpdateTodo",
            SystemCfg.Desc("UpdateTodo",
                "任务待办清单：任务开始时用 create 把任务拆成中文步骤（步数按复杂度定，简单任务 1~2 步即可，每步 ≤200 字）；步骤完成勾选由框架自动处理，一般无需手动 update。"),
            new JsonObject
            {
                ["action"] = ToolRegistry.Str("create=创建/覆盖清单；update=更新某步完成状态；done_all=标记全部完成"),
                ["steps"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = ToolRegistry.Str("步骤描述，不超过 200 字"),
                    ["description"] = "create 时必填：步数按任务复杂度定（简单任务 1~2 步）",
                },
                ["index"] = ToolRegistry.Int("update 时必填：步骤序号（1 起）；勾选通常由框架自动完成，无需手动调用"),
                ["done"] = new JsonObject { ["type"] = "boolean", ["description"] = "update 时是否已完成，默认 true" },
            },
            new JsonArray { "action" }),
            (a, ct) => Task.FromResult(HandleTodo(a)));
    }

    /// <summary>UpdateTodo 参数校验并返回简短确认文本（回喂模型）</summary>
    static string HandleTodo(JsonObject a)
    {
        var action = (a["action"]?.GetValue<string>() ?? "").Trim();
        switch (action)
        {
            case "create":
                if (a["steps"] is not JsonArray arr) return "错误：create 需要 steps 数组";
                if (arr.Count < SystemCfg.PlanMinSteps || arr.Count > 10) return $"错误：步骤数需 {SystemCfg.PlanMinSteps}~10 条（按任务复杂度定，简单任务少拆）";
                foreach (var s in arr)
                {
                    var t = s?.GetValue<string>()?.Trim() ?? "";
                    if (t.Length == 0) return "错误：步骤不能为空";
                    if (t.Length > 200) return "错误：单步骤不能超过 200 字";
                }
                return $"已创建待办清单（{arr.Count} 步），每完成一步用 update 勾选";
            case "update":
                var idx = a["index"]?.GetValue<int>() ?? 0;
                if (idx < 1) return "错误：index 需 >= 1";
                var done = a["done"]?.GetValue<bool>() ?? true;
                return $"已更新第 {idx} 步为{(done ? "完成" : "未完成")}";
            case "done_all":
                return "待办清单已全部完成";
            default:
                return "错误：action 仅支持 create/update/done_all";
        }
    }

    /* ---------- Write：备份 → 写 UTF-8 无 BOM → 日志 ---------- */

    /// <summary>写文件成功后通知检查官：算出项目根内相对路径并触发增量维护（失败静默）。</summary>
    static void NotifyIndexGuardian(AppConfig cfg, string path)
    {
        try
        {
            var root = cfg.ProjectRoot;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
            var full = Path.GetFullPath(path);
            var rootFull = Path.GetFullPath(root);
            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return;
            var rel = Path.GetRelativePath(rootFull, full);
            IndexGuardian.NotifyFile(rootFull, rel);
            IndexGuardian.EnsureStarted(rootFull);
        }
        catch { }
    }

    static string Write(AppConfig cfg, ChangeJournal j, JsonObject a)
    {
        var path = Phase1Tools.Resolve(cfg, a["path"]?.GetValue<string>() ?? "");
        var content = a["content"]?.GetValue<string>();
        var summary = (a["summary"]?.GetValue<string>() ?? "").Trim();
        var lineEnding = (a["line_ending"]?.GetValue<string>() ?? "auto").Trim().ToLowerInvariant();
        if (path.Length == 0) return "错误：path 为空";
        if (content == null) return "错误：content 为空";
        if (summary.Length == 0) return "错误：summary 为必填参数，请用一句话说明这次改动的目的";

        var mutex = FileLock.Acquire(path, LockWaitReporter(path), CurrentLockHolder());   // 并发会话写文件锁：排队等待（永不超时），等持锁会话完成后继续；等待中经回调上报（无执行 Loop 时无事件=原行为）
        try
        {
            // 整文件覆盖前校验快照：文件自上次 Read 后被改过（其它会话/进程/外部编辑），
            // 盲目覆盖会冲掉新内容；要求重新 Read 确认当前内容后再写
            if (File.Exists(path) && FileSnapshot.ChangedSinceRead(path))
                return "错误：文件自上次 Read 后已被修改（可能被其它会话/进程写入）。为避免用旧内容覆盖新改动，"
                     + "请先重新 Read 该文件确认当前内容，再视情况用 Write 覆盖或改用 Edit 局部修改：" + path;

            content = NormalizeLineEnding(path, content, lineEnding);

            var backup = j.BackupBeforeWrite(path);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            FileSnapshot.Record(path);   // 写后刷新快照基线：本会话后续 Edit 不再误判并发改动
            j.Log("Write", path, backup, summary);
            j.WriteCount++;   // 任务级写计数：供钉钉按需通知判定"有真实修改动作"
            NotifyIndexGuardian(cfg, path);   // 写后同步通知检查官：增量维护符号/向量索引

            var lines = content.Split('\n').Length;
            var warn = lines > cfg.MaxFileLines
                ? $"（提示：{lines} 行超过单文件上限 {cfg.MaxFileLines} 行，建议拆分）" : "";
            return $"已写入 {path}（{lines} 行，UTF-8 无 BOM）" +
                   (backup.Length > 0 ? "，原文件已备份：" + Path.GetFileName(backup) : "（新建文件）") + warn;
        }
        finally { mutex.ReleaseMutex(); FileLock.Unregister(path, CurrentLockHolder()); }   // 释放同时注销占用登记（条件删除：不误删已接手的新占用者）
    }

    /* ---------- 文件锁等待上报：排队等锁时经当前 Loop 上送 LockWait 事件（只上报不占锁语义，无 Loop=不产出事件） ---------- */

    /// <summary>当前占用者描述（会话标题优先，会话 Key 兜底）：Acquire 登记使其它会话排队时可见"占用者"。</summary>
    static string? CurrentLockHolder()
    {
        var loop = AgentLoop.CurrentToolLoop;
        if (loop == null) return null;
        var name = string.IsNullOrWhiteSpace(loop.SessionTitle) ? loop.SessionKey : loop.SessionTitle;
        return name.Length > 0 ? name : null;
    }

    /// <summary>排队等锁回调：elapsed>0=仍在排队（2s 节流上送：含已等待时长/占用者，长等待持续刷新不像卡死）；
    /// elapsed=0=锁到手（仅当曾上报过等待才补发恢复事件，无竞争时不产生噪音）。回调运行在等锁线程，整体包 try 防抛异常进锁循环。</summary>
    static Action<TimeSpan>? LockWaitReporter(string path)
    {
        var loop = AgentLoop.CurrentToolLoop;
        if (loop == null) return null;
        var fileName = Path.GetFileName(path);
        var reported = false;   // 是否已上报过等待：区分"无竞争"与"恢复"，无竞争零事件零噪音
        var lastReport = DateTime.MinValue;
        return elapsed =>
        {
            try
            {
                if (elapsed > TimeSpan.Zero)
                {
                    var now = DateTime.UtcNow;
                    if (reported && now - lastReport < TimeSpan.FromSeconds(2)) return;   // 节流：2s 一条，长等待持续刷新
                    reported = true;
                    lastReport = now;
                    var owner = FileLock.HolderOf(path) ?? "其它会话/进程";
                    loop.Emit(new UiEvent
                    {
                        Type = UiEventType.LockWait,
                        Waiting = true,
                        FilePath = path,
                        LogLine = $"⏳ 等待文件锁 {elapsed.TotalSeconds:F1}s：{fileName}（占用者：{owner}）",
                    });
                }
                else if (reported)
                {
                    loop.Emit(new UiEvent { Type = UiEventType.LockWait, Waiting = false, FilePath = path });
                }
            }
            catch { }
        };
    }

    /// <summary>根据 line_ending 参数及文件上下文规范化换行符（public：供 UI 手动保存等外部调用复用）</summary>
    public static string NormalizeLineEnding(string path, string content, string mode)
    {
        // 已含混合换行或显式指定时直接处理
        if (mode == "lf") return content.Replace("\r\n", "\n").Replace('\r', '\n');
        if (mode == "crlf") return content.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");

        // auto：内容本身已带 CRLF 则保持
        if (content.Contains("\r\n")) return content;

        // auto：原文件存在则跟随原风格
        if (File.Exists(path))
        {
            var old = Phase1Tools.Decode(File.ReadAllBytes(path)).Text;
            if (old.Contains("\r\n"))
                return content.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");
            return content.Replace("\r\n", "\n").Replace('\r', '\n');
        }

        // auto：新建文件按扩展名判断
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var crlfExts = new HashSet<string>
        {
            ".bat", ".cmd", ".cs", ".csproj", ".sln", ".txt", ".md", ".json", ".xml", ".config",
            ".xaml", ".resx", ".settings", ".props", ".targets", ".pubxml", ".asax", ".aspx",
            ".cshtml", ".razor", ".gitignore", ".editorconfig", ".ruleset"
        };
        if (crlfExts.Contains(ext) || Path.GetFileName(path).Equals("Dockerfile", StringComparison.OrdinalIgnoreCase))
            return content.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");

        // 其余默认 LF
        return content.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    /* ---------- Edit：CRLF 归一化 + 唯一匹配 + 失败回相近片段 ---------- */

    static string Edit(AppConfig cfg, ChangeJournal j, JsonObject a)
    {
        var path = Phase1Tools.Resolve(cfg, a["path"]?.GetValue<string>() ?? "");
        var old = a["old_text"]?.GetValue<string>();
        var neu = a["new_text"]?.GetValue<string>();
        var summary = (a["summary"]?.GetValue<string>() ?? "").Trim();
        if (path.Length == 0 || old == null || neu == null) return "错误：path/old_text/new_text 不能为空";
        if (summary.Length == 0) return "错误：summary 为必填参数，请用一句话说明这次改动的目的";
        if (!File.Exists(path))
        {
            // 写类工具不自动纠正（防改错文件），但报错附加定位候选，引导模型确认后重试
            var hit = Phase1Tools.Locate(cfg, path, out var note);
            return "错误：文件不存在 " + path
                + (hit.Length > 0 ? ";已定位到 " + hit + "，确认是目标文件请改用该路径重试" : note);
        }

        var mutex = FileLock.Acquire(path, LockWaitReporter(path), CurrentLockHolder());   // 并发会话写文件锁：排队等待（永不超时），等持锁会话完成后继续；等待中经回调上报（无执行 Loop 时无事件=原行为）
        try
        {
            var (text, _) = Phase1Tools.Decode(File.ReadAllBytes(path));
            var changed = FileSnapshot.ChangedSinceRead(path);   // 写前比对读时快照：Edit 本身已实时重读文件，故定位仍用当前内容重匹配
            var crlf = text.Contains("\r\n");
            var normText = text.Replace("\r\n", "\n");
            var normOld = old.Replace("\r\n", "\n");
            var normNew = neu.Replace("\r\n", "\n");

            var offs = MatchOffsets(normText, normOld);
 // 容错：old_text 与文件仅差缩进/行尾空白（模型抄原文时易丢缩进）→ 逐行 trim 相等且唯一即视为命中，
 // 用文件原文覆盖 normOld，避免定位失败反复重试
 if (offs.Count == 0)
 {
 var (indentAt, indentHit) = MatchIgnoreIndent(normText, normOld);
 if (indentAt >= 0 && indentHit != null) { normOld = indentHit; offs = new List<int> { indentAt }; }
 }
            var lineCount = normText.Split('\n').Length;
            if (offs.Count == 0)
            {
                var ws = WhitespaceHint(normText, normOld);
                // 定位失败 + 时间戳变过：文件功能可能已变化（其它会话改过），提示重新确认需求符合当前任务目标再继续
                var staleNote = changed
                    ? "警告：该文件自上次 Read 后已被其它会话/进程修改，且原定位内容已不存在，说明文件功能可能已有变化。"
                    + "请先重新 Read 全文确认当前实现，核实本次修改需求仍符合当前任务目标后，再重新定位并 Edit。\n"
                    : "";
                return "错误：old_text 未在文件中匹配。请先用 Read 读取目标区域，然后从 Read 返回结果中精确复制原文作为 old_text（不要包含行号前缀如 49→），再重试 Edit。\n"
                       + staleNote + ws + Candidates(normText, normOld);
            }
            if (offs.Count > 1)
                return $"错误：old_text 匹配到 {offs.Count} 处，不唯一。请在 old_text 中加入更多上下文使其唯一后重试\n"
                       + MatchLocations(normText, normOld, offs);

            var startLine = LineNoAt(normText, offs[0]);
            var result = normText.Replace(normOld, normNew);
            if (crlf) result = result.Replace("\n", "\r\n");

            var backup = j.BackupBeforeWrite(path);
            File.WriteAllText(path, result, new UTF8Encoding(false));
            FileSnapshot.Record(path);   // 写后刷新快照基线：本会话后续 Edit/Write 不再误判并发改动
            j.Log("Edit", path, backup, summary);
            j.WriteCount++;   // 任务级写计数：供钉钉按需通知判定"有真实修改动作"
            NotifyIndexGuardian(cfg, path);   // 写后同步通知检查官：增量维护符号/向量索引
            var stat = $"（行 {startLine}-{Math.Min(startLine + normOld.Split('\n').Length - 1, lineCount)}）";
            var diff = SystemCfg.EditDiff ? "\n\n" + Differ.Make(normOld, normNew, startLine, out _, out _) : "";
            return "编辑成功：" + path + stat + (backup.Length > 0 ? "（原文件已备份：" + Path.GetFileName(backup) + "）" : "") + diff;
        }
        finally { mutex.ReleaseMutex(); FileLock.Unregister(path, CurrentLockHolder()); }   // 释放同时注销占用登记（条件删除：不误删已接手的新占用者）
    }

    /// <summary>统计 old 在 text 中的全部匹配并返回各匹配起始字符偏移（供行号换算与不唯一提示）</summary>
    /// <summary>缩进容错匹配：old 与 text 逐行 trim 后相等即视为命中（用于仅差缩进/行尾空白的情形，
 /// 避免模型抄 old_text 时丢缩进导致定位失败循环）。唯一命中返回（文件起始偏移, 命中的文件原文）；
 /// 未命中或多义返回 (-1, null)——多义时不猜，回退到原有报错与相近片段提示。</summary>
 static (int at, string? hit) MatchIgnoreIndent(string text, string old)
 {
 var oldLines = old.Split((char)10);
 var tLines = text.Split((char)10);
 var startOff = new int[tLines.Length];
 var acc = 0;
 for (var i = 0; i < tLines.Length; i++) { startOff[i] = acc; acc += tLines[i].Length + 1; }
 var hits = new List<int>();
 for (var s = 0; s + oldLines.Length <= tLines.Length; s++)
 {
 var ok = true;
 for (var k = 0; k < oldLines.Length; k++)
 if (tLines[s + k].Trim() != oldLines[k].Trim()) { ok = false; break; }
 if (ok) hits.Add(s);
 }
 if (hits.Count != 1) return (-1, null);
 var st = hits[0];
 return (startOff[st], string.Join(((char)10).ToString(), tLines, st, oldLines.Length));
 }

 static List<int> MatchOffsets(string text, string old)
    {
        var list = new List<int>();
        if (old.Length == 0) return list;
        var i = 0;
        while ((i = text.IndexOf(old, i, StringComparison.Ordinal)) >= 0) { list.Add(i); i += old.Length; }
        return list;
    }

    /// <summary>字符偏移 → 行号（1 起）</summary>
    static int LineNoAt(string text, int offset)
    {
        var n = 1;
        for (var i = 0; i < offset && i < text.Length; i++) if (text[i] == '\n') n++;
        return n;
    }

    /// <summary>不唯一时列出各匹配处行号与首行内容（最多 5 处），助模型选对位置补上下文</summary>
    static string MatchLocations(string text, string old, List<int> offs)
    {
        var first = old.Split('\n')[0].Trim();
        var sb = new StringBuilder("匹配位置：");
        var k = 0;
        foreach (var o in offs)
        {
            if (k++ >= 5) { sb.Append(" …"); break; }
            sb.Append($" 行{LineNoAt(text, o)}");
        }
        if (first.Length > 0) sb.Append($"，各处首行均为「{LLMClient.Trunc(first, 60)}」");
        return sb.ToString();
    }

    /// <summary>空白差异专项提示：忽略全部空白后能唯一匹配时，说明内容一致仅换行/缩进不同，并给出文件中该区域精确原文</summary>
    static readonly Regex WsStrip = new(@"\s+", RegexOptions.Compiled);
    static string WhitespaceHint(string text, string old)
    {
        var key = WsStrip.Replace(old, "");
        var oldLines = old.Split('\n');
        if (key.Length < 8 || oldLines.Length > 100) return "";
        var lines = text.Split('\n');
        if (lines.Length > 8000 || (long)lines.Length * oldLines.Length > 200000) return "";

        // 按行窗口扫描：窗口内各行拼接后去空白与 key 比较（命中 2 处即放弃，避免误报）
        var w = oldLines.Length;
        var hits = 0; var at = -1;
        for (var i = 0; i + w <= lines.Length; i++)
        {
            if (WsStrip.Replace(string.Join('\n', lines, i, w), "") != key) continue;
            hits++; at = i;
            if (hits > 1) return "";
        }
        if (hits != 1) return "";

        var start = SumLen(lines, at);
        var len = Math.Min(old.Length + w, text.Length - start);
        var snippet = text.Substring(start, len);
        return $"提示：内容一致但换行/缩进不同。文件中该区域精确原文：\n{snippet}\n";
    }

    /// <summary>前 count 行总字符数（含行间换行符），用于行号→字符偏移换算</summary>
    static int SumLen(string[] lines, int count)
    {
        var n = 0;
        for (var i = 0; i < count && i < lines.Length; i++) n += lines[i].Length + 1;
        return n;
    }

    /// <summary>零匹配时双探针 + 滑动窗口定位最相近区域：给出对齐的 old 行与文件行，并明确差异行，助模型 1 次自纠</summary>
    static string Candidates(string text, string old)
    {
        var oldLines = old.Split('\n');
        var probes = oldLines.Select(l => l.Trim()).Where(l => l.Length > 0)
            .OrderByDescending(l => l.Length).Take(2).ToList();
        if (probes.Count == 0) return "";

        var lines = text.Split('\n');
        var hitIdx = new List<int>();
        for (var i = 0; i < lines.Length; i++)
            if (probes.Any(p => lines[i].Contains(p, StringComparison.Ordinal) ||
                                lines[i].Trim().Contains(p, StringComparison.Ordinal)))
                hitIdx.Add(i);
        if (hitIdx.Count == 0) return "";

        // 每个锚点展开一个 old 长度的窗口，按 trim 后行相等数打分，取最高分窗口
        var w = Math.Max(1, oldLines.Length);
        var bestAt = -1; var bestScore = -1;
        foreach (var s in hitIdx)
        {
            if (s + w > lines.Length) continue;
            var score = 0;
            for (var k = 0; k < w; k++)
                if (oldLines[k].Trim() == lines[s + k].Trim()) score++;
            if (score > bestScore) { bestScore = score; bestAt = s; }
        }
        if (bestAt < 0) bestAt = hitIdx[0];

        var sb = new StringBuilder("相近片段（行号 文件内容 ｜ old 对应行）：\n");
        var show = Math.Min(w, 6);
        var diffs = 0;
        for (var k = 0; k < show; k++)
        {
            var fl = bestAt + k < lines.Length ? lines[bestAt + k] : "";
            var ol = k < oldLines.Length ? oldLines[k] : "";
            var mark = fl.Trim() == ol.Trim() ? "  " : "✗ ";
            if (fl.Trim() != ol.Trim()) diffs++;
            sb.Append(mark).Append(bestAt + k + 1).Append(" ").Append(fl)
              .Append(" ｜ ").Append(ol).Append('\n');
        }
        if (w > show) sb.Append($"…（窗口共 {w} 行，余略）\n");
        sb.Append($"差异 {diffs} 行，请按文件原文修正 old_text 后重试");
        return sb.ToString();
    }

    /* ---------- Grep：正则搜内容（忽略清单 + 条数上限） ---------- */

    // 结果路径统一显示为项目根相对路径（大模型拿到的是可直接 Read 的真实路径）；
    // 文件在项目根之外（返回 .. 前缀或跨盘符）时回退为完整路径，避免歧义
    static string DisplayPath(AppConfig cfg, string file)
    {
        if (string.IsNullOrEmpty(cfg.ProjectRoot)) return file;
        var rel = Path.GetRelativePath(cfg.ProjectRoot, file);
        return rel.StartsWith("..") ? file : rel;
    }

    static string Grep(AppConfig cfg, JsonObject a)
    {
        var pattern = a["pattern"]?.GetValue<string>() ?? "";
        if (pattern.Length == 0) return "错误：pattern 为空";
        Regex re;
        try
        {
            var ic = a["ignore_case"]?.GetValue<bool>() ?? false;
            re = new Regex(pattern, ic ? RegexOptions.IgnoreCase : RegexOptions.None, TimeSpan.FromSeconds(SystemCfg.RegexTimeoutSec));
        }
        catch (Exception ex) { return "错误：正则无效 " + ex.Message; }

        var root = Phase1Tools.Locate(cfg, a["path"]?.GetValue<string>() ?? "", out var locateNote);
        var sb = new StringBuilder();
        var hits = 0;

        if (root.Length == 0) return locateNote;
        if (File.Exists(root)) { GrepFile(root, root); }
        else if (Directory.Exists(root))
        {
            foreach (var f in Phase1Tools.EnumerateFiles(root))
            {
                GrepFile(f, root);
                if (hits >= SystemCfg.GrepMaxHits) break;
            }
        }
        else return "错误：路径不存在 " + root;

        sb.Insert(0, $"（共 {hits}{(hits >= SystemCfg.GrepMaxHits ? "+" : "")} 处匹配）\n");
        return hits == 0 ? "无匹配" : LLMClient.Trunc(sb.ToString(), SystemCfg.OutputMaxChars);

        void GrepFile(string file, string baseDir)
        {
            string text;
            try { text = Phase1Tools.Decode(File.ReadAllBytes(file)).Text; } catch { return; }
            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length && hits < SystemCfg.GrepMaxHits; i++)
            {
                if (!re.IsMatch(lines[i])) continue;
                sb.Append(DisplayPath(cfg, file)).Append(':').Append(i + 1).Append(": ")
                  .Append(lines[i].Trim()).Append('\n');
                hits++;
            }
        }
    }

    /* ---------- Glob：通配符找文件 ---------- */

    static string Glob(AppConfig cfg, JsonObject a)
    {
        var pattern = a["pattern"]?.GetValue<string>() ?? "";
        if (pattern.Length == 0) return "错误：pattern 为空";
        var root = Phase1Tools.Locate(cfg, a["path"]?.GetValue<string>() ?? "", out var locateNote);
        if (root.Length == 0) return locateNote;
        if (!Directory.Exists(root)) return "错误：目录不存在 " + root + locateNote;

        // 含路径分隔符的模式按相对路径匹配，否则按文件名匹配
        var matchRel = pattern.Contains('/') || pattern.Contains('\\');
        var re = GlobToRegex(pattern);

        var sb = new StringBuilder();
        var n = 0;
        foreach (var f in Phase1Tools.EnumerateFiles(root))
        {
            var target = matchRel ? Path.GetRelativePath(root, f).Replace('\\', '/') : Path.GetFileName(f);
            if (!re.IsMatch(target)) continue;
            sb.Append(DisplayPath(cfg, f)).Append('\n');
            if (++n >= SystemCfg.GlobMaxItems) { sb.Append($"（已达 {SystemCfg.GlobMaxItems} 条上限）\n"); break; }
        }
        return n == 0 ? "无匹配文件" : $"（共 {n} 个文件）\n" + sb;
    }

    static Regex GlobToRegex(string glob)
    {
        var body = glob.Replace('\\', '/');
        var sb = new StringBuilder("^");
        foreach (var ch in body)
        {
            if (ch == '*') sb.Append(".*");
            else if (ch == '?') sb.Append('.');
            else sb.Append(Regex.Escape(ch.ToString()));
        }
        return new Regex(sb.Append('$').ToString(), RegexOptions.IgnoreCase, TimeSpan.FromSeconds(SystemCfg.RegexTimeoutSec));
    }
}
