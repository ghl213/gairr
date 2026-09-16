using System.Text.Encodings.Web;
using System.Text.Json;
using GAIRR.Core;

namespace GAIRR.AgentHost;

/// <summary>会话记录（SessionStore 落盘格式，一份文件 = 一个会话）</summary>
public class SessionRecord
{
    public string Id { get; set; } = "";

    /// <summary>身份分区键：默认 = 项目根路径归一化；多用户/多租户可扩为 {userId}/{project}</summary>
    public string Namespace { get; set; } = "";

    public string Project { get; set; } = "";
    public string Title { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";

    /// <summary>idle=空闲；busy=任务执行中；pendingDecision=等待危险决策</summary>
    public string Status { get; set; } = "idle";

    // ---- 编排会话（8.3 决策 1：三级 1:1 隔离 = 架构节点 → 编排任务 → 编排会话） ----
    /// <summary>true=编排会话（会话列表"计划会话"分组展示，不与普通工作会话混用上下文）。</summary>
    public bool IsOrchestration { get; set; }
    /// <summary>关联编排任务 id（IsOrchestration=true 时）。</summary>
    public string? PlanId { get; set; }
    /// <summary>关联架构节点 id（IsOrchestration=true 时；节点改名/移动以引用跟随）。</summary>
    public string? NodeId { get; set; }

    /// <summary>对话历史（AgentLoop.LoadHistory 可直接消费回灌）</summary>
    public List<SessionMessage> Messages { get; set; } = new();

    /// <summary>GUI 源会话基线：resume 落副本时桌面 session_history.json 该会话的对话条数（User/Agent 非空）。
    /// /history 据此把桌面文件里后新增的回复以只读视图合并进手机端（副本本身不追赶桌面，避免双写冲突）。
    /// 0 = 旧副本未记录（退回前缀/拼接启发合并）。</summary>
    public int GuiChatBase { get; set; }

    /// <summary>扩展元数据（模型/provider 等，可空）</summary>
    public Dictionary<string, string>? Meta { get; set; }
}

/// <summary>单条对话消息（role/user 与 assistant；UI 渲染态不属于会话存储）</summary>
public class SessionMessage
{
    public string Role { get; set; } = "";      // user / assistant
    public string Content { get; set; } = "";
}

/// <summary>
/// 会话存储（引擎共享模块，一份代码三端共用；不建 UI 专属第二份）：
/// 只存跨宿主对话资产 = 元数据 + 对话历史；UI 渲染态（时间线展开/草稿/滚动位）留壳。
/// 落盘 {SessionDir}/{namespace}/{id}.json；namespace 默认 = 项目路径归一化，
/// 未来多用户只需把键扩为 {userId}/{project}，存储实现不变。
/// </summary>
public class SessionStore
{
    readonly string root;   // {SessionDir} 或默认 {ProjectRoot}/.gairr/sessions

    public SessionStore(AppConfig cfg, string projectRoot)
    {
        root = string.IsNullOrWhiteSpace(SystemCfg.SessionDir)
            ? Path.Combine(projectRoot, ".gairr", "sessions")
            : SystemCfg.SessionDir;
    }

    /// <summary>namespace 归一化：项目全路径 → 下划线扁平键（d:\work\x → d_work_x）</summary>
    public static string NormalizeNamespace(string projectRoot) =>
        Path.GetFullPath(projectRoot).TrimEnd('\\', '/')
            .Replace(':', '_').Replace('\\', '_').Replace('/', '_');

    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };   // 会话记录 json 中文直存

    string PathFor(string ns, string id) => Path.Combine(root, ns, id + ".json");

    public void Save(SessionRecord rec)
    {
        var path = PathFor(rec.Namespace, rec.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(rec, JsonOpts), new System.Text.UTF8Encoding(false));
    }

    public SessionRecord? Load(string ns, string id)
    {
        var path = PathFor(ns, id);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<SessionRecord>(File.ReadAllText(path)); }
        catch { return null; }   // 个别文件损坏不影响其它会话
    }

    /// <summary>列会话（ns 空 = 全部身份分区），按最近更新倒序</summary>
    public List<SessionRecord> List(string? ns = null)
    {
        var dir = ns == null ? root : Path.Combine(root, ns);
        if (!Directory.Exists(dir)) return new List<SessionRecord>();
        var files = ns == null
            ? Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories)
            : Directory.GetFiles(dir, "*.json");
        var list = new List<SessionRecord>();
        foreach (var f in files)
        {
            try
            {
                var rec = JsonSerializer.Deserialize<SessionRecord>(File.ReadAllText(f));
                if (rec != null) list.Add(rec);
            }
            catch { }
        }
        return list.OrderByDescending(r => r.UpdatedAt).ToList();
    }

    public bool Delete(string ns, string id)
    {
        var path = PathFor(ns, id);
        if (!File.Exists(path)) return false;
        try { File.Delete(path); return true; }
        catch { return false; }
    }
}