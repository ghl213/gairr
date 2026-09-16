using System.IO;
using System.Text.Json.Nodes;

namespace GAIRR.Core;

/// <summary>任务后主动分词关联（后台异步）：任务收尾后把需求原文+最终答复提交大模型做分词→符号映射，
/// 追加进 .gairr/tag-refs.jsonl（Source=auto）。与 TagRefAccumulator 的模型自觉输出互补：
/// 主动调度不依赖模型在回复里输出 JSON；异步入队不阻塞对话流；失败静默不影响主流程。</summary>
public static class TagRefEnricher
{
    const int MaxPending = 8;                       // 队列上限（超限丢最旧，防积压）
    static readonly Queue<(string Task, string Reply)> Pending = new();
    static readonly object Sync = new();
    static bool running;

    static readonly string EnrichPrompt =
        "你是代码检索标签生成器。根据用户任务需求与最终答复，输出 JSON（不要输出其它内容）：\n" +
        "{\"tokens\": [中文分词或关键词，2-12 字，3-5 个，覆盖用户表达的核心概念与界面元素说法], " +
        "\"refs\": [{\"rel\": 代码相对路径(正斜杠), \"symbol\": 符号名, \"line\": 行号, \"kind\": method/class/endpoint/vue-comp}]}\n" +
        "要求：refs 必须来自本次任务实际涉及的文件；line 必须是定义声明所在行；拿不准的关联不要列。";

    /// <summary>任务收尾调用：入队即返回。</summary>
    public static void Enqueue(string task, string reply)
    {
        if (!SystemCfg.EnrichTagRefs) return;
        if ((task?.Length ?? 0) < 2 && (reply?.Length ?? 0) < 2) return;
        lock (Sync)
        {
            if (Pending.Count >= MaxPending) Pending.Dequeue();
            Pending.Enqueue((task ?? "", reply ?? ""));
        }
        if (running) return;
        lock (Sync)
        {
            if (running) return;
            running = true;
        }
        _ = Task.Run(Worker);
    }

    static async Task Worker()
    {
        try
        {
            while (true)
            {
                (string Task, string Reply) item;
                lock (Sync)
                {
                    if (Pending.Count == 0) { running = false; return; }
                    item = Pending.Dequeue();
                }
                await ProcessAsync(item);
            }
        }
        catch { lock (Sync) running = false; }
    }

    static async Task ProcessAsync((string Task, string Reply) item)
    {
        try
        {
            var cfg = new AppConfig();
            if (cfg.ProjectRoot.Length == 0 || !Directory.Exists(cfg.ProjectRoot)) return;
            var (baseUrl, apiKey, model) = cfg.ProviderCfg(cfg.Provider);
            if (baseUrl.Length == 0 || apiKey.Length == 0) return;   // 未配置模型则跳过（与 MapAuto 同策略）
            var client = new LLMClient(baseUrl, apiKey, model);
            var messages = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = EnrichPrompt },
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = "任务需求：" + LLMClient.Trunc(item.Task, 600) +
                                  "\n\n最终答复：" + LLMClient.Trunc(item.Reply, 2000),
                },
            };
            var resp = await client.ChatAsync(messages, new JsonArray(), CancellationToken.None);
            var entry = TagRefAccumulator.ExtractAuto(cfg, resp.Content, item.Task);
            if (entry != null && (entry.Tags.Count > 0 || entry.Refs.Count > 0))
            {
                TagRefAccumulator.Append(cfg, entry);
                System.Diagnostics.Debug.WriteLine(
                    $"[TagRefEnrich] 主动关联积累 {entry.Tags.Count} 个分词，{entry.Refs.Count} 个符号关联");
            }
        }
        catch { /* 静默失败：事后收录不打扰主流程 */ }
    }
}