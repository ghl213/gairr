using System.Text.Json.Serialization;

namespace GAIRR.Core;

/// <summary>任务触发方式。</summary>
public enum TaskTriggerMode
{
    Manual,
    Once,
    Cron,
}

/// <summary>自动任务项：可定时或手动触发，由 TaskScheduler 调度、TaskRunner 执行。</summary>
public class TaskItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("projectPath")]
    public string ProjectPath { get; set; } = "";

    [JsonPropertyName("skillName")]
    public string SkillName { get; set; } = "";

    [JsonPropertyName("triggerMode")]
    public TaskTriggerMode TriggerMode { get; set; } = TaskTriggerMode.Manual;

    [JsonPropertyName("cronExpression")]
    public string CronExpression { get; set; } = "";

    [JsonPropertyName("nextRunTime")]
    public DateTime? NextRunTime { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    /// <summary>是否已到触发时间。</summary>
    public bool IsDue(DateTime now)
    {
        if (!Enabled) return false;
        return NextRunTime.HasValue && NextRunTime.Value <= now;
    }
}
