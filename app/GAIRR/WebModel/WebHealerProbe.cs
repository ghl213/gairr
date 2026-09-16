using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using GAIRR.Core;

namespace GAIRR;

/// <summary>网页版自动修复侧车 · 取证与补丁门禁（WebHealer 的 partial 实现）：
/// 页面只读探针 / 触发分类 / 非网页模型选择 / 提示词构造 / 补丁门禁（含页面内 new Function 编译检查）/ 落盘与回滚。</summary>
public static partial class WebHealer
{
    /// <summary>系统提示词：修复工程师角色 + 硬规则（入口/消息类型/幂等守卫/禁导航/收尾判定不动）</summary>
    public const string SystemPrompt =
        "你是 GAIRR 网页模型通道的站点适配脚本自动修复工程师。适配脚本运行在 WebView2 里驱动第三方站点页面完成一轮问答"
        + "（定位输入框→写入→发送→轮询抓取回复正文→收尾判定→postMessage 回传）。站点改版（选择器失效/DOM 结构变化/新弹层）时脚本会报错。"
        + "请根据【失败信号】、【活动页面 DOM 采样】与【日志】修复脚本。\n"
        + "硬规则：\n"
        + "1) 输出必须是完整、可运行的修复版 js 全文（未改动的部分原样保留，不要输出 diff、不要省略号）；\n"
        + "2) 六个 C# 入口 window.__gairrAsk / __gairrCancel / __gairrNewChat / __gairrNewChatVerify / __gairrReady / __gairrLoggedIn "
        + "与 postMessage 消息类型（log/delta/reasoning/done/error/phase/sent）必须保留且语义不变；\n"
        + "3) 必须保留幂等注入守卫 if (window.__gairrAsk) return;\n"
        + "4) 禁止整页导航：不得出现 location.href=、location.replace、location.assign，"
        + "不得把带 href 的 a 标签当\"新对话\"入口（点击即整页跳转，会销毁脚本上下文）；\n"
        + "5) 选择器修改必须以【活动页面 DOM 采样】中的证据（class/aria-label/placeholder）为准，保留多候选回退风格；\n"
        + "6) 可顺手修复明显低效（过长的固定等待、冗余的全页扫描），但不得改动收尾判定（正文连续 8 拍稳定 / 90s 看门狗）与 sent 确证逻辑；\n"
        + "7) 若证据表明这不是脚本问题（未登录/网络故障/站点维护/用户操作等），只回复一行 NO_PATCH，不要输出脚本。";

    /// <summary>页面只读探针：采样当前页面的输入框/按钮/正文类节点（不改动 DOM），返回 JSON 字符串。
    /// 用普通 C# 字符串（\n 换行 + \" 转义）而非逐字字符串，避免 JS 属性选择器的双引号与字符串边界冲突。</summary>
    public const string ProbeJs =
        "(function(){try{\n" +
        "function vis(e){var r=e.getBoundingClientRect();return r.width>0&&r.height>0&&getComputedStyle(e).visibility!=='hidden';}\n" +
        "function sum(e){return{t:e.tagName.toLowerCase(),id:e.id||'',c:((e.className||'')+'').split(/\\s+/).slice(0,2).join('.'),ph:e.getAttribute('placeholder')||'',al:(e.getAttribute('aria-label')||'').slice(0,24),txt:((e.innerText||'')||'').trim().slice(0,14)};}\n" +
        "var inp=[];['textarea','input','div[contenteditable=\"true\"]','div[contenteditable=\"\"]'].forEach(function(s){var n=document.querySelectorAll(s);for(var i=0;i<n.length&&inp.length<12;i++)if(vis(n[i]))inp.push(sum(n[i]));});\n" +
        "var btn=[];var c=document.querySelectorAll('[role=\"button\"],button,[class*=\"icon-button\"],[class*=\"button\"]');for(var i=0;i<c.length&&btn.length<50;i++){var e=c[i];if(!vis(e))continue;var x=((e.innerText||e.getAttribute('aria-label')||'')||'').trim();if(x&&x.length<=14)btn.push(sum(e));}\n" +
        "var ans=[];var m=document.querySelectorAll('[class*=\"markdown\"],[class*=\"answer\"],[class*=\"message\"],[class*=\"bubble\"]');for(var j=0;j<m.length&&ans.length<8;j++)ans.push({c:((m[j].className||'')+'').split(/\\s+/).slice(0,3).join('.'),len:((m[j].innerText||'')||'').length});\n" +
        "return JSON.stringify({url:location.href,title:document.title,rs:document.readyState,inputs:inp,buttons:btn,answerLike:ans});\n" +
        "}catch(e){return JSON.stringify({err:String(e)});}})()";

