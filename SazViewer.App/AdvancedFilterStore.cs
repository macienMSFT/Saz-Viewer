using System.Text.Json;
using System.Text.Json.Serialization;
using SazViewer.App.Model;

namespace SazViewer.App;

internal sealed record SavedAdvancedFilter(string Name, AdvancedFilterDefinition Definition);

/// <summary>Strict, data-only named advanced-filter persistence and import/export.</summary>
internal sealed class AdvancedFilterStore
{
    public const int MaximumFilters = 100;
    public const int MaximumNameLength = 128;
    public const int MaximumFileBytes = 2 * 1024 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) }
    };

    private static AdvancedFilterStore? current;
    private readonly string storePath;
    private readonly List<SavedAdvancedFilter> filters = [];

    public AdvancedFilterStore(string storePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);
        this.storePath = storePath;
        filters.AddRange(ReadDocument(storePath, tolerateMissingOrInvalid: true).Filters);
    }

    public static AdvancedFilterStore Current => current ??= new AdvancedFilterStore(AppPaths.FiltersPath);

    internal static void Reset(AdvancedFilterStore? store = null) => current = store;

    public IReadOnlyList<SavedAdvancedFilter> Filters => filters;

    public void SaveAs(string name, AdvancedFilterDefinition definition)
    {
        var validated = Validate(name, definition);
        var index = filters.FindIndex(item => item.Name.Equals(validated.Name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            filters[index] = validated;
        }
        else
        {
            if (filters.Count >= MaximumFilters)
            {
                throw new InvalidDataException($"At most {MaximumFilters} named filters may be saved.");
            }
            filters.Add(validated);
        }
        filters.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));
        Save();
    }

    public bool Delete(string name)
    {
        var removed = filters.RemoveAll(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed)
        {
            Save();
        }
        return removed;
    }

    public void Export(string path, SavedAdvancedFilter filter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var validated = Validate(filter.Name, filter.Definition);
        WriteDocument(path, new FilterDocument { Filters = [validated] });
    }

    public int Import(string path)
    {
        var imported = ReadDocument(path, tolerateMissingOrInvalid: false).Filters;
        var merged = filters.ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var item in imported)
        {
            merged[item.Name] = item;
        }
        if (merged.Count > MaximumFilters)
        {
            throw new InvalidDataException($"Import would exceed the {MaximumFilters}-filter limit.");
        }
        filters.Clear();
        filters.AddRange(merged.Values.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase));
        Save();
        return imported.Count;
    }

    private static SavedAdvancedFilter Validate(string name, AdvancedFilterDefinition definition)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0 || trimmed.Length > MaximumNameLength)
        {
            throw new InvalidDataException($"Filter names must contain 1-{MaximumNameLength} characters.");
        }
        if (definition?.Rules is null || definition.Rules.Count == 0)
        {
            throw new InvalidDataException("A saved filter must contain at least one rule.");
        }
        foreach (var rule in definition.Rules)
        {
            if (rule?.Field is null
                || !Enum.IsDefined(rule.Operator) || !Enum.IsDefined(rule.Join) || !Enum.IsDefined(rule.Field.Kind))
            {
                throw new InvalidDataException("A filter contains an unknown operator, join, or field kind.");
            }
            if (rule.OpenParentheses is < 0 or > 3 || rule.CloseParentheses is < 0 or > 3)
            {
                throw new InvalidDataException("Saved parenthesis counts must be between zero and three.");
            }
            ValidateField(rule.Field);
        }
        var compilation = AdvancedFilterCompiler.Compile(definition);
        if (compilation.Errors.Count > 0)
        {
            throw new InvalidDataException(compilation.Errors[0].Message);
        }
        return new SavedAdvancedFilter(trimmed, Clone(definition));
    }

    private static void ValidateField(AdvancedFilterField field)
    {
        if (field.Kind is AdvancedFilterFieldKind.RequestBody or AdvancedFilterFieldKind.ResponseBody)
        {
            if (field.Column is not null)
            {
                throw new InvalidDataException("Body fields cannot contain a column definition.");
            }
            return;
        }
        var column = field.Column ?? throw new InvalidDataException("A column field is missing its definition.");
        if (string.IsNullOrWhiteSpace(column.Id) || column.Id.Length > 128
            || string.IsNullOrWhiteSpace(column.Source) || column.Source.Length > 256
            || column.Header is null || column.Header.Length > 128
            || column.Kind is not (SessionColumnSetting.BuiltInKind
                or SessionColumnSetting.RequestHeaderKind
                or SessionColumnSetting.ResponseHeaderKind
                or SessionColumnSetting.SessionFlagKind)
            || SessionColumnCatalog.Resolve(column) is null)
        {
            throw new InvalidDataException("A filter contains an invalid or unavailable column field.");
        }
    }

    private FilterDocument ReadDocument(string path, bool tolerateMissingOrInvalid)
    {
        try
        {
            if (!File.Exists(path))
            {
                if (tolerateMissingOrInvalid)
                {
                    return new FilterDocument();
                }
                throw new FileNotFoundException("The filter file does not exist.", path);
            }
            var information = new FileInfo(path);
            if (information.Length > MaximumFileBytes)
            {
                throw new InvalidDataException($"Filter files may not exceed {MaximumFileBytes / 1024:N0} KiB.");
            }
            using var stream = File.OpenRead(path);
            var document = JsonSerializer.Deserialize<FilterDocument>(stream, SerializerOptions)
                           ?? throw new InvalidDataException("The filter file is empty.");
            if (document.Version != 1 || document.Filters is null || document.Filters.Count > MaximumFilters)
            {
                throw new InvalidDataException("The filter file has an unsupported or invalid schema.");
            }
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var validated = new List<SavedAdvancedFilter>();
            foreach (var item in document.Filters)
            {
                if (item is null)
                {
                    throw new InvalidDataException("The filter file contains a missing filter.");
                }
                var filter = Validate(item.Name, item.Definition);
                if (!names.Add(filter.Name))
                {
                    throw new InvalidDataException($"The filter name '{filter.Name}' is duplicated.");
                }
                validated.Add(filter);
            }
            document.Filters = validated;
            return document;
        }
        catch (Exception exception) when (tolerateMissingOrInvalid
                                          && exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return new FilterDocument();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The filter file is not valid JSON: {exception.Message}", exception);
        }
    }

    private void Save() => WriteDocument(storePath, new FilterDocument { Filters = [.. filters] });

    private static void WriteDocument(string path, FilterDocument document)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            var temporaryPath = path + ".tmp";
            File.WriteAllBytes(temporaryPath, JsonSerializer.SerializeToUtf8Bytes(document, SerializerOptions));
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException("The filter file could not be written.", exception);
        }
    }

    private static AdvancedFilterDefinition Clone(AdvancedFilterDefinition definition) =>
        new(definition.Rules.Select(rule => rule with
        {
            Field = rule.Field with
            {
                Column = rule.Field.Column is null ? null : rule.Field.Column with { }
            }
        }).ToArray());

    private sealed class FilterDocument
    {
        public int Version { get; set; } = 1;

        public List<SavedAdvancedFilter> Filters { get; set; } = [];
    }
}
