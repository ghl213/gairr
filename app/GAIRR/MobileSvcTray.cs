using System.Drawing;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Forms;

namespace GAIRR
{
    /// <summary>手机端服务系统托盘（右下角常驻图标）：每 10 秒轮询本机 Server /health（127.0.0.1:8123）展示
    /// 运行状态与实时会话数；右键菜单提供 开启服务/退出服务（随状态切换文案）、打开主窗口、退出程序。
    /// 本类只做状态展示与事件转发——启停动作交由 MainWindow 复用手机端按钮同一套 bat 逻辑实际执行。</summary>
    public sealed class MobileSvcTray : IDisposable
    {
        const string HealthUrl = "http://127.0.0.1:8123/health";

        readonly NotifyIcon icon;
        readonly ToolStripMenuItem miSession;   // 会话数（只读展示行）
        readonly ToolStripMenuItem miToggle;    // 开启/退出服务
        readonly System.Windows.Threading.DispatcherTimer poller;
        readonly HttpClient http;
        static readonly Icon AppIcon = LoadAppIcon();

        bool up;          // 最近一次探测：服务是否在运行
        int sessions;     // 最近一次探测：会话数

        /// <summary>请求切换服务状态：true=开启，false=退出（由宿主复用手机端开关逻辑执行）。</summary>
        public event Action<bool>? ToggleRequested;
        /// <summary>请求显示并激活主窗口（左键双击/菜单"打开主窗口"）。</summary>
        public event Action? OpenWindowRequested;
        /// <summary>请求退出整个程序（含停服）。</summary>
        public event Action? ExitRequested;

        public MobileSvcTray()
        {
            http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            icon = new NotifyIcon
            {
                Icon = AppIcon,
                Visible = true,
                Text = "GAIRR 手机端服务（未运行）",
            };

            miSession = new ToolStripMenuItem("手机端会话数：—（服务未运行）") { Enabled = false };
            miToggle = new ToolStripMenuItem("开启服务");
            miToggle.Click += (_, _) => ToggleRequested?.Invoke(!up);
            var miOpen = new ToolStripMenuItem("打开主窗口");
            miOpen.Click += (_, _) => OpenWindowRequested?.Invoke();
            var miExit = new ToolStripMenuItem("退出程序");
            miExit.Click += (_, _) => ExitRequested?.Invoke();

            var menu = new ContextMenuStrip();
            menu.Items.Add(miSession);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miToggle);
            menu.Items.Add(miOpen);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miExit);
            icon.ContextMenuStrip = menu;
            icon.DoubleClick += (_, _) => OpenWindowRequested?.Invoke();

            poller = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            poller.Tick += async (_, _) => await PollAsync();
            poller.Start();
            _ = PollAsync();   // 启动即探测一次，尽早给出真实状态

            // Explorer 通知区可能晚于本程序就绪（开机自启/explorer 重启/多 explorer 实例抢占）：
            // 首次注册若落在窗口期外，图标不会自动出现——延迟 1.5s/5s 各强制重挂一次兜底
            foreach (var ms in new[] { 1500, 5000 })
            {
                var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
                t.Tick += (_, _) => { t.Stop(); try { icon.Visible = true; } catch { } };
                t.Start();
            }
        }

        /// <summary>轮询 /health：成功=服务运行并解析 sessions 会话数；失败=未运行/会话数归零。结果只更新自身状态，不抛异常。</summary>
        async Task PollAsync()
        {
            bool now = false;
            int count = 0;
            try
            {
                using var resp = await http.GetAsync(HealthUrl);
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    var root = doc.RootElement;
                    if (root.TryGetProperty("sessions", out var s) && s.ValueKind == JsonValueKind.Number)
                        count = s.GetInt32();
                    now = true;
                }
            }
            catch { /* 服务未启动/连接失败：视为未运行 */ }
            up = now;
            sessions = count;
            Render();
        }

        /// <summary>按当前状态刷新托盘文本：会话数行、开启/退出菜单文案、图标悬浮提示。</summary>
        void Render()
        {
            miSession.Text = up ? $"手机端会话数：{sessions}" : "手机端会话数：—（服务未运行）";
            miToggle.Text = up ? "退出服务" : "开启服务";
            icon.Text = up ? $"GAIRR 手机端服务 · 会话 {sessions}" : "GAIRR 手机端服务（未运行）";
        }

        /// <summary>通知托盘立即重新探测一次（启停操作完成后由宿主调用，菜单状态即时归位）。</summary>
        public void Refresh() => _ = PollAsync();

        static Icon LoadAppIcon()
        {
            try
            {
                var ic = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "");
                if (ic != null) return ic;
            }
            catch { }
            return SystemIcons.Application;
        }

        public void Dispose()
        {
            poller.Stop();
            icon.Visible = false;
            icon.Dispose();
        }
    }
}