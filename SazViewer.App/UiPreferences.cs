using System.Text.Json;
using System.Text.Json.Serialization;

namespace SazViewer.App;

/// <summary>HTTP inspector layout; remembered separately for wide and narrow windows (like the report).</summary>
internal enum InspectorLayout
{
    Single,
    Split
}

internal enum LayoutWidthClass
{
    Wide,
    Narrow
}

internal enum InspectorLayoutMode
{
    Automatic,
    AlwaysSplit,
    AlwaysSingle
}

internal enum SessionViewerLocation
{
    BottomPane,
    RightPane,
    NewWindow
}

internal sealed record SessionColumnSetting(
    string Id,
    string Kind,
    string Source,
    string Header,
    bool Visible = true,
    double? Width = null)
{
    public const string BuiltInKind = "builtin";
    public const string RequestHeaderKind = "request-header";
    public const string ResponseHeaderKind = "response-header";
    public const string SessionFlagKind = "session-flag";

    public static SessionColumnSetting BuiltIn(string id, bool visible = true, double? width = null) =>
        new(id, BuiltInKind, id, "", visible, width);
}

/// <summary>
/// Small per-user UI preferences persisted as JSON (the native counterpart of the report's localStorage keys):
/// the explicit theme choice, inspector layouts, session-grid columns and scrub banner state. A missing or corrupt
/// file yields the defaults; write failures are ignored so a read-only profile never blocks viewing.
/// </summary>
internal sealed class UiPreferences
{
    /// <summary>Windows narrower than this use the narrow layout class (the report's 900 px breakpoint).</summary>
    public const double NarrowBreakpoint = 900;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static UiPreferences? current;
    private static readonly SessionColumnSetting[] DefaultGridColumns =
    [
        SessionColumnSetting.BuiltIn("time"),
        SessionColumnSetting.BuiltIn("id"),
        SessionColumnSetting.BuiltIn("result"),
        SessionColumnSetting.BuiltIn("method"),
        SessionColumnSetting.BuiltIn("url"),
        SessionColumnSetting.BuiltIn("elapsed"),
        SessionColumnSetting.BuiltIn("request-size"),
        SessionColumnSetting.BuiltIn("response-size")
    ];
    private readonly string? storePath;
    private Document document;

    public UiPreferences(string? storePath)
    {
        this.storePath = storePath;
        document = Load(storePath);
    }

    /// <summary>The app-wide instance (in-memory only until <see cref="Initialize"/> binds it to a file).</summary>
    public static UiPreferences Current => current ??= new UiPreferences(null);

    public static void Initialize(string storePath) => current = new UiPreferences(storePath);

    /// <summary>Restores an in-memory instance (tests).</summary>
    internal static void Reset(UiPreferences? preferences = null) => current = preferences;

    public event EventHandler? GridColumnsChanged;

    public IReadOnlyList<SessionColumnSetting> GridColumns =>
        document.GridColumns is { Count: > 0 } saved
            ? NormalizeColumns(saved)
            : DefaultGridColumns;

