/* ============================================================================
 * GAIRR 网页模型通道 · DeepSeek 专家站点适配脚本
 * ----------------------------------------------------------------------------
 * 职责：驱动 chat.deepseek.com 页面完成一轮问答（定位输入框 → 可选开关 → 发送
 *       → 轮询抓取回复正文 → 流式回传 → 收尾判定）。
 * 收尾判定（血泪教训：判定太窄会"网页已显示结果，GAIRR 却卡在思考中到超时"）：
 *       正文 = 最后一个**有文字**的正文节点，且必须上溯到"本条消息最外层正文容器"再取文本
 *       （站点对段落/代码/数学/引用/裸 HTML 等子块也挂 ds-markdown 前缀类名，它们是容器的后代、
 *        文档序在容器之后 → 只取"最后一个候选"会塌成行内碎片：实测 9 字 "<summary>"、或 JSON 碎片）；
 *       本轮正文 = 正文节点数增加 或 末条正文与发送前快照不同（站点可能原地替换上一轮节点，计数不增）；
 *       收尾 = 正文连续 8 拍（≈2.4s）不变，或出现正文后 90s 再无新增（看门狗兜底）；
 *       全程不依赖站点"停止"按钮选择器（选择器失配会误判提前收尾），也不依赖单一路径的节点计数。
 * 约束：本文件是"站点适配层"，所有与 DeepSeek 页面 DOM 相关的细节都只出现在这里；
 *       C# 侧只调用 window.__gairrAsk / __gairrCancel / __gairrNewChat / __gairrNewChatVerify /
 *       __gairrReady（可对话）与 __gairrLoggedIn（登录态：未登录才弹窗让用户登录）。
 *       接入其它网页模型 = 复制本文件按站点改选择器，C# 与 Agent 侧零改动。
 * 通信：window.chrome.webview.postMessage({type,...}) → C# WebMessageReceived
 *       type: log | delta | reasoning | done | error | phase | sent
 *       sent：本条提示词确证已送出（输入框已清空 / 已出现新回复节点），C# 侧据此区分
 *             "投递前失败"（未送达 → 上下文同步点归零）与"已投递后异常"（已送达 → 保持同步）
 * 禁忌（血泪教训）：问答流程中**绝不允许 location.href / 点击首页链接**做"新对话"——
 *       整页重载会销毁正在等待的 __gairrAsk，表现为"页面什么都没收到"。因此候选入口一律
 *       排除带 href 的 a 标签（点了就是整页导航），且点击后必须在校验窗口（800ms）内确认会话
 *       确实切换（URL 变化/消息节点归零/输入框清空）才算生效；页面内找不到入口或切换无法
 *       确认时返回 'none'，由 C# 侧在提问**之前**重载页面（见 WebModelWindow.ResetConversationAsync）。
 * ========================================================================== */
