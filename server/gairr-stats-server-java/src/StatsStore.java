import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.AtomicMoveNotSupportedException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.locks.ReentrantLock;
import java.util.regex.Pattern;

/**
 * StatsStore —— data/stats.json 数据层（Java 零依赖版替代 .NET SQLite）。
 *
 * 口径与 server/GAIRR.StatServer/Program.cs 的 /api/report 完全一致：
 *   usage   ：(deviceId,date) 同键覆盖 seconds（上报值=该日最终累计）；
 *   devices ：首次插入记录 os/appVersion/firstIp/firstSeen，之后每次上报刷新
 *             lastIp/lastSeen、reportCnt+1、totalSeconds+=本次有效秒数和；
 *             os/appVersion 仅在非空时覆盖，firstIp/firstSeen 永不变；
 *   非法行（date 非 yyyy-MM-dd 或 seconds<0）跳过，不整体失败。
 *
 * 持久化：每次上报后 tmp+rename 原子替换 stats.json；加载容错——文件不存在/
 * 损坏/格式不符一律从零重建空库，不抛异常。全部公开方法加 ReentrantLock，
 * 可并发调用（HTTP 多线程上报安全）。
 *
 * JSON 读解析见 StatsJson.java（包私有，仅服务本文件格式）；写为本文件 esc/toJson。
 * 若后续引入统一 MiniJson 工具类，可整体替换这两处。
 */
public final class StatsStore {

    /** 一条按天用量上报（date=yyyy-MM-dd，seconds=该日最终累计，同键覆盖）。 */
    public record Day(String date, long seconds) {}

    /** 设备主档快照（字段与 .NET devices 表一一对应，只读）。 */
    public record Dev(String deviceId, String os, String appVersion,
                      String firstIp, String lastIp, String firstSeen, String lastSeen,
                      long reportCnt, long totalSeconds) {}

    /** 按天用量快照（(deviceId,date) 唯一）。 */
    public record Use(String deviceId, String date, long seconds) {}

    private static final Pattern DATE = Pattern.compile("^\\d{4}-\\d{2}-\\d{2}$");
    private static final String SEP = ""; // usage 复合键分隔符（deviceId+SEP+date）

    private final Path file; // data/stats.json
    private final LinkedHashMap<String, MutDev> devices = new LinkedHashMap<>();
    private final LinkedHashMap<String, Long> usage = new LinkedHashMap<>();
    private final ReentrantLock lock = new ReentrantLock();

    private StatsStore(Path file) { this.file = file; }

    /** 加载数据文件；不存在/损坏/格式不符一律从零重建（空库），不抛异常。 */
    public static StatsStore load(Path dataDir) {
        StatsStore st = new StatsStore(dataDir.resolve("stats.json"));
        if (!Files.isRegularFile(st.file)) return st;
        String text;
        try { text = Files.readString(st.file, StandardCharsets.UTF_8); }
        catch (IOException e) { return st; }
        if (text.startsWith("\uFEFF")) text = text.substring(1);
        Object root;
        try { root = StatsJson.parse(text); } catch (RuntimeException e) { return st; }
        if (!(root instanceof Map)) return st;
        Object devs = ((Map<?, ?>) root).get("devices");
        Object uses = ((Map<?, ?>) root).get("usage");
        if (devs instanceof List<?>) for (Object o : (List<?>) devs) st.loadDev(o);
        if (uses instanceof List<?>) for (Object o : (List<?>) uses) st.loadUse(o);
        return st;
    }

    /** 解析一条设备记录入库；字段缺失按空值/0 默认，类型不符跳过该条。 */
    private void loadDev(Object o) {
        if (!(o instanceof Map)) return;
        Map<?, ?> m = (Map<?, ?>) o;
        String id = str(m.get("deviceId"));
        if (id.isEmpty()) return;
        devices.put(id, new MutDev(id, str(m.get("os")), str(m.get("appVersion")),
                str(m.get("firstIp")), str(m.get("lastIp")), str(m.get("firstSeen")),
                str(m.get("lastSeen")), lng(m.get("reportCnt")), lng(m.get("totalSeconds"))));
    }

    /** 解析一条用量记录入库；键缺失/日期非法/秒数为负则跳过该条。 */
    private void loadUse(Object o) {
        if (!(o instanceof Map)) return;
        Map<?, ?> m = (Map<?, ?>) o;
        String id = str(m.get("deviceId"));
        String date = str(m.get("date"));
        long sec = lng(m.get("seconds"));
        if (id.isEmpty() || !DATE.matcher(date).matches() || sec < 0) return;
        usage.put(id + SEP + date, sec);
    }

    /**
     * 上报入库（口径同 .NET）：合法 day（date=yyyy-MM-dd 且 seconds>=0）写入 usage
     * （同键覆盖）并累计 totalAdd；devices 首插记 os/ver/firstIp/firstSeen，更新则刷新
     * lastIp/lastSeen、reportCnt+1、totalSeconds+=totalAdd，os/appVersion 仅非空覆盖；
     * 非法行跳过不整体失败。入库后原子落盘，返回有效行数。
     */
    public int recordReport(String deviceId, String os, String appVersion,
                            String ip, String nowIso, List<Day> days) throws IOException {
        if (deviceId == null || deviceId.isBlank()) throw new IllegalArgumentException("deviceId 必填");
        String id = deviceId.trim();
        String osT = os == null ? "" : os.trim();
        String verT = appVersion == null ? "" : appVersion.trim();
        String ipS = ip == null ? "" : ip;
        String now = nowIso == null ? "" : nowIso;
        lock.lock();
        try {
            long totalAdd = 0;
            int received = 0;
            if (days != null) for (Day d : days) {
                if (d == null || d.date() == null || d.seconds() < 0) continue;
                String date = d.date().trim();
                if (!DATE.matcher(date).matches()) continue;
                usage.put(id + SEP + date, d.seconds());
                totalAdd += d.seconds();
                received++;
            }
            MutDev dev = devices.get(id);
            if (dev == null) {
                devices.put(id, new MutDev(id, osT, verT, ipS, ipS, now, now, 1, totalAdd));
            } else {
                if (!osT.isEmpty()) dev.os = osT;
                if (!verT.isEmpty()) dev.appVersion = verT;
                dev.lastIp = ipS;
                dev.lastSeen = now;
                dev.reportCnt++;
                dev.totalSeconds += totalAdd;
            }
            saveLocked();
            return received;
        } finally {
            lock.unlock();
        }
    }

