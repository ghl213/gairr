// gairr-stats-server：GAIRR 桌面端匿名使用统计 + 版本检查服务
// - 客户端启动时 POST /api/report：携带 deviceId 与最近未上报的按天累计秒数；服务端记录 IP、首次/最后活跃时间、版本
// - 同一请求返回最新版本信息（latest.json），客户端据此提示更新
// - GET /health 健康检查；GET /api/overview 简易汇总看板（JSON）
// 存储：data/stats.json（devices 主档 + 按设备×日期秒数明细，语义见 StatsStore.cs），
//       每次上报后 tmp+rename 原子落盘；文件缺失/损坏 → 启动时从零重建。
// JSON 解析/序列化用本工程 MiniJson（零第三方依赖）。
// 配置：环境变量 GAIRR_STATS_PORT / --port 覆盖监听端口（默认 8300）；latest.json 放数据目录手工维护
using GAIRR.StatServer;
using Microsoft.AspNetCore.Http;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var port = 8300;
for (int i = 0; i < args.Length; i++)
    if (args[i] == "--port" && i + 1 < args.Length) port = int.Parse(args[++i]);
var envPort = Environment.GetEnvironmentVariable("GAIRR_STATS_PORT");
if (!string.IsNullOrEmpty(envPort) && int.TryParse(envPort, out var p)) port = p;

// 数据目录：exe/data（与桌面端 data 约定一致）
var baseDir = AppContext.BaseDirectory;
var dataDir = Path.Combine(baseDir, "data");
Directory.CreateDirectory(dataDir);
var statsPath = Path.Combine(dataDir, "stats.json");
var latestPath = Path.Combine(dataDir, "latest.json");

var store = new StatsStore(statsPath);
store.Load();

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args });
builder.Logging.ClearProviders();
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
var app = builder.Build();

app.MapGet("/health", () => Json(MiniJson.Serialize(new Dictionary<string, object?>
{
    ["ok"] = true,
    ["service"] = "gairr-stats-server",
    ["time"] = DateTime.Now.ToString("s"),
})));

// ── 上报入口（客户端启动时调用一次）────────────────────────────
// body: { deviceId, os?, appVersion?, days?: [ {date:"yyyy-MM-dd", seconds}, ... ] }
// 服务端记录设备信息 + 每次上报作为一次活跃（last_seen 更新、devices 上累计），
// 并回传最新版本信息，让客户端同一请求完成"统计上报 + 版本检查"两件事。
app.MapPost("/api/report", async (HttpContext ctx) =>
{
    string body;
    using (var r = new StreamReader(ctx.Request.Body))
        body = await r.ReadToEndAsync();

    Dictionary<string, object?>? root;
    try { root = MiniJson.Parse(body) as Dictionary<string, object?>; }
    catch (JsonParseException) { return JsonErr("body 不是合法 JSON"); }
    if (root == null) return JsonErr("body 不是合法 JSON");

    var deviceId = (MiniJson.AsString(Field(root, "deviceId")) ?? "").Trim();
    if (deviceId.Length == 0) return JsonErr("deviceId 必填");
    if (deviceId.Length > 64) return JsonErr("deviceId 过长");
    var os = (MiniJson.AsString(Field(root, "os")) ?? "").Trim();
    var appVersion = (MiniJson.AsString(Field(root, "appVersion")) ?? "").Trim();

    // days: [{date, seconds}] — 非法行（非对象/日期格式不符/秒数非整数）跳过，不整体失败
    var days = new List<(string Date, long Seconds)>();
    var rawCount = 0;
    if (MiniJson.AsArray(Field(root, "days")) is { } arr)
    {
        rawCount = arr.Count;
        foreach (var item in arr)
        {
            if (item is not Dictionary<string, object?> day) continue;
            var sec = MiniJson.AsNumber(Field(day, "seconds"));
            if (double.IsNaN(sec) || sec != Math.Floor(sec)) continue;
            days.Add(((MiniJson.AsString(Field(day, "date")) ?? ""), (long)sec));
        }
    }

    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
    var now = DateTime.Now.ToString("s");
    store.Report(deviceId, os, appVersion, ip, now, days);   // 内部原子落盘

    // 版本信息回传：latest.json 缺省时用内置默认（与 GAIRR 桌面当前版本一致）
    var latest = LoadLatest(latestPath);
    var hasNew = !string.IsNullOrEmpty(latest.Version)
        && !string.IsNullOrEmpty(appVersion)
        && !appVersion.Equals(latest.Version, StringComparison.OrdinalIgnoreCase);
    return Json(MiniJson.Serialize(new Dictionary<string, object?>
    {
        ["ok"] = true,
        ["received"] = (double)rawCount,
        ["latest"] = latest.Version,
        ["updateUrl"] = latest.Url,
        ["note"] = latest.Note,
        ["hasUpdate"] = hasNew,
        ["time"] = now,
    }));
});

// ── 汇总看板（JSON，便于后续接前端；对已收录设备数/按日活跃/总时长做简单统计）──
app.MapGet("/api/overview", () => Json(MiniJson.Serialize(store.Overview())));

Console.WriteLine($"gairr-stats-server 已启动：http://0.0.0.0:{port}（数据目录 {dataDir}）");
await app.RunAsync();

// ─────────────────────────── 辅助 ───────────────────────────

static IResult Json(string body, int status = 200)
    => new JsonResult(body, status);

static IResult JsonErr(string msg)
    => Json(MiniJson.Serialize(new Dictionary<string, object?> { ["error"] = msg }), 400);

// 取字段（忽略大小写，与原 System.Text.Json PropertyNameCaseInsensitive 行为一致）
static object? Field(Dictionary<string, object?> obj, string key)
{
    if (obj.TryGetValue(key, out var v)) return v;
    foreach (var kv in obj)
        if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
    return null;
}

// 读取 latest.json（不存在返回空，即不提示更新）
static LatestInfo LoadLatest(string path)
{
    try
    {
        if (File.Exists(path))
        {
            var root = MiniJson.Parse(File.ReadAllText(path)) as Dictionary<string, object?>;
            if (root != null)
                return new LatestInfo
                {
                    Version = MiniJson.AsString(Field(root, "version")) ?? "",
                    Url = MiniJson.AsString(Field(root, "url")) ?? "",
                    Note = MiniJson.AsString(Field(root, "note")) ?? "",
                };
        }
    }
    catch { }
    return new LatestInfo();
}

/// <summary>JSON 响应结果（任意状态码；MVC ContentResult 对 IResult 仅显式实现，故自定义）。</summary>
sealed class JsonResult : IResult
{
    private readonly string _body;
    private readonly int _status;
    public JsonResult(string body, int status) { _body = body; _status = status; }
    public Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Response.StatusCode = _status;
        httpContext.Response.ContentType = "application/json; charset=utf-8";
        var bytes = System.Text.Encoding.UTF8.GetBytes(_body);
        return httpContext.Response.Body.WriteAsync(bytes, 0, bytes.Length);
    }
}

record LatestInfo { public string Version { get; set; } = ""; public string Url { get; set; } = ""; public string Note { get; set; } = ""; }
