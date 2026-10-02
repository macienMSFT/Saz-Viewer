using System.Collections.ObjectModel;
using SazViewer.App.Model;
using SazViewer.App.Mvvm;

namespace SazViewer.App.ViewModels;

internal enum FilterFieldPrompt
{
    None,
    RequestHeader,
    ResponseHeader,
    SessionFlag
}

internal sealed record FilterFieldOption(
    string Label,
    string Category,
    AdvancedFilterField? Field,
    SessionColumnType Type,
    FilterFieldPrompt Prompt = FilterFieldPrompt.None);

internal sealed record FilterOperatorOption(AdvancedFilterOperator Value, string Label);

internal sealed record FilterCountOption(int Value, string Label);

internal sealed record FilterJoinOption(AdvancedFilterJoin Value, string Label);

internal sealed class AdvancedFilterViewModel : ObservableObject, IDisposable
{
    private static readonly object LastFilterGate = new();
    private static AdvancedFilterDefinition? lastFilter;

    public static readonly IReadOnlyList<FilterCountOption> OpenParenthesisOptions =
    [
        new(0, "none"), new(1, "("), new(2, "(("), new(3, "(((")
    ];

    public static readonly IReadOnlyList<FilterCountOption> CloseParenthesisOptions =
    [
        new(0, "none"), new(1, ")"), new(2, "))"), new(3, ")))")
    ];

    public static readonly IReadOnlyList<FilterJoinOption> JoinOptions =
    [
        new(AdvancedFilterJoin.And, "AND"), new(AdvancedFilterJoin.Or, "OR")
    ];

    public static readonly IReadOnlyList<string> Methods =
    [
        "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS", "CONNECT", "TRACE", "PROPFIND"
    ];

    private readonly SessionListViewModel sessions;
    private readonly UiPreferences preferences;
    private readonly AdvancedFilterStore store;
    private CancellationTokenSource? cancellation;
    private bool isPanelOpen;
    private bool isApplying;
    private string status = "No advanced filter applied.";
    private SavedAdvancedFilter? selectedSavedFilter;
    private bool hasAppliedFilter;

    public AdvancedFilterViewModel(
        SessionListViewModel sessions,
        UiPreferences preferences,
        AdvancedFilterStore? store = null)
    {
        this.sessions = sessions;
        this.preferences = preferences;
        this.store = store ?? AdvancedFilterStore.Current;
        Rules = [];
        FieldOptions = [];
        SavedFilters = [];
        AddRuleCommand = new RelayCommand(AddRule);
        RemoveRuleCommand = new RelayCommand(parameter =>
        {
            if (parameter is FilterRuleViewModel rule)
            {
                RemoveRule(rule);
            }
        });
        ClearCommand = new RelayCommand(Clear);
        CancelCommand = new RelayCommand(Cancel, () => IsApplying);
        RestoreLastCommand = new RelayCommand(RestoreLast, () => CanRestoreLast);
        preferences.GridColumnsChanged += OnGridColumnsChanged;
        sessions.VisibleRowsChanged += OnVisibleRowsChanged;
        RefreshFieldOptions();
        RefreshSavedFilters();
        AddRule();
    }

    public ObservableCollection<FilterRuleViewModel> Rules { get; }

    public ObservableCollection<FilterFieldOption> FieldOptions { get; }

    public ObservableCollection<SavedAdvancedFilter> SavedFilters { get; }

    public RelayCommand AddRuleCommand { get; }

    public RelayCommand RemoveRuleCommand { get; }

    public RelayCommand ClearCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand RestoreLastCommand { get; }

    public bool IsPanelOpen
    {
        get => isPanelOpen;
        set => SetProperty(ref isPanelOpen, value);
    }

