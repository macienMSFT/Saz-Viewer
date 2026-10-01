using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace SazViewer.App.Mvvm;

/// <summary>An <see cref="ObservableCollection{T}"/> that can replace its contents with a single Reset notification.</summary>
internal sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public BulkObservableCollection()
    {
    }

    public BulkObservableCollection(IEnumerable<T> items) : base(items)
    {
    }

    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
