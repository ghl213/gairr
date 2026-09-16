using GAIRR;
using System.Linq;

// 网页通道「DSML 归一化 + 格式纠偏」回归冒烟（对应缺陷：DeepSeek 网页版用 DSML 标签回工具调用）：
//   DeepSeek 用全角竖线包裹的特殊 token 形态回工具调用，此前系统不识别 → 只能靠向网页要求重答。
//   现改为解析前先做 DSML 归一化（全角竖线折回半角），该形态一次解析成功、不再触发纠偏重投；
//   归一化后仍认不出的才走原有"向网页要求按标准 JSON 重新回复"的纠偏路径。
//   ① 用户上报的 DSML 形态回复必须被归一化为协议形态（Parsed=true，解析出 2 个 Glob 调用，正文无 XML 残片）
//   ② 标准协议 JSON（含 2 个 Glob 调用）必须正确解析出 tool_calls
//   ③ 纯文本回复保持原有行为（Parsed=false，原样返回）
// 用法：dotnet run --project app/WebReplySmoke
int failures = 0;

void Check(bool ok, string name, string detail = "")
{
    Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + name + (ok ? "" : "  ← " + detail));
    if (!ok) failures++;
}

// ① 用户实际上报的原文（DeepSeek 网页版 DSML 工具调用形态，全角竖线前缀标签）
var dsml = """
<｜｜DSML｜｜ calls>
<｜｜DSML｜｜ invoke name="Glob">
<｜｜DSML｜｜ parameter name="arguments" string="true">{"pattern":"/MainWindow.xaml"}</｜｜DSML｜｜ parameter>
</｜｜DSML｜｜ invoke>
<｜｜DSML｜｜ invoke name="Glob">
<｜｜DSML｜｜ parameter name="arguments" string="true">{"pattern":"/*.ico"}</｜｜DSML｜｜ parameter>
</｜｜DSML｜｜ invoke>
</｜｜DSML｜｜ calls>
""";
var (c1, calls1, _, p1) = WebReplyParser.Parse(dsml);
Check(p1, "① DSML 归一化后被识别为协议（不再触发纠偏重投）", $"Parsed={p1}");
Check(calls1.Count == 2, "① 解析出 2 个工具调用", $"calls={calls1.Count}");
if (calls1.Count == 2)
{
    Check(calls1[0].Name == "Glob" && calls1[0].Arguments.Contains("/MainWindow.xaml"),
        "① 第 1 个调用：Glob + pattern 正确", $"{calls1[0].Name} {calls1[0].Arguments}");
    Check(calls1[1].Name == "Glob" && calls1[1].Arguments.Contains("/*.ico"),
        "① 第 2 个调用：Glob + pattern 正确", $"{calls1[1].Name} {calls1[1].Arguments}");
    Check(string.IsNullOrWhiteSpace(c1) || !c1.Contains("parameter"),
        "① 正文不含 XML 残片", c1 ?? "<null>");
}

// ② 模型收到纠偏后的重答：标准协议 JSON（两个 Glob 调用，arguments 为 JSON 字符串）
var fixedReply = """
{"role":"assistant","content":"","tool_calls":[{"id":"call_1","type":"function","function":{"name":"Glob","arguments":"{\"pattern\":\"/MainWindow.xaml\"}"}},{"id":"call_2","type":"function","function":{"name":"Glob","arguments":"{\"pattern\":\"/*.ico\"}"}}]}
""";
var (c2, calls2, _, p2) = WebReplyParser.Parse(fixedReply);
Check(p2, "② 纠偏后的标准协议 JSON 解析成功", "Parsed=false");
Check(calls2.Count == 2, "② 解析出 2 个工具调用", $"calls={calls2.Count}");
if (calls2.Count == 2)
{
    Check(calls2[0].Name == "Glob" && calls2[0].Arguments.Contains("/MainWindow.xaml"),
        "② 第 1 个调用：Glob + pattern 正确", $"{calls2[0].Name} {calls2[0].Arguments}");
    Check(calls2[1].Name == "Glob" && calls2[1].Arguments.Contains("/*.ico"),
        "② 第 2 个调用：Glob + pattern 正确", $"{calls2[1].Name} {calls2[1].Arguments}");
    Check(string.IsNullOrWhiteSpace(c2), "② 正文为空（纯工具轮）", c2 ?? "<null>");
}

// ③ 纯文本最终答复（未启用协议时的正常回复）保持原有行为
var (_, calls3, _, p3) = WebReplyParser.Parse("任务完成，共找到 2 个文件。");
Check(!p3 && calls3.Count == 0, "③ 纯文本回复原样返回（行为不变）", $"Parsed={p3}");

Console.WriteLine(failures == 0 ? "SMOKE PASS (全部通过)" : $"SMOKE FAIL（{failures} 项未通过）");
return failures == 0 ? 0 : 1;
