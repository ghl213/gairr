# GAIRR 移动端部署指南（SSH 隧道版）

## 架构

```
iPhone (Safari PWA) → 中转站:80 (Nginx) → 中转站:8123 (SSH隧道) → 工作电脑:8123 (GAIRR.Server)
```

## 前置要求

- 工作电脑已安装 OpenSSH 客户端（Windows 10/11 自带）
- 服务器已配置 SSH 密钥登录（免密码）

## 部署步骤

### 第一步：部署 PWA 到 Nginx（已完成）

PWA 页面已可通过 http://8.136.112.142 访问

### 第二步：工作电脑启动 SSH 隧道

```powershell
# 方式1: 直接运行脚本
.\start-ssh-tunnel.bat

# 方式2: 手动执行
ssh -R 8123:localhost:8123 root@8.136.112.142 -N
```

参数说明：
- `-R 8123:localhost:8123`：将服务器的 8123 端口映射到本地 8123 端口
- `-N`：不执行远程命令，仅做端口转发
- `-o ServerAliveInterval=60`：每60秒发送心跳包保持连接

### 第三步：iPhone 配置

1. Safari 打开 http://8.136.112.142
2. 点击 ⚙ 配置：
   - 服务器地址：`http://8.136.112.142:8123`
   - Token：你的 GAIRR Server Token（config.ini 中配置）
   - 项目路径：`d:\work\gairr`
3. 点击连接

## 访问地址

| 端 | 地址 | 说明 |
|---|------|------|
| PWA 页面 | http://8.136.112.142 | iPhone Safari 访问 |
| GAIRR API | http://8.136.112.142:8123 | 通过 SSH 隧道 |

## 保持连接

SSH 隧道需要保持运行，建议：

1. **使用 screen/tmux**（服务器端）保持会话
2. **使用 autossh**（可选）自动重连
3. **Windows 计划任务** 开机自动启动隧道

## 故障排查

| 问题 | 解决 |
|------|------|
| 连接超时 | 检查 SSH 隧道是否运行 |
| 鉴权失败 | 检查 Token 是否正确 |
| 端口被占 | 检查是否有其他程序占用 8123 |
