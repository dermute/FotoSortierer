using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace FotoSortierer.App;

internal sealed class PreviewRows : ObservableCollection<PreviewRow>
{
    public void ReplaceAll(IReadOnlyList<PreviewRow> replacement)
    {
        CheckReentrancy();
        // Publish one reset after all items are ready. Deferring the bound view's
        // refresh while changing its source prevents the DataGrid from reading it.
        Items.Clear();
        foreach (var row in replacement) Items.Add(row);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
