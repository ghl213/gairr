using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GAIRR.Core.Lsp;

/// <summary>多语言 LSP 运行环境：按文件扩展名判定语言；探测 / 自包含下载各语言 server 与运行环境（GAIRR 目录 lsp\ 下），
/// 按语言构造启动命令。后端：C#=Roslyn、Java=jdtls+JDK21、Vue/TS=volar+Node。</summary>
public static class LspEnv
{
    /// <summary>GAIRR 目录下 lsp\ 子目录（自包含运行环境根）</summary>
    public static string Root => Path.Combine(AppContext.BaseDirectory, "lsp");

    /// <summary>文件 → 语言标识：csharp / java / vue / ""（不支持）</summary>
    public static string LangOf(string file)
    {
        var ext = Path.GetExtension(file).ToLowerInvariant();
        return ext switch
        {
            ".cs" => "csharp",
            ".java" => "java",
            ".vue" or ".js" or ".jsx" or ".ts" or ".tsx" => "vue",
            _ => "",
        };
    }

    /// <summary>LSP 协议 languageId</summary>
    public static string LanguageIdOf(string lang) => lang == "csharp" ? "csharp" : lang;

    /// <summary>显示名</summary>
    public static string NameOf(string lang) => lang switch { "java" => "Java", "vue" => "Vue", _ => "C#" };

    static string Expand(string p) => p.Length > 0 ? Environment.ExpandEnvironmentVariables(p) : "";

    /// <summary>环境目录存在性探测（launcher/可执行文件任一层面都算"有"）；Java 另要求 JDK 21+（新版 jdtls 基线）</summary>
    public static bool JavaReady() => JavaVersionOk() && FindJdtlsLauncher() != null;

    /// <summary>Vue 就绪=Node+volar 在且版本组合有效（volar 2.x + TS 5.x）：
    /// volar 3.x 依赖宿主 tsserver（裸 stdio 请求永久挂起），TS 7.x 无 ts.server API——两者裸 stdio 均不可用</summary>
    public static bool VueReady() => FindNodeExe() != null && FindVolarJs() != null && VolarVersionOk() && TsVersionOk();

