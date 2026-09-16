// 会话改动文件数据层：左栏「本会话改动」卡片 + 每轮回复「本轮改动」条带共用一套模型与聚合逻辑。
// 数据源 = Agent 侧 ChangeJournal 的时间窗聚合（含会话归属），GUI 侧转相对项目根路径、按文件去重后合并进可观察集合。
// 持久化 = 每轮收口时把行 VM 转 FileChangeRecord 挂到 MessageRecord.Changes，随 session_history.json 落盘；
//          重开会话时由记录还原（存量历史无该字段 → 卡片为空，属预期）。
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using GAIRR.Core;

namespace GAIRR;

/// <summary>单轮改动文件的持久化记录（MessageRecord.Changes 数组元素）</summary>
public class FileChangeRecord
{
    /// <summary>相对项目根路径（正斜杠）</summary>
    [JsonPropertyName("path")] public string Path { get; set; } = "";

    /// <summary>产生改动的工具名（Write/Edit/…）</summary>
    [JsonPropertyName("tool")] public string Tool { get; set; } = "";

    /// <summary>true=本轮新建的文件（写前无备份）</summary>
    [JsonPropertyName("new")] public bool IsNew { get; set; }

    /// <summary>最后一次改动时刻（HH:mm）</summary>
    [JsonPropertyName("time")] public string Time { get; set; } = "";

    /// <summary>一句话改动说明（模型写文件时必填的 summary，悬停可见）</summary>
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";

    /// <summary>改前基线文件绝对路径（back/ 写前备份）：点击行做改动对比着色用；空=无基线（本轮新建或备份已清理）</summary>
    [JsonPropertyName("base")] public string Base { get; set; } = "";
}

/// <summary>改动文件行 VM（卡片/条带列表项）：状态标记 + 文件名 + 相对目录 + 工具 + 时间</summary>
public class ChangedFileVm : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>相对项目根路径（正斜杠）：列表内去重键 + 悬停完整路径展示</summary>
    public string RelPath { get; set; } = "";

    /// <summary>绝对路径：点击行打开代码查看器定位用</summary>
    public string FullPath { get; set; } = "";

    /// <summary>true=新建文件（显示 + 绿色），false=修改已有文件（显示 ~ 琥珀色）</summary>
    public bool IsNew { get; set; }

    /// <summary>产生改动的工具名（Write/Edit/…）</summary>
    public string Tool { get; set; } = "";

    /// <summary>最后一次改动时刻文本（HH:mm）</summary>
    public string TimeText { get; set; } = "";

    /// <summary>一句话改动说明（悬停提示正文）</summary>
    public string Summary { get; set; } = "";

    /// <summary>改前基线文件绝对路径（back/ 写前备份）：点击行做改动对比着色用；空=无基线（新建或备份已清理）</summary>
    public string BasePath { get; set; } = "";

    /// <summary>行内主标题 = 文件名（不带目录，窄栏不挤）</summary>
    public string FileName => RelPath.Length == 0 ? "(未知文件)" : RelPath[(RelPath.LastIndexOf('/') + 1)..];

    /// <summary>行内副标题 = 相对目录（无目录时显示项目根标记）</summary>
    public string DirText
    {
        get
        {
            var i = RelPath.LastIndexOf('/');
            return i <= 0 ? "·" : RelPath[..i];
        }
    }

    /// <summary>状态标记字符：+ 新建 / ~ 修改</summary>
    public string StatusIcon => IsNew ? "+" : "~";

    /// <summary>悬停提示：相对路径 + 工具 + 时间 + 改动说明</summary>
    public string Tip => RelPath + (Tool.Length > 0 ? $"  ·  {Tool} {TimeText}" : "") +
                         (Summary.Length > 0 ? "\n" + Summary : "");

    /// <summary>刷新全部派生显示属性（集合内就地更新行内容时调用）</summary>
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
}

