using System.Text.Json.Serialization;

namespace GAIRR.Core;

/// <summary>任务运行状态。</summary>
public enum TaskRunStatus
{
    Running,
    Success,
    Failed,
    Cancelled,
}

/// <summary>一次任务运行的完整记录。</summary>
public class TaskRunRecord
{
    [JsonPropertyName("runId")]
    public string RunId { get; set; } = "";

    [JsonPropertyName("taskId")]
    public string TaskId { get; set; } = "";

    [JsonPropertyName("taskName")]
    public string TaskName { get; set; } = "";

    [JsonPropertyName("startedAt")]
    public DateTime StartedAt { get; set; }

    [JsonPropertyName("finishedAt")]
    public DateTime? FinishedAt { get; set; }

    [JsonPropertyName("status")]
    public TaskRunStatus Status { get; set; } = TaskRunStatus.Running;

    [JsonPropertyName("rounds")]
    public int Rounds { get; set; }

    [JsonPropertyName("tokens")]
    public int Tokens { get; set; }

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = "";

    [JsonPropertyName("logPath")]
    public string LogPath { get; set; } = "";

    [JsonPropertyName("error")]
    public string Error { get; set; } = "";
}