    /* ---------- 触发分类（主编排在 WebHealer.cs） ---------- */

    /// <summary>该信号是否值得让模型修脚本（"已取消"/未登录类是用户态问题，修脚本无用，直接跳过）</summary>
    internal static bool IsPatchable(string signal)
    {
        var s = signal ?? "";
        if (s.Contains("已取消")) return false;
        if (s.Contains("未登录") || s.Contains("请登录")) return false;
        if (s.Contains("初始化超时（WebView2")) return false;   // 运行环境缺失（WebView2 运行库），非脚本问题
        return true;
    }

    /* ---------- 非网页模型选择（绝不递归走可能正故障的网页通道） ---------- */

    internal static string PickProvider(AppConfig cfg)
    {
        var cur = cfg.Provider;
        if (!string.IsNullOrWhiteSpace(cur) && !WebChannelSpec.IsWebProvider(cur)
            && !string.IsNullOrEmpty(cfg.ProviderCfg(cur).ApiKey)) return cur;
        foreach (var p in cfg.ProviderList())
            if (!string.IsNullOrEmpty(p.Key) && !WebChannelSpec.IsWebProvider(p.Key)
                && !string.IsNullOrEmpty(cfg.ProviderCfg(p.Key).ApiKey)) return p.Key;
        return "";
    }

    /* ---------- 取证 ---------- */

    /// <summary>ExecuteScriptAsync 的字符串结果去掉 JSON 引号（探针返回的是 JSON 字符串字面量）</summary>
    internal static string Unquote(string? raw)
    {
        try
        {
            raw = (raw ?? "").Trim();
            if (raw.Length >= 2 && raw.StartsWith("\"") && raw.EndsWith("\""))
            {
                var v = JsonNode.Parse(raw) as JsonValue;
                if (v != null && v.TryGetValue<string>(out var s)) return s;
            }
            return raw;
        }
        catch { return raw ?? ""; }
    }

    /// <summary>日志尾部：取 webmodel.log 末 120 行中属于本适配器的行（供模型看错误上下文），截 4000 字</summary>
    internal static string LogTail(string adapter, int maxChars = 4000)
    {
        try
        {
            var f = Path.Combine(Paths.LogDir, "webmodel.log");
            if (!File.Exists(f)) return "";
            var hit = File.ReadAllLines(f, Encoding.UTF8)
                .TakeLast(120)
                .Where(l => l.Contains("[" + adapter + "]") || l.Contains("[heal]"))
                .ToList();
            var sb = new StringBuilder();
            for (var i = hit.Count - 1; i >= 0 && sb.Length < maxChars; i--) sb.Insert(0, hit[i] + "\n");
            return sb.ToString();
        }
        catch { return ""; }
    }

    /// <summary>用户提示词：失败信号 + 活动页采样 + 日志尾部 + 当前脚本全文</summary>
    internal static string BuildPrompt(string adapter, string signal, string probe, string logTail, string currentJs)
    {
        var sb = new StringBuilder();
        sb.Append("【适配器】").Append(adapter).Append("\n\n");
        sb.Append("【失败信号】（问答主路已按原逻辑处理，本侧车只做离路修复）\n").Append(Trunc(signal, 500)).Append("\n\n");
        sb.Append("【活动页面 DOM 采样】（当前页面的只读探针结果，选择器修改的依据）\n").Append(Trunc(probe, 3000)).Append("\n\n");
        sb.Append("【最近日志】（webmodel.log 中本适配器相关行）\n").Append(Trunc(logTail, 2500)).Append("\n\n");
        sb.Append("【当前适配脚本全文】\n```js\n").Append(currentJs).Append("\n```\n\n");
        sb.Append("请按系统规则输出修复版完整脚本；若判定不是脚本问题，只回复一行 NO_PATCH。");
        return sb.ToString();
    }

    /* ---------- 补丁门禁 ---------- */

