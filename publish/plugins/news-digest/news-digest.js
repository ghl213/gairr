/**
 * 24小时中文科技新闻摘要脚本（插件自包含版）
 * 职责：根据 config.ini 中 [Sources] 配置，从 Reddit / GitHub / Hacker News
 *       拉取最近24小时的热门科技内容，调用 LLM 生成中文摘要，
 *       并将中文摘要作为最终结果输出到 stdout（不自动发送钉钉，是否发送由调用方决定）。
 * 摘要生成走同目录 llm.js：多模型通道从头按顺序尝试，某模型超时/失败自动
 *       重试并切换下一通道；所有通道都失败才走本地原文列表兜底。
 * 用法：node news-digest.js [config.ini路径] [提示词/风格要求]
 */

const fs = require('fs');
const path = require('path');
const { loadLlmChain, tryOneModel } = require('./llm');

const HOURS_BACK = 24;

/**
 * 解析扁平 ini 文件（去空行、去注释）
 */
function readIni(configPath) {
  if (!configPath || !fs.existsSync(configPath)) {
    throw new Error('找不到配置文件: ' + (configPath || '(未提供)'));
  }
  const lines = fs.readFileSync(configPath, 'utf8').split(/\r?\n/);
  const sectionRe = /^\[(.+)\]$/;
  const ini = {};
  let section = '';
  for (const raw of lines) {
    const line = raw.trim();
    if (line.length === 0 || line.startsWith(';')) continue;
    const ci = line.indexOf(';');
    const content = (ci >= 0 ? line.substring(0, ci) : line).trim();
    if (content.length === 0) continue;
    const m = sectionRe.exec(content);
    if (m) {
      section = m[1].trim();
      continue;
    }
    const ei = content.indexOf('=');
    if (ei <= 0) continue;
    const key = (section ? section + '_' : '') + content.substring(0, ei).trim();
    ini[key.toLowerCase()] = content.substring(ei + 1).trim();
  }
  return ini;
}

/**
 * 读取数据源配置
 */
function loadSourceConfig(ini) {
  const raw = ini['sources_sources'] || 'hackernews';
  const sources = raw.split(',').map((s) => s.trim().toLowerCase()).filter(Boolean);
  const topN = parseInt(ini['sources_topn'], 10) || 10;
  const githubToken = ini['sources_githubtoken'] || '';
  return { sources, topN, githubToken };
}

/**
 * 从指定数据源拉取内容
 */
async function fetchFromSource(name, topN, hours, githubToken) {
  const sourceFile = path.join(__dirname, 'sources', `${name}.js`);
  if (!fs.existsSync(sourceFile)) {
    throw new Error(`未知的数据源: ${name}（找不到 ${sourceFile}）`);
  }
  const fetchFn = require(sourceFile);
  const args = name === 'github' ? [topN, hours, githubToken] : [topN, hours];
  return await fetchFn(...args);
}

/**
 * 拉取所有启用数据源的内容
 */
async function fetchAllSources(config) {
  const results = [];
  for (const name of config.sources) {
    try {
      const result = await fetchFromSource(name, config.topN, HOURS_BACK, config.githubToken);
      results.push(result);
      console.log(`  ${result.source}: ${result.stories.length} 条`);
    } catch (err) {
      console.warn(`  ⚠️ ${name} 拉取失败: ${err.message}`);
      results.push({ source: name, stories: [], error: err.message });
    }
  }
  return results;
}

/**
 * 本地兜底：不依赖 LLM，直接用采集到的原始标题+链接拼装正文
 * （所有模型通道失败时调用，保证钉钉一定有内容）
 */
function buildLocalFallback(results) {
  const lines = [];
  for (const r of results) {
    if (!r.stories || r.stories.length === 0) continue;
    lines.push(`**${r.source}**`);
    r.stories.forEach((s, i) => {
      const title = s.title || '(无标题)';
      const url = s.url || '';
      const score = s.score ? `（${s.score}分）` : '';
      const tag = s.description ? ` | ${s.description}` : '';
      lines.push(`${i + 1}. [${title}](${url})${score}${tag}`);
    });
  }
  if (lines.length === 0) return '暂无最近24小时内的科技新闻/项目。';
  return lines.join('\n');
}

/**
 * 生成钉钉 Markdown 文本
 */
function buildMarkdown(summary, sources) {
  const dateStr = new Date().toLocaleString('zh-CN', {
    year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit'
  });
  const sourceList = sources.map((s) => {
    const map = { hackernews: '黑客新闻', reddit: 'Reddit 科技版', github: 'GitHub 热榜' };
    return map[s] || s;
  }).join(' / ');
  return `来源：${sourceList}（最近24小时，按热度排序）\n\n${summary}\n\n---\n\n生成时间：${dateStr}`;
}

async function main() {
  const pluginConfigPath = path.join(__dirname, 'config.ini');
  const globalConfigPath = process.argv[2];
  const userPrompt = (process.argv[3] || '').trim();

  // 优先使用全局 config.ini（调用方传入），否则回退到插件私有配置
  const configPath = globalConfigPath || pluginConfigPath;
  console.log('使用配置:', configPath);

  const ini = readIni(configPath);
  const chain = loadLlmChain(ini);
  const sourceConfig = loadSourceConfig(ini);

  if (sourceConfig.sources.length === 0) {
    throw new Error('[Sources] 中未启用任何数据源');
  }
  console.log(`启用数据源: ${sourceConfig.sources.join(', ')}，每源 Top ${sourceConfig.topN}`);
  console.log(`模型通道: ${chain.map((l) => l.provider + '/' + l.model).join(' → ')}`);

  console.log('正在拉取内容...');
  const results = await fetchAllSources(sourceConfig);

  // 从第一个模型开始按顺序尝试；某模型超时/失败会自动重试并重头切换下一通道
  let summary = '';
  for (const llm of chain) {
    summary = await tryOneModel(results, llm, userPrompt);
    if (summary) break;
  }
  if (!summary || !summary.trim()) {
    console.warn('  ⚠️ 所有模型通道均失败，改用本地原文列表兜底');
    summary = buildLocalFallback(results);
  }

  const content = buildMarkdown(summary, sourceConfig.sources);

  // 自动任务约定：不自动发送钉钉；最终中文结果直接输出，
  // 是否发送由调用方（Agent）按需通过 DingTalk 工具决定
  console.log('=== 最终结果（中文摘要）===');
  console.log(content);
  console.log('完成');
}

main().catch((err) => {
  console.error('❌ 失败:', err.message);
  process.exit(1);
});
