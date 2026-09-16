using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json.Nodes;

namespace GAIRR.Core;

/// <summary>
/// Gitee 市场源（gitee.com，API v5）：匿名已被限制（搜索空、仓库 404、git 要凭证），
/// 需在 config.ini [Market] GiteeToken 配置个人访问令牌后才可用。
/// 链路：搜索 /api/v5/search/repositories → 下载仓库 zip /api/v5/repos/{o}/{r}/zip
///       → 定位 SKILL.md → 落盘 skills/ → 追溯记录（与 GitHubHub 安装模式一致）。
/// </summary>
public static class GiteeHub
{
    /// <summary>Gitee 仓库搜索结果行（与 GitHubHub.Repo 同构，供 UI 直接渲染）</summary>
    public record Repo(string FullName, int Stars, string Description);

    const string Api = "https://gitee.com/api/v5";
    const int MaxZipBytes = 50 * 1024 * 1024;   // 与 Market 一致
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

    static GiteeHub()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("gairr-market/1.0");
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    /// <summary>读取配置的 Gitee token（[Market] GiteeToken），空=未配置/匿名不可用</summary>
    public static string Token(AppConfig cfg) => cfg.Get("Market", "GiteeToken", "").Trim();

    /// <summary>是否已配置 token（决定该源是否启用）</summary>
    public static bool Enabled(AppConfig cfg) => Token(cfg).Length > 0;

    /* ---------- 搜索 ---------- */

    /// <summary>按关键词搜索 Gitee 仓库（按 star 排序取前 8）；未配置 token 返回空列表。
    /// 异常抛出由调用方处理。</summary>
    public static async Task<List<Repo>> SearchItemsAsync(AppConfig cfg, string q, CancellationToken ct)
    {
        var token = Token(cfg);
        if (token.Length == 0) return new List<Repo>();
        var url = $"{Api}/search/repositories?q={Uri.EscapeDataString(q)}&sort=stars_desc&page=1&per_page=8&access_token={token}";
        var root = JsonNode.Parse(await Http.GetStringAsync(url, ct));
        var list = new List<Repo>();
        if (root is JsonArray items)
            foreach (var it in items)
            {
                if (it is not JsonObject o) continue;
                var name = o["path"]?.GetValue<string>() ?? "";
                var owner = o["owner"] is JsonObject ow ? ow["login"]?.GetValue<string>() ?? "" : "";
                var full = owner.Length > 0 && name.Length > 0 ? owner + "/" + name : "";
                if (full.Length == 0) continue;
                list.Add(new Repo(full, o["stargazers_count"]?.GetValue<int>() ?? 0,
                    OneLine(o["description"]?.GetValue<string>())));
            }
        return list;
    }

    /// <summary>文本版搜索结果清单（供模型工具用，失败返回错误文本不抛异常）</summary>
    public static async Task<string> SearchAsync(AppConfig cfg, string q, CancellationToken ct)
    {
        if (!Enabled(cfg)) return "Gitee 源未启用：请在 config.ini [Market] GiteeToken 配置个人访问令牌。";
        try
        {
            var list = await SearchItemsAsync(cfg, q, ct);
            if (list.Count == 0) return "Gitee 搜索无结果。";
            var sb = new System.Text.StringBuilder($"Gitee 仓库搜索结果（按 star 排序，前 {list.Count}）：\n");
            foreach (var r in list)
                sb.Append("- ").Append(r.FullName).Append(" ★").Append(r.Stars)
                  .Append("：").Append(r.Description).Append('\n');
            return sb.ToString();
        }
        catch (Exception ex) { return "Gitee 搜索失败：" + ex.Message; }
    }

    /* ---------- 安装技能 ---------- */

