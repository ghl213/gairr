using System.IO;
using System.Text.Json.Nodes;
using System.Threading;

namespace GAIRR.Core;

/// <summary>修复-验证循环：Write/Edit 写的是代码文件时，自动跑一次构建/测试，
/// 结果附到工具返回文本回喂 Agent（失败信息直接进上下文，模型下一轮自动修复）。
/// 开关与命令在 system.ini [Verify] 节；构建探测结果按项目根缓存。</summary>
public static class Verify
{
    static readonly SemaphoreSlim gate = new(1, 1);   // 构建互斥（同项目同刻只跑一个）
    static readonly string[] CodeExts =
    {
        ".cs", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".py", ".go", ".java", ".kt",
        ".dart", ".rb", ".php", ".vue", ".cpp", ".h", ".c", ".xaml", ".csproj",
    };
    static string projRoot = "";                       // 命令探测缓存键
    static string? buildCmd;                           // null=未探测；""=没有可构建工程
    static int consecFail;                             // 连续验证失败计数（任务级；成功即清零）

    /// <summary>任务开始时由 AgentLoop 调用：连续失败计数清零</summary>
    public static void ResetRepairCount() => consecFail = 0;

    /// <summary>从带"[自动验证]"段的工具结果中提取验证摘要（首行），供 changelog 证据链落盘</summary>
    public static string Brief(string result)
    {
        var i = result.IndexOf("[自动验证]", StringComparison.Ordinal);
        if (i < 0) return "";
        var line = result[(i + "[自动验证]".Length)..];
        var nl = line.IndexOf('\n');
        return (nl > 0 ? line[..nl] : line).Trim();
    }

    /// <summary>在工具结果后追加自动验证段（只对代码文件生效；无构建工程/被开关关闭时原样返回）</summary>
    public static async Task<string> AppendResult(AppConfig cfg, string result, string argumentsJson, CancellationToken ct)
    {
        try
        {
            if (!SystemCfg.AutoVerify) return result;
            string path;
            try
            {
                var args = JsonNode.Parse(argumentsJson) as JsonObject;
                path = args?["path"]?.GetValue<string>() ?? "";
            }
            catch { return result; }
            if (path.Length == 0) return result;
            var ext = Path.GetExtension(path.Replace('/', Path.DirectorySeparatorChar)).ToLowerInvariant();
            if (ext.Length == 0 || !CodeExts.Contains(ext)) return result;

            // 修复轮数上限：连续失败达到上限后停止验证并强提示停手（0=不限制）
            var limit = SystemCfg.MaxRepairRounds;
            if (limit > 0 && consecFail >= limit)
                return result + $"\n\n[自动验证] 已暂停：构建已连续失败 {consecFail} 次（上限 {limit}）。不要再盲目重试同一修改，请先完整阅读报错位置分析根因，或直接向用户说明问题寻求方案。";

            var cmd = ResolveCommand(cfg, path, out var isTest);
            if (string.IsNullOrEmpty(cmd)) return result;
            if (!await gate.WaitAsync(0, ct)) return result + "\n\n[自动验证] 已有构建任务在运行，本次跳过";

            try
            {
                var dir = Path.GetDirectoryName(cmd.Split('|')[0]) ?? cfg.ProjectRoot;
                var full = "cd /d \"" + dir + "\" && " + cmd[(cmd.IndexOf('|') + 1)..];
                var outText = await Phase1Tools.RunCmd(cfg, full, SystemCfg.VerifyTimeoutSec, ct);
                var ok = outText.Contains("退出码 0") || outText.Contains("已成功生成") ||
                         outText.Contains("Build succeeded", StringComparison.OrdinalIgnoreCase);
                var brief = LLMClient.Trunc(outText, SystemCfg.VerifyMaxChars);
                if (ok)
                {
                    consecFail = 0;   // 成功即清零：修复轮数是"连续失败"计数
                    var sym = isTest ? "dotnet test" : (SystemCfg.BuildCmd.Length == 0 ? "dotnet build" : SystemCfg.BuildCmd);
                    return result + $"\n\n[自动验证] {sym} 成功。" + (brief.Length > 0 ? "\n" + brief : "");
                }
                // 失败：截出错误行（去重、限条数）回喂 Agent，沉淀项目经验，并推进修复轮数计数
                consecFail++;
                var errLines = ErrorLines(outText);
                var failSummary = errLines.Count > 0 ? string.Join("\n", errLines.Take(8)) : brief;
                ProjectMapAuto.RecordLesson(cfg, (isTest ? "测试失败: " : "构建失败: ") + (errLines.Count > 0 ? errLines[0] : brief.Trim().Replace('\n', ' ')));
                var extra = "";
                if (limit > 0 && consecFail >= limit)
                    extra = $"\n⚠️ 已连续失败 {consecFail} 次，达到修复上限（{limit}）：自动验证已暂停，下一轮起不再自动跑构建。请停止针对同一报错盲目修改，先阅读报错涉及的完整上下文再决策，或直接向用户报告。";
                return result + $"\n\n[自动验证] {(isTest ? "测试失败" : "构建失败")}（第 {consecFail} 次），请修复后重试：\n{failSummary}{extra}";
            }
            finally { gate.Release(); }
        }
        catch (OperationCanceledException) { return result + "\n\n[自动验证] 验证被取消"; }
        catch (Exception ex) { return result + "\n\n[自动验证] 验证异常：" + ex.GetBaseException().Message; }
    }

