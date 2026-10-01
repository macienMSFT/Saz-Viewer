using System.Globalization;
using SazViewer.App.Mvvm;

namespace SazViewer.App.ViewModels;

/// <summary>
/// The per-pane "Search active view" toolbar: runs on every query change against the currently active view,
/// steps through matches (Enter / Shift+Enter, Previous / Next) and reports "i of N matches".
/// </summary>
internal sealed class ActiveSearchViewModel : ObservableObject
{
    private readonly Func<ISearchableView?> resolveTarget;
    private ISearchableView? target;
    private string query = "";
    private string status = "0 matches";
    private string placeholder;
    private string accessibleName;
    private int count;
    private int current;
    private bool capped;

    public ActiveSearchViewModel(Func<ISearchableView?> resolveTarget, string placeholder, string accessibleName)
    {
        this.resolveTarget = resolveTarget;
        this.placeholder = placeholder;
        this.accessibleName = accessibleName;
        PreviousCommand = new RelayCommand(() => Move(-1), () => count > 0);
        NextCommand = new RelayCommand(() => Move(1), () => count > 0);
    }

    public string Query
    {
        get => query;
        set
        {
            if (SetProperty(ref query, value ?? ""))
            {
                Run();
            }
        }
    }

    public string Status
    {
        get => status;
        private set => SetProperty(ref status, value);
    }

    public string Placeholder
    {
        get => placeholder;
        set => SetProperty(ref placeholder, value);
    }

    public string AccessibleName
    {
        get => accessibleName;
        set => SetProperty(ref accessibleName, value);
    }

    public int MatchCount => count;

    public int CurrentIndex => count == 0 ? -1 : current;

    public bool IsCapped => capped;

    public RelayCommand PreviousCommand { get; }

    public RelayCommand NextCommand { get; }

    /// <summary>Re-runs the current query against the (possibly changed) active view.</summary>
    public void Run()
    {
        Clear();
        var folded = SearchText.NormalizeQuery(query);
        if (folded.Length == 0)
        {
            return;
        }
        target = resolveTarget();
        if (target is null)
        {
            return;
        }
        var outcome = target.Search(folded);
        count = outcome.Count;
        capped = outcome.Capped;
        UpdateCommands();
        if (count > 0)
        {
            ShowCurrent();
        }
    }

    public void Move(int delta)
    {
        if (count == 0)
        {
            return;
        }
        current = ((current + delta) % count + count) % count;
        ShowCurrent();
    }

    /// <summary>Clears the query and every highlight (used when the active view changes).</summary>
    public void Reset()
    {
        if (query.Length > 0)
        {
            query = "";
            OnPropertyChanged(nameof(Query));
        }
        Clear();
    }

    private void Clear()
    {
        target?.ClearSearch();
        target = null;
        count = 0;
        current = 0;
        capped = false;
        Status = "0 matches";
        UpdateCommands();
    }

    private void ShowCurrent()
    {
        target?.ShowMatch(current);
        var total = count.ToString(CultureInfo.InvariantCulture) + (capped ? "+" : "");
        Status = $"{(current + 1).ToString(CultureInfo.InvariantCulture)} of {total} matches{(capped ? " (capped)" : "")}";
        OnPropertyChanged(nameof(CurrentIndex));
    }

    private void UpdateCommands()
    {
        PreviousCommand.RaiseCanExecuteChanged();
        NextCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(MatchCount));
        OnPropertyChanged(nameof(CurrentIndex));
    }
}
