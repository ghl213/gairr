/**
 * Hacker News 数据源
 * 职责：从 Algolia API 拉取最近24小时的热门故事
 */

const https = require('https');

const HN_API_URL = 'https://hn.algolia.com/api/v1/search_by_date?tags=story&hitsPerPage=50';

/**
 * HTTPS GET 请求并解析 JSON
 */
function httpsGetJson(url) {
  return new Promise((resolve, reject) => {
    const req = https.get(url, { timeout: 15000 }, (res) => {
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
    req.setTimeout(15000, () => { req.destroy(); reject(new Error('请求超时')); });
    req.on('error', reject);
  });
}

/**
 * 拉取 Hacker News 最近 hours 小时的热门故事
 * @param {number} topN 返回条数
 * @param {number} hours 时间窗口
 */
async function fetchStories(topN, hours) {
  const nowSec = Math.floor(Date.now() / 1000);
  const cutoffSec = nowSec - hours * 3600;
  const data = await httpsGetJson(HN_API_URL);
  if (!data || !Array.isArray(data.hits)) {
    throw new Error('HN API 返回格式异常');
  }

  const stories = data.hits
    .filter((hit) => hit.created_at_i && hit.created_at_i > cutoffSec)
    .sort((a, b) => (b.points || 0) - (a.points || 0))
    .slice(0, topN)
    .map((hit) => ({
      title: hit.title || '(无标题)',
      url: hit.url || `https://news.ycombinator.com/item?id=${hit.objectID}`,
      score: hit.points || 0,
      comments: hit.num_comments || 0
    }));

  return { source: '黑客新闻', stories };
}

module.exports = fetchStories;
