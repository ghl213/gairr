using System.Text.Encodings.Web;
using System.Text.Json;

namespace GAIRR.Core;

/// <summary>计划确认持久化存储：保存 Agent 生成的待审批计划，供 UI / CLI / Server 查询与审批。</summary>
public static class PlanStore
{
    /// <summary>计划存储根目录：{ProjectRoot}/.gairr/plans</summary>
    public static string RootDir(AppConfig cfg)
    {
        var root = !string.IsNullOrWhiteSpace(cfg.ProjectRoot) ? cfg.ProjectRoot : AppContext.BaseDirectory;
        var dir = Path.Combine(root, ".gairr", "plans");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>保存计划；返回存储路径。</summary>
    public static string Save(AppConfig cfg, Plan plan)
    {
        var dir = RootDir(cfg);
        var path = Path.Combine(dir, $"{SafeName(plan.Id)}.json");
        var opt = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };   // 计划 json 中文直存
        File.WriteAllText(path, JsonSerializer.Serialize(plan, opt));
        return path;
    }

    /// <summary>加载指定计划。</summary>
    public static Plan? Load(AppConfig cfg, string id)
    {
        var path = Path.Combine(RootDir(cfg), $"{SafeName(id)}.json");
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Plan>(json);
        }
        catch { return null; }
    }

    /// <summary>列出某会话的全部计划（最新在前）。</summary>
    public static List<Plan> LoadForSession(AppConfig cfg, string sessionId)
    {
        var dir = RootDir(cfg);
        var list = new List<Plan>();
        foreach (var file in SafeEnumerateFiles(dir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var p = JsonSerializer.Deserialize<Plan>(json);
                if (p != null && p.SessionId == sessionId) list.Add(p);
            }
            catch { }
        }
        return list.OrderByDescending(p => p.CreatedAt).ToList();
    }

    /// <summary>列出全部计划（最新在前）。</summary>
    public static List<Plan> LoadAll(AppConfig cfg)
    {
        var dir = RootDir(cfg);
        var list = new List<Plan>();
        foreach (var file in SafeEnumerateFiles(dir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var p = JsonSerializer.Deserialize<Plan>(json);
                if (p != null) list.Add(p);
            }
            catch { }
        }
        return list.OrderByDescending(p => p.CreatedAt).ToList();
    }

    /// <summary>批准计划：状态改为 approved 并回写文件。</summary>
    public static bool Approve(AppConfig cfg, string id)
    {
        var plan = Load(cfg, id);
        if (plan == null) return false;
        plan.Status = "approved";
        plan.DecidedAt = DateTime.Now;
        Save(cfg, plan);
        return true;
    }

    /// <summary>拒绝计划：状态改为 rejected 并回写文件。</summary>
    public static bool Reject(AppConfig cfg, string id)
    {
        var plan = Load(cfg, id);
        if (plan == null) return false;
        plan.Status = "rejected";
        plan.DecidedAt = DateTime.Now;
        Save(cfg, plan);
        return true;
    }

    static string SafeName(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "_";
        var invalid = Path.GetInvalidFileNameChars();
        return new string(id.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
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
