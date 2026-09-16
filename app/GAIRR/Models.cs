using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;

namespace GAIRR;

/// <summary>会话持久化记录（JSON 存盘）</summary>
public class SessionRecord
{
    [JsonPropertyName("version")] public string Version { get; set; } = "1.0";
    [JsonPropertyName("timestamp")] public string Timestamp { get; set; } = DateTime.Now.ToString("O");
    [JsonPropertyName("project")] public string Project { get; set; } = "";
    [JsonPropertyName("messages")] public List<MessageRecord> Messages { get; set; } = new();
}

public class MessageRecord
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("who")] public string Who { get; set; } = "";
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("cmd")] public string? Cmd { get; set; }
    [JsonPropertyName("note")] public string? Note { get; set; }
    [JsonPropertyName("isAlert")] public bool IsAlert { get; set; }
    [JsonPropertyName("alertLevel")] public string AlertLevel { get; set; } = "Block";
    [JsonPropertyName("steps")] public string? Steps { get; set; }
    [JsonPropertyName("reasonings")] public List<string> Reasonings { get; set; } = new();   // 旧版兼容
    [JsonPropertyName("thinkingItems")] public List<ThinkingRecord> ThinkingItems { get; set; } = new();   // 旧版兼容（曾用于完整保留思考条）
    [JsonPropertyName("tools")] public List<ToolRecord> Tools { get; set; } = new();        // 旧版兼容
    [JsonPropertyName("todos")] public List<TodoRecord> Todos { get; set; } = new();         // 旧版兼容
    [JsonPropertyName("processItems")] public List<ProcessItemRecord> ProcessItems { get; set; } = new(); // 新版：按实际发生顺序保存全部时间线条目
    // ---- 消息级执行参数快照：该条消息生成时钉住的 角色+模式+模型（头像右侧标题/头像 ToolTip 的数据源）。
    //      随消息逐条落盘——历史中不同时期的消息各自保留当时参数，顶部模型再改不回溯已保存的旧消息；
    //      打开历史会话时以“最后带头像消息”的快照回显顶部选择器。旧历史无此字段（null）回退会话级 Pin。----
    [JsonPropertyName("runRole")] public string? RunRole { get; set; }
    [JsonPropertyName("runRoleDisplay")] public string? RunRoleDisplay { get; set; }
    [JsonPropertyName("runMode")] public string? RunMode { get; set; }
    [JsonPropertyName("runModeDisplay")] public string? RunModeDisplay { get; set; }
    [JsonPropertyName("runProvider")] public string? RunProvider { get; set; }
    [JsonPropertyName("runModel")] public string? RunModel { get; set; }
    /// <summary>编排澄清问题卡片（题面+选项+作答进度，含未提交的勾选/补充草稿）：有卡时随消息落盘供中断续选；
    /// 无卡不写该键（WhenWritingNull，旧存档与普通消息零增负）。</summary>
    [JsonPropertyName("qcards")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<QuestionCardRecord>? QuestionCards { get; set; }

    /// <summary>本轮回复改动的文件清单（相对项目根 + 新建标记 + 工具/时间/说明）：驱动消息底部“本轮改动”条带，
    /// 并向上汇总成左栏“本会话改动”卡片。无改动的轮次不写该键（WhenWritingNull，存量历史为 null = 卡片空）。</summary>
    [JsonPropertyName("changes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<FileChangeRecord>? Changes { get; set; }
}

/// <summary>问题卡片持久化记录（9.4 决策：卡片+作答进度随消息落盘，重开会话可原样还原续选）。
/// 勾选项以文本引用（SelectedTexts），还原时映射回选项本体实例保证引用一致。</summary>
public class QuestionCardRecord
{
    [JsonPropertyName("cardId")] public string CardId { get; set; } = "";
    [JsonPropertyName("no")] public int No { get; set; }
    [JsonPropertyName("question")] public string Question { get; set; } = "";
    [JsonPropertyName("multi")] public bool Multi { get; set; }
    [JsonPropertyName("options")] public List<QuestionOptionRecord> Options { get; set; } = new();
    [JsonPropertyName("selectedTexts")] public List<string> SelectedTexts { get; set; } = new();
    [JsonPropertyName("extraText")] public string? ExtraText { get; set; }
    [JsonPropertyName("extraOpen")] public bool ExtraOpen { get; set; }
    [JsonPropertyName("answered")] public bool Answered { get; set; }
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";
}

public class QuestionOptionRecord
{
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("rec")] public bool Rec { get; set; }
}

/// <summary>会话历史毒字符深度清洗：逐字段把控制字符/孤立代理等替换为 U+FFFD（规则同
/// GAIRR.Core.Markdown.Sanitize，其正文会触发 WPF PtsHost fail-fast 崩溃）。
/// 载入历史与落盘边界调用；存量脏数据已由外部工具清洗，此层防运行时新数据回流污染。</summary>
public static class SessionSanitizer
{
    public static void Clean(SessionItem s)
    {
        s.Title = San(s.Title);
        foreach (var m in s.Messages) Clean(m);
    }

    static void Clean(MessageRecord m)
    {
        m.Text = San(m.Text);
        m.Cmd = San(m.Cmd);
        m.Note = San(m.Note);
        m.Steps = San(m.Steps);
        for (int i = 0; i < m.Reasonings.Count; i++) m.Reasonings[i] = San(m.Reasonings[i]);
        foreach (var t in m.ThinkingItems) { t.Title = San(t.Title); t.Content = San(t.Content); }
        foreach (var t in m.Tools) { t.Title = San(t.Title); t.Status = San(t.Status); t.Inner = San(t.Inner); t.Summary = San(t.Summary); }
        foreach (var t in m.Todos) { t.Title = San(t.Title); t.Content = San(t.Content); }
        foreach (var p in m.ProcessItems) { p.Title = San(p.Title); p.Content = San(p.Content); p.Status = San(p.Status); p.Inner = San(p.Inner); p.Summary = San(p.Summary); }
        if (m.QuestionCards != null)
            foreach (var q in m.QuestionCards)
            {
                q.Question = San(q.Question);
                q.Summary = San(q.Summary);
                q.ExtraText = San(q.ExtraText);
                foreach (var o in q.Options) o.Text = San(o.Text);
                for (int i = 0; i < q.SelectedTexts.Count; i++) q.SelectedTexts[i] = San(q.SelectedTexts[i]);
            }
    }

    static string San(string? x) => GAIRR.Core.Markdown.Sanitize(x);
}

/// <summary>执行过程时间线条目持久化记录：按 ProcessItems 顺序原样落盘，恢复时 1:1 还原</summary>
public class ProcessItemRecord
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";   // todo / thinking / tool / danger
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("content")] public string Content { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("inner")] public string Inner { get; set; } = "";
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";
    [JsonPropertyName("warn")] public bool Warn { get; set; }
    [JsonPropertyName("icon")] public string Icon { get; set; } = "";
    [JsonPropertyName("iconColor")] public string IconColor { get; set; } = "#5C8AE6";
    [JsonPropertyName("isStep")] public bool IsStep { get; set; }
    [JsonPropertyName("open")] public bool Open { get; set; } = true;
    [JsonPropertyName("visible")] public bool Visible { get; set; } = true;
    [JsonPropertyName("round")] public int Round { get; set; }
    [JsonPropertyName("durationMs")] public int DurationMs { get; set; }
    [JsonPropertyName("contextTokens")] public int ContextTokens { get; set; }
    /// <summary>条目发生时刻（思考卡用于在标题右侧显示日期时间；旧会话无值为 null，恢复时回退当前时刻）</summary>
    [JsonPropertyName("time")] public DateTime? Time { get; set; }
    /// <summary>文件工具目标路径（相对项目根，正斜杠；Read 成功时 Agent 回填），供历史会话打开代码快照</summary>
    [JsonPropertyName("filePath")] public string FilePath { get; set; } = "";
    /// <summary>实际读取行段起止（1 基含端点；0=未指定），供代码查看器定位突显读取段</summary>
    [JsonPropertyName("startLine")] public int StartLine { get; set; }
    [JsonPropertyName("endLine")] public int EndLine { get; set; }
}

public class ToolRecord
{
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("inner")] public string Inner { get; set; } = "";
    [JsonPropertyName("warn")] public bool Warn { get; set; }
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";
    [JsonPropertyName("isStep")] public bool IsStep { get; set; }
}

/// <summary>思考条持久化记录（标题 + 内容 + 展开状态，恢复时完整还原实时样式）</summary>
public class ThinkingRecord
{
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("content")] public string Content { get; set; } = "";
    [JsonPropertyName("open")] public bool Open { get; set; } = true;
}

/// <summary>待办计划卡持久化记录（标题 + 步骤清单多行内容，恢复时重建 TodoItem）</summary>
public class TodoRecord
{
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("content")] public string Content { get; set; } = "";
}

