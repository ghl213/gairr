using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace GAIRR.Core;

/// <summary>
/// 按文件路径的进程级/跨进程互斥锁（Named Mutex）。
/// 覆盖：同轮并行工具、进程内多会话、多 GAIRR 进程操作同一项目根。
/// </summary>
public static class FileLock
{
    static readonly ConcurrentDictionary<string, Mutex> Locks = new();

    /// <summary>进程内占用者登记：归一化路径 → 占用者描述（会话标题，AgentLoop 侧登记）：排队方经 HolderOf 提示"占用者"。
    /// 跨进程占用者不可见（无登记=显示"其它会话/进程"）。</summary>
    static readonly ConcurrentDictionary<string, string> Holders = new();

    /// <summary>按规范化路径取互斥锁；首次创建 Named Mutex（跨进程可见）。</summary>
    public static Mutex For(string path)
    {
        var key = Normalize(path);
        return Locks.GetOrAdd(key, _ =>
        {
            // Local\ 前缀：同一用户会话内跨进程互通；进程崩溃内核自动释放
            var name = @"Local\GAIRR-FileLock-" + Hash(key);
            return new Mutex(false, name);
        });
    }

    /// <summary>按路径取锁并排队等待（返回已持有锁的 Mutex，供 finally ReleaseMutex）。
    /// 语义：并发写同一文件时严格排队——持锁会话正常完成（finally 释放）或进程崩溃（内核自动移交）后才放行，永不超时放弃。
    /// 取代旧的"WaitOne(10 秒) 抢不到就失败"，避免两个会话并行写盘把文件写乱。</summary>
    /// <param name="onWaiting">可选等待上报回调：每次 250ms 轮询未拿到锁时以已等待时长调用一次；拿到锁时以 <see cref="TimeSpan.Zero"/> 调用一次（恢复信号）。
    /// 不传=完全原行为（P4a 排队语义零回归）。回调在等锁线程上同步执行，须轻量、不得阻塞/抛异常。</param>
    /// <param name="holder">可选占用者描述（如会话标题）：拿到锁时登记进进程内占用表，供排队方提示"占用者"；
    /// 释放锁后应同步调用 <see cref="Unregister"/> 注销（条件删除，防误删新占用者登记）。</param>
    public static Mutex Acquire(string path, Action<TimeSpan>? onWaiting = null, string? holder = null)
    {
        var key = Normalize(path);
        var mutex = For(path);
        var sw = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                if (mutex.WaitOne(250))
                {
                    // 拿到锁：登记占用者（进程内可见）+ 上送恢复信号
                    if (holder != null) Holders[key] = holder;
                    onWaiting?.Invoke(TimeSpan.Zero);
                    return mutex;
                }
                // 250ms 未拿到：说明别的会话正持锁，继续排队等它释放
                onWaiting?.Invoke(sw.Elapsed);
            }
            catch (AbandonedMutexException)
            {
                // 上个持锁者进程崩溃/被杀：内核已把锁移交给本线程，视为已获锁，继续执行
                if (holder != null) Holders[key] = holder;
                onWaiting?.Invoke(TimeSpan.Zero);
                return mutex;
            }
        }
    }

    /// <summary>查询进程内占用者描述（排队提示用）：无登记（跨进程占用/未登记）返回 null。</summary>
    public static string? HolderOf(string path)
        => Holders.TryGetValue(Normalize(path), out var h) ? h : null;

    /// <summary>注销占用登记：仅当当前登记值仍等于 <paramref name="holder"/> 时移除（条件删除原子，
    /// 防自己释放慢一步把已接手的新占用者登记抹掉）。</summary>
    public static void Unregister(string path, string? holder)
    {
        if (holder == null) return;
        Holders.TryRemove(new KeyValuePair<string, string>(Normalize(path), holder));
    }

    /// <summary>规范化路径：全路径 + 小写 + 统一分隔符（大小写/相对路径/\与/差异归一）。</summary>
    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        return full.Replace('/', '\\').ToLowerInvariant();
    }

    static string Hash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes)[..16]; // 64 字符太长，取前 16 位足够防碰撞
    }
}
