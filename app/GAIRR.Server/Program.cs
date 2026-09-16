using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;
using GAIRR.AgentHost;
using GAIRR.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

// gairr-agent-server：GAIRR Agent 接口服务（会话/任务级，SSE 推事件，无界面）
// 配置：config.ini [Server] Host/Port/Token；启动参数 --host/--port/--token 可覆盖
Console.OutputEncoding = System.Text.Encoding.UTF8;

var ini = new AppConfig();
var host = ini.Get("Server", "Host", "127.0.0.1");
var port = ini.GetInt("Server", "Port", 8123);
var token = ini.Get("Server", "Token", "");
// 多用户：[Server] Users = 名字:令牌,名字:令牌（逗号分隔；名字/令牌不含 : , 空白）。
// 身份语义：Token = 管理员（可见/操作全部会话）；Users 令牌 = 普通用户（仅自己创建或认领的会话）。
// 两者都未配置 = 开发模式：不鉴权、全共享（旧行为）。
var users = new Dictionary<string, string>(StringComparer.Ordinal);
foreach (var part in ini.Get("Server", "Users", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
{
    var i = part.IndexOf(':');
    if (i > 0 && i < part.Length - 1) users[part[..i]] = part[(i + 1)..];
}
var idleEvictMin = ini.GetInt("Server", "IdleEvictMin", 30);   // hub 空闲会话驱逐分钟数（≤0 = 不驱逐）
// GUI 会话源目录（桌面端 exe\data，含 session_history.json / busy_*.json）：配置后普通/计划会话与桌面左侧对齐
var guiDir = ini.Get("Server", "GuiDataDir", "").Trim();
if (guiDir.Length > 0 && !File.Exists(Path.Combine(guiDir, "session_history.json")))
    Console.WriteLine($"[警告] [Server] GuiDataDir={guiDir} 下无 session_history.json —— 普通/计划会话列表将回退为执行记录");
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--host" when i + 1 < args.Length: host = args[++i]; break;
        case "--port" when i + 1 < args.Length: port = int.Parse(args[++i]); break;
        case "--token" when i + 1 < args.Length: token = args[++i]; break;
    }
}
if (string.IsNullOrEmpty(token))
    Console.WriteLine("[警告] 未配置 [Server] Token —— 开发模式：除 /health 外接口不鉴权。生产部署务必设置。");

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args });
builder.Logging.ClearProviders();
builder.WebHost.UseUrls($"http://{host}:{port}");
var app = builder.Build();

// 令牌鉴权 + 身份注入：除 /health 外全部校验 Authorization: Bearer <Token>；
// /gui-live/*（桌面 GUI 实时流镜像入口）仅限本机回环来源，进程间信任免鉴权（手机等外部客户端不可达）
// Token = 管理员("*")；Users 名字:令牌 = 普通用户；均未配置 = 开发模式(不校验，全共享)
app.Use(async (ctx, next) =>
{
    var isLoopbackGuiLive = ctx.Request.Path.StartsWithSegments("/gui-live")
        && ctx.Connection.RemoteIpAddress != null
        && System.Net.IPAddress.IsLoopback(ctx.Connection.RemoteIpAddress);
    if ((token.Length > 0 || users.Count > 0) && !isLoopbackGuiLive
        && !ctx.Request.Path.Equals("/health", StringComparison.OrdinalIgnoreCase))
    {
        var auth = ctx.Request.Headers.Authorization.ToString();
        var tk = auth.StartsWith("Bearer ", StringComparison.Ordinal) ? auth[7..] : "";
        string? who = null;
        if (tk.Length > 0)
        {
            if (token.Length > 0 && tk == token) who = "*";   // 管理员
            else foreach (var kv in users) if (tk == kv.Value) { who = kv.Key; break; }
        }
        if (who == null)
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsync("{\"error\":\"unauthorized\"}");
            return;
        }
        ctx.Items["user"] = who;   // 后续端点按归属过滤/闸权
    }
    await next();
});

var hub = new SessionHub(idleEvictMin);
var liveHub = new GuiLiveHub();
var sseJson = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };   // SSE 内中文不转义为 \uXXXX

// ── 多用户归属：user=null=未启用鉴权(全共享)；"*"=管理员(全部)；其他=仅 owner 为自己的会话 ──
string? CurUser(HttpContext ctx) => ctx.Items["user"] as string;
bool CanAccess(string? user, string owner) => user == null || user == "*" || (owner.Length > 0 && owner == user);
static string OwnerOf(SessionRecord r) => r.Meta != null && r.Meta.TryGetValue("owner", out var o) ? o : "";

// 归属闸（hub 会话）：无权一律 404（不泄露会话存在性）；hub 内存归属滞后时回退磁盘记录判一次
AgentSession? GateHub(HttpContext ctx, string id)
{
    var s = hub.Get(id);
    if (s == null) return null;
    var u = CurUser(ctx);
    if (CanAccess(u, s.Owner)) return s;
    var rec = s.Store.Load(SessionStore.NormalizeNamespace(s.ProjectRoot), id);
    return CanAccess(u, OwnerOf(rec)) ? s : null;
}

// 认领：普通用户对无主的磁盘会话（resume/exec 时）自动归属自己（先占先得；管理员不独占，保持可指派）
bool Claim(string user, SessionStore store, SessionRecord rec)
{
    if (user.Length == 0 || user == "*" || OwnerOf(rec).Length > 0) return false;
    rec.Meta ??= new Dictionary<string, string>();
    rec.Meta["owner"] = user;
    try { store.Save(rec); return true; } catch { return false; }
}

app.MapGet("/health", () => Results.Json(new
{
    ok = true,
    service = "gairr-agent-server",
    sessions = hub.Count,
    time = DateTime.Now.ToString("s"),
}));

// 建会话：{ project: 项目根(必填), configPath?: 自定义 config.ini }
app.MapPost("/sessions", (SessionCreate body, HttpContext ctx) =>
{
    if (string.IsNullOrWhiteSpace(body.Project)) return Results.BadRequest(Err("project 必填"));
    if (!Directory.Exists(body.Project)) return Results.BadRequest(Err($"项目目录不存在：{body.Project}"));
    var id = hub.Create(Path.GetFullPath(body.Project), body.ConfigPath);
    var u = CurUser(ctx);
    if (u != null && u != "*") hub.Get(id)?.SetOwner(u);   // 归属当前用户（管理员不独占，保持可指派）
    return Results.Json(new { sessionId = id }, statusCode: 201);
});

