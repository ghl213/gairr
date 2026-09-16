/// <summary>自动任务调度器：按周期触发后台 Agent 任务执行，并负责 task 列表的加载/保存。</summary>
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using GAIRR.Core;

namespace GAIRR;

public class AutoTaskScheduler
{
    readonly AppConfig cfg;
    readonly ObservableCollection<TaskItem> tasks;
    readonly Func<TaskItem, CancellationToken, Task> runner;
    readonly Dispatcher dispatcher;
    System.Timers.Timer? timer;

    public ReadOnlyObservableCollection<TaskItem> Tasks { get; }

    public AutoTaskScheduler(AppConfig cfg, ObservableCollection<TaskItem> tasks, Func<TaskItem, CancellationToken, Task> runner)
    {
        this.cfg = cfg;
        this.tasks = tasks;
        this.runner = runner;
        this.dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        Tasks = new ReadOnlyObservableCollection<TaskItem>(tasks);
    }

    /// <summary>启动调度器：加载任务、计算下次执行时间并开始 30 秒轮询。</summary>
    public void Start()
    {
        LoadTasks();
        foreach (var t in tasks) t.NextRun = t.Enabled ? t.GetNextRun() : null;
        timer = new System.Timers.Timer(30_000) { AutoReset = true };
        timer.Elapsed += (_, _) => Check();
        timer.Start();
    }

    /// <summary>停止调度器。</summary>
    public void Stop() => timer?.Stop();

    /// <summary>轮询检查是否有到期的启用任务。</summary>
    void Check()
    {
        var now = DateTime.Now;
        foreach (var task in tasks)
        {
            if (!task.Enabled || task.running != 0) continue;
            if (task.NextRun == null || task.NextRun > now) continue;
            _ = Task.Run(async () => await RunTask(task));
        }
    }

    /// <summary>立即执行一次指定任务（用户手动触发）。</summary>
    public async Task RunTaskNow(TaskItem task) => await RunTask(task);

    /// <summary>执行单个自动任务并更新状态。</summary>
    async Task RunTask(TaskItem task)
    {
        if (Interlocked.Exchange(ref task.running, 1) != 0) return;
        Ui(() =>
        {
            task.Status = "进行中";
            task.Notify(nameof(TaskItem.DotBrush));
            task.Notify(nameof(TaskItem.RunningVisible));   // 显示任务卡片进度行
        });

        string result;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            await runner(task, cts.Token);
            result = "成功";
        }
        catch (OperationCanceledException)
        {
            result = "已取消";
        }
        catch (Exception ex)
        {
            result = "错误：" + ex.Message;
        }
        finally
        {
            task.running = 0;
        }

        Ui(() =>
        {
            task.LastRun = DateTime.Now;
            task.Status = result == "成功" ? "成功" : "失败";
            task.LastResult = result;
            task.RunCount++;
            task.NextRun = task.Enabled ? task.GetNextRun() : null;
            task.ProgressText = "";
            task.ProgressPercent = 0;
            task.Notify(nameof(TaskItem.DotBrush));
            task.Notify(nameof(TaskItem.RunningVisible));   // 收起任务卡片进度行
            SaveTasks();
        });
    }

    /// <summary>项目级任务存储路径：{ProjectRoot}/.gairr/tasks.json，任务随当前项目隔离。</summary>
    static string TasksFilePath(AppConfig cfg)
    {
        var root = !string.IsNullOrWhiteSpace(cfg.ProjectRoot) ? cfg.ProjectRoot : AppContext.BaseDirectory;
        return Path.Combine(root, ".gairr", "tasks.json");
    }

    /// <summary>保存全部任务到项目级 .gairr/tasks.json；成功返回文件路径，失败返回 null。</summary>
    public string? SaveTasks()
    {
        try
        {
            var path = TasksFilePath(cfg);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // 并发保护：tasks.json 会被 同进程多任务并发收尾/手动触发/跨 GAIRR 实例同时写，
            // 全程持 FileLock 排队（Named Mutex 进程级），10s 拿不到视为被长时间占用则放弃本次保存
            var mutex = FileLock.For(path);
            if (!mutex.WaitOne(TimeSpan.FromSeconds(10)))
            {
                System.Diagnostics.Debug.WriteLine("[Error] 保存任务失败: tasks.json 被另一写入方占用");
                return null;
            }
            try
            {
                // Encoder 放开：task 列表 json 中文直存
                var opt = new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
                System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(tasks.ToList(), opt));
            }
            finally { mutex.ReleaseMutex(); }
            return path;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Error] 保存任务失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>从项目级 data/tasks.json 加载任务。</summary>
    public void LoadTasks()
    {
        try
        {
            var path = TasksFilePath(cfg);
            if (!File.Exists(path)) return;
            // 读取防半写：并发写方（同进程多任务收尾/跨 GAIRR 实例）可能正在写 tasks.json，持锁读避免读到半截 json
            var mutex = FileLock.For(path);
            if (!mutex.WaitOne(TimeSpan.FromSeconds(5))) return;
            string json;
            try { json = File.ReadAllText(path); }
            finally { mutex.ReleaseMutex(); }
            var list = System.Text.Json.JsonSerializer.Deserialize<List<TaskItem>>(json);
            if (list == null) return;
            tasks.Clear();
            foreach (var t in list) tasks.Add(t);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Error] 加载任务失败: {ex.Message}");
        }
    }

    /// <summary>在 UI 线程执行操作。</summary>
    void Ui(Action a)
    {
        if (dispatcher.CheckAccess()) a();
        else dispatcher.BeginInvoke(a);
    }
}
