using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using GAIRR.AgentHost;
using GAIRR.Core;

namespace GAIRR;

/// <summary>桌面会话执行流镜像（手机实时观看数据源）：uiTimer 事件泵每拍把带会话归属的事件批量
/// POST 给本机 gairr-agent-server（/gui-live/{sessionId}，仅回环可达），Server 缓冲后手机端 SSE 只读订阅。
/// 尽力而为：Server 未启动/镜像失败一律静默退避，绝不阻塞 UI 线程、绝不影响任务主流程。</summary>
public sealed class LiveMirror
{
    // Server 端口与本机探测点（MobileSvcLocalUp 127.0.0.1:8123）对齐；改端口需同步 config.ini [Server] Port
    const string LocalServerBase = "http://127.0.0.1:8123";
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(2) };
    static readonly JsonSerializerOptions jsonOpts = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    int failStreak;
    long cooldownUntilTicks;   // 熔断截止（UTC Ticks）：连败后暂停 30s 再试，避免 Server 未启动时每拍空探

    /// <summary>泵 tick 尾部调用：把归属会话的事件按会话分组排队发送（仅分组+排程，不阻塞 UI 线程）</summary>
    public void Push(List<UiEvent> drained)
    {
        if (drained == null || drained.Count == 0) return;
        if (DateTime.UtcNow.Ticks < Interlocked.Read(ref cooldownUntilTicks)) return;   // 熔断期静默跳过
        foreach (var g in drained.GroupBy(e => e.SessionKey!))
        {
            var sid = g.Key;
            var batch = g.ToList();
            _ = Task.Run(() => SendBatchAsync(sid, batch));
        }
    }

    async Task SendBatchAsync(string sessionId, List<UiEvent> events)
    {
        try
        {
            var dtos = events.Select(UiEventMapper.ToDto).ToList();
            var json = JsonSerializer.Serialize(dtos, jsonOpts);
            using var req = new HttpRequestMessage(HttpMethod.Post,
                LocalServerBase + "/gui-live/" + Uri.EscapeDataString(sessionId));
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await http.SendAsync(req);
            if (resp.IsSuccessStatusCode) Interlocked.Exchange(ref failStreak, 0);
            else NoteFailure();
        }
        catch { NoteFailure(); }
    }

    void NoteFailure()
    {
        if (Interlocked.Increment(ref failStreak) >= 3)
        {
            Interlocked.Exchange(ref failStreak, 0);
            Interlocked.Exchange(ref cooldownUntilTicks, DateTime.UtcNow.AddSeconds(30).Ticks);
        }
    }

    // ── GUI 方案确认帧（手机端确认计划场景）：弹 PlanConfirmDialog 前推送挂起帧、弹窗收口后推送结果帧 ──
    // 与事件泵走同一 /gui-live/{sid} 通道但独立请求；同步短等待（≤500ms）保证弹框前 Server 已暂存，
    // 手机端任何时刻订阅都有 pending/缓冲帧可回放；失败静默（未启 Server 时桌面任务不受影响）。

    static readonly HttpClient planHttp = new() { Timeout = TimeSpan.FromMilliseconds(500) };

    /// <summary>弹框前推送 PlanConfirm 挂起帧（带 plan 摘要）；返回是否推送成功（失败不阻塞弹框）。</summary>
    public void NotifyPlanPending(string sessionId, PlanDto plan)
    {
        try
        {
            var payload = new
            {
                planId = plan.Id,
                title = plan.Title,
                goal = plan.Goal,
                planMode = plan.PlanMode,
                selectedCandidateId = plan.SelectedCandidateId,
                nodeCount = plan.Nodes?.Count ?? 0,
            };
            PostSync("/gui-live/" + Uri.EscapeDataString(sessionId) + "/plan-confirm", payload);
        }
        catch { /* 尽力而为 */ }
    }

    /// <summary>弹窗收口后推送 PlanResolved 结果帧（allow=确认/取消），清 Server 端 pending、通知手机端关闭确认 UI。</summary>
    public void NotifyPlanResolved(string sessionId, string planId, bool allow)
    {
        try { PostSync("/gui-live/" + Uri.EscapeDataString(sessionId) + "/plan-resolved", new { planId, allow }); }
        catch { /* 尽力而为 */ }
    }

    /// <summary>同步短超时 POST（当前线程等待 ≤500ms，调用方为 UI 线程弹框前后，可接受）。</summary>
    void PostSync(string path, object payload)
    {
        var json = JsonSerializer.Serialize(payload, jsonOpts);
        using var req = new HttpRequestMessage(HttpMethod.Post, LocalServerBase + path);
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        using var resp = planHttp.Send(req, cts.Token);
        planHttp.CancelPendingRequests();   // 释放可能悬挂的旧请求
    }
}
