using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GAIRR.Core.Lsp;

/// <summary>LSP 客户端：JSON-RPC over stdio 最小实现（只覆盖本工具集需要的协议方法）。
/// 帧格式：Content-Length 头 + UTF-8 JSON body；与 Roslyn LanguageServer 等标准 server 互通。</summary>
sealed class LspClient : IDisposable
{
    readonly Process proc;
    readonly Stream stdin;
    readonly object writeLock = new();
    readonly Dictionary<int, TaskCompletionSource<JsonNode?>> pending = new();
    readonly CancellationTokenSource recvCts = new();
    volatile bool disposed;
    int seq;

    /// <summary>启动任意 stdio LSP server 进程并完成 initialize 握手（30 秒超时，失败抛异常）。
    /// psi 由调用方按语言构造（C#=Roslyn、Java=jdtls、Vue=volar 等）；stderr 被收集用于失败时给出明确原因</summary>
    public static async Task<LspClient> StartAsync(ProcessStartInfo psi, string rootDir, CancellationToken ct, JsonObject? initOptions = null)
    {
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("语言服务器进程启动失败");
        var client = new LspClient(proc);

        // initialize：workspaceFolders 传项目根，server 自行解析 csproj/依赖
        var initParams = new JsonObject
        {
            ["processId"] = Environment.ProcessId,
            ["rootUri"] = UriOf(rootDir),
            ["capabilities"] = new JsonObject(),
            ["workspaceFolders"] = new JsonArray
            {
                new JsonObject
                {
                    ["uri"] = UriOf(rootDir),
                    ["name"] = Path.GetFileName(rootDir.TrimEnd('\\', '/')),
                },
            },
        };
        // 语言相关额外参数（vue 的 tsdk/hybridMode 等）：调用方按语言构造，其余语言可缺省
        if (initOptions != null) initParams["initializationOptions"] = initOptions;
        // server 配置错误（如缺 .NET 运行时 / 路径指错）会在数秒内退出并向 stderr 报错：后台收集，失败时给出明确原因
        var errBuf = new StringBuilder();
        var errTask = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await proc.StandardError.ReadLineAsync()) != null)
                    lock (errBuf) { if (errBuf.Length > 800) errBuf.Clear(); errBuf.AppendLine(line); }
            }
            catch { }
        });

        try
        {
            var req = client.RequestAsync("initialize", initParams, ct, TimeSpan.FromSeconds(30));
            // 快速退出=配置问题（缺运行时/路径错）：stderr 已落定，直接剥出真实原因
            if (await Task.Run(() => proc.WaitForExit(3000)))
            {
                try { await errTask; } catch { }   // 等 stderr 读完再加进报错
                throw new InvalidOperationException($"语言服务器启动即退出（退出码 {proc.ExitCode}）：{ErrTail(errBuf)}");
            }
            await req;
            client.Notify("initialized", new JsonObject());
            return client;
        }
        catch (Exception ex)
        {
            client.Dispose();
            var err = ErrTail(errBuf);
            if (proc.HasExited && err.Length > 0)
                throw new InvalidOperationException($"语言服务器进程已退出（退出码 {proc.ExitCode}）：{err}");
            if (ex is TimeoutException && err.Length > 0)
                throw new TimeoutException($"LSP 请求 initialize 超时（stderr: {err}）");
            throw;
        }
    }

    /// <summary>stderr 收集内容瘦身（去空行、截断），用于失败归因</summary>
    static string ErrTail(StringBuilder buf)
    {
        string s;
        lock (buf) s = buf.ToString();
        s = string.Join(" | ", s.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()));
        return s.Length > 600 ? s[^600..] : s;
    }

    LspClient(Process proc)
    {
        this.proc = proc;
        stdin = proc.StandardInput.BaseStream;
        var reader = new Thread(ReadLoop) { IsBackground = true, Name = "LspRead" };
        reader.Start();
    }

    /// <summary>file:// URI：Windows 绝对路径经 Uri 类转换即合规</summary>
    public static string UriOf(string absPath) => new Uri(absPath).AbsoluteUri;

    public async Task<JsonNode?> RequestAsync(string method, JsonNode prms, CancellationToken ct, TimeSpan? timeout = null)
    {
        var id = Interlocked.Increment(ref seq);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (pending) pending[id] = tcs;
        Send(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = prms,
        });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout != null) cts.CancelAfter(timeout.Value);
        using var reg = cts.Token.Register(() => tcs.TrySetException(new TimeoutException($"LSP 请求 {method} 超时")));
        try { return await tcs.Task; }
        finally { lock (pending) pending.Remove(id); }
    }

    /// <summary>通知（无响应）：didOpen/didChange/exit 等</summary>
    public void Notify(string method, JsonNode prms) => Send(new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["method"] = method,
        ["params"] = prms,
    });

    void Send(JsonObject msg)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(msg);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        lock (writeLock)
        {
            stdin.Write(header, 0, header.Length);
            stdin.Write(body, 0, body.Length);
            stdin.Flush();
        }
    }

    /// <summary>接收线程：逐帧解析，按 id 分发完成请求 / 忽略通知（诊断等暂无消费方）</summary>
    void ReadLoop()
    {
        var s = proc.StandardOutput.BaseStream;
        var headerBuf = new List<byte>(64);
        var len = -1;
        try
        {
            while (!recvCts.IsCancellationRequested)
            {
                var b = s.ReadByte();
                if (b < 0) break;   // 进程退出
                if (len < 0)
                {
                    headerBuf.Add((byte)b);
                    // 头部结束空行：\r\n\r\n
                    var n = headerBuf.Count;
                    if (n >= 4 && headerBuf[n - 4] == '\r' && headerBuf[n - 3] == '\n' && headerBuf[n - 2] == '\r' && headerBuf[n - 1] == '\n')
                    {
                        var head = Encoding.ASCII.GetString(headerBuf.ToArray());
                        headerBuf.Clear();
                        var m = System.Text.RegularExpressions.Regex.Match(head, @"Content-Length:\s*(\d+)");
                        len = m.Success ? int.Parse(m.Groups[1].Value) : -1;
                    }
                    continue;
                }
                var buf = new byte[len];
                buf[0] = (byte)b;   // ReadByte 已取走 body 首字节，须计入（否则 body 少一字节、错吞下一帧头）
                var got = 1;
                while (got < len)
                {
                    var r = s.Read(buf, got, len - got);
                    if (r <= 0) break;
                    got += r;
                }
                if (got < len) break;   // 流提前结束
                len = -1;
                Dispatch(JsonNode.Parse(buf) as JsonObject);
            }
        }
        catch { }
    }

    void Dispatch(JsonObject? msg)
    {
        if (msg == null) return;
        // server → client 请求（registerCapability / workspace/configuration 等）：id+method 且无 result/error。
        // 必须回响应（volar 等会等待结果，不回则 server 永久挂起）——回 null 响应表示未支持
        if (msg["id"] != null && msg["method"] != null && msg["result"] == null && msg["error"] == null)
        {
            Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = msg["id"]!.DeepClone(), ["result"] = null });
            return;
        }
        if (msg["id"] is JsonValue idVal && msg["result"] != null)
        {
            var id = idVal.GetValue<int>();
            TaskCompletionSource<JsonNode?>? tcs;
            lock (pending)
            {
                if (!pending.TryGetValue(id, out tcs)) return;
                pending.Remove(id);
            }
            tcs.SetResult(msg["result"]?.DeepClone());
        }
        else if (msg["id"] is JsonValue errIdVal && msg["error"] is JsonObject err)
        {
            var id = errIdVal.GetValue<int>();
            TaskCompletionSource<JsonNode?>? tcs;
            lock (pending)
            {
                if (!pending.TryGetValue(id, out tcs)) return;
                pending.Remove(id);
            }
            tcs.SetException(new InvalidOperationException($"LSP 错误：{err["message"]?.GetValue<string>() ?? "未知"}"));
        }
        // 其余为服务器通知（publishDiagnostics 等）：当前无消费方，忽略
    }

    /// <summary>优雅退出：shutdown→exit→杀进程</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            if (!proc.HasExited)
            {
                try { Notify("exit", new JsonObject()); } catch { }
                try { if (!proc.WaitForExit(1000)) proc.Kill(); } catch { }
            }
        }
        catch { }
        recvCts.Cancel();
        try { stdin.Dispose(); } catch { }
    }
}