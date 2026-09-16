using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace GAIRR.Core;

/// <summary>插件/技能运行时节：{python}/{node}/{java} 占位符统一解析并托管自带运行时到 GAIRR 目录 envs\ 下
/// （python=官方 embeddable 3.12、node=官方 LTS v20、java=Temurin JRE21）。
/// 命名 envs\ 而非 runtimes\：.NET publish 会自动生成原生 runtimes\ 目录（build.bat 有维护逻辑），同名会冲突。
/// 历史部署在 lsp\ 下的 node/java（LSP 专用）直接复用，避免二次下载。
/// 解析优先级：config.ini [Script] 显式路径 → 自带 envs\ → 系统 PATH；
/// 全无且 [Runtime] AutoDownload=1（默认开）时自动下载便携版；下载失败/被关时静默回退裸命令（由进程自身报错提示）。
/// 同时向 RunCmd 子进程 PATH 末尾追加自带解释器目录：SKILL.md / Bash 里直接敲 python/node/java 也能命中自带运行时。</summary>
public static class RuntimeEnv
{
    /// <summary>自带运行时根目录（GAIRR 目录 envs\，解释器环境）</summary>
    public static string Root => Path.Combine(AppContext.BaseDirectory, "envs");

    /// <summary>历史 LSP 自包含目录（lsp\，见 Core/Lsp/LspEnv.cs；已有 node/java 时复用，避免重复下载）</summary>
    static string LspRoot => Path.Combine(AppContext.BaseDirectory, "lsp");

    static string Expand(string p) => Environment.ExpandEnvironmentVariables(p);

    static string Q(string p) => "\"" + p + "\"";

    // ---------- 占位符解析（{python}/{node}/{java}） ----------

    /// <summary>{python}：显式配置 → 自带 envs\python\ → 系统 PATH → 自动下载官方 embeddable</summary>
    public static string Py(AppConfig cfg) => Placeholder(cfg, "PythonPath", "python", FindPythonExe, EnsurePython);

    /// <summary>{node}：显式配置 → 自带 envs\node\（或 lsp\node\）→ 系统 PATH → 自动下载官方 LTS 便携版</summary>
    public static string Node(AppConfig cfg) => Placeholder(cfg, "NodePath", "node", FindNodeExe, EnsureNode);

    /// <summary>{java}：显式配置（JavaHome=JDK/JRE 目录）→ 自带 envs\java\（或 lsp\java\）→ 系统 PATH → 自动下载 Temurin JRE21</summary>
    public static string Java(AppConfig cfg)
    {
        var v = Expand(cfg.Get("Script", "JavaHome", "")).Trim();
        if (v.Length == 0 || Bare(v))   // 空或裸命令 java → 自动探测
        {
            var own = FindJavaExe();
            if (own != null) return Q(own);
            if (HasSystemExe("java.exe")) return "java";
            if (SystemCfg.RuntimeAutoDownload)
            {
                own = EnsureJava();
                if (own != null) return Q(own);
            }
            return v.Length == 0 ? "java" : v;
        }
        // 显式目录（补 bin\java.exe）或完整 java.exe 路径；不校验存在性，失败由 cmd 报错提示
        var exe = v.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? v : Path.Combine(v, "bin", "java.exe");
        return Q(exe);
    }

    /// <summary>通用占位符解析。value 为空或裸命令（python/node）时走自动探测链；
    /// 带路径/参数（如 venv\Scripts\python.exe、py -3）时视为显式配置，原样返回尊重用户。</summary>
    static string Placeholder(AppConfig cfg, string key, string bare, Func<string?> find, Func<string?> ensure)
    {
        var v = Expand(cfg.Get("Script", key, "")).Trim();
        if (v.Length == 0 || Bare(v))
        {
            var own = find();
            if (own != null) return Q(own);
            if (HasBareSystem(bare)) return bare;           // 系统 PATH 已有 → 裸命令命中
            if (SystemCfg.RuntimeAutoDownload)
            {
                own = ensure();
                if (own != null) return Q(own);
            }
            return v.Length == 0 ? bare : v;                // 下载关/失败 → 回退，cmd 自行报错
        }
        return v;
    }

    /// <summary>裸命令判定：无路径分隔符/空格/引号、非 .exe（如 python、node、py、python3）</summary>
    static bool Bare(string v) =>
        !v.Contains('\\') && !v.Contains('/') && !v.Contains(' ') && !v.Contains('"')
        && !v.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    static bool HasBareSystem(string bare) =>
        bare == "python"
            ? (HasSystemExe("python.exe") || HasSystemExe("python3.exe") || HasSystemExe("py.exe"))
            : HasSystemExe(bare + ".exe");

