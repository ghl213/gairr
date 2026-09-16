// GAIRR.Core 类型桩：与 app/GAIRR.Agent/Core/LLMClient.cs 第 9-16 行定义保持一致，
// 让 WebReplyParser 两件套可不带 Agent 工程独立编译进本冒烟工程。
namespace GAIRR.Core;

public class LlmException(string message) : Exception(message);

public record LlmUsage(int Prompt, int Completion, int Total);

public record LlmToolCall(string Id, string Name, string Arguments);

public record LlmResponse(string? Content, string? ReasoningContent, List<LlmToolCall> ToolCalls, LlmUsage Usage, string? FinishReason = null);
