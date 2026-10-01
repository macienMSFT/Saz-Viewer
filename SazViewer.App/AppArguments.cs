namespace SazViewer.App;

/// <summary>Result of parsing the desktop app's command line: <c>SazViewer.App.exe [--] [path.saz]</c>.</summary>
internal sealed record AppArguments(string? CapturePath, bool ShowHelp, string? Error)
{
    public const string Usage =
        """
        Usage:
          SazViewer.App [path.saz]
          SazViewer.App --help

        Opens a Fiddler SAZ capture in the SAZ Viewer desktop app. Encrypted
        captures prompt for a password in a masked dialog; passwords are never
        accepted on the command line.
        """;

    public static AppArguments Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? path = null;
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
                return new AppArguments(null, ShowHelp: true, Error: null);
            }
            if (!optionsEnded && argument.StartsWith('-'))
            {
                return new AppArguments(null, false, $"Unknown option '{argument}'.");
            }
            if (string.IsNullOrWhiteSpace(argument))
            {
                return new AppArguments(null, false, "The capture path is empty.");
            }
            if (path is not null)
            {
                return new AppArguments(null, false, "Only one capture path can be opened at a time.");
            }
            path = argument;
        }

        return new AppArguments(path, false, null);
    }

    private static bool IsHelp(string argument) =>
        argument.Equals("--help", StringComparison.OrdinalIgnoreCase)
        || argument.Equals("-h", StringComparison.OrdinalIgnoreCase)
        || argument.Equals("/?", StringComparison.Ordinal);
}
