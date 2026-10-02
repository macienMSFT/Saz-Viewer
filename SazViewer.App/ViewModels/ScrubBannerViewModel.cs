using System.Globalization;
using SazViewer.App.Mvvm;
using SazViewer.Core;

namespace SazViewer.App.ViewModels;

/// <summary>
/// The report's collapsible "Credentials scrubbed" banner, shown for captures opened with credential scrubbing.
/// Collapsed by default; the expanded state is remembered across captures and sessions.
/// </summary>
internal sealed class ScrubBannerViewModel : ObservableObject
{
    private readonly UiPreferences preferences;
    private bool isExpanded;

    public ScrubBannerViewModel(AuthScrubSummary summary, UiPreferences? preferences = null)
    {
        this.preferences = preferences ?? UiPreferences.Current;
        var total = summary.Total;
        Summary = $"Credentials scrubbed: {total.ToString("N0", CultureInfo.InvariantCulture)} {(total == 1 ? "replacement" : "replacements")}";
        Counts = summary.Counts.Select(item => $"{item.Key}: {item.Value.ToString("N0", CultureInfo.InvariantCulture)}").ToArray();
        isExpanded = this.preferences.ScrubBannerExpanded;
        ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    public string Summary { get; }

    public string Details => "This capture was opened with credential scrubbing (same as --scrub-auth).";

    public IReadOnlyList<string> Counts { get; }

    public RelayCommand ToggleCommand { get; }

    public bool IsExpanded
    {
        get => isExpanded;
        set
        {
            if (SetProperty(ref isExpanded, value))
            {
                preferences.ScrubBannerExpanded = value;
                OnPropertyChanged(nameof(ToggleLabel));
            }
        }
    }

    /// <summary>The Details button's accessible name, as in the report.</summary>
    public string ToggleLabel => $"{(isExpanded ? "Hide" : "Show")} credential scrub details";
}