/// <summary>警报级别转画刷：Block=红色拦截，Confirm=琥珀色确认取消，Info=常规提示</summary>
public class AlertLevelToBrushConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var level = value as string ?? "Block";
        return level switch
        {
            "Block" => new SolidColorBrush(Color.FromRgb(0xF0, 0x4E, 0x4E)),
            "Confirm" => new SolidColorBrush(Color.FromRgb(0xF0, 0xA1, 0x3E)),
            _ => new SolidColorBrush(Color.FromRgb(0xF0, 0xA1, 0x3E)),
        };
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>自动任务编辑弹窗的数据模型</summary>
public class AutoTaskViewModel
{
    public string Title { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string ExecutionPrompt { get; set; } = "";
    public string FlowMode { get; set; } = "";   // Flow 模板名（工具目录 flows/<name>.json）；空=自主模式
    public TaskScheduleType Type { get; set; } = TaskScheduleType.Weekday;
    public int Hour { get; set; } = 17;
    public int Minute { get; set; }
    public int WeekDay { get; set; } = 1;
    public int MonthDay { get; set; } = 1;
    public string CronExpression { get; set; } = "";
}

/// <summary>自动任务周期类型</summary>
public enum TaskScheduleType { Daily, Weekday, Weekly, Monthly, Cron }

/// <summary>定时任务单次运行记录：完整保存执行过程，可在对话区回放</summary>
public class TaskRunRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    [JsonPropertyName("taskId")] public string TaskId { get; set; } = "";
    [JsonPropertyName("taskTitle")] public string TaskTitle { get; set; } = "";
    [JsonPropertyName("prompt")] public string Prompt { get; set; } = "";
    [JsonPropertyName("startedAt")] public string StartedAt { get; set; } = DateTime.Now.ToString("O");
    [JsonPropertyName("finishedAt")] public string? FinishedAt { get; set; }
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("result")] public string Result { get; set; } = "";
    [JsonPropertyName("rounds")] public int Rounds { get; set; }
    [JsonPropertyName("tokens")] public int Tokens { get; set; }
    [JsonPropertyName("messages")] public List<MessageRecord> Messages { get; set; } = new();

    [JsonIgnore] public DateTime StartedTime => DateTime.TryParse(StartedAt, out var d) ? d : DateTime.Now;
    [JsonIgnore] public string TimeText => StartedTime.ToString("MM-dd HH:mm");
}

/// <summary>任务运行历史项：绑定到左侧列表</summary>
public class TaskRunItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    void Notify(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    public string Id { get; set; } = "";
    public string TaskId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Result { get; set; } = "";
    public DateTime Time { get; set; }
    public int Rounds { get; set; }
    public int Tokens { get; set; }
    public string TimeText => Time.ToString("MM-dd HH:mm");
    public bool Success { get; set; }
    /// <summary>该次运行是否仍在执行中（开始时落盘的骨架记录 FinishedAt 为空）</summary>
    public bool Running { get; set; }
    /// <summary>任务运行持续时间（秒）</summary>
    public int DurationSeconds { get; set; }
    public string DurationText
    {
        get
        {
            if (DurationSeconds < 60) return $"{DurationSeconds}s";
            if (DurationSeconds < 3600) return $"{DurationSeconds / 60}m{DurationSeconds % 60}s";
            return $"{DurationSeconds / 3600}h{(DurationSeconds % 3600) / 60}m";
        }
    }

    bool selected;
    public bool Selected
    {
        get => selected;
        set { selected = value; Notify(nameof(Selected)); Notify(nameof(RowBg)); }
    }

    public Brush RowBg => Selected
        ? new SolidColorBrush(Color.FromRgb(0x15, 0x15, 0x20))
        : Brushes.Transparent;

    public Brush DotBrush => Running
        ? new SolidColorBrush(Color.FromRgb(0x3E, 0x7B, 0xFA))   // 蓝色：执行中
        : Success
            ? new SolidColorBrush(Color.FromRgb(0x3E, 0xCF, 0x8E))
            : new SolidColorBrush(Color.FromRgb(0xF0, 0x4E, 0x4E));
}

/// <summary>任务项：既用于任务历史也用于自动任务列表</summary>
public class TaskItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Notify(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Title { get; set; } = "";
    public string Prompt { get; set; } = "";          // 任务目标描述：发给 Agent 的"要做什么"
    public string ExecutionPrompt { get; set; } = ""; // 任务执行步骤提示词：已验证的固定"怎么做"，首次成功后自动生成、可手动修改；空=尚未生成
    public string FlowMode { get; set; } = "";        // 工作模式：Flow 模板名（工具目录 flows/<name>.json）；空=自主模式
    public string Status { get; set; } = "";          // 成功/失败/进行中/待执行

    TaskScheduleType scheduleType = TaskScheduleType.Weekday;
    public TaskScheduleType ScheduleType
    {
        get => scheduleType;
        set { scheduleType = value; Notify(nameof(ScheduleType)); Notify(nameof(ScheduleText)); Notify(nameof(NextRun)); }
    }

    int hour = 17, minute;
    public int Hour
    {
        get => hour;
        set { hour = Math.Clamp(value, 0, 23); Notify(nameof(Hour)); Notify(nameof(ScheduleText)); Notify(nameof(NextRun)); }
    }
    public int Minute
    {
        get => minute;
        set { minute = Math.Clamp(value, 0, 59); Notify(nameof(Minute)); Notify(nameof(ScheduleText)); Notify(nameof(NextRun)); }
    }

    // 仅 Weekly 使用：1=周一 ... 7=周日
    int weekDay = 1;
    public int WeekDay
    {
        get => weekDay;
        set { weekDay = Math.Clamp(value, 1, 7); Notify(nameof(WeekDay)); Notify(nameof(ScheduleText)); Notify(nameof(NextRun)); }
    }

    // 仅 Monthly 使用：1-31；超过当月天数时取当月最后一天
    int monthDay = 1;
    public int MonthDay
    {
        get => monthDay;
        set { monthDay = Math.Clamp(value, 1, 31); Notify(nameof(MonthDay)); Notify(nameof(ScheduleText)); Notify(nameof(NextRun)); }
    }

    // 仅 Cron 使用：5 字段表达式，如 "0 9 * * 1-5"
    string cronExpression = "";
    public string CronExpression
    {
        get => cronExpression;
        set { cronExpression = value ?? ""; Notify(nameof(CronExpression)); Notify(nameof(ScheduleText)); Notify(nameof(NextRun)); }
    }

    bool enabled = true;
    public bool Enabled
    {
        get => enabled;
        set { enabled = value; Notify(nameof(Enabled)); Notify(nameof(DotBrush)); Notify(nameof(NextRun)); }
    }

    // 是否被选中（单击任务卡片选中后，任务执行历史列表只显示该任务的记录；纯 UI 状态，不随 tasks.json 持久化）
    [JsonIgnore]
    bool isSelected;
    public bool IsSelected
    {
        get => isSelected;
        set { isSelected = value; Notify(nameof(IsSelected)); Notify(nameof(SelectedBg)); }
    }

    // 运行时进度显示（不持久化）：自动任务执行期间任务卡片上的实时进度文本与百分比
    [JsonIgnore] string progressText = "";
    [JsonIgnore]
    public string ProgressText { get => progressText; set { progressText = value; Notify(nameof(ProgressText)); } }

    [JsonIgnore] double progressPercent;
    [JsonIgnore]
    public double ProgressPercent { get => progressPercent; set { progressPercent = Math.Clamp(value, 0, 100); Notify(nameof(ProgressPercent)); } }

    /// <summary>进度行可见性：自动任务执行中才显示（调度器在开始/结束时通知该属性）</summary>
    [JsonIgnore] public Visibility RunningVisible => running != 0 ? Visibility.Visible : Visibility.Collapsed;

    public int Rounds { get; set; }
    public int Tokens { get; set; }
    public DateTime Time { get; set; }
    public DateTime? LastRun { get; set; }
    public string LastResult { get; set; } = "";
    public int RunCount { get; set; }

    /// <summary>并发执行标志：1=正在后台执行，防止调度器重复触发</summary>
    internal int running;

    [JsonIgnore] public string TimeText => Time.ToString("MM-dd HH:mm");

    /// <summary>任务卡片选中背景画刷：选中时淡蓝高亮（复用 RowBoxCur 同色系），未选中透明</summary>
    [JsonIgnore] public Brush SelectedBg => IsSelected
        ? new SolidColorBrush(Color.FromArgb(0x1F, 0x63, 0x66, 0xF1))
        : Brushes.Transparent;

    [JsonIgnore] public Brush DotBrush => !Enabled
        ? new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xB4))
        : Status == "成功"
            ? new SolidColorBrush(Color.FromRgb(0x3E, 0xCF, 0x8E))
            : Status == "失败"
                ? new SolidColorBrush(Color.FromRgb(0xF0, 0xA1, 0x3E))
                : new SolidColorBrush(Color.FromRgb(0x3E, 0xCF, 0x8E));

    [JsonIgnore] public string ScheduleText => ScheduleType switch
    {
        TaskScheduleType.Daily => $"每天 {Hour:D2}:{Minute:D2}",
        TaskScheduleType.Weekday => $"工作日 {Hour:D2}:{Minute:D2}",
        TaskScheduleType.Weekly => $"每周{WeekDayText(WeekDay)} {Hour:D2}:{Minute:D2}",
        TaskScheduleType.Monthly => $"每月{MonthDay}日 {Hour:D2}:{Minute:D2}",
        TaskScheduleType.Cron => string.IsNullOrWhiteSpace(CronExpression) ? "Cron 未配置" : $"Cron: {CronExpression}",
        _ => $"每天 {Hour:D2}:{Minute:D2}"
    };

    static string WeekDayText(int d) => d switch
    {
        1 => "一", 2 => "二", 3 => "三", 4 => "四", 5 => "五", 6 => "六", 7 => "日", _ => "?"
    };

    /// <summary>计算下一个执行时刻（从 now 开始）；若当前时刻正好命中，为避免立即重复执行，返回下一周期</summary>
    public DateTime GetNextRun(DateTime? from = null)
    {
        var t = from ?? DateTime.Now;
        if (ScheduleType == TaskScheduleType.Cron)
        {
            var cron = GAIRR.Core.Cron.CronExpression.TryParse(CronExpression);
            if (cron != null) return cron.GetNextOccurrence(t);
        }
        var candidate = new DateTime(t.Year, t.Month, t.Day, Hour, Minute, 0);
        if (candidate <= t) candidate = candidate.AddDays(1);

        while (!IsScheduled(candidate))
        {
            candidate = candidate.AddDays(1);
            if (candidate.Year > t.Year + 5) break; // 兜底
        }
        return candidate;
    }

    bool IsScheduled(DateTime d) => ScheduleType switch
    {
        TaskScheduleType.Daily => true,
        TaskScheduleType.Weekday => d.DayOfWeek is >= DayOfWeek.Monday and <= DayOfWeek.Friday,
        TaskScheduleType.Weekly => (int)d.DayOfWeek == (WeekDay % 7),
        TaskScheduleType.Monthly => d.Day == Math.Min(MonthDay, DateTime.DaysInMonth(d.Year, d.Month)),
        TaskScheduleType.Cron => GAIRR.Core.Cron.CronExpression.TryParse(CronExpression)?.GetNextOccurrence(d.AddMinutes(-1)) == d,
        _ => true
    };

    DateTime? nextRun;
    /// <summary>由调度器计算并更新的下一次执行时间；禁用时为 null</summary>
    public DateTime? NextRun
    {
        get => Enabled ? nextRun : null;
        set { nextRun = value; Notify(nameof(NextRun)); }
    }
}

