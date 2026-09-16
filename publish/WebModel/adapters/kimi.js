// ============================================================================
// GAIRR 网页模型通道 · Kimi 站点适配脚本（www.kimi.com/agent）
// ----------------------------------------------------------------------------
// 职责：驱动 Kimi 页面完成一轮问答（定位输入框 → 发送 → 轮询抓取回复正文
// → 流式回传 → 收尾判定）。
// 约束：本文件是"站点适配层"，所有与 Kimi 页面 DOM 相关的细节都只出现在这里；
// C# 侧只调用 window.__gairrAsk / __gairrCancel / __gairrNewChat /
// __gairrNewChatVerify / __gairrReady（可对话）与 __gairrLoggedIn（登录态）。
// 站点改版只需改 SEL 段。接入其它网页模型 = 复制本文件按站点改选择器。
// 通信：window.chrome.webview.postMessage({type,...}) → C# WebMessageReceived
// type: log | delta | reasoning | done | error | phase | sent
// 禁忌：问答流程中绝不允许 location.href / 点击带 href 的 a 做新对话——
// 整页重载会销毁正在等待的 __gairrAsk。候选入口一律排除带 href 的 a 标签，
// 点击后必须在校验窗口内确认会话确实切换才算生效（否则返回 'none'）。
// ============================================================================
(function () {
 if (window.__gairrAsk) return; // 注入幂等（重载/多次注入只生效一次）

 // ---------- 基础工具 ----------
 function post(o) { try { window.chrome.webview.postMessage(o); } catch (e) { } }
 function log(msg) {
 try { console.log('[GAIRR] ' + msg); } catch (e) { }
 post({ type: 'log', msg: msg });
 }
 function sleep(ms) { return new Promise(function (r) { setTimeout(r, ms); }); }

 // 站点选择器：按候选顺序取第一个存在且可见的元素（站点改版时只改这里）
 var SEL = {
 // Kimi 输入框：SPA 富文本 contenteditable，兼容旧版 textarea
 input: ['div[contenteditable="true"]', 'textarea[placeholder]', '.chat-input-editor', 'textarea'],
 // 发送按钮：aria-label / class 含 send
 send: ['button[aria-label="发送"]', 'div[role="button"][aria-label="发送"]',
 'button[class="send"]', 'div[class="send"]', 'button[type="submit"]'],
 // 停止按钮（等待上一条结束时用；失配时退化为轮询）
 stop: ['button[aria-label="停止"]', 'div[role="button"][aria-label="停止"]', '[class="stop"]'],
 // 回复正文：Kimi 正文容器
 answer: ['[class="markdown"]', '[class="segment-content"]', '[class="chat-content"]',
 '[class="message-content"]'],
 // 深度思考折叠区（取不到时思考为空，不影响正文与工具闭环）
 think: ['[class="think"]', '[class="reason"]', '[class="thought"]'],
 // 不含 a[href]：带 href 的 a 是导航链接，点击会整页重载
 newChat: ['div[role="button"][aria-label="新对话"]', 'button[aria-label="新对话"]',
 '[class="new-chat"]']
 };
 var NEW_CHAT_WORDS = ['开启新对话', '新对话', '新建对话', 'New chat'];

 function visible(el) {
 if (!el) return false;
 var r = el.getBoundingClientRect();
 return r.width > 0 && r.height > 0 && getComputedStyle(el).visibility !== 'hidden';
 }
 function pick(list) {
 for (var i = 0; i < list.length; i++) {
 var nodes = document.querySelectorAll(list[i]);
 for (var j = nodes.length - 1; j >= 0; j--) {
 var el = nodes[j];
 if (visible(el)) return el;
 }
 }
 return null;
 }
 // 按可见文字/aria-label 找按钮：先精确匹配再包含匹配；只认短文本
 function pickByWords(words) {
 var cands = document.querySelectorAll('[role="button"],button,a,[class="button"],[class="icon"]');
 for (var pass = 0; pass < 2; pass++) {
 for (var i = 0; i < cands.length; i++) {
 var el = cands[i];
 if (el.tagName === 'A' && el.hasAttribute('href')) continue; // 导航链接一律排除
 if (!visible(el)) continue;
 var t = (el.innerText || '').trim();
 if (!t) t = (el.getAttribute('aria-label') || '').trim();
 if (!t || t.length > 12) continue;
 for (var k = 0; k < words.length; k++) {
 if (pass === 0 ? t === words[k] : t.indexOf(words[k]) >= 0) return el;
 }
 }
 }
 return null;
 }

 // 写入输入框：React 受控组件必须走原生 setter + input 事件
 function setInput(el, text) {
 el.focus();
 if (el.tagName === 'TEXTAREA') {
 var desc = Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value');
 if (desc && desc.set) desc.set.call(el, text); else el.value = text;
 el.dispatchEvent(new Event('input', { bubbles: true }));
 } else {
 var sel = window.getSelection(); sel.removeAllRanges();
 var range = document.createRange(); range.selectNodeContents(el); sel.addRange(range);
 if (!document.execCommand('insertText', false, text)) el.innerText = text;
 el.dispatchEvent(new Event('input', { bubbles: true }));
 }
 }
 function inputTextOf(el) {
 if (!el) return '';
 return (el.tagName === 'TEXTAREA' ? (el.value || '') : (el.innerText || '')).trim();
 }
 function info(el) {
 if (!el) return '(null)';
 var t = ((el.getAttribute && el.getAttribute('aria-label')) || (el.innerText || '') || '').trim();
 if (t.length > 12) t = t.slice(0, 12) + '…';
 var c = ((el.className || '') + '').split(' ')[0];
 return el.tagName.toLowerCase() + '[' + t + ']' + (c ? '.' + c : '');
 }

 // ---------- 页面就绪 / 登录态判定 ----------
 window.__gairrReady = function () { return !!pick(SEL.input); };

 window.__gairrLoggedIn = function () {
 try {
 if (pick(SEL.input)) return 'in';
 if (document.readyState !== 'complete') return 'loading';
 return 'out';
 } catch (e) { return 'loading'; }
 };

 // ---------- 站点开关（Kimi 的深度思考/联网搜索；判定不出时不盲点） ----------
 function toggleState(el) {
 var c = (el.className || '') + ' ' + (el.getAttribute('aria-pressed') || '') + ' ' + (el.getAttribute('aria-checked') || '');
 c = c.toLowerCase();
 if (/active|selected|checked|pressed|true|开启|\bon\b/.test(c)) return true;
 if (/inactive|unselected|disabled|\boff\b/.test(c)) return false;
 return null;
 }
 function clickToggle(words, want) {
 if (!want) return false;
 var el = pickByWords(words);
 if (!el) { log('未找到站点开关：' + words.join('/')); return false; }
 var st = toggleState(el);
 if (st === true) { log('站点开关已是开启态：' + words[0]); return true; }
 if (st === false) { el.click(); log('已打开站点开关：' + words[0]); return true; }
 log('站点开关状态无法判定（' + words[0] + '），保持原样不盲点');
 return true;
 }

 function answerNodes() {
 var out = [];
 for (var i = 0; i < SEL.answer.length; i++) {
 var n = document.querySelectorAll(SEL.answer[i]);
 if (n.length > 0) { out = Array.prototype.slice.call(n); break; }
 }
 return out;
 }

 var ascentLogged = '';
 // 取最后一个有文字的正文节点的文本，并沿父链上溯到本条消息最外层正文容器
 function lastAnswerText() {
 var nodes = answerNodes();
 for (var i = nodes.length - 1; i >= 0; i--) {
 var el = nodes[i], t = (el.innerText || '').replace(/\s+/, '');
 if (!t) continue;
 var outer = el;
 for (var up = 0; up < 12 && outer.parentElement; up++) {
 var p = outer.parentElement, isCand = false;
 for (var k = 0; k < nodes.length; k++) { if (nodes[k] === p) { isCand = true; break; } }
 if (!isCand) {
 if (((p.innerText || '').replace(/\s+/, '')) === (outer.innerText || '').replace(/\s+/, '')) { outer = p; continue; }
 break;
 }
 outer = p;
 }
 if (outer === el) return t;
 var ot = (outer.innerText || '').replace(/\s+/, '');
 if (ot.length > t.length) {
 var sig = t.length + '>' + ot.length;
 if (sig !== ascentLogged) { ascentLogged = sig; log('正文节点上溯：候选 ' + t.length + ' 字 → 容器 ' + ot.length + ' 字'); }
 }
 return ot.length >= t.length ? ot : t;
 }
 return '';
 }

 // ---------- 深度思考（思维链）抓取 ----------
 function thinkText() {
 var nodes = answerNodes();
 if (!nodes.length) return '';
 var el = nodes[nodes.length - 1];
 for (var up = 0; up < 6 && el; up++) {
 for (var i = 0; i < SEL.think.length; i++) {
 var all = el.querySelectorAll(SEL.think[i]);
 for (var k = all.length - 1; k >= 0; k--) {
 var s = (all[k].innerText || '').trim();
 if (s) return s;
 }
 }
 el = el.parentElement;
 }
 return '';
 }
function isThinkChrome(s) {
 var t = s.replace(/\s+/g, '');
 return t.length <= 30 && /^已?深度思考/.test(t);
}

// ---------- 收尾闸门（Kimi 专属：思考阶段必须等完） ----------
// Kimi 一条回复分两段渲染：先"深度思考中"（正文节点此时可能是草稿或占位文本），再出最终正文。
// 若思考期间就按"稳定 8 拍"收尾，拿到的是半截内容——上层解析不出协议 JSON，会补发一条格式
// 纠正，于是页面表现为"多发一条信息 + 排队"，而原始 JSON 被当成正文刷进气泡。这里加两把闸门：
// thinkPhase() 判断是否仍在思考，jsonUnfinished() 判断协议 JSON 是否已收尾。
function thinkPhase() {
 if (pick(SEL.stop)) return true; // 停止按钮在场 = 页面仍在生成
 var nodes = document.querySelectorAll('[class="think"],[class="reason"],[class="thought"],[class="thinking"],[class="reason"],[class*="think"]');
 for (var i = nodes.length - 1; i >= 0; i--) {
 var t = (nodes[i].innerText || '').replace(/\s+/g, '');
 if (!t) continue;
 if (/思考已完成|已完成思考|已深度思考/.test(t)) return false;
 if (/思考中|正在思考|深度思考中/.test(t)) return true;
 }
 return false;
}
// 正文以 '{' 开头（协议 JSON）但结尾还不是 '}' → 视为仍在输出，继续等（纯文本回复不受约束）
function jsonUnfinished(s) {
 var t = (s || '').trim();
 if (t.length === 0 || t.charAt(0) !== '{') return false;
 return t.charAt(t.length - 1) !== '}';
}

// 上一条是否仍在生成：优先看停止按钮（命中即真）；Kimi 现版没有可靠的停止按钮 aria-label，
// 退化为「末条正文两次采样（约 700ms）之间仍在增长」——增长说明页面还在流式输出。
async function stillGrowing(waitMs) {
 var ms = waitMs || 700;
 if (pick(SEL.stop)) return true;
 var a = lastAnswerText().length;
 await sleep(ms);
 if (pick(SEL.stop)) return true;
 return lastAnswerText().length > a;
}
// 问前等待上一条结束：默认 700ms 采样
async function busyGenerating() { return await stillGrowing(700); }

 // ---------- 取消令牌 ----------
 var tokens = {};
 window.__gairrCancel = function (id) { if (tokens[id]) tokens[id].cancelled = true; };

 // ---------- 新建/重置网页对话（只做页面内操作，绝不跳转） ----------
 var chatSnap = null;
 function takeChatSnapshot() {
 chatSnap = { url: location.href, msgs: answerNodes().length,
 input: inputTextOf(pick(SEL.input)), t: Date.now() };
 }
 window.__gairrNewChat = function () {
 try {
 var el = pickByWords(NEW_CHAT_WORDS);
 if (!el) el = pick(SEL.newChat);
 if (!el) { log('未找到新对话入口（页面内，带 href 的 a 已排除）；由调用方决定是否重载页面'); return 'none'; }
 takeChatSnapshot();
 log('点击新对话入口：' + ((el.innerText || el.getAttribute('aria-label') || '') + '').trim() + '（待校验切换是否生效）');
 el.click();
 return 'clicked';
 } catch (e) { log('新建对话失败：' + (e && e.message ? e.message : e)); return 'none'; }
 };
 window.__gairrNewChatVerify = function () {
 try {
 if (!chatSnap) return 'pending';
 if (Date.now() - chatSnap.t > 5000) { chatSnap = null; return 'pending'; }
 if (location.href !== chatSnap.url) { chatSnap = null; return 'ok'; }
 if (chatSnap.msgs > 0 && answerNodes().length === 0) { chatSnap = null; return 'ok'; }
 if (chatSnap.input !== '' && inputTextOf(pick(SEL.input)) === '') { chatSnap = null; return 'ok'; }
 return 'pending';
 } catch (e) { return 'pending'; }
 };

 // ---------- 一轮问答主流程 ----------
 async function runAsk(text, opts) {
 opts = opts || {};
 var id = opts.id || String(Date.now());
 var tok = tokens[id] = { cancelled: false };
 var timeoutMs = opts.timeoutMs || 900000;

 try {
 var box = null;
 for (var i = 0; i < 150 && !box; i++) {
 if (tok.cancelled) { post({ type: 'error', msg: '已取消' }); return; }
 box = pick(SEL.input);
 if (!box) await sleep(200);
 }
 if (!box) { post({ type: 'error', msg: '未找到输入框：请确认页面已加载且已登录（www.kimi.com）' }); return; }

 // 等待上一条回复结束：Kimi 现版页面没有 aria-label="停止" 的按钮，SEL.stop 会失配——
 // 旧写法 while (pick(SEL.stop)) 一次都不进，下一条会直接挤在旧回复后面排队。
 // 兜底判据：末条正文在两次采样之间仍在增长 = 上一条还在生成。总等待上限约 14 秒；
 // 超时不再死等（后续「重置网页对话」会整页重载，旧请求随之作废）。
 for (var w = 0; w < 20; w++) {
 if (tok.cancelled) { post({ type: 'error', msg: '已取消' }); return; }
 if (!(await busyGenerating())) break;
 post({ type: 'phase', msg: '等待上一条回复结束…' });
 }

 var deep = clickToggle(['深度思考', 'DeepThink'], opts.deepThink);
 var web = clickToggle(['联网搜索', '搜索', 'Search'], opts.webSearch);
 post({ type: 'phase', msg: '已就绪（深度思考=' + deep + ' 联网搜索=' + web + '），正在输入…' });

 if (opts.expectNewChat) {
 log('旧版调用：需要重置对话（页面内新建，不重载文档）');
 var nc = window.__gairrNewChat();
 if (nc === 'clicked') {
 var vOk = false;
 for (var v = 0; v < 8 && !vOk; v++) { await sleep(100); vOk = window.__gairrNewChatVerify() === 'ok'; }
 log(vOk ? '已验证新对话切换生效' : '新对话点击后 800ms 内未确认切换：本次直接在当前对话继续提问');
 } else log('页面内没有新建入口：本次直接在当前对话继续提问');
 }

 var before = answerNodes().length;
 var beforeText = lastAnswerText();
 var beforeThink = thinkText();
 // 防返回内容不规范：发送前在提示词最尾部强制追加一句 JSON 格式要求（仅 Kimi 通道），
 // 已在文本中则跳过，避免重复叠加；先追加再算规范化文本，保证"排除本轮提示词"的判据同步包含这句。
 var kimiJsonTag = '【返回标准的json格式】';
 if (text.indexOf(kimiJsonTag) < 0) text += '\n\n' + kimiJsonTag;
 // 本轮提示词规范化文本（去空白）：Kimi 会把刚发出的用户消息也渲染进正文节点，
 // 必须在收尾判据里排除它，否则"提示词本身"会被当成回复（表现为 3 秒就收尾、正文上万字）。
 var promptNorm = text.replace(/\s+/g, '');
 setInput(box, text);
 await sleep(150);
 if (!inputTextOf(box)) {
 log('输入框首次写入未生效，重试一次');
 setInput(box, text);
 await sleep(200);
 }
 if (!inputTextOf(box)) { post({ type: 'error', msg: '已定位输入框但写入失败（页面结构可能已改版）' }); return; }

 async function sentOK() {
 for (var v = 0; v < 15; v++) {
 await sleep(200);
 var b2 = pick(SEL.input);
 if (!b2 || !inputTextOf(b2)) return true;
 if (answerNodes().length > before) return true;
 }
 return false;
 }
 async function trySend(how) {
 if (how === 'click') {
 var btn = null;
 for (var s = 0; s < 20 && !btn; s++) {
 btn = pick(SEL.send) || pickByWords(['发送', 'Send']);
 if (!btn) await sleep(100);
 }
 if (!btn) { log('未找到发送按钮，改用 Enter 提交'); return false; }
 log('点击发送按钮：' + info(btn));
 btn.click();
 } else {
 log('改用 Enter 键提交');
 box.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true }));
 box.dispatchEvent(new KeyboardEvent('keyup', { key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true }));
 }
 var ok = await sentOK();
 log((how === 'click' ? '点击发送按钮' : 'Enter 提交') + (ok ? '已生效' : '未生效'));
 return ok;
 }
 var sent = await trySend('click');
 if (!sent) sent = await trySend('enter');
 if (!sent) {
 post({ type: 'error', msg: '文字已写入输入框但发送未生效：请确认已登录且页面可对话' });
 return;
 }
 post({ type: 'sent' });
 post({ type: 'phase', msg: '已发送，等待回复…' });

 // 发送后基线重取：用户消息通常在发送成功后才插入 DOM，若此刻末条正文就是本轮提示词，
 // 说明它已被计入正文节点——把基线抬到它身上，后续只认新出现的助手回复。
 await sleep(600);
 var afterCur = lastAnswerText();
 var afterNorm = afterCur.replace(/\s+/g, '');
 if (afterCur && (afterNorm === promptNorm || (afterNorm.length >= 40 && promptNorm.indexOf(afterNorm.slice(0, 40)) >= 0))) {
 before = answerNodes().length;
 beforeText = afterCur;
 log('发送后基线重取：末条即本轮提示词（' + afterCur.length + ' 字），已排除，不作为回复');
 }

 var last = '', lastThink = '', stable = 0, started = false;
 var maxLen = 0, grewAt = Date.now(), beatAt = Date.now(), nodeCount = before;
 var deadline = Date.now() + timeoutMs;
 while (Date.now() < deadline) {
 if (tok.cancelled) { post({ type: 'error', msg: '已取消' }); return; }
 await sleep(300);

 var tk = thinkText();
 if (tk && tk !== beforeThink && tk !== lastThink && !isThinkChrome(tk)) {
 post({ type: 'reasoning', text: tk });
 lastThink = tk;
 }

 var nodes = answerNodes();
 nodeCount = nodes.length;
 var cur = lastAnswerText();
 // 排除"本轮自己发出去的提示词"被当成回复（Kimi 会把用户消息渲染进正文节点）
 var curNorm = cur.replace(/\s+/g, '');
 var ownPrompt = curNorm.length > 0 &&
 (curNorm === promptNorm || (curNorm.length >= 40 && promptNorm.indexOf(curNorm.slice(0, 40)) >= 0));
 if (!ownPrompt && cur && (nodes.length > before || cur !== beforeText)) {
 started = true;
 if (cur !== last) { post({ type: 'delta', text: cur }); last = cur; stable = 0; }
 else stable++;
 if (cur.length > maxLen) { maxLen = cur.length; grewAt = Date.now(); }
 }

 if (Date.now() - beatAt >= 10000) {
 beatAt = Date.now();
 log('等待中：正文节点 ' + nodeCount + ' 个、末条 ' + cur.length + ' 字、已收 ' + last.length +
 ' 字、思考 ' + lastThink.length + ' 字、已等待 ' + Math.round((timeoutMs - (deadline - Date.now())) / 1000) + 's');
 }

 // 收尾闸门（Kimi 专属）：思考未完成不收尾，协议 JSON 未闭合也不收尾。
 // 否则抓到半截正文，上层解析不出协议 JSON 会补发格式纠正——页面表现为多发一条消息 + 排队。
 // 兜底：思考区选择器失配导致长期判定为思考中时，正文停增 3 分钟即强制收尾，避免空等到超时。
 var thinkBusy = thinkPhase();
 if (started && thinkBusy && Date.now() - grewAt >= 180000) {
 log('思考阶段正文已停增 3 分钟：按可收尾处理，不再等思考完成');
 thinkBusy = false;
 }
 // 收尾硬前提：思考已完成 + 协议 JSON 已闭合 + 末条正文确实停增（1.2s 静默确认）。
 // 仍在增长则清零稳定计数继续等（外层 deadline 兜底），避免抓到半截正文触发上层重发。
 if (started && !thinkBusy && !jsonUnfinished(last) && (stable >= 8 || Date.now() - grewAt >= 90000)) {
 if (await stillGrowing(1200)) {
 stable = 0;
 post({ type: 'phase', msg: '末条正文仍在增长，继续等待生成结束…' });
 } else {
 break;
 }
 }
 }

 if (!started) {
 var sample = '', ns = answerNodes();
 for (var si = 0; si < ns.length && si < 3; si++) sample += ' / ' + (ns[si].className || '?') + '→' + ((ns[si].innerText || '').length) + '字';
 post({ type: 'error', msg: '超时未捕获到回复正文（正文节点 ' + nodeCount + ' 个' + sample + '）：请确认已登录、且页面处于可对话状态' });
 return;
 }
 log('收尾：正文 ' + last.length + ' 字（稳定 ' + stable + ' 拍）');
 post({ type: 'done', text: last });
 } catch (e) {
 post({ type: 'error', msg: '适配脚本异常：' + (e && e.message ? e.message : e) });
 } finally {
 delete tokens[id];
 }
 }

 // C# 侧入口：同步返回 'started'（真结果经 postMessage 回传）
 window.__gairrAsk = function (text, opts) {
 runAsk(text, opts);
 return 'started';
 };

 log('Kimi 适配脚本已注入');
})();
