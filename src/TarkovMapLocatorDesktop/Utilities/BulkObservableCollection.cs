using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace TarkovMapLocatorDesktop.Utilities;

public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public void AddRange(IEnumerable<T> items)
    {
        // Incremental Add notifications preserve WPF's recycling containers.
        // A Reset here forces every visible card to be regenerated and was the
        // main source of the short freezes while scrolling long result lists.
        foreach (var item in items) Add(item);
    }
}
