/// <summary>任务运行历史存储：统一管理定时任务每次执行的记录文件（.gairr/taskruns/）。</summary>
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using GAIRR.Core;

namespace GAIRR;

public static class TaskRunStore
{
    /// <summary>任务运行历史根目录：{ProjectRoot}/.gairr/taskruns</summary>
    public static string RootDir(AppConfig cfg)
    {
        var root = !string.IsNullOrWhiteSpace(cfg.ProjectRoot) ? cfg.ProjectRoot : AppContext.BaseDirectory;
        var dir = Path.Combine(root, ".gairr", "taskruns");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>按任务 ID 分组存储本次运行记录</summary>
    public static string Save(AppConfig cfg, TaskRunRecord record)
    {
        var dir = Path.Combine(RootDir(cfg), SafeName(record.TaskId));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{record.StartedTime:yyyyMMdd_HHmmss}_{record.Id}.json");
        var opt = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };   // 任务运行记录中文直存
        File.WriteAllText(path, JsonSerializer.Serialize(record, opt));
        return path;
    }

    /// <summary>加载所有任务运行记录</summary>
    public static List<TaskRunRecord> LoadAll(AppConfig cfg)
    {
        var list = new List<TaskRunRecord>();
        var root = RootDir(cfg);
        foreach (var taskDir in SafeEnumerateDirectories(root))
        {
            foreach (var file in SafeEnumerateFiles(taskDir, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var rec = JsonSerializer.Deserialize<TaskRunRecord>(json);
                    if (rec != null) list.Add(rec);
                }
                catch { /* 单条损坏不影响整体 */ }
            }
        }
        return list.OrderByDescending(r => r.StartedTime).ToList();
    }

    /// <summary>加载指定任务的运行记录</summary>
    public static List<TaskRunRecord> LoadForTask(AppConfig cfg, string taskId)
    {
        var dir = Path.Combine(RootDir(cfg), SafeName(taskId));
        if (!Directory.Exists(dir)) return new List<TaskRunRecord>();
        var list = new List<TaskRunRecord>();
        foreach (var file in SafeEnumerateFiles(dir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var rec = JsonSerializer.Deserialize<TaskRunRecord>(json);
                if (rec != null) list.Add(rec);
            }
            catch { }
        }
        return list.OrderByDescending(r => r.StartedTime).ToList();
    }

    /// <summary>删除指定运行记录文件</summary>
    public static bool Delete(AppConfig cfg, string taskId, string runId)
    {
        var dir = Path.Combine(RootDir(cfg), SafeName(taskId));
        if (!Directory.Exists(dir)) return false;
        foreach (var file in SafeEnumerateFiles(dir, "*.json"))
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (!name.EndsWith($"_{runId}")) continue;
                File.Delete(file);
                return true;
            }
            catch { }
        }
        return false;
    }

    static string SafeName(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "_";
        var invalid = Path.GetInvalidFileNameChars();
        return new string(id.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return Enumerable.Empty<string>();
            return Directory.EnumerateDirectories(path);
        }
        catch { return Enumerable.Empty<string>(); }
    }

    static IEnumerable<string> SafeEnumerateFiles(string path, string pattern)
    {
        try
        {
            if (!Directory.Exists(path)) return Enumerable.Empty<string>();
            return Directory.EnumerateFiles(path, pattern).OrderBy(f => f);
        }
        catch { return Enumerable.Empty<string>(); }
    }
}
