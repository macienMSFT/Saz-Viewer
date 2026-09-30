using System.Text;
using SazViewer.Cli;

Console.OutputEncoding = Encoding.UTF8;
return new CliApplication(new SystemCliConsole()).Run(args);