// ── 会话列表/恢复/历史（移动端多会话查看切换）：GUI 源（桌面 session_history.json）+ 手机端执行记录 ──
// 会话列表：?project= 指定项目列出全部（含历史），缺省=所有活跃会话所在项目；hub 活跃的排前
// GUI 源已配置（[Server] GuiDataDir）：普通/计划会话 = 桌面左侧分组（组内 Created 倒序，不置顶）；
// 手机端 s-* 会话 = 执行记录；未配置/文件缺失回退为执行记录全量旧分类（plan-/leaf-/review- 也展示）
app.MapGet("/sessions", (string? project, HttpContext ctx) =>
{
    var user = CurUser(ctx);
    var rows = new List<SessionRow>();
    var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    if (!string.IsNullOrWhiteSpace(project) && Directory.Exists(project)) roots.Add(Path.GetFullPath(project));
    foreach (var s in hub.All) roots.Add(s.ProjectRoot);   // hub 活跃会话的项目兜底列出（服务重启后新建过会话的项目）
    var guiFile = GuiSync.ResolveGuiFile(guiDir);

    void CollectMobile(string root)
    {
        var store = new SessionStore(new AppConfig(), root);
        foreach (var r in store.List(SessionStore.NormalizeNamespace(root)))
        {
            if (!r.Id.StartsWith("s-", StringComparison.OrdinalIgnoreCase)) continue;   // 只列手机端会话（执行记录不混入 GUI 组）
            var own = OwnerOf(r);
            if (!(user == null || user == "*" || own == user || own == "shared")) continue;   // 列表不展示"无主"旧会话：普通用户各自新建；仅显式 resume 旧 sessionId 时才认领
            var s = hub.Get(r.Id);
            rows.Add(new SessionRow(r.Id, r.Project, r.Title ?? "", r.Status ?? "idle", s != null, s?.State ?? "", r.CreatedAt ?? "", r.UpdatedAt ?? "", false, "mobile"));
        }
    }

    // 旧分类（未配 GUI 源时的回退）：执行记录全量，编排/执行记录按标记归组
    void CollectLegacy(string root)
    {
        var store = new SessionStore(new AppConfig(), root);
        foreach (var r in store.List(SessionStore.NormalizeNamespace(root)))
        {
            if (!CanAccess(user, OwnerOf(r))) continue;   // 多用户：普通用户只见自己的会话
            var s = hub.Get(r.Id);
            var planLike = r.IsOrchestration
                || !string.IsNullOrWhiteSpace(r.PlanId)
                || !string.IsNullOrWhiteSpace(r.NodeId)
                || r.Id.StartsWith("plan-", StringComparison.OrdinalIgnoreCase)
                || r.Id.StartsWith("leaf-", StringComparison.OrdinalIgnoreCase)
                || r.Id.StartsWith("review-", StringComparison.OrdinalIgnoreCase);
            var cat = planLike ? "plan" : r.Id.StartsWith("s-", StringComparison.OrdinalIgnoreCase) ? "mobile" : "normal";
            rows.Add(new SessionRow(r.Id, r.Project, r.Title ?? "", r.Status ?? "idle", s != null, s?.State ?? "", r.CreatedAt ?? "", r.UpdatedAt ?? "", r.IsOrchestration, cat));
        }
    }

    if (guiFile != null)
    {
        // GUI 源：普通/计划会话与桌面左侧 1:1（Project 匹配大小写不敏感，容手机端路径大小写差异）
        var heads = GuiSync.ListHeads(guiFile);
        if (heads == null)
            Console.WriteLine("[GUI 源] session_history.json 解析失败，本次仅列手机端会话");
        foreach (var root in roots)
        {
            // 多用户：桌面 GUI 会话组仅管理员/未鉴权模式可见（普通用户不混入桌面分组）
            if (heads != null && (user == null || user == "*"))
                foreach (var h in heads)
                {
                    if (!string.Equals(h.Project, root, StringComparison.OrdinalIgnoreCase)) continue;
                    var s = hub.Get(h.Id);
                    var st = s?.State ?? "idle";
                    rows.Add(new SessionRow(h.Id, root, h.Title ?? "", st, s != null, s?.State ?? "", h.Created.ToString("s"), (h.Time ?? h.Created).ToString("s"), h.IsOrchestration, h.IsOrchestration ? "plan" : "normal",
                        guiDir.Length > 0 && GuiSync.IsGuiBusy(guiDir, h.Id)));
                }
            CollectMobile(root);
        }
        // 组内排序：普通/计划 = Created 倒序（桌面序）；手机端 = 活跃优先 + 最近更新倒序
        var list = rows
            .GroupBy(r => r.Category)
            .OrderBy(g => g.Key == "normal" ? 0 : g.Key == "plan" ? 1 : 2)
            .SelectMany(g => g.Key == "mobile"
                ? g.OrderByDescending(r => r.Active).ThenByDescending(r => r.UpdatedAt, StringComparer.Ordinal)
                : g.OrderByDescending(r => r.CreatedAt, StringComparer.Ordinal))
            .Select(r => new
            {
                sessionId = r.SessionId,
                project = r.Project,
                title = r.Title,
                status = r.Status,
                active = r.Active,
                busy = r.Busy,
                state = r.State,
                createdAt = r.CreatedAt,
                updatedAt = r.UpdatedAt,
                orchestration = r.IsOrchestration,
                category = r.Category,
            });
        return Results.Json(new { sessions = list });
    }

    foreach (var root in roots) CollectLegacy(root);
    var legacy = rows
        .OrderByDescending(r => r.Active)          // 活跃会话排前
        .ThenByDescending(r => r.UpdatedAt)        // 同组按最近更新倒序
        .Select(r => new
        {
            sessionId = r.SessionId,
            project = r.Project,
            title = r.Title,
            status = r.Status,
            active = r.Active,
            busy = r.Busy,
            state = r.State,
            createdAt = r.CreatedAt,
            updatedAt = r.UpdatedAt,
            orchestration = r.IsOrchestration,
            category = r.Category,
        });
    return Results.Json(new { sessions = legacy });
});

