# GAIRR 移动端部署指南

## 架构

```
iPhone (Safari PWA) → 中转站 (8.136.112.142:80) → frp → 工作电脑 (GAIRR.Server:8123)
```

## 部署步骤

### 第一步：中转站部署 frps

```bash
# 1. 上传 install-frps.sh 到服务器
scp deploy/install-frps.sh root@8.136.112.142:/tmp/

# 2. SSH 登录并执行
ssh root@8.136.112.142
chmod +x /tmp/install-frps.sh
sudo /tmp/install-frps.sh

# 3. 开放防火墙端口
sudo firewall-cmd --permanent --add-port=7000/tcp
sudo firewall-cmd --permanent --add-port=7500/tcp
sudo firewall-cmd --permanent --add-port=8123/tcp
sudo firewall-cmd --reload
```

### 第二步：部署 PWA 到 Nginx

```bash
# 1. 上传 PWA 文件
scp -r site/gairr-mobile/* root@8.136.112.142:/tmp/gairr-mobile/

# 2. 上传并执行部署脚本
scp deploy/install-pwa.sh root@8.136.112.142:/tmp/
ssh root@8.136.112.142 "chmod +x /tmp/install-pwa.sh && sudo /tmp/install-pwa.sh"
```

### 第三步：工作电脑配置

```powershell
# 1. 复制配置文件到工作电脑
# 将 deploy/frpc.toml 和 deploy/start-frpc.bat 放到同一目录

# 2. 运行启动脚本
.\start-all.bat
```

## 文件清单

```
deploy/
├── install-frps.sh      # 中转站 frps 安装脚本
├── install-pwa.sh       # PWA 部署到 Nginx 脚本
├── frpc.toml            # 工作电脑 frpc 配置
├── start-frpc.bat       # 工作电脑 frpc 启动
└── start-all.bat        # 工作电脑一键启动所有服务
```

## 访问方式

| 端 | 地址 | 说明 |
|---|------|------|
| PWA 页面 | http://8.136.112.142 | iPhone Safari 访问 |
| GAIRR API | http://8.136.112.142:8123 | 通过 frp 穿透 |
| frp 管理面板 | http://8.136.112.142:7500 | 查看连接状态 |

## iPhone 添加到主屏幕

1. Safari 打开 http://8.136.112.142
2. 点击底部分享按钮 ↑
3. 选择"添加到主屏幕"
4. 点击添加

## 故障排查

| 问题 | 检查 |
|------|------|
| PWA 无法访问 | 检查 Nginx: `systemctl status nginx` |
| API 无法连接 | 检查 frps: `systemctl status frps` |
| 本地服务正常但公网不通 | 检查 frpc 是否运行 |
| 防火墙问题 | 确认 7000/8123 端口开放 |
