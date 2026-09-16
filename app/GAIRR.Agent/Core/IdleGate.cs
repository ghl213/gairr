/// <summary>用户活跃判定（钉钉按需延迟通知）：记录 UI 最后一次鼠标移动/点击/按键时刻，
/// 任务结束/报错/危险窗事件后等待一段时间（默认 3 分钟），期间用户仍有操作则视为"人还盯着"跳过钉钉，
/// 期间无任何操作（人走开了）才发送，减少无用消息打扰。CLI/服务宿主模式无 UI 概念，不 Enable 时立即发送。</summary>
using System.Threading;

namespace GAIRR.Core;

/// <summary>空闲门控：UI 事件打点 + "等待 N 分钟无操作"判定</summary>
public static class IdleGate
{
    static long _lastActiveTicks = Environment.TickCount64;
    static int _enabled;   // 0=未启用（CLI/服务宿主，通知立即发） 1=已启用（UI 模式，按无操作判定）

    /// <summary>静默判定阈值：默认 3 分钟无操作视为"人已离开"</summary>
    public static readonly TimeSpan SilentThreshold = TimeSpan.FromMinutes(3);

    /// <summary>是否已启用空闲判定（UI 窗口启动时 Enable 一次；false=CLI/服务宿主，通知立即发送）</summary>
    public static bool Enabled => Volatile.Read(ref _enabled) == 1;

    /// <summary>距最后一次用户操作已静默的毫秒数（未启用时同样有效，仅记录打点）</summary>
    public static long IdleMs => Environment.TickCount64 - Volatile.Read(ref _lastActiveTicks);

    /// <summary>启用空闲判定（UI 窗口启动时调用一次）；未启用时通知立即发送</summary>
    public static void Enable() => Interlocked.Exchange(ref _enabled, 1);

    /// <summary>记录一次用户操作（鼠标移动/点击/键盘按键）：由 UI 事件处理器调用，仅一次原子写，开销可忽略</summary>
    public static void MarkActive() => Interlocked.Exchange(ref _lastActiveTicks, Environment.TickCount64);

    /// <summary>等待阈值时长，等待期间（自调用时刻起）无任何操作返回 true；未启用时直接 true（立即通知）</summary>
    public static async Task<bool> WaitSilentAsync(TimeSpan? threshold = null)
    {
        if (_enabled == 0) return true;
        var t = threshold ?? SilentThreshold;
        await Task.Delay(t);
        // 等待结束时距最后操作仍 >= 阈值，等价于"等待期间无任何操作"
        return Environment.TickCount64 - Volatile.Read(ref _lastActiveTicks) >= t.Ticks;
    }
}
