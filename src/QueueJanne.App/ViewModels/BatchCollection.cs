using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace QueueJanne.App.ViewModels;

/// <summary>
/// An observable collection that can add many items with one notification. The grid's collection view handles a
/// Reset with one refresh, where per-item Add notifications cost one filter, sort and group pass each.
/// </summary>
public sealed class BatchCollection<T> : ObservableCollection<T>
{
    public void AddRange(IReadOnlyCollection<T> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        CheckReentrancy();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        RaiseReset();
    }

    /// <summary>Removes matching items: one by one when there are few, so the grid keeps its scroll position, else with one Reset.</summary>
    public void RemoveWhere(Func<T, bool> match)
    {
        var indexes = Items.Select((item, index) => (item, index)).Where(p => match(p.item)).Select(p => p.index).ToList();
        if (indexes.Count <= 50)
        {
            for (var i = indexes.Count - 1; i >= 0; i--)
            {
                RemoveAt(indexes[i]);
            }

            return;
        }

        CheckReentrancy();
        var keep = Items.Where(item => !match(item)).ToList();
        Items.Clear();
        foreach (var item in keep)
        {
            Items.Add(item);
        }

        RaiseReset();
    }

    private void RaiseReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
