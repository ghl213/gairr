using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using GAIRR.Core;

namespace GAIRR;

/// <summary>网页模型通道自动修复侧车（旁路触发，不占问答主路）。
/// 触发 = WebModelWindow.RepairSignal（适配脚本报错 / 页面导航打断 / 注入失败）；
/// 流程 = 门禁（已暂停/冷却10分/每日3次）→ 活动页只读取证（DOM 采样 + 日志尾部 + 当前脚本全文）
///       → 调**非网页**模型生成修复后的完整适配脚本 → 门禁（NO_PATCH 判定 / 6 入口齐全 / 禁整页导航 /
///         体积合理 / 页面内 new Function 编译检查）→ 应用（旧版备份 + 补丁归档 + 原子落盘）
///       → 页面空闲时重载，使下次提问从磁盘重新注入新脚本；
/// 回滚 = 新补丁 30 分钟内仍报 2 次错 → 自动回滚到上一版并暂停修复（防补丁无限循环，重启程序恢复）。
/// 全部动作记 log/webmodel.log（tag=heal）；补丁与备份归档 data/webmodel/heal/&lt;适配器&gt;/ 供用户审查。
/// 开关：[WebChannels] AutoRepair（缺省 1=开）。</summary>
public static partial class WebHealer
{
    static readonly Dictionary<string, SemaphoreSlim> gates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>给某通道窗口挂上自动修复侧车（窗口创建时调用一次；开关关闭时不挂）</summary>
    public static void Attach(WebModelWindow win)
    {
        try
        {
            var cfg = WebChannelHost.Cfg;
            if (cfg == null || !cfg.WebChannelAutoRepair) return;
            win.RepairSignal += msg => _ = HealAsync(win, msg);
        }
        catch { }
    }

    static SemaphoreSlim GateFor(string adapter)
    {
        lock (gates)
            return gates.TryGetValue(adapter, out var g) ? g : (gates[adapter] = new SemaphoreSlim(1, 1));
    }

    static async Task HealAsync(WebModelWindow win, string signal)
    {
        var adapter = win.Spec.Adapter;
        var gate = GateFor(adapter);
        if (!await gate.WaitAsync(TimeSpan.FromSeconds(1))) return;   // 上一次修复还在跑：不叠加
        try { await HealCoreAsync(win, signal); }
        catch (Exception ex) { WebChannelHost.AppendLog($"[heal][{adapter}] 自动修复异常（忽略，不影响问答）：{Trunc(ex.Message, 120)}", "heal"); }
        finally { gate.Release(); }
    }

