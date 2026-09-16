/* ============================================================
 app.js · Agent 平台静态原型共享脚本
 导航注入 / 主题 / Tabs / 分段 / 抽屉 / 表格 / 四态 / 流式模拟
 ============================================================ */
(function () {
 'use strict';
 var G = (window.GAIRR = window.GAIRR || {});

 /* ---------- 图标 ---------- */
 var ICON = {
 grid:'<path d="M3 3h7v7H3zM14 3h7v7h-7zM3 14h7v7H3zM14 14h7v7h-7z"/>',
 bot:'<rect x="4" y="8" width="16" height="12" rx="2"/><path d="M12 8V4"/><circle cx="9" cy="14" r="1"/><circle cx="15" cy="14" r="1"/>',
 wrench:'<path d="M15 4a5 5 0 0 0-4.5 7.2L4 17.7 6.3 20l6.5-6.5A5 5 0 1 0 15 4z"/>',
 book:'<path d="M4 4h9a3 3 0 0 1 3 3v13H7a3 3 0 0 0-3 3z"/><path d="M20 4h-4v16h4z"/>',
 cpu:'<rect x="7" y="7" width="10" height="10" rx="2"/><path d="M4 10h3M4 14h3M17 10h3M17 14h3M10 4v3M14 4v3M10 17v3M14 17v3"/>',
 shield:'<path d="M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6z"/>',
 check:'<path d="M20 6L9 17l-5-5"/>',
 activity:'<path d="M3 12h4l3 8 4-16 3 8h4"/>',
 building:'<path d="M4 21V5a2 2 0 0 1 2-2h8a2 2 0 0 1 2 2v16M16 9h2a2 2 0 0 1 2 2v10M8 7h4M8 11h4M8 15h4"/>',
 users:'<circle cx="9" cy="8" r="3"/><path d="M3 20a6 6 0 0 1 12 0"/><path d="M17 11a3 3 0 1 0 0-6"/><path d="M21 20a5 5 0 0 0-4-4.9"/>',
 chart:'<path d="M4 20V10M10 20V4M16 20v-7M22 20H2"/>',
 file:'<path d="M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z"/><path d="M14 3v5h5"/>',
 settings:'<circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.6 1.6 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.6 1.6 0 0 0-1.8-.3 1.6 1.6 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1A1.6 1.6 0 0 0 8.5 19a1.6 1.6 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.6 1.6 0 0 0 .3-1.8 1.6 1.6 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1A1.6 1.6 0 0 0 4.6 8.5a1.6 1.6 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.6 1.6 0 0 0 1.8.3H9a1.6 1.6 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.6 1.6 0 0 0 1 1.5 1.6 1.6 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.6 1.6 0 0 0-.3 1.8V9a1.6 1.6 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.6 1.6 0 0 0-1.5 1z"/>',
 plug:'<path d="M9 2v6M15 2v6M7 8h10v3a5 5 0 0 1-10 0zM12 16v6"/>',
 message:'<path d="M21 12a8 8 0 0 1-8 8H7l-4 3V12a8 8 0 0 1 8-8h2a8 8 0 0 1 8 8z"/>',
 search:'<circle cx="11" cy="11" r="7"/><path d="M21 21l-4-4"/>',
 bell:'<path d="M18 8a6 6 0 1 0-12 0c0 7-3 9-3 9h18s-3-2-3-9M13.7 21a2 2 0 0 1-3.4 0"/>',
 sun:'<circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4"/>',
 moon:'<path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z"/>',
 menu:'<path d="M3 6h18M3 12h18M3 18h18"/>',
 plus:'<path d="M12 5v14M5 12h14"/>',
 more:'<circle cx="5" cy="12" r="1"/><circle cx="12" cy="12" r="1"/><circle cx="19" cy="12" r="1"/>',
 stop:'<rect x="6" y="6" width="12" height="12" rx="2"/>',
 send:'<path d="M4 12l16-8-6 16-3-6z"/>',
 lock:'<rect x="4" y="10" width="16" height="11" rx="2"/><path d="M8 10V7a4 4 0 0 1 8 0v3"/>',
 alert:'<path d="M12 3l10 18H2z"/><path d="M12 9v5M12 17h.01"/>',
 copy:'<rect x="9" y="9" width="12" height="12" rx="2"/><path d="M5 15V5a2 2 0 0 1 2-2h10"/>',
 ext:'<path d="M15 3h6v6M10 14L21 3M21 14v5a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5"/>',
 refresh:'<path d="M21 12a9 9 0 1 1-3-6.7L21 8"/><path d="M21 3v5h-5"/>',
 play:'<path d="M6 4l14 8-14 8z"/>',
 clock:'<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>',
 download:'<path d="M12 3v12M7 10l5 5 5-5M4 21h16"/>',
 filter:'<path d="M3 5h18l-7 8v6l-4-2v-4z"/>',
 x:'<path d="M6 6l12 12M18 6L6 18"/>',
 chev:'<path d="M9 6l6 6-6 6"/>'
 };
 G.icon = function (name, cls) {
 var d = ICON[name] || ICON.grid;
 return '<svg class="' + (cls || '') + '" viewBox="0 0 24 24" aria-hidden="true">' + d + '</svg>';
 };

 /* ---------- 导航定义 ---------- */
 var NAV_ADMIN = [
 { group:'总览', items:[{ code:'A-01', label:'仪表盘', href:'admin-dashboard.html', icon:'grid' }] },
 { group:'构建域', items:[
 { code:'A-04', label:'Agent 管理', href:'admin-agents.html', icon:'bot' },
 { code:'A-05', label:'工具管理', href:'admin-tools.html', icon:'wrench' },
 { code:'A-06', label:'知识库', href:'admin-knowledge.html', icon:'book' },
 { code:'A-07', label:'模型与路由', href:'admin-models.html', icon:'cpu' }]},
 { group:'治理域', items:[
 { code:'A-08', label:'策略管理', href:'admin-policies.html', icon:'shield' },
 { code:'A-09', label:'审批中心', href:'admin-approvals.html', icon:'check', badge:8 },
 { code:'A-10', label:'任务监控', href:'admin-tasks.html', icon:'activity' }]},
 { group:'运营域', items:[
 { code:'A-02', label:'租户管理', href:'admin-tenants.html', icon:'building' },
 { code:'A-03', label:'用户与角色', href:'admin-users.html', icon:'users' },
 { code:'A-12', label:'用量与配额', href:'admin-usage.html', icon:'chart' }]},
 { group:'审计域', items:[{ code:'A-11', label:'审计查询', href:'admin-audit.html', icon:'file' }]},
 { group:'系统域', items:[
 { code:'A-13', label:'系统与健康', href:'admin-settings.html', icon:'settings' },
 { code:'A-14', label:'连接器', href:'admin-connectors.html', icon:'plug' }]}
 ];

 var NAV_USER = [
 { code:'U-02', label:'对话', href:'chat.html', icon:'message' },
 { code:'U-04', label:'Agent 广场', href:'agents.html', icon:'bot' },
 { code:'U-05', label:'任务与审批', href:'tasks.html', icon:'check' },
 { code:'U-06', label:'知识检索', href:'knowledge-search.html', icon:'search' },
 { code:'U-07', label:'我的用量', href:'usage.html', icon:'chart' },
 { code:'U-08', label:'偏好与凭证', href:'settings.html', icon:'settings' }
 ];

 function currentFile () {
 var p = location.pathname.split('/');
 return p[p.length - 1] || 'index.html';
 }

 function injectAdminNav () {
 var host = document.getElementById('app-nav');
 if (!host) return;
 var cur = currentFile();
 var h = '';
 h += '<div class="brand"><span class="brand-mark">GA</span>';
 h += '<span class="brand-name">Agent 平台<em>Admin</em></span></div>';
 h += '<div class="nav-scroll">';
 NAV_ADMIN.forEach(function (g) {
 h += '<div class="nav-group"><div class="nav-group-title"><span>' + g.group + '</span></div>';
 g.items.forEach(function (it) {
 var on = it.href === cur ? ' is-active' : '';
 h += '<a class="nav-item' + on + '" href="' + it.href + '" title="' + it.label + '">';
 h += G.icon(it.icon, 'nav-ico');
 h += '<span class="nav-label">' + it.label + '</span>';
 h += it.badge ? '<span class="nav-badge">' + it.badge + '</span>' : '<span class="nav-code">' + it.code + '</span>';
 h += '</a>';
 });
 h += '</div>';
 });
 h += '</div>';
 h += '<div class="nav-foot"><div class="nav-foot-row"><span class="env-dot"></span><span class="nav-foot-meta">生产 · v1.0.0</span></div></div>';
 host.className = 'app-nav';
 host.innerHTML = h;
 }

 function injectUserNav () {
 var host = document.getElementById('user-nav');
 if (!host) return;
 var cur = currentFile();
 var h = '';
 NAV_USER.forEach(function (it) {
 var on = it.href === cur ? ' is-on' : '';
 h += '<a class="unav' + on + '" href="' + it.href + '">' + it.label + '</a>';
 });
 host.innerHTML = h;
 }

 /* ---------- 主题 ---------- */
 var THEME_KEY = 'gairr.theme';
 function applyTheme (t) {
 document.documentElement.setAttribute('data-theme', t);
 try { localStorage.setItem(THEME_KEY, t); } catch (e) {}
 document.querySelectorAll('[data-theme-toggle]').forEach(function (b) {
 b.innerHTML = G.icon(t === 'dark' ? 'sun' : 'moon');
 });
 }
 G.toggleTheme = function () {
 var cur = document.documentElement.getAttribute('data-theme') === 'dark' ? 'dark' : 'light';
 applyTheme(cur === 'dark' ? 'light' : 'dark');
 };

 /* ---------- 交互 ---------- */
 function initSidebarToggle () {
 document.querySelectorAll('[data-nav-toggle]').forEach(function (b) {
 b.addEventListener('click', function () {
 var app = document.querySelector('.app');
 if (app) app.classList.toggle('is-collapsed');
 });
 });
 }

 function initTabs () {
 document.querySelectorAll('[data-tabs]').forEach(function (bar) {
 var tabs = bar.querySelectorAll('[data-tab]');
 tabs.forEach(function (t) {
 t.addEventListener('click', function () {
 tabs.forEach(function (x) { x.classList.remove('is-on'); });
 t.classList.add('is-on');
 var root = document.querySelector(bar.getAttribute('data-tabs')) || document;
 root.querySelectorAll('[data-pane]').forEach(function (p) {
 p.classList.toggle('is-on', p.getAttribute('data-pane') === t.getAttribute('data-tab'));
 });
 });
 });
 });
 }

 function initSeg () {
 document.querySelectorAll('[data-seg]').forEach(function (grp) {
 grp.querySelectorAll('button').forEach(function (b) {
 b.addEventListener('click', function () {
 grp.querySelectorAll('button').forEach(function (x) { x.classList.remove('is-on'); });
 b.classList.add('is-on');
 });
 });
 });
 }

 function initDrawer () {
 document.querySelectorAll('[data-drawer-open]').forEach(function (b) {
 b.addEventListener('click', function () {
 var id = b.getAttribute('data-drawer-open');
 var d = document.getElementById(id);
 var s = document.getElementById(id + '-scrim');
 if (d) d.classList.add('is-open');
 if (s) s.classList.add('is-open');
 });
 });
 document.querySelectorAll('[data-drawer-close]').forEach(function (b) {
 b.addEventListener('click', function () {
 var id = b.getAttribute('data-drawer-close');
 var d = document.getElementById(id);
 var s = document.getElementById(id + '-scrim');
 if (d) d.classList.remove('is-open');
 if (s) s.classList.remove('is-open');
 });
 });
 document.querySelectorAll('.scrim').forEach(function (s) {
 s.addEventListener('click', function () {
 var id = s.id.replace('-scrim', '');
 var d = document.getElementById(id);
 if (d) d.classList.remove('is-open');
 s.classList.remove('is-open');
 });
 });
 }

 function initDemoButtons () {
 document.querySelectorAll('[data-demo]').forEach(function (b) {
 b.addEventListener('click', function () {
 alert('静态原型：该操作仅作演示，未接后端。\n\n动作：' + (b.getAttribute('data-demo') || b.textContent.trim()));
 });
 });
 }

 /* ---------- 表格渲染 ---------- */
 G.renderTable = function (sel, cols, rows) {
 var host = typeof sel === 'string' ? document.querySelector(sel) : sel;
 if (!host) return;
 var h = '<table class="data"><thead><tr>';
 cols.forEach(function (c) { h += '<th class="' + (c.cls || '') + (c.num ? ' num' : '') + '">' + c.v + '</th>'; });
 h += '</tr></thead><tbody>';
 rows.forEach(function (r) {
 h += '<tr>';
 cols.forEach(function (c) {
 var v = typeof c.k === 'function' ? c.k(r) : (r[c.k] == null ? '' : r[c.k]);
 h += '<td class="' + (c.cls || '') + (c.num ? 'num' : '') + '">' + v + '</td>';
 });
 h += '</tr>';
 });
 h += '</tbody></table>';
 host.innerHTML = h;
 };

 /* ---------- 四态 ---------- */
 G.state = function (kind, o) {
 o = o || {};
 var m = {
 empty: { ico:'plus', t:o.title || '这里还没有内容', d:o.desc || '创建第一条记录后，它会出现在这里。', a:o.act || '创建' },
 error: { ico:'alert', t:o.title || '加载失败', d:o.desc || '请检查网络后重试。', a:o.act || '重试', code:o.code || 'SYS-9001' },
 denied: { ico:'lock', t:o.title || '需要更高权限', d:o.desc || '当前角色无法访问该资源，可向管理员申请。', a:o.act || '申请权限' },
 loading: { ico:'refresh', t:o.title || '加载中', d:o.desc || '正在获取数据…', a:'' }
 }[kind] || {};
 var h = '<div class="state state-' + kind + '">';
 if (kind === 'loading') {
 h += '<div class="state-ico"><span class="spinner" style="width:22px;height:22px;border-width:3px"></span></div>';
 } else {
 h += '<div class="state-ico">' + G.icon(m.ico) + '</div>';
 }
 h += '<div class="state-title">' + m.t + '</div><div class="state-desc">' + m.d + '</div>';
 if (kind === 'error' && m.code) h += '<div class="state-code">' + m.code + '</div>';
 if (m.a) h += '<div class="state-actions"><button class="btn btn-primary btn-sm" data-demo="' + m.a + '">' + m.a + '</button></div>';
 return h + '</div>';
 };

 /* ---------- 流式模拟 ---------- */
 G.streamText = function (el, text, done) {
 var i = 0;
 var cursor = document.createElement('span');
 cursor.className = 'cursor';
 el.appendChild(cursor);
 (function step () {
 if (i >= text.length) { cursor.remove(); if (done) done(); return; }
 var n = Math.random() < 0.4 ? 2 : 1;
 cursor.insertAdjacentText('beforebegin', text.slice(i, i + n));
 i += n;
 setTimeout(step, 18 + Math.random() * 26);
 })();
 };

 /* ---------- 启动 ---------- */
 function boot () {
 var t = 'light';
 try { t = localStorage.getItem(THEME_KEY) || 'light'; } catch (e) {}
 applyTheme(t);
 var shell = document.body.getAttribute('data-shell');
 if (shell === 'admin') injectAdminNav();
 if (shell === 'user') injectUserNav();
 document.querySelectorAll('[data-theme-toggle]').forEach(function (b) { b.addEventListener('click', G.toggleTheme); });
 initSidebarToggle(); initTabs(); initSeg(); initDrawer(); initDemoButtons();
 }
 if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot);
 else boot();
})();
