using System.Text;
using GAIRR.AgentHost;
using GAIRR.Core;

// gairr-cli：GAIRR Agent 命令行宿主（headless 验证 / 脚本集成）
// 用法：gairr-cli --project <项目根> [--config <config.ini>] [--policy Allow|Deny|Ask] [--tools 白名单] --ask "任务文本"
Console.OutputEncoding = Encoding.UTF8;

string? project = null, config = null, ask = null, toolsArg = null, policy = null;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--project" or "-p" when i + 1 < args.Length: project = args[++i]; break;
        case "--config" or "-c" when i + 1 < args.Length: config = args[++i]; break;
        case "--ask" or "-a" when i + 1 < args.Length: ask = args[++i]; break;
        case "--tools" or "-t" when i + 1 < args.Length: toolsArg = args[++i]; break;
        case "--policy" when i + 1 < args.Length: policy = args[++i]; break;
        case "--help" or "-h":
            Console.WriteLine("用法：gairr-cli --project <项目根> [--config <config.ini>] [--policy Allow|Deny|Ask] [--tools a,b,c] --ask \"任务文本\"");
            return 0;
        default:
            ask ??= args[i];   // 位置参数 = 任务文本
            break;
    }
}
if (project is null || ask is null)
{
    Console.WriteLine("缺少参数：--project 与任务文本必填（gairr-cli --help 看用法）");
    return 2;
}
if (policy is not null) SystemCfg.DangerPolicy = policy;

var session = new AgentSession("cli", project, config);
var ns = SessionStore.NormalizeNamespace(session.ProjectRoot);
if (session.Store.Load(ns, session.Id) is { } saved && saved.Title.Length > 0)
    Console.WriteLine($"[会话] 恢复历史 {saved.Messages.Count} 条 · 标题：{saved.Title}");
Console.WriteLine($"[项目] {session.ProjectRoot}");
Console.WriteLine($"[任务] {ask}");
Console.WriteLine($"[危险策略] {SystemCfg.DangerPolicy}");

var req = new TaskRequest { Text = ask };
if (toolsArg is not null)
    req.Tools = toolsArg.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); Console.WriteLine("\n[取消] 正在中止…"); };

TaskResult? result = null;
try
{
    var task = session.StartAsync(req, cts.Token);
    while (!task.IsCompleted)
    {
        PrintEvents(session.DrainEvents());
        // Ask 策略：危险命令等待外部决策 —— 交互式 y/N（脚本管道场景用 --policy Allow/Deny 或 /decisions 接口）
        if (session.State == "pendingDecision" && !Console.IsInputRedirected)
        {
            Console.Write("[危险] 允许执行该命令？(y/N): ");
            var ans = Console.ReadLine();
            session.DecideDanger(ans?.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) == true);
        }
        await Task.Delay(80);
    }
    PrintEvents(session.DrainEvents());   // 排空残留事件
    result = await task;
}
catch (OperationCanceledException)
{
    Console.WriteLine("[取消] 任务已被取消");
}
catch (Exception ex)
{
    Console.WriteLine($"[错误] {ex.Message}");
    return 1;
}

Console.WriteLine();
Console.WriteLine("───── 任务结果 ─────");
Console.WriteLine(result!.Reply);
if (result.ChangedFiles.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("修改的文件：");
    foreach (var f in result.ChangedFiles) Console.WriteLine("  • " + f);
}
Console.WriteLine($"tokens: {result.Tokens} · 标题: {result.Title}");
return 0;

/// <summary>按事件类型输出进度（与 MainWindow 时间线同构的文本化）</summary>
static void PrintEvents(List<SessionEventDto> events)
{
    foreach (var e in events)
    {
        switch (e.Type)
        {
            case "Started":
                Console.WriteLine("◌ 任务开始");
                break;
            case "WaitingModel":
                Console.WriteLine("◌ 思考中…");
                break;
            case "ToolStart":
                Console.WriteLine($"⚙ [{e.Round}] {e.Tool?.Title} {e.Tool?.Status}");
                break;
            case "ToolUpdate":
                if (e.Tool?.Inner is { Length: > 0 } inner) Console.WriteLine($"    ↳ {inner}");
                break;
            case "Round":
                Console.WriteLine($"— 第 {e.Round} 轮完成 · 本轮 {e.Usage?.Total ?? 0} tokens · 累计 {e.TotalTokens}");
                break;
            case "StreamDelta":
                if (e.Start) Console.WriteLine();
                Console.Write(e.Delta);
                break;
            case "Summary":
                Console.WriteLine($"[会话标题] {e.Reasoning}");
                break;
            case "SecurityAlert":
                Console.WriteLine(e.Alert?.Level == "Confirm"
                    ? $"⚠ [危险·待确认] {e.Alert?.Command}（命中模式：{e.Alert?.Pattern}）"
                    : $"⛔ [危险·已拦截] {e.Alert?.Command}");
                break;
            case "DangerTimeout":
                Console.WriteLine("⛔ [危险] 决策超时（60 秒），已自动拒绝");
                break;
            case "Todo":
                Console.WriteLine($"📋 [计划] {TodoText(e.Todo)}");
                break;
            case "Finished":
                Console.WriteLine($"\n✅ 完成 · 累计 {e.TotalTokens} tokens");
                break;
            case "Failed":
                Console.WriteLine($"\n❌ 任务失败：{e.LogLine}");
                break;
            case "Log":
                if (e.LogLine is { Length: > 0 } l) Console.WriteLine($"  · {l}");
                break;
            case "DocStatus":
                if (e.LogLine is { Length: > 0 } d) Console.WriteLine($"[地图] {d}");
                break;
        }
    }
}

static string TodoText(TodoUpdate? t)
{
    if (t == null) return "";
    return t.Action switch
    {
        "create" => $"创建 {t.Steps?.Count ?? 0} 步：{string.Join(" / ", t.Steps ?? new())}",
        "done_all" => "全部完成",
        "update" => $"步骤 {t.Index} {(t.Done ? "完成" : "进行中")}：{t.Text}",
        _ => t.Action,
    };
}