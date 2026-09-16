using System.IO;
using System.Text;

namespace GAIRR.Core;

/// <summary>Git 全量管理：自动探测/安装 git（winget 静默）→ ProjectRoot 自动 init → 任务结束自动提交。
/// 身份用命令行 -c 参数提供，从不写全局/仓库 git config；只本地管理，不 push。</summary>
public static class GitMgr
{
    static string? exe;

    /// <summary>找 git.exe：先遍历 PATH，再探常见安装目录</summary>
    static string? Find()
    {
        foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try { var p = Path.Combine(d, "git.exe"); if (File.Exists(p)) return p; } catch { }
        }
        foreach (var p in new[] { @"C:\Program Files\Git\cmd\git.exe", @"C:\Program Files (x86)\Git\cmd\git.exe" })
            if (File.Exists(p)) return p;
        return null;
    }

    /// <summary>确保 git 可用：已有直接用；缺失则 winget 静默装 Git.Git 后重探</summary>
    public static async Task<string> EnsureInstalledAsync(AppConfig cfg)
    {
        exe ??= Find();
        if (exe != null) return "git 就绪";
        var winget = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "",
            @"Microsoft\WindowsApps\winget.exe");
        if (File.Exists(winget))
        {
            await RunExe(winget,
                "install --id Git.Git -e --silent --accept-package-agreements --accept-source-agreements", 600);
            exe = Find();
        }
        return exe != null ? "git 已自动安装" : "git 缺失且自动安装失败，请手动安装后重启";
    }

    /// <summary>确保 ProjectRoot 为仓库：无 .git 则 init + 写 .gitignore + 初始提交</summary>
    public static async Task<string> EnsureRepoAsync(AppConfig cfg)
    {
        var root = cfg.ProjectRoot;
        if (root.Length == 0 || !Directory.Exists(root)) return "ProjectRoot 未设置，跳过 git 初始化";
        if (exe == null) return "git 不可用，跳过初始化";
        if (Directory.Exists(Path.Combine(root, ".git"))) return "仓库已存在";
        await Run(cfg, "init \"" + root + "\"");
        var gi = Path.Combine(root, ".gitignore");
        if (!File.Exists(gi))
        {
            var sb = new StringBuilder("bin/\nobj/\npublish/\nback/\n.vs/\n*.user\n");
            foreach (var e in cfg.Get("Git", "IgnoreExtra", "")
                         .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                sb.Append(e).Append('\n');
            File.WriteAllText(gi, sb.ToString());
        }
        await CommitAsync(cfg, "init: GAIRR git 全量管理接管");
        return "仓库已初始化";
    }

    /// <summary>任务结束自动提交（无变更则跳过）。提交信息含归属标识 auto@&lt;taskKey&gt;（=会话/运行短 id，
    /// 供 git log 反查"哪些提交属于某任务"）+ 轮数/tokens/任务摘要（摘要截 150 保留功能描述）。
    /// 返回结果：Created=false=无变更跳过/配置关闭/git 不可用（调用方不当错误处理）。</summary>
    public static async Task<GitCommitOutcome> AutoCommitAsync(AppConfig cfg, string task, int rounds, int tokens,
        string? taskKey = null)
    {
        var outcome = new GitCommitOutcome();
        try
        {
            if (cfg.Get("Git", "Enabled", "1") != "1" || cfg.Get("Git", "AutoCommit", "1") != "1") return outcome;
            if (exe == null) await EnsureInstalledAsync(cfg);
            if (exe == null) return outcome;
            if (!Directory.Exists(Path.Combine(cfg.ProjectRoot, ".git"))) await EnsureRepoAsync(cfg);
            var pre = await HeadAsync(cfg);
            if (pre.Length == 0) return outcome;   // rev-parse 异常：不提交（保留工作区状态供人工处理）
            var msg = BuildMessage(taskKey, rounds, tokens, task);
            outcome.Message = msg;
            await CommitAsync(cfg, msg);
            var post = await HeadAsync(cfg);
            if (post.Length > 0 && post != pre)
            {
                outcome.Created = true;
                outcome.Hash = post;
            }
        }
        catch { /* git 失败不打断主流程 */ }
        return outcome;
    }

    /// <summary>构建自动提交信息：auto@&lt;key&gt; N 轮 · M tokens :: 任务摘要（摘要去换行/引号，超 150 字截断加 …；key 为空省略 @ 段）。</summary>
    static string BuildMessage(string? taskKey, int rounds, int tokens, string task)
    {
        var head = string.IsNullOrEmpty(taskKey) ? "auto " : "auto@" + taskKey + " ";
        var sum = (task ?? "").Replace('\n', ' ').Replace('\r', ' ').Replace('"', '\'');
        if (sum.Length > 150) sum = sum[..150] + "…";
        return head + rounds + " 轮 · " + tokens + " tokens :: " + sum;
    }

    /// <summary>当前 HEAD 完整哈希（40 位十六进制）；命令失败/仓库异常返回空串（IsHash 校验）。</summary>
    static async Task<string> HeadAsync(AppConfig cfg)
    {
        var raw = await Run(cfg, "-C \"" + cfg.ProjectRoot + "\" rev-parse HEAD");
        return raw.Length == 40 && IsHash(raw) ? raw : "";
    }

    /// <summary>是否为 git 完整/短哈希串（十六进制 7-64 位；短哈希仅展示用，不做全文判等）。</summary>
    static bool IsHash(string s)
    {
        if (s.Length < 7 || s.Length > 64) return false;
        foreach (var c in s)
            if (!(c is >= '0' and <= '9' || c is >= 'a' and <= 'f' || c is >= 'A' and <= 'F')) return false;
        return true;
    }

    /// <summary>读侧前置检查：Git 启用 + git.exe 就绪 + ProjectRoot 为仓库；不满足返回 false（调用方返回空结果，不触发安装）。</summary>
    static bool Ready(AppConfig cfg)
    {
        if (cfg.Get("Git", "Enabled", "1") != "1") return false;
        if (exe == null) exe = Find();
        if (exe == null) return false;
        var root = cfg.ProjectRoot;
        return root.Length > 0 && Directory.Exists(Path.Combine(root, ".git"));
    }

    /// <summary>最近提交记录（log 单行 subject，auto@key / auto: / auto 前缀解析为归属标识并剥离头部）；空表=仓库不可用/无提交。</summary>
    public static async Task<List<GitCommitInfo>> LogRecentAsync(AppConfig cfg, int limit = 50)
    {
        var list = new List<GitCommitInfo>();
        try
        {
            if (!Ready(cfg)) return list;
            var root = cfg.ProjectRoot;
            // %x1f 作字段分隔（提交摘要可能含空格/逗号）；quotepath=false 防中文路径被引号包裹
            var output = await Run(cfg,
                "-C \"" + root + "\" -c core.quotepath=false log -n " + Math.Clamp(limit, 1, 200) +
                " --format=%H%x1f%h%x1f%aI%x1f%s");
            if (output.Length == 0 || output.StartsWith("执行失败") || output == "超时") return list;
            foreach (var line in output.Split('\n'))
            {
                var parts = line.TrimEnd('\r').Split('\x1f');
                if (parts.Length < 4 || !IsHash(parts[0])) continue;
                var msg = parts[3];
                string? key = null;
                var auto = msg.StartsWith("auto@", StringComparison.Ordinal)
                    || msg.StartsWith("auto:", StringComparison.Ordinal)
                    || msg.StartsWith("auto ", StringComparison.Ordinal);
                if (auto)
                {
                    // 剥离自动提交头部（auto@key / auto: / auto），正文从“N 轮…”开始；key 仅 auto@ 段带
                    var sp = msg.IndexOf(' ');
                    if (msg.StartsWith("auto@", StringComparison.Ordinal) && sp >= 8) key = msg[5..sp];
                    msg = msg[(msg.IndexOf(' ') + 1)..];
                    if (msg.Length == 0) msg = "(空提交信息)";
                }
                list.Add(new GitCommitInfo
                {
                    Hash = parts[0],
                    Short = parts[1],
                    Message = msg,
                    When = DateTimeOffset.TryParse(parts[2], null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var when) ? when : DateTimeOffset.Now,
                    IsAuto = auto,
                    Key = key,
                });
            }
        }
        catch { }
        return list;
    }

    /// <summary>最近提交的变更路径表（hash → 该提交变更的全部路径）：git log --name-status 一次拉取，
    /// 供“项目跟踪”分组对 commit 分类（纯内部/产物变更的提交收纳灰组，其余按归属任务分组展示；
    /// commit 行展开的文件明细仍由 ShowStatAsync 懒加载）。空字典=仓库不可用/无提交。</summary>
    public static async Task<Dictionary<string, List<string>>> LogFilesAsync(AppConfig cfg, int limit = 50)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Ready(cfg)) return map;
            var root = cfg.ProjectRoot;
            // 每提交一行 %H 哈希，随后是 name-status 行（状态\t路径，重命名 R100 old\tnew 取新路径）；quotepath=false 防中文路径转义
            var output = await Run(cfg,
                "-C \"" + root + "\" -c core.quotepath=false log -n " + Math.Clamp(limit, 1, 200) +
                " --format=%H --name-status");
            if (output.Length == 0 || output.StartsWith("执行失败") || output == "超时") return map;
            string? cur = null;
            foreach (var line in output.Split('\n'))
            {
                var t = line.TrimEnd('\r');
                if (t.Length == 0) continue;
                if (IsHash(t))   // 哈希行=新提交起点
                {
                    cur = t;
                    map[cur] = new List<string>();
                    continue;
                }
                if (cur == null) continue;
                var tab = t.IndexOf('\t');
                if (tab <= 0) continue;   // 非 name-status 行（理论不出现，防御跳过）
                var p = t[(tab + 1)..];
                var tab2 = p.IndexOf('\t');
                if (tab2 >= 0) p = p[(tab2 + 1)..];
                map[cur].Add(p);
            }
        }
        catch { }
        return map;
    }

    /// <summary>仓库内部/产物路径的顶层集合：已解除 git 跟踪并忽略的目录与根级文件（.gairr 工具数据、
    /// temp 临时、build_tmp/node_modules 等产物），只可能出现在历史提交的变更清单里，用于把
    /// “仅内部更新”的自动提交收纳进灰组，让源码变更一眼可见。</summary>
    static readonly HashSet<string> InternalRoots = new(StringComparer.Ordinal)
    {
        ".gairr", "temp", "tmp", "build_tmp", "publish-back", "node_modules", "test-taobao",
        "prompts.zip", "site.zip", "GAIRR.exe.lnk", "_wer_top.txt",
        ".fix_flowdisplay.py", ".fix_flowdisplay.js", ".fix_flowdisplay2.js",
        "FlowTemplate_work.cs", "build_out.txt", "eng.traineddata",
    };

    /// <summary>单个变更路径是否属于内部/产物（顶层目录或根级文件在解除跟踪清单内）。</summary>
    public static bool IsInternalGitPath(string p)
    {
        if (string.IsNullOrEmpty(p)) return true;
        var s = p.Replace('\\', '/');
        var i = s.IndexOf('/');
        var top = i < 0 ? s : s[..i];
        return InternalRoots.Contains(top) || InternalRoots.Contains(s);
    }

    /// <summary>单次提交的变更文件清单：name-status 状态 + numstat 行数合并（按 diff 文件序 zip；
    /// 重命名等两表字段数不一致时以状态行为主，行数按路径尽力匹配）。空表=提交不存在/仓库不可用。</summary>
    public static async Task<List<GitFileStat>> ShowStatAsync(AppConfig cfg, string hash)
    {
        var files = new List<GitFileStat>();
        try
        {
            if (!Ready(cfg)) return files;
            var root = cfg.ProjectRoot;
            var ns = await Run(cfg, "-C \"" + root + "\" -c core.quotepath=false show --format= --name-status " + hash);
            var num = await Run(cfg, "-C \"" + root + "\" -c core.quotepath=false show --format= --numstat " + hash);
            if (ns.Length == 0 || ns.StartsWith("执行失败") || ns == "超时") return files;
            // numstat 行：ins\tdel\tpath（二进制文件 ins/del 为 -）；重命名路径形如 {old => new} 整体作键
            var counts = new Dictionary<string, (int Ins, int Del)>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in num.Split('\n'))
            {
                var t = line.TrimEnd('\r');
                if (t.Length == 0) continue;
                var t1 = t.IndexOf('\t');
                if (t1 <= 0) continue;
                var t2 = t.IndexOf('\t', t1 + 1);
                if (t2 < 0) continue;
                var ins = t[..t1] == "-" ? 0 : (int.TryParse(t[..t1], out var i) ? i : 0);
                var del = t[(t1 + 1)..t2] == "-" ? 0 : (int.TryParse(t[(t1 + 1)..t2], out var d) ? d : 0);
                counts[t[(t2 + 1)..].Trim()] = (ins, del);
            }
            foreach (var line in ns.Split('\n'))
            {
                var t = line.TrimEnd('\r');
                if (t.Length == 0) continue;
                var tab = t.IndexOf('\t');
                if (tab <= 0) continue;
                var st = t[..tab];
                var p = t[(tab + 1)..];
                var tab2 = p.IndexOf('\t');
                if (tab2 >= 0) p = p[(tab2 + 1)..];   // 重命名 R100 old\tnew：取新路径
                var f = new GitFileStat { Status = st, Path = p };
                if (counts.TryGetValue(p, out var c)) { f.Ins = c.Ins; f.Del = c.Del; }
                files.Add(f);
            }
        }
        catch { }
        return files;
    }

    /// <summary>单次提交内单个文件的 diff 原文（含 diff/@@ 头，供右栏只读展示）；超 600 行截断防 UI 卡顿，仓库不可用返回空串。</summary>
    public static async Task<string> FileDiffAsync(AppConfig cfg, string hash, string path)
    {
        try
        {
            if (!Ready(cfg)) return "";
            var output = await Run(cfg, "-C \"" + cfg.ProjectRoot + "\" -c core.quotepath=false show --format= " + hash + " -- \"" + path + "\"");
            if (output.Length == 0 || output.StartsWith("执行失败") || output == "超时") return "";
            var lines = output.Split('\n');
            if (lines.Length > 600)
                return string.Join("\n", lines, 0, 600) + $"\n…（diff 超过 600 行已截断，共 {lines.Length} 行）";
            return output;
        }
        catch { return ""; }
    }

    /// <summary>任务开始时的现状快照：工作区未提交改动清单（供模型尽早发现"功能可能已实现"，避免重复探索）。
    /// 正常时返回文件清单或"工作区干净"；不可用时返回 "跳过: 原因"（供调用方记日志定位，不阻断主流程）</summary>
    public static async Task<string> StatusSnapshotAsync(AppConfig cfg)
    {
        try
        {
            if (cfg.Get("Git", "Enabled", "1") != "1") return "跳过: Git 已禁用";
            exe ??= Find();
            if (exe == null) return "跳过: 未找到 git.exe";
            var root = cfg.ProjectRoot;
            if (root.Length == 0 || !Directory.Exists(Path.Combine(root, ".git"))) return "跳过: ProjectRoot 非 git 仓库";
            var output = await Run(cfg, "-C \"" + root + "\" status --porcelain");
            if (output.StartsWith("执行失败") || output == "超时")
                return "跳过: git 执行异常（" + LLMClient.Trunc(output, 60) + "）";
            // porcelain 行格式：XY+空格+路径；XY 状态码不含空格，以第一个空格作分隔符取路径
            // （不能按固定位置 [3..] 切片：RunExe 对整个输出 Trim 会吃掉首行前导空格，
            //  修改行 " M xxx" 被裁成 "M xxx" 后固定切片会丢路径首字符）；
            // 重命名行为 "R  旧 -> 新"，取新路径；带中文的路径可能被 git 引号包裹，顺手去引号
            var files = output.Split('\n')
                .Select(l => l.TrimEnd('\r'))
                .Where(l => l.Length > 2)
                .Select(l =>
                {
                    var sp = l.IndexOf(' ');
                    var p = (sp >= 0 ? l[(sp + 1)..] : "").Trim().Trim('"');
                    var idx = p.LastIndexOf(" -> ", StringComparison.Ordinal);
                    return idx >= 0 ? p[(idx + 4)..].Trim('"') : p;
                })
                .Where(p => p.Length > 0)
                .Take(15)
                .ToList();
            return files.Count == 0 ? "工作区干净（无未提交改动）" : string.Join("、", files);
        }
        catch (Exception ex) { return "跳过: " + ex.Message; }
    }

    static async Task<string> CommitAsync(AppConfig cfg, string msg)
    {
        var root = cfg.ProjectRoot;
        await Run(cfg, "-C \"" + root + "\" add -A");
        // 提交消息走 stdin（-F -）直写 UTF-8 字节，避开 argv 系统码页转换丢中文
        return await Run(cfg, "-C \"" + root + "\" -c user.name=GAIRR -c user.email=agent@gairr.local commit -F -",
            msg);
    }

    /// <summary>直启进程（不经 cmd /c，避免引号剥落破坏带空格路径）；输出+错误合并返回；stdinText 以 UTF-8 写入</summary>
    static async Task<string> RunExe(string file, string args, int timeoutSec, string? stdinText = null)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutSec * 1000);
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = stdinText != null,
                StandardInputEncoding = stdinText != null ? new UTF8Encoding(false) : null,
                // git 输出/错误均为 UTF-8 字节（commit -F - 直写 UTF-8；quotepath=false 路径同理）；
                // .NET 默认按系统 ANSI 码页解码会致中文乱码，必须显式指定 UTF-8
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            if (stdinText != null)
            {
                await p.StandardInput.WriteAsync(stdinText);
                p.StandardInput.Close();
            }
            var so = p.StandardOutput.ReadToEndAsync();
            var se = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync(cts.Token);
            return ((await so) + (await se)).Trim();
        }
        catch (OperationCanceledException) { return "超时"; }
        catch (Exception ex) { return "执行失败: " + ex.Message; }
    }

    static Task<string> Run(AppConfig cfg, string args, string? stdin = null) => RunExe(exe!, args, 60, stdin);
}