/// <summary>会话历史项</summary>
public class SessionItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    void Notify(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    string title = "";
    /// <summary>会话标题（树项实时绑定；变更发通知，保存会话不再整树重建列表也能即时刷新）。
    /// 赋值即单行化（去回车/换行/制表符）：会话列表项只支持显示一行，含旧历史与反序列化装载。</summary>
    public string Title { get => title; set { title = ToOneLine(value); Notify(nameof(Title)); Notify(nameof(TitleLine)); } }
    /// <summary>标题单行化：去除回车/换行/制表符并压缩连续空白。
    /// 标题取自用户首句或模型摘要，原文可能带换行，而会话列表项只允许显示一行。</summary>
    public static string ToOneLine(string? s) =>
        string.Join(" ", (s ?? "").Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim()).Where(x => x.Length > 0));
    /// <summary>会话列表项显示用标题（单行）：过长由 XAML TextTrimming=CharacterEllipsis 截断成 …</summary>
    [JsonIgnore]
    public string TitleLine => ToOneLine(title);
    public string Project { get; set; } = "";
    DateTime time;
    /// <summary>最后活动时间（树项绑定 TimeText；变更发通知供实时刷新）</summary>
    public DateTime Time { get => time; set { time = value; Notify(nameof(Time)); Notify(nameof(TimeText)); } }
    /// <summary>会话创建时间（创建后不再变化）：既是列表排序的默认基准，也是"本会话改动"回溯备份的时间窗起点，
    /// 属审计字段不可被排序策略改写（改写会让改动前状态取错备份）。</summary>
    public DateTime Created { get; set; }
    /// <summary>列表排序键（活跃前置）：仅当会话已掉出所在分组前 N 名、又被重新发言时才刷成当时时间，
    /// 使其一次性跳到组首；default = 从未被提前过，排序回退 Created。
    /// 前 N 名内发言不刷新 —— 避免每句话都重排导致会话列表一直变来变去。</summary>
    public DateTime SortAt { get; set; }
    /// <summary>会话列表实际排序值：SortAt 优先，未提前过的会话回退创建时间。</summary>
    [JsonIgnore]
    public DateTime SortKey => SortAt == default ? Created : SortAt;
    public string TimeText => Time.ToString("MM-dd HH:mm");
    public List<MessageRecord> Messages { get; set; } = new();

    /// <summary>内存占位标记（方案1）：点「+ 新会话」时插入左侧列表的占位项只存在于内存，左侧即时可见；
    /// 首条消息发送即"转正"（IsPending=false）随保存落盘；未发送就切走/回放/删除/切项目 → 占位项被摘除，
    /// 历史里不留空「新会话」记录。SaveSessionHistory 序列化时过滤，[JsonIgnore] 保证任何序列化路径都不会把它带进盘。</summary>
    [JsonIgnore]
    public bool IsPending { get; set; }

    // ---- 列表右侧执行进度（RunState=1 进行中时显示"轮次 + 计划进度%"；非执行中清空） ----
    int _runRounds;   // 已执行轮次（Round 事件驱动）
    int _todoDone;    // 待办已完成数
    int _todoTotal;   // 待办总数（0=未建清单）
    /// <summary>会话列表右侧活动文本：执行中 = "第N轮 · 计划 X%"；无待办仅显示轮次；未执行/已收口为空。由 Round/Todo 事件驱动。</summary>
    [JsonIgnore]
    public string ListProgressText
    {
        get
        {
            var s = "";
            if (_runRounds > 0) s += $"第{_runRounds}轮";
            if (_todoTotal > 0)
            {
                var pct = Math.Min(100, (int)Math.Round(_todoDone * 100.0 / _todoTotal));
                s += (s.Length > 0 ? " · " : "") + $"计划 {pct}%";
            }
            return s;
        }
    }
    /// <summary>增量更新执行进度字段（null 参数跳过该字段），通知列表刷新组合文本。</summary>
    public void SetListProgress(int? rounds = null, int? done = null, int? total = null)
    {
        if (rounds != null) _runRounds = rounds.Value;
        if (done != null) _todoDone = done.Value;
        if (total != null) _todoTotal = total.Value;
        Notify(nameof(ListProgressText));
    }
    /// <summary>任务启动/收口时清空轮次与待办进度（列表右侧回归时间显示）。</summary>
    public void ResetListProgress() { _runRounds = 0; _todoDone = 0; _todoTotal = 0; Notify(nameof(ListProgressText)); }

    bool selected;
    public bool Selected { get => selected; set { selected = value; Notify(nameof(Selected)); Notify(nameof(RowBg)); } }
    public Brush RowBg => Selected
        ? new SolidColorBrush(Color.FromRgb(0x15, 0x15, 0x20))
        : Brushes.Transparent;

    /// <summary>是否当前打开的会话，用于列表置顶</summary>
    public bool IsCurrent { get; set; }

    // ---- 本会话改动文件卡片（左栏会话列表下方固定卡片，点哪个会话就随动显示它改过哪些文件）----
    // 运行期：任务执行/收口时由 ChangeJournal 时间窗聚合增量写入（同文件多次改只留最后一次状态）；
    // 历史会话：打开时由 MessageRecord.Changes 还原（存量历史无该字段 → 清单为空、整卡隐藏）。
    /// <summary>本会话累计改动的文件行</summary>
    [JsonIgnore] public ObservableCollection<ChangedFileVm> ChangedFiles { get; } = new();

    bool changedOpen = true;
    /// <summary>卡片清单展开/折叠（默认展开；标题行点击切换）</summary>
    [JsonIgnore]
    public bool ChangedOpen
    {
        get => changedOpen;
        set
        {
            if (changedOpen == value) return;
            changedOpen = value;
            Notify(nameof(ChangedOpen));
            Notify(nameof(ChangedListVisible));
            Notify(nameof(ChangedArrow));
        }
    }

    /// <summary>卡片整体显隐：无任何改动记录时整卡隐藏，不挤占会话列表空间</summary>
    [JsonIgnore] public Visibility ChangedCardVisible => ChangedFiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>清单区显隐：展开且有内容</summary>
    [JsonIgnore] public Visibility ChangedListVisible => changedOpen && ChangedFiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>卡片标题计数文本（含新建数拆分）</summary>
    [JsonIgnore]
    public string ChangedCountText
    {
        get
        {
            var n = ChangedFiles.Count;
            var added = ChangedFiles.Count(v => v.IsNew);
            return added > 0 && added < n ? $" ({n} · 新增 {added})" : $" ({n})";
        }
    }

    /// <summary>卡片标题右侧折叠箭头</summary>
    [JsonIgnore] public string ChangedArrow => changedOpen ? "▾" : "▸";

    /// <summary>改动集合变化后刷新卡片（计数/显隐/箭头一并重算）</summary>
    public void RefreshChangedFiles()
    {
        Notify(nameof(ChangedCardVisible));
        Notify(nameof(ChangedCountText));
        Notify(nameof(ChangedListVisible));
    }

    /// <summary>整体替换累计清单（打开历史会话时按落盘记录还原）</summary>
    public void ResetChangedFiles(IEnumerable<ChangedFileVm> items)
    {
        ChangedFiles.Clear();
        foreach (var i in items) ChangedFiles.Add(i);
        RefreshChangedFiles();
    }

    // ---- 编排会话（8.3 决策 1：三级 1:1 = 架构节点 → 编排任务 → 编排会话） ----
    /// <summary>true=编排会话（会话列表"计划会话"分组展示）。</summary>
    public bool IsOrchestration { get; set; }
    /// <summary>关联编排任务 id（IsOrchestration=true 时）。</summary>
    public string? PlanId { get; set; }
    /// <summary>关联架构节点 id（IsOrchestration=true 时；以引用跟随，节点改名/移动不拷贝）。</summary>
    public string? NodeId { get; set; }
    /// <summary>编排会话状态机阶段：collecting|generating|done（8.3 决策 1；Generating 起禁止问答注入）。</summary>
    public string? OrchPhase { get; set; }
    /// <summary>生成方案：interactive=单模型交互式（对话 + 问题卡片点选收集）；multi=多模型决策（勾选模型 + 素材后一次生成）。
    /// 旧历史无此字段（null）按 interactive 处理。</summary>
    public string? PlanMode { get; set; }

    /// <summary>编排生成方案（旧历史缺省 interactive）。</summary>
    public string Mode => (PlanMode ?? "interactive") switch
    {
        "multi" => "multi",
        _ => "interactive",
    };

    // ---- 会话钉住执行参数（角色+模式+模型）：首个任务执行前取样顶部选择器后固定，会话全程使用并随会话持久化；
    //      切换会话时顶部选择器回显、会话内改顶部选择器时同步覆盖——顶部选择器为唯一编辑入口 ----
    /// <summary>钉住的角色 name（roles 仓库；null/空=尚未钉住，未执行过任务）。</summary>
    public string? PinRole { get; set; }
    /// <summary>钉住的模式 name（角色下；null=尚未钉住）。</summary>
    public string? PinMode { get; set; }
    /// <summary>钉住的角色显示名（头像标题/执行参数卡展示；角色包改名后仍回退存档显示名）。</summary>
    public string? PinRoleDisplay { get; set; }
    /// <summary>钉住的模式显示名。</summary>
    public string? PinModeDisplay { get; set; }
    /// <summary>钉住的模型供应商 key（=AppConfig 厂商 key）。</summary>
    public string? PinProvider { get; set; }
    /// <summary>钉住的模型 id（下拉展示名）。</summary>
    public string? PinModel { get; set; }
    /// <summary>true=该会话发言时 角色/模式 选择器停在"🤖 自动匹配"（会话选择的角色模式=自动）：
    /// 切换会话进入时顶部回显"自动匹配"项、每个回合独立重匹配具体角色（命中结果写回 PinRole 仅作"最后回合实际使用"记录）；
    /// false=会话钉具体角色，会话与每回合均按该角色执行。发言时（OnSend）与会话内改选择器（OnCbModeChanged）同步写入，随会话持久化；
    /// 旧数据（无此字段）载入时按消息快照跨回合≥2个不同 角色|mode 启发式回填。</summary>
    public bool AutoRoute { get; set; }

    /// <summary>多模型决策方案：参与决策的模型列表（"provider:modelId"，创建对话框勾选，重生成时复用）。</summary>
    public List<string> MultiModels { get; set; } = new();

    /// <summary>会话列表分组键/组显示名（编排会话归"计划会话"组，普通会话归"普通会话"组，不与计划会话混用）。</summary>
    public string ListGroup => IsOrchestration ? "计划会话" : "普通会话";

    int _runState;

    /// <summary>会话运行状态（仅 UI 引擎驱动，不入持久化白名单；随执行启停更新）。
    /// 0=空闲/默认（蓝点）；1=进行中(桔)；2=等待文件占用锁(黄)；3=中断/暂停(红，用户停止/失败/取消)；
    /// 4=本任务完成(绿，点击打开该会话后恢复 0)。顶部"会话"徽标统计 1/2/3 计数。</summary>
    public int RunState
    {
        get => _runState;
        set
        {
            if (_runState == value) return;
            _runState = value;
            Notify(nameof(RunState));
        }
    }

    int _planState;
    /// <summary>计划会话圆点聚合色（仅编排会话 IsOrchestration=true 使用）：0=无计划/无叶子（回落 RunState 色）；
    /// 1=有未完成子叶（桔，含 pending/running/reviewing）；2=子叶全部终态成功（绿，passed/skipped）；3=有失败子叶（红）。
    /// 状态变化点（执行启停/叶状态轮询）经 RefreshPlanSessionDots 聚合刷新。</summary>
    [JsonIgnore]
    public int PlanState
    {
        get => _planState;
        set
        {
            if (_planState == value) return;
            _planState = value;
            Notify(nameof(PlanState));
        }
    }

    int _pendingKind;
    /// <summary>待决策提醒角标（会话树行右侧小徽标，仅 UI 引擎驱动不入持久化）：
    /// 0=无挂起；1=危险命令待裁决（该会话 TaskRecord.DangerCard 在卡）；2=计划审批/方案待确认（挂起中）。
    /// 仅"非当前会话存在人工介入点挂起"时由 RefreshPendingBadges 置位——两会话并行各自独立判定、互不串台；
    /// 切回该会话处理决策卡/任务收口后归 0。每会话一份，随 uiTimer 事件泵实时刷新。</summary>
    [JsonIgnore]
    public int PendingKind
    {
        get => _pendingKind;
        set
        {
            if (_pendingKind == value) return;
            _pendingKind = value;
            Notify(nameof(PendingKind));
        }
    }
}