(function () {
    if (window.__gairrAsk) return;   // 注入幂等（重载/多次注入只生效一次）

    /* ---------- 基础工具 ---------- */
    function post(o) { try { window.chrome.webview.postMessage(o); } catch (e) { } }
    function log(msg) {
        try { console.log('[GAIRR] ' + msg); } catch (e) { }   // 同步打进页面控制台，便于 DevTools 排查
        post({ type: 'log', msg: msg });
    }
    function sleep(ms) { return new Promise(function (r) { setTimeout(r, ms); }); }

    /* 站点选择器：按候选顺序取第一个"存在且可见"的元素（站点改版时只改这里） */
    var SEL = {
        input: ['textarea#chat-input', 'textarea[placeholder]', 'div[contenteditable="true"]', 'textarea'],
        send: ['div[role="button"][aria-label*="发送"]', 'button[aria-label*="发送"]', 'div[aria-label*="send" i]',
               'div[class*="icon-button--filled"]', 'button[class*="send"]'],
        stop: ['div[role="button"][aria-label*="停止"]', 'button[aria-label*="停止"]', 'div[aria-label*="stop" i]'],
        answer: ['[class*="ds-markdown"]', '.ds-markdown--block'],
        // "深度思考"块：deepseek 把思维链渲染在正文节点之外的独立容器里（类名含 think/thought/reason）。
        // 站点改版只改这里；候选全取不到时思考为空（降级为不展示思考，正文与工具闭环不受影响）
        think: ['[class*="ds-think"]', '[class*="think-block"]', '[class*="thinking"]',
                '[class*="thought"]', '[class*="reason"]'],
        // 不含 a[href]：带 href 的 a 是导航链接，点击会整页重载（销毁脚本上下文），禁止用于"新对话"
        newChat: ['div[role="button"][aria-label*="新对话"]', 'button[aria-label*="新对话"]',
                  'div[aria-label*="新对话"]']
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
    /* 按可见文字/aria-label 找按钮：先精确匹配再包含匹配；只认短文本，避免点到整块容器 */
    function pickByWords(words) {
        var cands = document.querySelectorAll('[role="button"],button,a,[class*="button"],[class*="icon-button"]');
        for (var pass = 0; pass < 2; pass++) {
            for (var i = 0; i < cands.length; i++) {
                var el = cands[i];
                // 带 href 的 a = 导航链接：点击触发整页导航（销毁脚本上下文 = "页面什么都没收到"），一律排除
                if (el.tagName === 'A' && el.hasAttribute('href')) continue;
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

    /* 写入输入框：React 受控组件必须走原生 setter + input 事件，否则框架不认账 */
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
    /* 元素摘要（日志用）：标签[可见文字或 aria-label].首个class，便于定位命中了哪个控件 */
    function info(el) {
        if (!el) return '(null)';
        var t = ((el.getAttribute && el.getAttribute('aria-label')) || (el.innerText || '') || '').trim();
        if (t.length > 12) t = t.slice(0, 12) + '…';
        var c = ((el.className || '') + '').split(' ')[0];
        return el.tagName.toLowerCase() + '[' + t + ']' + (c ? '.' + c : '');
    }

    /* ---------- 页面就绪判定（C# 侧据此等待"可对话"状态） ---------- */
    window.__gairrReady = function () { return !!pick(SEL.input); };

    /* ---------- 登录态判定（C# 侧据此决定"是否需要弹窗让用户登录"） ----------
       已登录 = 页面处于可对话状态（输入框存在且可见）——这本就是整个适配脚本赖以工作的判据；
       页面尚在加载（readyState 未 complete）时返回 'loading'，让 C# 侧继续等：首屏还没渲染完
       就没有输入框，此时若直接判"未登录"会把已登录用户也弹一次窗口；
       加载完仍看不到输入框 → 'out'（未登录页面根本没有对话输入框，需要用户登录）。
       站点若是"登录弹层压在聊天页之上"的形态，只需在 SEL 里补一条登录弹层选择器并在下方加判定。 */
    window.__gairrLoggedIn = function () {
        try {
            if (pick(SEL.input)) return 'in';
            if (document.readyState !== 'complete') return 'loading';
            return 'out';
        } catch (e) { return 'loading'; }
    };

    /* ---------- 站点开关（深度思考 / 联网搜索） ---------- */
    /* 读取开关当前态：class / aria-pressed / aria-checked 任一命中即为开；无法判定返回 null */
    function toggleState(el) {
        var c = (el.className || '') + ' ' + (el.getAttribute('aria-pressed') || '') + ' ' + (el.getAttribute('aria-checked') || '');
        c = c.toLowerCase();
        if (/active|selected|checked|pressed|true|开启|\bon\b/.test(c)) return true;
        if (/inactive|unselected|disabled|\boff\b/.test(c)) return false;
        return null;
    }
    /* want=true 时确保开关处于开启态。判定不出当前态时**不盲点**——盲点会把用户已开的
       "深度思考/专家模式"点成关闭，比不开更糟；只在能判定为关时才点一次。 */
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

    var ascentLogged = '';   // 上溯日志去重：本函数每拍都调（~3 次/秒），不去重会刷爆 webmodel.log
    /* 取"最后一个有文字的正文节点"的文本。三个坑（都实测踩过，改这段前先读完）：
       ① 站点常在答复末尾挂空占位节点（流式光标容器，class 同样含 ds-markdown）→ 从后往前跳过空节点，
          否则把空串当"还没出正文"，空转到超时（网页早显示结果，GAIRR 一直卡在思考中）；
       ② 站点把**子块**也挂上 ds-markdown 前缀类名（ds-markdown-paragraph / ds-markdown-html /
          ds-markdown-math / ds-markdown-cite / ds-markdown-code-copy-button …，均为正文容器的后代），
          文档序排在容器之后 → "取最后一个有文字的候选"常常只取到**行内碎片**。实测两例：气泡里只显示
          9 字 "<summary>"（回复里带 /// <summary> 文档注释）、或只显示 JSON 的一小片（被工具解析器当成
          工具名 path → "未知工具 path"）；
       ③ 故取到候选后沿父链上溯到"不再被候选包含"的那一层（= 本条消息最外层正文容器）再取文本：
          容器文本必然包含碎片文本，取容器不会丢内容；顶层候选（新消息追加形态）上溯不动，与旧版一致。
       选择器失配时最多返回空串，不影响既有行为。 */
    function lastAnswerText() {
        var nodes = answerNodes();
        for (var i = nodes.length - 1; i >= 0; i--) {
            var el = nodes[i], t = (el.innerText || '').replace(/\s+$/, '');
            if (!t) continue;
            var outer = el;
            for (var up = 0; up < 12 && outer.parentElement; up++) {
                var p = outer.parentElement, isCand = false;
                for (var k = 0; k < nodes.length; k++) { if (nodes[k] === p) { isCand = true; break; } }
                if (!isCand) {
                    // 非候选：纯布局包裹层（文本与当前层一模一样，加了等于没加）继续穿透，
                    // 否则到此为止——再往上可能是跨多轮的列表容器，宁可少取也不能把历史答复当本轮正文
                    if (((p.innerText || '').replace(/\s+$/, '')) === (outer.innerText || '').replace(/\s+$/, '')) { outer = p; continue; }
                    break;
                }
                outer = p;
            }
            if (outer === el) return t;
            var ot = (outer.innerText || '').replace(/\s+$/, '');
            if (ot.length > t.length) {
                var sig = t.length + '>' + ot.length;
                if (sig !== ascentLogged) { ascentLogged = sig; log('正文节点上溯：候选 ' + t.length + ' 字 → 容器 ' + ot.length + ' 字'); }
            }
            return ot.length >= t.length ? ot : t;   // 容器必含碎片文本，保险起见取长的
        }
        return '';
    }

    /* ---------- 深度思考（思维链）抓取 ---------- */
    /* 取"最新一轮"的思考文本：从最后一条正文节点向上逐层（最多 6 层）找 think 候选容器，
       每层命中即取该层**最后一个**匹配（最新），避免把历史轮次的思考当成本轮；取不到返回空串。
       站点选择器失配时只会"没有思考可展示"，绝不影响正文抓取与工具闭环。 */
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
    /* 过滤站点自己的思考 UI 文案（思考结束折叠后的"已深度思考（用时 x 秒）"），只留真正的思维链 */
    function isThinkChrome(s) {
        var t = s.replace(/\s+/g, '');
        return t.length <= 30 && /^已?深度思考/.test(t);
    }

    /* ---------- 取消令牌 ---------- */
    var tokens = {};
    window.__gairrCancel = function (id) { if (tokens[id]) tokens[id].cancelled = true; };

    /* ---------- 新建/重置网页对话（**只做页面内操作，绝不跳转**） ---------- */
    /* 候选已排除带 href 的 a 标签（见 pickByWords/SEL.newChat），杜绝整页导航。
       "点击"不等于"生效"：SPA 路由切换跑在页面自己的事件循环上，JS 里同步等待反而会阻塞切换，
       所以拆成两个同步入口，800ms 校验窗口由调用方（C# / 旧版兼容路径）以 100ms 间隔轮询驱动：
         __gairrNewChat()       点击前记录快照并点击，返回 'clicked'（已点击、待校验）或 'none'（没找到入口）
         __gairrNewChatVerify() 与快照比对：'ok' = 确认已切换（URL 变化 / 消息节点归零 / 输入框清空，
                                任一即可，且只认点击前就有量的信号）；'pending' = 尚未或无法确认
       调用方轮询 800ms 内见到 'ok' 才按 'clicked' 处理，否则按 'none' 处理（C# 走重载路径；
       此时提示词尚未发出，不会丢失也不会打断当前轮）。全部入口返回同步字符串，避免宿主
       ExecuteScriptAsync 对 Promise 的序列化歧义（同 __gairrAsk 的注释）。 */
    var chatSnap = null;   // 点击前快照 { url, msgs, input, t }，供 __gairrNewChatVerify 比对
    function takeChatSnapshot() {
        chatSnap = { url: location.href, msgs: answerNodes().length,
                     input: inputTextOf(pick(SEL.input)), t: Date.now() };
    }
    window.__gairrNewChat = function () {
        try {
            var el = pickByWords(NEW_CHAT_WORDS);
            if (!el) el = pick(SEL.newChat);
            if (!el) { log('未找到"新对话"入口（页面内，带 href 的 a 已排除）；由调用方决定是否重载页面'); return 'none'; }
            takeChatSnapshot();
            log('点击新对话入口：' + ((el.innerText || el.getAttribute('aria-label') || '') + '').trim() + '（待校验切换是否生效）');
            el.click();
            return 'clicked';
        } catch (e) { log('新建对话失败：' + (e && e.message ? e.message : e)); return 'none'; }
    };
    window.__gairrNewChatVerify = function () {
        try {
            if (!chatSnap) return 'pending';
            if (Date.now() - chatSnap.t > 5000) { chatSnap = null; return 'pending'; }  // 快照过期：视为无法确认（防轮询失控）
            if (location.href !== chatSnap.url) { chatSnap = null; return 'ok'; }                   // URL 变化（SPA 路由已切换）
            if (chatSnap.msgs > 0 && answerNodes().length === 0) { chatSnap = null; return 'ok'; }   // 消息节点归零（点击前确有消息）
            if (chatSnap.input !== '' && inputTextOf(pick(SEL.input)) === '') { chatSnap = null; return 'ok'; }  // 输入框清空（点击前确有内容）
            return 'pending';
        } catch (e) { return 'pending'; }
    };

    /* ---------- 对话长度上限自愈（页面提示"达到对话长度上限，请开启新对话"时自动开新会话） ---------- */
    // 触发场景：C# 侧每轮提问前虽会重置对话，但上一轮重置未真正生效（点击未确认、重载后站点仍停在旧会话），
    // 或用户手动在网页里聊到了上限——此时输入框被锁、发送被吞，不自愈就会白等到 15 分钟/20 分钟超时。
    // 检测只认"达到对话长度上限"整句：不能只认"开启新对话"（左侧"新对话"按钮一带就有这个字样，会恒误判）。
    function chatLimitVisible() {
        /* 收窄检测（曾误判：整个 body.innerText 里，左侧会话列表的历史标题、输入框当前文本含此句都算命中——
           实际发送已成功、模型在深度思考还没吐正文时，10s 兜底检测就误报"新会话未真正生效"）。
           规则：逐文本节点找命中，且命中处必须 ① 可见 ② 不在侧栏/导航里 ③ 不在输入框里 ④ 所在文本块很短（站点提示样式） */
        try {
            var KNOWN = '达到对话长度上限';
            var root = document.body;
            if (!root) return false;
            var walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
            var n;
            while ((n = walker.nextNode())) {
                var t = n.nodeValue || '';
                if (t.indexOf(KNOWN) < 0) continue;
                var el = n.parentElement;
                if (!el) continue;
                if (!visible(el)) continue;                                   // ① 隐藏节点不算
                var anc = el, inChrome = false;
                while (anc && anc !== document.documentElement) {
                    var tag = anc.tagName;
                    if (tag === 'NAV' || tag === 'ASIDE' ||
                        ((anc.getAttribute && (anc.getAttribute('role') || '')) + '').indexOf('navigation') >= 0 ||
                        (anc.id || '') === 'chat-input' ||
                        (anc.getAttribute && anc.getAttribute('contenteditable') === 'true') ||
                        tag === 'TEXTAREA') { inChrome = true; break; }       // ② 侧栏/导航 ③ 输入框不算
                    anc = anc.parentElement;
                }
                if (inChrome) continue;
                var block = el;
                for (var b = 0; b < 3 && block.parentElement && (block.parentElement.innerText || '').length < 200; b++)
                    block = block.parentElement;                              // 上溯到提示文案所在的小容器
                var bt = (block.innerText || '').trim();
                if (bt.length <= 100) return true;                            // ④ 站点提示是一行短文案；历史消息里引用此句的都是长段
            }
            return false;
        }
        catch (e) { return false; }
    }
    // 页面内点"新对话"（默认在界面左侧；带 href 的 a 已排除，绝不整页导航）+ 2s 切换校验。
    // 返回 true = 会话确认已切换（DOM 可能重建：调用方必须重新取输入框；若其后再取发送前快照，基线天然正确）。
    async function startNewChatInPage() {
        var nc = window.__gairrNewChat();
        if (nc !== 'clicked') { log('页面内未找到"新对话"入口（左侧按钮）'); return false; }
        for (var v = 0; v < 20; v++) {
            await sleep(100);
            if (window.__gairrNewChatVerify() === 'ok') return true;
        }
        log('点击"新对话"后 2s 内未确认切换（URL/消息节点/输入框均无变化）');
        return false;
    }

    /* ---------- 一轮问答主流程 ---------- */
    /* opts: { id, deepThink, webSearch, timeoutMs, expectNewChat }（expectNewChat 仅兼容旧版 C#：为真时在页面内新建对话；新版重置由 C# 侧提问前完成） */
    async function runAsk(text, opts) {
        opts = opts || {};
        var id = opts.id || String(Date.now());
        var tok = tokens[id] = { cancelled: false };
        var timeoutMs = opts.timeoutMs || 900000;   // 默认 15 分钟：专家模式深思考耗时长

        try {
            /* 等输入框出现（页面可能刚重载完，给足 30s） */
            var box = null;
            for (var i = 0; i < 150 && !box; i++) {
                if (tok.cancelled) { post({ type: 'error', msg: '已取消' }); return; }
                box = pick(SEL.input);
                if (!box) await sleep(200);
            }
            if (!box) { post({ type: 'error', msg: '未找到输入框：请确认页面已加载且已登录（chat.deepseek.com）' }); return; }

            /* 待上一条回复结束再发（避免打断） */
            for (var w = 0; w < 40 && pick(SEL.stop); w++) { post({ type: 'phase', msg: '等待上一条回复结束…' }); await sleep(500); }

            /* 长度上限自愈：页面提示"达到对话长度上限，请开启新对话"时，先点界面左侧"新对话"开新会话再继续。
               切换成功后输入框 DOM 可能重建 → 重新等输入框；其后的发送前快照（before 等）统一在后面取，基线天然正确。
               切换失败不硬发：旧会话已锁，硬发只会白等到超时 → 报明确错因，重发时（下轮）再试。 */
            if (chatLimitVisible()) {
                post({ type: 'phase', msg: '站点已达对话长度上限：点击左侧"新对话"开启新会话…' });
                if (!(await startNewChatInPage())) {
                    post({ type: 'error', msg: '站点已达对话长度上限，且页面内"新对话"点击未生效（未找到入口或 2s 内未确认切换）：请在网页窗口手动开启新对话后重发' });
                    return;
                }
                log('新会话已开启，继续提问');
                box = null;
                for (var i2 = 0; i2 < 50 && !box; i2++) {
                    if (tok.cancelled) { post({ type: 'error', msg: '已取消' }); return; }
                    box = pick(SEL.input);
                    if (!box) await sleep(200);
                }
                if (!box) { post({ type: 'error', msg: '已开启新会话但未找到输入框：请确认页面处于可对话状态' }); return; }
            }

            var deep = clickToggle(['深度思考', 'DeepThink'], opts.deepThink);
            var web = clickToggle(['联网搜索', '搜索', 'Search'], opts.webSearch);
            post({ type: 'phase', msg: '已就绪（深度思考=' + deep + ' 联网搜索=' + web + '），正在输入…' });

            /* 兼容旧版 C#（旧版会传 expectNewChat=true 让 JS 自己开新对话）：在页面内新建对话后再输入，
               绝不整页跳转。新版 C# 已在提问前自行重置，这里 opts.expectNewChat 为空 → 什么都不做。 */
            if (opts.expectNewChat) {
                log('旧版调用：需要重置对话（页面内新建，不重载文档）');
                var vOk = await startNewChatInPage();
                log(vOk ? '已验证新对话切换生效' : '未确认切换（无入口或窗口内未确认）：本次直接在当前对话继续提问（上下文由 C# 整段投递）');
            }

            var before = answerNodes().length;
            var beforeText = lastAnswerText();   // 发送前"末条正文"快照：站点原地替换节点（计数不增）时靠它识别本轮正文
            var beforeThink = thinkText();
 /* 末尾强制追加 JSON 格式要求（与 kimi 通道一致）：未包含该标记才追加，避免重复叠加 */
 var jsonTag = '【返回标准的json格式】';
 if (text.indexOf(jsonTag) < 0) text += '\n\n' + jsonTag;   // 发送前最新一轮的思考文本：用于排除"历史轮的思考"被当成本轮
            setInput(box, text);
            await sleep(150);
            if (!inputTextOf(box)) {           // 首次写入被框架吞掉（React 竞态）→ 再写一次
                log('输入框首次写入未生效，重试一次');
                setInput(box, text);
                await sleep(200);
            }
            if (!inputTextOf(box)) { post({ type: 'error', msg: '已定位输入框但写入失败（页面结构可能已改版）' }); return; }

            /* 发送：优先点发送按钮，点不动/没找到改 Enter 提交。**
               关键：每步都验证"确实发出去了"（输入框被清空 或 出现新的回复节点），
               否则错误地"点了某个按钮但没发"会让上层白等十几分钟——这正是上一版
               "页面什么都没收到"的观感来源。 */
            async function sentOK() {
                for (var v = 0; v < 15; v++) {
                    await sleep(200);
                    var b2 = pick(SEL.input);                       // 重新取：发送后 React 可能重建输入框
                    if (!b2 || !inputTextOf(b2)) return true;        // 输入框被清空 = 提交成功
                    if (answerNodes().length > before) return true;  // 已出现新回复节点
                }
                return false;
            }
            async function trySend(how) {
                if (how === 'click') {
                    var btn = null;
                    for (var s = 0; s < 20 && !btn; s++) {   // 发送按钮在有文字后才出现/变可用
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
                log((how === 'click' ? '点击发送按钮' : 'Enter 提交') + (ok ? '已生效（输入框已清空/出现新回复）' : '未生效（输入框仍有文字且无新回复）'));
                return ok;
            }
            var sent = await trySend('click');
            if (!sent) sent = await trySend('enter');
            if (!sent && chatLimitVisible()) {
                /* 发送未生效且此刻页面出现上限提示 = 发送前页面还没锁（提示在发送时才出现）：
                   开新会话后整段重发一次（本轮提示词由 C# 整段携带，重发不会重复历史）。 */
                log('发送未生效且检测到"达到对话长度上限"：开新会话后重发一次');
                post({ type: 'phase', msg: '站点已达对话长度上限：点击左侧"新对话"开启新会话后重发…' });
                if (await startNewChatInPage()) {
                    box = null;
                    for (var i3 = 0; i3 < 50 && !box; i3++) { box = pick(SEL.input); if (!box) await sleep(200); }
                    if (box) {
                        before = answerNodes().length;   // 刷新基线：新会话里旧的节点数/末条正文/思考快照已失效
                        beforeText = lastAnswerText();
                        beforeThink = thinkText();
                        setInput(box, text);
                        await sleep(150);
                        if (!inputTextOf(box)) { setInput(box, text); await sleep(200); }
                        if (inputTextOf(box)) sent = (await trySend('click')) || (await trySend('enter'));
                    }
                }
            }
            if (!sent) {
                post({ type: 'error', msg: '文字已写入输入框但发送未生效：请确认已登录且页面可对话；若站点刚改版请把 log/webmodel.log 与 F12 控制台反馈' });
                return;
            }
            // 页面确证"本条已送出"（输入框已清空 / 已出现新回复节点）→ 结构化上报：
            // C# 侧据 delivered 标记区分"投递前失败"与"已投递后异常"，只用于日志判据
            // （网页通道每轮都重置对话并整段投递完整上下文，不再有跨轮同步点需要维护）
            post({ type: 'sent' });
            post({ type: 'phase', msg: '已发送，等待回复…' });

            var last = '', lastThink = '', stable = 0, started = false;
            var maxLen = 0, grewAt = Date.now(), beatAt = Date.now(), limitBeatAt = 0, nodeCount = before;
            var deadline = Date.now() + timeoutMs;
            while (Date.now() < deadline) {
                if (tok.cancelled) { post({ type: 'error', msg: '已取消' }); return; }
                await sleep(300);

                // 思维链（深度思考块）：整段累计上报，C# 侧差分后既进"思考中"直播卡、也作为本轮思考内容留存；
                // 与发送前快照相同 = 还是上一轮的思考，不上报；站点折叠文案（"已深度思考（用时 x 秒）"）过滤掉
                var tk = thinkText();
                if (tk && tk !== beforeThink && tk !== lastThink && !isThinkChrome(tk)) {
                    post({ type: 'reasoning', text: tk });
                    lastThink = tk;
                }

                var nodes = answerNodes();
                nodeCount = nodes.length;
                var cur = lastAnswerText();
                /* 本轮正文判定（两条命中任一即可；旧版只看节点数，缺了②就会"网页已出结果却空转到超时"）：
                   ① 正文节点数增加 = 新答复追加在末尾（站点常规行为）；
                   ② 末条正文与发送前快照不同 = 站点原地替换了上一轮节点（节点数不变）。
                   注意用 lastAnswerText() 而非 nodes[last]：站点末尾会挂空占位/光标容器，直接取会拿到空串。 */
                if (cur && (nodes.length > before || cur !== beforeText)) {
                    started = true;
                    if (cur !== last) { post({ type: 'delta', text: cur }); last = cur; stable = 0; }
                    else stable++;
                    if (cur.length > maxLen) { maxLen = cur.length; grewAt = Date.now(); }   // 只在"变长"时算进展：光标闪烁/计时器等抖动不算
                }

                // 心跳日志（每 10s 一条）：下次再卡住，看 webmodel.log 就能判断是"页面没出正文"还是"正文在变但收不了尾"
                if (Date.now() - beatAt >= 10000) {
                    beatAt = Date.now();
                    log('等待中：正文节点 ' + nodeCount + ' 个、末条 ' + cur.length + ' 字、已收 ' + last.length +
                        ' 字、思考 ' + lastThink.length + ' 字、已等待 ' + Math.round((timeoutMs - (deadline - Date.now())) / 1000) + 's');
                }

                /* 长度上限兜底（节流：约每 10s 查一次 body.innerText，且仅在还没收到正文时查）：
                   "发送成功"但会话其实没切换（校验假通过）时，上限提示会在等待期间出现——
                   不必白等到 15 分钟超时，直接报明确错因；重发时（下轮）会自动开新会话。 */
                if (!started && Date.now() - limitBeatAt >= 10000) {
                    limitBeatAt = Date.now();
                    if (chatLimitVisible()) {
                        post({ type: 'error', msg: '发送后站点出现"达到对话长度上限"提示：新会话未真正生效，本轮拿不到回复，请重发（下轮会自动开启新会话）' });
                        return;
                    }
                }

                /* 收尾：正文连续 8 拍（≈2.4s）不变；或已出现正文后 90s 再无新增（看门狗兜底，
                   防"页面已出结果却不收尾"空转到 20 分钟超时）。不依赖站点"停止"按钮选择器——选择器失配会误判提前收尾 */
                if (started && (stable >= 8 || Date.now() - grewAt >= 90000)) break;
            }

            if (!started) {
                /* 超时错因带上 DOM 采样（正文节点数 + 前 3 个节点的 class 与文本长度）：
                   下次再出现"网页已出结果却收不了尾"，一眼就能看出是选择器失配、还是节点被空占位顶掉。 */
                var sample = '', ns = answerNodes();
                for (var si = 0; si < ns.length && si < 3; si++) sample += ' / ' + (ns[si].className || '?') + '→' + ((ns[si].innerText || '').length) + '字';
                var limitHint = chatLimitVisible() ? '；且页面仍显示"达到对话长度上限"提示：请先在网页窗口手动开启新对话' : '';
                post({ type: 'error', msg: '超时未捕获到回复正文（正文节点 ' + nodeCount + ' 个' + sample + '、思考 ' + lastThink.length + ' 字）：请确认已登录、且页面处于可对话状态，并把 log/webmodel.log 反馈' + limitHint });
                return;
            }
            log('收尾：正文 ' + last.length + ' 字（稳定 ' + stable + ' 拍' + (Date.now() - grewAt >= 90000 ? '，看门狗触发：90s 无新增' : '') + '）');
            post({ type: 'done', text: last });
        } catch (e) {
            post({ type: 'error', msg: '适配脚本异常：' + (e && e.message ? e.message : e) });
        } finally {
            delete tokens[id];
        }
    }

    /* C# 侧入口：同步返回 'started'（真结果经 postMessage 回传）。
       返回同步字符串而非 Promise，避免宿主 ExecuteScriptAsync 对 Promise 的序列化歧义。 */
    window.__gairrAsk = function (text, opts) {
        runAsk(text, opts);
        return 'started';
    };

    log('DeepSeek 适配脚本已注入');
})();
