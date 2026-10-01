using System.Text.Json;

namespace SazViewer.App;

/// <summary>
/// Most-recently-used capture paths persisted as JSON. Only full paths are stored, never
/// passwords or capture content. A missing or corrupt file is treated as an empty list.
/// </summary>
internal sealed class RecentFilesStore
{
    public const int DefaultCapacity = 10;
    private const int MaximumPathLength = 32_767;
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string storePath;
    private readonly int capacity;
    private readonly List<string> paths = [];

    public RecentFilesStore(string storePath, int capacity = DefaultCapacity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        this.storePath = storePath;
        this.capacity = capacity;
        Load();
    }

    public IReadOnlyList<string> Paths => paths;

    public void Add(string path)
    {
        var normalized = Normalize(path) ?? throw new ArgumentException("A rooted file path is required.", nameof(path));
        paths.RemoveAll(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase));
        paths.Insert(0, normalized);
        if (paths.Count > capacity)
        {
            paths.RemoveRange(capacity, paths.Count - capacity);
        }
        Save();
    }

    public bool Remove(string path)
    {
        var normalized = Normalize(path);
        if (normalized is null
            || paths.RemoveAll(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)) == 0)
        {
            return false;
        }
        Save();
        return true;
    }

    public void Clear()
    {
        paths.Clear();
        Save();
    }

    private void Load()
    {
        RecentFilesDocument? document;
        try
        {
            if (!File.Exists(storePath))
            {
                return;
            }
            using var stream = File.OpenRead(storePath);
            document = JsonSerializer.Deserialize<RecentFilesDocument>(stream);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return;
        }

        foreach (var candidate in document?.Files ?? [])
        {
            var normalized = Normalize(candidate);
            if (normalized is not null
                && !paths.Contains(normalized, StringComparer.OrdinalIgnoreCase)
                && paths.Count < capacity)
            {
                paths.Add(normalized);
            }
        }
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(storePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            var temporaryPath = storePath + ".tmp";
            File.WriteAllBytes(
                temporaryPath,
                JsonSerializer.SerializeToUtf8Bytes(new RecentFilesDocument { Files = [.. paths] }, SerializerOptions));
            File.Move(temporaryPath, storePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Recent files are a convenience; a read-only profile must not block viewing captures.
        }
    }

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaximumPathLength || !Path.IsPathFullyQualified(path))
        {
            return null;
        }
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private sealed class RecentFilesDocument
    {
        public int Version { get; set; } = 1;

        public List<string>? Files { get; set; }
    }
}
