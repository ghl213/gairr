import com.sun.net.httpserver.HttpExchange;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.LocalDateTime;
import java.time.format.DateTimeFormatter;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;

/**
 * /api/report handler: parse body, validate, persist via StatsStore, return version info.
 * Field-by-field compatible with .NET endpoint in server/GAIRR.StatServer/Program.cs.
 * No auth on this endpoint (client carries no token). Uses package-private StatsJson.
 */
final class ReportHandler {
 private static final DateTimeFormatter TS = DateTimeFormatter.ofPattern("yyyy-MM-dd'T'HH:mm:ss");
 private ReportHandler() {}

 static void handle(HttpExchange ex, StatsStore store, Path latestPath) throws IOException {
 String body;
 try (InputStream in = ex.getRequestBody()) { body = new String(in.readAllBytes(), StandardCharsets.UTF_8); }
 Object rootV;
 try { rootV = StatsJson.parse(body); }
 catch (RuntimeException e) { send(ex, 400, err("bad json")); return; }
 if (!(rootV instanceof Map)) { send(ex, 400, err("bad json")); return; }
 Map<?, ?> root = (Map<?, ?>) rootV;
 String deviceId = str(field(root, "deviceId")).trim();
 if (deviceId.isEmpty()) { send(ex, 400, err("deviceId required")); return; }
 if (deviceId.length() > 64) { send(ex, 400, err("deviceId too long")); return; }
 String os = str(field(root, "os")).trim();
 String appVersion = str(field(root, "appVersion")).trim();
 List<StatsStore.Day> days = new ArrayList<>();
 int rawCount = 0;
 Object daysV = field(root, "days");
 if (daysV instanceof List<?>) {
 List<?> arr = (List<?>) daysV;
 rawCount = arr.size();
 for (Object item : arr) {
 if (!(item instanceof Map)) continue;
 Map<?, ?> dm = (Map<?, ?>) item;
 Object secV = field(dm, "seconds");
 if (!(secV instanceof Number)) continue;
 double sec = ((Number) secV).doubleValue();
 if (sec != Math.floor(sec)) continue;
 days.add(new StatsStore.Day(str(field(dm, "date")), (long) sec));
 }
 }
 String ip = "";
 if (ex.getRemoteAddress() != null && ex.getRemoteAddress().getAddress() != null) { ip = ex.getRemoteAddress().getAddress().getHostAddress(); }
 String now = LocalDateTime.now().format(TS);
 try { store.recordReport(deviceId, os, appVersion, ip, now, days); }
 catch (IOException e) { send(ex, 500, err("persist failed")); return; }
 Latest latest = loadLatest(latestPath);
 boolean hasUpdate = !latest.version.isEmpty() && !appVersion.isEmpty() && !appVersion.equalsIgnoreCase(latest.version);
 StringBuilder b = new StringBuilder();
 b.append('{');
 b.append(q("ok")).append(':').append("true");
 b.append(',').append(q("received")).append(':').append(rawCount);
 b.append(',').append(q("latest")).append(':').append(q(latest.version));
 b.append(',').append(q("updateUrl")).append(':').append(q(latest.url));
 b.append(',').append(q("note")).append(':').append(q(latest.note));
 b.append(',').append(q("hasUpdate")).append(':').append(hasUpdate ? "true" : "false");
 b.append(',').append(q("time")).append(':').append(q(now));
 b.append('}');
 send(ex, 200, b.toString());
 }

 private static Object field(Map<?, ?> obj, String key) {
 if (obj.containsKey(key)) return obj.get(key);
 for (Map.Entry<?, ?> e : obj.entrySet()) { if (e.getKey() instanceof String && ((String) e.getKey()).equalsIgnoreCase(key)) return e.getValue(); }
 return null;
 }
 private static String str(Object o) { return o instanceof String ? (String) o : ""; }

 private static Latest loadLatest(Path path) {
 try {
 if (path != null && Files.isRegularFile(path)) {
 String text = Files.readString(path, StandardCharsets.UTF_8);
 if (!text.isEmpty() && text.charAt(0) == 65279) text = text.substring(1);
 Object root = StatsJson.parse(text);
 if (root instanceof Map) {
 Map<?, ?> m = (Map<?, ?>) root;
 return new Latest(str(field(m, "version")), str(field(m, "url")), str(field(m, "note")));
 }
 }
 } catch (Exception ignored) { }
 return new Latest("", "", "");
 }

 private static final class Latest {
 final String version, url, note;
 Latest(String v, String u, String n) { version = v; url = u; note = n; }
 }

 private static String err(String msg) { return "{" + q("error") + ":" + q(msg) + "}"; }
 private static String q(String s) { return String.valueOf((char) 34) + esc(s) + (char) 34; }

 private static String esc(String s) {
 if (s == null) return "";
 StringBuilder sb = new StringBuilder(s.length() + 8);
 for (int i = 0; i < s.length(); i++) {
 char c = s.charAt(i);
 if (c == 34) sb.append((char) 92).append((char) 34);
 else if (c == 92) sb.append((char) 92).append((char) 92);
 else if (c == 10) sb.append((char) 92).append('n');
 else if (c == 13) sb.append((char) 92).append('r');
 else if (c == 9) sb.append((char) 92).append('t');
 else if (c < 32) sb.append('?');
 else sb.append(c);
 }
 return sb.toString();
 }

 private static void send(HttpExchange ex, int code, String body) throws IOException {
 byte[] bytes = body.getBytes(StandardCharsets.UTF_8);
 ex.getResponseHeaders().set("Content-Type", "application/json; charset=utf-8");
 ex.getResponseHeaders().set("Server", "gairr-stats-server-java");
 ex.sendResponseHeaders(code, bytes.length);
 try (OutputStream os = ex.getResponseBody()) { os.write(bytes); }
 }
}
