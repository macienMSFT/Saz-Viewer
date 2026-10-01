using SazViewer.App.Model;
using SazViewer.App.Themes;
using SazViewer.Core;

namespace SazViewer.App.ViewModels;

/// <summary>
/// The WebView tab (the report's <c>createWebView</c>): Rendered / Source modes over a
/// <see cref="SafeHtmlPreview"/>. The rendered document is the sanitized, inert HTML the report produces; the
/// view hosts it in a sandboxed WebView2 (<see cref="WebPreviewPolicy"/>) only while
/// <see cref="ShouldRender"/> is true, i.e. the tab is shown in Rendered mode.
/// </summary>
internal sealed class WebPreviewViewModel : TabContentViewModel
{
    public const string IdleStatus = "Inert preview loads only when this tab is selected.";
    public const string LoadedStatus = "Inert preview rendered locally. Scripts, forms, navigation, storage, and subresources are blocked.";
    public const string BlockedStatus = "The inert preview attempted an unexpected navigation and was closed.";
    public const string SourceStatus = "Source view is active. Search and Copy use the original decoded captured source.";

    private readonly SafeHtmlPreview preview;
    private readonly Func<string> decodeSource;
    private TextDocumentViewModel? sourceDocument;
    private string? source;
    private string? sourceError;
    private bool isSourceMode;
    private bool isActive;
    private bool isBlocked;
    private string status = IdleStatus;
    private bool isWarning;

    public WebPreviewViewModel(SafeHtmlPreview preview, Func<string> decodeSource)
    {
        this.preview = preview;
        this.decodeSource = decodeSource;
    }

    public string Detection => preview.Detection;

    public bool IsSourceMode
    {
        get => isSourceMode;
        set
        {
            if (!SetProperty(ref isSourceMode, value))
            {
                return;
            }
            isBlocked = false;
            OnPropertyChanged(nameof(IsRenderedMode));
            OnPropertyChanged(nameof(SourceDocument));
            if (value)
            {
                _ = SourceDocument;
                SetStatus(sourceError is null ? SourceStatus : $"WebView source could not be loaded: {sourceError}", sourceError is not null);
            }
            else
            {
                SetStatus(IdleStatus, false);
            }
            OnPropertyChanged(nameof(ShouldRender));
            RaiseSearchTargetChanged();
        }
    }

    public bool IsRenderedMode
    {
        get => !isSourceMode;
        set => IsSourceMode = !value;
    }

    /// <summary>True while the sandboxed WebView2 should exist: tab shown, Rendered mode, not closed after a block.</summary>
    public bool ShouldRender => isActive && !isSourceMode && !isBlocked;

    public string Status
    {
        get => status;
        private set => SetProperty(ref status, value);
    }

    public bool IsWarning
    {
        get => isWarning;
        private set => SetProperty(ref isWarning, value);
    }

    /// <summary>The original decoded source (Source mode), built on first use.</summary>
    public TextDocumentViewModel? SourceDocument
    {
        get
        {
            if (!isSourceMode)
            {
                return null;
            }
            if (sourceDocument is null)
            {
                var text = Source();
                sourceDocument = new TextDocumentViewModel(
                    LineDocument.FromText(text ?? $"WebView source could not be loaded: {sourceError}", text is null ? LineKind.Warning : LineKind.Code),
                    GetCopyText);
            }
            return sourceDocument;
        }
    }

    public override ISearchableView? SearchTarget => isSourceMode ? SourceDocument?.Document : null;

    /// <summary>The sanitized document with the app's effective theme (the report's <c>webViewDocument</c>).</summary>
    public string RenderedDocument(AppTheme theme) =>
        preview.Document.Replace(
            "<html data-theme=\"system\">",
            theme == AppTheme.Dark ? "<html data-theme=\"dark\">" : "<html data-theme=\"light\">",
            StringComparison.Ordinal);

    /// <summary>Called by the view when it is shown.</summary>
    public void Activate()
    {
        if (isActive)
        {
            return;
        }
        isActive = true;
        isBlocked = false;
        if (!isSourceMode)
        {
            SetStatus(IdleStatus, false);
        }
        OnPropertyChanged(nameof(ShouldRender));
    }

    public override void Deactivate()
    {
        if (!isActive)
        {
            return;
        }
        isActive = false;
        OnPropertyChanged(nameof(ShouldRender));
    }

    public void ReportLoaded()
    {
        if (ShouldRender)
        {
            SetStatus(LoadedStatus, false);
        }
    }

    /// <summary>The preview tried to navigate after its document loaded: close it (the report's behaviour).</summary>
    public void ReportBlockedNavigation()
    {
        if (!ShouldRender)
        {
            return;
        }
        isBlocked = true;
        OnPropertyChanged(nameof(ShouldRender));
        SetStatus(BlockedStatus, true);
    }

    public void ReportFailure(string message)
    {
        if (!isSourceMode)
        {
            SetStatus($"WebView could not be loaded: {message}", true);
        }
    }

    public override CopyResult GetCopyText()
    {
        var text = Source();
        return text is null ? CopyResult.Fail($"Copy source could not be decoded. {sourceError}") : CopyResult.Bounded(text);
    }

    private string? Source()
    {
        if (source is null && sourceError is null)
        {
            try
            {
                source = decodeSource();
            }
            catch (InvalidDataException error)
            {
                sourceError = error.Message;
            }
        }
        return source;
    }

    private void SetStatus(string text, bool warning)
    {
        Status = text;
        IsWarning = warning;
    }
}
