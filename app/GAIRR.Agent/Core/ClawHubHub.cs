using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;

namespace GAIRR.Core;

/// <summary>
/// ClawHub 市场源（clawhub.ai，匿名可用）：搜索 /api/v1/search、热度榜 /api/v1/trending、
/// 下载 /api/v1/download?slug=&amp;ownerHandle=（返回 zip，内含 SKILL.md）。
/// 安装复用 Market.InstallAsync 链路（下载→解包→结构校验→落盘 skills/→追溯记录）。
/// 端点清单来自官方 CLI（npm: clawhub）routes 源码，已实测可用。
/// </summary>
public static class ClawHubHub
{
    /// <summary>ClawHub 技能条目（搜索结果/热度榜共用行结构）</summary>
    public record HubItem(string Slug, string OwnerHandle, string DisplayName, string Summary,
        string Version, long Downloads, int Stars, string CanonicalUrl);

    const string Base = "https://clawhub.ai";
    const int MaxItems = 10;            // 搜索/热榜单次条数上限（防刷屏）
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    static ClawHubHub()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("gairr-market/1.0");
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    /* ---------- 搜索 / 热度榜 ---------- */

    /// <summary>按关键词搜索技能（/api/v1/search?q=），取前 10 条；异常抛出由调用方处理</summary>
    public static async Task<List<HubItem>> SearchAsync(string q, CancellationToken ct)
    {
        var url = $"{Base}/api/v1/search?q={Uri.EscapeDataString(q)}&limit={MaxItems}";
        var root = JsonNode.Parse(await Http.GetStringAsync(url, ct)) as JsonObject;
        var list = new List<HubItem>();
        if (root?["results"] is JsonArray arr)
            foreach (var it in arr)
                if (it is JsonObject o) list.Add(ParseResult(o));
        return list;
    }

    /// <summary>拉取热度榜（/api/v1/trending，按近期热度排序），取前 10 条</summary>
    public static async Task<List<HubItem>> TrendingAsync(CancellationToken ct)
    {
        var url = $"{Base}/api/v1/trending?limit={MaxItems}";
        var root = JsonNode.Parse(await Http.GetStringAsync(url, ct)) as JsonObject;
        var list = new List<HubItem>();
        if (root?["items"] is JsonArray arr)
            foreach (var it in arr)
                if (it is JsonObject o) list.Add(ParseResult(o));
        return list;
    }

    /// <summary>解析搜索/热榜单条：displayName/slug/summary/下载量/星数/版本号；字段缺失给兜底值</summary>
    static HubItem ParseResult(JsonObject o)
    {
        var slug = o["slug"]?.GetValue<string>() ?? "";
        var owner = o["ownerHandle"]?.GetValue<string>() ?? "";
        // 下载量：顶层 downloads 优先；无则取 native.skill.stats.downloads（两种响应形态都兼容）
        var dl = o["downloads"]?.GetValue<long?>() ?? 0;
        var stars = 0;
        string ver = "";
        if (o["native"] is JsonObject nv && nv["skill"] is JsonObject sk)
        {
            if (sk["stats"] is JsonObject st)
            {
                if (dl == 0) dl = st["downloads"]?.GetValue<long?>() ?? 0;
                stars = st["stars"]?.GetValue<int?>() ?? 0;
            }
            ver = sk["tags"] is JsonObject tg ? tg["latest"]?.GetValue<string>() ?? "" : "";
        }
        // 热榜 items 形态：stats 在顶层、版本在 tags.latest
        if (dl == 0 && o["stats"] is JsonObject st2) dl = st2["downloads"]?.GetValue<long?>() ?? 0;
        if (stars == 0 && o["stats"] is JsonObject st3) stars = st3["stars"]?.GetValue<int?>() ?? 0;
        if (ver.Length == 0 && o["tags"] is JsonObject tg2) ver = tg2["latest"]?.GetValue<string>() ?? "";
        return new HubItem(slug, owner,
            o["displayName"]?.GetValue<string>() ?? slug,
            o["summary"]?.GetValue<string>() ?? "",
            ver, dl, stars,
            o["canonicalUrl"]?.GetValue<string>() ?? "");
    }

    /* ---------- 安装 ---------- */

    /// <summary>把 ClawHub 条目转成目录册条目（Url 指向官方 download 端点，可直接走 Market.InstallAsync）：
    /// kind 固定 skill；name 用 slug；Source 记为 clawhub:owner/slug 供追溯与识别</summary>
    public static CatalogEntry ToCatalogEntry(HubItem h) => new("skill", h.Slug, h.Summary, h.Version,
        $"{Base}/api/v1/download?slug={Uri.EscapeDataString(h.Slug)}" +
        (h.OwnerHandle.Length > 0 ? "&ownerHandle=" + Uri.EscapeDataString(h.OwnerHandle) : ""),
        "", "clawhub:" + (h.OwnerHandle.Length > 0 ? h.OwnerHandle + "/" : "") + h.Slug,
        h.Downloads + "|" + h.Stars);   // Extra=下载量|星标数（列表名称下方数量行显示用）

    /// <summary>判断追溯来源是否 ClawHub（source 以 clawhub: 开头）</summary>
    public static bool IsClawHubSource(string source) => source.StartsWith("clawhub:", StringComparison.OrdinalIgnoreCase);

    /// <summary>市场详情页地址（标题悬停/跳转用）</summary>
    public static string PageUrl(HubItem h) => Base + (h.CanonicalUrl.Length > 0 ? h.CanonicalUrl : "/skills/" + h.Slug);
}
