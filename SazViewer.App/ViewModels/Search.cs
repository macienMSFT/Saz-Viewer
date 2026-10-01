namespace SazViewer.App.ViewModels;

/// <summary>Result of running an active-view search.</summary>
internal readonly record struct SearchOutcome(int Count, bool Capped)
{
    public static readonly SearchOutcome None = new(0, false);
}

/// <summary>
/// A view whose visible text can be searched (case-insensitive, literal). Implementations highlight every
/// match, reveal collapsed ancestors of matches (restoring them on <see cref="ClearSearch"/>), and scroll
/// the current match into view.
/// </summary>
internal interface ISearchableView
{
    /// <summary>Highlights matches of the already folded (lower-cased, trimmed, non-empty) query.</summary>
    SearchOutcome Search(string foldedQuery);

    void ShowMatch(int index);

    void ClearSearch();
}

/// <summary>Literal case-insensitive matching shared by every searchable view (the report's <c>foldActiveSearchText</c>).</summary>
internal static class SearchText
{
    /// <summary>The report's cap on highlighted matches per search.</summary>
    public const int MaxMatches = 5000;

    /// <summary>Folds per UTF-16 unit so match offsets map 1:1 back to the displayed text.</summary>
    public static string Fold(string value)
    {
        return string.Create(value.Length, value, static (span, source) =>
        {
            for (var index = 0; index < source.Length; index++)
            {
                span[index] = char.ToLowerInvariant(source[index]);
            }
        });
    }

    /// <summary>Normalizes user input the way the report does (trim, fold); empty means "no search".</summary>
    public static string NormalizeQuery(string? query) => Fold((query ?? string.Empty).Trim());

    /// <summary>Non-overlapping match starts of <paramref name="foldedQuery"/> in <paramref name="text"/>.</summary>
    public static IEnumerable<int> Matches(string text, string foldedQuery)
    {
        if (foldedQuery.Length == 0 || text.Length < foldedQuery.Length)
        {
            yield break;
        }
        var folded = Fold(text);
        var offset = 0;
        while (offset <= folded.Length - foldedQuery.Length)
        {
            var found = folded.IndexOf(foldedQuery, offset, StringComparison.Ordinal);
            if (found < 0)
            {
                yield break;
            }
            yield return found;
            offset = found + foldedQuery.Length;
        }
    }
}
