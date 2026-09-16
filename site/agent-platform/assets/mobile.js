/* ============================================================
 mobile.js · 移动端 H5 原型共享脚本
 底部 Tab 注入 / 状态栏时间 / Toast / 底部弹层 / 选项组 / 属性事件
 依赖 app.js 暴露的 GAIRR.icon
 ============================================================ */
(function () {
  'use strict';
  var G = window.GAIRR || {};
  function ico(n, c) { return G.icon ? G.icon(n, c) : ''; }

  /* ---------- 底部 Tab ---------- */
  var TABS = [
    { label: '工作台', href: 'm-index.html', icon: 'grid' },
    { label: '收单', href: 'm-intake.html', icon: 'file' },
    { label: '比对', href: 'm-compare.html', icon: 'chart' },
    { label: '问答', href: 'm-chat.html', icon: 'message' },
    { label: '跟踪', href: 'm-logistics.html', icon: 'activity' }
  ];

  function injectTab () {
    var host = document.getElementById('m-tab');
    if (!host) return;
    var cur = location.pathname.split('/').pop() || 'm-index.html';
    host.innerHTML = TABS.map(function (t) {
      var on = t.href === cur ? ' is-on' : '';
      return '<a class="' + on.trim() + '" href="' + t.href + '">' + ico(t.icon) + '<span>' + t.label + '</span></a>';
    }).join('');
  }

  /* ---------- 状态栏时间 ---------- */
  function statusTime () {
    var el = document.querySelector('[data-mtime]');
    if (!el) return;
    function tick () {
      var d = new Date();
      el.textContent = d.getHours() + ':' + ('0' + d.getMinutes()).slice(-2);
    }
    tick();
    setInterval(tick, 20000);
  }

  /* ---------- Toast ---------- */
  var timer = null;
  function toast (msg) {
    var el = document.getElementById('m-toast');
    if (!el) { alert(msg); return; }
    el.textContent = msg;
    el.classList.add('is-on');
    clearTimeout(timer);
    timer = setTimeout(function () { el.classList.remove('is-on'); }, 1900);
  }
  window.mToast = toast;

  /* ---------- 底部弹层 ---------- */
  function initSheet () {
    document.querySelectorAll('[data-sheet-open]').forEach(function (b) {
      b.addEventListener('click', function () {
        var id = b.getAttribute('data-sheet-open');
        var s = document.getElementById(id);
        var c = document.getElementById('m-scrim');
        if (s) s.classList.add('is-open');
        if (c) c.classList.add('is-open');
      });
    });
    document.querySelectorAll('[data-sheet-close]').forEach(function (b) {
      b.addEventListener('click', closeSheet);
    });
    var scrim = document.getElementById('m-scrim');
    if (scrim) scrim.addEventListener('click', closeSheet);
  }
  function closeSheet () {
    document.querySelectorAll('.m-sheet').forEach(function (s) { s.classList.remove('is-open'); });
    var c = document.getElementById('m-scrim');
    if (c) c.classList.remove('is-open');
  }

  /* ---------- 选项组（.m-chips / .m-tabs-simple） ---------- */
  function initChips () {
    document.querySelectorAll('[data-chips]').forEach(function (grp) {
      grp.querySelectorAll('button').forEach(function (b) {
        b.addEventListener('click', function () {
          grp.querySelectorAll('button').forEach(function (x) { x.classList.remove('is-on'); });
          b.classList.add('is-on');
          var key = grp.getAttribute('data-chips');
          if (key) filterRows(key, b.getAttribute('data-val') || b.textContent.trim());
        });
      });
    });
  }

  /* 按 data-属性过滤行：data-chips="flag" + data-val="best" → 匹配 [data-flag] */
  function filterRows (key, val) {
    var all = val === '全部' || val === 'all' || val === '';
    document.querySelectorAll('[data-' + key + ']').forEach(function (el) {
      var v = el.getAttribute('data-' + key);
      el.style.display = (all || v.indexOf(val) >= 0) ? '' : 'none';
    });
  }

  /* ---------- 点击提示 / 演示交互 ---------- */
  function initToastButtons () {
    document.querySelectorAll('[data-toast]').forEach(function (b) {
      b.addEventListener('click', function () { toast(b.getAttribute('data-toast')); });
    });
  }

  /* 应答式 demo：点一下切换 is-on（用于"已关注/已勾选"类交互） */
  function initToggles () {
    document.querySelectorAll('[data-toggle-on]').forEach(function (b) {
      b.addEventListener('click', function () {
        b.classList.toggle('is-on');
        var cls = b.getAttribute('data-toggle-on');
        if (cls) b.classList.toggle(cls);
      });
    });
  }

  function boot () {
    injectTab();
    statusTime();
    initSheet();
    initChips();
    initToastButtons();
    initToggles();
    document.querySelectorAll('[data-icon]').forEach(function (el) {
      el.insertAdjacentHTML('afterbegin', ico(el.getAttribute('data-icon')));
    });
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot);
  else boot();
})();
