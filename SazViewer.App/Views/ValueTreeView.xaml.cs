using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SazViewer.App.ViewModels;

namespace SazViewer.App.Views;

/// <summary>
/// Virtualized flattened tree (JSON, XML, MAPI) with boxed expanders and dotted guides. Keyboard: Up/Down,
/// Home/End, Right (expand, then first child), Left (collapse, then parent), Enter/Space toggle.
/// </summary>
internal partial class ValueTreeView : UserControl
{
    private ValueTreeViewModel? tree;

    public ValueTreeView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as ValueTreeViewModel);
        Loaded += (_, _) => Attach(DataContext as ValueTreeViewModel);
        Unloaded += (_, _) => Attach(null);
    }

    internal ListBox List => Rows;

    private void Attach(ValueTreeViewModel? next)
    {
        if (ReferenceEquals(tree, next))
        {
            return;
        }
        if (tree is not null)
        {
            tree.ScrollRequested -= OnScrollRequested;
        }
        tree = next;
        if (tree is not null)
        {
            tree.ScrollRequested += OnScrollRequested;
        }
    }

    private void OnScrollRequested(object? sender, TreeRow row)
    {
        Rows.UpdateLayout();
        Rows.ScrollIntoView(row);
    }

    private void OnExpanderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TreeRow row })
        {
            tree?.Toggle(row);
            Rows.SelectedItem = row;
            FocusRow(row);
            e.Handled = true;
        }
    }

    private void OnRowsDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(Rows, source) is ListBoxItem { DataContext: TreeRow row })
        {
            tree?.Toggle(row);
            e.Handled = true;
        }
    }

    private void OnRowsKeyDown(object sender, KeyEventArgs e)
    {
        if (tree is null || Keyboard.Modifiers != ModifierKeys.None
            || Keyboard.FocusedElement is not ListBoxItem { DataContext: TreeRow row })
        {
            return;
        }
        TreeRow? target = null;
        switch (e.Key)
        {
            case Key.Right when row.IsExpandable && !row.IsExpanded:
                tree.SetExpanded(row, true);
                target = row;
                break;
            case Key.Right when row.IsExpandable:
                target = row.Children[0];
                break;
            case Key.Left when row.IsExpandable && row.IsExpanded:
                tree.SetExpanded(row, false);
                target = row;
                break;
            case Key.Left:
                target = row.Parent;
                break;
            case Key.Enter or Key.Space when row.IsExpandable:
                tree.Toggle(row);
                target = row;
                break;
            case Key.Right or Key.Enter or Key.Space:
                break;
            default:
                return;
        }
        e.Handled = true;
        if (target is not null)
        {
            Rows.SelectedItem = target;
            FocusRow(target);
        }
    }

    private void FocusRow(TreeRow row) => Dispatcher.BeginInvoke(() =>
    {
        Rows.ScrollIntoView(row);
        Rows.UpdateLayout();
        if (Rows.ItemContainerGenerator.ContainerFromItem(row) is ListBoxItem item)
        {
            item.Focus();
        }
    });

    private void OnCanCopy(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = Rows.SelectedItems.Count > 0;
        e.Handled = true;
    }

    private void OnCopy(object sender, ExecutedRoutedEventArgs e)
    {
        e.Handled = true;
        if (tree is null)
        {
            return;
        }
        var selected = new HashSet<TreeRow>(Rows.SelectedItems.Cast<TreeRow>());
        var result = CopyResult.Bounded(ValueTreeViewModel.OutlineText(tree.VisibleRows.Where(selected.Contains)));
        if (result.Text is not null)
        {
            WpfClipboardService.Instance.TrySetText(result.Text);
        }
    }
}
