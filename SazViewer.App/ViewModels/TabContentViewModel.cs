using SazViewer.App.Mvvm;

namespace SazViewer.App.ViewModels;

/// <summary>Base for the content of one per-side tab (Headers, Raw, JSON, ...). Created lazily on first display.</summary>
internal abstract class TabContentViewModel : ObservableObject
{
    /// <summary>What the active-view search runs against, or null when the view isn't searchable.</summary>
    public virtual ISearchableView? SearchTarget => null;

    public abstract CopyResult GetCopyText();

    /// <summary>Called when the tab stops being shown (release resources, reset reveal state, ...).</summary>
    public virtual void Deactivate()
    {
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