    /// <summary>解析验证命令：改动文件位于测试工程目录（*.Tests/）且 AutoTest 开启时跑 dotnet test（增量验证）；否则走常规构建</summary>
    static string ResolveCommand(AppConfig cfg, string path, out bool isTest)
    {
        isTest = false;
        if (SystemCfg.AutoTest)
        {
            var proj = FindTestProject(cfg, path);
            if (proj.Length > 0)
            {
                isTest = true;
                return proj + "|" + DotnetExe() + " test " + Q(proj) + " -nologo -v q";
            }
        }
        return ProbeCommand(cfg.ProjectRoot);
    }

    /// <summary>查找改动文件所属的测试 csproj：相对路径任一段含 ".Tests"，且该段目录内有 *.csproj；""=不属于测试工程</summary>
    static string FindTestProject(AppConfig cfg, string path)
    {
        try
        {
            var full = Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar));
            var rel = Path.GetRelativePath(cfg.ProjectRoot, full).Replace('\\', '/');
            if (rel.StartsWith("..", StringComparison.Ordinal)) return "";
            var segs = rel.Split('/');
            var acc = "";
            foreach (var s in segs[..^1])   // 跳过文件名本身，只看目录段
            {
                acc += (acc.Length > 0 ? "/" : "") + s;
                if (!s.Contains(".Tests", StringComparison.OrdinalIgnoreCase)) continue;
                var dir = Path.Combine(cfg.ProjectRoot, acc.Replace('/', Path.DirectorySeparatorChar));
                var csproj = Directory.GetFiles(dir, "*.csproj").FirstOrDefault();
                if (csproj != null) return csproj;
            }
        }
        catch { /* 路径异常回退常规构建 */ }
        return "";
    }

    /// <summary>探测构建命令：优先 system.ini BuildCmd；否则在项目根找 *.sln/*.csproj（按根缓存）</summary>
    static string ProbeCommand(string root)
    {
        if (SystemCfg.BuildCmd.Length > 0) return "|" + SystemCfg.BuildCmd;
        if (string.Equals(projRoot, root, StringComparison.OrdinalIgnoreCase)) return buildCmd ?? "";
        projRoot = root;
        buildCmd = null;
        try
        {
            foreach (var f in Phase1Tools.EnumerateFiles(root))
            {
                var p = f.ToLowerInvariant();
                if (p.EndsWith(".sln") || p.EndsWith(".csproj"))
                {
                    buildCmd = f + "|" + DotnetExe() + " build " + Q(f) + " -nologo -v q";
                    return buildCmd;
                }
            }
        }
        catch { }
        return "";
    }

    /// <summary>dotnet 可执行路径：本机通常不在 PATH（build.bat 靠 DOTNET_ROOT 补偿），先探测固定安装路径</summary>
    static string DotnetExe()
    {
        var home = Environment.GetEnvironmentVariable("USERPROFILE") ?? "";
        var p = Path.Combine(home, "AppData", "Local", "Microsoft", "dotnet", "dotnet.exe");
        return File.Exists(p) ? "\"" + p + "\"" : "dotnet";
    }

    static string Q(string s) => "\"" + s + "\"";

    /// <summary>从构建输出提取错误行（含"错误"/error 的行，去头尾噪音）</summary>
    static List<string> ErrorLines(string outText)
    {
        var errs = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in outText.Replace("\r\n", "\n").Split('\n'))
        {
            var t = raw.Trim();
            var lower = t.ToLowerInvariant();
            if ((lower.Contains("错误") || lower.Contains("error")) &&
                !lower.Contains("错误日志") && !lower.Contains("error log") && !lower.Contains("验证异常"))
            {
                var norm = t.Length > 220 ? t[..220] : t;
                if (seen.Add(norm)) errs.Add(norm);
            }
            if (errs.Count >= 12) break;
        }
        return errs;
    }
}