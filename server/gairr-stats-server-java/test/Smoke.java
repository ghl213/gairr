import java.io.File;
import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.time.Duration;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.TimeUnit;

/**
 * Smoke —— gairr-stats-server-java 冒烟自测（零第三方依赖，仅 JDK 自带 http client）。
 *
 * 用法：java test\Smoke.java [jar 路径]   （默认 build\gairr-stats-server.jar）
 * 会依次拉起多个服务进程，验证路由/端口来源后自动销毁，不留后台进程。
 * 控制台输出统一用 ASCII，避免 Windows cmd 代码页导致中文乱码。
 */
public class Smoke {

    private static final HttpClient HTTP = HttpClient.newBuilder().connectTimeout(Duration.ofSeconds(3)).build();
    private static final List<Process> PROCS = new ArrayList<>();
    private static String jar;
    private static int pass = 0, fail = 0;

    public static void main(String[] args) throws Exception {
        jar = new File(args.length > 0 ? args[0] : "build/gairr-stats-server.jar").getAbsolutePath();
        if (!new File(jar).exists()) { System.out.println("JAR NOT FOUND: " + jar + " (run build.cmd first)"); System.exit(2); }
        try {
            run();
        } finally {
            shutdownAll();
        }
        System.out.println("---- RESULT: pass=" + pass + " fail=" + fail);
        System.exit(fail == 0 ? 0 : 1);
    }

    /** 全部用例：路由分发 + 端口来源（参数/等号/环境变量/优先级）+ 异常输入 + 进程常驻。 */
    private static void run() throws Exception {
        Process p1 = start("smoke-default.log", null, null, 8300);
        check("default port 8300 listening", p1 != null);
        if (p1 != null) {
            R h = get(8300, "/health");
            check("GET /health -> 200", h.code == 200);
            check("GET /health has \"ok\"", h.body.contains("\"ok\":true"));
            check("GET /health has \"service\"", h.body.contains("\"service\":\"gairr-stats-server\""));
            check("GET /health has \"time\"", h.body.contains("\"time\":\""));
            check("response Content-Type=application/json", h.raw != null
                    && h.raw.headers().firstValue("Content-Type").orElse("").startsWith("application/json"));
            R root = get(8300, "/");
            check("GET / -> 200 and lists endpoints", root.code == 200 && root.body.contains("/api/overview"));
            R rep = post(8300, "/api/report", "{\"deviceId\":\"smoke-1\"}");
            check("POST /api/report responds (skeleton 501)", rep.code == 501 || rep.code == 200);
            R ov = get(8300, "/api/overview");
            check("GET /api/overview responds (skeleton 501)", ov.code == 501 || ov.code == 200);
            check("unknown path -> 404", get(8300, "/nope").code == 404);
            check("wrong method -> 405", get(8300, "/api/report").code == 405);
            check("process stays alive after start", p1.isAlive());
        }

        check("--port 8321 works", alive(start("smoke-arg.log", new String[]{"--port", "8321"}, null, 8321), 8321));
        check("--port=8322 equals form works", alive(start("smoke-eq.log", new String[]{"--port=8322"}, null, 8322), 8322));

        Map<String, String> env = new HashMap<>();
        env.put("GAIRR_STATS_PORT", "8323");
        env.put("GAIRR_STATS_TOKEN", "smoke-token");
        check("env GAIRR_STATS_PORT=8323 works", alive(start("smoke-env.log", null, env, 8323), 8323));

        Map<String, String> env2 = new HashMap<>();
        env2.put("GAIRR_STATS_PORT", "8399");
        Process p5 = start("smoke-precedence.log", new String[]{"--port", "8324"}, env2, 8324);
        check("cli --port wins over env", alive(p5, 8324) && get(8399, "/health").code != 200);

        check("invalid port 99999 exits non-zero", startFails(new String[]{"--port", "99999"}, "smoke-badport.log"));

        hookProbe(8335);

        System.out.println("---- server stdout (default) ----");
        System.out.println(tail(new File(tmp(), "smoke-default.log"), 6));
        System.out.println("---- server stdout (precedence) ----");
        System.out.println(tail(new File(tmp(), "smoke-precedence.log"), 6));
    }

    private static boolean alive(Process p, int port) { return p != null && get(port, "/health").code == 200; }

    private static void shutdownAll() {
        for (Process p : PROCS) {
            if (p.isAlive()) {
                p.destroy();
                try { p.waitFor(5, TimeUnit.SECONDS); } catch (InterruptedException ignored) { Thread.currentThread().interrupt(); }
                if (p.isAlive()) p.destroyForcibly();
            }
        }
    }

    /** 拉起一个服务进程并等待 /health 就绪；就绪失败返回 null 并打印日志尾部。 */
    private static Process start(String logName, String[] extra, Map<String, String> env, int port) throws Exception {
        List<String> cmd = new ArrayList<>();
        cmd.add(javaPath()); cmd.add("-jar"); cmd.add(jar);
        if (extra != null) for (String s : extra) cmd.add(s);
        ProcessBuilder pb = new ProcessBuilder(cmd);
        pb.redirectErrorStream(true);
        pb.redirectOutput(new File(tmp(), logName));
        if (env != null) pb.environment().putAll(env);
        Process p = pb.start();
        PROCS.add(p);
        for (int i = 0; i < 60; i++) {
            if (!p.isAlive()) { System.out.println("server exited early (code=" + p.exitValue() + "), log: " + logName); return null; }
            if (get(port, "/health").code == 200) return p;
            Thread.sleep(300);
        }
        System.out.println("server not ready on " + port + ", log tail: " + tail(new File(tmp(), logName), 8));
        return null;
    }

