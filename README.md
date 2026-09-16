<!-- GAIRR（概尔工栈）开源版 README：SEO 增强版，中英关键词覆盖 AI agent / automation / skills / plugins / CLI 等检索入口 -->

** 由于文件较大，请使用  git clone https://github.com/ghl213/gairr.git 方式下载 **

# GAIRR — 本地优先的 AI 开发助手（Local-First AI Development Agent）

**GAIRR（概尔工栈）** 是运行在 Windows 上的桌面级 AI 开发 Agent：以自然语言驱动**代码检索 → 修改 → 编译验证 → 结果总结**的全流程，帮你把"查代码、改代码、跑通构建"从手动操作变成一句话任务。本地优先（Local-First），可离线的桌面端 + Agent 引擎 + 匿名使用统计服务。

- 🖥️ **GUI 桌面端**：对话式开发，支持网页模型通道（免 ApiKey，登录态自动持久化）
- ⚙️ **Agent 引擎**：任务规划、工具调用、会话存储、多 Agent 拆分编排并行执行
- 🧩 **技能与插件生态**：`SKILL.md` 技能标准 + 即装即用的插件市场（Marketplace），支持从 GitHub 安装社区技能
- 🔍 **语义代码定位**：项目地图 / 符号检索 / 语义搜索（SmartSearch），**中文查询同样可用**
- 💻 **CLI 无头模式**：脚本与计划任务友好，无需界面
- ⏰ **自动任务**：每日 / 工作日 / 每周 / 每月 / Cron 周期触发，项目级隔离

GAIRR 将 GUI、命令行、Agent 引擎、工具与技能生态整合为**一个本地运行的 AI 自动化开发助手**。主体代码以 Apache-2.0 开源；核心对话引擎以编译后的二进制随发布包分发，接口与功能不受影响。

## 快速开始（Quick Start）

### 环境要求

- Windows 10/11（x64）
- 从源码构建：.NET 8 SDK
- 仅运行 CLI（框架依赖版）：.NET 8 运行时

### 从源码构建 / 运行

```bat
:: 1. 构建（仓库根目录）
build.bat

:: 2. 运行 CLI（无头模式）
publish\cli\gairr-cli.exe -p <项目根目录> --ask "任务描述"

:: 3. 或双击 publish\GAIRR.exe 打开 GUI
```

构建产物：
- `publish\GAIRR.exe` —— GUI 单文件版（自包含，无需预装运行时）
- `publish\cli\gairr-cli.exe` —— CLI 无头宿主（框架依赖版，体积小）

### 配置（config.ini）

程序读取 exe 同目录的 `config.ini`（仅本地保存，仓库内不含任何真实密钥）。在 `[Bailian]` / `[DeepSeek]` / `[Kimi]` 等节填入你自己的 ApiKey 即可接入对应模型；统计服务地址在 `[Stats] ServerUrl`。

## 功能一览

| 能力 | 说明 |
|---|---|
| 对话式开发 | 自然语言下达任务，Agent 自动检索→改码→编译验证→总结 |
| 语义代码定位 | 项目地图 / 符号检索 / 语义搜索（SmartSearch），中文查询也可 |
| 文件引用标记 | 输入 `@文件路径` 直接引用文件，`@dir` 引用目录 |
| 技能（Skill） | `skills/<名>/SKILL.md` 定义流程，命中描述自动触发，或 `/技能名` 强制触发 |
| 插件（Plugin） | `plugins/<名>/` 挂载自定义工具，可即装即用 |
| 市场（Marketplace） | 内置市场目录一键安装技能/插件；支持从 GitHub 安装社区 SKILL.md 规范技能 |
| 网页模型通道 | 内嵌网页窗口驱动真实模型站点（免 ApiKey，登录态自动持久化） |
| 多 Agent 编排 | 按功能架构树拆解任务，子 Agent 并行执行、审查汇总 |
| 自动任务 | 每日/工作日/每周/每月/Cron 周期触发后台任务，项目级隔离 |
| 消息推送 | 钉钉群消息插件（可选） |
| CLI 无头模式 | 脚本/计划任务友好，无需界面 |

## 目录结构

| 路径 | 说明 |
|---|---|
| app/GAIRR | WPF 桌面端（主界面、网页模型通道、使用统计） |
| app/GAIRR.Agent | Agent 引擎（任务规划、工具调用、会话存储） |
| app/GAIRR.Cli | 命令行宿主 |
| server/gairr-stats-server-java | 匿名使用统计与版本检查服务（纯 JDK，零第三方依赖） |
| site | 官网与移动端页面 |

## 构建

- 桌面端：`dotnet build GAIRR.sln`（需 .NET SDK）
- 统计服务：`cd server\gairr-stats-server-java && build.cmd`（需 JDK 17+，仅用 javac + jar）

## 文档

- [GAIRR-使用说明.md](GAIRR-使用说明.md)：功能 / 安装 / 使用详细说明
- [LICENSE](LICENSE)：Apache-2.0 开源许可证
- [GAIRR.Agent-EULA.txt](GAIRR.Agent-EULA.txt)：核心引擎终端用户许可协议

## 许可

[Apache License 2.0](LICENSE)，详见 LICENSE 文件。
