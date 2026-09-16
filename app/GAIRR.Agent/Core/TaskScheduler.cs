namespace GAIRR.Core;

/// <summary>自动任务调度器：后台轮询 tasks.json，到期后交给 TaskRunner 执行。</summary>
public class TaskScheduler : IDisposable
{
    readonly AppConfig cfg;
    readonly TaskStore store;
    readonly TaskRunner runner;
    readonly Timer timer;
    readonly Dictionary<string, Task> running = new();
    readonly object gate = new();
    bool disposed;

    /// <summary>轮询间隔，默认 30 秒。</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);

    public TaskScheduler(AppConfig cfg)
    {
        this.cfg = cfg;
        store = new TaskStore(cfg);
        runner = new TaskRunner(cfg, store);
        timer = new Timer(_ => Tick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>启动调度器。</summary>
    public void Start()
    {
        if (disposed) return;
        timer.Change(TimeSpan.Zero, Interval);
    }

    /// <summary>停止调度器，不再触发新任务。</summary>
    public void Stop()
    {
        if (disposed) return;
        timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>立即手动触发一次轮询（测试/启动时用）。</summary>
    public void TickOnce() => Tick();

    /// <summary>等待当前正在执行的任务完成（最多 wait 时长）。</summary>
    public async Task WaitForRunningAsync(TimeSpan wait)
    {
        List<Task> tasks;
        lock (gate)
        {
            tasks = running.Values.ToList();
        }
        if (tasks.Count == 0) return;
        try
        {
            await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(wait));
        }
        catch { }
    }

    void Tick()
    {
        if (disposed) return;
        try
        {
            var now = DateTime.Now;
            var jobs = store.GetDueJobs(now);
            foreach (var job in jobs)
            {
                lock (gate)
                {
                    if (running.ContainsKey(job.Id)) continue; // 同任务并发跳过
                }
                _ = RunJobAsync(job);
            }
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(Paths.AgentLog, $"[{DateTime.Now:HH:mm:ss.fff}] [TaskScheduler] Tick 异常：{ex}\n"); }
            catch { }
        }
    }

    async Task RunJobAsync(TaskItem job)
    {
        lock (gate) { running[job.Id] = Task.CompletedTask; }
        try
        {
            // 更新下次执行时间（Cron 才需要；Once 触发后禁用）
            var tasks = store.LoadTasks();
            var current = tasks.FirstOrDefault(t => t.Id == job.Id);
            if (current != null)
            {
                if (current.TriggerMode == TaskTriggerMode.Cron && !string.IsNullOrWhiteSpace(current.CronExpression))
                {
                    try { current.NextRunTime = new SimpleCron(current.CronExpression).GetNextOccurrence(DateTime.Now); }
                    catch { current.NextRunTime = null; }
                }
                else if (current.TriggerMode == TaskTriggerMode.Once)
                {
                    current.Enabled = false;
                    current.NextRunTime = null;
                }
                current.UpdatedAt = DateTime.Now;
                store.SaveTasks(tasks);
            }

            var run = await runner.RunAsync(job);
            try
            {
                File.AppendAllText(Paths.AgentLog,
                    $"[{DateTime.Now:HH:mm:ss.fff}] [TaskScheduler] 任务 {job.Name}({job.Id}) 执行完成：{run.Status}，摘要：{run.Summary}\n");
            }
            catch { }
        }
        catch (Exception ex)
        {
            try
            {
                File.AppendAllText(Paths.AgentLog,
                    $"[{DateTime.Now:HH:mm:ss.fff}] [TaskScheduler] 任务 {job.Name}({job.Id}) 执行异常：{ex.Message}\n");
            }
            catch { }
        }
        finally
        {
            lock (gate) { running.Remove(job.Id); }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Stop();
        timer.Dispose();
    }
}
