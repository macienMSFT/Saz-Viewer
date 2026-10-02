using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using SazViewer.App.Mvvm;

namespace SazViewer.App.Views;

/// <summary>A text search field with a shared placeholder, clear button, and Escape-to-clear behavior.</summary>
internal sealed class SearchBox : TextBox
{
    public SearchBox()
    {
        ClearCommand = new RelayCommand(ClearSearch);
    }

    public ICommand ClearCommand { get; }

    internal void ClearSearch()
    {
        Clear();
        GetBindingExpression(TextProperty)?.UpdateSource();
        Focus();
        CaretIndex = 0;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (!e.Handled && e.Key == Key.Escape && Text.Length > 0)
        {
            ClearSearch();
            e.Handled = true;
        }
    }
}
