/// <summary>运行目录文件布局：数据文件归 data/、日志文件归 log/，
/// 统一提供带自动建目录的路径，避免散落在各处的 Path.Combine 重复维护。</summary>
using System.IO;

namespace GAIRR.Core;

public static class Paths
{
    /// <summary>数据子目录：session.json、session_history.json 等程序数据文件</summary>
    public static string DataDir => EnsureDir(Path.Combine(AppContext.BaseDirectory, "data"));

    /// <summary>日志子目录：agent.log、stream_debug.log 等运行日志</summary>
    public static string LogDir => EnsureDir(Path.Combine(AppContext.BaseDirectory, "log"));

    /// <summary>当前会话存盘路径（data/session.json）</summary>
    public static string Session => Path.Combine(DataDir, "session.json");

    /// <summary>会话历史列表存盘路径（data/session_history.json）</summary>
    public static string SessionHistory => Path.Combine(DataDir, "session_history.json");

    /// <summary>手机端服务开关记忆文件（data/mobile_svc.state，内容 on/off=用户最后拨动的意图，
    /// 桌面 GUI 重启据此自动恢复开关；关窗自动停服不改写该意图）</summary>
    public static string MobileSvcState => Path.Combine(DataDir, "mobile_svc.state");

    /// <summary>每轮请求上下文快照根目录（data/rounds/，按会话 Id 分子目录、每轮独立 roundN.json，
    /// Agent 请求前落盘，供思考卡「查看」复盘“模型当时看到了什么”）</summary>
    public static string RoundsDir => EnsureDir(Path.Combine(DataDir, "rounds"));

    /// <summary>Agent 运行日志路径（log/agent.log）</summary>
    public static string AgentLog => Path.Combine(LogDir, "agent.log");

    /// <summary>SSE 流式调试日志路径（log/stream_debug.log）</summary>
    public static string StreamDebugLog => Path.Combine(LogDir, "stream_debug.log");

    /// <summary>确保目录存在后返回路径（目录已存在时零开销）</summary>
    static string EnsureDir(string dir)
    {
        try { Directory.CreateDirectory(dir); } catch { }
        return dir;
    }
}
