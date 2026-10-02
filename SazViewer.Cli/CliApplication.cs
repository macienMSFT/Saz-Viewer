using System.Text;
using SazViewer.Core;

namespace SazViewer.Cli;

internal interface ICliConsole
{
    bool IsInputRedirected { get; }
    TextReader In { get; }
    void Write(string value);
    void WriteLine(string value = "");
    void WriteErrorLine(string value);
    ConsoleKeyInfo ReadKey(bool intercept);
}

internal sealed class SystemCliConsole : ICliConsole
{
    public bool IsInputRedirected => Console.IsInputRedirected;
    public TextReader In => Console.In;
    public void Write(string value) => Console.Write(value);
    public void WriteLine(string value = "") => Console.WriteLine(value);
    public void WriteErrorLine(string value) => Console.Error.WriteLine(value);
    public ConsoleKeyInfo ReadKey(bool intercept) => Console.ReadKey(intercept);
}

internal sealed class CliApplication(ICliConsole console)
{
    public int Run(string[] args)
    {
        if (args.Length == 0 || args.Any(IsHelp))
        {
            PrintHelp();
            return args.Length == 0 ? 1 : 0;
        }

        if (!TryParseArguments(args, out var parsed, out var argumentError))
        {
            console.WriteErrorLine($"Error: {argumentError}");
            console.WriteErrorLine("Run 'saz-viewer --help' for usage.");
            return 2;
        }

        try
        {
            var inputPath = Path.GetFullPath(parsed.InputPath);
            var outputPath = parsed.OutputPath is not null
                ? Path.GetFullPath(parsed.OutputPath)
                : Path.ChangeExtension(inputPath, ".html");

            if (!inputPath.EndsWith(".saz", StringComparison.OrdinalIgnoreCase)
                && !inputPath.EndsWith(".har", StringComparison.OrdinalIgnoreCase))
            {
                console.WriteErrorLine($"Warning: input does not use the .saz or .har extension: {inputPath}");
            }

            if (string.Equals(inputPath, outputPath, StringComparison.OrdinalIgnoreCase))
            {
                console.WriteErrorLine("Error: output path must be different from the input path.");
                return 2;
            }

            var format = CaptureParser.DetectFormat(inputPath);
            if (format == CaptureFormat.Har && parsed.PasswordFromStandardInput)
            {
                console.WriteErrorLine("Error: --password-stdin applies only to encrypted SAZ archives, not HAR files.");
                return 2;
            }
            ISazPasswordProvider? passwordProvider = format == CaptureFormat.Saz
                ? parsed.PasswordFromStandardInput
                    ? new StandardInputPasswordProvider(console)
                    : new InteractivePasswordProvider(console)
                : null;
            var report = new CaptureParser().Parse(inputPath, passwordProvider);
            if (parsed.ScrubAuth)
            {
                AuthScrubber.Scrub(report);
            }
            var html = new HtmlReportGenerator().Generate(report);
            var outputDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }
            File.WriteAllText(
                outputPath,
                html,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            console.WriteLine($"Created: {outputPath}");
            console.WriteLine(
                $"HTTP sessions: {report.Sessions.Count}; WebSocket messages: {report.WebSocketMessages.Count}; Warnings: {report.Warnings.Count}");
            if (report.AuthScrub is { } scrub)
            {
                console.WriteLine(
                    $"Authentication scrub: {scrub.Total:N0} value(s) replaced"
                    + (scrub.Counts.Count == 0
                        ? "."
                        : $" ({string.Join(", ", scrub.Counts.Select(item => $"{item.Key}: {item.Value:N0}"))})."));
            }
            if (report.Warnings.Count > 0)
            {
                console.WriteLine("The report contains warning details for entries that could not be fully parsed.");
            }
            return 0;
        }
        catch (FileNotFoundException exception)
        {
            console.WriteErrorLine($"Error: {exception.Message}");
            return 3;
        }
        catch (ArgumentException exception)
        {
            console.WriteErrorLine($"Error: invalid path or argument: {exception.Message}");
            return 2;
        }
        catch (UnauthorizedAccessException exception)
        {
            console.WriteErrorLine($"Error: access denied: {exception.Message}");
            return 4;
        }
        catch (SazArchiveException exception)
        {
            console.WriteErrorLine($"Error: {exception.Message}");
            return 5;
        }
        catch (InvalidDataException exception)
        {
            console.WriteErrorLine($"Error: {exception.Message}");
            return 5;
        }
        catch (IOException exception)
        {
            console.WriteErrorLine($"Error: I/O failure: {exception.Message}");
            return 6;
        }
    }