    public bool IsApplying
    {
        get => isApplying;
        private set
        {
            if (SetProperty(ref isApplying, value))
            {
                CancelCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasAppliedFilter
    {
        get => hasAppliedFilter;
        private set => SetProperty(ref hasAppliedFilter, value);
    }

    public string Status
    {
        get => status;
        private set => SetProperty(ref status, value);
    }

    public SavedAdvancedFilter? SelectedSavedFilter
    {
        get => selectedSavedFilter;
        set => SetProperty(ref selectedSavedFilter, value);
    }

    public bool CanRestoreLast
    {
        get
        {
            lock (LastFilterGate)
            {
                return lastFilter is not null;
            }
        }
    }

    public async Task<bool> ApplyAsync()
    {
        Cancel();
        ClearErrors();
        var definition = ToDefinition();
        var compilation = AdvancedFilterCompiler.Compile(definition);
        if (compilation.Errors.Count > 0)
        {
            ApplyErrors(compilation.Errors);
            Status = compilation.Errors.FirstOrDefault(error => error.RuleIndex < 0)?.Message
                     ?? "Fix the highlighted filter rules.";
            return false;
        }

        var current = new CancellationTokenSource();
        cancellation = current;
        IsApplying = true;
        Status = compilation.Filter!.RequiresPayload
            ? $"Filtering decoded payloads\u2026 0/{sessions.AllRows.Count:N0}"
            : $"Filtering sessions\u2026 0/{sessions.AllRows.Count:N0}";
        try
        {
            var progress = new Progress<PayloadSearchProgress>(value =>
            {
                if (ReferenceEquals(cancellation, current))
                {
                    Status = $"{(compilation.Filter.RequiresPayload ? "Filtering decoded payloads" : "Filtering sessions")}\u2026 "
                             + $"{value.Completed:N0}/{value.Total:N0}";
                }
            });
            var result = await AdvancedFilterEvaluator.EvaluateAsync(
                compilation.Filter,
                sessions.AllRows,
                sessions.PayloadCache,
                progress,
                current.Token);
            if (!ReferenceEquals(cancellation, current))
            {
                return false;
            }
            sessions.SetAdvancedFilterMatches(result.Matches);
            HasAppliedFilter = true;
            lock (LastFilterGate)
            {
                lastFilter = Clone(definition);
            }
            OnPropertyChanged(nameof(CanRestoreLast));
            RestoreLastCommand.RaiseCanExecuteChanged();
            UpdateShowingStatus();
            return true;
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception) when (FindRuntimeException(exception) is not null)
        {
            var runtime = FindRuntimeException(exception)!;
            if (runtime.RuleIndex >= 0 && runtime.RuleIndex < Rules.Count)
            {
                Rules[runtime.RuleIndex].Error = runtime.Message;
            }
            Status = "Filter evaluation stopped because a rule exceeded its safety limit.";
            return false;
        }
        finally
        {
            if (ReferenceEquals(cancellation, current))
            {
                cancellation = null;
                IsApplying = false;
            }
            current.Dispose();
        }
    }

    public void Clear()
    {
        Cancel();
        sessions.ClearAdvancedFilter();
        HasAppliedFilter = false;
        Rules.Clear();
        AddRule();
        Status = "No advanced filter applied.";
    }

    public void Cancel()
    {
        var wasApplying = IsApplying;
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;
        IsApplying = false;
        if (wasApplying)
        {
            Status = "Advanced filter canceled.";
        }
    }

    public void SaveAs(string name)
    {
        var definition = ToDefinition();
        ClearErrors();
        var compilation = AdvancedFilterCompiler.Compile(definition);
        if (compilation.Errors.Count > 0)
        {
            ApplyErrors(compilation.Errors);
            Status = compilation.Errors.FirstOrDefault(error => error.RuleIndex < 0)?.Message
                     ?? "Fix the highlighted rules before saving.";
            return;
        }
        store.SaveAs(name, definition);
        RefreshSavedFilters();
        SelectedSavedFilter = SavedFilters.First(item => item.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        Status = $"Saved filter \u201c{SelectedSavedFilter.Name}\u201d.";
    }

    public void LoadSelected()
    {
        if (SelectedSavedFilter is null)
        {
            Status = "Choose a saved filter to load.";
            return;
        }
        Load(SelectedSavedFilter.Definition);
        Status = $"Loaded filter \u201c{SelectedSavedFilter.Name}\u201d. Select Apply to run it.";
    }

    public void DeleteSelected()
    {
        if (SelectedSavedFilter is null)
        {
            return;
        }
        var name = SelectedSavedFilter.Name;
        store.Delete(name);
        RefreshSavedFilters();
        Status = $"Deleted filter \u201c{name}\u201d.";
    }

    public int Import(string path)
    {
        var count = store.Import(path);
        RefreshSavedFilters();
        Status = $"Imported {count:N0} named filter{(count == 1 ? "" : "s")}.";
        return count;
    }

    public void ExportSelected(string path)
    {
        if (SelectedSavedFilter is null)
        {
            throw new InvalidOperationException("Choose a saved filter to export.");
        }
        store.Export(path, SelectedSavedFilter);
        Status = $"Exported filter \u201c{SelectedSavedFilter.Name}\u201d.";
    }

    public FilterFieldOption AddCustomField(string kind, string source, string? header)
    {
        var setting = SessionColumnCatalog.CreateCustom(kind, source, header);
        var definition = SessionColumnCatalog.Resolve(setting)
                         ?? throw new InvalidOperationException("The custom field is invalid.");
        var option = new FilterFieldOption(definition.Header, definition.Category, AdvancedFilterField.ForColumn(setting), definition.Type);
        var insert = Math.Max(0, FieldOptions.Count - 5);
        FieldOptions.Insert(insert, option);
        return option;
    }

    private void RestoreLast()
    {
        AdvancedFilterDefinition? saved;
        lock (LastFilterGate)
        {
            saved = lastFilter is null ? null : Clone(lastFilter);
        }
        if (saved is not null)
        {
            Load(saved);
            Status = "Restored the last filter used in this app session. Select Apply to run it.";
        }
    }

    private void AddRule()
    {
        var defaultField = FieldOptions.First(option => option.Field?.Column?.Source == "url");
        Rules.Add(new FilterRuleViewModel(this, defaultField));
    }

    private void RemoveRule(FilterRuleViewModel rule)
    {
        if (Rules.Count == 1)
        {
            Rules[0].Reset();
            return;
        }
        Rules.Remove(rule);
    }

    private AdvancedFilterDefinition ToDefinition() => new(Rules.Select(rule => rule.ToModel()).ToArray());

    private void Load(AdvancedFilterDefinition definition)
    {
        Cancel();
        sessions.ClearAdvancedFilter();
        HasAppliedFilter = false;
        Rules.Clear();
        foreach (var rule in definition.Rules)
        {
            var option = FindOrAddField(rule.Field);
            Rules.Add(new FilterRuleViewModel(this, option, rule));
        }
        if (Rules.Count == 0)
        {
            AddRule();
        }
        IsPanelOpen = true;
    }

    private FilterFieldOption FindOrAddField(AdvancedFilterField field)
    {
        var found = FieldOptions.FirstOrDefault(option => option.Field == field);
        if (found is not null)
        {
            return found;
        }
        if (field.Kind == AdvancedFilterFieldKind.Column && field.Column is { } setting
            && SessionColumnCatalog.Resolve(setting) is { } definition)
        {
            var added = new FilterFieldOption(
                definition.Header,
                definition.Category,
                field,
                definition.Type);
            FieldOptions.Insert(Math.Max(0, FieldOptions.Count - 5), added);
            return added;
        }
        return field.Kind == AdvancedFilterFieldKind.RequestBody
            ? FieldOptions.First(option => option.Field?.Kind == AdvancedFilterFieldKind.RequestBody)
            : FieldOptions.First(option => option.Field?.Kind == AdvancedFilterFieldKind.ResponseBody);
    }

    private void RefreshFieldOptions()
    {
        var selected = Rules.Select(rule => rule.FieldOption.Field).ToArray();
        FieldOptions.Clear();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in SessionColumnCatalog.BuiltIns)
        {
            ids.Add(definition.Id);
            FieldOptions.Add(new FilterFieldOption(
                definition.Header,
                definition.Category,
                AdvancedFilterField.ForColumn(definition.Setting),
                definition.Type));
        }
        foreach (var setting in preferences.GridColumns)
        {
            if (setting.Kind == SessionColumnSetting.BuiltInKind || !ids.Add(setting.Id)
                || SessionColumnCatalog.Resolve(setting) is not { } definition)
            {
                continue;
            }
            FieldOptions.Add(new FilterFieldOption(
                definition.Header,
                definition.Category,
                AdvancedFilterField.ForColumn(setting),
                definition.Type));
        }
        FieldOptions.Add(new("Request body", "Payload", AdvancedFilterField.RequestBody, SessionColumnType.Text));
        FieldOptions.Add(new("Response body", "Payload", AdvancedFilterField.ResponseBody, SessionColumnType.Text));
        FieldOptions.Add(new("Request header\u2026", "Custom", null, SessionColumnType.Text, FilterFieldPrompt.RequestHeader));
        FieldOptions.Add(new("Response header\u2026", "Custom", null, SessionColumnType.Text, FilterFieldPrompt.ResponseHeader));
        FieldOptions.Add(new("Session flag\u2026", "Custom", null, SessionColumnType.Text, FilterFieldPrompt.SessionFlag));
        for (var index = 0; index < Rules.Count; index++)
        {
            if (selected[index] is { } field)
            {
                Rules[index].FieldOption = FindOrAddField(field);
            }
        }
    }

    private void RefreshSavedFilters()
    {
        var selectedName = SelectedSavedFilter?.Name;
        SavedFilters.Clear();
        foreach (var filter in store.Filters)
        {
            SavedFilters.Add(filter);
        }
        SelectedSavedFilter = selectedName is null
            ? null
            : SavedFilters.FirstOrDefault(item => item.Name.Equals(selectedName, StringComparison.OrdinalIgnoreCase));
    }

    private void ApplyErrors(IReadOnlyList<AdvancedFilterValidationError> errors)
    {
        foreach (var error in errors)
        {
            if (error.RuleIndex >= 0 && error.RuleIndex < Rules.Count)
            {
                Rules[error.RuleIndex].Error = error.Message;
            }
            else
            {
                Status = error.Message;
            }
        }
    }

    private void ClearErrors()
    {
        foreach (var rule in Rules)
        {
            rule.Error = null;
        }
    }

    private void OnVisibleRowsChanged(object? sender, EventArgs e)
    {
        if (HasAppliedFilter && !IsApplying)
        {
            UpdateShowingStatus();
        }
    }

    private void UpdateShowingStatus() =>
        Status = $"Showing {sessions.VisibleRows.Count:N0} of {sessions.AllRows.Count:N0} sessions.";

    private void OnGridColumnsChanged(object? sender, EventArgs e) => RefreshFieldOptions();

    public void Dispose()
    {
        Cancel();
        preferences.GridColumnsChanged -= OnGridColumnsChanged;
        sessions.VisibleRowsChanged -= OnVisibleRowsChanged;
    }

    private static AdvancedFilterDefinition Clone(AdvancedFilterDefinition definition) =>
        new(definition.Rules.Select(rule => rule with
        {
            Field = rule.Field with
            {
                Column = rule.Field.Column is null ? null : rule.Field.Column with { }
            }
        }).ToArray());

    private static AdvancedFilterRuntimeException? FindRuntimeException(Exception exception)
    {
        if (exception is AdvancedFilterRuntimeException runtime)
        {
            return runtime;
        }
        if (exception is AggregateException aggregate)
        {
            return aggregate.Flatten().InnerExceptions.Select(FindRuntimeException).FirstOrDefault(found => found is not null);
        }
        return exception.InnerException is null ? null : FindRuntimeException(exception.InnerException);
    }
}

internal sealed class FilterRuleViewModel : ObservableObject
{
    private readonly AdvancedFilterViewModel owner;
    private FilterFieldOption fieldOption;
    private FilterFieldOption lastConcreteField;
    private bool enabled = true;
    private int openParentheses;
    private int closeParentheses;
    private AdvancedFilterOperator filterOperator = AdvancedFilterOperator.Contains;
    private string value = "";
    private AdvancedFilterJoin join = AdvancedFilterJoin.And;
    private bool caseSensitive;
    private string? error;

    public FilterRuleViewModel(
        AdvancedFilterViewModel owner,
        FilterFieldOption defaultField,
        AdvancedFilterRule? model = null)
    {
        this.owner = owner;
        fieldOption = defaultField;
        lastConcreteField = defaultField;
        if (model is not null)
        {
            enabled = model.Enabled;
            openParentheses = model.OpenParentheses;
            closeParentheses = model.CloseParentheses;
            filterOperator = model.Operator;
            value = model.Value;
            join = model.Join;
            caseSensitive = model.CaseSensitive;
        }
        UpdateOperators();
    }

    public bool Enabled
    {
        get => enabled;
        set => SetProperty(ref enabled, value);
    }

    public int OpenParentheses
    {
        get => openParentheses;
        set => SetProperty(ref openParentheses, value);
    }

    public FilterFieldOption FieldOption
    {
        get => fieldOption;
        set
        {
            if (!SetProperty(ref fieldOption, value))
            {
                return;
            }
            if (value.Prompt == FilterFieldPrompt.None)
            {
                lastConcreteField = value;
                UpdateOperators();
            }
        }
    }

    public ObservableCollection<FilterOperatorOption> Operators { get; } = [];

    public AdvancedFilterOperator Operator
    {
        get => filterOperator;
        set
        {
            if (SetProperty(ref filterOperator, value))
            {
                OnPropertyChanged(nameof(HasValueEditor));
                OnPropertyChanged(nameof(ValueHint));
            }
        }
    }

    public string Value
    {
        get => value;
        set => SetProperty(ref this.value, value ?? "");
    }

    public int CloseParentheses
    {
        get => closeParentheses;
        set => SetProperty(ref closeParentheses, value);
    }

    public AdvancedFilterJoin Join
    {
        get => join;
        set => SetProperty(ref join, value);
    }

    public bool CaseSensitive
    {
        get => caseSensitive;
        set => SetProperty(ref caseSensitive, value);
    }

    public string? Error
    {
        get => error;
        set => SetProperty(ref error, value);
    }

    public bool IsMethod => FieldOption.Type == SessionColumnType.Method;

    public bool SupportsCaseSensitivity =>
        FieldOption.Type is SessionColumnType.Text or SessionColumnType.Method;

    public bool HasValueEditor =>
        Operator is not (AdvancedFilterOperator.IsEmpty or AdvancedFilterOperator.IsNotEmpty);

    public string ValueHint => Operator switch
    {
        AdvancedFilterOperator.Between when FieldOption.Type == SessionColumnType.Status => "e.g. 200-299",
        AdvancedFilterOperator.Between when FieldOption.Type == SessionColumnType.Size => "e.g. 1 KB..2 MB",
        AdvancedFilterOperator.Between => "start..end",
        AdvancedFilterOperator.IsOneOf or AdvancedFilterOperator.IsNotOneOf => "comma or newline list",
        AdvancedFilterOperator.MatchesRegex => ".NET regex (100 ms limit)",
        _ when FieldOption.Type == SessionColumnType.Size => "bytes, KB, MB, GB",
        _ when FieldOption.Type == SessionColumnType.Duration => "ms, s, min, h",
        _ when FieldOption.Type == SessionColumnType.Timestamp => "date and time",
        _ => "value"
    };

    public AdvancedFilterRule ToModel() =>
        new(
            Enabled,
            OpenParentheses,
            FieldOption.Field ?? lastConcreteField.Field!,
            Operator,
            Value,
            CloseParentheses,
            Join,
            CaseSensitive);

    public void CancelFieldPrompt() => FieldOption = lastConcreteField;

    public void Reset()
    {
        Enabled = true;
        OpenParentheses = 0;
        CloseParentheses = 0;
        Operator = AdvancedFilterOperator.Contains;
        Value = "";
        Join = AdvancedFilterJoin.And;
        CaseSensitive = false;
        Error = null;
    }

    private void UpdateOperators()
    {
        var allowed = AdvancedFilterCompiler.OperatorsFor(FieldOption.Type);
        Operators.Clear();
        foreach (var item in allowed)
        {
            Operators.Add(new FilterOperatorOption(item, AdvancedFilterCompiler.Display(item)));
        }
        if (!allowed.Contains(Operator))
        {
            Operator = AdvancedFilterOperator.Equals;
        }
        if (!SupportsCaseSensitivity)
        {
            CaseSensitive = false;
        }
        OnPropertyChanged(nameof(IsMethod));
        OnPropertyChanged(nameof(SupportsCaseSensitivity));
        OnPropertyChanged(nameof(HasValueEditor));
        OnPropertyChanged(nameof(ValueHint));
    }
}
