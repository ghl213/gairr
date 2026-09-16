/* ============================================================
 pages.js · 页面级增强
 ① data-ico 图标注入 / 输入框前导图标 / 文本域自适应
 ② Agent 列表：按「适用场景」分组 + 筛选（admin-agents.html，A-04）
 依赖 app.js 暴露的 GAIRR.icon / GAIRR.state
 ============================================================ */
(function () {
 'use strict';
 var G = window.GAIRR || {};

 /* 把 data-ico="name" 的元素内容换成对应 SVG */
 function injectIcons () {
 if (!G.icon) return;
 document.querySelectorAll('[data-ico]').forEach(function (el) {
 if (el.querySelector('svg')) return;
 el.insertAdjacentHTML('afterbegin', G.icon(el.getAttribute('data-ico')));
 });
 }

 /* 文本域随内容自适应高度（演示用，避免出现滚动条） */
 function autoGrow () {
 document.querySelectorAll('textarea.textarea').forEach(function (t) {
 function fit () { t.style.height = 'auto'; t.style.height = Math.min(t.scrollHeight + 2, 420) + 'px'; }
 t.addEventListener('input', fit);
 fit();
 });
 }

 /* ============================================================
 Agent 列表（A-04）：适用场景 = Agent 面向的业务场景
 —— 数据维度，与「受限编排」的流程图不是一回事
 ============================================================ */
 var SCENES = ['客户服务', '知识问答', '数据分析', '供应链采购', '流程审批', '办公协同', '研发辅助'];
 var SCENE_DESC = {
 客户服务: '对外/对内一线问答、查单、发起工单，常带人工升级',
 知识问答: '制度、政策、规范类检索问答，回答须给引用',
 数据分析: '查数仓、算指标、出图表，导出属写操作',
 供应链采购: '收报价单与归档、供应商横向比价、价格核对与加价出单',
 流程审批: '需要人工确认的写操作与业务审批流',
 办公协同: '日报周报、会议纪要、任务跟进',
 研发辅助: '排障、日志分析、代码与接口查询'
 };

 var AGENTS = [
 { name:'报价单收单比价智能体', code:'agent_quote_compare', scenes:['供应链采购', '流程审批'], status:'已发布', statusCls:'tag-ok', version:'v3', owner:'陈可', calls:'6,410', updated:'10-03 09:12', desc:'企微/微信自动收报价单并落盘归档，Excel 解析后生成多供应商横向比价表；加价出单低于毛利底线转人工审批' },
 { name:'客服助手', code:'agent_cs_helper', scenes:['客户服务', '知识问答'], status:'已发布', statusCls:'tag-ok', version:'v12', owner:'陈可', calls:'18,204', updated:'10-02 14:12', desc:'面向 C 端客户的售后问答，可查订单、查政策、发起工单' },
 { name:'制度问答助手', code:'agent_policy_qa', scenes:['知识问答', '办公协同'], status:'已发布', statusCls:'tag-ok', version:'v3', owner:'李维', calls:'12,480', updated:'10-01 17:05', desc:'回答公司制度类问题，必须给出条款引用，不确定时明确拒答' },
 { name:'数据分析 Agent', code:'agent_data_analyst', scenes:['数据分析'], status:'已发布', statusCls:'tag-ok', version:'v2', owner:'王强', calls:'8,431', updated:'09-30 11:20', desc:'自然语言转 SQL 查数仓，自动画趋势图；导出明细需本人确认' },
 { name:'法务审阅 Agent', code:'agent_legal_review', scenes:['流程审批', '知识问答'], status:'已发布', statusCls:'tag-ok', version:'v4', owner:'吴敏', calls:'2,106', updated:'09-28 16:40', desc:'合同条款风险初筛，输出风险等级与修改建议，不出法律意见结论' },
 { name:'报销单预审 Agent', code:'agent_expense_precheck', scenes:['流程审批'], status:'灰度 30%', statusCls:'tag-warn', version:'v1', owner:'周敏', calls:'612', updated:'10-02 09:30', desc:'校验发票与行程单标准、识别缺件，金额超限自动转人工复核' },
 { name:'HR 助手', code:'agent_hr_assistant', scenes:['知识问答', '客户服务'], status:'已发布', statusCls:'tag-ok', version:'v2', owner:'刘芳', calls:'3,980', updated:'09-26 10:15', desc:'考勤、假期、证明开具流程咨询；涉及他人薪酬绩效一律拒答' },
 { name:'运维排障助手', code:'agent_ops_troubleshoot', scenes:['研发辅助'], status:'已发布', statusCls:'tag-ok', version:'v5', owner:'赵磊', calls:'3,120', updated:'09-25 20:02', desc:'按报错日志与拓扑定位故障点，只读查询，重启需审批' },
 { name:'我的周报助手', code:'agent_weekly_report', scenes:['办公协同'], status:'草稿', statusCls:'tag-outline', version:'—', owner:'张三', calls:'0', updated:'10-02 15:40', desc:'拉取本周工单与提交记录，按模板生成周报草稿，尚未通过评审' }
 ];

 var folded = {};        /* 场景分组折叠状态：{客户服务:true} */
 var viewMode = 'group'; /* group=按场景分组 / flat=平铺列表 */
 var activeScene = '';   /* 当前筛选的场景，空=全部 */
 var keyword = '';
 var currentName = '客服助手'; /* 编辑器中正在编辑的 Agent */

 var HEAD = '<thead><tr><th>Agent</th><th>适用场景</th><th>状态</th><th>版本</th><th>负责人</th>'
  + '<th class="num">近 7 日调用</th><th>更新时间</th><th></th></tr></thead>';

 function esc (s) {
 return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
 }
 function byScene (s) { return AGENTS.filter(function (a) { return a.scenes.indexOf(s) >= 0; }); }
 function hit (a, kw) {
 if (!kw) return true;
 return (a.name + a.code + a.owner + a.desc + a.scenes.join(' ')).toLowerCase().indexOf(kw) >= 0;
 }

 function sceneTags (a, cur) {
 return a.scenes.map(function (s) {
 return '<span class="tag ' + (s === cur ? 'tag-brand' : 'tag-outline') + '">' + s + '</span>';
 }).join('');
 }

 function rowHtml (a, cur) {
 return '<tr class="agent-row' + (a.name === currentName ? ' is-selected' : '') + '">'
  + '<td><div class="cell-2"><span class="l1">' + esc(a.name) + '</span><span class="l2">' + esc(a.code) + '</span></div></td>'
  + '<td><div class="row wrap gap-1">' + sceneTags(a, cur) + '</div></td>'
  + '<td><span class="tag ' + a.statusCls + '"><span class="td"></span>' + esc(a.status) + '</span></td>'
  + '<td class="mono t-sm">' + esc(a.version) + '</td>'
  + '<td class="t-sm">' + esc(a.owner) + '</td>'
  + '<td class="num">' + esc(a.calls) + '</td>'
  + '<td class="t-sm muted">' + esc(a.updated) + '</td>'
  + '<td class="actions">'
  + '<button class="btn btn-sm" data-edit="' + esc(a.name) + '">编辑</button>'
  + '<button class="btn btn-sm btn-ghost" data-demo="复制 ' + esc(a.name) + '">复制</button>'
  + '</td></tr>';
 }

 function renderSceneBar () {
 var bar = document.getElementById('agent-scene-filter');
 if (!bar) return;
 var h = '<button class="chip' + (activeScene ? '' : ' is-on') + '" data-scene="">全部 <b class="mono">' + AGENTS.length + '</b></button>';
 SCENES.forEach(function (s) {
 h += '<button class="chip' + (activeScene === s ? ' is-on' : '') + '" data-scene="' + s + '">'
  + s + ' <b class="mono">' + byScene(s).length + '</b></button>';
 });
 bar.innerHTML = h;
 }

 function renderList () {
 var host = document.getElementById('agent-list');
 if (!host) return;
 var kw = keyword.trim().toLowerCase();
 var rows = AGENTS.filter(function (a) {
 return hit(a, kw) && (!activeScene || a.scenes.indexOf(activeScene) >= 0);
 });

 var h = '';
 if (!rows.length) {
 h = G.state('empty', {
 title: '这个场景下还没有匹配的 Agent',
 desc: '换个场景筛选或清空搜索词；也可以新建一个 Agent 并标注适用场景。',
 act: '新建 Agent'
 });
 } else if (viewMode === 'flat') {
 h = '<div class="table-wrap"><table class="data">' + HEAD + '<tbody>'
  + rows.map(function (a) { return rowHtml(a, ''); }).join('') + '</tbody></table></div>';
 } else {
 SCENES.forEach(function (s) {
 if (activeScene && activeScene !== s) return;
 var list = rows.filter(function (a) { return a.scenes.indexOf(s) >= 0; });
 if (!list.length) return;
 h += '<div class="scene-grp' + (folded[s] ? ' is-folded' : '') + '">';
 h += '<div class="scene-grp-head" data-fold="' + s + '">'
  + '<span class="scene-caret" data-ico="chev"></span>'
  + '<span class="scene-name">' + s + '</span>'
  + '<span class="tag tag-outline tag-mono">' + list.length + '</span>'
  + '<span class="scene-desc truncate">' + esc(SCENE_DESC[s] || '') + '</span>'
  + '<span class="grow"></span>'
  + '<span class="t-xs dim">' + (folded[s] ? '已折叠' : '点击折叠') + '</span></div>';
 h += '<div class="scene-grp-body"><table class="data">' + HEAD + '<tbody>'
  + list.map(function (a) { return rowHtml(a, s); }).join('') + '</tbody></table></div>';
 h += '</div>';
 });
 }

 var multi = AGENTS.filter(function (a) { return a.scenes.length > 1; }).length;
 var scenes = SCENES.filter(function (s) { return byScene(s).length; }).length;
 h += '<div class="row row-gap t-xs dim mt-1"><span>共 ' + rows.length + ' / ' + AGENTS.length + ' 个 Agent</span>'
  + '<span>·</span><span>覆盖 ' + scenes + ' 个场景</span>'
  + '<span>·</span><span>' + multi + ' 个跨场景 Agent（会出现在每个所属分组下）</span></div>';

 host.innerHTML = h;
 injectIcons();
 }

 function render () { renderSceneBar(); renderList(); }

 /* 点「编辑」：把编辑器切到该 Agent（静态原型：只切展示，不重载表单全部字段） */
 function focusAgent (name) {
 var a = AGENTS.filter(function (x) { return x.name === name; })[0];
 if (!a) return;
 currentName = name;
 var t = document.getElementById('edit-title'); if (t) t.textContent = name;
 var c = document.getElementById('edit-crumb'); if (c) c.textContent = name;
 var n = document.getElementById('edit-name-inline'); if (n) n.textContent = name;
 var st = document.getElementById('edit-state');
 if (st) { st.className = 'tag ' + a.statusCls; st.innerHTML = '<span class="td"></span>' + esc(a.status) + ' · ' + esc(a.version); }
 var sc = document.getElementById('edit-scene'); if (sc) sc.value = a.scenes[0];
 var cd = document.getElementById('edit-code'); if (cd) cd.value = a.code;
 render();
 var sec = document.getElementById('editor-section');
 if (sec) sec.scrollIntoView({ behavior: 'smooth', block: 'start' });
 }

 function bindAgentList () {
 if (!document.getElementById('agent-list')) return;
 document.addEventListener('click', function (e) {
 if (!e.target.closest) return;
 var ed = e.target.closest('[data-edit]');
 if (ed) { focusAgent(ed.getAttribute('data-edit')); return; }
 var sc = e.target.closest('[data-scene]');
 if (sc) { activeScene = sc.getAttribute('data-scene'); render(); return; }
 var vw = e.target.closest('[data-view]');
 if (vw) { viewMode = vw.getAttribute('data-view'); render(); return; }
 var fd = e.target.closest('[data-fold]');
 if (fd) { var k = fd.getAttribute('data-fold'); folded[k] = !folded[k]; render(); return; }
 var dm = e.target.closest('#agent-list [data-demo], #agent-scene-filter [data-demo]');
 if (dm) alert('静态原型：该操作仅作演示，未接后端。\n\n动作：' + (dm.getAttribute('data-demo') || dm.textContent.trim()));
 });
 var box = document.getElementById('agent-search');
 if (box) box.addEventListener('input', function () { keyword = box.value; renderList(); });
 render();
 }

 function boot () {
 bindAgentList();
 injectIcons();
 autoGrow();
 }

 if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot);
 else boot();
})();
