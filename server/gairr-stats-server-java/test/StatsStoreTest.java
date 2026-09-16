import java.nio.charset.StandardCharsets;
import java.nio.file.DirectoryStream;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;

/**
 * StatsStoreTest —— 数据层单元自测（不进 jar，随 verify.cmd 运行）。
 *
 * 对应叶子验收标准：
 *   1) 同一 device+date 重复上报 → 覆盖而非累加；
 *   2) 多次上报后 devices 累计字段（reportCnt/totalSeconds/首末 IP/首末 seen/os 非空覆盖）
 *      与 .NET 口径手算结果逐一比对；
 *   3) 上报落盘后模拟重启（load 新实例）→ 数据不丢；
 *   4) stats.json 损坏 → 从零重建且可继续上报；
 *   5) 并发上报（8 线程 × 50 次）→ 计数/秒数精确无竞态；
 *   6) 字符串转义回环；7) 落盘后无 .tmp 残留。
 */
public class StatsStoreTest {

    private static int pass = 0, fail = 0;

    public static void main(String[] args) throws Exception {
        Path dir = Files.createTempDirectory("stats-store-test");

        testOverwriteAndCumulative(dir.resolve("case1"));
        testRestartPersistence(dir.resolve("case2"));
        testCorruptRecovery(dir.resolve("case3"));
        testConcurrency(dir.resolve("case4"));
        testEscapingRoundTrip(dir.resolve("case5"));

        System.out.printf("---- RESULT: pass=%d fail=%d%n", pass, fail);
        if (fail > 0) System.exit(1);
        System.out.println("STATS STORE TEST OK");
    }

    /** 覆盖上报 + devices 累计口径（与 .NET 手算一致）。 */
    private static void testOverwriteAndCumulative(Path dir) throws Exception {
        Files.createDirectories(dir);
        StatsStore st = StatsStore.load(dir);

        int n1 = st.recordReport("dev-A", "win", "1.0", "1.1.1.1", "2026-09-09T10:00:00",
                List.of(new StatsStore.Day("2026-09-09", 100)));
        eq("首次上报 received=1", 1, n1);

        int n2 = st.recordReport("dev-A", "", "2.0", "2.2.2.2", "2026-09-09T11:00:00",
                List.of(new StatsStore.Day("2026-09-09", 150),   // 同键覆盖：100 -> 150
                        new StatsStore.Day("2026-09-10", 30),
                        new StatsStore.Day("bad-date", 999),      // 非法行跳过
                        new StatsStore.Day("2026-09-11", -5)));   // 负数跳过
        eq("二次上报 received=2（非法行跳过）", 2, n2);

        // usage 覆盖而非累加
        eq("usage 条数=2", 2, st.usage().size());
        eq("usage 同键覆盖=150", 150, usageOf(st, "dev-A", "2026-09-09"));
        eq("usage 新日期=30", 30, usageOf(st, "dev-A", "2026-09-10"));

        // devices 累计（.NET 口径手算）：reportCnt=2，totalSeconds=100+150+30=280，
        // os 空不覆盖仍 win，appVersion 覆盖为 2.0，firstIp/firstSeen 保持首值，last* 刷新
        eq("设备数=1", 1, st.deviceCount());
        StatsStore.Dev d = st.devices().get(0);
        eq("reportCnt=2", 2, d.reportCnt());
        eq("totalSeconds=280（首报 100 + 次报 180）", 280, d.totalSeconds());
        eq("os 空值不覆盖=win", "win", d.os());
        eq("appVersion 非空覆盖=2.0", "2.0", d.appVersion());
        eq("firstIp 保持=1.1.1.1", "1.1.1.1", d.firstIp());
        eq("lastIp 刷新=2.2.2.2", "2.2.2.2", d.lastIp());
        eq("firstSeen 保持", "2026-09-09T10:00:00", d.firstSeen());
        eq("lastSeen 刷新", "2026-09-09T11:00:00", d.lastSeen());
        eq("totalSeconds() 汇总=280", 280, st.totalSeconds());
        noTmpLeft(dir, "上报后无 tmp 残留");
    }