/// <summary>会话历史分组节点（"普通会话"/"计划会话"），驱动会话树顶层折叠/展开。
/// IsOpen 供 TreeViewItem.IsExpanded 双向绑定，折叠/展开状态经窗口侧记录可在列表刷新重建后保持。</summary>
public class SessionGroup
{
    public string Name { get; set; } = "";
    public bool IsOpen { get; set; } = true;
    /// <summary>该分组真实会话总数（组头计数显示，不受"加载更多"分页截断影响）。</summary>
    public int Total { get; set; }
    /// <summary>组内当前展示的子项集合：可见的 SessionItem + 组尾（还有更多时）的 SessionMoreItem 加载更多占位。
    /// 元素类型为 object：两类子项靠 TreeView DataTemplate DataType 分派，故与 Total 不同——Total 不含占位。</summary>
    public ObservableCollection<object> Items { get; set; } = new();
}

/// <summary>会话历史分组内的"加载更多"占位项：点击后该分组再展示一页（10 条）历史。
/// 仅作为 UI 叶子挂在 SessionGroup.Items 末尾，不映射真实会话，点击打开/删除等会话行为对其不适用。</summary>
public class SessionMoreItem
{
    /// <summary>所属分组名（如"普通会话"），点击加载更多时据此把该组已展示条数 +1 页。</summary>
    public string GroupName { get; set; } = "";
    /// <summary>该组尚未展示的剩余条数（提示文案用）。</summary>
    public int Remain { get; set; }
    /// <summary>加载更多行文案：箭头 + 剩余条数。</summary>
    public string Hint => $"加载更多 · 还有 {Remain} 条 ▾";
}

/// <summary>bool → Brush 转换器（技能启用状态：启用=绿色，禁用=红色）</summary>
public class BoolToBrushConverter : System.Windows.Data.IValueConverter
{
    static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x3E, 0xCF, 0x8E));
    static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xF0, 0x4E, 0x4E));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Green : Red;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>项目列表项（ComboBox 绑定用）</summary>
public class ProjectItem
{
    public string Name { get; set; }
    public string Path { get; set; }
    public ProjectItem(string name, string path) { Name = name; Path = path; }
    public override string ToString() => $"{Name} · {Path}";
}

/// <summary>项目目录树节点（真实文件系统：支持展开/收缩/选中）</summary>
public class TreeNode : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    void Notify(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    string name = "";
    public string Name { get => name; set { name = value; Notify(nameof(Name)); } }
    public string FullPath { get; set; } = "";
    public bool IsDir { get; set; }
    public int Level { get; set; }

    bool isOpen;
    public bool IsOpen { get => isOpen; set { isOpen = value; Notify(nameof(IsOpen)); Notify(nameof(Glyph)); Notify(nameof(ChildrenVisible)); } }

    bool isSelected;
    /// <summary>选中态：仅驱动 TreeViewItem 选中底色（模板触发器），文字颜色保持不变</summary>
    public bool IsSelected { get => isSelected; set { isSelected = value; Notify(nameof(IsSelected)); } }

    public ObservableCollection<TreeNode> Children { get; } = new();

    public string Glyph => IsDir ? (IsOpen ? "▾" : "▸") : "·";
    public Visibility ChildrenVisible => IsDir && IsOpen ? Visibility.Visible : Visibility.Collapsed;
    public Thickness Margin => new Thickness(8 + Level * 16, 4, 0, 4);

    /// <summary>类型语义色（#RRGGBB，空=默认灰）：目录青/代码紫/配置琥珀/文档绿。<br/>
    /// 带通知：清单标记切换时就地改色即可刷新，无需重绘整棵树。<br/>
    /// 选中态不改文字色（只加底色），保持用户习惯的类型色识别</summary>
    string colorHex = "";
    public string ColorHex
    {
        get => colorHex;
        set { colorHex = value; Notify(nameof(ColorHex)); Notify(nameof(Foreground)); }
    }
    public Brush Foreground => ColorHex.Length == 0
        ? new SolidColorBrush(Color.FromRgb(0xA0, 0xA8, 0xB8))
        : new SolidColorBrush((Color)ColorConverter.ConvertFromString(ColorHex));

    // ---- git 变更节点扩展（左侧"项目跟踪"分组：commit 行 / commit 内变更文件行；普通目录文件节点不使用） ----

    /// <summary>节点类型标识，事件分流用：空=文件系统节点（目录/文件，双击系统打开）；
    /// "group-fs"=分组-项目文件（工作目录）；"group-git"=分组-项目跟踪（git 变更）；
    /// "git-c"=commit 行（展开懒加载文件清单）；"git-f"=commit 内变更文件行（双击看 diff）。</summary>
    public string Tag { get; set; } = "";
    /// <summary>git 专用：所属 commit 完整哈希（commit 行/变更文件行使用，其他节点为空）</summary>
    public string GitHash { get; set; } = "";
    /// <summary>git commit 行：归属任务标识（提交信息 auto@key 解析所得；手动提交为空）</summary>
    public string? GitKey { get; set; }
    /// <summary>git commit 行：是否 GAIRR 自动提交（驱动行色区分；手动提交灰系）</summary>
    public bool GitAuto { get; set; }

    string sub = "";
    /// <summary>主行下方的次行小字（commit 行：短 hash · 相对时间 · 归属；变更文件行：行数统计）。空=不显示次行，行高不变。</summary>
    public string Sub
    {
        get => sub;
        set { sub = value ?? ""; Notify(nameof(Sub)); Notify(nameof(SubVisible)); }
    }
    public System.Windows.Visibility SubVisible => sub.Length > 0
        ? System.Windows.Visibility.Visible
        : System.Windows.Visibility.Collapsed;

    /// <summary>节点 ToolTip 完整文本（git 行：完整提交信息/完整路径等；空=无 ToolTip）</summary>
    public string? Tip { get; set; }
}

