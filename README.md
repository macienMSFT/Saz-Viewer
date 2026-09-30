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

In the report, select an HTTP row with the mouse or keyboard to open its request and response in the resizable bottom pane. The pane keeps the session table visible, switches to a vertical layout on narrow windows, and provides formatted/raw views for detected JSON, XML, and text bodies. Detected MAPI/HTTP and NSPI sessions have a protocol badge/filter and a searchable, expandable protocol tree with byte offsets and lengths.

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
- Declared chunked transfer framing is removed before content decoding. `gzip`, zlib-wrapped `deflate` (with a raw-DEFLATE compatibility fallback), and `br` are supported, including chained and repeated `Content-Encoding` fields in HTTP decoding order.
- Decoding is transactional and bounded: an HTTP entry is read through a 4 MiB protocol-payload limit plus bounded headers, each decoded stage is limited to at most 4 MiB and 100x expansion (with a 1 MiB floor), gzip content is capped at 128 members, displayed decoded bodies remain capped at 64 KiB, and captured encoded-byte views are capped at 16 KiB. Unsupported, corrupt, incomplete, or oversized content remains available as bounded captured hex with a warning.
- The detail pane keeps captured headers unchanged. Successfully decoded bodies offer formatted content, decoded text, and the original captured bytes as distinct views.
- WebSocket records are displayed separately with direction, opcode/type, length, timestamp when available, and safe text or hex previews.
- The tool does not execute captured content and does not make network requests.

The WebSocket reader follows the mixed pseudo-header/binary record layout [described by Fiddler's author](https://stackoverflow.com/a/29566732) and decodes each declared-length frame according to [RFC 6455](https://datatracker.ietf.org/doc/html/rfc6455#section-5.2). Unknown or malformed variants are retained as bounded undecoded summaries with warnings.

Concatenated gzip members are supported. The raw-DEFLATE compatibility path has no format checksum, so it provides structural validation but cannot provide the integrity guarantee available for gzip and zlib-wrapped DEFLATE.

## MAPI/HTTP and NSPI inspection

MAPI parsing uses the fully normalized HTTP entity bytes, not the 64 KiB display preview. Detection is based on `application/mapi-http`, `X-RequestType`, and `X-ResponseCode`. Parser state and budgets are capture-local; payloads are capped at 4 MiB, protocol trees at 25,000 nodes and depth 64, collections at 100,000 items, strings at 1 MiB, and retained raw nodes at 16 KiB.

Implemented transport coverage includes all 23 `X-RequestType` request and response envelope schemas, response additional headers/status, mailbox and NSPI structures, all 32 property-value forms, all 12 restriction forms (including SubObject, which upstream omits from its dispatcher), all 11 rule-action types and payload layouts, all 572 PidTag, 365 PidLid, and 131 string-named upstream property symbols, chained MS-OXCRPC extended buffers, XOR `0xA5`, bounded Direct2/LZ77 decompression, ROP list/handle-table framing, all 132 upstream ROP names, 50 context-independent request and 52 response semantic ROP schemas, all 40 upstream auxiliary payload dispatch mappings plus MS-OXCRPC `AUX_TYPE_SERVER_CAPABILITIES`, and safe unknown/raw fallback. NSPI `PtypString8` values use the capture's declared code page; unsupported or unavailable code pages retain bounded raw bytes and mark the parse partial. Extended-rule EntryID sizes and recipient/property counts follow the 32-bit MS-OXORULE definitions, correcting narrower reads in the pinned upstream implementation. Auxiliary-buffer offset-referenced trailer strings/bytes are resolved from validated, bounds-checked offsets rather than upstream's unchecked sequential pointer arithmetic. Remaining variable/context-dependent ROP semantics and FastTransfer reconstruction are not yet parity-complete and are reported as partial rather than success-shaped decoded content. See [`docs/mapi-parity.json`](docs/mapi-parity.json) for exact counts and named gaps.

The implementation was independently adapted with reference to the MIT-licensed [Office Inspectors for Fiddler](https://github.com/OfficeDev/Office-Inspectors-for-Fiddler) source at commit `c18dd66c99f3b5a96c2e1d31698c5cf2deb828e7` and Microsoft Open Specifications. See [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md). No Fiddler/FiddlerCore, HexBox, Outlook interop, EQATEC, Ionic Zip, or other upstream binary is included.
