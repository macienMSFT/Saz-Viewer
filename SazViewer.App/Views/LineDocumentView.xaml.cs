using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SazViewer.App.ViewModels;

namespace SazViewer.App.Views;

/// <summary>Virtualized, selectable view of a <see cref="LineDocument"/> (Headers, Raw, Formatted Text, ...).</summary>
internal partial class LineDocumentView : UserControl
{
    public static readonly DependencyProperty AccessibleNameProperty = DependencyProperty.Register(
        nameof(AccessibleName), typeof(string), typeof(LineDocumentView), new PropertyMetadata("Content"));

    private LineDocument? document;

    public LineDocumentView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach((DataContext as TextDocumentViewModel)?.Document);
        Unloaded += (_, _) => Attach(null);
        Loaded += (_, _) => Attach((DataContext as TextDocumentViewModel)?.Document);
    }

    public string AccessibleName
    {
        get => (string)GetValue(AccessibleNameProperty);
        set => SetValue(AccessibleNameProperty, value);
    }

    internal ListBox List => Rows;

    private void Attach(LineDocument? next)
    {
        if (ReferenceEquals(document, next))
        {
            return;
        }
        if (document is not null)
        {
            document.ScrollRequested -= OnScrollRequested;
        }
        document = next;
        if (document is not null)
        {
            document.ScrollRequested += OnScrollRequested;
        }
    }

    private void OnScrollRequested(object? sender, DocumentLine row)
    {
        Rows.UpdateLayout();
        Rows.ScrollIntoView(row);
    }

    private void OnRowsMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(Rows, source) is ListBoxItem { DataContext: DocumentLine { ToggleSection: { } section } }
            && Keyboard.Modifiers == ModifierKeys.None)
        {
            document?.Toggle(section);
            e.Handled = true;
        }
    }

    private void OnRowsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space
            && Keyboard.FocusedElement is ListBoxItem { DataContext: DocumentLine { ToggleSection: { } section } line })
        {
            document?.Toggle(section);
            e.Handled = true;
            Dispatcher.BeginInvoke(() =>
            {
                if (Rows.ItemContainerGenerator.ContainerFromItem(line) is ListBoxItem item)
                {
                    item.Focus();
                }
            });
        }
    }

    private void OnCanCopy(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = Rows.SelectedItems.Count > 0;
        e.Handled = true;
    }

    private void OnCopy(object sender, ExecutedRoutedEventArgs e)
    {
        e.Handled = true;
        if (document is null)
        {
            return;
        }
        var selected = new HashSet<DocumentLine>(Rows.SelectedItems.Cast<DocumentLine>());
        var text = LineDocument.JoinRows(document.VisibleRows.Where(selected.Contains));
        var result = CopyResult.Bounded(text);
        if (result.Text is not null)
        {
            WpfClipboardService.Instance.TrySetText(result.Text);
        }
    }
}
