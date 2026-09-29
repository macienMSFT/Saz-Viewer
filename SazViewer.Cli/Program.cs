using System.Text;
using SazViewer.Core;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length == 0 || args.Contains("--help", StringComparer.OrdinalIgnoreCase) || args.Contains("-h"))
{
    PrintHelp();
    return args.Length == 0 ? 1 : 0;
}

if (args.Length > 2 || args[0].StartsWith('-'))
{
    Console.Error.WriteLine("Error: expected an input .saz path and, optionally, an output .html path.");
    Console.Error.WriteLine("Run 'saz-viewer --help' for usage.");
    return 2;
}

try
{
    var inputPath = Path.GetFullPath(args[0]);
    var outputPath = args.Length == 2
        ? Path.GetFullPath(args[1])
        : Path.ChangeExtension(inputPath, ".html");

    if (!inputPath.EndsWith(".saz", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine($"Warning: input does not use the .saz extension: {inputPath}");
    }

    if (string.Equals(inputPath, outputPath, StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("Error: output path must be different from the input path.");
        return 2;
    }

    var report = new SazParser().Parse(inputPath);
    var html = new HtmlReportGenerator().Generate(report);
    var outputDirectory = Path.GetDirectoryName(outputPath);
    if (!string.IsNullOrEmpty(outputDirectory))
    {
        Directory.CreateDirectory(outputDirectory);
    }
    File.WriteAllText(outputPath, html, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    Console.WriteLine($"Created: {outputPath}");
    Console.WriteLine(
        $"HTTP sessions: {report.Sessions.Count}; WebSocket messages: {report.WebSocketMessages.Count}; Warnings: {report.Warnings.Count}");
    if (report.Warnings.Count > 0)
    {
        Console.WriteLine("The report contains warning details for entries that could not be fully parsed.");
    }
    return 0;
}
catch (FileNotFoundException exception)
{
    Console.Error.WriteLine($"Error: {exception.Message}");
    return 3;
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine($"Error: invalid path or argument: {exception.Message}");
    return 2;
}
catch (UnauthorizedAccessException exception)
{
    Console.Error.WriteLine($"Error: access denied: {exception.Message}");
    return 4;
}
catch (InvalidDataException exception)
{
    Console.Error.WriteLine($"Error: {exception.Message}");
    return 5;
}
catch (IOException exception)
{
    Console.Error.WriteLine($"Error: I/O failure: {exception.Message}");
    return 6;
}

static void PrintHelp()
{
    Console.WriteLine(
        """
        SAZ Viewer - create a portable, local HTML report from a Fiddler SAZ archive.

        Usage:
          saz-viewer <input.saz> [output.html]
          saz-viewer --help

        If output.html is omitted, the report is written beside the archive using
        the same base name. The tool never executes captured content or accesses
        the network.
        """);
}