/// <summary>git diff 浮层查看器行模型（贴左树“提交详情”浮层）：Text=原始行文本；Bg/Fg 由解析器按行首标记着色
/// （+ 新增绿 / - 删除红 / @@ 区块琥珀 / 头部与元信息灰蓝 / 上下文默认），行色固化于模型，供 AvalonEdit 只读查看器的
/// 渲染器按文档行读取（Bg=文本区/行号区行底，Fg=整行文本前景与行号列行号色）；
/// OldNo/NewNo=该行在 hunk 内的旧/新文件行号（@@ 头起始号逐行推进；非内容行 null 不显示行号列）。</summary>
public class GitDiffRow
{
    /// <summary>上下文行默认前景（灰白）。静态冻结：GitDiffRow 允许在任意线程构造（diff 行解析在后台时），
    /// 未冻结 Brush 归属创建线程，UI 侧绑定会抛“无法绑定到不同线程上创建的 DependencySource”；冻结后跨线程共享安全。</summary>
    static readonly Brush CtxFg = MakeCtxFg();
    static Brush MakeCtxFg()
    {
        var b = new SolidColorBrush(Color.FromRgb(0xC2, 0xC9, 0xD6));
        b.Freeze();
        return b;
    }

    public string Text { get; set; }
    public Brush? Bg { get; set; }
    public Brush Fg { get; set; }

    /// <summary>旧文件行号（删除行/上下文行；@@ 与文件头行、新增行为 null）</summary>
    public int? OldNo { get; set; }
    /// <summary>新文件行号（新增行/上下文行；@@ 与文件头行、删除行为 null）</summary>
    public int? NewNo { get; set; }

    public GitDiffRow(string text, Brush? bg = null, Brush? fg = null)
    {
        Text = text;
        Bg = bg;
        Fg = fg ?? CtxFg;
    }
}

/// <summary>执行过程时间线条目基类：思考条与工具卡共用同一有序列表，按发生时间交错排列</summary>
public abstract class ProcessItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Notify(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    bool open;
    /// <summary>卡片展开/收缩（同一时刻只展开最新一张）</summary>
    public bool Open { get => open; set { open = value; Notify(nameof(Open)); } }
}

/// <summary>待办分步清单卡片：模型经 UpdateTodo 拆分任务步骤，随执行逐条勾选（计划视角，气泡内最顶部）</summary>
public class TodoItem : ProcessItem
{
    string title = "", content = "";

    public string Title { get => title; set { title = value; Notify(nameof(Title)); } }
    /// <summary>步骤清单（多行，每行：状态符号 + 序号 + 步骤文本）</summary>
    public string Content { get => content; set { content = value; Notify(nameof(Content)); } }
}

/// <summary>每轮思考条（标题“第 N 轮思考”，内容超长截断尾部加 ...；Full 保存完整原文供工具卡出现时补全）</summary>
public class ThinkingItem : ProcessItem
{
    string title = "", content = "";
    string? full;
    int durationMs;
    int contextTokens;

    /// <summary>卡片创建（该轮思考发生）时刻，用于在标题右侧显示日期时间；历史回放用持久化值恢复</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    /// <summary>格式化后的日期时间显示文本，如 "MM-dd HH:mm"</summary>
    public string TimeText => CreatedAt.ToString("MM-dd HH:mm");
    public string Title { get => title; set { title = value; Notify(nameof(Title)); } }
    public string Content { get => content; set { content = value; Notify(nameof(Content)); } }
    /// <summary>完整思考原文（未截断）：工具卡出现时若 Content 是截断预览，用 Full 替换补全</summary>
    public string? Full
    {
        get => full;
        set { full = value; Notify(nameof(Full)); }
    }
    /// <summary>本轮模型调用耗时（毫秒）：用于在卡片右上角显示</summary>
    public int DurationMs
    {
        get => durationMs;
        set { durationMs = value; Notify(nameof(DurationMs)); Notify(nameof(DurationText)); }
    }
    /// <summary>格式化后的耗时显示文本，如 "1.2s"，0 时为空字符串</summary>
    public string DurationText => durationMs > 0 ? $"{durationMs / 1000.0:F1}s" : "";
    /// <summary>耗时文本的可见性：有耗时值时显示，否则隐藏</summary>
    public System.Windows.Visibility DurationVisible => durationMs > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    /// <summary>深度思考直播卡标记：思考期间实时预览思维动向（ThinkingLive 事件逐帧刷新），轮结束由 Round 事件就地转正为正式思考卡</summary>
    public bool IsLive { get; set; }
    int round;
    /// <summary>所属轮次（1 基，与 Round 事件同源）：供「上下文」按钮定位请求上下文快照文件；0=无归属（旧数据）不显示按钮</summary>
    public int Round
    {
        get => round;
        set { round = value; Notify(nameof(Round)); Notify(nameof(CtxVisible)); }
    }
    /// <summary>「查看」按钮可见性：有轮次归属（快照文件可能已落盘）时显示</summary>
    public System.Windows.Visibility CtxVisible => Round > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    /// <summary>本轮发送给模型的上下文 token 数（Prompt tokens）</summary>
    public int ContextTokens
    {
        get => contextTokens;
        set { contextTokens = value; Notify(nameof(ContextTokens)); Notify(nameof(ContextText)); Notify(nameof(ContextVisible)); }
    }
    /// <summary>格式化后的上下文大小显示文本，如 "12.3K"，0 时为空字符串</summary>
    public string ContextText => contextTokens > 0 ? $"{contextTokens / 1000.0:F1}K" : "";
    /// <summary>上下文大小文本的可见性：有值时显示，否则隐藏</summary>
    public System.Windows.Visibility ContextVisible => contextTokens > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
}

/// <summary>工具调用卡片（对应官网 details/summary 折叠块，属性变化实时驱动模板）</summary>
public class ToolCall : ProcessItem
{
    string title = "", status = "", inner = "", summary = "";
    bool warn, isStep;
    DiffLine[]? diffLines;   // Inner 解析出的 diff 行（null=非 diff）

    string titlePrefix = "";
    string icon = "";
    string iconColor = "#5C8AE6";
    double iconFontSize = 13;
    public string Title { get => title; set { title = value; Notify(nameof(Title)); Notify(nameof(Header)); } }
    public string Status { get => status; set { status = value; Notify(nameof(Status)); } }
    /// <summary>工具图标（Unicode emoji），显示在卡片标题前</summary>
    public string Icon { get => icon; set { icon = value; Notify(nameof(Icon)); } }
    /// <summary>工具图标颜色（Hex 字符串，如 #5C8AE6）</summary>
    public string IconColor { get => iconColor; set { iconColor = value; Notify(nameof(IconColor)); } }
    /// <summary>工具图标字号（WPF FontSize）</summary>
    public double IconFontSize { get => iconFontSize; set { iconFontSize = value; Notify(nameof(IconFontSize)); } }
    /// <summary>工具结果正文；以【diff】开头时解析为 DiffLines 走红绿 diff 渲染（>200 行回退纯文本预览）</summary>
    public string Inner
    {
        get => inner;
        set
        {
            inner = value;
            var d = ParseDiff(inner);
            diffLines = d;
            Notify(nameof(Inner)); Notify(nameof(DiffLines)); Notify(nameof(IsDiff));
        }
    }
    /// <summary>diff 行集合（Inner 解析产出；null=非 diff，走原纯文本预览）</summary>
    public DiffLine[]? DiffLines => diffLines;
    /// <summary>是否 diff 视图（XAML 模板据此切换 ItemsControl 红绿渲染）</summary>
    public bool IsDiff => diffLines != null;
    /// <summary>结果摘要（如 共 72 行 · 返回 1-72），收缩态标题旁展示增强可读性</summary>
    public string Summary { get => summary; set { summary = value; Notify(nameof(Summary)); } }
    /// <summary>状态文字颜色：绿=成功，琥珀=警告/失败</summary>
    public bool Warn { get => warn; set { warn = value; Notify(nameof(Warn)); Notify(nameof(StatusBrush)); } }
    string filePath = "";
    int startLine, endLine;
    /// <summary>文件工具目标路径（相对项目根，正斜杠；Read 成功回填），供"查看代码"打开快照并定位</summary>
    public string FilePath { get => filePath; set { filePath = value; Notify(nameof(FilePath)); Notify(nameof(CodeVisible)); } }
    /// <summary>实际读取行段起止（1 基含端点；0=未指定），供代码查看器自动定位突显</summary>
    public int StartLine { get => startLine; set { startLine = value; Notify(nameof(StartLine)); } }
    public int EndLine { get => endLine; set { endLine = value; Notify(nameof(EndLine)); } }
    /// <summary>是否显示"查看代码"入口（仅文件工具成功回填路径后）</summary>
    public bool CodeVisible => filePath.Length > 0;
    /// <summary>是否计划步骤相关卡片（创建/开始/完成计划步骤），UI 标题使用橙色区分</summary>
    public bool IsStep { get => isStep; set { isStep = value; Notify(nameof(IsStep)); } }
    int round;
    /// <summary>轮数前缀，如 [6]，显示在工具条标题前。注意：setter 直接改 round 字段，不得回调 Round setter，否则两者互调无限递归导致栈溢出</summary>
    public string TitlePrefix
    {
        get => titlePrefix;
        set { titlePrefix = value; round = ParseRound(value); Notify(nameof(TitlePrefix)); Notify(nameof(Round)); Notify(nameof(Header)); }
    }
    /// <summary>所属轮次，持久化/重建时使用。注意：setter 直接改 titlePrefix 字段，不得回调 TitlePrefix setter</summary>
    public int Round { get => round; set { round = value; titlePrefix = value > 0 ? $"[{value}]" : ""; Notify(nameof(Round)); Notify(nameof(TitlePrefix)); Notify(nameof(Header)); } }
    /// <summary>标题文本（不含轮次前缀，轮次信息由 UI 另行展示）</summary>
    public string Header => Title.Trim();