// ── 角色模式 / 模型（手机端详情页顶栏"切换角色模型"面板）──────────────────────────

// 角色清单：热加载 roles/ 目录（目录包 role.json 或扁平 *.json），modes 顺序即 UI 顺序
app.MapGet("/roles", () =>
{
    var roles = RoleModeStore.LoadAll().Select(r => new
    {
        name = r.Name,
        displayName = r.DisplayName.Length > 0 ? r.DisplayName : r.Name,
        icon = r.Icon,
        description = r.Description,
        defaultMode = r.DefaultMode,
        modes = r.Modes.Select(m => new
        {
            name = m.Name,
            displayName = m.DisplayName.Length > 0 ? m.DisplayName : m.Name,
            icon = m.Icon,
            description = m.Description,
            hasFlow = RoleModeStore.ResolveFlow(m) != null,   // 带流程模板=框架逐步驱动（前端标"流程"角标）
        }).ToList(),
    }).ToList();
    return Results.Json(new { roles });
});

// 模型清单：config.ini [Providers] × 各自 models（有 ApiKey 的排前）；default* = 服务端默认厂商/模型
app.MapGet("/models", () =>
{
    var cfg = new AppConfig();
    var models = cfg.ModelOptions().Select(o => new
    {
        provider = o.Provider,
        providerDisplay = o.ProviderDisplay,
        model = o.ModelId,
        display = o.Display,
        hasKey = o.HasKey,   // false=该厂商没配 ApiKey，前端置灰不可选
    }).ToList();
    var p = cfg.LastProvider.Length > 0 ? cfg.LastProvider : cfg.Provider;
    var m = cfg.LastModel.Length > 0 ? cfg.LastModel : cfg.ProviderCfg(p).Model;
    return Results.Json(new { models, defaultProvider = p, defaultModel = m });
});

// 会话当前执行参数（角色模式 + 模型）：?project= 会话所属项目（hub 未命中时据此恢复）
app.MapGet("/sessions/{id}/exec", (string id, string? project, HttpContext ctx) =>
{
    var (s, err) = ResolveExecSession(id, project, ctx);
    if (s == null) return ErrCode(err ?? "会话不可用", 404);
    return Results.Json(s.ExecSnapshot());
});

// 切换会话执行参数：body { role?, mode?, provider?, model? }（role+mode / provider+model 各成对，留空=该项不变）；
// 执行中（busy/挂起）拒绝，等本轮结束再切；成功后写会话记录 Meta → resume/重启仍生效，下一轮任务即用新参数
app.MapPost("/sessions/{id}/exec", (string id, ExecBody body, string? project, HttpContext ctx) =>
{
    var (s, err) = ResolveExecSession(id, project, ctx);
    if (s == null) return ErrCode(err ?? "会话不可用", 404);
    if (s.State != "idle") return ErrCode($"会话{s.State}：请等本轮结束后再切换", 409);
    var e = s.SetExecParams(body.Role, body.Mode, body.Provider, body.Model);
    if (e != null) return ErrCode(e, 400);
    return Results.Json(s.ExecSnapshot());
});

// 取（必要时恢复）会话供执行参数读写：hub 命中直接用；否则要求磁盘确有执行记录再恢复
// （不凭空 Resume：未知 id 会造出幽灵会话；GUI 源会话需先 POST /resume 落副本）；
// 桌面端正在执行的 GUI 源会话不接管（双写冲突），返回可读的错误说明
(AgentSession? Session, string? Error) ResolveExecSession(string id, string? project, HttpContext ctx)
{
    var user = CurUser(ctx);
    var hit = hub.Get(id);
    if (hit != null)
    {
        if (CanAccess(user, hit.Owner)) return (hit, null);
        var hitRec = hit.Store.Load(SessionStore.NormalizeNamespace(hit.ProjectRoot), id);
        return CanAccess(user, OwnerOf(hitRec)) ? (hit, null) : (null, "会话不存在：" + id);
    }
    if (string.IsNullOrWhiteSpace(project) || !Directory.Exists(project)) return (null, "project 无效：目录不存在");
    var root = Path.GetFullPath(project);
    if (GuiSync.IsGuiBusy(guiDir, id)) return (null, "该会话正在桌面端执行中，暂不能切换角色/模型");
    var store = new SessionStore(new AppConfig(), root);
    var rec = store.Load(SessionStore.NormalizeNamespace(root), id);
    if (rec == null)
        return (null, GuiSync.IsGuiSessionId(id)
            ? "该会话由桌面端主持，手机端为只读观看，暂不能切换角色/模型"
            : "会话不存在：" + id);
    if (!CanAccess(user, OwnerOf(rec))) return (null, "会话不存在：" + id);
    Claim(user ?? "", store, rec);   // 无主会话先占先得
    try { return (hub.Resume(id, root), null); }
    catch (Exception ex) { return (null, "恢复会话失败：" + ex.Message); }
}

