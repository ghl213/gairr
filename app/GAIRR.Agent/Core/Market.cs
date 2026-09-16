using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GAIRR.Core;

/// <summary>目录册条目：市场索引中的一行（插件或技能），来源可为多个市场源合并结果。
/// Extra 为可选补充指标（如 ClawHub 下载量数字串），供列表热度展示用。</summary>
public record CatalogEntry(string Kind, string Name, string Desc, string Version, string Url, string Sha256, string Source, string Extra = "");

/// <summary>
/// 市场对接层：从配置的市场源（Market.Sources，逗号分隔 URL 或本地 json 路径）拉取目录册，
/// 提供安装/更新/卸载。安装包落盘到 plugins/ 或 skills/ 后，现有加载链路（插件热加载/技能热加载）自动接管。
/// 来源追溯写入 项目根 .gairr/market.json（{名称: kind/version/source/installed}）。
/// </summary>
public static class Market
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    const int MaxZipBytes = 50 * 1024 * 1024;   // 单包上限 50MB，防恶意大包

    /// <summary>安装结果：success + 人读消息（供工具直接返回给模型）</summary>
    public record Result(bool Success, string Message);

    /* ---------- 目录与路径 ---------- */

    /// <summary>插件/技能本地目录（与 PluginLoader/SkillLoader 同规则：相对路径基于 exe 运行目录）</summary>
    static string Dir(AppConfig cfg, string section, string key, string def)
    {
        var raw = cfg.Get(section, key, def);
        return Path.IsPathRooted(raw) ? raw : Path.Combine(AppContext.BaseDirectory, raw);
    }
    static string PluginDir(AppConfig cfg) => Dir(cfg, "Plugins", "Dir", "plugins");
    static string SkillDir(AppConfig cfg) => Dir(cfg, "Skills", "Dir", "skills");

    /// <summary>来源追溯文件：项目根 .gairr/market.json</summary>
    static string MetaPath(AppConfig cfg) => Path.Combine(cfg.ProjectRoot, ".gairr", "market.json");

    /* ---------- 目录册 ---------- */

    /// <summary>拉取并合并所有市场源的目录册。源列表来自配置 Market.Sources（逗号分隔）。
    /// catalog.json 格式：{ "plugins":[{name,desc,version,url,sha256}], "skills":[...] }</summary>
    public static async Task<List<CatalogEntry>> FetchCatalogAsync(AppConfig cfg, CancellationToken ct = default)
    {
        var sources = cfg.Get("Market", "Sources", "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var all = new List<CatalogEntry>();
        foreach (var src in sources)
        {
            try
            {
                var isHttp = src.StartsWith("http", StringComparison.OrdinalIgnoreCase);
                // 本地源相对路径按 exe 运行目录解析（与 Plugins.Dir/Skills.Dir 约定一致）
                var local = !isHttp && !Path.IsPathRooted(src) ? Path.Combine(AppContext.BaseDirectory, src) : src;
                var json = isHttp
                    ? await Http.GetStringAsync(src, ct)
                    : await File.ReadAllTextAsync(local, ct);
                var root = JsonNode.Parse(json) as JsonObject;
                if (root == null) continue;
                foreach (var kind in new[] { "plugins", "skills" })
                    if (root[kind] is JsonArray arr)
                        foreach (var node in arr)
                        {
                            if (node is not JsonObject o) continue;
                            var name = o["name"]?.GetValue<string>() ?? "";
                            if (name.Length == 0) continue;
                            all.Add(new CatalogEntry(
                                kind == "plugins" ? "plugin" : "skill",
                                name,
                                o["desc"]?.GetValue<string>() ?? "",
                                o["version"]?.GetValue<string>() ?? "",
                                ResolveUrl(o["url"]?.GetValue<string>() ?? "", isHttp ? src : local, isHttp),
                                o["sha256"]?.GetValue<string>() ?? "",
                                src));
                        }
            }
            catch { /* 单源失败不影响其余源 */ }
        }
        return all;
    }

    /// <summary>相对包 url 解析：http 源按源 URL 解析，本地源按目录册文件所在目录解析
    /// （市场目录可整体迁移/拷贝，包路径始终相对 catalog.json）；绝对/http 地址原样返回</summary>
    static string ResolveUrl(string url, string source, bool isHttp)
    {
        if (url.Length == 0 || url.StartsWith("http", StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(url)) return url;
        if (isHttp)
        {
            if (Uri.TryCreate(new Uri(source), url, out var abs)) return abs.AbsoluteUri;
            return url;
        }
        var dir = Path.GetDirectoryName(Path.GetFullPath(source));
        return dir == null ? url : Path.GetFullPath(Path.Combine(dir, url));
    }

    /* ---------- 安装 / 更新 ---------- */

    /// <summary>按目录册条目安装（同名已装则覆盖=更新）。
    /// 流程：下载/读包 → sha256 校验（目录册提供时）→ 解包到临时目录 → 结构校验 → 落盘 → 记录追溯 → 插件即时注册。</summary>
    public static async Task<Result> InstallAsync(AppConfig cfg, ToolRegistry reg, CatalogEntry entry, CancellationToken ct = default)
    {
        if (entry.Url.Length == 0) return new Result(false, "错误：条目缺少 url");
        var targetRoot = entry.Kind == "plugin" ? PluginDir(cfg) : SkillDir(cfg);
        var dest = Path.Combine(targetRoot, entry.Name);
        var tmpZip = Path.Combine(Path.GetTempPath(), $"gairr-mkt-{entry.Name}-{Guid.NewGuid():N}.zip");
        var tmpDir = Path.Combine(Path.GetTempPath(), $"gairr-mkt-{entry.Name}-{Guid.NewGuid():N}");
        try
        {
            // 1. 获取包（http 或本地路径）
            if (entry.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = await Http.GetByteArrayAsync(entry.Url, ct);
                if (bytes.Length > MaxZipBytes) return new Result(false, "错误：包体积超限（>50MB）");
                await File.WriteAllBytesAsync(tmpZip, bytes, ct);
            }
            else File.Copy(entry.Url, tmpZip, true);

            // 2. sha256 校验（目录册提供时强制执行，防投毒）
            if (entry.Sha256.Length > 0)
            {
                using var sha = SHA256.Create();
                var actual = Convert.ToHexString(await sha.ComputeHashAsync(File.OpenRead(tmpZip), ct)).ToLowerInvariant();
                if (!actual.Equals(entry.Sha256.Trim().ToLowerInvariant(), StringComparison.OrdinalIgnoreCase))
                    return new Result(false, "错误：sha256 校验失败，已中止安装");
            }

            // 3. 解包到临时目录（过滤路径穿越）
            Directory.CreateDirectory(tmpDir);
            using (var zip = ZipFile.OpenRead(tmpZip))
            {
                foreach (var ze in zip.Entries)
                {
                    var full = Path.GetFullPath(Path.Combine(tmpDir, ze.FullName));
                    if (!full.StartsWith(Path.GetFullPath(tmpDir), StringComparison.OrdinalIgnoreCase)) continue; // 路径穿越
                    if (string.IsNullOrEmpty(ze.Name)) continue; // 目录项
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    ze.ExtractToFile(full, true);
                }
            }

            // 4. 结构校验 + 归一化根（兼容包内单顶层目录）
            var root = NormalizeRoot(tmpDir, entry.Kind, out var err);
            if (root == null) return new Result(false, err);

            // 5. 落盘（已装覆盖：先删旧目录）
            if (Directory.Exists(dest)) Directory.Delete(dest, true);
            Directory.CreateDirectory(targetRoot);
            CopyDir(root, dest);

            // 6. 来源追溯
            WriteMeta(cfg, entry);

            // 7. 插件即时注册（热加载也会兜底，但 CLI/无 watcher 场景需要显式触发）
            if (entry.Kind == "plugin") PluginLoader.Load(reg, cfg);

            return new Result(true, $"已安装{KindCn(entry.Kind)} {entry.Name}" +
                (entry.Version.Length > 0 ? " v" + entry.Version : "") + $" → {dest}");
        }
        catch (Exception ex)
        {
            return new Result(false, "安装失败：" + ex.Message);
        }
        finally
        {
            TryDelete(tmpZip); TryDeleteDir(tmpDir);
        }
    }

    /// <summary>校验包结构并归一化根目录：要求含 plugin.ini（插件）或 SKILL.md（技能）；
    /// 若文件都在唯一顶层子目录内则以该子目录为根（兼容 GitHub 风格打包）</summary>
    static string? NormalizeRoot(string tmpDir, string kind, out string err)
    {
        var marker = kind == "plugin" ? "plugin.ini" : "SKILL.md";
        if (File.Exists(Path.Combine(tmpDir, marker)))
        {
            if (kind == "plugin" && !ValidPluginIni(Path.Combine(tmpDir, marker), out err)) return null;
            err = ""; return tmpDir;
        }
        var subs = Directory.GetDirectories(tmpDir);
        if (subs.Length == 1 && File.Exists(Path.Combine(subs[0], marker)))
        {
            if (kind == "plugin" && !ValidPluginIni(Path.Combine(subs[0], marker), out err)) return null;
            err = ""; return subs[0];
        }
        err = $"错误：包结构无效（缺少 {marker}）";
        return null;
    }

    /// <summary>plugin.ini 必填项校验：Name + Command（与 PluginLoader.TryLoad 的失败口径一致）</summary>
    static bool ValidPluginIni(string ini, out string err)
    {
        err = "";
        var hasName = false; var hasCmd = false;
        foreach (var raw in File.ReadAllLines(ini))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('[')) continue;
            var ei = line.IndexOf('=');
            if (ei <= 0) continue;
            var k = line[..ei].Trim(); var v = line[(ei + 1)..].Trim();
            if (k.Equals("Name", StringComparison.OrdinalIgnoreCase) && v.Length > 0) hasName = true;
            if (k.Equals("Command", StringComparison.OrdinalIgnoreCase) && v.Length > 0) hasCmd = true;
        }
        if (!hasName || !hasCmd) err = "错误：plugin.ini 缺少必填项（Name/Command）";
        return err.Length == 0;
    }

    /* ---------- 卸载 ---------- */

    /// <summary>卸载：删本地目录 + 摘除工具注册（插件）+ 清追溯记录。技能由热加载自动摘除清单。</summary>
    public static Result Uninstall(AppConfig cfg, ToolRegistry reg, string kind, string name)
    {
        try
        {
            var targetRoot = kind == "plugin" ? PluginDir(cfg) : SkillDir(cfg);
            var dest = Path.Combine(targetRoot, name);
            if (!Directory.Exists(dest)) return new Result(false, "错误：未安装 " + name);
            // 删除前先读包内 plugin.ini 的工具名（可能与目录名不同，卸载后需显式摘除注册，否则残留死工具）
            var toolName = "";
            if (kind == "plugin")
            {
                var iniPath = Path.Combine(dest, "plugin.ini");
                if (File.Exists(iniPath)) toolName = ReadIniValue(iniPath, "Name");
            }
            Directory.Delete(dest, true);
            if (kind == "plugin")
            {
                PluginLoader.Load(reg, cfg);                          // 重扫其余插件（同名覆盖）
                reg.Unregister(toolName.Length > 0 ? toolName : name); // 摘除已删插件的注册
            }
            RemoveMeta(cfg, name);
            return new Result(true, $"已卸载{KindCn(kind)} {name}");
        }
        catch (Exception ex) { return new Result(false, "卸载失败：" + ex.Message); }
    }

    /* ---------- 追溯记录 .gairr/market.json ---------- */

    static JsonObject ReadMeta(AppConfig cfg)
    {
        try
        {
            var p = MetaPath(cfg);
            return File.Exists(p) ? JsonNode.Parse(File.ReadAllText(p)) as JsonObject ?? new JsonObject() : new JsonObject();
        }
        catch { return new JsonObject(); }
    }

    static void WriteMeta(AppConfig cfg, CatalogEntry e)
    {
        var meta = ReadMeta(cfg);
        meta[e.Name] = new JsonObject
        {
            ["kind"] = e.Kind, ["version"] = e.Version,
            ["source"] = e.Source, ["installed"] = DateTime.Now.ToString("s"),
        };
        SaveMeta(cfg, meta);
    }

    static void RemoveMeta(AppConfig cfg, string name)
    {
        var meta = ReadMeta(cfg);
        meta.Remove(name);
        SaveMeta(cfg, meta);
    }

    static void SaveMeta(AppConfig cfg, JsonObject meta)
    {
        var p = MetaPath(cfg);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, meta.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>外部源（如 GitHub 源）安装完成后经此写入追溯记录（共用 .gairr/market.json）</summary>
    public static void RecordMeta(AppConfig cfg, CatalogEntry e) => WriteMeta(cfg, e);

    /// <summary>查询已安装追溯记录（UI 标记"来自市场"用）；返回 {名称: kind|version|source}</summary>
    public static Dictionary<string, string> Installed(AppConfig cfg)
    {
        var r = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in ReadMeta(cfg))
            if (v is JsonObject o)
                r[k] = $"{o["kind"]?.GetValue<string>()}|{o["version"]?.GetValue<string>()}|{o["source"]?.GetValue<string>()}";
        return r;
    }

    /// <summary>已安装追溯条目（结构化，供市场"已安装"分组直接渲染）</summary>
    public record InstalledInfo(string Name, string Kind, string Version, string Source);

    /// <summary>以列表形式返回已安装追溯记录（市场面板"已安装"栏用，含 kind/version/source 供卸载）</summary>
    public static List<InstalledInfo> InstalledList(AppConfig cfg)
    {
        var list = new List<InstalledInfo>();
        foreach (var (k, v) in ReadMeta(cfg))
            if (v is JsonObject o)
                list.Add(new InstalledInfo(k,
                    o["kind"]?.GetValue<string>() ?? "skill",
                    o["version"]?.GetValue<string>() ?? "",
                    o["source"]?.GetValue<string>() ?? ""));
        return list;
    }

    /* ---------- 小助手 ---------- */

    static string KindCn(string kind) => kind == "plugin" ? "插件" : "技能";

    /// <summary>从扁平 ini 读单个键值（解析规则与 PluginLoader.ReadIni 一致：去注释、忽略节）</summary>
    static string ReadIniValue(string path, string key)
    {
        try
        {
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('[')) continue;
                var ei = line.IndexOf('=');
                if (ei <= 0) continue;
                if (line[..ei].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                    return line[(ei + 1)..].Trim();
            }
        }
        catch { /* 读失败按空名处理，卸载时回退用目录名 */ }
        return "";
    }

    static void CopyDir(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(src))
            CopyDir(d, Path.Combine(dest, Path.GetFileName(d)));
    }

    static void TryDelete(string f) { try { File.Delete(f); } catch { } }
    static void TryDeleteDir(string d) { try { Directory.Delete(d, true); } catch { } }
}
