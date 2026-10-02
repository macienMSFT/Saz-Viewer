using System.Windows.Controls;
using System.Windows.Input;
using SazViewer.App.ViewModels;

namespace SazViewer.App.Views;

/// <summary>"Search active view" toolbar: Enter / Shift+Enter step through matches, Escape clears.</summary>
internal partial class ActiveSearchBar : UserControl
{
    public ActiveSearchBar()
    {
        InitializeComponent();
    }

    public void FocusQuery()
    {
        QueryBox.Focus();
        QueryBox.SelectAll();
    }

    private void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not ActiveSearchViewModel search)
        {
            return;
        }
        if (e.Key == Key.Enter)
        {
            // Flush the delayed binding so Enter right after typing searches the typed text first.
            var pending = QueryBox.GetBindingExpression(TextBox.TextProperty);
            if (search.Query != QueryBox.Text)
            {
                pending?.UpdateSource();
            }
            else
            {
                search.Move(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
            }
            e.Handled = true;
        }
    }
}