// 恢复历史会话进 hub（幂等：已在 hub 直接返回）：?project= 会话所属项目；
// GUI 源会话（session_history.json 里 8 位 hex id）：磁盘无副本时先落副本（转换富消息为对话正文）再恢复，桌面文件不回写；
// busy 标记/磁盘状态 busy 时拒绝（桌面端执行中，双写冲突）
app.MapPost("/sessions/{id}/resume", (string id, string? project, HttpContext ctx) =>
{
    if (string.IsNullOrWhiteSpace(project) || !Directory.Exists(project))
        return Results.BadRequest(Err("project 无效：目录不存在"));
    var user = CurUser(ctx);   // null=未鉴权全共享；"*"=管理员；其他=普通用户（仅自己的会话）
    var root = Path.GetFullPath(project);
    var store = new SessionStore(new AppConfig(), root);
    var ns = SessionStore.NormalizeNamespace(root);
    var guiFile = GuiSync.ResolveGuiFile(guiDir);
    var rec = store.Load(ns, id);
    if (rec != null && !CanAccess(user, OwnerOf(rec))) return Results.NotFound(Err("会话不存在：" + id));
    if (rec == null && guiFile != null)
    {
        // 执行记录无此会话 → GUI 源兜底（桌面主持会话，手机侧只读续聊，不回写桌面文件）
        if (GuiSync.IsGuiBusy(guiDir, id))
            return Results.Conflict(Err($"会话 {id} 正在桌面端执行中，手机端暂不能接入"));
        var full = GuiSync.FindSession(guiFile, id);
        if (full == null) return Results.NotFound(Err("会话不存在：" + id));
        // 落一份磁盘副本（含完整对话正文）：AgentSession 构造 Load 后即恢复全部上下文；后续轮次都在副本上累积
        rec = new SessionRecord
        {
            Id = id,
            Namespace = ns,
            Project = root,
            Title = full.Head.Title,
            CreatedAt = full.Head.Created == DateTime.MinValue ? DateTime.Now.ToString("s") : full.Head.Created.ToString("s"),
            UpdatedAt = DateTime.Now.ToString("s"),
            Messages = full.Chats.Select(c => new SessionMessage { Role = c.Role, Content = c.Content }).ToList(),
            GuiChatBase = full.Chats.Count,   // 基线：后续 /history 据此合并桌面文件里后新增的回复（只读视图）
        };
        if (user != null && user != "*") { rec.Meta ??= new Dictionary<string, string>(); rec.Meta["owner"] = user; }   // 副本归属认领者
        try { store.Save(rec); }
        catch (Exception ex) { Console.WriteLine($"[resume {id}] GUI 副本落盘失败：{ex.Message}（本轮仍可续聊）"); }
        GuiSync.NoteResumed(id, GuiSync.ChatCount(rec));
        var s2 = hub.Resume(id, root);
        return Results.Ok(new { sessionId = id, state = s2.State });
    }
    if (rec == null) return Results.NotFound(Err("会话不存在：" + id));
    // 本机 Server 正在执行/挂起该会话（busy / 危险确认 pendingDecision / 计划审批 pendingPlan）：
    // 幂等放行 → 前端转常规 SSE 监控（/events 订阅会先回放历史，SecurityAlert/PlanPending 可补送 → 继续出卡决策），
    // 避免落入 "409 → 误入桌面观看流 → 该 s-* 移动会话无 GUI 帧 → no_live → 自动重连" 的死循环（危险卡永远弹不出）。
    // 注：仅覆盖本进程 hub 里活跃的会话；桌面 GUI 执行中的 8 位 hex 源会话不在此列，仍走下方 busy 409 → 前端观看桌面。
    var active = hub.Get(id);
    if (active != null && !CanAccess(user, active.Owner))
        return Results.NotFound(Err("会话不存在：" + id));
    if (active != null && active.State != "idle")
        // 挂起中的危险确认随状态携带详情：客户端进会话可直接建卡（重放帧可能已丢失/被过滤）
        return Results.Ok(new { sessionId = id, state = active.State, pendingDanger = active.CurrentPendingDanger });
    if (rec.Status is "busy" or "pendingDecision" or "pendingPlan")
        return Results.Conflict(Err($"会话 {id} 正在桌面端执行中，暂不能切换"));
    if (guiFile != null && GuiSync.IsGuiBusy(guiDir, id))
        return Results.Conflict(Err($"会话 {id} 正在桌面端执行中，手机端暂不能接入"));
    if (GuiSync.IsGuiSessionId(id))
    {
        GuiSync.NoteResumed(id, GuiSync.ChatCount(rec));   // GUI 源会话：记录基线供收口刷收件箱
        // 旧副本（无 GuiChatBase）补基线：桌面当前对话与副本逐条相同才记录，防错位；此后桌面新增可被 /history 合并
        if (rec.GuiChatBase <= 0 && guiFile != null)
        {
            var full0 = GuiSync.FindSession(guiFile, id);
            if (full0 != null && GuiSync.TryBackfillGuiBase(rec, full0.Chats))
                try { store.Save(rec); } catch { }
        }
    }
    var owner0 = OwnerOf(rec);
    if (user != null && user != "*" && (owner0 == null || owner0 == "shared"))   // 普通用户：resume 即认领无主/共享会话
    {
        rec.Meta ??= new Dictionary<string, string>(); rec.Meta["owner"] = user;
        try { store.Save(rec); } catch { }
        hub.Get(id)?.SetOwner(user);   // 已活跃的内存会话同步归属（内存 + 落盘）
    }
    var s = hub.Resume(id, root);   // AgentSession 构造自动恢复磁盘对话历史（含 Meta.owner）
    return Results.Ok(new { sessionId = id, state = s.State });
});

// 会话历史消息：?project= 会话所属项目；只返回 user/assistant 对话（tool 内部消息不下发），
// max=最近 N 条（默认 100，0=全部），total=该会话对话总条数；执行记录无此会话时 GUI 源只读兜底
app.MapGet("/sessions/{id}/history", (string id, string? project, HttpContext ctx, int max = 100) =>
{
    if (string.IsNullOrWhiteSpace(project) || !Directory.Exists(project))
        return Results.BadRequest(Err("project 无效：目录不存在"));
    var user = CurUser(ctx);
    var root = Path.GetFullPath(project);
    var store = new SessionStore(new AppConfig(), root);
    var rec = store.Load(SessionStore.NormalizeNamespace(root), id);
    if (rec != null && !CanAccess(user, OwnerOf(rec))) return Results.NotFound(Err("会话不存在：" + id));
    var guiFile = GuiSync.ResolveGuiFile(guiDir);
    if (rec == null && guiFile != null)
    {
        var full = GuiSync.FindSession(guiFile, id);   // GUI 源会话（副本未建时也可查看桌面历史；副本建后走上面副本分支）
        if (full == null) return Results.NotFound(Err("会话不存在：" + id));
        var gmsgs = full.Chats.Select(c => new { role = c.Role, content = c.Content }).ToList();
        var gtotal = gmsgs.Count;
        if (max > 0 && gmsgs.Count > max) gmsgs = gmsgs.Skip(gmsgs.Count - max).ToList();
        return Results.Json(new { sessionId = id, title = full.Head.Title, total = gtotal, messages = gmsgs });
    }
    if (rec == null) return Results.NotFound(Err("会话不存在：" + id));
    // GUI 源会话副本 + 桌面文件同时存在：把桌面文件里 resume 后新增的回复合并进只读视图
    //（副本是手机续聊工作域、不追桌面；不加此合并则桌面 gairr.exe 后续回复手机端永远不可见）
    List<SessionMessage>? merged = null;
    if (guiFile != null && GuiSync.IsGuiSessionId(id))
    {
        var gfull = GuiSync.FindSession(guiFile, id);
        if (gfull != null) merged = GuiSync.MergeGuiTail(rec, gfull.Chats);
    }
    List<SessionMessage> view = merged ?? (rec.Messages ?? new List<SessionMessage>())
        .Where(m => m.Role is "user" or "assistant" && !string.IsNullOrWhiteSpace(m.Content))
        .ToList();
    var msgs = view.Select(m => new { role = m.Role, content = m.Content }).ToList();
    var total = msgs.Count;
    if (max > 0 && msgs.Count > max) msgs = msgs.Skip(msgs.Count - max).ToList();
    return Results.Json(new { sessionId = id, title = rec.Title ?? "", total, messages = msgs });
});