    static int ParseRound(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        var m = System.Text.RegularExpressions.Regex.Match(s.Trim(), @"^\[(\d+)\]");
        return m.Success && int.TryParse(m.Groups[1].Value, out var r) ? r : 0;
    }

    /// <summary>解析 Inner 中的 diff 段：以【diff】标记开头且行数 ≤200 时解析为 DiffLine 数组走红绿渲染，否则返回 null 回退纯文本预览</summary>
    static DiffLine[]? ParseDiff(string text)
    {
        if (!text.StartsWith(GAIRR.Core.Differ.Marker)) return null;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length > 200) return null;
        var list = new List<DiffLine>();
        foreach (var l in lines)
        {
            if (l.Length == 0) continue;
            var c = l[0];
            if (c == '+') list.Add(new DiffLine { Text = l, IsAdd = true });
            else if (c == '-') list.Add(new DiffLine { Text = l, IsDel = true });
            else list.Add(new DiffLine { Text = l });   // 头部/上下文行
        }
        return list.Count > 0 ? list.ToArray() : null;
    }

    public Brush StatusBrush => Warn
        ? new SolidColorBrush(Color.FromRgb(0xF0, 0xA1, 0x3E))
        : new SolidColorBrush(Color.FromRgb(0x3E, 0xCF, 0x8E));
}

/// <summary>diff 卡单行视图模型：'+'行绿底、'-'行红底、其余（头部/上下文）正常色</summary>
public class DiffLine
{
    public string Text { get; set; } = "";
    public bool IsAdd { get; set; }
    public bool IsDel { get; set; }
    /// <summary>行前景色：加=绿、删=红、其他=正常文字色</summary>
    public Brush FgBrush => IsAdd
        ? new SolidColorBrush(Color.FromRgb(0x3E, 0xCF, 0x8E))
        : IsDel ? new SolidColorBrush(Color.FromRgb(0xF0, 0x4E, 0x4E))
        : new SolidColorBrush(Color.FromRgb(0xC8, 0xCC, 0xD8));
    /// <summary>行背景色：加=淡绿、删=淡红、其他透明</summary>
    public Brush BgBrush => IsAdd
        ? new SolidColorBrush(Color.FromArgb(0x28, 0x3E, 0xCF, 0x8E))
        : IsDel ? new SolidColorBrush(Color.FromArgb(0x28, 0xF0, 0x4E, 0x4E))
        : Brushes.Transparent;
}

/// <summary>危险操作确认卡片：嵌入执行过程时间线（工具卡下方），含允许/取消按钮，决策或超时后自动隐藏</summary>
public class DangerConfirmItem : ProcessItem
{
    string content = "";
    bool visible = true;
    int countdownSeconds = -1;   // -1=不自动拒绝：Agent 侧危险确认永不超时（挂起等人工裁决；人离开后由 Agent 的 IdleGate 静默期钉钉提醒）
    string countdownText = "等待人工确认…（不会自动拒绝，人不在时会钉钉提醒）";

    /// <summary>确认卡片正文（危险行为 + 命中规则 + 模型意图）</summary>
    public string Content { get => content; set { content = value; Notify(nameof(Content)); } }
    /// <summary>卡片可见性：决策或收起后设为 false 自动隐藏</summary>
    public bool Visible { get => visible; set { visible = value; Notify(nameof(Visible)); Notify(nameof(Vis)); } }
    public Visibility Vis => visible ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>等待状态：-1=等待人工确认不自动拒绝（默认）；&gt;=0 时保留倒计时展示（兼容旧逻辑）。设置时自动更新 CountdownText。</summary>
    public int CountdownSeconds
    {
        get => countdownSeconds;
        set
        {
            countdownSeconds = value;
            countdownText = value < 0
                ? "等待人工确认…（不会自动拒绝，人不在时会钉钉提醒）"
                : $"剩余 {value} 秒";
            Notify(nameof(CountdownSeconds));
            Notify(nameof(CountdownText));
        }
    }
    /// <summary>倒计时显示文本（由 CountdownSeconds 派生）</summary>
    public string CountdownText { get => countdownText; private set { countdownText = value; Notify(nameof(CountdownText)); } }
}

public enum MsgKind { Agent, User, Typing, Cmd }

