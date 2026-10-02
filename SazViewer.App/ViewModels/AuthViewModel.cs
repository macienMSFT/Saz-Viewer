using SazViewer.App.Model;
using SazViewer.App.Mvvm;
using SazViewer.Core;

namespace SazViewer.App.ViewModels;

/// <summary>
/// The Auth tab (the report's <c>createAuthView</c>): Authorization, Proxy-Authorization, WWW-Authenticate and
/// Proxy-Authenticate headers, redacted by default. Reveal shows the full values for this view only; every
/// transition (tab, side or session change) calls <see cref="Deactivate"/>, which redacts again.
/// </summary>
internal sealed class AuthViewModel : TabContentViewModel
{
    public const string Warning = "Authentication values are redacted. Reveal only when it is safe to display captured credentials or challenge tokens.";

    private readonly HtmlReportGenerator.AuthViewData data;
    private TextDocumentViewModel redactedDocument;
    private TextDocumentViewModel? revealedDocument;
    private bool isRevealed;

    public AuthViewModel(HtmlReportGenerator.AuthViewData data)
    {
        this.data = data;
        redactedDocument = Create(data.Redacted);
        ToggleCommand = new RelayCommand(() => IsRevealed = !IsRevealed);
    }

    public RelayCommand ToggleCommand { get; }

    public bool IsRevealed
    {
        get => isRevealed;
        set
        {
            if (!SetProperty(ref isRevealed, value))
            {
                return;
            }
            // A fresh document per reveal so no search highlight or scroll state crosses the boundary.
            revealedDocument = value ? Create(data.Full) : null;
            redactedDocument = Create(data.Redacted);
            OnPropertyChanged(nameof(Document));
            OnPropertyChanged(nameof(ButtonText));
            OnPropertyChanged(nameof(ButtonAccessibleName));
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(CopyDescription));
            RaiseSearchTargetChanged();
        }
    }

    public TextDocumentViewModel Document => revealedDocument ?? redactedDocument;

    public string ButtonText => isRevealed ? "Hide values" : "Reveal values";

    public string ButtonAccessibleName => isRevealed ? "Hide full authentication header values" : "Reveal full authentication header values";

    public string Status => isRevealed ? "Full captured authentication values are visible." : "Values are redacted.";

    public override string? CopyDescription => isRevealed ? "revealed authentication headers" : "redacted authentication headers";

    public override ISearchableView SearchTarget => Document.Document;

    /// <summary>The full values only while revealed; otherwise the redacted text.</summary>
    public override CopyResult GetCopyText() => CopyResult.Of(isRevealed ? data.Full : data.Redacted);

    public override void Deactivate() => IsRevealed = false;

    private TextDocumentViewModel Create(string text) => new(LineDocument.FromText(text.TrimEnd('\n')), GetCopyText);
}