// 发任务：{ text, tools?, flow?(预留) } → taskId（任务后台执行，进度经 /events SSE 推送）
app.MapPost("/sessions/{id}/messages", (string id, TaskRequest req, HttpContext ctx) =>
{
    var user = CurUser(ctx);
    var s = hub.Get(id);
    if (s == null) return Results.NotFound(Err("会话不存在"));
    if (!CanAccess(user, s.Owner)) return Results.NotFound(Err("会话不存在"));
    if (string.IsNullOrWhiteSpace(req.Text)) return Results.BadRequest(Err("text 必填"));
    if (s.State != "idle") return Results.Conflict(Err($"会话状态 {s.State}：繁忙或待决策，可先 Cancel 或等待"));
    var guiFile = GuiSync.ResolveGuiFile(guiDir);
    // GUI 源会话最后一道闸：resume 后桌面才启动同会话任务（busy 标记）→ 拦截发送，防上下文分叉
    if (guiFile != null && GuiSync.IsGuiBusy(guiDir, id))
        return Results.Conflict(Err("桌面端正在执行该会话，手机端不能发送。请在桌面端停止该任务后重试"));
    var taskId = "t-" + Guid.NewGuid().ToString("N")[..8];
    var ns = SessionStore.NormalizeNamespace(s.ProjectRoot);
    var gdir = guiDir;
    var gfile = guiFile;
    _ = Task.Run(async () =>
    {
        try
        {
            await s.StartAsync(req);
            // GUI 源会话收口：副本尾部新消息（本轮 user/assistant）刷入收件箱，桌面 GUI 下次启动时合并
            GuiSync.FlushInboxTail(id, gdir, gfile, s.Store, ns);
        }
        catch (Exception ex) { Console.WriteLine($"[{id}] 任务异常：{ex.Message}"); }
    });
    return Results.Accepted(null, new { taskId, state = "busy" });
});

// SSE 事件流：广播模式（支持多客户端同时接收）
app.MapGet("/sessions/{id}/events", async (string id, HttpContext ctx) =>
{
    var user = CurUser(ctx);
    var s = hub.Get(id);
    if (s == null)
    {
        ctx.Response.StatusCode = 404;
        await ctx.Response.WriteAsync("{\"error\":\"会话不存在\"}");
        return;
    }
    if (!CanAccess(user, s.Owner))
    {
        ctx.Response.StatusCode = 404;
        await ctx.Response.WriteAsync("{\"error\":\"会话不存在\"}");
        return;
    }
    ctx.Response.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    await ctx.Response.WriteAsync("event: hello\ndata: {\"type\":\"connected\"}\n\n");
    await ctx.Response.Body.FlushAsync();
    // 回放范围：只回放当前进行中这一轮的帧（补齐客户端连流前错过的开头）。
    // 会话空闲时一帧不放——总线历史跨轮不清，重放上一轮的 Finished 会让下面的 tcs 立即置位、
    // 连接刚建好就被判定收工而关闭，前端随即报连接中断正在重连，重连又撞同一份回放直到次数耗尽。
    var sinceSeq = s.IsBusy ? s.RoundStartSeq : (long?)null;
    try
    {
        var ct = ctx.RequestAborted;
        var tcs = new TaskCompletionSource();
        var writeLock = new object();   // 心跳与事件帧共用响应流：串行写，防并发写交错损坏帧
        // 空闲心跳：思考期/工具期事件流长时间无帧，隧道或代理按空闲超时掐线 → 前端误报"事件流已断开"
        using var hbCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var hb = Task.Run(async () =>
        {
            try
            {
                while (!tcs.Task.IsCompleted)
                {
                    await Task.Delay(10000, hbCts.Token);
                    lock (writeLock)
                    {
                        ctx.Response.WriteAsync(": ping\n\n", ct).Wait(ct);   // SSE 注释行，客户端解析时忽略
                        ctx.Response.Body.FlushAsync(ct).Wait(ct);
                    }
                }
            }
            catch { /* 连接已断或心跳取消：静默退出 */ }
        });
        using var sub = s.SubscribeEvents(e =>
        {
            try
            {
                lock (writeLock)
                {
                    ctx.Response.WriteAsync("data: " + JsonSerializer.Serialize(e, sseJson) + "\n\n", ct).Wait(ct);
                    ctx.Response.Body.FlushAsync(ct).Wait(ct);
                }
                if (e.Type is "Finished" or "Failed")
                    tcs.TrySetResult();
            }
            catch { tcs.TrySetResult(); }
        }, sinceSeq);
        await tcs.Task.WaitAsync(ct);
        hbCts.Cancel();
    }
    catch (OperationCanceledException) { /* 客户端断开 */ }
    catch (Exception ex) { Console.WriteLine($"[SSE {id}] 异常：{ex.Message}"); }
});

