/// <summary>主机资源负载监视器：后台每 1 秒采样本机 CPU / 内存占用率并缓存，
/// 供"任务启动前资源闸"（普通/自动/编排任务 CPU 或内存 ≥ 阈值时不放行新任务）与 UI Tooltip 展示使用。
/// Windows 平台用 P/Invoke（GetSystemTimes / GlobalMemoryStatusEx）；非 Windows 或采样失败时值保持 -1（视为未知，不误拦）。
/// 静态实例随进程共享：Changed 事件可能来自后台线程，订阅方需自行切 Dispatcher。</summary>
using System.Runtime.InteropServices;

namespace GAIRR.Core;

public static class HostLoadMonitor
{
    /// <summary>资源闸默认阈值：CPU 或内存占用 ≥ 该值（%）时不再放行新任务（可扩展为 config 配置）。</summary>
    public const double GatePct = 80;

    static readonly object _sync = new();
    static readonly Timer _timer = new(_ => Tick(), null, 0, 1000);   // 首次立即采样，此后每 1 秒一次

    static double _cpuPct = -1;      // CPU 占用率（0-100；-1=未知/未就绪）
    static double _memPct = -1;      // 内存占用率（0-100；-1=未知）
    // CPU 采样基线（GetSystemTimes 差值法需要相邻两次样本）
    static bool _hasCpuBase;
    static ulong _prevIdle, _prevKernel, _prevUser;

    /// <summary>采样更新事件（每秒一次，后台线程触发；UI 订阅需切 Dispatcher）</summary>
    public static event Action? Changed;

    /// <summary>最近一次 CPU 占用率（0-100；-1=未知/未就绪）</summary>
    public static double CpuPercent { get { lock (_sync) return _cpuPct; } }

    /// <summary>最近一次内存占用率（0-100；-1=未知）</summary>
    public static double MemPercent { get { lock (_sync) return _memPct; } }

    /// <summary>主机是否超载：CPU 或内存任一 ≥ 阈值（未知按未超载处理，避免误拦）。
    /// 用于"CPU/内存超过 80% 不再开新任务"：只拦新任务启动，不打断正在运行的任务。</summary>
    public static bool IsOverloaded()
    {
        double cpu, mem;
        lock (_sync) { cpu = _cpuPct; mem = _memPct; }
        return (cpu >= 0 && cpu >= GatePct) || (mem >= 0 && mem >= GatePct);
    }

    static void Tick()
    {
        double cpu = -1, mem = -1;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                mem = SampleMemory();
                cpu = SampleCpu();
            }
        }
        catch { /* 采样失败保持 -1（未知=放行），不让资源闸因平台问题误拦或崩溃 */ }
        bool anyChanged;
        lock (_sync)
        {
            anyChanged = cpu != _cpuPct || mem != _memPct;
            _cpuPct = cpu;
            _memPct = mem;
        }
        if (anyChanged) Changed?.Invoke();
    }

    /// <summary>全局内存使用率（GlobalMemoryStatusEx.dwMemoryLoad 直接给 0-100 百分比）</summary>
    static double SampleMemory()
    {
        var mse = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref mse) ? mse.dwMemoryLoad : -1;
    }

    /// <summary>全局 CPU 使用率：GetSystemTimes 相邻两次样本差值（kernel 含 idle，总增量 = kernel+user 增量，
    /// 忙增量 = 总增量 - idle 增量）。首次调用只建基线返回 -1，下一秒得到真实值。</summary>
    static double SampleCpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return -1;
        var i = ToU64(idle);
        var k = ToU64(kernel);
        var u = ToU64(user);
        lock (_sync)
        {
            if (!_hasCpuBase)
            {
                _hasCpuBase = true;
                _prevIdle = i; _prevKernel = k; _prevUser = u;
                return -1;
            }
            var totalDelta = (k - _prevKernel) + (u - _prevUser);
            if (totalDelta == 0) return _cpuPct;   // 时间未推进，沿用旧值
            var busyDelta = totalDelta - (i - _prevIdle);
            _prevIdle = i; _prevKernel = k; _prevUser = u;
            var pct = busyDelta * 100.0 / totalDelta;
            return Math.Clamp(pct, 0, 100);
        }
    }

    static ulong ToU64(FILETIME ft) => ((ulong)ft.dwHighDateTime << 32) | ft.dwLowDateTime;

    // ---- Windows P/Invoke ----
    [StructLayout(LayoutKind.Sequential)]
    struct FILETIME { public uint dwLowDateTime; public uint dwHighDateTime; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