    /** 已收录设备数（.NET overview 的 totalDevices 口径）。 */
    public long deviceCount() {
        lock.lock();
        try { return devices.size(); } finally { lock.unlock(); }
    }

    /** 全部设备累计秒数总和（.NET overview 的 totalSeconds 口径）。 */
    public long totalSeconds() {
        lock.lock();
        try {
            long sum = 0;
            for (MutDev d : devices.values()) sum += d.totalSeconds;
            return sum;
        } finally { lock.unlock(); }
    }

    /** 设备主档快照（按首次上报顺序的浅拷贝，只读记录）。 */
    public List<Dev> devices() {
        lock.lock();
        try {
            List<Dev> out = new ArrayList<>(devices.size());
            for (MutDev d : devices.values())
                out.add(new Dev(d.deviceId, d.os, d.appVersion, d.firstIp, d.lastIp,
                        d.firstSeen, d.lastSeen, d.reportCnt, d.totalSeconds));
            return out;
        } finally { lock.unlock(); }
    }

    /** 按天用量快照（(deviceId,date) 唯一，写入顺序）。 */
    public List<Use> usage() {
        lock.lock();
        try {
            List<Use> out = new ArrayList<>(usage.size());
            for (Map.Entry<String, Long> e : usage.entrySet()) {
                int k = e.getKey().indexOf(SEP);
                out.add(new Use(e.getKey().substring(0, k), e.getKey().substring(k + 1), e.getValue()));
            }
            return out;
        } finally { lock.unlock(); }
    }

    /** 原子落盘：先写 stats.json.tmp 再 move 覆盖（ATOMIC_MOVE 不可用时降级普通 move）。 */
    private void saveLocked() throws IOException {
        Path tmp = file.resolveSibling(file.getFileName() + ".tmp");
        Files.writeString(tmp, toJson(), StandardCharsets.UTF_8);
        try {
            Files.move(tmp, file, StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING);
        } catch (AtomicMoveNotSupportedException e) {
            Files.move(tmp, file, StandardCopyOption.REPLACE_EXISTING);
        }
    }

    /** 序列化为单行紧凑 JSON：{"devices":[...],"usage":[...]}。 */
    private String toJson() {
        StringBuilder sb = new StringBuilder(4096);
        sb.append("{\"devices\":[");
        boolean first = true;
        for (MutDev d : devices.values()) {
            if (!first) sb.append(','); first = false;
            sb.append("{\"deviceId\":\"").append(esc(d.deviceId))
              .append("\",\"os\":\"").append(esc(d.os))
              .append("\",\"appVersion\":\"").append(esc(d.appVersion))
              .append("\",\"firstIp\":\"").append(esc(d.firstIp))
              .append("\",\"lastIp\":\"").append(esc(d.lastIp))
              .append("\",\"firstSeen\":\"").append(esc(d.firstSeen))
              .append("\",\"lastSeen\":\"").append(esc(d.lastSeen))
              .append("\",\"reportCnt\":").append(d.reportCnt)
              .append(",\"totalSeconds\":").append(d.totalSeconds).append('}');
        }
        sb.append("],\"usage\":[");
        first = true;
        for (Map.Entry<String, Long> e : usage.entrySet()) {
            int k = e.getKey().indexOf(SEP);
            if (!first) sb.append(','); first = false;
            sb.append("{\"deviceId\":\"").append(esc(e.getKey().substring(0, k)))
              .append("\",\"date\":\"").append(esc(e.getKey().substring(k + 1)))
              .append("\",\"seconds\":").append(e.getValue()).append('}');
        }
        return sb.append("]}").toString();
    }

    /** JSON 字符串转义：控制字符 + 引号 + 反斜杠；非 ASCII 原样输出（UTF-8）。 */
    private static String esc(String s) {
        StringBuilder sb = new StringBuilder(s.length() + 16);
        for (int i = 0; i < s.length(); i++) {
            char c = s.charAt(i);
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

    private static String str(Object o) { return o instanceof String ? (String) o : ""; }
    private static long lng(Object o) { return o instanceof Number ? ((Number) o).longValue() : 0; }

    /** 可变设备记录（内部状态；对外只读快照见 Dev）。 */
    private static final class MutDev {
        final String deviceId;
        String os, appVersion, firstIp, lastIp, firstSeen, lastSeen;
        long reportCnt, totalSeconds;

        MutDev(String id, String os, String ver, String fIp, String lIp,
               String fSeen, String lSeen, long cnt, long secs) {
            this.deviceId = id;
            this.os = os;
            this.appVersion = ver;
            this.firstIp = fIp;
            this.lastIp = lIp;
            this.firstSeen = fSeen;
            this.lastSeen = lSeen;
            this.reportCnt = cnt;
            this.totalSeconds = secs;
        }
    }

}
