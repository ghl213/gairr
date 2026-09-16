// StatsStore.cs — GAIRR.StatServer 统计存储：data/stats.json（devices 主档 + usage 按设备×日期秒数）
// 口径与原 SQLite 版 /api/report 完全一致：
//   usage  ：同键 (deviceId+date) 以最新上报秒数覆盖（上报值=该日最终累计，天然幂等）；
//   devices：report_cnt / total_seconds 跨上报累加；first_seen / first_ip 仅首报写入；
//            last_seen / last_ip 每次上报更新；os / app_version 仅当上报值非空才覆盖。
// 落盘：每次上报后先写 stats.json.tmp 再 rename 覆盖 stats.json（原子，崩溃不留半截文件）；
// 加载：文件缺失 / 解析失败 / 结构非法 → 从零重建（打警告，不阻断启动）；
// 并发：全部读写操作共用一把锁（_gate）。
// JSON 解析/序列化用本工程 MiniJson（零第三方依赖）。

using System.Text.RegularExpressions;

namespace GAIRR.StatServer;

/// <summary>devices 主档一行（字段与 stats.json 存储键名对应，camelCase）。</summary>
public sealed class DeviceRow
{
    public string Os = "";
    public string AppVersion = "";
    public string FirstIp = "";
    public string LastIp = "";
    public string FirstSeen = "";
    public string LastSeen = "";
    public long ReportCnt;
    public long TotalSeconds;
}

