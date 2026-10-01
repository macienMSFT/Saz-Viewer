namespace SazViewer.App;

/// <summary>Result of parsing the desktop app's command line: <c>SazViewer.App.exe [--] [path.saz ...]</c>.</summary>
internal sealed record AppArguments(IReadOnlyList<string> CapturePaths, bool ShowHelp, string? Error)
{
    /// <summary>Upper bound on paths per launch; also the single-instance hand-off limit.</summary>
    public const int MaximumPaths = 32;

    public const string Usage =
        """
        Usage:
          SazViewer.App [path.saz ...]
          SazViewer.App --help

        Opens one or more Fiddler SAZ captures in the SAZ Viewer desktop app,
        one tab per capture. If SAZ Viewer is already running, the paths are
        opened as new tabs in the running window. Encrypted captures prompt for
        a password in a masked dialog; passwords are never accepted on the
        command line.
        """;

    public static AppArguments Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var paths = new List<string>();
        var optionsEnded = false;
        foreach (var argument in args)
        {
            if (!optionsEnded && argument == "--")
            {
                optionsEnded = true;
                continue;
            }
            if (!optionsEnded && IsHelp(argument))
            {
                return new AppArguments([], ShowHelp: true, Error: null);
            }
            if (!optionsEnded && argument.StartsWith('-'))
            {
                return new AppArguments([], false, $"Unknown option '{argument}'.");
            }
            if (string.IsNullOrWhiteSpace(argument))
            {
                return new AppArguments([], false, "The capture path is empty.");
            }
            if (paths.Count == MaximumPaths)
            {
                return new AppArguments([], false, $"At most {MaximumPaths} capture paths can be opened at once.");
            }
            paths.Add(argument);
        }

        return new AppArguments(paths, false, null);
    }

    private static bool IsHelp(string argument) =>
        argument.Equals("--help", StringComparison.OrdinalIgnoreCase)
        || argument.Equals("-h", StringComparison.OrdinalIgnoreCase)
        || argument.Equals("/?", StringComparison.Ordinal);
}