    public void SetGridColumns(IEnumerable<SessionColumnSetting> columns)
    {
        document.GridColumns = NormalizeColumns(columns).ToList();
        Save();
        GridColumnsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ResetGridColumns()
    {
        document.GridColumns = null;
        Save();
        GridColumnsChanged?.Invoke(this, EventArgs.Empty);
    }

    public static LayoutWidthClass WidthClassFor(double width) =>
        width < NarrowBreakpoint ? LayoutWidthClass.Narrow : LayoutWidthClass.Wide;

    public static InspectorLayout DefaultLayout(LayoutWidthClass widthClass) =>
        widthClass == LayoutWidthClass.Wide ? InspectorLayout.Split : InspectorLayout.Single;

    /// <summary>The explicit theme ("light"/"dark"), or null to follow Windows.</summary>
    public string? Theme
    {
        get => document.Theme is "light" or "dark" ? document.Theme : null;
        set
        {
            document.Theme = value is "light" or "dark" ? value : null;
            Save();
        }
    }

    public InspectorLayoutMode DefaultInspectorLayout
    {
        get => document.DefaultInspectorLayout switch
        {
            "split" => InspectorLayoutMode.AlwaysSplit,
            "single" => InspectorLayoutMode.AlwaysSingle,
            _ => InspectorLayoutMode.Automatic
        };
        set
        {
            document.DefaultInspectorLayout = value switch
            {
                InspectorLayoutMode.AlwaysSplit => "split",
                InspectorLayoutMode.AlwaysSingle => "single",
                _ => "automatic"
            };
            Save();
        }
    }

    public SessionViewerLocation SessionViewer
    {
        get => document.SessionViewer switch
        {
            "right" => SessionViewerLocation.RightPane,
            "window" => SessionViewerLocation.NewWindow,
            _ => SessionViewerLocation.BottomPane
        };
        set
        {
            document.SessionViewer = value switch
            {
                SessionViewerLocation.RightPane => "right",
                SessionViewerLocation.NewWindow => "window",
                _ => "bottom"
            };
            Save();
        }
    }

    /// <summary>Fraction of the embedded content area kept for the grid above a bottom-pane viewer.</summary>
    public double BottomPaneGridFraction
    {
        get => Fraction(document.BottomPaneGridFraction, .40);
        set
        {
            document.BottomPaneGridFraction = Fraction(value, .40);
            Save();
        }
    }

    /// <summary>Fraction of the embedded content area kept for the grid left of a right-pane viewer.</summary>
    public double RightPaneGridFraction
    {
        get => Fraction(document.RightPaneGridFraction, .45);
        set
        {
            document.RightPaneGridFraction = Fraction(value, .45);
            Save();
        }
    }

    /// <summary>Fraction of a right-pane HTTP split reserved for the Request pane above Response.</summary>
    public double RightPaneHttpSplitFraction
    {
        get => Fraction(document.RightPaneHttpSplitFraction, .50);
        set
        {
            document.RightPaneHttpSplitFraction = Fraction(value, .50);
            Save();
        }
    }

    public bool HideConnectOnOpen
    {
        get => document.HideConnectOnOpen == true;
        set
        {
            document.HideConnectOnOpen = value;
            Save();
        }
    }

    public bool SearchPayloads
    {
        get => document.SearchPayloads == true;
        set
        {
            document.SearchPayloads = value;
            Save();
        }
    }

    public bool ScrubBannerExpanded
    {
        get => document.ScrubBanner == "expanded";
        set
        {
            document.ScrubBanner = value ? "expanded" : "collapsed";
            Save();
        }
    }

    public InspectorLayout GetLayout(LayoutWidthClass widthClass) =>
        Parse(widthClass == LayoutWidthClass.Wide ? document.HttpLayoutWide : document.HttpLayoutNarrow)
        ?? DefaultLayout(widthClass);

    public InspectorLayout GetDefaultLayout(LayoutWidthClass widthClass) => DefaultInspectorLayout switch
    {
        InspectorLayoutMode.AlwaysSplit => InspectorLayout.Split,
        InspectorLayoutMode.AlwaysSingle => InspectorLayout.Single,
        _ => GetLayout(widthClass)
    };

    public void SetLayout(LayoutWidthClass widthClass, InspectorLayout layout)
    {
        var value = layout == InspectorLayout.Split ? "split" : "single";
        if (widthClass == LayoutWidthClass.Wide)
        {
            document.HttpLayoutWide = value;
        }
        else
        {
            document.HttpLayoutNarrow = value;
        }
        Save();
    }

    private static InspectorLayout? Parse(string? value) => value switch
    {
        "split" => InspectorLayout.Split,
        "single" => InspectorLayout.Single,
        _ => null
    };

    private static double Fraction(double? value, double fallback) =>
        value is { } fraction && double.IsFinite(fraction) ? Math.Clamp(fraction, .10, .90) : fallback;

    private static Document Load(string? path)
    {
        if (path is null)
        {
            return new Document();
        }
        try
        {
            if (!File.Exists(path))
            {
                return new Document();
            }
            using var stream = File.OpenRead(path);
            var loaded = JsonSerializer.Deserialize<Document>(stream) ?? new Document();
            loaded.Version = 3;
            return loaded;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Document();
        }
    }

    private void Save()
    {
        if (storePath is null)
        {
            return;
        }
        try
        {
            var directory = Path.GetDirectoryName(storePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            var temporaryPath = storePath + ".tmp";
            File.WriteAllBytes(temporaryPath, JsonSerializer.SerializeToUtf8Bytes(document, SerializerOptions));
            File.Move(temporaryPath, storePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static IReadOnlyList<SessionColumnSetting> NormalizeColumns(IEnumerable<SessionColumnSetting> columns)
    {
        var normalized = new List<SessionColumnSetting>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in columns.Take(512))
        {
            var kind = candidate.Kind switch
            {
                SessionColumnSetting.BuiltInKind => SessionColumnSetting.BuiltInKind,
                SessionColumnSetting.RequestHeaderKind => SessionColumnSetting.RequestHeaderKind,
                SessionColumnSetting.ResponseHeaderKind => SessionColumnSetting.ResponseHeaderKind,
                SessionColumnSetting.SessionFlagKind => SessionColumnSetting.SessionFlagKind,
                _ => null
            };
            var source = candidate.Source?.Trim();
            if (kind is null || string.IsNullOrEmpty(source) || source.Length > 256)
            {
                continue;
            }
            var id = candidate.Id?.Trim();
            if (string.IsNullOrEmpty(id) || id.Length > 128 || !ids.Add(id))
            {
                continue;
            }
            var header = candidate.Header?.Trim() ?? "";
            if (header.Length > 128)
            {
                header = header[..128];
            }
            var width = candidate.Width is { } value && double.IsFinite(value)
                ? (double?)Math.Clamp(value, 40, 2000)
                : null;
            normalized.Add(new SessionColumnSetting(id, kind, source, header, candidate.Visible, width));
        }
        if (normalized.Count == 0)
        {
            return DefaultGridColumns;
        }
        if (!normalized.Any(column => column.Visible))
        {
            normalized[0] = normalized[0] with { Visible = true };
        }
        return normalized;
    }

    private sealed class Document
    {
        public int Version { get; set; } = 3;

        public string? Theme { get; set; }

        public string? HttpLayoutWide { get; set; }

        public string? HttpLayoutNarrow { get; set; }

        public string? ScrubBanner { get; set; }

        public string? DefaultInspectorLayout { get; set; }

        public string? SessionViewer { get; set; }

        public bool? HideConnectOnOpen { get; set; }

        public bool? SearchPayloads { get; set; }

        public double? BottomPaneGridFraction { get; set; }

        public double? RightPaneGridFraction { get; set; }

        public double? RightPaneHttpSplitFraction { get; set; }

        public List<SessionColumnSetting>? GridColumns { get; set; }
    }
}