    /** 启动应立即失败的场景（如非法端口）：进程在 10s 内退出且退出码非 0。 */
    private static boolean startFails(String[] args, String logName) throws Exception {
        List<String> cmd = new ArrayList<>();
        cmd.add(javaPath()); cmd.add("-jar"); cmd.add(jar);
        for (String s : args) cmd.add(s);
        ProcessBuilder pb = new ProcessBuilder(cmd);
        pb.redirectErrorStream(true);
        pb.redirectOutput(new File(tmp(), logName));
        Process p = pb.start();
        PROCS.add(p);
        return p.waitFor(10, TimeUnit.SECONDS) && p.exitValue() != 0;
    }

    /**
     * 优雅退出验证：以源码方式运行 test\HookProbe.java（jar 放在 classpath 上），
     * 服务就绪后探针自身 System.exit(0)；据此确认 Main 注册的 shutdown hook 会执行：
     * 打印 "[gairr-stats-server] stopped"、进程退出码 0、端口随即释放。
     * （Windows 下 Ctrl+C 与 System.exit 走同一条 JVM 关闭序列，故为等价验证。）
     */
    private static void hookProbe(int port) throws Exception {
        File probe = new File("test" + File.separator + "HookProbe.java");
        File log = new File(tmp(), "smoke-hook.log");
        if (!probe.exists()) {
            check("graceful shutdown hook runs (probe file missing: " + probe.getAbsolutePath() + ")", false);
            return;
        }
        List<String> cmd = new ArrayList<>();
        cmd.add(javaPath()); cmd.add("-cp"); cmd.add(jar); cmd.add(probe.getPath());
        cmd.add("--port"); cmd.add(String.valueOf(port));
        ProcessBuilder pb = new ProcessBuilder(cmd);
        pb.redirectErrorStream(true);
        pb.redirectOutput(log);
        Process p = pb.start();
        PROCS.add(p);

        boolean ready = false;
        for (int i = 0; i < 60 && p.isAlive(); i++) {
            if (get(port, "/health").code == 200) { ready = true; break; }
            Thread.sleep(300);
        }
        check("hook probe: server ready on port " + port, ready);

        boolean exited = p.waitFor(15, TimeUnit.SECONDS);
        String out = tail(log, 20);
        check("hook probe: JVM exits on its own", exited);
        check("hook probe: exit code 0", exited && p.exitValue() == 0);
        check("hook probe: shutdown hook logged \"stopped\"", out.contains("[gairr-stats-server] stopped"));
        Thread.sleep(500);
        check("hook probe: port " + port + " released after shutdown", get(port, "/health").code != 200);
        System.out.println("---- hook probe stdout ----");
        System.out.println(out);
    }

    private static String javaPath() {
        File cand = new File(System.getProperty("java.home") + File.separator + "bin" + File.separator + "java.exe");
        return cand.exists() ? cand.getAbsolutePath() : "java";
    }

    private static String tmp() { return System.getProperty("java.io.tmpdir"); }

    private static void check(String name, boolean ok) {
        if (ok) { pass++; System.out.println("PASS  " + name); }
        else { fail++; System.out.println("FAIL  " + name); }
    }

    /** 一次 HTTP 调用结果；code=-1 表示连接失败（服务未监听）。 */
    private static final class R {
        final int code; final String body; final HttpResponse<String> raw;
        R(int c, String b, HttpResponse<String> r) { code = c; body = b; raw = r; }
    }

    private static R req(String method, int port, String path, String body) {
        try {
            HttpRequest.Builder b = HttpRequest.newBuilder(URI.create("http://127.0.0.1:" + port + path)).timeout(Duration.ofSeconds(5));
            if ("POST".equals(method)) {
                b.POST(HttpRequest.BodyPublishers.ofString(body == null ? "" : body, StandardCharsets.UTF_8)).header("Content-Type", "application/json");
            } else {
                b.GET();
            }
            HttpResponse<String> resp = HTTP.send(b.build(), HttpResponse.BodyHandlers.ofString(StandardCharsets.UTF_8));
            return new R(resp.statusCode(), resp.body(), resp);
        } catch (Exception e) {
            return new R(-1, e.getClass().getSimpleName() + ": " + e.getMessage(), null);
        }
    }

    private static R get(int port, String path) { return req("GET", port, path, null); }

    private static R post(int port, String path, String body) { return req("POST", port, path, body); }

    /** 读取日志尾部 n 行（服务用平台默认编码输出，这里按 ISO-8859-1 兜底避免抛异常）。 */
    private static String tail(File f, int n) {
        try {
            if (!f.exists()) return "(no log)";
            List<String> lines = Files.readAllLines(f.toPath(), StandardCharsets.ISO_8859_1);
            StringBuilder sb = new StringBuilder();
            for (int i = Math.max(0, lines.size() - n); i < lines.size(); i++) sb.append(lines.get(i)).append('\n');
            return sb.toString();
        } catch (Exception e) {
            return "(log read failed: " + e + ")";
        }
    }
}