    /// <summary>下载 Gitee 仓库 zip 并安装其中技能。逻辑与 GitHubHub.InstallSkillAsync 对齐：
    /// 单技能仓直接装；多技能仓需 skill 参数指定子目录；技能名取 SKILL.md frontmatter name。</summary>
    public static async Task<GitHubHub.InstallResult> InstallSkillAsync(AppConfig cfg, string repo, string skill,
        CancellationToken ct, Action<string>? onProgress = null)
    {
        repo = NormalizeRepo(repo);
        if (repo.Length == 0) return new GitHubHub.InstallResult(false, "错误：repo 参数格式应为 owner/repo", Empty, "");
        var token = Token(cfg);
        if (token.Length == 0) return new GitHubHub.InstallResult(false, "错误：Gitee 未配置 token（config.ini [Market] GiteeToken）", Empty, "");
        var tmpZip = Path.Combine(Path.GetTempPath(), $"gairr-gitee-{Guid.NewGuid():N}.zip");
        var tmpDir = Path.Combine(Path.GetTempPath(), $"gairr-gitee-{Guid.NewGuid():N}");
        try
        {
            await DownloadAndUnpackAsync(cfg, repo, tmpZip, tmpDir, ct, onProgress);
            onProgress?.Invoke("定位技能…");
            var candidates = FindSkillFiles(tmpDir).Take(GitHubHub.MaxCandidates).ToList();
            if (candidates.Count == 0)
                return new GitHubHub.InstallResult(false, $"错误：仓库 {repo} 中未找到 SKILL.md（非技能仓库）", Empty, "");

            string skillDir;
            if (candidates.Count == 1) skillDir = Path.GetDirectoryName(candidates[0])!;
            else
            {
                var names = candidates.Select(c => RelSkillName(tmpDir, c)).ToList();
                if (skill.Length == 0)
                    return new GitHubHub.InstallResult(false, $"仓库 {repo} 含 {candidates.Count} 个技能，请指定其一", names, "");
                var idx = names.FindIndex(n => n.Equals(skill, StringComparison.OrdinalIgnoreCase)
                    || n.Split('/').Last().Equals(skill, StringComparison.OrdinalIgnoreCase));
                if (idx < 0) return new GitHubHub.InstallResult(false, $"错误：仓库中无名为 {skill} 的技能目录", Empty, "");
                skillDir = Path.GetDirectoryName(candidates[idx])!;
            }

            var skillMd = Path.Combine(skillDir, "SKILL.md");
            var name = ParseFrontmatterName(skillMd);
            if (name.Length == 0) name = Path.GetFileName(skillDir);
            onProgress?.Invoke($"写入 skills/{name}…");
            var raw = cfg.Get("Skills", "Dir", "skills");
            var skillRoot = Path.IsPathRooted(raw) ? raw : Path.Combine(AppContext.BaseDirectory, raw);
            var dest = Path.Combine(skillRoot, name);
            if (Directory.Exists(dest)) Directory.Delete(dest, true);
            Directory.CreateDirectory(skillRoot);
            CopyDir(skillDir, dest);

            Market.RecordMeta(cfg, new CatalogEntry("skill", name, OneLine(""), "gitee", "", "", "gitee:" + repo));
            return new GitHubHub.InstallResult(true,
                $"已安装技能 {name}（来自 gitee:{repo}）→ {dest}；技能热加载约 2 秒后生效。", Empty, name);
        }
        catch (Exception ex) { return new GitHubHub.InstallResult(false, "安装失败：" + ex.Message, Empty, ""); }
        finally { TryDelete(tmpZip); TryDeleteDir(tmpDir); }
    }

    static readonly List<string> Empty = new();

    /* ---------- 小助手 ---------- */

    /// <summary>下载仓库 zip 并解包（带 token）；Gitee zip 端点需 access_token</summary>
    static async Task DownloadAndUnpackAsync(AppConfig cfg, string repo, string tmpZip, string tmpDir,
        CancellationToken ct, Action<string>? onProgress)
    {
        onProgress?.Invoke("下载中…");
        var token = Token(cfg);
        var url = $"{Api}/repos/{repo}/zip?access_token={token}";
        var bytes = await Http.GetByteArrayAsync(url, ct);
        if (bytes.Length > MaxZipBytes) throw new InvalidOperationException("仓库 zip 超限（>50MB），无法安装");
        onProgress?.Invoke($"下载完成 {bytes.Length / 1024 / 1024.0:0.#}MB，解包…");
        await File.WriteAllBytesAsync(tmpZip, bytes, ct);
        Directory.CreateDirectory(tmpDir);
        using var zip = ZipFile.OpenRead(tmpZip);
        foreach (var ze in zip.Entries)
        {
            var full = Path.GetFullPath(Path.Combine(tmpDir, ze.FullName));
            if (!full.StartsWith(Path.GetFullPath(tmpDir), StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrEmpty(ze.Name)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            ze.ExtractToFile(full, true);
        }
    }

    /// <summary>归一化 owner/repo（接受 https://gitee.com/owner/repo(.git)）</summary>
    static string NormalizeRepo(string repo)
    {
        var s = repo.Trim().TrimEnd('/');
        if (s.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            var i = s.IndexOf("gitee.com/", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return "";
            s = s[(i + 10)..];
        }
        if (s.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        return s.Split('/').Length == 2 ? s : "";
    }

    /// <summary>递归找 SKILL.md（限深 4 层，跳隐藏目录）</summary>
    static IEnumerable<string> FindSkillFiles(string dir, int depth = 0)
    {
        if (depth > 4) yield break;
        foreach (var f in Directory.GetFiles(dir, "SKILL.md")) yield return f;
        foreach (var d in Directory.GetDirectories(dir))
        {
            if (Path.GetFileName(d).StartsWith('.')) continue;
            foreach (var f in FindSkillFiles(d, depth + 1)) yield return f;
        }
    }

    static string RelSkillName(string root, string skillMd)
    {
        var rel = Path.GetRelativePath(root, Path.GetDirectoryName(skillMd)!).Replace('\\', '/');
        var parts = rel.Split('/');
        return parts.Length > 1 && parts[0].Contains('-') ? string.Join('/', parts[1..]) : rel;
    }

    static string ParseFrontmatterName(string path)
    {
        try
        {
            var lines = File.ReadAllLines(path);
            if (lines.Length == 0 || lines[0].Trim() != "---") return "";
            for (var i = 1; i < lines.Length; i++)
            {
                var t = lines[i].Trim();
                if (t == "---") break;
                var ci = t.IndexOf(':');
                if (ci > 0 && t[..ci].Trim().Equals("name", StringComparison.OrdinalIgnoreCase))
                    return t[(ci + 1)..].Trim();
            }
        }
        catch { }
        return "";
    }

    static string OneLine(string? s) => (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();

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
