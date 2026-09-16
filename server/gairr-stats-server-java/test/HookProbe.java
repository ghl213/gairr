/**
 * HookProbe —— 优雅退出探针（配合 test\Smoke.java 使用，不进 jar）。
 *
 * 作用：调用 Main.main 启动服务，随后主动 System.exit(0)，用于验证 Main 注册的
 * JVM shutdown hook 确实执行（server.stop(0) + 打印 "[gairr-stats-server] stopped" + 端口释放）。
 *
 * 为什么能代表 Ctrl+C：Windows 下 CTRL_C_EVENT / CTRL_CLOSE_EVENT 与 System.exit 走的是
 * 同一条 JVM 关闭序列（先跑 shutdown hooks 再退出），而外部进程无法向控制台子进程注入
 * Ctrl+C（taskkill 不带 /F 对无窗口进程直接报"只能强行终止"），故用本探针做等价自动化验证。
 *
 * 用法（项目根目录下）：java -cp build\gairr-stats-server.jar test\HookProbe.java --port 8335
 */
public class HookProbe {

    public static void main(String[] args) throws Exception {
        Main.main(args);
        Thread.sleep(1500); // 留出端口就绪时间，供 Smoke 先探活再观察退出
        System.out.println("[hook-probe] calling System.exit(0), shutdown hook should stop the server");
        System.exit(0);
    }
}