// ── 桌面 GUI 实时流（手机观看桌面执行流）：GUI 事件镜像入站（仅回环）+ 只读 SSE 出站 ──
// GUI 推送：POST /gui-live/{id} body = SessionEventDto 数组（uiTimer 每拍批量一组，最多 MaxEvPerBus=256 条）
app.MapPost("/gui-live/{id}", (string id, List<SessionEventDto>? events) =>
{
    if (string.IsNullOrWhiteSpace(id) || events is not { Count: > 0 }) return Results.NoContent();
    if (events.Count > 512) return Results.BadRequest(Err("单批事件超限（≤512）"));
    foreach (var e in events) liveHub.Post(id, e);
    return Results.NoContent();
});

// 手机观看：SSE /sessions/{id}/gui-events —— 先回放"进行中轮"缓冲，再订阅实时事件；
// plan-confirm：桌面弹 PlanConfirmDialog 前调用，暂存 pending + 推送 PlanConfirm 帧（手机端可见有待决策方案）
app.MapPost("/gui-live/{id}/plan-confirm", (string id, PlanConfirmBody body) =>
{
    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(body?.PlanId))
        return Results.BadRequest(Err("planId 必填"));
    GuiSync.WritePending(guiDir, id, body);
    liveHub.Post(id, new SessionEventDto { Type = "PlanConfirm", PlanConfirm = body.ToInfo() });
    return Results.Ok(new { status = "pending", planId = body.PlanId });
});

// 手机端决策回写：allow=true 批准 / false 拒绝 → 追加到 decisions.inbox.json（桌面 GUI 弹窗轮询消费），并清 pending。
// type=danger（或 planId 为空）：危险确认决策，planId 记空串，桌面 GUI 危险卡轮询按"会话+空 planId"消费
app.MapPost("/sessions/{id}/gui-decisions", (string id, GuiDecisionBody body) =>
{
    if (string.IsNullOrWhiteSpace(id) || body == null)
        return Results.BadRequest(Err("参数无效"));
    var dtype = (body.Type ?? "").ToLowerInvariant();
    if (dtype == "danger" || string.IsNullOrWhiteSpace(body.PlanId))
    {
        GuiSync.AppendDecision(guiDir, id, "", body.Allow);
        GuiSync.ClearPending(guiDir, id);
        GuiSync.ClearDangerPending(guiDir, id);   // 决策已回写：挂起标记即清，后续回放不再合成/保留该帧
        return Results.Ok(new { status = "decided", type = "danger", allow = body.Allow });
    }
    GuiSync.AppendDecision(guiDir, id, body.PlanId, body.Allow);
    GuiSync.ClearPending(guiDir, id);
    liveHub.Post(id, new SessionEventDto { Type = "PlanResolved", PlanConfirm = new PlanConfirmInfo { PlanId = body.PlanId, Allow = body.Allow } });
    return Results.Ok(new { status = "decided", planId = body.PlanId, allow = body.Allow });
});

// 桌面确认框收口（本机回环）：桌面用户本地确认/取消后调用，清 pending + 推 PlanResolved（手机端关闭确认 UI）
app.MapPost("/gui-live/{id}/plan-resolved", (string id, PlanConfirmBody body) =>
{
    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(body?.PlanId))
        return Results.BadRequest(Err("planId 必填"));
    GuiSync.ClearPending(guiDir, id);
    liveHub.Post(id, new SessionEventDto { Type = "PlanResolved", PlanConfirm = new PlanConfirmInfo { PlanId = body.PlanId, Allow = body.Allow } });
    return Results.Ok(new { status = "resolved", planId = body.PlanId });
});

