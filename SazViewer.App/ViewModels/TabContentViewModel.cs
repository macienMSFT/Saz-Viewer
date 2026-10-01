using SazViewer.App.Mvvm;

namespace SazViewer.App.ViewModels;

/// <summary>Base for the content of one per-side tab (Headers, Raw, JSON, ...). Created lazily on first display.</summary>
internal abstract class TabContentViewModel : ObservableObject
{
    /// <summary>What the active-view search runs against, or null when the view isn't searchable.</summary>
    public virtual ISearchableView? SearchTarget => null;

    /// <summary>View-specific toolbar buttons shown before Copy (e.g. Expand all / Collapse all).</summary>
    public virtual IReadOnlyList<ToolbarAction> ToolbarActions => [];

    /// <summary>Raised when the view switches what it shows (e.g. Tree to Formatted Text); active searches reset.</summary>
    public event EventHandler? SearchTargetChanged;

    public abstract CopyResult GetCopyText();

    /// <summary>Called when the tab stops being shown (release resources, reset reveal state, ...).</summary>
    public virtual void Deactivate()
    {
    }

    protected void RaiseSearchTargetChanged() => SearchTargetChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>A toolbar button contributed by a tab's content view.</summary>
internal sealed class ToolbarAction(string label, string accessibleName, Action execute) : ObservableObject
{
    private bool isVisible = true;

    public string Label { get; } = label;

    public string AccessibleName { get; } = accessibleName;

    public RelayCommand Command { get; } = new(execute);

    public bool IsVisible
    {
        get => isVisible;
        set => SetProperty(ref isVisible, value);
    }
}

/// <summary>A read-only text view backed by a virtualized <see cref="LineDocument"/>.</summary>
internal sealed class TextDocumentViewModel(LineDocument document, Func<CopyResult> copy) : TabContentViewModel
{
    public LineDocument Document { get; } = document;

    public override ISearchableView SearchTarget => Document;

    public override CopyResult GetCopyText() => copy();
}

/// <summary>Placeholder shown while a view is not yet implemented natively.</summary>
internal sealed class PlaceholderViewModel(string message) : TabContentViewModel
{
    public string Message { get; } = message;

    public override CopyResult GetCopyText() => CopyResult.Fail("Copy source is unavailable.");
}
