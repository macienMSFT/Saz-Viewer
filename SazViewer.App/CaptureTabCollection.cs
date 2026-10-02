using System.Collections.ObjectModel;

namespace SazViewer.App;

/// <summary>What <see cref="CaptureTabCollection{TTab}"/> needs from a tab.</summary>
internal interface ICaptureTab : IDisposable
{
    /// <summary>Fully qualified capture path; tabs are de-duplicated on it case-insensitively.</summary>
    string SourcePath { get; }
}

/// <summary>
/// Ordered open tabs and the active one, independent of WPF: de-duplication by path, activation,
/// Ctrl+Tab cycling, and close-with-dispose that selects the neighbor to the right (else left).
/// </summary>
internal sealed class CaptureTabCollection<TTab> where TTab : class, ICaptureTab
{
    private readonly ObservableCollection<TTab> tabs = [];

    public CaptureTabCollection()
    {
        Tabs = new ReadOnlyObservableCollection<TTab>(tabs);
    }

    public ReadOnlyObservableCollection<TTab> Tabs { get; }

    public TTab? Active { get; private set; }

    public int Count => tabs.Count;

    /// <summary>Raised whenever <see cref="Active"/> changes (including to null).</summary>
    public event EventHandler<TTab?>? ActiveChanged;

    public static string NormalizePath(string path) => Path.GetFullPath(path);

    public TTab? Find(string path)
    {
        var normalized = NormalizePath(path);
        return tabs.FirstOrDefault(tab => string.Equals(tab.SourcePath, normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Activates the existing tab for <paramref name="path"/> and returns it, or null if none is open.</summary>
    public TTab? FocusExisting(string path)
    {
        var existing = Find(path);
        if (existing is not null)
        {
            Activate(existing);
        }
        return existing;
    }

    /// <summary>Appends and activates a new tab. A tab for the same path must not already be open.</summary>
    public void Add(TTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (Find(tab.SourcePath) is not null)
        {
            throw new InvalidOperationException("A tab for this capture is already open.");
        }
        tabs.Add(tab);
        Activate(tab);
    }

    public void Activate(TTab tab)
    {
        if (!tabs.Contains(tab))
        {
            throw new ArgumentException("The tab is not open.", nameof(tab));
        }
        if (!ReferenceEquals(Active, tab))
        {
            Active = tab;
            ActiveChanged?.Invoke(this, tab);
        }
    }

    /// <summary>Moves to the next (or previous) tab, wrapping around; no-op with fewer than two tabs.</summary>
    public void Cycle(bool forward)
    {
        if (tabs.Count < 2 || Active is null)
        {
            return;
        }
        var index = tabs.IndexOf(Active);
        var next = (index + (forward ? 1 : -1) + tabs.Count) % tabs.Count;
        Activate(tabs[next]);
    }

    /// <summary>Removes and disposes <paramref name="tab"/>; returns false if it was not open.</summary>
    public bool Close(TTab tab)
    {
        var index = tabs.IndexOf(tab);
        if (index < 0)
        {
            return false;
        }
        var wasActive = ReferenceEquals(Active, tab);
        tabs.RemoveAt(index);
        if (wasActive)
        {
            Active = tabs.Count == 0 ? null : tabs[Math.Min(index, tabs.Count - 1)];
            ActiveChanged?.Invoke(this, Active);
        }
        tab.Dispose();
        return true;
    }

    public void CloseAll()
    {
        while (tabs.Count > 0)
        {
            Close(tabs[^1]);
        }
    }
}