/// <summary>一条聊天消息（演示用，属性直接驱动模板各区块显隐）</summary>
public class ChatMessage : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public MsgKind Kind { get; set; }
    public string Who { get; set; } = "";
    /// <summary>GAIRR 头像标题（角色+模式+模型）：按会话钉住执行参数生成，仅实时显示不入历史；
    /// 运行期由 UI 层在消息入列/历史装载时填充，悬停头像（ToolTip）可见。</summary>
    [JsonIgnore] public string AvatarTitle { get; set; } = "";
    // ---- 消息级执行参数快照（运行时）：该条消息入列时刻 钉住的 角色+模式+模型 六要素，
    //      由 AddMessage 统一取样写入，随消息落盘到 MessageRecord.RunXxx（历史重放按各消息自身快照显示）----
    [JsonIgnore] public string? RunRole { get; set; }
    [JsonIgnore] public string? RunRoleDisplay { get; set; }
    [JsonIgnore] public string? RunMode { get; set; }
    [JsonIgnore] public string? RunModeDisplay { get; set; }
    [JsonIgnore] public string? RunProvider { get; set; }
    [JsonIgnore] public string? RunModel { get; set; }
    public string? Cmd { get; set; }
    public string? Text { get; set; }
    /// <summary>运行期状态提示（编排执行日志/计划完成提示等）：仅当前会话显示，不写入会话历史。
    /// 避免重开历史时回放出大段执行噪音。</summary>
    [JsonIgnore] public bool NoPersist { get; set; }
    public string? Note { get; set; }
    /// <summary>安全警报标记：true 时对话气泡使用红色警告样式</summary>
    public bool IsAlert { get; set; }
    /// <summary>警报级别：Block=已拦截，Confirm=等待用户确认，Info=普通提示</summary>
    public string AlertLevel { get; set; } = "Block";
    /// <summary>危险确认中（Confirm）显示 [允许执行][取消] 按钮区；决策后调用 CloseDangerActions 收起</summary>
    public Visibility DangerActionsVisible =>
        IsAlert && AlertLevel == "Confirm" ? Visibility.Visible : Visibility.Collapsed;

    public void CloseDangerActions(string tail)
    {
        AlertLevel = "Block";
        Text += tail;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DangerActionsVisible)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
    }
    string? steps;
    public string? Steps
    {
        get => steps;
        set { steps = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Steps))); }
    }
    /// <summary>Steps 基础文本（不含展开/收缩提示后缀）</summary>
    public string StepsBase { get; set; } = "";
    /// <summary>执行过程时间线：思考条与工具卡按实际发生顺序交错排列（Add 即时驱动增量渲染）</summary>
    public ObservableCollection<ProcessItem> ProcessItems { get; } = new();

    /// <summary>历史重放的待装配时间线条目（惰性加载）：打开历史会话时只挂记录骨架不实例化卡片，
    /// 用户首次展开该轮（ProcessOpen）时才转换为 ProcessItems（见 MainWindow.MaterializePending），
    /// 降低长历史会话的首屏构建成本。实时消息为 null；未展开即保存时由保存侧连同 ProcessItems 一并落盘。</summary>
    public List<ProcessItemRecord>? PendingItems { get; set; }

    public Visibility AgentVisible =>
        Kind is MsgKind.Agent or MsgKind.Typing or MsgKind.Cmd
            ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UserVisible =>
        Kind == MsgKind.User ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TypingVisible =>
        Kind == MsgKind.Typing ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>等待模型返回阶段（等待提示条显示"正在思考"）</summary>
    public bool IsWaitingForModel { get; set; }
    /// <summary>工具执行阶段（等待提示条文字切换为"正在执行"）</summary>
    public bool IsRunningTool { get; set; }
    /// <summary>上下文压缩整理阶段（等待提示条文字切换为"正在整理上下文"，压缩结束后由 WaitingModel 事件接替）</summary>
    public bool IsCompressing { get; set; }
    /// <summary>等待提示文字：思考/执行/压缩三态切换——调用大模型时"正在思考"，工具执行时"正在执行"，压缩上下文时"正在整理上下文"</summary>
    public string WaitingText => IsRunningTool ? "正在执行" : IsCompressing ? "正在整理上下文" : "正在思考";
    /// <summary>模型等待/工具执行/上下文压缩期间显示左侧状态提示条（三点加载动画）</summary>
    public Visibility WaitingVisible =>
        Kind == MsgKind.Typing && (IsWaitingForModel || IsRunningTool || IsCompressing) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BubbleVisible =>
        Kind == MsgKind.Typing ? Visibility.Collapsed : Visibility.Visible;
    public Visibility CmdVisible => Vis(Cmd);
    public Visibility TextVisible => Vis(Text);
    public Visibility NoteVisible => Vis(Note);
    public Visibility StepsVisible => Vis(Steps);

    /// <summary>任务是否已出结果（出结果后过程区默认收缩）</summary>
    public bool Finished { get; set; }

    // ---- 编排问题卡片（8.3 决策 1 修订：收集期主持人以卡片提问，用户点选作答，无需手打长文本） ----
    /// <summary>本轮模型回复携带的问题卡片（解析 question-card 块所得）。卡片本体不随历史持久化：
    /// 提交后答案折叠为摘要（Summary）并作为用户消息文本进入历史，重开会话只回放文字问答。</summary>
    public List<QuestionCardVm> QuestionCards { get; } = new();
    public Visibility QuestionCardsVisible =>
        QuestionCards.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    // ---- 多卡公共提交：同一消息同时给出多张问题卡时，卡组底部显示公共“提交答案”条；
    //      全部未提交卡作答完成才可点击（未完成禁用），一次性把本批答案合并为一条用户消息提交。
    //      单卡消息仍走卡内提交按钮，本组状态仅在 MultiCardMode 下供 UI 消费 ----
    /// <summary>添加问题卡并订阅其作答变化（勾选/补充说明文本/提交折叠）→ 实时重算公共提交条状态。</summary>
    public void AddQuestionCard(QuestionCardVm card)
    {
        QuestionCards.Add(card);
        card.AnswerChanged += OnCardAnswerChanged;
        InvalidateMultiSubmit();
    }

    /// <summary>卡组作答状态变化（勾选/补充说明文本/提交折叠，含本消息任何卡）：会话级澄清进度提示订阅用——
    /// UI 窗口层聚合当前会话全部消息的卡作答进度（还差几题/可生成任务树），本消息内部订阅之外的二级上抛。</summary>
    public event Action? CardsChanged;

    void OnCardAnswerChanged()
    {
        MultiSubmitError = null;   // 用户再次作答后清除过期拦截提示
        InvalidateMultiSubmit();
        CardsChanged?.Invoke();   // 上抛会话级：澄清进度提示随每次作答/提交实时刷新
    }

    /// <summary>同消息是否多张卡（>1 张时卡内“提交答案”按钮让位于底部公共提交条）。</summary>
    public bool MultiCardMode => QuestionCards.Count > 1;

    /// <summary>未提交卡数（含已作答未提交；整批提交后全部折叠归零）。</summary>
    public int PendingCards { get; private set; }

    /// <summary>全部未提交卡均已作答（未完成时公共按钮禁用，满足“全部完成才可提交”）。</summary>
    public bool AllCardsDone { get; private set; }

    /// <summary>公共提交条显隐：多卡且仍有未提交卡时显示；整批提交后折叠隐藏。</summary>
    public Visibility MultiSubmitVisible =>
        MultiCardMode && PendingCards > 0 ? Visibility.Visible : Visibility.Collapsed;

    string? multiSubmitHint;
    /// <summary>进度提示：剩余未完成题数（全部完成时为 null 不显示）。</summary>
    public string? MultiSubmitHint
    {
        get => multiSubmitHint;
        private set
        {
            multiSubmitHint = value;
            Notify(nameof(MultiSubmitHint));
            Notify(nameof(MultiSubmitHintVisible));
        }
    }
    public Visibility MultiSubmitHintVisible =>
        string.IsNullOrEmpty(MultiSubmitHint) ? Visibility.Collapsed : Visibility.Visible;

    string? multiSubmitError;
    /// <summary>公共提交被拦截时的错误提示（非收集阶段等），显示在提交条右侧（作答变化自动清除）。</summary>
    public string? MultiSubmitError
    {
        get => multiSubmitError;
        private set
        {
            multiSubmitError = value;
            Notify(nameof(MultiSubmitError));
            Notify(nameof(MultiSubmitErrorVisible));
        }
    }
    public Visibility MultiSubmitErrorVisible =>
        string.IsNullOrEmpty(MultiSubmitError) ? Visibility.Collapsed : Visibility.Visible;

    public void SetMultiSubmitError(string err) => MultiSubmitError = err;

    void InvalidateMultiSubmit()
    {
        var pending = QuestionCards.Where(c => !c.Answered).ToList();
        PendingCards = pending.Count;
        var left = pending.Count(c => !c.HasAnswer);
        AllCardsDone = pending.Count > 0 && left == 0;
        MultiSubmitHint = left > 0 ? $"还有 {left} 题未作答，全部作答完成后才能提交" : null;
        Notify(nameof(PendingCards));
        Notify(nameof(AllCardsDone));
        Notify(nameof(MultiSubmitVisible));
    }

    void Notify(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    bool processOpen;
    /// <summary>完成后用户是否点击展开了过程区</summary>
    public bool ProcessOpen
    {
        get => processOpen;
        set
        {
            processOpen = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProcessOpen)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProcessVisible)));
        }
    }
    /// <summary>过程区（工具卡列表）显隐：无过程内容（思路/工具卡/轮次内容）时不显示空框；
    /// 有内容时轮中常显、完成后仅展开时可见</summary>
    public Visibility ProcessVisible =>
        HasProcessContent && (!Finished || ProcessOpen) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>过程区是否有内容（思考条/工具卡；含尚未实例化的 Pending 惰性记录，供收缩条展开/计数判定）</summary>
    public bool HasProcessContent => ProcessItems.Count > 0 || (PendingItems?.Count ?? 0) > 0;

    /// <summary>Steps 行仅在有过程内容（分析思路/工具卡/轮次内容）且已完成时可交互（小手），否则普通箭头</summary>
    public System.Windows.Input.Cursor StepsCursor =>
        Finished && HasProcessContent ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow;

    /// <summary>Steps 行提示：不可交互时无 ToolTip</summary>
    public string? StepsTip =>
        Finished && HasProcessContent ? "点击展开/收缩执行过程" : null;

    static Visibility Vis(string? s) =>
        string.IsNullOrEmpty(s) ? Visibility.Collapsed : Visibility.Visible;

    // ---- 本轮改动文件条带（结果气泡下方一行摘要，点击展开文件清单）----
    // 数据来源：任务收口时按 ChangeJournal 时间窗聚合本会话本轮的写入记录；随 MessageRecord.Changes 落盘，
    // 历史会话回放时由记录还原（无记录 = 条带整体隐藏，不占消息流空间）。
    /// <summary>本轮改动的文件行（同文件多次改动只保留最后一次状态）</summary>
    public ObservableCollection<ChangedFileVm> RoundChanges { get; } = new();

    bool changesOpen;
    /// <summary>条带展开/折叠（默认折叠：只显示"本轮改动 N 个文件"一行摘要）</summary>
    public bool ChangesOpen
    {
        get => changesOpen;
        set { changesOpen = value; Notify(nameof(ChangesOpen)); Notify(nameof(ChangesListVisible)); Notify(nameof(ChangesArrow)); }
    }

    /// <summary>条带整体显隐：本轮无文件改动时隐藏</summary>
    public Visibility ChangesVisible => RoundChanges.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>文件清单区显隐：展开且有内容</summary>
    public Visibility ChangesListVisible => changesOpen && RoundChanges.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>摘要行文本（含新建/修改拆分计数）</summary>
    public string ChangesText
    {
        get
        {
            var n = RoundChanges.Count;
            var added = RoundChanges.Count(v => v.IsNew);
            return added > 0 && added < n ? $"本轮改动 {n} 个文件（新增 {added}）" : $"本轮改动 {n} 个文件";
        }
    }

    /// <summary>摘要行右侧折叠箭头</summary>
    public string ChangesArrow => changesOpen ? "▾" : "▸";

    /// <summary>改动集合变化后刷新条带（计数/摘要/显隐一并重算）</summary>
    public void RefreshChanges()
    {
        Notify(nameof(ChangesVisible));
        Notify(nameof(ChangesText));
        Notify(nameof(ChangesListVisible));
    }

    // ---- 失败/异常气泡「重试」入口 ----
    /// <summary>是否失败/异常终态气泡（产品收口约定：Agent 终态正文以“⚠”开头 = 模型调用失败/工具执行阶段中止，
    /// 见 UiEventType.Failed 与孤儿中止两处收口；“发送失败：”前缀为发送管道异常出口）。正常/成功消息恒不显示。</summary>
    public Visibility RetryVisible =>
        Kind == MsgKind.Agent && !IsAlert && !string.IsNullOrEmpty(Text) &&
        ((Text!.StartsWith("⚠") && Finished) || Text!.StartsWith("发送失败"))
            ? Visibility.Visible : Visibility.Collapsed;

    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
}

