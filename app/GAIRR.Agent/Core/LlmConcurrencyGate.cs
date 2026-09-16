using System;
using System.Collections.Generic;
using System.Linq;

namespace GAIRR.Core;

/// <summary>
/// 厂商级并发闸：跨所有模型调用入口（普通会话 / 自动任务 / 编排执行 / 计划子会话 / 地图增补 / 服务端多会话等）
/// 统一统计每个模型厂商"当前正在运行的任务数"。某厂商任务数达到其最大并发时，新任务在启动前被拒绝，
/// 由调用方给出明确提示（不进入运行、不占上下文）。
/// 上限规则：config.ini 对应 [厂商] 节写 MaxConcurrency=N 可覆盖；否则本地大模型默认 1、在线厂商默认 2。
/// 静态实例随进程共享；UI 通过 Changed 事件（可能来自后台线程，订阅方需自行切 Dispatcher）刷新占用展示。
/// 每个占用位还登记"该位当前跑的具体模型"（IsModelInUse），供 UI 只把真正在用的那一项着金色，
/// 而不是按厂商把整片模型全涂金（并发上限仍是厂商级，模型登记只用于展示归属）。
/// </summary>
public static class LlmConcurrencyGate
{
    /// <summary>单个并发占用者：登记该占用位"当前跑的是哪个具体模型"。
    /// ModelGetter 为活引用（如 () =&gt; client.Model），任务中途切模型 / 故障转移后 UI 金色标注自动跟随，无需重新占位。</summary>
    sealed class Occupant
    {
        public Func<string?>? ModelGetter;

        /// <summary>取当前模型 id：getter 异常或为 null 时返回空串（不影响占用计数，只是不参与金色标注）</summary>
        public string Model()
        {
            try { return ModelGetter?.Invoke() ?? ""; }
            catch { return ""; }
        }
    }

    sealed class SlotInfo
    {
        /// <summary>在册占用者列表：个数即当前占用数，元素记录各自在跑的具体模型</summary>
        public readonly List<Occupant> Occupants = new();

        /// <summary>当前占用数（与旧版计数字段同口径）</summary>
        public int Cur => Occupants.Count;

        public int Max;
    }

    static readonly object _sync = new();
    static readonly Dictionary<string, SlotInfo> _slots = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>并发占用变化事件：进入占用 / 释放占用 / 上限被查询记录变化时触发。</summary>
    public static event Action? Changed;

    /// <summary>尝试占用某厂商的一个并发位；占用成功返回作用域对象（释放时自动回退计数），
    /// 已达上限返回 null —— 调用方应拒启任务并给出明确原因。
    /// modelGetter 可选：登记本占用位"当前在跑的具体模型"（建议传活引用如 () =&gt; client.Model，
    /// 中途切模型 / 故障转移后 IsModelInUse 自动跟随），不传则该位不参与模型级金色标注。</summary>
    public static IDisposable? TryEnter(string provider, int max, Func<string?>? modelGetter = null)
    {
        if (string.IsNullOrWhiteSpace(provider) || max <= 0) return null;
        Occupant occ;
        lock (_sync)
        {
            if (!_slots.TryGetValue(provider, out var s))
            {
                s = new SlotInfo();
                _slots[provider] = s;
            }
            // 上限取"本次配置 vs 当前在跑数"的较大者：配置调高即时生效；配置调低不打断在跑任务，
            // 随着任务结束 cur 回落、新的 Enter 携新上限进入后自然收紧到新上限
            var desiredMax = Math.Max(max, s.Cur);
            if (desiredMax != s.Max) s.Max = desiredMax;
            if (s.Cur >= s.Max) return null;
            occ = new Occupant { ModelGetter = modelGetter };
            s.Occupants.Add(occ);
        }
        Changed?.Invoke();
        return new SlotScope(provider, occ);
    }

    /// <summary>只读判满：按 TryEnter 同口径判断某厂商当前是否已无空位（当前占用 cur ≥ 期望上限 max 即满），
    /// 不实际占用并发位 —— 供 UI 在点击发送瞬间预检（此时文本尚在输入框、任务未启动），
    /// 满则直接弹窗提示并中止发送，避免先上屏/启动后到 RunAsync 里才报"并发已达上限"。</summary>
    public static bool IsFull(string provider, int max)
    {
        if (string.IsNullOrWhiteSpace(provider) || max <= 0) return true;
        lock (_sync)
        {
            // 与 TryEnter 判定一致：cur >= max(本次配置, cur) ⟺ cur >= max（无记录时 cur=0 必可进）
            return _slots.TryGetValue(provider, out var s) && s.Cur >= max;
        }
    }

    /// <summary>某厂商当前占用与上限；无任何记录时按"未知在线 2"返回（调用方展示时应以 cfg.MaxConcurrencyFor 为准）</summary>
    public static (int Cur, int Max) Current(string provider)
    {
        lock (_sync)
        {
            if (provider != null && _slots.TryGetValue(provider, out var s)) return (s.Cur, s.Max);
        }
        return (0, 2);
    }

    /// <summary>精确到模型的在用查询：某厂商下"这个具体模型"当前是否正被任务占用。
    /// 供 UI 只把真正在跑的那一项着金色，同厂商其它模型保持正文色（不再按厂商整片涂金）。</summary>
    public static bool IsModelInUse(string provider, string model)
    {
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model)) return false;
        lock (_sync)
        {
            if (!_slots.TryGetValue(provider, out var s)) return false;
            foreach (var o in s.Occupants)
                if (string.Equals(o.Model(), model, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>某厂商当前在跑的模型清单（去重、按占用先后）；供 Tooltip / 状态栏展示"谁在忙"，无占用返回空表</summary>
    public static IReadOnlyList<string> InUseModels(string provider)
    {
        lock (_sync)
        {
            if (provider == null || !_slots.TryGetValue(provider, out var s)) return Array.Empty<string>();
            var list = new List<string>();
            foreach (var o in s.Occupants)
            {
                var m = o.Model();
                if (!string.IsNullOrWhiteSpace(m) && !list.Contains(m, StringComparer.OrdinalIgnoreCase)) list.Add(m);
            }
            return list;
        }
    }

    /// <summary>全部在册厂商的占用快照（供 UI Tooltip / 状态栏展示，保持厂商首次出现顺序）</summary>
    public static IReadOnlyList<(string Provider, int Cur, int Max)> Snapshot()
    {
        lock (_sync)
            return _slots.Select(kv => (kv.Key, kv.Value.Cur, kv.Value.Max)).ToList();
    }

    sealed class SlotScope : IDisposable
    {
        readonly string _provider;
        readonly Occupant _occ;
        bool _disposed;

        public SlotScope(string provider, Occupant occ)
        {
            _provider = provider;
            _occ = occ;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (_sync)
            {
                // 摘掉本占用位（列表个数即占用数）；找不到说明已被摘过，不重复回退
                if (_slots.TryGetValue(_provider, out var s)) s.Occupants.Remove(_occ);
            }
            Changed?.Invoke();
        }
    }
}
