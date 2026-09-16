using System.Collections.Concurrent;
using GAIRR.AgentHost;

// 桌面 GUI 实时流中转：GUI 进程（gairr.exe）把事件泵里的会话事件 POST /gui-live/{sid} 推送进来，
// 手机端经 SSE /sessions/{sid}/gui-events 只读观看（桌面执行中 busy 会话）。
// 缓冲按"轮边界收编"：Round/Finished/Failed 事件 = 桌面已完成该轮并落盘（GUI 每轮 Round 后 SaveCurrentSession），
// 立即清空缓冲——晚接入的观看者由 /history 拿已落盘轮，只回放"当前进行中轮"，避免与历史重复渲染。
sealed class GuiLiveFeed
{
    readonly List<SessionEventDto> buffer = new();
    readonly List<Action<SessionEventDto>> subs = new();
    readonly object gate = new();
    const int MaxBuf = 600;   // 单轮事件兜底上限（ThinkingLive 约 1.2s 节流全量快照 + 工具/日志事件；溢出丢最旧）

    public void Post(SessionEventDto e)
    {
        Action<SessionEventDto>[]? targets = null;
        lock (gate)
        {
            if (e.Type is "Round" or "Finished" or "Failed")
                buffer.Clear();   // 轮收编：本轮已落盘，后续轮从空缓冲开始
            else
            {
                buffer.Add(e);
                if (buffer.Count > MaxBuf) buffer.RemoveAt(0);
            }
            if (subs.Count > 0) targets = subs.ToArray();
        }
        if (targets != null)
            foreach (var s in targets)
            {
                try { s(e); } catch { }
            }
    }

    /// <summary>注册订阅并原子取回当前缓冲快照（同锁内完成：注册后的实时事件必达、快照不回放重复）
    /// 返回 (可释放订阅, 回放快照)</summary>
    public (IDisposable Sub, List<SessionEventDto> Snapshot) Attach(Action<SessionEventDto> handler)
    {
        lock (gate)
        {
            subs.Add(handler);
            return (new Sub(() => Detach(handler)), buffer.ToList());
        }
    }

    public void Detach(Action<SessionEventDto> handler)
    {
        lock (gate) subs.Remove(handler);
    }

    sealed class Sub : IDisposable
    {
        readonly Action release;
        public Sub(Action r) => release = r;
        public void Dispose() => release();
    }
}

/// <summary>会话级实时流存储：ConcurrentDictionary 惰性建 feed（首个事件/首个订阅时创建）</summary>
sealed class GuiLiveHub
{
    readonly ConcurrentDictionary<string, GuiLiveFeed> map = new(StringComparer.OrdinalIgnoreCase);

    public void Post(string sessionId, SessionEventDto e) =>
        map.GetOrAdd(sessionId, _ => new GuiLiveFeed()).Post(e);

    /// <summary>订阅会话实时流（附当前进行中轮缓冲快照）；无历史订阅者也建空 feed 挂起等首事件</summary>
    public (IDisposable Sub, List<SessionEventDto> Replay) Attach(string sessionId, Action<SessionEventDto> handler)
    {
        var feed = map.GetOrAdd(sessionId, _ => new GuiLiveFeed());
        return feed.Attach(handler);
    }
}
