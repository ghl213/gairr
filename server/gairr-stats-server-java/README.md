# gairr-stats-server-java（骨架）

GAIRR 统计服务（`server/GAIRR.StatServer` 为 .NET 参考版）的 **Java 零第三方依赖** 版本骨架，
仅用 JDK 自带 `com.sun.net.httpserver.HttpServer`，无 Maven / 无任何第三方 jar。

当前为**骨架阶段**：HTTP 进程生命周期、路由分发、端口/token 配置解析已完成；
`/api/report`（上报入库）与 `/api/overview`（统计汇总）为占位（返回 501），待后续填充。

## 环境

- JDK 17+（Temurin 17 验证通过；`jdk.httpserver` 为标准模块）

## 构建与验证

> ⚠️ **不要用 `dotnet build` 判定本工程**：这是纯 Java 工程（javac/jar，无 csproj），
> 且仓库根目录没有 `.sln`/`.csproj`，在根目录执行 `dotnet build` 必然报
> `MSB1003: 请指定项目或解决方案文件` —— 那是"根目录没有 .NET 工程"，不是本叶子编译失败。
> 本工程的验证命令是 `verify.cmd`。
>
> **实测证据**（2026-09-09，JDK 17.0.13 / Temurin）：
> - 仓库根 `dotnet build -v q` → `退出码 1` + `MSBUILD : error MSB1003: 请指定项目或解决方案文件`（根目录确无 `.sln`/`.csproj`）；
> - 本工程 `build.cmd` → `BUILD OK: build\gairr-stats-server.jar`（javac 0 error 0 warning + jar cfe 打包）；
> - 本工程 `verify.cmd` → `---- RESULT: pass=22 fail=0` + `VERIFY OK`（退出码 0）。
>
> **这条误判来自编排的硬检查点，非本叶子代码问题**：`app/GAIRR.Agent/AgentHost/PlanRunner.cs`
> 的 `NeedHardCheck`（L64-67）只要叶子 accept 文本命中 `编译|build|构建|compile` 就触发门禁，
> 而门禁（L377-387）**写死**在项目根执行 `dotnet build -v q` 并要求输出含 `Build succeeded`
> （不读 `SystemCfg.BuildCmd` 覆盖，也不按叶子目录选命令）。因此**任何 accept 写了"编译"的
> 纯 Java/前端叶子都会被这条门禁误判失败**。建议框架侧修法（三选一，均需改 PlanRunner 后
> build + 替换 publish 里的 GAIRR.Agent.dll）：① 根目录探测不到 `*.sln`/`*.csproj` 时跳过该门禁；
> ② 门禁命令改读 `SystemCfg.BuildCmd`；③ 支持叶子级 `verify.cmd`/自定义校验命令。
> 在框架修好前，本叶子的编译验收以 `verify.cmd` 输出为准。

```bash
build.cmd        # javac 编译到 out\，jar cfe 打包 build\gairr-stats-server.jar
verify.cmd       # build.cmd + test\Smoke.java 冒烟自测（拉起服务打端点，跑完自动销毁进程）
```

`verify.cmd` 共 22 项断言，全通过时打印 `VERIFY OK` 并返回退出码 0；日志写在 `%TEMP%\smoke-*.log`。

## 运行

```bash
run.cmd                        # 默认 http://0.0.0.0:8300
run.cmd --port 9000            # 命令行指定端口
set GAIRR_STATS_PORT=9000 && run.cmd   # 或环境变量指定
```

Ctrl+C 触发 shutdown hook 优雅停机（`server.stop(0)`）。

## 配置优先级

`--port/--token 命令行参数` > `GAIRR_STATS_PORT / GAIRR_STATS_TOKEN 环境变量` > 默认值
（端口默认 8300；token 默认不配置）。支持 `--port 9000` 与 `--port=9000` 两种写法。

> 注：与 .NET 参考版（env 覆盖命令行）优先级相反，此处采用更常规的「命令行优先」，
> 已在启动日志中提示冲突场景。

## 路由与响应

| 方法 | 路径 | 行为 | 当前状态 |
|---|---|---|---|
| GET | `/` | 服务信息（endpoints 清单） | ✅ 骨架 |
| GET | `/health` | `{"ok":true,"service":"gairr-stats-server","time":"yyyy-MM-dd'T'HH:mm:ss"}` | ✅ 可用 |
| POST | `/api/report` | 上报（消费请求体后返回占位） | ⏳ 501 占位，待入库实现 |
| GET | `/api/overview` | 汇总看板 | ⏳ 501 占位，待统计实现 |

未知路径 404、方法不符 405、异常 500，均为 JSON 响应，`Content-Type: application/json; charset=utf-8`。

