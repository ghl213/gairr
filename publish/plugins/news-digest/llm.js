/**
 * NewsDigest LLM 摘要模块
 * 职责：从 config.ini 读取多模型通道（Model1..ModelN，按顺序依次尝试），
 *       调用 OpenAI 兼容接口生成中文摘要；单模型超时/失败先重试，
 *       再切换下一通道；全部通道失败时返回空串，由调用方走原文兜底。
 */

// 单模型超时必须小于 plugin.ini 的 Timeout（300s），否则 LLM 卡住会导致整个插件被强杀、
// 连本地兜底摘要都来不及生成（此前 120s/120s 相等，曾触发该问题）；
// 多模型依次重试的总耗时也须落在 300s 总超时内
const SINGLE_TIMEOUT = 60000;   // 每个模型的单次请求超时（毫秒）
const RETRY_ATTEMPTS = 1;       // 每个模型超时/失败后的重试次数（0=不重试）
const MAX_MODELS = 5;           // 最多读取的模型通道数（Model1..Model5）

const DEFAULT_SYSTEM_PROMPT = `你是一位科技新闻编辑。请根据下面最近24小时来自多个渠道的热门科技内容列表，为每条生成中文摘要。
要求：
1. 把英文标题翻译为简洁、准确的中文标题；
2. 用一句话概括核心内容（中文）；
3. 保持客观，不展开无关内容；
4. 按数据源分组输出，格式：
   **来源名**
   序号. [中文标题](URL) | 一句话摘要
5. 如果某来源列表为空，跳过该来源即可。`;

/**
 * 调用 LLM 生成中文摘要（OpenAI 兼容 /chat/completions，非流式）
 */
function callLlm(llm, systemPrompt, userContent) {
  return new Promise((resolve, reject) => {
    const https = require('https');
    const url = new URL(llm.baseUrl.replace(/\/$/, '') + '/chat/completions');
    const body = JSON.stringify({
      model: llm.model,
      messages: [
        { role: 'system', content: systemPrompt },
        { role: 'user', content: userContent }
      ],
      stream: false
    });

    const req = https.request(url, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Authorization': 'Bearer ' + llm.apiKey,
        'User-Agent': 'GAIRR/1.0'
      },
      timeout: SINGLE_TIMEOUT
    }, (res) => {
      let data = '';
      res.on('data', (chunk) => { data += chunk; });
      res.on('end', () => {
        try {
          const json = JSON.parse(data);
          if (!res.statusCode || res.statusCode >= 400) {
            reject(new Error(`LLM 请求失败 ${res.statusCode}: ${json?.error?.message || data}`));
            return;
          }
          const content = json.choices?.[0]?.message?.content;
          if (typeof content !== 'string') {
            reject(new Error('LLM 响应缺少 content'));
            return;
          }
          resolve(content.trim());
        } catch (e) {
          reject(new Error('LLM 响应解析失败: ' + e.message));
        }
      });
    });

    req.setTimeout(SINGLE_TIMEOUT, () => { req.destroy(); reject(new Error('LLM 请求超时')); });
    req.on('error', reject);
    req.write(body);
    req.end();
  });
}

/**
 * 读取模型通道列表，按顺序依次尝试：
 * 1. 优先读扁平通道行 ModelN=Provider|BaseUrl|ApiKey|Model（N=1..MAX_MODELS）。
 *    注意：插件用的 ini 解析是扁平格式（所有节压平为单层 key），
 *    若用 [Model1]/[Model2] 两个节会因键名相同（provider/baseurl...）互相覆盖，
 *    因此通道必须写成单行扁平格式；
 * 2. 未配置任何 ModelN 行时回退旧的单模型配置：
 *    [LLM] Provider=xxx 指向的供应商节（[Kimi]/[Bailian] 等）中的 BaseUrl/ApiKey/Model。
 */
function loadLlmChain(ini) {
  const chain = [];
  for (let i = 1; i <= MAX_MODELS; i++) {
    const raw = ini[`llm_model${i}`];
    if (!raw) break;
    const parts = raw.split('|').map((s) => s.trim());
    if (parts.length !== 4 || parts.some((s) => !s)) {
      console.warn(`  ⚠️ Model${i} 配置格式错误（应为 Provider|BaseUrl|ApiKey|Model），跳过`);
      continue;
    }
    chain.push({ provider: parts[0], baseUrl: parts[1], apiKey: parts[2], model: parts[3] });
  }
  if (chain.length > 0) return chain;

  // 兼容旧的单模型配置
  const provider = ini['llm_provider'] || 'Kimi';
  const s = provider.toLowerCase();
  const baseUrl = ini[`${provider}_baseurl`] || ini[`${s}_baseurl`];
  const apiKey = ini[`${provider}_apikey`] || ini[`${s}_apikey`];
  const model = ini[`${provider}_model`] || ini[`${s}_model`];
  if (!baseUrl || !apiKey || !model) {
    throw new Error(`config.ini 未配置 Model1..N 通道，且 [${provider}] 未完整配置 BaseUrl / ApiKey / Model`);
  }
  return [{ provider, baseUrl, apiKey, model }];
}

/**
 * 用 LLM 把采集到的内容翻译成中文摘要
 */
async function translateSummary(results, llm, userPrompt) {
  const hasStories = results.some((r) => r.stories && r.stories.length > 0);
  if (!hasStories) return '暂无最近24小时内的科技新闻/项目。';

  const systemPrompt = userPrompt
    ? `${DEFAULT_SYSTEM_PROMPT}\n\n补充要求：${userPrompt}`
    : DEFAULT_SYSTEM_PROMPT;
  const userContent = `请生成中文摘要：\n\n${JSON.stringify(results, null, 2)}`;
  return await callLlm(llm, systemPrompt, userContent);
}

/**
 * 尝试单个模型通道：超时/失败最多重试 RETRY_ATTEMPTS 次；成功返回摘要，
 * 否则返回空串（由调用方切换下一个模型）
 */
async function tryOneModel(results, llm, userPrompt) {
  for (let attempt = 0; attempt <= RETRY_ATTEMPTS; attempt++) {
    const tag = attempt > 0 ? `（重试 ${attempt}）` : '';
    console.log(`正在使用 ${llm.provider} / ${llm.model}${tag} 生成中文摘要...`);
    try {
      const summary = await translateSummary(results, llm, userPrompt);
      if (summary && summary.trim()) return summary;
      console.warn('  ⚠️ 模型返回内容为空，切换下一个模型');
    } catch (err) {
      console.warn(`  ⚠️ ${llm.provider} / ${llm.model} 失败（${err.message}）`);
    }
  }
  return '';
}

module.exports = { loadLlmChain, tryOneModel };
