using System.Text.Json.Nodes;
using GAIRR;

// 一次性冒烟验证（WebReplyParser 的"说明文字 + 协议 JSON"形态）：验证完即可整目录删除。
// 背景：线上网页模型把回复写成「一段说明文字 + 一个协议 JSON」，且 arguments 里的内层引号未转义，
// 解析器认不出 → 退化为纯文本 → 本轮没有 tool_calls → 任务中止。见 round-12 报障。

var sample = """{"role":"assistant","content":"先核实上一轮三处改动的落盘状态。","tool_calls":[{"id":"call_1","type":"function","function":{"name":"Grep","arguments":"{"pattern":"appLogoText|RefreshAppLogo","path":"app/GAIRR"}"}},{"id":"call_2","type":"function","function":{"name":"Grep","arguments":"{"pattern":"Text=\"GAIRR\"","path":"app/GAIRR/MainWindow.xaml"}"}}]}""";

var cases = new (string Name, string Text, bool Parsed, int Calls, string ExpectContent)[]
{
    ("① 纯协议 JSON",
        """{"role":"assistant","content":"好的","tool_calls":[{"id":"c1","type":"function","function":{"name":"Grep","arguments":"{\"pattern\":\"abc\"}"}}]}""",
        true, 1, "好的"),

    ("② 说明文字 + 规范 JSON",
        "先说一句。\n\n" + """{"role":"assistant","content":"好的","tool_calls":[]}""",
        true, 0, "先说一句。\n\n好的"),

    ("③ 说明文字 + 畸形 JSON（本轮线上样例）",
        "核实上一轮改动是否已落盘，先并行检索关键标识。\n\n" + sample,
        true, 2, "核实上一轮改动是否已落盘，先并行检索关键标识。\n\n先核实上一轮三处改动的落盘状态。"),

    ("④ JSON + 尾部说明文字",
        """{"role":"assistant","content":"好的","tool_calls":[]}""" + "\n\n以上。",
        true, 0, "好的"),

    ("⑤ 纯文本（不该被解析）",
        "我先看一下情况再说。",
        false, 0, ""),
};

var fail = 0;
foreach (var (name, text, parsed, calls, expectContent) in cases)
{
    var (content, toolCalls, _, isParsed) = WebReplyParser.Parse(text);
    var problems = new List<string>();
    if (isParsed != parsed) problems.Add($"Parsed 期望 {parsed} 实得 {isParsed}");
    if (toolCalls.Count != calls) problems.Add($"tool_calls 期望 {calls} 实得 {toolCalls.Count}");
    if (parsed && content != expectContent) problems.Add($"正文不符：\n   期望 [{Show(expectContent)}]\n   实得 [{Show(content)}]");

    // 参数必须是合法 JSON（否则工具执行会失败）
    foreach (var c in toolCalls)
    {
        try { JsonNode.Parse(c.Arguments); }
        catch (Exception ex) { problems.Add($"call {c.Id} 参数不是合法 JSON：{c.Arguments}（{ex.Message}）"); }
    }

    Console.WriteLine($"[{(problems.Count == 0 ? "PASS" : "FAIL")}] {name}");
    if (toolCalls.Count > 0)
        Console.WriteLine("       调用：" + string.Join("；", toolCalls.Select(c => $"{c.Name}({c.Arguments})")));
    foreach (var p in problems) { fail++; Console.WriteLine("       ✗ " + p); }
}

Console.WriteLine(fail == 0 ? "\n全部通过 ✓" : $"\n{fail} 项不通过 ✗");
return fail == 0 ? 0 : 1;

static string Show(string s) => s.Replace("\n", "\\n");
