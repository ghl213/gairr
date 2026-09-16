using System.Windows;

namespace GAIRR;

public partial class App : Application
{
    /// <summary>
    /// CLI 退出码：0=成功，1=失败，2=取消/超时。
    /// 字段保持静态，供非主线程写入后由 Shutdown 前统一读取。
    /// </summary>
    static int cliExitCode = 0;
    static bool cliShutdownRequested;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 命令行批处理模式：GAIRR.exe -t "任务描述" [-p 项目路径] [-s 技能名] [-n 任务名]
        var args = e.Args;
        // 命令行执行计划模式：GAIRR.exe -plan <计划id> [-p 项目路径]（无 UI 端到端执行 1-4 编排闭环）
        var planIdx = Array.IndexOf(args, "-plan");
        if (planIdx >= 0 && planIdx + 1 < args.Length)
        {
            _ = RunPlanCliAsync(args[planIdx + 1], GetArg(args, "-p"));
            return; // 不启动 WPF 窗口
        }
        var taskIdx = Array.IndexOf(args, "-t");
        if (taskIdx >= 0 && taskIdx + 1 < args.Length)
        {
            var task = args[taskIdx + 1];
            var project = GetArg(args, "-p");
            var skill = GetArg(args, "-s");
            var name = GetArg(args, "-n");
            _ = RunCliAsync(task, project, skill, name);
            return; // 不启动 WPF 窗口
        }
        base.OnStartup(e);
        // 无 CLI 参数：直接进入主窗口（登录门禁已停用，LoginWindow 保留备用）；
        // 启动匿名统计：本地按天计时 + 后台上报服务器并做版本检查（失败不阻塞）。
        // 退出策略以主窗口为准：网页模型通道是独立窗口、关闭时只隐藏，
        // 若沿用"最后窗口关闭"会导致主窗口关了进程仍残留。
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        var mw = new MainWindow();
        MainWindow = mw;
        mw.Show();
        Exit += (_, __) => WebChannelHost.ShutdownAll();
        UsageTracker.Start(mw);
    }

    /// <summary>取指定参数的下一个值；不存在返回 null。</summary>
    static string? GetArg(string[] args, string key)
    {
        var idx = Array.IndexOf(args, key);
        return (idx >= 0 && idx + 1 < args.Length) ? args[idx + 1] : null;
    }

    async Task RunCliAsync(string task, string? project, string? skill, string? name)
    {
        var cfg = new Core.AppConfig();
        if (!string.IsNullOrWhiteSpace(project))
            cfg.ProjectRoot = System.IO.Path.GetFullPath(project);

        var registry = new Core.ToolRegistry();
        Core.SystemCfg.Init();
        Core.Phase1Tools.RegisterAll(registry, cfg);
        var journal = new Core.ChangeJournal(cfg);
        Core.Phase2Tools.RegisterAll(registry, cfg, journal);
        var skills = new Core.SkillLoader(cfg);
        var loop = new Core.AgentLoop(cfg, registry, journal, skills);

        // 技能注入：-s 指定时，在任务文本前拼 /技能名，让 AgentLoop 走技能 system prompt
        var prompt = string.IsNullOrWhiteSpace(skill) ? task : $"/{skill.Trim()} {task}";
        var displayName = string.IsNullOrWhiteSpace(name) ? "CLI任务" : name.Trim();

        // CLI 模式：后台线程消费事件总线，任务完成后输出结果并设置退出码
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(100);
                foreach (var ev in loop.Bus.Drain())
                {
                    switch (ev.Type)
                    {
                        case Core.UiEventType.ToolStart:
                            if (ev.Tool != null) Console.WriteLine($"[工具] {ev.Tool.Title} …");
                            break;
                        case Core.UiEventType.ToolUpdate:
                            if (ev.Tool != null) Console.WriteLine($"[工具] {ev.Tool.Title} {ev.Tool.Status}");
                            break;
                        case Core.UiEventType.Round:
                            Console.WriteLine($"[第 {ev.Round} 轮] token={ev.Usage?.Total ?? 0}");
                            break;
                        case Core.UiEventType.StreamDelta:
                            // 结论全文已在 Finished 时统一输出，流式增量不再打印（避免重复）
                            break;
                        case Core.UiEventType.SecurityAlert:
                            if (ev.Alert != null)
                            {
                                var label = ev.Alert.Level == "Confirm" ? "[安全] 用户已取消" : "[安全] 危险命令已拦截";
                                Console.WriteLine($"{label}: {ev.Alert.Command}\n原因：{ev.Alert.Pattern}");
                            }
                            break;
                        case Core.UiEventType.Finished:
                            Console.WriteLine($"[完成] 共 {ev.Round} 轮，累计 {ev.TotalTokens} token");
                            var reply = loop.GetLastAssistantMessage();
                            if (!string.IsNullOrEmpty(reply))
                                Console.WriteLine("\n" + reply);
                            cliExitCode = 0;
                            _ = Dispatcher.BeginInvoke(new Action(SafeShutdown));
                            return;
                        case Core.UiEventType.Failed:
                            Console.WriteLine($"[失败] {(!string.IsNullOrWhiteSpace(ev.Reasoning) ? ev.Reasoning : "任务异常终止")}");
                            cliExitCode = 1;
                            _ = Dispatcher.BeginInvoke(new Action(SafeShutdown));
                            return;
                    }
                }
            }
        });

        try
        {
            Console.WriteLine($"[概尔 Agent CLI] 任务：{displayName}");
            if (!string.IsNullOrWhiteSpace(project))
                Console.WriteLine($"[项目] {cfg.ProjectRoot}");
            if (!string.IsNullOrWhiteSpace(skill))
                Console.WriteLine($"[技能] {skill.Trim()}");
            await loop.RunAsync(prompt, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            cliExitCode = 2;
            Console.WriteLine("[取消] 任务被取消或超时");
        }
        catch (Exception ex)
        {
            cliExitCode = 1;
            Console.WriteLine($"[异常] {ex.Message}");
        }
        finally
        {
            Environment.ExitCode = cliExitCode;
            // 若事件循环尚未触发 Shutdown（极端情况），确保进程退出
            _ = Dispatcher.BeginInvoke(new Action(SafeShutdown));
        }
    }

    /// <summary>无 UI 执行指定计划：PlanRunner 串行跑叶子 → 审查 → 下一叶；日志直接输出到控制台。</summary>
    async Task RunPlanCliAsync(string planId, string? project)
    {
        var cfg = new Core.AppConfig();
        if (!string.IsNullOrWhiteSpace(project))
            cfg.ProjectRoot = System.IO.Path.GetFullPath(project);
        var runner = new AgentHost.PlanRunner(cfg.ProjectRoot, Console.WriteLine);
        Console.WriteLine($"[概尔 Agent CLI -plan] 计划：{planId}  项目：{cfg.ProjectRoot}");
        try
        {
            var ok = await runner.RunAsync(planId);
            cliExitCode = ok ? 0 : 1;
            Console.WriteLine(ok ? "[完成] 计划全部叶子执行通过。" : "[未完成] 计划中止：存在失败叶子，可用 -plan 重跑续传或人工裁决后重跑。");
        }
        catch (Exception ex)
        {
            cliExitCode = 1;
            Console.WriteLine($"[异常] {ex.Message}");
        }
        finally
        {
            Environment.ExitCode = cliExitCode;
            _ = Dispatcher.BeginInvoke(new Action(SafeShutdown));
        }
    }

    static void SafeShutdown()
    {
        if (cliShutdownRequested) return;
        cliShutdownRequested = true;
        try { Current.Shutdown(); }
        catch { /* 已退出或重复关闭时忽略 */ }
    }
}
