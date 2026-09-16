using System.Text.Json.Nodes;
using GAIRR.AgentHost;

namespace GAIRR.Core;

/// <summary>任务执行器：负责把 TaskItem 交给 AgentLoop 执行，并生成 TaskRunRecord。</summary>
public class TaskRunner
{
    readonly AppConfig cfg;
    readonly TaskStore store;

    public TaskRunner(AppConfig cfg, TaskStore store)
    {
        this.cfg = cfg;
        this.store = store;
    }

    /// <summary>执行指定任务并持久化运行记录；返回运行记录。</summary>
    public async Task<TaskRunRecord> RunAsync(TaskItem task, CancellationToken ct = default)
    {
        var run = new TaskRunRecord
        {
            RunId = TaskStore.NewId(),
            TaskId = task.Id,
            TaskName = task.Name,
            StartedAt = DateTime.Now,
            Status = TaskRunStatus.Running,
            LogPath = Path.Combine(store.AppDataDir, $"run_{DateTime.Now:yyyyMMddHHmmss}_{task.Id}.log"),
        };
        store.AppendRun(run);

        // 进入自动任务危险命令自动放行作用域（无人值守不能人工确认）
        using var _ = AgentLoop.AutoAllowDangerScope();

        var session = new AgentSession(task.Id, task.ProjectPath);
        try
        {
            var prompt = string.IsNullOrWhiteSpace(task.SkillName)
                ? task.Description
                : $"/{task.SkillName.Trim()} {task.Description}";

            var req = new TaskRequest { Text = prompt };
            var result = await session.StartAsync(req, ct);

            run.FinishedAt = DateTime.Now;
            run.Status = session.Loop.LastRunSuccess ? TaskRunStatus.Success : TaskRunStatus.Failed;
            run.Rounds = GetRounds(session.Loop);
            run.Tokens = result.Tokens;
            run.Summary = result.Reply.Length > 0
                ? result.Reply[..Math.Min(result.Reply.Length, 200)].Replace("\n", " ")
                : (run.Status == TaskRunStatus.Success ? "已完成" : "无结果");
            run.Error = session.Loop.LastFailReason;

            // 保存本次运行日志副本
            try
            {
                File.WriteAllText(run.LogPath, await File.ReadAllTextAsync(Paths.AgentLog, ct));
            }
            catch { run.LogPath = ""; }
        }
        catch (OperationCanceledException)
        {
            run.FinishedAt = DateTime.Now;
            run.Status = TaskRunStatus.Cancelled;
            run.Summary = "任务被取消";
        }
        catch (Exception ex)
        {
            run.FinishedAt = DateTime.Now;
            run.Status = TaskRunStatus.Failed;
            run.Summary = $"执行异常：{ex.Message}";
            run.Error = ex.Message;
        }
        finally
        {
            store.AppendRun(run);
        }

        return run;
    }

    static int GetRounds(AgentLoop loop) => loop.RoundCount;
}
