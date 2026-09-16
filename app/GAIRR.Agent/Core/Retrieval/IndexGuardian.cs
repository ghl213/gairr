using System.Collections.Concurrent;
using System.IO;

namespace GAIRR.Core;

/// <summary>索引检查官：文件变更后主动增量维护符号索引与向量索引，检索路径不再付"全树遍历/全量重建"税。
/// 模型：per-file 待处理集（键 = 项目根|文件路径，同一文件多次通知在队门口合并，天然去重；多项目互不串）
/// + 单 worker 顺序执行（共享索引结构天然串行，避免并发 upsert 竞态；单文件任务百毫秒级，并行无收益）。
/// 事件源：① Write/Edit 写后同步钩子（主，无漏报）；② 每项目一个 FileSystemWatcher 兜底外部修改（IDE/git/手工）；
/// ③ 低频文件数核对兜底（防线，只查文件数不扫内容）。
/// 处理时以磁盘为准（重读当前 mtime+内容）：处理期间又被改则重新入队，保证最后一次修改必被索引。</summary>
public static class IndexGuardian
{
    sealed class PendingItem { public string Root = ""; public string Rel = ""; public DateTime At = DateTime.MinValue; }

    static readonly ConcurrentDictionary<string, PendingItem> Pending = new(StringComparer.OrdinalIgnoreCase);
    static readonly ConcurrentDictionary<string, FileSystemWatcher> Watchers = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, int> FileCounts = new(StringComparer.OrdinalIgnoreCase);
    static readonly object CountSync = new();
    static readonly object TimerGate = new();
    static Timer? CheckTimer;
    static int RebuildStreak = 0;

    /// <summary>确保检查官已启动（每项目根幂等；写后钩子/外部修改均走本入口）。</summary>
    public static void EnsureStarted(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
        lock (TimerGate)
        {
            if (CheckTimer == null)
            {
                // 首个项目根启动时拉起 worker 与低频核对定时器（进程内只一次）
                CheckTimer = new Timer(_ => SweepAll(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
                Task.Run(WorkerLoop);
            }
        }
        TryStartWatcher(root);
        lock (CountSync)
        {
            if (!FileCounts.ContainsKey(root))
            {
                var c = CountFiles(root);
                if (c > 0) FileCounts[root] = c;   // 首建基线：避免启动即误判"文件数变化"
            }
        }
    }

    /// <summary>文件变更通知：按（项目根, 文件）合并入队——同一文件重复通知只刷新时间戳，天然去重。</summary>
    public static void NotifyFile(string root, string rel)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(rel)) return;
        var relN = rel.Replace('\\', '/');
        if (relN.StartsWith(".gairr/", StringComparison.OrdinalIgnoreCase)) return;   // 自身产物不反应
        Pending[Key(root, relN)] = new PendingItem { Root = root, Rel = relN, At = DateTime.UtcNow };
    }

    static string Key(string root, string rel) => root.TrimEnd('\\') + "|" + rel;

    /// <summary>每项目一个 FileSystemWatcher 兜底外部修改（per-root，不顶替其他项目的 watcher）。</summary>
    static void TryStartWatcher(string root)
    {
        if (Watchers.ContainsKey(root)) return;
        try
        {
            var w = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
            };
            void OnFs(object? s, FileSystemEventArgs e)
            {
                try
                {
                    if (e.FullPath.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return;
                    var rel = Path.GetRelativePath(root, e.FullPath).Replace('\\', '/');
                    NotifyFile(root, rel);
                }
                catch { }
            }
            w.Changed += OnFs;
            w.Created += OnFs;
            w.Deleted += OnFs;
            w.EnableRaisingEvents = true;
            Watchers[root] = w;
        }
        catch { }
    }

    /// <summary>worker 主循环：每 500ms 取一批待处理（≤8，按通知时间最旧优先）顺序处理；
    /// 每条自带项目根，多项目并存互不串。</summary>
    static void WorkerLoop()
    {
        while (true)
        {
            try { Thread.Sleep(500); } catch { }
            if (Pending.IsEmpty) continue;
            var batch = Pending
                .OrderBy(p => p.Value.At)
                .Take(8)
                .Select(p => p.Key)
                .ToList();
            var requeue = new List<PendingItem>();
            foreach (var k in batch)
            {
                if (!Pending.TryRemove(k, out var item)) continue;
                try
                {
                    // 处理期间又被改：重新入队（保证最后一次修改被索引）
                    if (ProcessFile(item.Root, item.Rel, out var changedDuring) && changedDuring)
                        requeue.Add(new PendingItem { Root = item.Root, Rel = item.Rel });
                }
                catch { }
            }
            // 批内改动只在内存标脏，收尾统一落盘一次（旧实现每文件 Load+Save 整个索引，一批 8 文件 = 8 次全量写盘）
            try { EmbedIndexBuilder.FlushPending(); } catch { }
            foreach (var it in requeue)
                Pending[Key(it.Root, it.Rel)] = new PendingItem { Root = it.Root, Rel = it.Rel, At = DateTime.UtcNow };
        }
    }

    /// <summary>处理单文件：重读磁盘现状 → 增量更新符号索引 + 向量索引；返回处理期间又被改与否。</summary>
    static bool ProcessFile(string root, string rel, out bool changedDuring)
    {
        changedDuring = false;
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(rel)) return false;
        var abs = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(abs))
        {
            SymbolIndex.RemoveFile(rel);   // 文件已删除：清索引条目（作用于当前缓存索引，cache.Root 与通知根一致）
            EmbedIndexBuilder.RemoveFileKeys(root, rel);   // 向量库仍按显式根目录维护
            return true;
        }
        var mtimeBefore = File.GetLastWriteTimeUtc(abs);
        var r = SymbolIndex.UpsertFile(rel);
        if (r == SymbolIndex.UpsertResult.RebuildRequested)
        {
            RebuildStreak++;
            if (RebuildStreak >= 3)
            {
                SymbolIndex.Refresh(new AppConfig { ProjectRoot = root });   // 连续多文件触发重建：全量一次
                RebuildStreak = 0;
            }
            return true;
        }
        if (r == SymbolIndex.UpsertResult.Changed)
        {
            RebuildStreak = 0;
            EmbedIndexBuilder.UpdateFile(root, rel);
        }
        // 处理期间又被改：mtime 变了则重新入队
        try { changedDuring = File.GetLastWriteTimeUtc(abs) > mtimeBefore; } catch { }
        return true;
    }

    /// <summary>低频兜底（30s）：核对文件数，变化则触发一次全量重建（防钩子/Watcher 漏报；内容修改由钩子+Watcher 覆盖）。</summary>
    static void SweepAll()
    {
        var roots = new List<string>();
        lock (CountSync) roots.AddRange(FileCounts.Keys);
        foreach (var root in roots)
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                var c = CountFiles(root);
                lock (CountSync)
                {
                    if (!FileCounts.TryGetValue(root, out var baseC)) { FileCounts[root] = c; continue; }
                    if (c == baseC) continue;
                    FileCounts[root] = c;
                }
                // 文件数变化：全量重建（含新增/删除文件，per-file 无法覆盖）
                SymbolIndex.Refresh(new AppConfig { ProjectRoot = root });
            }
            catch { }
        }
    }

    static int CountFiles(string root)
    {
        var n = 0;
        try
        {
            foreach (var f in Phase1Tools.EnumerateFiles(root))
            {
                var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
                if (rel.StartsWith(".gairr/", StringComparison.OrdinalIgnoreCase)) continue;
                n++;
                if (n > 10000) break;
            }
        }
        catch { }
        return n;
    }
}
