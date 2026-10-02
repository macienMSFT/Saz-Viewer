namespace SazViewer.App;

/// <summary>
/// Decides which forwarded paths the running instance will open. Only fully qualified, normalized
/// paths to existing <c>.saz</c> or <c>.har</c> files are accepted; device/namespace paths, wildcards, and overlong
/// paths are refused. The sender resolves relative paths against its own working directory first.
/// </summary>
internal static class ForwardedPathValidator
{
    public const int MaximumPathLength = 2048;

    private static readonly char[] WildcardAndReservedCharacters = ['*', '?', '"', '<', '>', '|'];

    /// <summary>Resolves a command-line path to the absolute form that is forwarded; null when it cannot be resolved.</summary>
    public static string? TryResolve(string path, string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaximumPathLength)
        {
            return null;
        }
        try
        {
            var full = Path.GetFullPath(path, workingDirectory);
            return full.Length <= MaximumPathLength ? full : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>Syntactic checks only (no file system access).</summary>
    public static bool IsWellFormed(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.Length > MaximumPathLength
            || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0
            || path.IndexOfAny(WildcardAndReservedCharacters) >= 0
            || path.IndexOf(':', 2) >= 0
            || path.StartsWith(@"\\?\", StringComparison.Ordinal)
            || path.StartsWith(@"\\.\", StringComparison.Ordinal)
            || !Path.IsPathFullyQualified(path)
            || (!path.EndsWith(".saz", StringComparison.OrdinalIgnoreCase)
                && !path.EndsWith(".har", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
        try
        {
            return string.Equals(Path.GetFullPath(path), path, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Well-formed and an existing file (not a directory).</summary>
    public static bool IsAcceptable(string? path) => IsWellFormed(path) && File.Exists(path);
}
