using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using GAIRR.Core;

namespace GAIRR;

/// <summary>
/// 匿名使用统计（GAIRR 桌面端）：
/// - 首次运行生成匿名设备 ID（data/device_id，不采集任何个人标识）
/// - 按天累计前台可见使用秒数，落盘 data/usage_daily.json
/// - 每次启动把"今天之前"已封存的日数据上报统计服务器（gairr-stats-server），
///   同一请求顺带做版本检查；网络失败不阻塞启动，数据留到下次启动再补报。
/// 服务器地址与开关在 config.ini [Stats] 节配置。
/// </summary>
public static class UsageTracker
{
    static readonly string DeviceIdFile = Path.Combine(Paths.DataDir, "device_id");
    static readonly string StoreFile = Path.Combine(Paths.DataDir, "usage_daily.json");

    static DispatcherTimer? timer;
    static MainWindow? window;
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(6) };
    static bool updatePrompted;
    static DateTime lastTick = DateTime.Now;
    static string todayKey = DateTime.Now.ToString("yyyy-MM-dd");
    static long todaySeconds;                 // 今日已累计秒数（含最近一次 flush）
    static Dictionary<string, long> days = new();   // 全部历史日累计：date(yyyy-MM-dd) -> 秒
    static string? deviceId;

    /// <summary>在 GUI 主窗口显示后调用；CLI 模式不启动。</summary>
    public static void Start(MainWindow mw)
    {
        window = mw;
        Load();
        lastTick = DateTime.Now;
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        timer.Tick += (_, _) => Tick();
        timer.Start();
        Application.Current.Exit += (_, _) => FlushNow();   // 退出兜底落盘
        _ = ReportAndCheckAsync();                           // 后台上报+版本检查，失败静默
    }

    /// <summary>本地累计逻辑：窗口可见且非最小化才算"正在使用"，按真实流逝秒数累计。</summary>
    static void Tick()
    {
        var now = DateTime.Now;
        var elapsed = (now - lastTick).TotalSeconds;
        lastTick = now;
        if (window == null) return;

        var visible = window.IsVisible && window.WindowState != WindowState.Minimized;
        if (!visible) return;

        var key = now.ToString("yyyy-MM-dd");
        if (key != todayKey)
        {
            // 跨天：今日归零，旧日条目在 days 中保留，等下次启动上报
            todayKey = key;
            todaySeconds = 0;
        }
        todaySeconds += (long)Math.Round(elapsed);
        FlushNow();
    }

    static void Load()
    {
        try
        {
            if (File.Exists(StoreFile))
            {
                var doc = JsonSerializer.Deserialize<UsageStore>(File.ReadAllText(StoreFile));
                if (doc?.Days != null)
                    foreach (var kv in doc.Days) days[kv.Key] = kv.Value;
            }
        }
        catch { /* 损坏则忽略，从零开始 */ }

        todaySeconds = days.TryGetValue(todayKey, out var s) ? s : 0;
        if (File.Exists(DeviceIdFile)) deviceId = File.ReadAllText(DeviceIdFile).Trim();
        if (string.IsNullOrEmpty(deviceId))
        {
            deviceId = Guid.NewGuid().ToString("N");
            try { File.WriteAllText(DeviceIdFile, deviceId); } catch { /* 写失败不影响运行 */ }
        }
    }

    static void FlushNow()
    {
        try
        {
            days[todayKey] = todaySeconds;
            var json = JsonSerializer.Serialize(new UsageStore { Days = days });
            File.WriteAllText(StoreFile, json);
        }
        catch { /* 本地写盘失败静默 */ }
    }

    /// <summary>启动上报：携带今天之前全部封存日，成功后移除（服务端按 device+date 覆盖，天然幂等）；并解析版本回执。</summary>
    static async Task ReportAndCheckAsync()
    {
        try
        {
            var cfg = new AppConfig();
            if (cfg.GetInt("Stats", "Enabled", 1) == 0) return;
            var serverUrl = cfg.Get("Stats", "ServerUrl", "").Trim().TrimEnd('/');
            if (string.IsNullOrEmpty(serverUrl)) return;

            // 组装历史日（严格早于今天的才算封存数据）
            var payloadDays = new List<DayPayload>();
            foreach (var kv in days)
                if (string.CompareOrdinal(kv.Key, todayKey) < 0 && kv.Value > 0)
                    payloadDays.Add(new DayPayload { date = kv.Key, seconds = kv.Value });

            var version = typeof(UsageTracker).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            var req = new ReportPayload
            {
                deviceId = deviceId ?? "",
                os = Environment.OSVersion.VersionString,
                appVersion = version,
                days = payloadDays,
            };
            var json = JsonSerializer.Serialize(req);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync(serverUrl + "/api/report", content);
            if (!resp.IsSuccessStatusCode) return;   // 失败保留本地，下次再报
            var body = await resp.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<ReportResult>(body);

            // 上报成功 → 从本地移除已封存历史日（幂等保障）
            if (result != null && result.ok)
            {
                var changed = false;
                foreach (var kv in payloadDays) { if (days.Remove(kv.date)) changed = true; }
                if (changed) FlushNow();

                // 版本检查回执：有新版且提供更新地址 → 延迟弹出一次提示（启动不阻塞）
                if (!updatePrompted && result.hasUpdate
                    && !string.IsNullOrEmpty(result.latest)
                    && !string.IsNullOrEmpty(result.updateUrl))
                {
                    updatePrompted = true;
                    await Task.Delay(1500);
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        var note = string.IsNullOrWhiteSpace(result.note) ? "" : "\n\n更新说明：" + result.note.Trim();
                        MessageBox.Show(
                            $"发现新版本 v{result.latest}！{note}\n\n下载地址：\n{result.updateUrl}",
                            "GAIRR 更新可用", MessageBoxButton.OK, MessageBoxImage.Information);
                    });
                }
            }
        }
        catch (Exception ex)
        {
            // 网络失败/服务器未部署：绝不阻塞启动，已累计数据留在本地等下次补报
            try { File.AppendAllText(Paths.AgentLog, $"[usage] 上报失败(已忽略)：{ex.Message}\n"); } catch { }
        }
    }

    sealed class UsageStore { public Dictionary<string, long> Days { get; set; } = new(); }
    sealed class DayPayload { public string date { get; set; } = ""; public long seconds { get; set; } }
    sealed class ReportPayload { public string deviceId { get; set; } = ""; public string os { get; set; } = ""; public string appVersion { get; set; } = ""; public List<DayPayload> days { get; set; } = new(); }
    sealed class ReportResult { public bool ok { get; set; } public string latest { get; set; } = ""; public string updateUrl { get; set; } = ""; public string note { get; set; } = ""; public bool hasUpdate { get; set; } }
}
