#!/bin/bash
# ============================================
# frps (中转站服务端) 一键安装脚本
# 运行环境: Linux (CentOS/Ubuntu/Debian)
# 目标服务器: 8.136.112.142
# ============================================

set -e

FRP_VERSION="0.58.1"
FRP_DIR="/opt/frp"
TOKEN="gairr-frp-token-2024"  # 建议修改为你的强密码

echo "=== GAIRR 中转站 - frps 安装 ==="

# 1. 创建目录
mkdir -p $FRP_DIR
cd $FRP_DIR

# 2. 下载 frp
echo "[1/5] 下载 frp v${FRP_VERSION}..."
if [ ! -f "frp_${FRP_VERSION}_linux_amd64.tar.gz" ]; then
    wget -q https://github.com/fatedier/frp/releases/download/v${FRP_VERSION}/frp_${FRP_VERSION}_linux_amd64.tar.gz
fi

# 3. 解压
echo "[2/5] 解压..."
tar -xzf frp_${FRP_VERSION}_linux_amd64.tar.gz
ln -sf frp_${FRP_VERSION}_linux_amd64 current

# 4. 创建配置文件
echo "[3/5] 创建配置..."
cat > $FRP_DIR/frps.toml << EOF
# frps 服务端配置
bindPort = 7000
auth.token = "${TOKEN}"

# Web 管理面板 (可选)
webServer.addr = "0.0.0.0"
webServer.port = 7500
webServer.user = "admin"
webServer.password = "gairr-admin-2024"

# 日志
log.to = "/var/log/frps.log"
log.level = "info"
log.maxDays = 30
EOF

# 5. 创建 systemd 服务
echo "[4/5] 创建系统服务..."
cat > /etc/systemd/system/frps.service << EOF
[Unit]
Description=FRP Server (GAIRR)
After=network.target

[Service]
Type=simple
ExecStart=${FRP_DIR}/current/frps -c ${FRP_DIR}/frps.toml
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
EOF

# 6. 启动服务
echo "[5/5] 启动 frps..."
systemctl daemon-reload
systemctl enable frps
systemctl restart frps

# 7. 检查状态
sleep 2
if systemctl is-active --quiet frps; then
    echo "✅ frps 启动成功！"
    echo ""
    echo "=== 配置信息 ==="
    echo "服务端端口: 7000"
    echo "Token: ${TOKEN}"
    echo "管理面板: http://8.136.112.142:7500"
    echo "管理账号: admin / gairr-admin-2024"
    echo ""
    echo "=== 防火墙规则 ==="
    echo "请确保以下端口已开放:"
    echo "  - 7000 (frp 主端口)"
    echo "  - 7500 (管理面板，可选)"
    echo "  - 8123 (GAIRR API，穿透后)"
else
    echo "❌ frps 启动失败，请检查日志: journalctl -u frps -f"
    exit 1
fi