    static bool HasSystemExe(string name)
    {
        foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            var dir = d.Trim();
            if (dir.Length == 0) continue;
            try { if (File.Exists(Path.Combine(dir, name))) return true; } catch { }
        }
        return false;
    }

    // ---------- 自带运行时探测（只读，不触发下载） ----------

    static string? FindPythonExe()
    {
        var f = Path.Combine(Root, "python", "python.exe");
        return File.Exists(f) ? f : null;
    }

    static string? FindNodeExe() => FindUnder("node", "node.exe");

    static string? FindJavaExe() => FindUnder("java", Path.Combine("bin", "java.exe"));

    /// <summary>在 envs\ 与 lsp\ 的 rel 子目录下找可执行文件：子目录内优先，其次该层直放。</summary>
    static string? FindUnder(string rel, string exeRel)
    {
        foreach (var b in new[] { Root, LspRoot })
        {
            var dir = Path.Combine(b, rel);
            if (!Directory.Exists(dir)) continue;
            try
            {
                var direct = Path.Combine(dir, exeRel);
                if (File.Exists(direct)) return direct;
                foreach (var sub in Directory.GetDirectories(dir))
                {
                    var f = Path.Combine(sub, exeRel);
                    if (File.Exists(f)) return f;
                }
            }
            catch { }
        }
        return null;
    }

    // ---------- 向子进程注入自带目录（Tools.cs RunCmd 调用）：系统 PATH 之后追加，裸命令系统优先、自带兜底 ----------

    static string? _bins;

    /// <summary>已就绪的自带解释器所在目录（分号连接；只探测不下载）。由 Tools.RunCmd 拼到子进程 PATH 末尾。</summary>
    public static string OwnBinsPath()
    {
        if (_bins != null) return _bins;
        var sb = new StringBuilder();
        void Add(string? exe)
        {
            if (exe == null) return;
            var d = Path.GetDirectoryName(exe);
            if (d == null) return;
            if (sb.Length > 0) sb.Append(';');
            sb.Append(d);
        }
        Add(FindPythonExe());
        Add(FindNodeExe());
        Add(FindJavaExe());
        return _bins = sb.ToString();
    }

    /// <summary>下载成功后清缓存（OwnBinsPath 结果变化）</summary>
    public static void ResetCache() => _bins = null;

    // ---------- 自动下载（[Runtime] AutoDownload 已检查；只在本机全无时触发） ----------

    static readonly SemaphoreSlim gate = new(1, 1);

    static string? EnsurePython() => Ensure("python", () =>
    {
        var dir = Path.Combine(Root, "python");
        Directory.CreateDirectory(dir);
        var zip = Path.Combine(dir, "python-download.zip");
        try
        {
            Download(PythonUrl(), zip);
            ExtractZip(zip, dir);
            MoveUpIfNested(dir, "python.exe");
            TryDelete(zip);
            var exe = FindPythonExe();
            if (exe != null) ResetCache();
            return exe;
        }
        catch
        {
            TryDelete(zip);
            return null;
        }
    });

    static string? EnsureNode() => Ensure("node", () =>
    {
        var dir = Path.Combine(Root, "node");
        Directory.CreateDirectory(dir);
        var zip = Path.Combine(dir, "node-download.zip");
        try
        {
            Download(NodeUrl(), zip);
            ExtractZip(zip, dir);
            TryDelete(zip);
            var exe = FindNodeExe();
            if (exe != null) ResetCache();
            return exe;
        }
        catch
        {
            TryDelete(zip);
            return null;
        }
    });

    static string? EnsureJava() => Ensure("java", () =>
    {
        var dir = Path.Combine(Root, "java");
        Directory.CreateDirectory(dir);
        var zip = Path.Combine(dir, "jre21-download.zip");
        try
        {
            Download(Jre21Url(), zip);
            ExtractZip(zip, dir);
            TryDelete(zip);
            var exe = FindJavaExe();
            if (exe != null) ResetCache();
            return exe;
        }
        catch
        {
            TryDelete(zip);
            return null;
        }
    });

    static string? Ensure(string lang, Func<string?> download)
    {
        gate.Wait();
        try
        {
            // 门内复查：可能已被并行线程下载完成
            var exe = lang switch { "python" => FindPythonExe(), "node" => FindNodeExe(), _ => FindJavaExe() };
            if (exe != null) return exe;
            if (!SystemCfg.RuntimeAutoDownload) return null;
            return download();
        }
        finally { gate.Release(); }
    }

    // ---------- 下载源 ----------

    /// <summary>python 官方 embeddable（无 pip，最小可用；依赖模块多的插件建议 config 显式配全量 PythonPath）
    /// 官方连通失败自动回退 npmmirror 二进制镜像。</summary>
    static string PythonUrl()
    {
        const string ver = "3.12.10";
        const string file = "python-3.12.10-embed-amd64.zip";
        try
        {
            using var hc = Http();
            using var r = hc.GetAsync($"https://www.python.org/ftp/python/{ver}/{file}", HttpCompletionOption.ResponseHeadersRead)
                .GetAwaiter().GetResult();
            if (r.IsSuccessStatusCode) return $"https://www.python.org/ftp/python/{ver}/{file}";
        }
        catch { }
        return $"https://registry.npmmirror.com/-/binary/python/{ver}/{file}";
    }

    /// <summary>node 官方 latest-v20.x 目录取 win-x64 zip 直链（与 LspEnv 的 Node 下载同源，先到者存 envs\node\）</summary>
    static string NodeUrl()
    {
        using var hc = Http();
        var page = hc.GetStringAsync("https://nodejs.org/dist/latest-v20.x/").GetAwaiter().GetResult();
        var m = Regex.Match(page, @"href=""(node-v20\.[0-9]+\.[0-9]+-win-x64\.zip)""");
        if (!m.Success) throw new InvalidOperationException("未找到 Node 下载链接");
        return "https://nodejs.org/dist/latest-v20.x/" + m.Groups[1].Value;
    }

    /// <summary>Temurin JRE21（~55MB，运行 jar 足够；LSP 用 JDK 属另一套 lsp\java\）：
    /// 官方 Adoptium API 优先，连通失败回退 TUNA 镜像最新热点版。</summary>
    static string Jre21Url()
    {
        const string official = "https://api.adoptium.net/v3/binary/latest/21/ga/windows/x64/jre/hotspot/normal/eclipse";
        try
        {
            using var hc = Http();
            using var r = hc.GetAsync(official, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            if (r.IsSuccessStatusCode) return official;
        }
        catch { }
        var page = Http().GetStringAsync("https://mirrors.tuna.tsinghua.edu.cn/Adoptium/21/jre/x64/windows/").GetAwaiter().GetResult();
        var best = (ver: new Version(0, 0, 0), build: 0, file: "");
        foreach (Match m in Regex.Matches(page, @"href=""(OpenJDK21U-jre_x64_windows_hotspot_([0-9]+\.[0-9]+\.[0-9]+)_([0-9]+)\.zip)"""))
        {
            if (!Version.TryParse(m.Groups[2].Value, out var v)) continue;
            var b = int.Parse(m.Groups[3].Value);
            if (v > best.ver || (v == best.ver && b > best.build)) best = (v, b, m.Groups[1].Value);
        }
        if (best.file.Length == 0) throw new InvalidOperationException("TUNA 镜像未找到 JRE21 zip");
        return "https://mirrors.tuna.tsinghua.edu.cn/Adoptium/21/jre/x64/windows/" + best.file;
    }

    static HttpClient Http()
    {
        var hc = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        hc.DefaultRequestHeaders.UserAgent.ParseAdd("GAIRR/1.0 (+https://gairr.com)");
        return hc;
    }

    static void Download(string url, string destFile)
    {
        using var hc = Http();
        using var resp = hc.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        resp.EnsureSuccessStatusCode();
        using var src = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
        using var dst = File.Create(destFile);
        src.CopyTo(dst);
    }

    static void ExtractZip(string zip, string destDir)
    {
        Directory.CreateDirectory(destDir);
        ZipFile.ExtractToDirectory(zip, destDir, true);
    }

    /// <summary>若解压产物嵌了一层目录（zip 根非目标文件），把内容上提到目标层（python embeddable 为平铺，防御性保留）</summary>
    static void MoveUpIfNested(string dir, string exeName)
    {
        if (File.Exists(Path.Combine(dir, exeName))) return;
        foreach (var sub in Directory.GetDirectories(dir))
        {
            var f = Path.Combine(sub, exeName);
            if (!File.Exists(f)) continue;
            foreach (var src in Directory.GetFiles(sub)) File.Move(src, Path.Combine(dir, Path.GetFileName(src)));
            foreach (var sd in Directory.GetDirectories(sub))
                Directory.Move(sd, Path.Combine(dir, Path.GetFileName(sd)));
            TryDeleteDir(sub);
            break;
        }
    }

    static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }
    static void TryDeleteDir(string d) { try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { } }
}
