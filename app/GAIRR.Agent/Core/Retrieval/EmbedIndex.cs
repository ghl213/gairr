using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace GAIRR.Core;

/// <summary>向量索引：紧凑二进制存储（.gairr/embed-index.bin）+ 进程级共享常驻 + Key 字典 O(1) 增删查。
/// 旧版 embed-index.json 用 JSON DOM 解析（18MB 需数十秒、堆内存数倍膨胀），首次访问时以 Utf8JsonReader
/// 流式迁移为 bin，之后读写均走二进制（顺序读 7MB 亚秒级）。写侧只标脏，由调用方批末统一 SaveIfDirty 落盘；
/// 查询侧与写侧共用同一实例直读内存，落盘不再触发重载。</summary>
public sealed class EmbedIndex
{
    public const string FileName = "embed-index.json";      // 旧格式：仅用于路径兼容与一次性迁移
    public const string FileNameBin = "embed-index.bin";    // 现行格式：紧凑二进制
    public const int ModelVersion = 512;                    // bge-small-zh-v1.5 向量维度
    const string Magic = "GAIREMB1";                        // 文件头魔数（8 字节 ASCII）

    /// <summary>索引条目：Key 唯一、Text 用于重建向量、Vec 为 L2 归一化向量</summary>
    public sealed class Entry
    {
        public string Key = "";
        public string Text = "";
        public float[] Vec = Array.Empty<float>();
    }

    /// <summary>进程级共享池：按 bin 绝对路径复用同一实例（写侧与查询侧共享内存，避免反复 Load）</summary>
    static readonly ConcurrentDictionary<string, EmbedIndex> Pool = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>取共享实例（path 传 .json 或 .bin 均可，内部归一到 bin）；首次自动加载/迁移磁盘数据</summary>
    public static EmbedIndex For(string path)
    {
        var bin = BinPathOf(path);
        return Pool.GetOrAdd(bin, p =>
        {
            var idx = new EmbedIndex(p);
            idx.Load();
            return idx;
        });
    }

    /// <summary>按项目根取共享实例</summary>
    public static EmbedIndex ForRoot(string root) => For(Path.Combine(root, ".gairr", FileNameBin));

    /// <summary>所有共享实例落盘脏数据（批量写后调用一次，代替每文件一次全量 Save）；返回落盘实例数</summary>
    public static int FlushAll()
    {
        int n = 0;
        foreach (var idx in Pool.Values) if (idx.SaveIfDirty()) n++;
        return n;
    }

    /// <summary>把任意 embed-index 路径归一为同目录下的 bin 路径</summary>
    static string BinPathOf(string path) =>
        Path.Combine(Path.GetDirectoryName(path) ?? "", FileNameBin);

    readonly string _binPath;
    readonly string _jsonPath;
    readonly object _sync = new();
    readonly List<Entry> _entries = new();
    readonly Dictionary<string, Entry> _byKey = new(StringComparer.Ordinal);
    bool _dirty;
    DateTime _onDiskUtc = DateTime.MinValue;   // 本实例认知的磁盘状态时间（用于识别外部进程改写）

    /// <summary>创建索引；path 可为 .gairr/embed-index.bin 或旧版 .json（内部归一到 bin）</summary>
    public EmbedIndex(string path)
    {
        _binPath = BinPathOf(path);
        _jsonPath = Path.Combine(Path.GetDirectoryName(_binPath) ?? "", FileName);
    }