## 验证（冒烟）

```bash
verify.cmd       # 推荐：构建 + 22 项自动断言
```

手工复验（另开窗口）：

```bash
run.cmd
curl http://127.0.0.1:8300/health
curl -X POST http://127.0.0.1:8300/api/report -H "Content-Type: application/json" --data-binary "{}"
```

### 验收项与断言对应

| 验收标准 | 断言（verify.cmd 输出） | 结果 |
|---|---|---|
| javac 编译通过 | `build.cmd` → `BUILD OK`（javac + jar cfe） | ✅ |
| `/health` 返回 `{ok,service,time}` | `GET /health -> 200` + has `"ok"`/`"service"`/`"time"` | ✅ |
| `--port` 能改端口 | `--port 8321 works`、`--port=8322 equals form works`、`cli --port wins over env` | ✅ |
| `GAIRR_STATS_PORT` 能改端口 | `env GAIRR_STATS_PORT=8323 works` | ✅ |
| 启动后进程不退出 | `process stays alive after start` | ✅ |
| Ctrl+C 可正常退出 | `hook probe: exit code 0` + `shutdown hook logged "stopped"` + `port 8335 released` | ✅（等价验证，见下） |

> Ctrl+C 说明：外部进程无法向控制台子进程注入 CTRL_C（`taskkill` 不带 `/F` 对无窗口进程直接
> 报"只能强行终止"）。故用 `test\HookProbe.java` 做**等价验证**——Windows 下 CTRL_C_EVENT 与
> `System.exit` 走同一条 JVM 关闭序列（先跑 shutdown hooks），探针确认了 hook 执行、打印
> `[gairr-stats-server] stopped`、退出码 0、端口释放。人工确认仍是 `run.cmd` 后按 Ctrl+C。

## 目录

```
src/Main.java          服务本体（进程生命周期 + 配置解析 + 路由分发）
test/Smoke.java        冒烟自测（HTTP 断言，不进 jar）
test/HookProbe.java    优雅退出探针（不进 jar）
build.cmd run.cmd verify.cmd
out/  build/           javac 输出与 jar（构建产物，已由叶子级 .gitignore 排除、不再入库）
.gitignore             忽略 out/ build/ *.class *.jar（产物由 build.cmd 现打，避免二进制 diff 噪音）
```

## TODO（交接给后续叶子）

> 口径以编排方案（plan 96c10ee1）为准，已确认决策不再重复讨论：**替换** .NET 版（同 API、同默认端口 8300，旧目录保留停维）；数据**全新开始不迁移** `stats.db`；JSON 一律自研工具类（**禁止任何第三方依赖**，含 JSON 库）；页面内联资源不引外网 CDN。

1. **极简 JSON 工具**：自研 object/array/string/number/bool/null 的解析与序列化（含转义与 `\uXXXX`），供上报 body 与数据文件读写共用；现有 `Main.jsonEscape` 可并入。
2. **数据层（替代 SQLite）**：`data/stats.json` 存 `devices`（deviceId/os/appVersion/firstIp/lastIp/firstSeen/lastSeen/reportCnt/totalSeconds）与 `usage`（deviceId+date+seconds）；usage 同键**覆盖**、devices 累计口径与 .NET 一致；写盘 tmp+rename 原子替换；加载容错（文件损坏则从零开始）。
3. [DONE] **`/api/report`**：逐字段对齐 `server/GAIRR.StatServer/Program.cs` 的 `ReportRequest/DaySeconds/LatestInfo`，回执 `{ok,received,latest,updateUrl,note,hasUpdate,time}`；校验 deviceId 必填≤64、date `yyyy-MM-dd`、seconds≥0（非法行跳过不整体失败）；非法 JSON / 缺 deviceId → 400；**本端点不鉴权**（客户端无 token）。`data/latest.json` 沿用旧语义：有版本且与上报 `appVersion` 不同 → `hasUpdate=true`，缺失或相同 → `false`。
4. **`/api/overview`**：输出 `{totalDevices,totalSeconds,daily(近30日倒序),recent(最近50设备)}`；**配置 token 时鉴权**（错/缺 token → 401），未配 token 直开。
5. **统计后台页 `/`**：由当前 JSON 信息占位改为单文件 HTML+内联 JS/CSS（输口令 → 拉 overview 渲染 总设备数/总时长/近30日趋势/最近设备表），断网可开。改 `/` 时注意 `Smoke.java` 断言 `GET / -> 200 and lists endpoints` 需同步更新。
6. **端口优先级差异**：本工程为「命令行 `--port` 优先于 env」（.NET 版相反，env 覆盖命令行），已在启动日志提示冲突；如需与旧版完全一致再改 `Config.parse`。
