using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace GAIRR;

/// <summary>
/// 支持 AddRange / ReplaceRange 的 ObservableCollection。
/// 批量添加时只触发一次 CollectionChanged（Reset 或 自定义范围事件），
/// 避免 ItemsControl 逐条生成容器导致长列表 UI 卡顿。
/// </summary>
public class RangeObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>批量添加元素，完成后只触发一次 Reset 事件。</summary>
    public void AddRange(IEnumerable<T> items)
    {
        CheckReentrancy();
        foreach (var item in items)
            Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>清空集合并替换为新的元素列表，完成后只触发一次 Reset 事件。</summary>
    public void ReplaceRange(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items)
            Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
