using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace GAIRR;

/// <summary>自动任务编辑弹窗：负责标题/提示词/周期/时间录入与校验；ESC 默认取消关闭。</summary>
public partial class AutoTaskDialog : Window
{
    public AutoTaskViewModel Result { get; private set; } = new();

    public AutoTaskDialog(AutoTaskViewModel? init = null)
    {
        InitializeComponent();
        if (init != null)
        {
            tbTitle.Text = init.Title;
            tbPrompt.Text = init.Prompt;
            tbExecPrompt.Text = init.ExecutionPrompt;   // 执行步骤提示词（首次成功后自动总结生成）
            tbHour.Text = init.Hour.ToString("D2");
            tbMinute.Text = init.Minute.ToString("D2");
            tbMonthDay.Text = init.MonthDay.ToString();
            tbCron.Text = init.CronExpression;
            cbType.SelectedIndex = (int)init.Type;
            cbWeekDay.SelectedIndex = Math.Clamp(init.WeekDay - 1, 0, 6);
        }
        else
        {
            cbType.SelectedIndex = 1; // 默认工作日
        }
        LoadFlowModes(init?.FlowMode ?? "");
    }

    /// <summary>填充工作模式下拉：首项"自主模式"，其后为工具级 flows/ 下的 Flow 模板；恢复已有选择。</summary>
    void LoadFlowModes(string current)
    {
        try
        {
            foreach (var t in GAIRR.AgentHost.FlowTemplateStore.LoadAll())
                cbFlowMode.Items.Add(new ComboBoxItem
                {
                    Content = string.IsNullOrWhiteSpace(t.DisplayName) ? t.Name : t.DisplayName,
                    Tag = t.Name,
                    ToolTip = string.IsNullOrWhiteSpace(t.Description) ? t.Name : t.Description
                });
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Flow] 模板加载失败: {ex.Message}"); }
        var match = cbFlowMode.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals(i.Tag as string, current, StringComparison.OrdinalIgnoreCase));
        cbFlowMode.SelectedIndex = match == null ? 0 : cbFlowMode.Items.IndexOf(match);
    }

    void OnTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        panelWeek.Visibility = Visibility.Collapsed;
        panelMonth.Visibility = Visibility.Collapsed;
        panelCron.Visibility = Visibility.Collapsed;
        var type = (TaskScheduleType)cbType.SelectedIndex;
        switch (type)
        {
            case TaskScheduleType.Weekly: panelWeek.Visibility = Visibility.Visible; break;
            case TaskScheduleType.Monthly: panelMonth.Visibility = Visibility.Visible; break;
            case TaskScheduleType.Cron: panelCron.Visibility = Visibility.Visible; break;
        }
    }

    void OnSave(object sender, RoutedEventArgs e)
    {
        var title = tbTitle.Text.Trim();
        if (title.Length == 0)
        {
            MessageBox.Show("请输入任务名称", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!int.TryParse(tbHour.Text, out var hour) || hour < 0 || hour > 23)
        {
            MessageBox.Show("小时请输入 0-23 之间的整数", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!int.TryParse(tbMinute.Text, out var minute) || minute < 0 || minute > 59)
        {
            MessageBox.Show("分钟请输入 0-59 之间的整数", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var type = (TaskScheduleType)cbType.SelectedIndex;
        var weekDay = cbWeekDay.SelectedIndex + 1; // 界面索引 0=周一，模型值 1-7
        if (!int.TryParse(tbMonthDay.Text, out var monthDay) || monthDay < 1 || monthDay > 31)
            monthDay = 1;

        var cronExpr = tbCron.Text.Trim();
        if (type == TaskScheduleType.Cron)
        {
            if (string.IsNullOrWhiteSpace(cronExpr))
            {
                MessageBox.Show("请输入 Cron 表达式，格式：分 时 日 月 周", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (GAIRR.Core.Cron.CronExpression.TryParse(cronExpr) == null)
            {
                MessageBox.Show("Cron 表达式格式不正确，示例：0 9 * * 1-5", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }

        Result = new AutoTaskViewModel
        {
            Title = title,
            Prompt = tbPrompt.Text.Trim(),
            ExecutionPrompt = tbExecPrompt.Text.Trim(),   // 任务执行步骤提示词（可手动编辑）
            FlowMode = (cbFlowMode.SelectedItem as ComboBoxItem)?.Tag as string ?? "",   // 工作模式：Flow 模板名，空=自主
            Type = type,
            Hour = hour,
            Minute = minute,
            WeekDay = weekDay,
            MonthDay = monthDay,
            CronExpression = cronExpr,
        };
        DialogResult = true;
        Close();
    }

    void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    /// <summary>窗口键盘事件：ESC 默认取消关闭（不保存）。</summary>
    void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            OnCancel(this, new RoutedEventArgs());
        }
    }
}