    /// <summary>加载索引：bin 优先；无 bin 但有旧 json 则流式迁移并立即落盘为 bin。无数据返回 false</summary>
    public bool Load()
    {
        lock (_sync)
        {
            _entries.Clear();
            _byKey.Clear();
            _dirty = false;
            _onDiskUtc = DateTime.MinValue;
            try
            {
                if (File.Exists(_binPath) && LoadBin())
                {
                    _onDiskUtc = File.GetLastWriteTimeUtc(_binPath);
                    return _entries.Count > 0;
                }
                if (File.Exists(_jsonPath) && MigrateJson())
                {
                    Save();   // 迁移后立刻写 bin，后续访问不再付迁移成本
                    return _entries.Count > 0;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>二进制顺序读：magic(8) + dim(4) + count(4) + count × [keyLen|key|textLen|text|vec]</summary>
    bool LoadBin()
    {
        var bytes = File.ReadAllBytes(_binPath);
        if (bytes.Length < 16) return false;
        if (Encoding.ASCII.GetString(bytes, 0, 8) != Magic) return false;
        var dim = BitConverter.ToInt32(bytes, 8);
        if (dim != ModelVersion) return false;
        var count = BitConverter.ToInt32(bytes, 12);
        int p = 16, vecBytes = dim * 4;
        for (int i = 0; i < count && p + 8 <= bytes.Length; i++)
        {
            var klen = BitConverter.ToInt32(bytes, p); p += 4;
            if (klen < 0 || p + klen + 4 > bytes.Length) break;
            var key = Encoding.UTF8.GetString(bytes, p, klen); p += klen;
            var tlen = BitConverter.ToInt32(bytes, p); p += 4;
            if (tlen < 0 || p + tlen + vecBytes > bytes.Length) break;
            var text = Encoding.UTF8.GetString(bytes, p, tlen); p += tlen;
            var vec = new float[dim];
            Buffer.BlockCopy(bytes, p, vec, 0, vecBytes); p += vecBytes;
            Add(new Entry { Key = key, Text = text, Vec = vec });
        }
        return true;
    }

    /// <summary>一次性迁移：Utf8JsonReader 流式解析旧 json（不建 DOM），只认 dim/key/text/vec 四类字段</summary>
    bool MigrateJson()
    {
        try
        {
            var bytes = File.ReadAllBytes(_jsonPath);
            var reader = new Utf8JsonReader(bytes);
            string lastProp = "", key = "", text = "";
            var vec = new List<float>(ModelVersion);
            int dim = 0;
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName:
                        lastProp = reader.GetString() ?? "";
                        break;
                    case JsonTokenType.Number:
                        if (lastProp == "vec") vec.Add(reader.GetSingle());
                        else if (lastProp == "dim") dim = reader.GetInt32();
                        break;
                    case JsonTokenType.String:
                        if (lastProp == "key") key = reader.GetString() ?? "";
                        else if (lastProp == "text") text = reader.GetString() ?? "";
                        break;
                    case JsonTokenType.EndObject:
                        if (key.Length > 0 && vec.Count == ModelVersion)
                            Add(new Entry { Key = key, Text = text, Vec = vec.ToArray() });
                        key = ""; text = ""; vec.Clear();
                        break;
                }
            }
            // 维度不符说明是别的模型产物：整批丢弃，交由上层全量重建
            if (dim != 0 && dim != ModelVersion) { _entries.Clear(); _byKey.Clear(); return false; }
            return _entries.Count > 0;
        }
        catch
        {
            _entries.Clear();
            _byKey.Clear();
            return false;
        }
    }

    /// <summary>保存到磁盘（紧凑二进制，先写临时文件再原子替换）；返回是否成功</summary>
    public bool Save()
    {
        lock (_sync)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_binPath) ?? "");
                using var ms = new MemoryStream();
                using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
                {
                    w.Write(Encoding.ASCII.GetBytes(Magic));
                    w.Write(ModelVersion);
                    w.Write(_entries.Count);
                    foreach (var e in _entries)
                    {
                        var kb = Encoding.UTF8.GetBytes(e.Key);
                        var tb = Encoding.UTF8.GetBytes(e.Text);
                        w.Write(kb.Length); w.Write(kb);
                        w.Write(tb.Length); w.Write(tb);
                        w.Write(MemoryMarshal.AsBytes(e.Vec.AsSpan()));
                    }
                    w.Flush();
                }
                var tmp = _binPath + ".tmp";
                File.WriteAllBytes(tmp, ms.ToArray());
                File.Move(tmp, _binPath, true);
                _dirty = false;
                _onDiskUtc = File.GetLastWriteTimeUtc(_binPath);
                return true;
            }
            catch
            {
                return false;   // 保存失败不阻断，内存索引仍可用，下次再试
            }
        }
    }

    /// <summary>有脏数据才落盘（批量写收尾调用）；返回是否真的写了盘</summary>
    public bool SaveIfDirty()
    {
        lock (_sync) if (!_dirty) return false;
        return Save();
    }

    /// <summary>查询侧保鲜：仅当其他进程改写了磁盘文件才重载；本进程有未落盘改动时不覆盖</summary>
    public bool ReloadIfChangedExternally()
    {
        try
        {
            if (!File.Exists(_binPath)) return false;
            var m = File.GetLastWriteTimeUtc(_binPath);
            lock (_sync)
            {
                if (_dirty || _onDiskUtc == m) return false;
            }
            return Load();
        }
        catch { return false; }
    }

    /// <summary>替换指定 Key 的条目（文本+向量）；Key 不存在则新增；返回是否变更（变更即标脏）</summary>
    public bool Upsert(string key, string text, float[] vec)
    {
        lock (_sync)
        {
            if (_byKey.TryGetValue(key, out var ex))
            {
                if (ex.Text == text && ex.Vec.AsSpan().SequenceEqual(vec)) return false;
                ex.Text = text;
                ex.Vec = vec;
                _dirty = true;
                return true;
            }
            Add(new Entry { Key = key, Text = text, Vec = vec });
            _dirty = true;
            return true;
        }
    }

    /// <summary>删除指定 Key 的条目；返回是否删除</summary>
    public bool Remove(string key)
    {
        lock (_sync)
        {
            if (!_byKey.TryGetValue(key, out var e)) return false;
            _byKey.Remove(key);
            _entries.Remove(e);
            _dirty = true;
            return true;
        }
    }

    /// <summary>批量删除：一次 RemoveAll 重建列表，避免逐条 O(n) 查找退化成 O(n²)</summary>
    public int RemoveMany(IEnumerable<string> keys)
    {
        lock (_sync)
        {
            var set = keys as HashSet<string> ?? keys.ToHashSet(StringComparer.Ordinal);
            if (set.Count == 0) return 0;
            var before = _entries.Count;
            _entries.RemoveAll(e => set.Contains(e.Key));
            foreach (var k in set) _byKey.Remove(k);
            var removed = before - _entries.Count;
            if (removed > 0) _dirty = true;
            return removed;
        }
    }

    /// <summary>删除某文件相关的全部条目（symbol:{rel}:* 前缀 + summary:{rel} / summary:{rel}:m）；返回删除条数</summary>
    public int RemoveFile(string rel)
    {
        if (string.IsNullOrWhiteSpace(rel)) return 0;
        var relN = rel.Replace('\\', '/');
        lock (_sync)
        {
            var prefix = "symbol:" + relN + ":";
            var s1 = "summary:" + relN;
            var s2 = s1 + ":m";
            var ks = _byKey.Keys.Where(k =>
                k.StartsWith(prefix, StringComparison.Ordinal) || k == s1 || k == s2).ToList();
            return ks.Count == 0 ? 0 : RemoveMany(ks);
        }
    }

    /// <summary>清空索引（重建前调用）</summary>
    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
            _byKey.Clear();
            _dirty = true;
        }
    }

    /// <summary>当前条目数</summary>
    public int Count
    {
        get { lock (_sync) return _entries.Count; }
    }

    /// <summary>余弦 TopK 查询：全量点积 + 长度为 topK 的有序小数组选择（不排序全表、不额外分配）</summary>
    public List<(string Key, float Cos)> Search(float[] query, int topK = 10)
    {
        lock (_sync)
        {
            var res = new List<(string Key, float Cos)>(Math.Max(topK, 0));
            if (topK <= 0 || query.Length != ModelVersion || _entries.Count == 0) return res;
            foreach (var e in _entries)
            {
                var v = e.Vec;
                if (v.Length != query.Length) continue;
                float cos = 0f;
                for (int i = 0; i < v.Length; i++) cos += query[i] * v[i];
                if (res.Count < topK)
                {
                    res.Add((e.Key, cos));
                    res.Sort((a, b) => b.Cos.CompareTo(a.Cos));
                }
                else if (cos > res[topK - 1].Cos)
                {
                    res[topK - 1] = (e.Key, cos);
                    res.Sort((a, b) => b.Cos.CompareTo(a.Cos));
                }
            }
            return res;
        }
    }

    /// <summary>获取指定 Key 的条目（供增量判断）</summary>
    public Entry? Get(string key)
    {
        lock (_sync) return _byKey.TryGetValue(key, out var e) ? e : null;
    }

    /// <summary>遍历所有条目（用于重建/导出）</summary>
    public IEnumerable<Entry> All()
    {
        lock (_sync) return _entries.ToList();
    }

    /// <summary>内部登记：Key 重复或为空则忽略，List 保序 + 字典索引同步维护</summary>
    void Add(Entry e)
    {
        if (e.Key.Length == 0 || _byKey.ContainsKey(e.Key)) return;
        _entries.Add(e);
        _byKey[e.Key] = e;
    }
}