    /// <summary>补丁门禁（全部通过才允许应用）。返回 (Category, Js, Reason)：
    /// "ok" 放行（Js=补丁全文）；"nopatch"=模型判定非脚本问题；
    /// 其余为失败类别（extract/entry/guard/navigate/size/syntax），Reason 带可读原因。</summary>
    internal static async Task<(string Category, string Js, string Reason)> ValidateAsync(WebModelWindow win, string currentJs, string raw)
    {
        var t = (raw ?? "").Trim();
        if (t.Replace(" ", "").Replace("\u3000", "").Contains("NO_PATCH"))
            return ("nopatch", "", Trunc(t, 200));

        var js = ExtractJsBlock(t);
        if (js.Length < 200) return ("extract", js, "回复中未提取到完整 js 代码块");

        foreach (var entry in new[] { "__gairrAsk", "__gairrCancel", "__gairrNewChat", "__gairrNewChatVerify", "__gairrReady", "__gairrLoggedIn" })
            if (!js.Contains("window." + entry)) return ("entry", js, "缺少 C# 入口 window." + entry);
        if (!Regex.IsMatch(js, @"if\s*\(\s*window\.__gairrAsk\s*\)\s*return")) return ("guard", js, "缺少幂等注入守卫 if(window.__gairrAsk) return");
        // 整页导航禁忌：location.href= 赋值（排除 ===/!== 比较）、location.replace/assign 调用
        if (Regex.IsMatch(js, @"location\.href\s*=(?!=)") || Regex.IsMatch(js, @"location\.(replace|assign)\s*\("))
            return ("navigate", js, "含整页导航语句（location.href=/replace/assign），违反适配脚本禁忌");
        if (js.Length < currentJs.Length / 2 || js.Length > currentJs.Length * 3)
            return ("size", js, $"体积异常（原 {currentJs.Length} 字 → {js.Length} 字，疑似截断/掺水）");

        // 语法编译检查：借用活动页面 new Function 只编译不执行（编译失败 = SyntaxError）
        var code = JsonValue.Create(js).ToJsonString();   // JSON 字符串字面量即合法 JS 字符串
        var probe = Unquote(await win.ExecJsAsync($"try {{ new Function({code}); 'ok' }} catch (e) {{ 'ERR:' + e.message }}") ?? "");
        if (probe.StartsWith("ERR", StringComparison.Ordinal))
            return ("syntax", js, "语法检查失败：" + Trunc(probe, 120));
        if (probe != "ok")
            return ("syntax", js, "页面暂不可执行脚本，语法检查无法进行（保守不应用）：" + Trunc(probe, 60));
        return ("ok", js, "");
    }

    /// <summary>从模型回复提取 js：优先 ``` 围栏块（内容 ≥200 字）；无围栏但整体形似完整脚本也接受</summary>
    internal static string ExtractJsBlock(string t)
    {
        var m = Regex.Match(t, "```(?:js|javascript|JS|JAVASCRIPT)?\\s*\\r?\\n([\\s\\S]*?)```");
        if (m.Success && m.Groups[1].Value.Trim().Length >= 200) return m.Groups[1].Value.Trim();
        if (t.Contains("window.__gairrAsk") && t.Contains("(function") && !t.Contains("```")) return t.Trim();
        return "";
    }

    /* ---------- 应用与回滚 ---------- */

    /// <summary>应用补丁：旧版备份（pre-patch-&lt;时间&gt;.js = 回滚基线）+ 补丁归档（patches/）+ 原子落盘到 adapters/</summary>
    internal static void ApplyPatch(string dir, string jsFile, string js)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        try { File.Copy(jsFile, Path.Combine(dir, "pre-patch-" + stamp + ".js"), true); } catch { }
        try
        {
            var pdir = Path.Combine(dir, "patches");
            Directory.CreateDirectory(pdir);
            File.WriteAllText(Path.Combine(pdir, stamp + ".js"), js, Encoding.UTF8);
        }
        catch { }
        var tmp = jsFile + ".heal.tmp";
        File.WriteAllText(tmp, js, Encoding.UTF8);
        File.Move(tmp, jsFile, true);
    }

    /// <summary>回滚：把最近一份 pre-patch 备份拷回 adapters/，并（空闲时）重载页面使旧脚本重新注入</summary>
    internal static bool Rollback(WebModelWindow win, string dir)
    {
        try
        {
            var f = Directory.GetFiles(dir, "pre-patch-*.js").OrderByDescending(x => x).LastOrDefault();
            if (f == null) return false;
            File.Copy(f, win.AdapterJsFile, true);
            _ = win.ReloadForHealAsync();
            return true;
        }
        catch { return false; }
    }
}
