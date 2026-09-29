# SAZ Viewer

SAZ Viewer is a local command-line tool that reads Fiddler SAZ archives and writes a single portable HTML report. It has no runtime dependency on Fiddler Classic, FiddlerCore, a browser library, or a network service. Captured values are HTML-encoded, and the report's Content Security Policy blocks network access and captured active content.

## Prerequisites

- Windows with the .NET 8 SDK or newer to build
- Any modern browser to open the generated report

## Build and test

```powershell
dotnet build .\SazViewer.sln
dotnet test .\SazViewer.sln
```

## Usage

```powershell
dotnet run --project .\SazViewer.Cli -- .\capture.saz
dotnet run --project .\SazViewer.Cli -- .\capture.saz .\reports\capture.html
dotnet run --project .\SazViewer.Cli -- --help
```

When no output path is supplied, the report is written beside the input archive with an `.html` extension.

In the report, select an HTTP row with the mouse or keyboard to open its request and response in the resizable bottom pane. The pane keeps the session table visible, switches to a vertical layout on narrow windows, and provides formatted/raw views for detected JSON, XML, and text bodies.

## Publish a self-contained Windows executable

```powershell
dotnet publish .\SazViewer.Cli\SazViewer.Cli.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\publish
.\publish\saz-viewer.exe .\capture.saz .\capture.html
```

Use `win-arm64` instead of `win-x64` for Windows on ARM. The published executable includes the .NET runtime; only the generated executable is required to run the tool.

## Capture handling

- SAZ files are treated as ZIP archives, and sparse `raw/<id>_*` entries are supported.
- Individual missing or malformed records produce warnings instead of aborting the archive.
- HTTP body previews are bounded. Text uses a safely recognized charset; binary data is shown as a bounded hex preview.
- WebSocket records are displayed separately with direction, opcode/type, length, timestamp when available, and safe text or hex previews.
- The tool does not execute captured content and does not make network requests.

The WebSocket reader follows the mixed pseudo-header/binary record layout [described by Fiddler's author](https://stackoverflow.com/a/29566732) and decodes each declared-length frame according to [RFC 6455](https://datatracker.ietf.org/doc/html/rfc6455#section-5.2). Unknown or malformed variants are retained as bounded undecoded summaries with warnings.