/// <summary>自动提交结果：Created=false=无变更跳过/配置关闭/git 不可用（非错误，调用方照常收尾）。</summary>
public sealed class GitCommitOutcome
{
    public bool Created { get; set; }
    public string Hash { get; set; } = "";
    public string Message { get; set; } = "";
}

/// <summary>提交记录行（git log 输出，供左侧"项目跟踪"分组时间线展示）。</summary>
public sealed class GitCommitInfo
{
    public string Hash { get; set; } = "";
    public string Short { get; set; } = "";
    /// <summary>提交摘要（auto@key 前缀已剥离，保留"N 轮 · M tokens :: 任务内容"部分）</summary>
    public string Message { get; set; } = "";
    public DateTimeOffset When { get; set; }
    /// <summary>是否 GAIRR 自动提交（auto@key 或旧格式 auto 前缀）；手动提交 false</summary>
    public bool IsAuto { get; set; }
    /// <summary>归属任务标识（auto@&lt;key&gt; 解析所得：会话/运行短 id）；手动提交或旧格式为 null</summary>
    public string? Key { get; set; }
}

/// <summary>单文件变更统计（show --name-status/--numstat 合并，供 commit 节点展开的文件清单展示）。</summary>
public sealed class GitFileStat
{
    /// <summary>git 状态码：A=新增 M=修改 D=删除 R100=重命名…（保留原始码，展示端映射文字）</summary>
    public string Status { get; set; } = "M";
    public string Path { get; set; } = "";
    public int Ins { get; set; }
    public int Del { get; set; }
}
