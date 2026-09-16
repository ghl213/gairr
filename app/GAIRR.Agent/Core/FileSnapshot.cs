using System.Collections.Concurrent;

namespace GAIRR.Core;

/// <summary>
/// 文件"读时快照"登记：Read 工具读成功后记录文件最后修改时间戳与长度，Write/Edit 写盘前校验。
/// 解决"后拿到锁的会话仍可能拿旧快照覆盖新内容"：锁只保证写不交叉，拿到锁时文件可能已被
/// 其它会话/进程/外部编辑改过；写前比对时间戳，变了就拒绝盲目覆盖或提示重新定位。
/// </summary>
public static class FileSnapshot
{
    /// <summary>规范化路径 → 上次 Read 时的时间戳与长度（进程内共享，同一文件只保留最新值）。</summary>
    static readonly ConcurrentDictionary<string, (DateTime MtimeUtc, long Length)> Snap = new();

    /// <summary>记录当前文件时间戳（Read 成功后调用；写入类工具自己写盘后也应调用以刷新基线）。</summary>
    public static void Record(string path)
    {
        try
        {
            var key = FileLock.Normalize(path);
            var fi = new FileInfo(path);
            if (!fi.Exists) { Snap.TryRemove(key, out _); return; }
            Snap[key] = (fi.LastWriteTimeUtc, fi.Length);
        }
        catch { }
    }

    /// <summary>距上次 Read 后文件是否被改动。
    /// 无快照（本会话未读过）返回 false——无从判断，不拦截；有快照且时间戳或长度不一致 → true。
    /// 文件被删除也视为有变化。</summary>
    public static bool ChangedSinceRead(string path)
    {
        if (!Snap.TryGetValue(FileLock.Normalize(path), out var snap)) return false;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return true;
            return fi.LastWriteTimeUtc != snap.MtimeUtc || fi.Length != snap.Length;
        }
        catch { return false; }
    }
}
