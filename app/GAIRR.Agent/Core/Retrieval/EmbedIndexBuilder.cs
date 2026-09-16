using System.IO;

namespace GAIRR.Core;

/// <summary>向量索引构建器：收集语料 → 批量 embedding → 写入共享 EmbedIndex 实例；
/// 支持全量重建、全量增量与单文件增量（文本未变则跳过），失败静默降级不阻断主流程。
/// 性能约定：① 索引走 EmbedIndex.ForRoot 进程级常驻，不再每次 Load/Save 整个文件；
/// ② embedding 引擎复用 LocalEmbeddingEngine.Shared（ONNX 会话加载是秒级，禁止每次新建）；
/// ③ 单文件增量只收集该文件语料（EmbedCorpus.CollectFile），不重解析全项目 symbols/notes；
/// ④ 高频的单文件更新只标脏，由调用方（IndexGuardian）批末统一 FlushPending 落盘一次。</summary>
public static class EmbedIndexBuilder
{
    /// <summary>全量重建：清空索引 → 收集全量语料 → 逐条 embedding → 落盘</summary>
    public static int Rebuild(AppConfig cfg)
    {
        var root = cfg.ProjectRoot;
        if (root.Length == 0 || !Directory.Exists(root)) return 0;

        var engine = LocalEmbeddingEngine.Shared;
        if (!engine.IsAvailable || !engine.EnsureReady()) return 0;

        var index = EmbedIndex.ForRoot(root);
        index.Clear();

        var items = EmbedCorpus.Collect(cfg);
        int count = 0;
        foreach (var item in items)
        {
            var vec = engine.Embed(item.Text);
            if (vec == null) continue;
            if (index.Upsert(item.Key, item.Text, vec)) count++;
        }

        index.Save();
        return count;
    }

    /// <summary>按 Key 前缀删除条目（用于增量删除场景，如某文件符号全部移除）</summary>
    public static int RemoveByPrefix(AppConfig cfg, string prefix)
    {
        var root = cfg.ProjectRoot;
        if (root.Length == 0 || !Directory.Exists(root)) return 0;
        var removed = EmbedIndex.ForRoot(root).RemoveMany(
            EmbedIndex.ForRoot(root).All()
                .Where(e => e.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(e => e.Key).ToList());
        EmbedIndex.ForRoot(root).SaveIfDirty();
        return removed;
    }

    /// <summary>全量增量更新：仅对文本变更的条目重新 embedding，并清理已消失条目；返回变更条数</summary>
    public static int Update(AppConfig cfg)
    {
        var root = cfg.ProjectRoot;
        if (root.Length == 0 || !Directory.Exists(root)) return 0;

        var index = EmbedIndex.ForRoot(root);
        if (index.Count == 0 && !index.Load()) return 0;

        var engine = LocalEmbeddingEngine.Shared;
        if (!engine.IsAvailable || !engine.EnsureReady()) return 0;

        var items = EmbedCorpus.Collect(cfg);
        int changed = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            seen.Add(item.Key);
            var existing = index.Get(item.Key);
            if (existing != null && existing.Text == item.Text) continue;
            var vec = engine.Embed(item.Text);
            if (vec == null) continue;
            if (index.Upsert(item.Key, item.Text, vec)) changed++;
        }

        // 清理已删除的条目（一次批量删，避免逐条 O(n) 查找）
        var stale = index.All().Where(e => !seen.Contains(e.Key)).Select(e => e.Key).ToList();
        if (stale.Count > 0) changed += index.RemoveMany(stale);

        index.SaveIfDirty();
        return changed;
    }

    /// <summary>单文件增量更新：删除该文件旧条目 → 只收集该文件语料 → 重嵌（标脏不落盘）。
    /// 语料 key 约定：symbol:{rel}:{name}、summary:{rel}、summary:{rel}:m。返回变更条数。</summary>
    public static int UpdateFile(string root, string rel)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(rel)) return 0;
        if (!SystemCfg.Embed) return 0;
        if (!Directory.Exists(root)) return 0;

        var index = EmbedIndex.ForRoot(root);
        if (index.Count == 0 && !index.Load()) return 0;

        var engine = LocalEmbeddingEngine.Shared;
        if (!engine.IsAvailable || !engine.EnsureReady()) return 0;

        var relN = rel.Replace('\\', '/');
        var changed = index.RemoveFile(relN);

        foreach (var item in EmbedCorpus.CollectFile(root, relN))
        {
            var vec = engine.Embed(item.Text);
            if (vec == null) continue;
            if (index.Upsert(item.Key, item.Text, vec)) changed++;
        }
        return changed;   // 脏数据由 FlushPending 批量落盘
    }

    /// <summary>删除某文件相关的全部向量条目（文件被删除时调用；标脏不落盘）</summary>
    public static void RemoveFileKeys(string root, string rel)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(rel)) return;
        if (!Directory.Exists(root)) return;
        EmbedIndex.ForRoot(root).RemoveFile(rel.Replace('\\', '/'));
    }

    /// <summary>把各共享实例的脏数据统一落盘（批量文件处理收尾调用一次，代替每文件写一次全量索引）</summary>
    public static int FlushPending() => EmbedIndex.FlushAll();

    /// <summary>检查索引是否需要重建（旧格式待迁移不算需要重建；已建但为空才算）</summary>
    public static bool NeedsRebuild(AppConfig cfg)
    {
        var root = cfg.ProjectRoot;
        if (root.Length == 0 || !Directory.Exists(root)) return false;
        var dir = Path.Combine(root, ".gairr");
        var hasBin = File.Exists(Path.Combine(dir, EmbedIndex.FileNameBin));
        var hasJson = File.Exists(Path.Combine(dir, EmbedIndex.FileName));
        if (!hasBin && !hasJson) return false;   // 从未建库：交由 MapAuto 首次构建
        return EmbedIndex.ForRoot(root).Count == 0;
    }
}