    /** 上报落盘后模拟进程重启（新实例 load）→ 数据不丢。 */
    private static void testRestartPersistence(Path dir) throws Exception {
        Files.createDirectories(dir);
        StatsStore st = StatsStore.load(dir);
        st.recordReport("dev-A", "win", "1.0", "1.1.1.1", "2026-09-09T10:00:00",
                List.of(new StatsStore.Day("2026-09-09", 100)));
        st.recordReport("dev-B", "mac", "3.0", "3.3.3.3", "2026-09-09T12:00:00",
                List.of(new StatsStore.Day("2026-09-09", 40), new StatsStore.Day("2026-09-08", 60)));

        // 模拟重启：丢弃内存，仅从文件重建
        StatsStore re = StatsStore.load(dir);
        eq("重启后设备数=2", 2, re.deviceCount());
        eq("重启后总秒数=200", 200, re.totalSeconds());
        eq("重启后 usage 条数=3", 3, re.usage().size());
        StatsStore.Dev b = re.devices().stream().filter(x -> x.deviceId().equals("dev-B")).findFirst().orElseThrow();
        eq("重启后 dev-B reportCnt=1", 1, b.reportCnt());
        eq("重启后 dev-B totalSeconds=100", 100, b.totalSeconds());
        eq("重启后继续上报累计", 2,
                re.recordReport("dev-B", "mac", "3.1", "4.4.4.4", "2026-09-10T09:00:00",
                        List.of(new StatsStore.Day("2026-09-09", 55))));
        eq("覆盖后 dev-B totalSeconds=115", 115, re.devices().stream()
                .filter(x -> x.deviceId().equals("dev-B")).findFirst().orElseThrow().totalSeconds());
    }

    /** 文件损坏 → 从零重建；继续上报可用且重新落盘。 */
    private static void testCorruptRecovery(Path dir) throws Exception {
        Files.createDirectories(dir);
        StatsStore st = StatsStore.load(dir);
        st.recordReport("dev-A", "win", "1.0", "1.1.1.1", "2026-09-09T10:00:00",
                List.of(new StatsStore.Day("2026-09-09", 100)));
        Files.writeString(dir.resolve("stats.json"), "{corrupted!!!", StandardCharsets.UTF_8);

        StatsStore broken = StatsStore.load(dir);
        eq("损坏后从零：设备数=0", 0, broken.deviceCount());
        eq("损坏后从零：usage=0", 0, broken.usage().size());
        broken.recordReport("dev-C", "linux", "0.9", "5.5.5.5", "2026-09-09T13:00:00",
                List.of(new StatsStore.Day("2026-09-09", 10)));
        eq("重建后可继续上报", 1, broken.deviceCount());
        StatsStore re = StatsStore.load(dir);
        eq("重建落盘可再加载", 1, re.deviceCount());

        // 半损坏：JSON 合法但记录字段类型错乱 → 坏行跳过，好行保留
        Files.writeString(dir.resolve("stats.json"),
                "{\"devices\":[{\"deviceId\":\"ok\",\"os\":123,\"reportCnt\":\"x\"},"
                        + "{\"deviceId\":\"\",\"os\":\"ghost\"},{\"os\":\"noid\"}],"
                        + "\"usage\":[{\"deviceId\":\"ok\",\"date\":\"2026-09-09\",\"seconds\":7},"
                        + "{\"deviceId\":\"ok\",\"date\":\"bad\",\"seconds\":9}]}",
                StandardCharsets.UTF_8);
        StatsStore half = StatsStore.load(dir);
        eq("半损坏容错：坏行跳过好行保留", 1, half.deviceCount());
        eq("半损坏容错：os 类型错默认空", "", half.devices().get(0).os());
        eq("半损坏容错：合法 usage 保留", 1, half.usage().size());
    }

