using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GAIRR.Core;

/// <summary>任务持久化存储：管理 tasks.json（任务配置）和 task_runs.jsonl（运行记录追加）。</summary>
public class TaskStore
{
    readonly AppConfig cfg;

    public TaskStore(AppConfig cfg)
    {
        this.cfg = cfg;
    }

    /// <summary>任务配置文件路径：{AppData}/tasks.json（CLI/Server 模式无固定项目根时）。</summary>
    public string TasksPath
    {
        get
        {
            var dir = AppDataDir;
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "tasks.json");
        }
    }

    /// <summary>任务运行记录路径：{AppData}/task_runs.jsonl。</summary>
    public string RunsPath
    {
        get
        {
            var dir = AppDataDir;
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "task_runs.jsonl");
        }
    }

    /// <summary>应用数据目录：优先使用项目根 .gairr/tasks，否则退回程序目录 tasks。</summary>
    public string AppDataDir
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(cfg.ProjectRoot))
            {
                var d = Path.Combine(cfg.ProjectRoot, ".gairr", "tasks");
                Directory.CreateDirectory(d);
                return d;
            }
            var fallback = Path.Combine(AppContext.BaseDirectory, "tasks");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    static readonly JsonSerializerOptions Opt = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };   // tasks.json / task_runs.jsonl 中文直存

    /// <summary>读取全部任务。</summary>
    public List<TaskItem> LoadTasks()
    {
        try
        {
            var path = TasksPath;
            if (!File.Exists(path)) return new List<TaskItem>();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<TaskItem>>(json, Opt) ?? new List<TaskItem>();
        }
        catch { return new List<TaskItem>(); }
    }

    /// <summary>保存全部任务。</summary>
    public void SaveTasks(List<TaskItem> tasks)
    {
        var path = TasksPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(tasks, Opt));
    }

    /// <summary>追加一条运行记录。</summary>
    public void AppendRun(TaskRunRecord record)
    {
        var path = RunsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var line = JsonSerializer.Serialize(record, Opt).Replace("\n", " ");
        File.AppendAllText(path, line + Environment.NewLine);
    }

    /// <summary>读取全部运行记录（最新在前）。</summary>
    public List<TaskRunRecord> LoadRuns()
    {
        var list = new List<TaskRunRecord>();
        try
        {
            var path = RunsPath;
            if (!File.Exists(path)) return list;
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var r = JsonSerializer.Deserialize<TaskRunRecord>(line);
                    if (r != null) list.Add(r);
                }
                catch { }
            }
        }
        catch { }
        return list.OrderByDescending(r => r.StartedAt).ToList();
    }

    /// <summary>读取某任务的运行记录。</summary>
    public List<TaskRunRecord> LoadRunsForTask(string taskId)
    {
        return LoadRuns().Where(r => r.TaskId == taskId).ToList();
    }

    /// <summary>获取已启用且到期的任务列表（不修改下次执行时间）。</summary>
    public List<TaskItem> GetDueJobs(DateTime now)
    {
        return LoadTasks().Where(t => t.IsDue(now)).ToList();
    }

    /// <summary>生成短 GUID 作为任务 ID。</summary>
    public static string NewId() => Guid.NewGuid().ToString("N")[..8];
}