    /// <summary>volar 主版本合规（2.x）</summary>
    static bool VolarVersionOk()
    {
        var js = FindVolarJs();
        return js != null && PkgMajor(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(js))!, "package.json")) == 2;
    }

    /// <summary>配套 TypeScript 主版本合规（5.x；7.x 移除了 ts.server 协议）
    /// volarRoot：vue-language-server.js 向上 4 层=node_modules（parent: bin→包根→@vue→node_modules）</summary>
    static bool TsVersionOk()
    {
        var js = FindVolarJs();
        return js != null && PkgMajor(Path.Combine(VolarRoot(js), "typescript", "package.json")) == 5;
    }

    /// <summary>server js 所在 node_modules 目录（volarRoot）：js 向上 4 层（bin→包根→@vue→node_modules）</summary>
    static string VolarRoot(string js)
    {
        var d = Path.GetDirectoryName(js);
        for (var i = 0; i < 3 && d != null; i++) d = Path.GetDirectoryName(d);
        return d!;
    }

    static int PkgMajor(string pkgJson)
    {
        try
        {
            var v = JsonNode.Parse(File.ReadAllText(pkgJson))?["version"]?.GetValue<string>();
            if (v == null) return 0;
            var i = v.IndexOf('.');
            return int.TryParse(i > 0 ? v[..i] : v, out var m) ? m : 0;
        }
        catch { return 0; }
    }

    // ---------- Java：JDK ----------

    /// <summary>探测 java.exe：JdkPath 配置 → lsp\java\ 自带 → JAVA_HOME → PATH</summary>
    public static string? FindJavaExe()
    {
        var cfg = Expand(SystemCfg.LspJdkPath);
        if (cfg.Length > 0)
        {
            var f = Path.Combine(cfg, "bin", "java.exe");
            if (File.Exists(f)) return f;
        }
        var dir = Path.Combine(Root, "java");
        if (Directory.Exists(dir))
            foreach (var jdk in Directory.GetDirectories(dir))
            {
                var f = Path.Combine(jdk, "bin", "java.exe");
                if (File.Exists(f)) return f;
            }
        var home = Environment.GetEnvironmentVariable("JAVA_HOME") ?? "";
        var h = Expand(home);
        if (h.Length > 0)
        {
            var f = Path.Combine(h, "bin", "java.exe");
            if (File.Exists(f)) return f;
        }
        foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            var f = Path.Combine(d.Trim(), "java.exe");
            if (f.Length > 3 && File.Exists(f)) return f;
        }
        return null;
    }

    /// <summary>JDK 主版本 ≥21（新版 jdtls 要求 JDK 21+，系统 JAVA_HOME 常为 17）：java -version 解析，失败按不合格处理</summary>
    static bool JavaVersionOk()
    {
        var java = FindJavaExe();
        if (java == null) return false;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = java,
                Arguments = "-version",
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("java -version 启动失败");
            var ver = p.StandardError.ReadToEnd();   // java -version 输出到 stderr
            var m = Regex.Match(ver, @"version \""(\d+)");
            return m.Success && int.TryParse(m.Groups[1].Value, out var maj) && maj >= 21;
        }
        catch { return false; }
    }

    // ---------- Java：jdtls ----------

    /// <summary>jdtls Equinox launcher jar（plugins 目录）；配置 → lsp\jdtls\ 自带</summary>
    public static string? FindJdtlsLauncher() => FindLauncher(Expand(SystemCfg.LspJdtlsPath))
        ?? FindLauncher(Path.Combine(Root, "jdtls"));

    static string? FindLauncher(string jdtlsRoot)
    {
        if (jdtlsRoot.Length == 0 || !Directory.Exists(jdtlsRoot)) return null;
        var plugins = Path.Combine(jdtlsRoot, "plugins");
        if (!Directory.Exists(plugins)) return null;   // 下载中断/残留空目录时按"未就绪"处理，调用方会重下
        var files = Directory.GetFiles(plugins, "org.eclipse.equinox.launcher_*.jar");
        return files.Length > 0 ? files[0] : null;
    }

    /// <summary>jdtls -data 工作区（按项目根 hash 隔离，避免缓存串项目）</summary>
    public static string JavaWsDir(string root)
    {
        var h = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(root)))[..8];
        return Path.Combine(Path.Combine(Root, "java"), "ws-" + h);
    }

    // ---------- Vue：Node ----------

    /// <summary>探测 node.exe：NodePath 配置 → PATH → lsp\node\ 自带</summary>
    public static string? FindNodeExe()
    {
        var cfg = Expand(SystemCfg.LspNodePath);
        if (cfg.Length > 0 && File.Exists(cfg)) return cfg;
        foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            var f = Path.Combine(d.Trim(), "node.exe");
            if (f.Length > 3 && File.Exists(f)) return f;
        }
        var dir = Path.Combine(Root, "node");
        if (Directory.Exists(dir))
            foreach (var n in Directory.GetDirectories(dir))
            {
                var f = Path.Combine(n, "node.exe");
                if (File.Exists(f)) return f;
            }
        return null;
    }

    /// <summary>vue-language-server.js 入口：VolarPath 配置 → lsp\volar\ 自带</summary>
    public static string? FindVolarJs()
    {
        var c = Expand(SystemCfg.LspVolarPath);
        if (c.Length > 0 && File.Exists(c)) return c;
        var f = Path.Combine(Root, "volar", "node_modules", "@vue", "language-server", "bin", "vue-language-server.js");
        return File.Exists(f) ? f : null;
    }

    /// <summary>各语言 initialize 额外参数：vue=volar 2.x 需 tsdk（指向 TS 包 lib，server 用 require.resolve 定位）
    /// 与 vue.hybridMode:false（进程内建 TS 工程而非依赖宿主 tsserver，裸 stdio 才可用）；其余语言无特殊参数</summary>
    public static JsonObject? InitializeOptionsFor(string lang)
    {
        if (lang != "vue") return null;
        var js = FindVolarJs() ?? throw new InvalidOperationException("volar 未安装，无法构造初始化参数");
        var tsdk = Path.Combine(VolarRoot(js), "typescript", "lib").Replace('\\', '/');
        return new JsonObject
        {
            ["typescript"] = new JsonObject { ["tsdk"] = tsdk },
            ["vue"] = new JsonObject { ["hybridMode"] = false },
        };
    }

    // ---------- 启动命令构造 ----------

    /// <summary>按语言构造 stdio LSP server 进程启动参数（含环境变量）；组件缺失返回 null（调用方先 Ensure）</summary>
    public static ProcessStartInfo? PsiFor(string lang, string root)
    {
        var psi = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        switch (lang)
        {
            case "csharp":
                var exe = SystemCfg.LspServerPath.Length > 0 ? Expand(SystemCfg.LspServerPath) : "";
                if (exe.Length == 0 || !File.Exists(exe))
                {
                    exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".dotnet", "tools", "dotnet-language-server.exe");
                    if (!File.Exists(exe)) return null;
                }
                psi.FileName = exe;
                // --stdio：stdio 通信；--autoLoadProjects：按 initialize 的 workspaceFolders 自动发现/加载项目；
                // --clientProcessId：客户端退出时 server 自动终止（防残留）
                psi.Arguments = "--stdio --autoLoadProjects --clientProcessId " + Environment.ProcessId + " --telemetryLevel off";
                var dr = Expand(SystemCfg.LspDotnetRoot);
                if (dr.Length > 0) psi.Environment["DOTNET_ROOT"] = dr;
                return psi;

            case "java":
                var java = FindJavaExe();
                var launcher = FindJdtlsLauncher();
                if (java == null || launcher == null) return null;
                psi.FileName = java;
                // jdtls 官方启动参数：Eclipse RCP 应用 + 解决 JDK 模块限制的 --add-opens（精简自官方 jdtls.bat）
                psi.ArgumentList.Add("-Declipse.application=org.eclipse.jdt.ls.core.id1");
                psi.ArgumentList.Add("-Dosgi.bundles.defaultStartLevel=4");
                psi.ArgumentList.Add("-Declipse.product=org.eclipse.jdt.ls.core.product");
                psi.ArgumentList.Add("-Xmx1G");
                psi.ArgumentList.Add("--add-modules=ALL-SYSTEM");
                psi.ArgumentList.Add("--add-opens"); psi.ArgumentList.Add("java.base/java.util=ALL-UNNAMED");
                psi.ArgumentList.Add("--add-opens"); psi.ArgumentList.Add("java.base/java.lang=ALL-UNNAMED");
                psi.ArgumentList.Add("--add-opens"); psi.ArgumentList.Add("java.base/java.text=ALL-UNNAMED");
                psi.ArgumentList.Add("--add-opens"); psi.ArgumentList.Add("java.desktop/java.awt.font=ALL-UNNAMED");
                psi.ArgumentList.Add("--add-opens"); psi.ArgumentList.Add("java.base/sun.nio.ch=ALL-UNNAMED");
                psi.ArgumentList.Add("--add-opens"); psi.ArgumentList.Add("java.base/java.io=ALL-UNNAMED");
                psi.ArgumentList.Add("--add-opens"); psi.ArgumentList.Add("java.base/java.util.concurrent=ALL-UNNAMED");
                psi.ArgumentList.Add("--add-opens"); psi.ArgumentList.Add("java.base/java.net=ALL-UNNAMED");
                psi.ArgumentList.Add("-jar");
                psi.ArgumentList.Add(launcher);
                psi.ArgumentList.Add("-configuration");
                psi.ArgumentList.Add(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(launcher))!, "config_win"));
                psi.ArgumentList.Add("-data");
                psi.ArgumentList.Add(JavaWsDir(root));
                return psi;

            case "vue":
                var node = FindNodeExe();
                var volar = FindVolarJs();
                if (node == null || volar == null) return null;
                psi.FileName = node;
                psi.ArgumentList.Add(volar);
                psi.ArgumentList.Add("--stdio");
                return psi;
        }
        return null;
    }

    // ---------- 环境确保（探测 + 自包含下载到 lsp\ 子目录） ----------

    static readonly SemaphoreSlim ensureGate = new(1, 1);

    /// <summary>确保某语言环境就绪（缺则按 system.ini 配置自动下载到 lsp\）；返回 null=就绪，否则为不可用原因（已发送 status）</summary>
    public static async Task<string?> EnsureAsync(string lang, Action<string> status, CancellationToken ct)
    {
        await ensureGate.WaitAsync(ct);
        try
        {
            switch (lang)
            {
                case "java": return await EnsureJavaAsync(status, ct);
                case "vue": return await EnsureVueAsync(status, ct);
            }
            return null;
        }
        finally { ensureGate.Release(); }
    }

    static async Task<string?> EnsureJavaAsync(Action<string> status, CancellationToken ct)
    {
        if (JavaReady()) return null;
        if (!SystemCfg.LspJavaAutoDownload)
            return "未配置 Java 环境：system.ini [LspJava] 填 JdkPath/JdtlsPath 或开 AutoDownload=1";

        // 1. JDK 21+ → lsp\java\（优先自带：新版 jdtls 要求 JDK 21+，系统 JAVA_HOME 常为 17——版本不足同样下载自带）
        if (!JavaVersionOk())
        {
            var dir = Path.Combine(Root, "java");
            Directory.CreateDirectory(dir);
            var zip = Path.Combine(dir, "jdk21-download.zip");
            try
            {
                status("Java：下载 JDK 21（Adoptium Temurin win-x64，~190MB，一次性）…");
                var url = await LatestJdk21UrlAsync(ct);   // 官方 API 优先，国内网络失败自动回退 TUNA 镜像
                await DownloadAsync(url, zip, status, ct);
                status("Java：解压 JDK 21…");
                ExtractZip(zip, dir);
                TryDelete(zip);
                if (!JavaVersionOk()) return "JDK21 下载解压后仍未就绪（bin\\java.exe 缺失或版本低于 21）";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                TryDelete(zip);
                return $"JDK21 下载失败：{ex.Message}";
            }
        }

        // 2. jdtls → lsp\jdtls\
        if (FindJdtlsLauncher() == null)
        {
            var dir = Path.Combine(Root, "jdtls");
            Directory.CreateDirectory(dir);
            var tgz = Path.Combine(Root, "jdtls-download.tar.gz");
            try
            {
                var url = await LatestJdtlsUrlAsync(ct);
                status("Java：下载 jdtls 语言服务器（Eclipse 官方里程碑，~180MB，一次性）…");
                await DownloadAsync(url, tgz, status, ct);
                status("Java：解压 jdtls…");
                ExtractTarGz(tgz, dir);
                TryDelete(tgz);
                if (FindJdtlsLauncher() == null) return "jdtls 解压后未找到 launcher";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                TryDelete(tgz);
                return $"jdtls 下载失败：{ex.Message}";
            }
        }
        return null;
    }

    static async Task<string?> EnsureVueAsync(Action<string> status, CancellationToken ct)
    {
        if (VueReady()) return null;
        if (!SystemCfg.LspVueAutoDownload)
            return "未配置 Vue 环境：system.ini [LspVue] 填 NodePath/VolarPath 或开 AutoDownload=1";

        // 1. Node（系统 PATH 有就直接用；否则下载官方 LTS 便携版到 lsp\node\）
        var nodeExe = FindNodeExe();
        if (nodeExe == null)
        {
            var dir = Path.Combine(Root, "node");
            Directory.CreateDirectory(dir);
            var zip = Path.Combine(dir, "node-download.zip");
            try
            {
                var url = await LatestNodeUrlAsync(ct);
                status("Vue：下载 Node（官方 LTS v20 win-x64，~30MB，一次性）…");
                await DownloadAsync(url, zip, status, ct);
                status("Vue：解压 Node…");
                ExtractZip(zip, dir);
                TryDelete(zip);
                nodeExe = FindNodeExe();
                if (nodeExe == null) return "Node 解压后未找到 node.exe";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                TryDelete(zip);
                return $"Node 下载失败：{ex.Message}";
            }
        }

        // 2. volar → lsp\volar\（npm 安装：钉 volar 2.x + TS 5.x——npm 默认流已升级到 volar 3.x/TS 7.x 裸 stdio 不可用；
        //    volar-service-emmet 依赖 git 源 @emmetio/css-parser→npm 鉴权失败，overrides 钉到 npm 版规避）
        if (!VolarVersionOk() || !TsVersionOk())
        {
            var volarDir = Path.Combine(Root, "volar");
            Directory.CreateDirectory(volarDir);
            File.WriteAllText(Path.Combine(volarDir, "package.json"),
                "{\n  \"name\": \"gairr-volar\",\n  \"private\": true,\n" +
                "  \"dependencies\": {\n    \"@vue/language-server\": \"2.2.12\",\n    \"typescript\": \"5.8\"\n  },\n" +
                "  \"overrides\": { \"@emmetio/css-parser\": \"0.4.1\" }\n}\n");
            var npmCli = Path.Combine(Path.GetDirectoryName(nodeExe)!, "node_modules", "npm", "bin", "npm-cli.js");
            if (!File.Exists(npmCli)) return "未找到 npm（node 目录异常）";
            try
            {
                // 清掉旧安装树：组合不一致（如 volar 3.x/TS 7.x）时确保从干净的 2.x 重装（含旧锁文件）
                var old = Path.Combine(volarDir, "node_modules");
                if (Directory.Exists(old)) TryDeleteDir(old);
                var oldLock = Path.Combine(volarDir, "package-lock.json");
                if (File.Exists(oldLock)) TryDelete(oldLock);

                status("Vue：npm 安装 volar 2.x + TypeScript 5.x（国内镜像源，1~2 分钟）…");
                var psi = new ProcessStartInfo
                {
                    FileName = nodeExe,
                    WorkingDirectory = volarDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                psi.ArgumentList.Add(npmCli);
                psi.ArgumentList.Add("install");
                psi.ArgumentList.Add("--registry=https://registry.npmmirror.com");
                psi.ArgumentList.Add("--no-audit"); psi.ArgumentList.Add("--no-fund");
                using var p = Process.Start(psi) ?? throw new InvalidOperationException("npm 进程启动失败");
                var outp = p.StandardOutput.ReadToEndAsync();
                var errp = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit((int)TimeSpan.FromMinutes(10).TotalMilliseconds))
                {
                    try { p.Kill(); } catch { }
                    return "npm 安装超时（10 分钟）";
                }
                if (p.ExitCode != 0)
                {
                    var msg = errp.Result.Trim();
                    return $"npm 安装失败（exit {p.ExitCode}）：{(msg.Length > 300 ? msg[..300] : msg)}";
                }
                if (FindVolarJs() == null) return "npm 安装完成但未找到 vue-language-server.js";
                if (!VolarVersionOk() || !TsVersionOk())
                    return "npm 安装完成但版本不兼容（应 volar 2.x + TS 5.x；npm 默认流已升级 volar 3.x/TS 7.x，3.x 裸 stdio 不可用）";
                status("Vue：volar 就绪（2.x + TS 5.x）");
            }
            catch (Exception ex)
            {
                return $"npm 安装异常：{ex.Message}";
            }
        }
        return null;
    }

    // ---------- 下载与解压 ----------

    static HttpClient Http()
    {
        var hc = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        hc.DefaultRequestHeaders.UserAgent.ParseAdd("GAIRR/1.0 (+https://gairr.com)");
        return hc;
    }

    static async Task DownloadAsync(string url, string destFile, Action<string> status, CancellationToken ct)
    {
        using var hc = Http();
        using var resp = await hc.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? 0;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        var dir = Path.GetDirectoryName(destFile)!;
        Directory.CreateDirectory(dir);
        await using var dst = File.Create(destFile);
        var buf = new byte[81920];
        long done = 0, lastReport = 0;
        while (true)
        {
            var r = await src.ReadAsync(buf, ct);
            if (r <= 0) break;
            await dst.WriteAsync(buf.AsMemory(0, r), ct);
            done += r;
            if (total > 0 && done - lastReport >= 4L << 20)   // 每 4MB 报一次进度
            {
                lastReport = done;
                status($"下载中：{Path.GetFileName(destFile)} {done * 100 / total}%（{done >> 20}MB/{total >> 20}MB）");
            }
        }
    }

    static void ExtractZip(string zip, string destDir)
    {
        Directory.CreateDirectory(destDir);
        ZipFile.ExtractToDirectory(zip, destDir, true);
    }

    static void ExtractTarGz(string tgz, string destDir)
    {
        Directory.CreateDirectory(destDir);
        using var fs = File.OpenRead(tgz);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var tar = new TarReader(gz);
        while (tar.GetNextEntry() is { } e)
        {
            var outPath = SafeJoin(destDir, e.Name);
            if (e.EntryType is TarEntryType.Directory or TarEntryType.SymbolicLink or TarEntryType.HardLink) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            using var w = File.Create(outPath);
            e.DataStream?.CopyTo(w);
        }
    }

    /// <summary>tar 条目路径净化：防 ../ 越界</summary>
    static string SafeJoin(string baseDir, string name)
    {
        name = name.Replace('\\', '/').TrimStart('/');
        var full = Path.GetFullPath(Path.Combine(baseDir, name));
        if (!full.StartsWith(Path.GetFullPath(baseDir), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"解压路径越界：{name}");
        return full;
    }

    static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }
    static void TryDeleteDir(string d) { try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { } }

    /// <summary>JDK21 下载地址：官方 Adoptium API 优先（latest/21 重定向到实际 zip）；连通性失败自动回退
    /// 清华 TUNA 镜像（取 archives 目录最新热点版，文件名 21.0.x_y 数值比较）</summary>
    static async Task<string> LatestJdk21UrlAsync(CancellationToken ct)
    {
        using var hc = Http();
        const string official = "https://api.adoptium.net/v3/binary/latest/21/ga/windows/x64/jdk/hotspot/normal/eclipse?project=jdk";
        try
        {
            using var r = await hc.GetAsync(official, HttpCompletionOption.ResponseHeadersRead, ct);
            if (r.IsSuccessStatusCode) return official;
        }
        catch { }
        // 镜像回退：TUNA Adoptium 目录，取最新 21 热点版 zip
        var page = await hc.GetStringAsync("https://mirrors.tuna.tsinghua.edu.cn/Adoptium/21/jdk/x64/windows/", ct);
        var best = (ver: new Version(0, 0, 0), build: 0, file: "");
        foreach (Match m in Regex.Matches(page, @"href=""(OpenJDK21U-jdk_x64_windows_hotspot_([0-9]+\.[0-9]+\.[0-9]+)_([0-9]+)\.zip)"""))
        {
            if (!Version.TryParse(m.Groups[2].Value, out var v)) continue;
            var build = int.Parse(m.Groups[3].Value);
            if (v > best.ver || (v == best.ver && build > best.build))
                best = (v, build, m.Groups[1].Value);
        }
        if (best.file.Length == 0) throw new InvalidOperationException("TUNA 镜像未找到 JDK21 zip");
        return "https://mirrors.tuna.tsinghua.edu.cn/Adoptium/21/jdk/x64/windows/" + best.file;
    }

    /// <summary>解析 jdtls 里程碑目录取最新版 tar.gz 直链（版本号带发布时间戳，不硬编码）</summary>
    static async Task<string> LatestJdtlsUrlAsync(CancellationToken ct)
    {
        using var hc = Http();
        var page = await hc.GetStringAsync("https://download.eclipse.org/jdtls/milestones/", ct);
        var best = (ver: new Version(0, 0, 0), dir: "");
        // 目录链接：2026 改版后形如 href='/jdtls/milestones/1.60.0'>（单引号、无尾斜杠）；保留双引号回退以防回改
        var dirRe = new Regex(@"href=['\x22]/?jdtls/milestones/([0-9]+\.[0-9]+\.[0-9]+)['\x22]");
        foreach (Match m in dirRe.Matches(page))
        {
            if (Version.TryParse(m.Groups[1].Value, out var v) && v > best.ver)
                best = (v, m.Groups[1].Value);
        }
        if (best.dir.Length == 0) throw new InvalidOperationException("未找到 jdtls 里程碑版本");
        var pg2 = await hc.GetStringAsync($"https://download.eclipse.org/jdtls/milestones/{best.dir}/", ct);
        // tar.gz 直链形如 href='https://www.eclipse.org/downloads/download.php?file=/jdtls/milestones/1.60.0/jdt-language-server-1.60.0-202606262232.tar.gz'
        var fm = Regex.Match(pg2, @"href=['\x22][^'\x22]*?(jdt-language-server-[^'\x22]*\.tar\.gz)['\x22]");
        if (!fm.Success) throw new InvalidOperationException($"未在 {best.dir} 找到 tar.gz");
        return $"https://download.eclipse.org/jdtls/milestones/{best.dir}/{fm.Groups[1].Value}";
    }

    /// <summary>解析 nodejs.org latest-v20.x 目录取 win-x64 zip 直链</summary>
    static async Task<string> LatestNodeUrlAsync(CancellationToken ct)
    {
        using var hc = Http();
        var page = await hc.GetStringAsync("https://nodejs.org/dist/latest-v20.x/", ct);
        var m = Regex.Match(page, @"href=""(node-v20\.[0-9]+\.[0-9]+-win-x64\.zip)""");
        if (!m.Success) throw new InvalidOperationException("未找到 Node 下载链接");
        return "https://nodejs.org/dist/latest-v20.x/" + m.Groups[1].Value;
    }
}