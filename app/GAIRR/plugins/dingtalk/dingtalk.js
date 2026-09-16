/**
 * 钉钉机器人通知脚本（插件自包含版）
 * 使用内置模块：crypto, https, fs, url
 * 用法：node dingtalk.js "标题" "内容" [config.ini路径]
 * webhook/密钥优先从传入的 config.ini [Hooks] 节读取；
 * 若未传入或读取失败，则回退到同目录下的 config.ini。
 */
const crypto = require('crypto');
const https = require('https');
const fs = require('fs');
const path = require('path');
const urlModule = require('url');

/**
 * 从 config.ini 读取 [Hooks] 节的 DingTalkWebhook / DingTalkSecret
 */
function loadDingTalkConfig(configPath) {
  if (!configPath || !fs.existsSync(configPath)) {
    throw new Error('找不到配置文件: ' + (configPath || '(未提供 config.ini 路径)'));
  }
  const lines = fs.readFileSync(configPath, 'utf8').split(/\r?\n/);
  let section = '';
  let webhook = '', secret = '';
  for (const raw of lines) {
    const line = raw.trim();
    if (line.length === 0 || line.startsWith(';')) continue;
    // 去行内注释（; 及之后）
    const ci = line.indexOf(';');
    const content = (ci >= 0 ? line.substring(0, ci) : line).trim();
    if (content.length === 0) continue;
    if (content.startsWith('[') && content.endsWith(']')) {
      section = content.substring(1, content.length - 1).trim();
      continue;
    }
    if (section.toLowerCase() !== 'hooks') continue;
    const ei = content.indexOf('=');
    if (ei <= 0) continue;
    const key = content.substring(0, ei).trim().toLowerCase();
    const val = content.substring(ei + 1).trim();
    if (key === 'dingtalkwebhook') webhook = val;
    else if (key === 'dingtalksecret') secret = val;
  }
  if (!webhook) throw new Error('config.ini [Hooks] 中未配置 DingTalkWebhook');
  return { webhook, secret };
}

/**
 * 生成加签（无密钥时返回空）
 */
function genSign(timestamp, secret) {
  if (!secret) return '';
  const str = timestamp + '\n' + secret;
  const hmac = crypto.createHmac('sha256', secret);
  hmac.update(str);
  return encodeURIComponent(hmac.digest('base64'));
}

/**
 * 发送消息
 */
function sendMessage(title, content, webhook, secret) {
  return new Promise((resolve, reject) => {
    const timestamp = Date.now();

    // 处理 \n 转义为真实换行
    const realContent = content.replace(/\\n/g, '\n');

    const parsedUrl = urlModule.parse(webhook);
    let requestUrl = `${parsedUrl.protocol}//${parsedUrl.host}${parsedUrl.path}`;
    if (secret) {
      requestUrl += `&timestamp=${timestamp}&sign=${genSign(timestamp, secret)}`;
    }

    const body = JSON.stringify({
      msgtype: 'markdown',
      markdown: {
        title: title,
        text: `## ${title}\n\n${realContent}`
      }
    });

    const options = {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Content-Length': Buffer.byteLength(body)
      }
    };

    const req = https.request(requestUrl, options, (res) => {
      let data = '';
      res.on('data', (chunk) => { data += chunk; });
      res.on('end', () => {
        try {
          const result = JSON.parse(data);
          if (result.errcode === 0) {
            console.log('✅ 钉钉消息发送成功');
            resolve(result);
          } else {
            console.error('❌ 钉钉消息发送失败:', result.errmsg);
            reject(new Error(result.errmsg));
          }
        } catch (e) {
          console.error('❌ 响应解析失败:', data);
          reject(e);
        }
      });
    });

    req.setTimeout(10000, () => {
      req.destroy();
      reject(new Error('请求超时（10s）'));
    });

    req.on('error', (err) => {
      console.error('❌ 请求失败:', err.message);
      reject(err);
    });

    req.write(body);
    req.end();
  });
}

// 命令行参数：标题 内容 [config.ini路径]
const args = process.argv.slice(2);
if (args.length < 2) {
  console.error('用法: node dingtalk.js "标题" "内容" [config.ini路径]');
  process.exit(1);
}

const [title, rawContent] = args;
// 空正文拦截：把字面 \n 还原为真实换行后仍无实质内容时拒绝发送，避免发出只有标题的空壳消息
const content = rawContent.replace(/\\n/g, '\n');
if (!content || !content.trim()) {
  console.error('❌ 消息内容为空，拒绝发送（请检查调用方是否传入了正文）');
  process.exit(2);
}
// 默认使用同目录 config.ini；调用方通过 {config} 传入全局 config.ini 时优先使用
const configPath = args[2] || path.join(__dirname, 'config.ini');

try {
  const { webhook, secret } = loadDingTalkConfig(configPath);
  sendMessage(title, content, webhook, secret)
    .then(() => process.exit(0))
    .catch((err) => {
      console.error('发送失败:', err.message);
      process.exit(1);
    });
} catch (err) {
  console.error('配置读取失败:', err.message);
  process.exit(1);
}
