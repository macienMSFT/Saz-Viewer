using System.Windows.Controls;
using SazViewer.App.ViewModels;

namespace SazViewer.App.Views;

/// <summary>One side (Request or Response) of the inspector: the fixed-order per-side tabs with lazy content.</summary>
internal partial class MessagePaneView : UserControl
{
    public MessagePaneView()
    {
        InitializeComponent();
        // A recycled view gets a new pane: the TabControl may briefly select an item of the new ItemsSource on
        // its own, so re-assert the view-model's selection once bindings have settled.
        DataContextChanged += (_, _) => Dispatcher.BeginInvoke(SyncSelection);
    }

    /// <summary>Moves keyboard focus to the selected tab header.</summary>
    public void FocusSelectedTab()
    {
        if (Tabs.ItemContainerGenerator.ContainerFromItem(Tabs.SelectedItem) is TabItem item)
        {
            item.Focus();
        }
    }

    private void SyncSelection()
    {
        if (DataContext is MessagePaneViewModel pane && !ReferenceEquals(Tabs.SelectedItem, pane.SelectedTab) && pane.SelectedTab is not null)
        {
            Tabs.SelectedItem = pane.SelectedTab;
        }
    }
}