/// <summary>问题卡片选项行（编排收集期 question-card 渲染）：Rec=true 为模型推荐项（显示 ⭐ 推荐）。</summary>
public class QuestionOptionVm
{
    /// <summary>所属卡片（代码同步选择状态用；卡片不随历史持久化，无序列化循环风险）。</summary>
    internal QuestionCardVm? Owner { get; set; }
    public string Text { get; set; } = "";
    public bool Rec { get; set; }
}

/// <summary>
/// 编排问题卡片 UI 模型：单选（Multi=false，RadioButton 互斥）或多选（Multi=true，CheckBox 打勾）；
/// 视觉状态由控件自身负责，逻辑状态在 SyncSelect/Selected 同步；提交后整卡折叠成一行答案摘要。
/// </summary>
public class QuestionCardVm : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    void Notify(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    /// <summary>作答状态变化（勾选/补充说明文本/提交折叠）：宿主消息订阅后重算多卡公共提交条状态。</summary>
    public event Action? AnswerChanged;
    void RaiseAnswerChanged() => AnswerChanged?.Invoke();

    /// <summary>唯一 ID：单选组 RadioButton GroupName 用（同条消息多卡互不串组）；历史还原时回填原值保持组名稳定。</summary>
    public string CardId { get; set; } = Guid.NewGuid().ToString("N");

    public string Question { get; set; } = "";
    /// <summary>问题序号：会话内自增（主持人每轮澄清问题顺延编号），UI 题面前缀显示。</summary>
    public int No { get; set; }
    public bool Multi { get; set; }
    /// <summary>作答方式提示（题面下小字：单选/可多选打勾）。</summary>
    public string MultiTip => Multi ? "可多选（打勾）" : "单选";
    /// <summary>选项列表（构造时一次性填充，不动态增减）。</summary>
    public List<QuestionOptionVm> Options { get; } = new();

    /// <summary>当前选中项（单选最多一项；Multi=true 可多项）。</summary>
    public List<QuestionOptionVm> Selected { get; } = new();

    /// <summary>是否已作答（可提交）：至少勾选一个选项，或补充说明有实际文本。</summary>
    public bool HasAnswer => Selected.Count > 0 || !string.IsNullOrWhiteSpace(ExtraText);

    string? extraText;
    /// <summary>补充说明文本（「✏️ 补充说明」展开的输入框内容，TwoWay）。</summary>
    public string? ExtraText
    {
        get => extraText;
        set { extraText = value; Notify(nameof(ExtraText)); RaiseAnswerChanged(); }
    }

    bool extraOpen;
    /// <summary>补充说明输入框是否展开。</summary>
    public bool ExtraOpen { get => extraOpen; set { extraOpen = value; Notify(nameof(ExtraOpen)); Notify(nameof(ExtraVisible)); } }
    public Visibility ExtraVisible => ExtraOpen ? Visibility.Visible : Visibility.Collapsed;

    bool answered;
    /// <summary>已提交：操作区隐藏，卡片折叠成一行答案摘要（答案以用户消息进入历史）。</summary>
    public bool Answered
    {
        get => answered;
        set { answered = value; Notify(nameof(Answered)); Notify(nameof(ActionVisible)); Notify(nameof(AnswerVisible)); RaiseAnswerChanged(); }
    }
    public Visibility ActionVisible => Answered ? Visibility.Collapsed : Visibility.Visible;
    public Visibility AnswerVisible => Answered ? Visibility.Visible : Visibility.Collapsed;

    string summary = "";
    /// <summary>已提交答案摘要（AnswerVisible 时显示在卡内）。</summary>
    public string Summary { get => summary; set { summary = value; Notify(nameof(Summary)); } }

    string? errorText;
    /// <summary>提交校验失败提示（如未选任何项也未写补充）。</summary>
    public string? ErrorText { get => errorText; set { errorText = value; Notify(nameof(ErrorText)); Notify(nameof(ErrorVisible)); } }
    public Visibility ErrorVisible => string.IsNullOrWhiteSpace(ErrorText) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>选项勾选状态同步（行控件 Checked/Unchecked 驱动；单选双序安全：先新后旧或先旧后新均收敛）。</summary>
    internal void SyncSelect(QuestionOptionVm opt, bool isChecked)
    {
        if (Multi)
        {
            if (isChecked) { if (!Selected.Contains(opt)) Selected.Add(opt); }
            else Selected.Remove(opt);
        }
        else
        {
            if (isChecked) { Selected.Clear(); Selected.Add(opt); }
            else Selected.Remove(opt);
        }
        RaiseAnswerChanged();   // 勾选状态变化：宿主刷新公共提交条可用性
    }

    /// <summary>组装答案文本（提交后作为用户回复发送）：题目 + 所选 + 补充说明；空=无任何作答。</summary>
    public string BuildAnswerText()
    {
        var sb = new StringBuilder();
        sb.Append('[').Append(Question).Append(']');
        if (Selected.Count > 0) sb.Append("选择：").Append(string.Join("、", Selected.Select(o => o.Text)));
        var extra = ExtraText?.Trim();
        if (!string.IsNullOrWhiteSpace(extra))
        {
            if (Selected.Count > 0) sb.Append("；");
            sb.Append("补充说明：").Append(extra);
        }
        return sb.ToString();
    }
}

/// <summary>宽度减半转换器（顶部信息栏用户气泡 MaxWidth = 所在容器宽的 50%）</summary>
public class HalfWidthConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double d ? d * 0.5 : 0d;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>按文本自然宽度给出盒子 MaxWidth（参数 "字号|字体族|余量"，余量含内边距）：
/// 内容短→收窄，内容长→超过可用宽时自然被布局截住。TextBox/FlowDocument 宽度贪婪不能自收，需外部测量。同时支持单绑与 MultiBinding（取多段文本最宽行）。</summary>
public class FitWidthConverter : System.Windows.Data.IValueConverter, System.Windows.Data.IMultiValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Measure(new[] { value as string ?? "" }, parameter as string, culture);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        return Measure(values.Select(v => v as string ?? ""), parameter as string, culture);
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    static object Measure(IEnumerable<string> texts, string? spec, CultureInfo culture)
    {
        var p = (spec ?? "14||48").Split('|');
        var size = double.Parse(p[0], CultureInfo.InvariantCulture);
        var family = p.Length > 1 && p[1].Length > 0 ? new FontFamily(p[1]) : SystemFonts.MessageFontFamily;
        var slack = p.Length > 2 && p[2].Length > 0 ? double.Parse(p[2], CultureInfo.InvariantCulture) : 48;
        var asMin = p.Length > 3 && p[3] == "min";   // min 模式：无文本时回退 0（用作 MinWidth），否则回退无穷（用作 MaxWidth）
        var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        double max = 0;
        foreach (var t in texts)
            foreach (var line in (t ?? "").Replace("\r", "").Split('\n'))
            {
                var ft = new FormattedText(line, culture, FlowDirection.LeftToRight, typeface, size, Brushes.Black, 96);
                // 渲染余量：TextFormattingMode=Display 下 TextBlock 实际字形排布宽于 FormattedText 亚像素测量
                // （汉字等全角字形每字约 1px 差），短余量不足会在行尾差几像素时被提前折行；逐字符补足后
                // 折行时机回到"确放不下才折"。数值为经验上界，宁宽勿窄（MaxWidth 只是上限，超可用宽时仍按容器折行）
                var w = ft.Width + line.Length;
                if (w > max) max = w;
            }
        return max > 0 ? max + slack : (asMin ? 0d : double.PositiveInfinity);
    }
}

/// <summary>布尔反转为 Visibility：true=Collapsed，false=Visible</summary>
public class InverseBoolToVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>布尔转透明度：true=1.0, false=0.4（无 key 选项置灰）</summary>
public class BoolToOpacityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? 1.0 : 0.4;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>警报级别转中文标签：Block=🚫 已拦截，Confirm=空（标题仅显示 Who 字段）</summary>
public class LevelLabelConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value as string ?? "Block") switch
        {
            "Block" => "🚫 已拦截",
            "Confirm" => "",
            _ => "⚠️ 安全提示",
        };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>目录树层级引导竖线水平偏移：Level→Margin（线 x=2+16L，从父行箭头中心下方垂下，每层 16px 阶梯；Level 0 顶行不画）。</summary>
public class TreeGuideMarginConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var l = value is int i ? i : System.Convert.ToInt32(value);
        return new Thickness(2 + 16 * l, 0, 0, 0);
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>目录树层级引导竖线可见性：Level 0 顶层行无父级，不画线。</summary>
public class TreeGuideVisConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int i && i > 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>手机侧续聊收件箱条目（GAIRR.Server 写 data/session_history.inbox.json，桌面 GUI 启动时合并进对应会话）：
/// GuiBaseCount = 条目创建时该会话在 GUI 文件中的消息数 —— 合并前校验桌面侧未再新增（防两段历史错位拼接）。</summary>
public class InboxEntry
{
    public string SessionId { get; set; } = "";
    public int GuiBaseCount { get; set; }
    public List<MessageRecord> Messages { get; set; } = new();
}
