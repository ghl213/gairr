/**
 * GitHub 数据源
 * 职责：从 GitHub Search API 拉取最近24小时内创建的、按 Star 排序的热门仓库
 */

const https = require('https');

const SEARCH_API_URL = 'https://api.github.com/search/repositories';

/**
 * HTTPS GET 请求并解析 JSON
 */
function httpsGetJson(url, token) {
  return new Promise((resolve, reject) => {
    const headers = {
      'User-Agent': 'GAIRR-NewsDigest/1.0 (bot)',
      'Accept': 'application/vnd.github.v3+json'
    };
    if (token) {
      headers['Authorization'] = `Bearer ${token}`;
    }

    const req = https.get(url, { timeout: 15000, headers }, (res) => {
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
 * 生成 YYYY-MM-DD 格式日期字符串
 */
function formatDate(iso) {
  return iso.toISOString().split('T')[0];
}

/**
 * 拉取 GitHub 最近24小时热门仓库
 * @param {number} topN 返回条数
 * @param {number} hours 时间窗口
 * @param {string} token GitHub 访问令牌（可选）
 */
async function fetchStories(topN, hours, token) {
  const since = new Date(Date.now() - hours * 3600 * 1000);
  const date = formatDate(since);
  const url = `${SEARCH_API_URL}?q=created:>${date}&sort=stars&order=desc&per_page=${topN}`;

  const data = await httpsGetJson(url, token);
  if (!data || !Array.isArray(data.items)) {
    const msg = data?.message ? `GitHub API: ${data.message}` : 'GitHub API 返回格式异常';
    throw new Error(msg);
  }

  const stories = data.items.map((repo) => ({
    title: repo.full_name || '(无标题)',
    url: repo.html_url || '',
    score: repo.stargazers_count || 0,
    comments: repo.watchers_count || 0,
    description: repo.description || ''
  }));

  return { source: 'GitHub 热榜', stories };
}

module.exports = fetchStories;
