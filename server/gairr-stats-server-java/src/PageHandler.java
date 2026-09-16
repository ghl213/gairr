import com.sun.net.httpserver.HttpExchange;
import java.io.IOException;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;

// GAIRR stats page: single-file HTML, inline CSS/JS, no external resources (works offline).
// Design: telemetry console (graphite base + amber signal), monospace readouts, bar chart signature.
// Prompts for token on /api/overview 401.
final class PageHandler {
 private PageHandler() {}

 static void handle(HttpExchange ex) throws IOException {
 byte[] bytes = HTML.getBytes(StandardCharsets.UTF_8);
 ex.getResponseHeaders().set("Content-Type", "text/html; charset=utf-8");
 ex.sendResponseHeaders(200, bytes.length);
 try (OutputStream os = ex.getResponseBody()) { os.write(bytes); }
 }

 private static final String HTML =
 "<!DOCTYPE html><html lang=zh><head><meta charset=utf-8>" +
 "<meta name=viewport content='width=device-width,initial-scale=1'>" +
 "<title>GAIRR · 遥测</title><style>" +
 ":root{--bg:#0F1115;--panel:#161A21;--line:#252B35;--txt:#E7EAF0;--mut:#8B93A3;--amber:#E8A33D;--blue:#5B8DEF}" +
 "{box-sizing:border-box}" +
 "body{margin:0;background:var(--bg);color:var(--txt);font-family:ui-sans-serif,system-ui,-apple-system,'Segoe UI',sans-serif;font-size:14px;line-height:1.5}" +
 ".wrap{max-width:1060px;margin:0 auto;padding:30px 20px 64px}" +
 ".top{display:flex;justify-content:space-between;align-items:center;margin-bottom:24px}" +
 ".brand{font-weight:700;letter-spacing:.16em;font-size:14px;display:flex;align-items:center;gap:10px;text-transform:uppercase}" +
 ".dot{width:9px;height:9px;border-radius:50%;background:var(--amber);box-shadow:0 0 12px var(--amber)}" +
 ".sub{color:var(--mut);font-weight:400;letter-spacing:.08em}" +
 ".meta{color:var(--mut);font-family:ui-monospace,Menlo,Consolas,monospace;font-size:12px}" +
 ".auth{background:var(--panel);border:1px solid var(--line);border-radius:14px;padding:20px 22px;display:flex;align-items:center;gap:14px;flex-wrap:wrap}" +
 ".auth label{color:var(--mut);font-size:13px}" +
 "input{background:#0D1014;border:1px solid var(--line);border-radius:9px;color:var(--txt);padding:10px 13px;font-size:14px;outline:none;min-width:210px}" +
 "input:focus{border-color:var(--blue);box-shadow:0 0 0 3px rgba(91,141,239,.16)}" +
 "button{background:var(--amber);color:#1A1305;border:0;border-radius:9px;padding:10px 20px;font-weight:600;font-size:14px;cursor:pointer}" +
 "button:hover{filter:brightness(1.08)}" +
 ".err{color:#F0666B;font-size:13px}" +
 ".hero{display:grid;grid-template-columns:1fr 1fr;gap:16px;margin:18px 0}" +
 ".stat{background:var(--panel);border:1px solid var(--line);border-radius:14px;padding:22px 24px}" +
 ".eyebrow{color:var(--mut);font-size:11px;letter-spacing:.16em;text-transform:uppercase;margin-bottom:10px}" +
 ".value{font-family:ui-monospace,Menlo,Consolas,monospace;font-size:40px;font-weight:600;color:var(--amber);line-height:1;letter-spacing:-.01em}" +
 ".unit{font-size:14px;color:var(--mut);margin-left:6px;font-weight:400}" +
 ".panel{background:var(--panel);border:1px solid var(--line);border-radius:14px;padding:20px 22px;margin-bottom:16px}" +
 ".panel-h{display:flex;justify-content:space-between;align-items:baseline;margin-bottom:18px}" +
 "h2{margin:0;font-size:13px;font-weight:600;letter-spacing:.04em}" +
 ".legend{color:var(--mut);font-size:12px}" +
 ".chart{display:flex;align-items:flex-end;gap:3px;height:150px}" +
 ".col{flex:1;display:flex;flex-direction:column;justify-content:flex-end;height:100%}" +
 ".bar{width:100%;background:linear-gradient(180deg,var(--amber),#B87A22);border-radius:3px 3px 0 0;min-height:2px}" +
 ".col:hover .bar{filter:brightness(1.3)}" +
 ".axis{display:flex;justify-content:space-between;color:var(--mut);font-size:11px;margin-top:10px;font-family:ui-monospace,Consolas,monospace}" +
 "table{border-collapse:collapse;width:100%}" +
 "th{color:var(--mut);font-size:11px;letter-spacing:.1em;text-transform:uppercase;text-align:left;padding:0 10px 11px;font-weight:600;border-bottom:1px solid var(--line)}" +
 "td{padding:9px 10px;font-size:13px;border-bottom:1px solid #1C212A}" +
 "td.mono{font-family:ui-monospace,Consolas,monospace}" +
 "tbody tr:last-child td{border-bottom:0}" +
 "tbody tr:hover{background:#1A1F27}" +
 ".empty{color:var(--mut);font-size:13px;padding:14px 0}" +
 "@media(max-width:640px){.hero{grid-template-columns:1fr}.value{font-size:31px}}" +
 "</style></head><body><div class=wrap>" +
 "<div class=top><div class=brand><span class=dot></span>GAIRR <span class=sub>telemetry</span></div>" +
 "<div class=meta id=meta>anonymous usage</div></div>" +
 "<div id=auth class=auth><label>访问口令</label><input id=tk type=password placeholder='输入 token'>" +
 "<button onclick=go()>进入</button><span id=msg class=err></span></div>" +
 "<main id=dash style='display:none'>" +
 "<section class=hero>" +
 "<div class=stat><div class=eyebrow>设备总数</div><div class=value><span id=td>-</span><span class=unit>台</span></div></div>" +
 "<div class=stat><div class=eyebrow>累计使用时长</div><div class=value><span id=ts>-</span></div></div>" +
 "</section>" +
 "<section class=panel><div class=panel-h><h2>近 30 日活跃</h2><span class=legend>柱高 = 当日秒数 · 悬停查看明细</span></div>" +
 "<div id=chart class=chart></div><div id=axis class=axis><span id=ax0></span><span id=ax1></span></div></section>" +
 "<section class=panel><div class=panel-h><h2>每日明细</h2></div>" +
 "<table><thead><tr><th>日期</th><th>活跃设备</th><th>秒数</th></tr></thead><tbody id=daily></tbody></table></section>" +
 "<section class=panel><div class=panel-h><h2>最近设备</h2></div>" +
 "<table><thead><tr><th>设备</th><th>系统</th><th>版本</th><th>最后活跃</th><th>次数</th><th>秒数</th></tr></thead><tbody id=recent></tbody></table></section>" +
 "</main></div><script>" +
 "function el(i){return document.getElementById(i)}" +
 "function esc(s){if(s==null)return '';return String(s).replace(/[&<>]/g,function(c){return c==String.fromCharCode(38)?'&'+'amp;':c=='<'?'&'+'lt;':'&'+'gt;'})}" +
 "function dur(s){s=Number(s)||0;var h=Math.floor(s/3600),m=Math.floor((s%3600)/60);if(h>0)return h+'h '+m+'m';if(m>0)return m+'m '+(s%60)+'s';return s+'s'}" +
 "function load(t){var u='api/overview'+(t?('?token='+encodeURIComponent(t)):'');" +
 "fetch(u).then(function(r){if(r.status===401){el('auth').style.display='flex';el('dash').style.display='none';el('msg').textContent='口令错误或缺失';return null}" +
 "if(!r.ok){el('msg').textContent='加载失败 '+r.status;return null}return r.json()" +
 "}).then(function(d){if(!d)return;el('auth').style.display='none';el('dash').style.display='block';el('msg').textContent='';render(d)}" +
 ").catch(function(){el('msg').textContent='请求失败'})}" +
 "function go(){var t=el('tk').value;try{sessionStorage.setItem('gairr_token',t)}catch(e){}load(t)}" +
 "function render(d){el('td').textContent=d.totalDevices;el('ts').textContent=dur(d.totalSeconds);" +
 "var days=d.daily||[];var cd=days.slice().reverse();var max=1;days.forEach(function(r){if(r.seconds>max)max=r.seconds});" +
 "var c=el('chart');c.innerHTML='';" +
 "if(!cd.length){c.innerHTML='<div class=empty>暂无数据</div>';el('axis').style.display='none'}" +
 "else{el('axis').style.display='flex';cd.forEach(function(r){var col=document.createElement('div');col.className='col';" +
 "var b=document.createElement('div');b.className='bar';b.style.height=Math.max(2,Math.round(r.seconds/max100))+'%';" +
 "b.title=r.date+' · '+r.devices+' 台 · '+r.seconds+' 秒';col.appendChild(b);c.appendChild(col)});" +
 "el('ax0').textContent=cd[0].date;el('ax1').textContent=cd[cd.length-1].date}" +
 "var db=el('daily');db.innerHTML='';" +
 "if(!days.length){db.innerHTML='<tr><td colspan=3 class=empty>暂无数据</td></tr>'}" +
 "days.forEach(function(r){var tr=document.createElement('tr');tr.innerHTML='<td class=mono>'+esc(r.date)+'</td><td>'+esc(r.devices)+'</td><td class=mono>'+esc(r.seconds)+'</td>';db.appendChild(tr)});" +
 "var rb=el('recent');rb.innerHTML='';var rec=d.recent||[];" +
 "if(!rec.length){rb.innerHTML='<tr><td colspan=6 class=empty>暂无数据</td></tr>'}" +
 "rec.forEach(function(r){var tr=document.createElement('tr');tr.innerHTML='<td class=mono>'+esc(r.deviceId)+'</td><td>'+esc(r.os)+'</td><td class=mono>'+esc(r.appVersion)+'</td><td class=mono>'+esc(r.lastSeen)+'</td><td>'+esc(r.reportCnt)+'</td><td class=mono>'+esc(r.totalSeconds)+'</td>';rb.appendChild(tr)});}" +
 "var saved='';try{saved=sessionStorage.getItem('gairr_token')||''}catch(e){}if(saved){el('tk').value=saved}load(saved)" +
 "</script></body></html>";
}