    static async Task HealCoreAsync(WebModelWindow win, string signal)
    {
        var cfg = WebChannelHost.Cfg;
        if (cfg == null || !cfg.WebChannelAutoRepair) return;
        var adapter = win.Spec.Adapter;
        var dir = Path.Combine(Paths.DataDir, "webmodel", "heal", adapter);
        Directory.CreateDirectory(dir);
        var stateFile = Path.Combine(dir, "state.json");
        var st = HealState.Load(stateFile);
        var now = DateTime.Now;

        /* ① 回滚门禁：上一版补丁的宽限窗（30 分）内已 2 次报错 → 回滚上一版并暂停（防补丁循环） */
        if (st.ErrorsSinceApply >= 2 && st.AppliedAt != "" && DateTime.TryParse(st.AppliedAt, out var appliedAt)
            && (now - appliedAt).TotalMinutes < 30)
        {
            var rolledBack = Rollback(win, dir);
            st = new HealState(now.ToString("yyyy-MM-dd"), st.RepairsToday, st.LastRepairAt, "", 0, true);
            st.Save(stateFile);
            WebChannelHost.AppendLog($"[heal][{adapter}] 新补丁后 30 分钟内仍报错 {st.ErrorsSinceApply} 次：{(rolledBack ? "已回滚到上一版脚本" : "未找到可回滚版本")}，暂停自动修复（重启程序恢复）", "heal");
            win.LogHeal("自动修复：新版仍报错，已回滚并暂停（重启程序恢复）");
            return;
        }

        /* ② 触发门禁：已暂停 / 非脚本问题 / 冷却 10 分 / 每日配额 3 次 */
        if (st.Paused) return;
        if (!IsPatchable(signal)) return;
        if (st.LastRepairAt != "" && DateTime.TryParse(st.LastRepairAt, out var last) && (now - last).TotalMinutes < 10) return;
        var today = now.ToString("yyyy-MM-dd");
        if (st.Date != today) { st.Date = today; st.RepairsToday = 0; }
        if (st.RepairsToday >= 3)
        {
            WebChannelHost.AppendLog($"[heal][{adapter}] 今日修复配额（3 次）已用完，跳过：{Trunc(signal, 100)}", "heal");
            return;
        }
        st.ErrorsSinceApply++;
        st.Save(stateFile);   // 先记"报错+1"：后续任一步失败也保留计数（回滚门禁靠它）

        /* ③ 取证：活动页只读采样 + 日志尾部 + 当前脚本全文 */
        var jsFile = win.AdapterJsFile;
        if (!File.Exists(jsFile))
        {
            WebChannelHost.AppendLog($"[heal][{adapter}] 适配脚本文件不存在（{jsFile}），无法修复", "heal");
            return;
        }
        var currentJs = File.ReadAllText(jsFile, Encoding.UTF8);
        var probe = Unquote(await win.ProbePageAsync() ?? "");
        var logTail = LogTail(adapter);
        WebChannelHost.AppendLog($"[heal][{adapter}] 触发：{Trunc(signal, 120)} → 取证（页面采样 {probe.Length} 字、日志尾部 {logTail.Length} 字、当前脚本 {currentJs.Length} 字）", "heal");

        /* ④ 调非网页模型生成修复版完整脚本（网页通道本身可能就是故障方，绝不递归走它） */
        var provider = PickProvider(cfg);
        if (provider == "")
        {
            WebChannelHost.AppendLog($"[heal][{adapter}] 没有可用的非网页模型（[Providers] 未配 ApiKey），无法自动修复", "heal");
            return;
        }
        string? patched = null;
        try
        {
            var (url, key, model) = cfg.ProviderCfg(provider);
            var client = new LLMClient(url, key, model, cfg);
            var messages = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = SystemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = BuildPrompt(adapter, signal, probe, logTail, currentJs) });
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            patched = (await client.ChatAsync(messages, new JsonArray(), cts.Token)).Content;
        }
        catch (Exception ex) { WebChannelHost.AppendLog($"[heal][{adapter}] 模型调用失败：{Trunc(ex.Message, 120)}", "heal"); return; }
        if (string.IsNullOrWhiteSpace(patched)) { WebChannelHost.AppendLog($"[heal][{adapter}] 模型返回空，修复中止", "heal"); return; }

        /* ⑤ 补丁门禁：NO_PATCH / 提取 / 6 入口 / 幂等守卫 / 禁整页导航 / 体积 / 页面内编译检查 */
        var (verdict, js, reason) = await ValidateAsync(win, currentJs, patched);
        if (verdict == "nopatch")
        {
            WebChannelHost.AppendLog($"[heal][{adapter}] 模型判定本次非脚本问题（不打补丁）：{Trunc(reason, 120)}", "heal");
            return;
        }
        if (verdict != "ok")
        {
            WebChannelHost.AppendLog($"[heal][{adapter}] 补丁未过门禁[{verdict}]：{Trunc(reason, 160)} → 不应用（旧脚本保持不变）", "heal");
            return;
        }

        /* ⑥ 应用：备份旧版（回滚基线）+ 补丁归档 + 原子落盘 */
        ApplyPatch(dir, jsFile, js);
        st.RepairsToday++;
        st.LastRepairAt = now.ToString("yyyy-MM-dd HH:mm:ss");
        st.AppliedAt = st.LastRepairAt;
        st.ErrorsSinceApply = 0;
        st.Save(stateFile);
        WebChannelHost.AppendLog($"[heal][{adapter}] 补丁已应用（{js.Length} 字，模型 {provider}/{cfg.ProviderCfg(provider).Model}）：旧版已备份，页面空闲则重载，下次提问生效", "heal");

        /* ⑦ 页面空闲时重载，使新脚本从磁盘重新注入（不打断进行中的轮次；忙则留到下次重载/注入） */
        var reloaded = await win.ReloadForHealAsync();
        win.LogHeal(reloaded
            ? "自动修复：新脚本已应用并重载页面，下次提问生效"
            : "自动修复：新脚本已落盘（页面正忙未重载，下次重载/注入时生效）");
    }

    /// <summary>截断（日志/提示词用）</summary>
    internal static string Trunc(string? s, int n)
    {
        s = (s ?? "").Replace('\r', ' ').Replace('\n', ' ');
        return s.Length <= n ? s : s.Substring(0, n) + "…";
    }

    /// <summary>自动修复状态（data/webmodel/heal/&lt;适配器&gt;/state.json）：
    /// RepairsToday=当日已修次数（Date 换日清零）；ErrorsSinceApply=当前补丁生效后累计报错数（回滚门禁判据）；
    /// Paused=暂停（回滚后置位，重启程序时 Load 重置）。</summary>
    internal sealed class HealState
    {
        public string Date { get; set; } = "";
        public int RepairsToday { get; set; }
        public string LastRepairAt { get; set; } = "";
        public string AppliedAt { get; set; } = "";
        public int ErrorsSinceApply { get; set; }
        public bool Paused { get; set; }

        public HealState(string date, int repairsToday, string lastRepairAt, string appliedAt, int errorsSinceApply, bool paused)
        {
            Date = date; RepairsToday = repairsToday; LastRepairAt = lastRepairAt;
            AppliedAt = appliedAt; ErrorsSinceApply = errorsSinceApply; Paused = paused;
        }

        public static HealState Load(string file)
        {
            try
            {
                if (File.Exists(file))
                {
                    var o = JsonNode.Parse(File.ReadAllText(file, Encoding.UTF8)) as JsonObject;
                    if (o != null) return new HealState(
                        Str(o["Date"]), Int(o["RepairsToday"]), Str(o["LastRepairAt"]),
                        Str(o["AppliedAt"]), Int(o["ErrorsSinceApply"]), Bool(o["Paused"]));
                }
            }
            catch { }
            return new HealState("", 0, "", "", 0, false);   // 状态损坏/不存在：按全新状态启动（Paused 自然复位 = 重启恢复）
        }

        public void Save(string file)
        {
            try
            {
                var o = new JsonObject
                {
                    ["Date"] = Date, ["RepairsToday"] = RepairsToday, ["LastRepairAt"] = LastRepairAt,
                    ["AppliedAt"] = AppliedAt, ["ErrorsSinceApply"] = ErrorsSinceApply, ["Paused"] = Paused,
                };
                File.WriteAllText(file, o.ToJsonString(), Encoding.UTF8);
            }
            catch { }
        }

        static string Str(JsonNode? n) => n?.GetValue<string>() ?? "";
        static int Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;
        static bool Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    }
}
