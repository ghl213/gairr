# GAIRR 统计服务（gairr-stats-server）

与 GAIRR 桌面端（GAIRR.exe）配套的**匿名使用统计 + 版本检查**服务：
桌面端每次启动自动上报「匿名设备ID + 已封存日的按天累计使用秒数 + IP」，
并顺带查询最新版本号。不上报任何用户可识别信息（无手机号 / 无用户名 / 无会话内容）。

- 技术栈：.NET 8（ASP.NET Core Minimal API，Kestrel），数据存 SQLite（`data/stats.db`）
- 数据目录：exe 同级 `data/`（启动自动创建）

## 1. 本地运行 / 冒烟测试

```bash
cd server/GAIRR.StatServer
dotnet run            # 默认监听 http://0.0.0.0:8300
# 或改端口：dotnet run -- --port 9000   /   环境变量 GAIRR_STATS_PORT=9000
```

健康检查：`curl http://127.0.0.1:8300/health`

模拟一次客户端上报（Windows cmd 下请用文件方式传 JSON，避免引号转义问题）：

```json
{ "deviceId": "abc123", "os": "windows", "appVersion": "0.2.0",
  "days": [ { "date": "2026-09-08", "seconds": 3600 } ] }
```

```bash
curl -X POST http://127.0.0.1:8300/api/report -H "Content-Type: application/json" --data-binary @report.json
```

汇总看板（JSON）：`curl http://127.0.0.1:8300/api/overview`（总设备数 / 总时长 / 近 30 日逐日活跃 / 最近设备）

## 2. 公网部署

目标是一台有公网 IP 的 Linux/Windows 服务器（与 GAIRR 主程序无关的独立进程）：

```bash
# Linux x64 单文件发布
cd server/GAIRR.StatServer
dotnet publish -c Release -r linux-x64 --self-contained false -o /opt/gairr-stats
# 服务方式运行（systemd 示例略），监听 0.0.0.0:8300，防火墙/反向代理放行 8300
```

建议在反向代理（nginx/caddy）后配 https，并把客户端 `ServerUrl` 指向该地址。

## 3. 版本检查配置

服务器读 `data/latest.json`（随发布目录带一份，可直接改，无需重编译）：

```json
{ "version": "0.2.0", "url": "https://你的下载页", "note": "本次更新内容" }
```

- `version`：最新版本号；客户端当前版本低于它时，返回 `hasUpdate: true`
- `url` 留空则不弹提示（只记录）；`note` 为更新说明
- 日常发版流程：更新完 GAIRR 桌面端版本号后，同步改服务器这份 json 即可

## 4. 客户端对接（GAIRR.exe）

客户端 `config.ini` 增加：

```ini
[Stats]
Enabled=1 ; 匿名使用统计总开关（0=本地仍按天计时，但不联网上报）
ServerUrl=http://127.0.0.1:8300 ; 改成公网部署地址
```

行为（均在 GAIRR 主窗口启动后进行，全部失败静默、绝不阻塞启动）：

1. 首次运行生成匿名 ID 存 `data/device_id`（随机 GUID，不含个人信息）；
2. 按天累计「窗口可见且非最小化」的使用秒数，落盘 `data/usage_daily.json`；
3. 每次启动把「今天之前」已封存日一次 POST 上报；服务端按 `device_id + date` 覆盖，天然幂等，
   上报成功后才从本地删除，网络失败数据保留到下次启动补报；
4. 同一响应回传最新版本，有新版且配了下载地址时弹窗提示一次。

本地统计口径注意：最小化/托盘驻留不计时长；退出前的最后几秒最多丢 10 秒（10 秒落盘一次 + 退出兜底落盘）。

## 5. 数据表

| 表 | 说明 | 键 |
|---|---|---|
| `devices` | 设备主档：首/末次 IP、首/末次活跃、上报次数、总秒数、系统与版本 | device_id |
| `usage` | 按设备+日期 的每日累计秒数（服务端只存最终累计值） | (device_id, date) |
