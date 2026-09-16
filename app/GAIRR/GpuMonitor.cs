using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace GAIRR;

/// <summary>本地模型 GPU 资源监控（标题栏固定三色柱）：每 15 秒拉取 config.ini [Local] MonitorUrl，
/// 解析响应 gpu 数组第一张卡（memoryPercent/util/temp），驱动标题栏 显存/利用率/温度 三色柱（绿→琥珀→红 阈值色）。
/// 轮询在后台线程执行，失败保留末次值并标记离线（柱子变灰）；不弹窗、不影响 Agent 主流程。</summary>
public sealed class GpuMonitor : INotifyPropertyChanged
{
    /// <summary>全局单例：标题栏 XAML 经 x:Static local:GpuMonitor.Current 绑定</summary>
    public static readonly GpuMonitor Current = new();

    /// <summary>刷新间隔（毫秒）：15 秒</summary>
    public const int IntervalMs = 15_000;

    static readonly Brush Green = Freeze("#57D9A3");   // 正常
    static readonly Brush Amber = Freeze("#F5A623");   // 偏高
    static readonly Brush Red = Freeze("#F87171");     // 危险
    static readonly Brush Gray = Freeze("#4A5568");    // 离线/未知

    // 超时 20 秒：监控端点（system.php）实测响应 5~12 秒，超时过短会频繁误判离线
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    readonly System.Timers.Timer _timer = new(IntervalMs) { AutoReset = true };
    string _url = "";
    bool _busy;
    double _vram = -1, _util = -1, _temp = -1;
    bool _online;
    string _vramText = "--", _utilText = "--", _tempText = "--", _tip = "";

    public GpuMonitor() => _timer.Elapsed += async (_, _) => await PollAsync();

    public event PropertyChangedEventHandler? PropertyChanged;
    void Notify(params string[] names)
    {
        var ev = PropertyChanged;
        if (ev == null) return;
        foreach (var n in names) ev(this, new PropertyChangedEventArgs(n));
    }

    /// <summary>显存占用 %（色柱数值，未知时 0）</summary>
    public double Vram => _vram < 0 ? 0 : Math.Min(_vram, 100);
    /// <summary>GPU 利用率 %（色柱数值，未知时 0）</summary>
    public double Util => _util < 0 ? 0 : Math.Min(_util, 100);
    /// <summary>GPU 温度 °C（色柱数值 0-100，未知时 0）</summary>
    public double Temp => _temp < 0 ? 0 : Math.Min(_temp, 100);

    public string VramText => _vramText;
    public string UtilText => _utilText;
    public string TempText => _tempText;
    /// <summary>面板悬停提示（第一卡明细：显存/利用率/温度/风扇/功耗；离线时显示离线文字）</summary>
    public string Tip => _tip;

    /// <summary>显存柱色：&lt;70 绿 / 70~90 琥珀 / ≥90 红；离线灰</summary>
    public Brush VramBrush => _online ? BarBrush(_vram, 70, 90) : Gray;
    /// <summary>利用率柱色：&lt;60 绿 / 60~85 琥珀 / ≥85 红；离线灰</summary>
    public Brush UtilBrush => _online ? BarBrush(_util, 60, 85) : Gray;
    /// <summary>温度柱色：&lt;65 绿 / 65~80 琥珀 / ≥80 红；离线灰</summary>
    public Brush TempBrush => _online ? BarBrush(_temp, 65, 80) : Gray;

    /// <summary>启动（重启）轮询；url 为空时仅停止轮询</summary>
    public void Start(string url)
    {
        _timer.Stop();
        _url = url ?? "";
        if (_url.Length == 0) return;
        _timer.Start();
        _ = PollAsync();
    }

    /// <summary>停止轮询（切走本地模型或窗口关闭时）</summary>
    public void Stop() => _timer.Stop();

    /// <summary>单次拉取：成功解析 gpu[0] 并切 UI 线程刷新；失败标记离线、保留末次值（下个周期自动重试）</summary>
    async Task PollAsync()
    {
        if (_busy || _url.Length == 0) return;
        _busy = true;
        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(_url));
            var gpu = doc.RootElement.GetProperty("gpu")[0];
            var vram = Num(gpu, "memoryPercent");
            var util = Num(gpu, "util");
            var temp = Num(gpu, "temp");
            var tip = BuildTip(gpu, vram, util, temp);
            var disp = Application.Current?.Dispatcher;
            if (disp != null) await disp.InvokeAsync(() => Apply(vram, util, temp, tip));
        }
        catch
        {
            var disp = Application.Current?.Dispatcher;
            if (disp != null) await disp.InvokeAsync(MarkOffline);
        }
        finally { _busy = false; }
    }

    /// <summary>写入新值并通知（仅 UI 线程调用）</summary>
    void Apply(double? vram, double? util, double? temp, string tip)
    {
        _vram = vram ?? -1; _util = util ?? -1; _temp = temp ?? -1;
        _online = true;
        // 面板已简化为"标签+色柱"，右侧数值文本不再显示；这三个 Text 属性保留供其它界面/调试取用
        _vramText = vram is >= 0 ? $"{vram:F1}" : "--";
        _utilText = util is >= 0 ? $"{util:0}" : "--";
        _tempText = temp is >= 0 ? $"{temp:0}°C" : "--";
        _tip = tip;
        Notify(nameof(Vram), nameof(Util), nameof(Temp),
            nameof(VramText), nameof(UtilText), nameof(TempText), nameof(Tip),
            nameof(VramBrush), nameof(UtilBrush), nameof(TempBrush));
    }

    /// <summary>标记离线并保留末次值（仅 UI 线程调用）</summary>
    void MarkOffline()
    {
        if (_online || _tip.Length == 0)
        {
            _online = false;
            _tip = "监控离线（保留末次值）";
            Notify(nameof(VramBrush), nameof(UtilBrush), nameof(TempBrush), nameof(Tip));
        }
    }

    /// <summary>安全读取数值字段（数字或数字字符串均可）；缺失/解析失败返回 null</summary>
    static double? Num(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String => double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : (double?)null,
            _ => (double?)null,
        };
    }

    /// <summary>拼第一卡明细提示（显存/利用率/温度/容量/风扇/功耗），供面板悬停查看</summary>
    static string BuildTip(JsonElement gpu, double? vram, double? util, double? temp)
    {
        var parts = new List<string>();
        if (vram is >= 0) parts.Add($"显存 {vram:F1}%");
        if (util is >= 0) parts.Add($"利用率 {util:0}%");
        if (temp is >= 0) parts.Add($"温度 {temp:0}°C");
        if (gpu.TryGetProperty("fanSpeed", out var f) && f.ValueKind == JsonValueKind.Number)
            parts.Add($"风扇 {f.GetDouble():0}%");
        if (gpu.TryGetProperty("powerDraw", out var p)) parts.Add($"功耗 {p.GetString()}");
        return string.Join(" · ", parts);
    }

    static Brush BarBrush(double? v, double warn, double danger) =>
        v is < 0 ? Gray : v >= danger ? Red : v >= warn ? Amber : Green;

    static Brush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
