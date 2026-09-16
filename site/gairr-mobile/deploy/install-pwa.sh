#!/bin/bash
# ============================================
# PWA 部署到 Nginx 脚本
# 在中转站 (8.136.112.142) 上运行
# ============================================

set -e

PWA_SOURCE="/tmp/gairr-mobile"  # 上传后的PWA目录
NGINX_ROOT="/var/www/gairr-mobile"
NGINX_CONF="/etc/nginx/conf.d/gairr-mobile.conf"

echo "=== GAIRR PWA 部署 ==="

# 1. 检查 PWA 文件
if [ ! -d "$PWA_SOURCE" ]; then
    echo "❌ 未找到 PWA 文件: $PWA_SOURCE"
    echo "请先上传: scp -r gairr-mobile/ root@8.136.112.142:/tmp/"
    exit 1
fi

# 2. 复制到 Nginx 目录
echo "[1/3] 复制 PWA 文件..."
mkdir -p $NGINX_ROOT
cp -r $PWA_SOURCE/* $NGINX_ROOT/

# 3. 创建 Nginx 配置
echo "[2/3] 配置 Nginx..."
cat > $NGINX_CONF << 'EOF'
server {
    listen 80;
    server_name _;  # 接受所有域名/IP访问

    root /var/www/gairr-mobile;
    index index.html;

    # Gzip 压缩
    gzip on;
    gzip_types text/plain text/css application/json application/javascript text/xml;

    # 缓存静态资源
    location ~* \.(js|css|png|jpg|jpeg|gif|ico|svg)$ {
        expires 1d;
        add_header Cache-Control "public, immutable";
    }

    # PWA 必需: 处理路由
    location / {
        try_files $uri $uri/ /index.html;
    }

    # 安全头
    add_header X-Frame-Options "SAMEORIGIN" always;
    add_header X-Content-Type-Options "nosniff" always;
}

# GAIRR API 反向代理 (通过 frp 穿透后)
server {
    listen 80;
    server_name api.gairr.local;

    location / {
        proxy_pass http://127.0.0.1:8123;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;

        # SSE 支持
        proxy_buffering off;
        proxy_cache off;
        proxy_read_timeout 3600s;
    }
}
EOF

# 4. 测试并重载 Nginx
echo "[3/3] 重载 Nginx..."
nginx -t && nginx -s reload

echo ""
echo "✅ PWA 部署完成！"
echo ""
echo "=== 访问地址 ==="
echo "PWA 页面: http://8.136.112.142"
echo "API 接口: http://8.136.112.142:8123 (frp穿透后)"
echo ""
echo "=== iPhone 使用 ==="
echo "1. Safari 打开 http://8.136.112.142"
echo "2. 点击分享按钮 (底部 ↑)"
echo "3. 选择'添加到主屏幕'"