    private static bool TryParseArguments(
        IReadOnlyList<string> args,
        out ParsedArguments parsed,
        out string error)
    {
        var passwordFromStandardInput = false;
        var scrubAuth = false;
        var positional = new List<string>(2);
        foreach (var argument in args)
        {
            if (argument.Equals("--password-stdin", StringComparison.OrdinalIgnoreCase))
            {
                if (passwordFromStandardInput)
                {
                    parsed = default;
                    error = "--password-stdin may be specified only once.";
                    return false;
                }
                passwordFromStandardInput = true;
            }
            else if (argument.Equals("--scrub-auth", StringComparison.OrdinalIgnoreCase)
                || argument.Equals("-ScrubAuth", StringComparison.OrdinalIgnoreCase))
            {
                if (scrubAuth)
                {
                    parsed = default;
                    error = "--scrub-auth/-ScrubAuth may be specified only once.";
                    return false;
                }
                scrubAuth = true;
            }
            else if (argument.StartsWith('-'))
            {
                parsed = default;
                error = $"unknown option '{argument}'.";
                return false;
            }
            else
            {
                positional.Add(argument);
            }
        }

        if (positional.Count is < 1 or > 2)
        {
            parsed = default;
            error = "expected an input .saz or .har path and, optionally, an output .html path.";
            return false;
        }

        parsed = new ParsedArguments(
            positional[0],
            positional.Count == 2 ? positional[1] : null,
            passwordFromStandardInput,
            scrubAuth);
        error = "";
        return true;
    }

    private void PrintHelp()
    {
        console.WriteLine(
            """
            SAZ Viewer - create a portable, local HTML report from a Fiddler SAZ or HTTP Archive file.

            Usage:
              saz-viewer [--password-stdin] [--scrub-auth] <input.saz|input.har> [output.html]
              saz-viewer --help

            Encrypted SAZ archives are detected automatically. When necessary, the tool
            securely prompts for a password with no echo and allows up to three attempts.
            For noninteractive use, --password-stdin reads one password line (up to 1,024
            characters) from standard input and does not retry. Leading and trailing
            password spaces are preserved. A password command-line option is intentionally
            not supported because process arguments can leak secrets.
            --password-stdin applies only to SAZ input; HAR files are not password-protected.

            --scrub-auth (also -ScrubAuth) replaces credentials, tokens, cookies,
            secret query/body fields, WebSocket values, and MAPI authentication values
            with typed markers before report generation. Binary retained bytes that
            cannot be safely rewritten are omitted. The report and CLI show replacement
            counts but never print the captured secret values.

            If output.html is omitted, the report is written beside the capture using
            the same base name. The tool never executes captured content or accesses
            the network.
            """);
    }

    private static bool IsHelp(string argument) =>
        argument.Equals("--help", StringComparison.OrdinalIgnoreCase)
        || argument.Equals("-h", StringComparison.OrdinalIgnoreCase);

    private readonly record struct ParsedArguments(
        string InputPath,
        string? OutputPath,
        bool PasswordFromStandardInput,
        bool ScrubAuth);
}

internal sealed class InteractivePasswordProvider(ICliConsole console) : ISazPasswordProvider
{
    public int MaximumAttempts => 3;

    public char[]? GetPassword(SazPasswordRequest request)
    {
        if (console.IsInputRedirected)
        {
            throw new SazPasswordUnavailableException();
        }

        if (request.PreviousPasswordRejected)
        {
            console.WriteErrorLine("Incorrect password. Try again.");
        }
        console.Write("Password: ");
        return PasswordInput.ReadNoEcho(console);
    }
}

internal sealed class StandardInputPasswordProvider(ICliConsole console) : ISazPasswordProvider
{
    public int MaximumAttempts => 1;

    public char[]? GetPassword(SazPasswordRequest request)
    {
        if (!console.IsInputRedirected)
        {
            return PasswordInput.ReadNoEcho(console);
        }

        var characters = new List<char>(
            Math.Min(64, SazPasswordLimits.MaximumCharacters + 1));
        try
        {
            while (true)
            {
                var value = console.In.Read();
                if (value < 0)
                {
                    return characters.Count == 0 ? null : characters.ToArray();
                }

                var character = (char)value;
                if (character == '\n')
                {
                    return characters.ToArray();
                }
                if (character == '\r')
                {
                    if (console.In.Peek() == '\n')
                    {
                        console.In.Read();
                    }
                    return characters.ToArray();
                }
                if (characters.Count <= SazPasswordLimits.MaximumCharacters)
                {
                    characters.Add(character);
                }
            }

        }
        finally
        {
            for (var index = 0; index < characters.Count; index++)
            {
                characters[index] = '\0';
            }
        }
    }
}

internal static class PasswordInput
{
    public static char[]? ReadNoEcho(ICliConsole console)
    {
        var characters = new List<char>(
            Math.Min(64, SazPasswordLimits.MaximumCharacters + 1));
        try
        {
            while (true)
            {
                var key = console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                {
                    console.WriteLine();
                    return characters.ToArray();
                }
                if (key.Key == ConsoleKey.Escape || key.KeyChar == '\u0003')
                {
                    console.WriteLine();
                    return null;
                }
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (characters.Count > 0)
                    {
                        characters.RemoveAt(characters.Count - 1);
                    }
                    continue;
                }
                if (!char.IsControl(key.KeyChar)
                    && characters.Count <= SazPasswordLimits.MaximumCharacters)
                {
                    characters.Add(key.KeyChar);
                }
            }
        }
        finally
        {
            for (var index = 0; index < characters.Count; index++)
            {
                characters[index] = '\0';
            }
        }
    }
}