/// <summary>改动文件聚合工具：变更日志条目/持久化记录 → 行 VM，并把结果增量合并进可观察集合</summary>
public static class SessionChanges
{
    /// <summary>绝对路径 → 相对项目根（正斜杠）；不在项目根下时原样返回全路径</summary>
    public static string ToRel(string fullPath, string projectRoot)
    {
        var f = (fullPath ?? "").Replace('\\', '/');
        var root = (projectRoot ?? "").Replace('\\', '/').TrimEnd('/');
        if (root.Length > 0 && f.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)) return f[(root.Length + 1)..];
        return f;
    }

    /// <summary>变更日志条目 → 行 VM</summary>
    public static ChangedFileVm ToVm(ChangeEntry e, string projectRoot)
    {
        var rel = ToRel(e.File, projectRoot);
        return new ChangedFileVm
        {
            RelPath = rel,
            FullPath = e.File,
            IsNew = e.IsNew,
            Tool = e.Tool,
            TimeText = e.Time.ToString("HH:mm"),
            Summary = e.Summary ?? "",
            BasePath = e.Backup ?? "",
        };
    }

    /// <summary>持久化记录 → 行 VM（历史会话回放；无绝对路径，点击时按当前项目根重算）</summary>
    public static ChangedFileVm ToVm(FileChangeRecord r, string projectRoot)
    {
        var rel = (r.Path ?? "").Replace('\\', '/');
        return new ChangedFileVm
        {
            RelPath = rel,
            FullPath = rel.Length == 0 ? "" : Path.Combine(projectRoot ?? "", rel.Replace('/', Path.DirectorySeparatorChar)),
            IsNew = r.IsNew,
            Tool = r.Tool ?? "",
            TimeText = r.Time ?? "",
            Summary = r.Summary ?? "",
            BasePath = r.Base ?? "",
        };
    }

    /// <summary>行 VM 列表 → 持久化记录（收口落盘用）</summary>
    public static List<FileChangeRecord> ToRecords(IEnumerable<ChangedFileVm> items) =>
        items.Select(v => new FileChangeRecord
        {
            Path = v.RelPath,
            Tool = v.Tool,
            IsNew = v.IsNew,
            Time = v.TimeText,
            Summary = v.Summary,
            Base = v.BasePath,
        }).ToList();

    /// <summary>
    /// 增量合并进可观察集合：同路径命中 → 就地更新行内容（不重建行，UI 不闪）；未命中 → 追加新行。
    /// 返回是否发生变化（调用方据此决定是否刷新计数/可见性）。
    /// </summary>
    public static bool Merge(ObservableCollection<ChangedFileVm> list, IEnumerable<ChangedFileVm> items)
    {
        var changed = false;
        foreach (var it in items)
        {
            if (it.RelPath.Length == 0) continue;
            var hit = list.FirstOrDefault(x => string.Equals(x.RelPath, it.RelPath, StringComparison.OrdinalIgnoreCase));
            if (hit == null) { list.Add(it); changed = true; continue; }
            // 基线只认最早一次（会话起点状态）：后续同名文件的备份是「上一次改完」的版本，不能当改前基线
            if (hit.BasePath.Length == 0 && it.BasePath.Length > 0) hit.BasePath = it.BasePath;
            // 已有行：仅当内容确有变化时更新（避免每次事件都触发全量重绘）
            if (hit.IsNew == it.IsNew && hit.Tool == it.Tool && hit.TimeText == it.TimeText && hit.Summary == it.Summary) continue;
            hit.IsNew = it.IsNew;
            hit.Tool = it.Tool;
            hit.TimeText = it.TimeText;
            hit.Summary = it.Summary;
            hit.FullPath = it.FullPath;
            hit.Refresh();
            changed = true;
        }
        return changed;
    }

    /// <summary>从会话历史消息还原累计改动清单（按消息先后合并，后写的覆盖同名文件的旧状态）</summary>
    public static List<ChangedFileVm> FromMessages(IEnumerable<MessageRecord>? messages, string projectRoot)
    {
        var list = new ObservableCollection<ChangedFileVm>();
        if (messages == null) return list.ToList();
        foreach (var m in messages)
        {
            if (m.Changes == null) continue;
            Merge(list, m.Changes.Select(r => ToVm(r, projectRoot)));
        }
        return list.ToList();
    }
}
