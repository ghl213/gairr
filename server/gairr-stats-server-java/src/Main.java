import com.sun.net.httpserver.HttpExchange;
import com.sun.net.httpserver.HttpServer;

import java.io.IOException;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.time.LocalDateTime;
import java.time.format.DateTimeFormatter;

// gairr-stats-server-java: GAIRR anonymous stats service (zero deps, JDK HttpServer).
// Routes:
// GET / stats page (HTML, auto loads /api/overview)
// GET /health health check {ok,service,time}
// POST /api/report report endpoint (no token required)
// GET /api/overview overview (requires token when configured, else 401)
// Config: --port/GAIRR_STATS_PORT (default 8300), --token/GAIRR_STATS_TOKEN.
public class Main {

 private static final String SERVICE = "gairr-stats-server";
 private static final DateTimeFormatter TS = DateTimeFormatter.ofPattern("yyyy-MM-dd'T'HH:mm:ss");

 private static StatsStore store;
 private static Path latestPath;
 private static String authToken;

 public static void main(String[] args) throws IOException {
 final Config cfg;
 try {
 cfg = Config.parse(args);
 } catch (IllegalArgumentException e) {
 System.err.println("[gairr-stats-server] startup failed: " + e.getMessage());
 System.err.println("usage: java -jar gairr-stats-server.jar [--port 8300] [--token xxx]");
 System.exit(1);
 return;
 }

 Path dataDir = Paths.get("data");
 Files.createDirectories(dataDir);
 store = StatsStore.load(dataDir);
 latestPath = dataDir.resolve("latest.json");
 authToken = cfg.token;

 HttpServer server = HttpServer.create(new InetSocketAddress(cfg.port), 0);
 server.createContext("/", Main::dispatch);

 Runtime.getRuntime().addShutdownHook(new Thread(() -> {
 server.stop(0);
 System.out.println("[gairr-stats-server] stopped");
 }, "gairr-stats-shutdown"));

 server.start();
 System.out.printf("gairr-stats-server started: http://0.0.0.0:%d (token auth: %s, press Ctrl+C to exit)%n",
 cfg.port, cfg.hasToken() ? "configured" : "off");
 }

 // Route dispatch by path + method; unknown path 404, wrong method 405.
 private static void dispatch(HttpExchange ex) {
 try {
 String path = ex.getRequestURI().getPath();
 String method = ex.getRequestMethod();

 if ("/".equals(path) && "GET".equals(method)) { PageHandler.handle(ex); return; }
 if ("/health".equals(path) && "GET".equals(method)) { health(ex); return; }
 if ("/api/report".equals(path) && "POST".equals(method)) { ReportHandler.handle(ex, store, latestPath); return; }
 if ("/api/overview".equals(path) && "GET".equals(method)) { if (!checkToken(ex)) { sendJson(ex, 401, "{\"ok\":false,\"error\":\"unauthorized\"}"); return; } OverviewHandler.handle(ex, store); return; }

 boolean knownPath = "/".equals(path) || "/health".equals(path)
 || "/api/report".equals(path) || "/api/overview".equals(path);
 if (knownPath) {
 sendJson(ex, 405, "{\"ok\":false,\"error\":\"method not allowed\"}");
 } else {
 sendJson(ex, 404, "{\"ok\":false,\"error\":\"not found: " + jsonEscape(path) + "\"}");
 }
 } catch (Exception e) {
 e.printStackTrace();
 try {
 sendJson(ex, 500, "{\"ok\":false,\"error\":\"internal error\"}");
 } catch (IOException ignored) {
 }
 } finally {
 ex.close();
 }
 }

 // Token check: no token configured -> open; else require query ?token= or X-Token header.
 private static boolean checkToken(HttpExchange ex) {
 if (authToken == null || authToken.isEmpty()) return true;
 String t = null;
 String q = ex.getRequestURI().getQuery();
 if (q != null) {
 for (String pair : q.split("&")) {
 int i = pair.indexOf("=");
 if (i > 0 && "token".equals(pair.substring(0, i))) {
 try { t = java.net.URLDecoder.decode(pair.substring(i + 1), "UTF-8"); } catch (Exception e) { t = pair.substring(i + 1); }
 }
 }
 }
 if (t == null) t = ex.getRequestHeaders().getFirst("X-Token");
 return authToken.equals(t);
 }

 // GET /health: health check, returns {ok, service, time}; not affected by token.
 private static void health(HttpExchange ex) throws IOException {
 String body = "{\"ok\":true,\"service\":\"" + SERVICE + "\",\"time\":\""
 + LocalDateTime.now().format(TS) + "\"}";
 sendJson(ex, 200, body);
 }

 // Send JSON response (hand-built string, zero deps).
 private static void sendJson(HttpExchange ex, int code, String body) throws IOException {
 byte[] bytes = body.getBytes(StandardCharsets.UTF_8);
 ex.getResponseHeaders().set("Content-Type", "application/json; charset=utf-8");
 ex.getResponseHeaders().set("Server", SERVICE + "-java");
 ex.sendResponseHeaders(code, bytes.length);
 try (OutputStream os = ex.getResponseBody()) {
 os.write(bytes);
 }
 }

 // Simple JSON string escape.
 private static String jsonEscape(String s) {
 if (s == null) return "";
 StringBuilder sb = new StringBuilder(s.length() + 16);
 for (char c : s.toCharArray()) {
 switch (c) {
 case '"': sb.append("\\\""); break;
 case '\\': sb.append("\\\\"); break;
 case '\n': sb.append("\\n"); break;
 case '\r': sb.append("\\r"); break;
 case '\t': sb.append("\\t"); break;
 default:
 if (c < 0x20) sb.append(String.format("\\u%04x", (int) c));
 else sb.append(c);
 }
 }
 return sb.toString();
 }

 // Service config: port and optional token.
 static final class Config {
 final int port;
 final String token;

 private Config(int port, String token) {
 this.port = port;
 this.token = token;
 }

 boolean hasToken() {
 return token != null && !token.isEmpty();
 }

 static Config parse(String[] args) {
 String portArg = null;
 String tokenArg = null;
 for (int i = 0; i < args.length; i++) {
 String a = args[i];
 if (a.startsWith("--port=")) portArg = a.substring("--port=".length());
 else if (a.startsWith("--token=")) tokenArg = a.substring("--token=".length());
 else if ("--port".equals(a) && i + 1 < args.length) portArg = args[++i];
 else if ("--token".equals(a) && i + 1 < args.length) tokenArg = args[++i];
 else System.err.println("[gairr-stats-server] ignoring unknown argument: " + a);
 }

 int port = portArg != null
 ? parsePort(portArg)
 : parseEnvPort(System.getenv("GAIRR_STATS_PORT"), 8300);
 String token = tokenArg != null ? tokenArg : System.getenv("GAIRR_STATS_TOKEN");
 return new Config(port, token);
 }

 private static int parsePort(String v) {
 return parseInt("--port", v, 1, 65535);
 }

 private static int parseEnvPort(String v, int def) {
 if (v == null || v.trim().isEmpty()) return def;
 return parseInt("GAIRR_STATS_PORT", v, 1, 65535);
 }

 private static int parseInt(String source, String v, int min, int max) {
 final int n;
 try {
 n = Integer.parseInt(v.trim());
 } catch (NumberFormatException e) {
 throw new IllegalArgumentException(source + " not a valid integer: " + v);
 }
 if (n < min || n > max) {
 throw new IllegalArgumentException(source + " out of range [" + min + "," + max + "]: " + n);
 }
 return n;
 }
 }
}
