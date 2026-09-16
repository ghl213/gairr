using System.Text.Json.Serialization;

namespace GAIRR.Core;

/// <summary>单次计划确认交互中的数据模型：Agent 改数据/代码前先生成计划，经审批后再执行。</summary>
public class Plan
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = "";

    [JsonPropertyName("userText")]
    public string UserText { get; set; } = "";

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = "";

    [JsonPropertyName("steps")]
    public List<PlanStep> Steps { get; set; } = new();

    /// <summary>pending / approved / rejected</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "pending";

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [JsonPropertyName("decidedAt")]
    public DateTime? DecidedAt { get; set; }

    [JsonIgnore]
    public bool IsPending => Status == "pending";

    [JsonIgnore]
    public bool IsApproved => Status == "approved";
}

/// <summary>计划中的单一步骤。</summary>
public class PlanStep
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = "";

    [JsonPropertyName("target")]
    public string Target { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";
}