/// <summary>stats.json 读写存储（线程安全，语义见文件头）。</summary>
public sealed class StatsStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Dictionary<string, DeviceRow> _devices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, long>> _usage = new(StringComparer.Ordinal);

    // 日期格式与原版一致：yyyy-MM-dd
    private static readonly Regex DatePattern = new(@"^\d{4}-\d{2}-\d{2}$");

    public StatsStore(string path) => _path = path;

    /// <summary>加载 stats.json：文件不存在→空存储；解析失败/结构非法→从零重建（控制台告警）。</summary>
    public void Load()
    {
        lock (_gate)
        {
            _devices.Clear();
            _usage.Clear();
            try
            {
                if (!File.Exists(_path)) return;
                var root = MiniJson.Parse(File.ReadAllText(_path)) as Dictionary<string, object?>
                    ?? throw new JsonParseException("stats.json 顶层须为对象", 0);
                if (root.TryGetValue("devices", out var dv)) ParseDevices(dv);
                if (root.TryGetValue("usage", out var uv)) ParseUsage(uv);
            }
            catch (Exception ex) when (ex is JsonParseException or IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"[StatsStore] stats.json 读取失败，从零重建：{ex.Message}");
                _devices.Clear();
                _usage.Clear();
            }
        }
    }

    /// <summary>
    /// 处理一次上报（语义同原版 /api/report）：
    /// 非法日行（日期空/秒数负/日期格式不符）跳过不整体失败；usage 同键覆盖；devices 累加。
    /// 处理完原子落盘（tmp+rename），返回有效日行数。deviceId 须由调用方预校验后传入。
    /// </summary>
    public int Report(string deviceId, string os, string appVersion, string ip, string now,
        IEnumerable<(string Date, long Seconds)> days)
    {
        lock (_gate)
        {
            long totalAdd = 0;
            var valid = 0;
            foreach (var (date, seconds) in days)
            {
                if (string.IsNullOrWhiteSpace(date) || seconds < 0) continue;
                if (!DatePattern.IsMatch(date)) continue;
                totalAdd += seconds;
                valid++;
                if (!_usage.TryGetValue(deviceId, out var row))
                    _usage[deviceId] = row = new Dictionary<string, long>(StringComparer.Ordinal);
                row[date] = seconds;   // 同键覆盖
            }

            if (_devices.TryGetValue(deviceId, out var dev))
            {
                if (os.Length > 0) dev.Os = os;
                if (appVersion.Length > 0) dev.AppVersion = appVersion;
                dev.LastIp = ip;
                dev.LastSeen = now;
                dev.ReportCnt += 1;
                dev.TotalSeconds += totalAdd;
            }
            else
            {
                _devices[deviceId] = new DeviceRow
                {
                    Os = os, AppVersion = appVersion,
                    FirstIp = ip, LastIp = ip,
                    FirstSeen = now, LastSeen = now,
                    ReportCnt = 1, TotalSeconds = totalAdd,
                };
            }

            SaveLocked();
            return valid;
        }
    }

    /// <summary>构造 /api/overview 汇总（同原版 SQL：设备总数/总时长、按日最近 30 天、最近活跃 top 50）。</summary>
    public object? Overview()
    {
        lock (_gate)
        {
            long totalSeconds = 0;
            foreach (var d in _devices.Values) totalSeconds += d.TotalSeconds;

            var dailyAgg = new Dictionary<string, long[]>();
            foreach (var days in _usage.Values)
                foreach (var (date, secs) in days)
                {
                    if (!dailyAgg.TryGetValue(date, out var agg)) dailyAgg[date] = agg = new long[2];
                    agg[0]++;
                    agg[1] += secs;
                }
            var daily = new List<object?>();
            foreach (var date in dailyAgg.Keys.OrderByDescending(x => x).Take(30))
            {
                var agg = dailyAgg[date];
                daily.Add(new Dictionary<string, object?>
                {
                    ["date"] = date,
                    ["devices"] = (double)agg[0],
                    ["seconds"] = (double)agg[1],
                });
            }

            var recent = new List<object?>();
            foreach (var (id, d) in _devices
                .OrderByDescending(kv => kv.Value.LastSeen, StringComparer.Ordinal).Take(50))
                recent.Add(new Dictionary<string, object?>
                {
                    ["deviceId"] = id,
                    ["os"] = d.Os,
                    ["appVersion"] = d.AppVersion,
                    ["lastIp"] = d.LastIp,
                    ["firstSeen"] = d.FirstSeen,
                    ["lastSeen"] = d.LastSeen,
                    ["reportCnt"] = (double)d.ReportCnt,
                    ["totalSeconds"] = (double)d.TotalSeconds,
                });

            return new Dictionary<string, object?>
            {
                ["totalDevices"] = (double)_devices.Count,
                ["totalSeconds"] = totalSeconds,
                ["daily"] = daily,
                ["recent"] = recent,
            };
        }
    }

    // ─────────────────────────── 内部 ───────────────────────────

    /// <summary>序列化当前状态：先写 stats.json.tmp，再 rename 原子覆盖 stats.json。</summary>
    private void SaveLocked()
    {
        var doc = new Dictionary<string, object?>
        {
            ["devices"] = BuildDevices(),
            ["usage"] = BuildUsage(),
        };
        var json = MiniJson.Serialize(doc, pretty: true);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json, new System.Text.UTF8Encoding(false));
        File.Move(tmp, _path, overwrite: true);
    }

    private object? BuildDevices()
    {
        var obj = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (id, d) in _devices)
            obj[id] = new Dictionary<string, object?>
            {
                ["os"] = d.Os,
                ["appVersion"] = d.AppVersion,
                ["firstIp"] = d.FirstIp,
                ["lastIp"] = d.LastIp,
                ["firstSeen"] = d.FirstSeen,
                ["lastSeen"] = d.LastSeen,
                ["reportCnt"] = (double)d.ReportCnt,
                ["totalSeconds"] = (double)d.TotalSeconds,
            };
        return obj;
    }

    private object? BuildUsage()
    {
        var obj = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (id, days) in _usage)
        {
            var dayObj = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (date, secs) in days) dayObj[date] = secs;
            obj[id] = dayObj;
        }
        return obj;
    }

    private void ParseDevices(object? v)
    {
        var map = v as Dictionary<string, object?> ?? throw new JsonParseException("devices 应为对象", 0);
        foreach (var (id, rowV) in map)
        {
            if (string.IsNullOrEmpty(id)) throw new JsonParseException("devices 含空键", 0);
            var row = rowV as Dictionary<string, object?>
                ?? throw new JsonParseException($"devices[{id}] 应为对象", 0);
            _devices[id] = new DeviceRow
            {
                Os = Str(row, "os"),
                AppVersion = Str(row, "appVersion"),
                FirstIp = Str(row, "firstIp"),
                LastIp = Str(row, "lastIp"),
                FirstSeen = Str(row, "firstSeen"),
                LastSeen = Str(row, "lastSeen"),
                ReportCnt = (long)MiniJson.AsNumber(Get(row, "reportCnt")),
                TotalSeconds = (long)MiniJson.AsNumber(Get(row, "totalSeconds")),
            };
        }
    }

    private void ParseUsage(object? v)
    {
        var map = v as Dictionary<string, object?> ?? throw new JsonParseException("usage 应为对象", 0);
        foreach (var (id, daysV) in map)
        {
            if (string.IsNullOrEmpty(id)) throw new JsonParseException("usage 含空键", 0);
            var days = daysV as Dictionary<string, object?>
                ?? throw new JsonParseException($"usage[{id}] 应为对象", 0);
            var row = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var (date, secV) in days)
                row[date] = (long)MiniJson.AsNumber(secV);
            _usage[id] = row;
        }
    }

    private static object? Get(Dictionary<string, object?> row, string key)
        => row.TryGetValue(key, out var v) ? v : null;

    private static string Str(Dictionary<string, object?> row, string key)
        => MiniJson.AsString(Get(row, key)) ?? "";
}
