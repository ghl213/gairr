/**
 * Reddit 数据源
 * 职责：从 r/technology 热榜拉取热门科技帖子
 */

const https = require('https');
const http = require('http');
const { URL } = require('url');

const SUBREDDIT = 'technology';
const API_URL = `https://www.reddit.com/r/${SUBREDDIT}/hot.json?limit=50`;

/**
 * 读取环境代理（HTTP_PROXY / HTTPS_PROXY）
 */
function getProxyUrl() {
  return process.env.HTTPS_PROXY || process.env.https_proxy ||
         process.env.HTTP_PROXY || process.env.http_proxy || '';
}

/**
 * 通过 CONNECT 隧道建立 HTTPS over HTTP 代理请求
 */
function httpsGetViaProxy(targetUrl, proxyUrl, timeoutMs, headers) {
  return new Promise((resolve, reject) => {
    const target = new URL(targetUrl);
    const proxy = new URL(proxyUrl);
    const connectReq = http.request({
      host: proxy.hostname,
      port: proxy.port || 80,
      method: 'CONNECT',
      path: `${target.hostname}:${target.port || 443}`,
      timeout: timeoutMs
    });

    connectReq.on('connect', (res, socket) => {
      if (res.statusCode !== 200) {
        reject(new Error(`代理 CONNECT 失败: ${res.statusCode}`));
        return;
      }
      const req = https.get(targetUrl, {
        socket,
        headers,
        timeout: timeoutMs,
        servername: target.hostname
      }, (res2) => {
        let data = '';
        res2.on('data', (chunk) => { data += chunk; });
        res2.on('end', () => resolve({ statusCode: res2.statusCode, data }));
      });
      req.setTimeout(timeoutMs, () => { req.destroy(); reject(new Error('请求超时')); });
      req.on('error', reject);
    });

    connectReq.setTimeout(timeoutMs, () => { connectReq.destroy(); reject(new Error('代理连接超时')); });
    connectReq.on('error', reject);
    connectReq.end();
  });
}

/**
 * HTTPS GET 请求并解析 JSON，带 Reddit 要求的 User-Agent，支持代理
 */
async function httpsGetJson(url, timeoutMs = 45000) {
  const headers = { 'User-Agent': 'GAIRR-NewsDigest/1.0 (bot)' };
  const proxyUrl = getProxyUrl();

  if (proxyUrl) {
    const { statusCode, data } = await httpsGetViaProxy(url, proxyUrl, timeoutMs, headers);
    if (statusCode && statusCode >= 400) {
      throw new Error(`Reddit HTTP ${statusCode}: ${data.slice(0, 200)}`);
    }
    return JSON.parse(data);
  }

  return new Promise((resolve, reject) => {
    const req = https.get(url, { timeout: timeoutMs, headers }, (res) => {
      let data = '';
      res.on('data', (chunk) => { data += chunk; });
      res.on('end', () => {
        try {
          resolve(JSON.parse(data));
        } catch (e) {
          reject(new Error('JSON 解析失败: ' + e.message));
        }
      });
    });
    req.setTimeout(timeoutMs, () => { req.destroy(); reject(new Error('请求超时')); });
    req.on('error', reject);
  });
}

/**
 * 拉取 Reddit r/technology 热榜（带指数退避重试）
 * @param {number} topN 返回条数
 * @param {number} hours 时间窗口（仅保留最近 hours 小时内创建的帖子）
 */
async function fetchStories(topN, hours) {
  const proxyUrl = getProxyUrl();
  if (proxyUrl) console.log(`  Reddit 使用代理: ${proxyUrl.replace(/\/.*$/, '')}`);

  let lastError;
  const maxAttempts = 5;
  for (let attempt = 0; attempt < maxAttempts; attempt++) {
    try {
      const data = await httpsGetJson(API_URL);
      if (!data || !Array.isArray(data.data?.children)) {
        throw new Error('Reddit API 返回格式异常');
      }

      const cutoffSec = Math.floor(Date.now() / 1000) - hours * 3600;

      const stories = data.data.children
        .map((child) => child.data)
        .filter((post) => post.created_utc && post.created_utc > cutoffSec && !post.is_self)
        .sort((a, b) => (b.score || 0) - (a.score || 0))
        .slice(0, topN)
        .map((post) => ({
          title: post.title || '(无标题)',
          url: post.url || `https://www.reddit.com${post.permalink}`,
          score: post.score || 0,
          comments: post.num_comments || 0
        }));

      return { source: 'Reddit 科技版', stories };
    } catch (err) {
      lastError = err;
      console.warn(`  ⚠️ Reddit 拉取第 ${attempt + 1} 次失败: ${err.message}`);
      if (attempt < maxAttempts - 1) {
        const delay = 2000 * Math.pow(2, attempt);
        await new Promise((r) => setTimeout(r, delay));
      }
    }
  }
  throw lastError;
}

module.exports = fetchStories;