// 手机观看：SSE /sessions/{id}/gui-events —— 先回放"进行中轮"缓冲，再订阅实时事件；
// 桌面未在执行（busy 标记无）且无缓冲时回 no_live 帧并关闭（客户端此时应回退常规 resume/history 路径）；
// 收到 Finished/Failed（桌面收口）后推给客户端并关闭连接
app.MapGet("/sessions/{id}/gui-events", async (string id, HttpContext ctx) =>
{
    // 桌面流不设归属闸：多人围观同一会话是本功能设计用途（trusted 圈子内共享）
    var busyNow = guiDir.Length > 0 && GuiSync.IsGuiBusy(guiDir, id);
    ctx.Response.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    await ctx.Response.WriteAsync("event: hello\ndata: {\"type\":\"connected\"}\n\n");
    await ctx.Response.Body.FlushAsync();
    try
    {
        var ct = ctx.RequestAborted;
        // 帧序统一串行：注册后到达的实时帧先进队列，回放帧先写、再逐个排空队列，
        // 避免"回放期间实时帧插队"造成旧帧晚于新帧到达的乱序渲染（旧 Round 覆盖新文本）；
        // Finished/Failed（桌面收口）写出后关流
        var queue = new System.Collections.Concurrent.ConcurrentQueue<SessionEventDto>();
        using var gate = new SemaphoreSlim(0);
        var attached = liveHub.Attach(id, e => { queue.Enqueue(e); gate.Release(); });
        using var sub = attached.Sub;
        async Task WriteFrameAsync(SessionEventDto e)
        {
            await ctx.Response.WriteAsync("data: " + JsonSerializer.Serialize(e, sseJson) + "\n\n", ct);
            await ctx.Response.Body.FlushAsync(ct);
        }
        // 危险挂起标记：在 = 桌面正挂起待决策（先补送合成帧，缓冲丢帧也能建卡）；无 = 历史帧均已决策，回放一律过滤
        var dangerPending = GuiSync.ReadDangerPending(guiDir, id);
        if (dangerPending != null)
            await WriteFrameAsync(new SessionEventDto { Type = "SecurityAlert", Alert = dangerPending });
        foreach (var e in attached.Replay)
        {
            if (dangerPending == null && e.Type == "SecurityAlert") continue;   // 已决策旧帧：重放不重显
            await WriteFrameAsync(e);
            if (e.Type is "Finished" or "Failed") return;
        }
        // 桌面无运行中任务且无 pending 决策：观看无内容可看（已落盘轮走 /history），明确告知客户端不挂长连接
        if (!busyNow && attached.Replay.Count == 0 && !GuiSync.HasPending(guiDir, id))
        {
            await ctx.Response.WriteAsync("data: {\"type\":\"no_live\"}\n\n", ct);
            await ctx.Response.Body.FlushAsync(ct);
            return;
        }
        while (true)
        {
            while (queue.TryDequeue(out var e))
            {
                await WriteFrameAsync(e);
                if (e.Type is "Finished" or "Failed") return;
            }
            // 带超时的等待 = 空闲心跳：桌面长任务无事件期防隧道/代理空闲掐线（注释行，客户端忽略）
            var got = await gate.WaitAsync(TimeSpan.FromSeconds(10), ct);
            if (!got)
            {
                await ctx.Response.WriteAsync(": ping\n\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
            }
        }
    }
    catch (OperationCanceledException) { /* 客户端断开 */ }
    catch (ObjectDisposedException) { /* 断开时信号量释放竞态，忽略 */ }
    catch (Exception ex) { Console.WriteLine($"[GUI-SSE {id}] 异常：{ex.Message}"); }
});

// 决策接口：type=danger 处理危险命令确认；type=plan 处理计划审批（Approve/Reject）
app.MapPost("/sessions/{id}/decisions", (string id, DecisionBody body, HttpContext ctx) =>
{
    var user = CurUser(ctx);
    var s = hub.Get(id);
    if (s == null) return Results.NotFound(Err("会话不存在"));
    if (!CanAccess(user, s.Owner)) return Results.NotFound(Err("会话不存在"));
    var type = (body.Type ?? "danger").ToLowerInvariant();
    if (type == "danger")
    {
        if (s.State != "pendingDecision") return Results.Conflict(Err($"当前无待决策项（状态 {s.State}）"));
        s.DecideDanger(body.Allow);
        return Results.Ok(new { status = "decided", type = "danger", allow = body.Allow });
    }
    if (type == "plan")
    {
        if (s.State != "pendingPlan") return Results.Conflict(Err($"当前无待审批计划（状态 {s.State}）"));
        s.ResolvePlan(body.Allow);
        return Results.Ok(new { status = "decided", type = "plan", planId = s.PendingPlan?.Id, allow = body.Allow });
    }
    return Results.BadRequest(Err("不支持的 decision 类型，仅支持 danger|plan"));
});

// 取消当前任务
app.MapPost("/sessions/{id}/cancel", (string id, HttpContext ctx) =>
{
    var user = CurUser(ctx);
    var s = hub.Get(id);
    if (s == null) return Results.NotFound(Err("会话不存在"));
    if (!CanAccess(user, s.Owner)) return Results.NotFound(Err("会话不存在"));
    s.Cancel();
    return Results.Ok(new { status = "cancelling" });
});

// 任务结果汇总
app.MapGet("/sessions/{id}/result", (string id) =>
{
    var s = hub.Get(id);
    if (s == null) return Results.NotFound(Err("会话不存在"));
    return Results.Json(s.GetResult());
});

// 删除会话（取消进行中任务 + 移除）
app.MapDelete("/sessions/{id}", (string id) =>
{
    var s = hub.Get(id);
    if (s == null) return Results.NotFound(Err("会话不存在"));
    s.Cancel();
    hub.Remove(id);
    return Results.NoContent();
});

// 释放会话回历史（幂等，供手机端“5 分钟无操作自动断开”等场景调用）：
// 仅空闲会话允许释放（从 hub 移除、断连）；执行中/待审批会话返回 409，由客户端自行取舍（仅本地断开）
app.MapPost("/sessions/{id}/release", (string id) =>
{
    var s = hub.Get(id);
    if (s == null) return Results.NotFound(Err("会话不存在或已释放"));
    if (s.State != "idle") return Results.Conflict(Err("会话正在执行（" + s.State + "），暂不能释放"));
    hub.Remove(id);
    return Results.Json(new { sessionId = id, status = "released" });
});

// 四层检索直出（独立于任务的只读接口）
app.MapGet("/search", (string q, string? project, int max = 5) =>
{
    if (string.IsNullOrWhiteSpace(q)) return Results.BadRequest(Err("q 必填"));
    var cfg = new AppConfig();
    if (!string.IsNullOrWhiteSpace(project)) cfg.ProjectRoot = Path.GetFullPath(project);
    SystemCfg.Init();
    return Results.Text(SmartSearch.Run(cfg, q, max));
});

// ── 计划审批接口 ──
app.MapGet("/sessions/{id}/plans", (string id) =>
{
    var s = hub.Get(id);
    if (s == null) return Results.NotFound(Err("会话不存在"));
    return Results.Json(GAIRR.Core.PlanStore.LoadForSession(s.Config, id));
});

app.MapGet("/plans", (string? project) =>
{
    var cfg = new AppConfig();
    if (!string.IsNullOrWhiteSpace(project)) cfg.ProjectRoot = Path.GetFullPath(project);
    return Results.Json(GAIRR.Core.PlanStore.LoadAll(cfg));
});

// ── 任务管理接口 ──
app.MapGet("/sessions/{id}/tasks", (string id) =>
{
    var s = hub.Get(id);
    if (s == null) return Results.NotFound(Err("会话不存在"));
    return Results.Json(new TaskStore(s.Config).LoadTasks());
});

app.MapGet("/sessions/{id}/tasks/{taskId}", (string id, string taskId) =>
{
    var s = hub.Get(id);
    if (s == null) return Results.NotFound(Err("会话不存在"));
    var task = new TaskStore(s.Config).LoadTasks().FirstOrDefault(t => t.Id == taskId);
    return task == null ? Results.NotFound(Err("任务不存在")) : Results.Json(task);
});

app.MapPost("/sessions/{id}/tasks", (string id, TaskItem body) =>
{
    var s = hub.Get(id);
    if (s == null) return Results.NotFound(Err("会话不存在"));
    var store = new TaskStore(s.Config);
    var tasks = store.LoadTasks();
    if (string.IsNullOrWhiteSpace(body.Id)) body.Id = TaskStore.NewId();
    body.ProjectPath = s.ProjectRoot;
    body.UpdatedAt = DateTime.Now;
    if (body.CreatedAt == default) body.CreatedAt = DateTime.Now;
    var idx = tasks.FindIndex(t => t.Id == body.Id);
    if (idx >= 0) tasks[idx] = body; else tasks.Add(body);
    store.SaveTasks(tasks);
    return Results.Json(body, statusCode: 201);
});

app.MapDelete("/sessions/{id}/tasks/{taskId}", (string id, string taskId) =>
{
    var s = hub.Get(id);
    if (s == null) return Results.NotFound(Err("会话不存在"));
    var store = new TaskStore(s.Config);
    var tasks = store.LoadTasks();
    var removed = tasks.RemoveAll(t => t.Id == taskId);
    if (removed == 0) return Results.NotFound(Err("任务不存在"));
    store.SaveTasks(tasks);
    return Results.NoContent();
});

app.MapPost("/sessions/{id}/tasks/{taskId}/run", (string id, string taskId) =>
{
    var s = hub.Get(id);
    if (s == null) return Results.NotFound(Err("会话不存在"));
    var store = new TaskStore(s.Config);
    var task = store.LoadTasks().FirstOrDefault(t => t.Id == taskId);
    if (task == null) return Results.NotFound(Err("任务不存在"));
    if (!task.Enabled) return Results.Conflict(Err("任务已禁用"));
    _ = Task.Run(async () =>
    {
        try
        {
            var runner = new TaskRunner(s.Config, store);
            await runner.RunAsync(task);
        }
        catch (Exception ex) { Console.WriteLine($"[任务 {taskId}] 手动运行异常：{ex.Message}"); }
    });
    return Results.Accepted(null, new { taskId, status = "running" });
});

app.MapGet("/sessions/{id}/tasks/{taskId}/runs", (string id, string taskId) =>
{
    var s = hub.Get(id);
    if (s == null) return Results.NotFound(Err("会话不存在"));
    return Results.Json(new TaskStore(s.Config).LoadRunsForTask(taskId));
});

Console.WriteLine($"gairr-agent-server 已启动：http://{host}:{port}（会话/任务接口 + SSE）");
await app.RunAsync();

static IResult Err(string msg) => Results.Json(new { error = msg });

/// <summary>带状态码的错误响应（Err 恒为 200；执行参数接口用真实 404/409/400，错误体同样是 {error:"..."}）。</summary>
static IResult ErrCode(string msg, int code) => Results.Json(new { error = msg }, statusCode: code);

// ── 请求体 ──
record SessionCreate(string Project, string? ConfigPath);
record DecisionBody(bool Allow, string Type = "danger", string? PlanId = null);

/// <summary>GUI 方案确认帧请求体（桌面 plan-confirm / plan-resolved 共用；Allow 仅 resolved 帧有效）。</summary>
record PlanConfirmBody(string PlanId, string? Title, string? Goal, string? PlanMode, string? SelectedCandidateId = null, int NodeCount = 0, bool Allow = false)
{
    public PlanConfirmInfo ToInfo() => new()
    {
        PlanId = PlanId,
        Title = Title,
        Goal = Goal,
        PlanMode = PlanMode,
        SelectedCandidateId = SelectedCandidateId,
        NodeCount = NodeCount,
        Allow = Allow,
    };
}

/// <summary>手机端 GUI 方案决策回写请求体：allow=true 批准 / false 拒绝。</summary>
record GuiDecisionBody(string PlanId, bool Allow = true, string Type = "");

/// <summary>会话执行参数切换请求体：role+mode 与 provider+model 各成对（留空=该项保持不变）。</summary>
record ExecBody(string? Role, string? Mode, string? Provider, string? Model);

/// <summary>决策收件箱条目（decisions.inbox.json 数组元素；桌面 GUI 弹窗轮询消费）。</summary>
sealed class GuiDecisionEntry
{
    public string SessionId { get; set; } = "";
    public string PlanId { get; set; } = "";
    public bool Allow { get; set; }
    public string At { get; set; } = "";
}

/// <summary>会话列表行（内部排序用，序列化前再投影 camelCase 匿名对象）</summary>
record SessionRow(string SessionId, string Project, string Title, string Status, bool Active, string State, string CreatedAt, string UpdatedAt, bool IsOrchestration, string Category, bool Busy = false);

/// <summary>会话集：进程内 ConcurrentDictionary（多会话可并行，互不串扰）。
/// idleEvictMin&gt;0 时每分钟扫描一次：空闲超时且非 busy 的会话移出 hub ——
/// 对话历史已落盘（SyncRecord），下次访问经 /resume 无感恢复，避免多用户长期使用内存只涨不回收</summary>
sealed class SessionHub
{
    readonly ConcurrentDictionary<string, AgentSession> map = new(StringComparer.OrdinalIgnoreCase);
    readonly object gate = new();
    readonly int idleEvictMin;
    readonly System.Threading.Timer? evictTimer;

    public SessionHub(int idleEvictMin)
    {
        this.idleEvictMin = idleEvictMin;
        if (idleEvictMin > 0)
            evictTimer = new Timer(_ => EvictIdle(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public AgentSession? Get(string id) => map.TryGetValue(id, out var s) ? s : null;
    public IEnumerable<AgentSession> All => map.Values;
    public string Create(string projectRoot, string? configPath)
    {
        var id = "s-" + Guid.NewGuid().ToString("N")[..8];
        map[id] = new AgentSession(id, projectRoot, configPath);
        return id;
    }
    /// <summary>恢复历史会话进 hub（幂等）：AgentSession 构造自动从磁盘 Load 对话历史回灌 Loop</summary>
    public AgentSession Resume(string id, string projectRoot)
    {
        if (map.TryGetValue(id, out var hit)) return hit;
        lock (gate)   // 并发重复恢复保护：同一 id 只构造一个实例
        {
            if (map.TryGetValue(id, out hit)) return hit;
            hit = new AgentSession(id, projectRoot);
            map[id] = hit;
            return hit;
        }
    }
    public void Remove(string id) => map.TryRemove(id, out _);
    public int Count => map.Count;

    void EvictIdle()
    {
        try
        {
            foreach (var s in map.Values)
            {
                if (s.Busy || s.IdleMinutesUtc < idleEvictMin) continue;
                map.TryRemove(s.Id, out _);   // 只摘引用：对话已落盘，resume 时重建
            }
        }
        catch { /* 驱逐异常不影响服务 */ }
    }
}