using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace GAIRR.Core;

/// <summary>
/// GitHub 技能源：搜索他人公开仓库，安装其中含 SKILL.md 的技能（开放规范，免 token）。
/// 注意：plugin.ini 格式插件是 GAIRR 私有，GitHub 上搜不到；需要时按主提示词自开发规则自行包装。
/// 链路：搜索（api.github.com）→ 下载仓库 zip（codeload）→ 定位 SKILL.md → 落盘 skills/ → 追溯记录。
/// </summary>
public static class GitHubHub
{
    /// <summary>GitHub 仓库搜索结果行：owner/repo + star 数 + 单行描述（结构化，供 UI 列表直接渲染）</summary>
    public record Repo(string FullName, int Stars, string Description);

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };
    const int MaxZipBytes = 50 * 1024 * 1024;   // 单包上限 50MB（与 Market 一致）
    internal const int MaxCandidates = 20;      // 单仓最多列出的技能数（GiteeHub 复用）

    static GitHubHub()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("gairr-market/1.0");   // GitHub API 强制要求 UA
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /* ---------- 搜索 ---------- */

    /// <summary>按关键词搜索他人公开仓库（按 star 排序取前 8），返回结构化列表；异常抛出由调用方处理。
    /// 免 token 限流：搜索 10 次/分钟，超限抛 WebException/HttpRequestException。</summary>
    public static async Task<List<Repo>> SearchItemsAsync(string q, CancellationToken ct)
    {
        var url = $"https://api.github.com/search/repositories?q={Uri.EscapeDataString(q)}&sort=stars&order=desc&per_page=8";
        var root = JsonNode.Parse(await Http.GetStringAsync(url, ct)) as JsonObject;
        var list = new List<Repo>();
        if (root?["items"] is JsonArray items)
            foreach (var it in items)
            {
                if (it is not JsonObject o) continue;
                var name = o["full_name"]?.GetValue<string>() ?? "";
                if (name.Length == 0) continue;
                list.Add(new Repo(name, o["stargazers_count"]?.GetValue<int>() ?? 0,
                    OneLine(o["description"]?.GetValue<string>())));
            }
        return list;
    }

    /// <summary>文本版搜索结果清单（供模型工具 GhSearch 用，失败返回错误文本不抛异常）</summary>
    public static async Task<string> SearchAsync(string q, CancellationToken ct)
    {
        try
        {
            var list = await SearchItemsAsync(q, ct);
            if (list.Count == 0) return "GitHub 搜索无结果，换个关键词试试。";
            var sb = new StringBuilder($"GitHub 仓库搜索结果（按 star 排序，前 {list.Count}）：\n");
            foreach (var r in list)
                sb.Append("- ").Append(r.FullName).Append(" ★").Append(r.Stars)
                  .Append("：").Append(r.Description).Append('\n');
            sb.Append('\n').Append("安装其中技能：GhInstallSkill(repo=\"owner/repo\")；多技能仓加 skill=\"子目录名\" 指定。");
            return sb.ToString();
        }
        catch (Exception ex) { return "GitHub 搜索失败：" + ex.Message; }
    }

    /* ---------- 安装技能 ---------- */

    /// <summary>技能安装结构化结果（UI 与模型工具共用）。
    /// Candidates 非空 = 多技能仓需调用方选一个子目录后重调；SkillName 为实际落盘的技能名（卸载/追溯用）。</summary>
    public record InstallResult(bool Success, string Message, List<string> Candidates, string SkillName);

    /// <summary>下载他人仓库 zip 并安装其中的技能。
    /// 单技能仓直接装；多技能仓须用 skill 参数指定子目录名（不传时返回候选清单供选择）。
    /// 技能名取 SKILL.md frontmatter 的 name（缺省回退所在目录名），同名覆盖安装。
    /// onProgress 可选：各阶段进度文本回调（"下载中…"→"解包…"→"定位技能…"→"写入 skills/…"）。</summary>
    public static async Task<InstallResult> InstallSkillAsync(AppConfig cfg, string repo, string skill,
        CancellationToken ct, Action<string>? onProgress = null)
    {
        repo = NormalizeRepo(repo);
        if (repo.Length == 0) return new InstallResult(false, "错误：repo 参数格式应为 owner/repo", EmptyCandidates, "");
        var tmpZip = Path.Combine(Path.GetTempPath(), $"gairr-gh-{Guid.NewGuid():N}.zip");
        var tmpDir = Path.Combine(Path.GetTempPath(), $"gairr-gh-{Guid.NewGuid():N}");
        try
        {
            // 1. 下载仓库 zip 并解包（先查默认分支，失败按 main 兜底）
            await DownloadAndUnpackAsync(repo, tmpZip, tmpDir, ct, onProgress);

            // 2. 定位全部 SKILL.md（限深限数）
            onProgress?.Invoke("定位技能…");
            var candidates = FindSkillFiles(tmpDir).Take(MaxCandidates).ToList();
            if (candidates.Count == 0)
                return new InstallResult(false,
                    $"错误：仓库 {repo} 中未找到 SKILL.md（它不是技能仓库；GitHub 无 GAIRR 插件格式，需要插件请按自开发规则包装）",
                    EmptyCandidates, "");

            // 3. 多技能仓：按 skill 参数匹配（全相对名如 skills/pdf 或叶子目录名 pdf 均可，后者为工具描述口径）；
            //    未指定则列候选让调用方选
            string skillDir;
            if (candidates.Count == 1) skillDir = Path.GetDirectoryName(candidates[0])!;
            else
            {
                var names = candidates.Select(c => RelSkillName(tmpDir, c)).ToList();
                if (skill.Length == 0)
                    return new InstallResult(false, $"仓库 {repo} 含 {candidates.Count} 个技能，请指定其一", names, "");
                var idx = names.FindIndex(n => n.Equals(skill, StringComparison.OrdinalIgnoreCase)
                    || n.Split('/').Last().Equals(skill, StringComparison.OrdinalIgnoreCase));
                if (idx < 0) return new InstallResult(false, $"错误：仓库中无名为 {skill} 的技能目录", EmptyCandidates, "");
                skillDir = Path.GetDirectoryName(candidates[idx])!;
            }

            // 4. 落盘：技能名取 frontmatter name（缺省用目录名），同名覆盖
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

            // 5. 追溯记录（与目录册市场共用 market.json，界面本地列表显示〔市场〕标记）
            Market.RecordMeta(cfg, new CatalogEntry("skill", name, OneLine(skill), "github", "", "", "github:" + repo));
            return new InstallResult(true,
                $"已安装技能 {name}（来自 github:{repo}）→ {dest}；技能热加载约 2 秒后生效。", EmptyCandidates, name);
        }
        catch (Exception ex) { return new InstallResult(false, "安装失败：" + ex.Message, EmptyCandidates, ""); }
        finally { TryDelete(tmpZip); TryDeleteDir(tmpDir); }
    }

    internal static readonly List<string> EmptyCandidates = new();

    /// <summary>下载仓库 zip 并解包到临时目录（安装与列候选共用）。
    /// 进度：下载中…（默认分支查询后附包大小）→ 解包…；路径穿越条目过滤同原逻辑。</summary>
    static async Task DownloadAndUnpackAsync(string repo, string tmpZip, string tmpDir,
        CancellationToken ct, Action<string>? onProgress)
    {
        onProgress?.Invoke("下载中…");
        var branch = await FetchDefaultBranchAsync(repo, ct);
        var url = $"https://codeload.github.com/{repo}/zip/refs/heads/{branch}";
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

    /* ---------- 小助手 ---------- */

    /// <summary>归一化仓库标识：接受 owner/repo 或 https://github.com/owner/repo(.git)</summary>
    static string NormalizeRepo(string repo)
    {
        var s = repo.Trim().TrimEnd('/');
        if (s.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            var i = s.IndexOf("github.com/", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return "";
            s = s[(i + 11)..];
        }
        if (s.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        return s.Split('/').Length == 2 ? s : "";
    }

    /// <summary>查默认分支（repos API）；失败回退 main</summary>
    static async Task<string> FetchDefaultBranchAsync(string repo, CancellationToken ct)
    {
        try
        {
            var o = JsonNode.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{repo}", ct)) as JsonObject;
            var b = o?["default_branch"]?.GetValue<string>();
            return string.IsNullOrEmpty(b) ? "main" : b;
        }
        catch { return "main"; }
    }

    /// <summary>递归找 SKILL.md：跳过隐藏目录，限深 4 层（覆盖 root / skills/<名> / 嵌套打包等常见布局）</summary>
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

    /// <summary>候选清单里的相对显示名：多技能仓显示子目录名（含中间路径时保留，如 skills/pdf）</summary>
    static string RelSkillName(string root, string skillMd)
    {
        var rel = Path.GetRelativePath(root, Path.GetDirectoryName(skillMd)!).Replace('\\', '/');
        // 剥掉 zip 顶层目录（repo-branch 形式，路径第一段含仓库名）
        var parts = rel.Split('/');
        return parts.Length > 1 && parts[0].Contains('-') ? string.Join('/', parts[1..]) : rel;
    }

    /// <summary>解析 SKILL.md frontmatter 的 name 字段（与 SkillLoader 同口径，缺省返回空）</summary>
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