    /** 并发上报：8 线程 × 50 次同设备同日期（每次 seconds=1）→ 精确计数无竞态。 */
    private static void testConcurrency(Path dir) throws Exception {
        Files.createDirectories(dir);
        StatsStore st = StatsStore.load(dir);
        int threads = 8, rounds = 50;
        ExecutorService pool = Executors.newFixedThreadPool(threads);
        CountDownLatch start = new CountDownLatch(1);
        AtomicInteger errors = new AtomicInteger();
        for (int t = 0; t < threads; t++) {
            pool.submit(() -> {
                try {
                    start.await();
                    for (int r = 0; r < rounds; r++)
                        st.recordReport("dev-C", "win", "1.0", "9.9.9.9", "2026-09-09T10:00:00",
                                List.of(new StatsStore.Day("2026-09-09", 1)));
                } catch (Exception e) {
                    errors.incrementAndGet();
                    e.printStackTrace();
                }
            });
        }
        start.countDown();
        pool.shutdown();
        ok("并发线程全部完成", pool.awaitTermination(60, TimeUnit.SECONDS));
        eq("并发无异常", 0, errors.get());
        eq("并发 reportCnt=400", threads * rounds, st.devices().get(0).reportCnt());
        eq("并发 totalSeconds=400", threads * rounds, st.totalSeconds());
        eq("并发 usage 覆盖=1", 1, usageOf(st, "dev-C", "2026-09-09"));
        StatsStore re = StatsStore.load(dir);
        eq("并发落盘重启一致 reportCnt=400", threads * rounds, re.devices().get(0).reportCnt());
    }

    /** 字符串转义回环：引号/反斜杠/换行/中文/控制字符落盘后原样恢复。 */
    private static void testEscapingRoundTrip(Path dir) throws Exception {
        Files.createDirectories(dir);
        StatsStore st = StatsStore.load(dir);
        String tricky = "os\"\\\n\t中文x";
        st.recordReport("dev-E", tricky, "v\"1", "ip\r\n7", "2026-09-09T10:00:00",
                List.of(new StatsStore.Day("2026-09-09", 5)));
        StatsStore re = StatsStore.load(dir);
        StatsStore.Dev d = re.devices().get(0);
        eq("转义回环 os", tricky, d.os());
        eq("转义回环 appVersion", "v\"1", d.appVersion());
        eq("转义回环 lastIp", "ip\r\n7", d.lastIp());
    }

    private static long usageOf(StatsStore st, String deviceId, String date) {
        return st.usage().stream()
                .filter(u -> u.deviceId().equals(deviceId) && u.date().equals(date))
                .mapToLong(StatsStore.Use::seconds).findFirst().orElse(-1);
    }

    private static void noTmpLeft(Path dir, String name) throws Exception {
        boolean clean;
        try (DirectoryStream<Path> ds = Files.newDirectoryStream(dir, "*.tmp")) {
            clean = !ds.iterator().hasNext();
        }
        ok(name, clean);
    }

    private static void eq(String name, long expect, long actual) {
        if (expect == actual) { pass++; System.out.println("PASS  " + name); }
        else { fail++; System.out.println("FAIL  " + name + " (expect=" + expect + ", actual=" + actual + ")"); }
    }

    private static void eq(String name, String expect, String actual) {
        if (expect.equals(actual)) { pass++; System.out.println("PASS  " + name); }
        else { fail++; System.out.println("FAIL  " + name + " (expect=" + printable(expect) + ", actual=" + printable(actual) + ")"); }
    }

    private static void ok(String name, boolean cond) {
        if (cond) { pass++; System.out.println("PASS  " + name); }
        else { fail++; System.out.println("FAIL  " + name); }
    }

    private static String printable(String s) {
        return s == null ? "null" : s.replace("\n", "\\n").replace("\r", "\\r").replace("\t", "\\t");
    }
}
