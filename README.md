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

In the report, select an HTTP row with the mouse or keyboard to open a full-screen inspector dialog. The session table stays behind the dialog with its filter and scroll position preserved. The dialog shows Previous/Next controls that step through the currently visible/filtered rows in chronological order (disabled at the first/last row, plus an `Alt+Left`/`Alt+Right` shortcut), a position indicator, and an accessible Close control (also closable with `Escape`, which restores focus to the originating row). Each session has top-level Request and Response tabs — Request is always selected first, whether the dialog was just opened or navigated to via Previous/Next — and exactly one side is shown at a time, including on narrow windows. Within the active side, a second, fixed-order tab strip — JSON, XML, MAPI, Headers, Raw — fills the remaining space, with unavailable tabs shown disabled. JSON and XML tabs offer a default, initially expanded Tree view (individually togglable nodes, Expand all/Collapse all, keyboard and mouse toggles) plus a Pretty Text syntax-highlighted view, when the body is recognized as valid JSON/XML; both views are rendered with bounded depth/child/node budgets so very large or deeply nested bodies stay responsive and show an accurate truncation note instead of expanding everything. The MAPI tab holds a searchable, lazily expandable protocol tree with byte offsets and lengths for detected MAPI/HTTP and NSPI sessions; Headers shows only the captured header block; Raw shows the original captured headers plus the decoded body text (with decode status/encodings), a bounded hex view for binary bodies, and a compact "Captured bytes" control when pre-decode bytes are available. The initial secondary tab favors MAPI, then JSON/XML, then Raw, then Headers, whichever is available, and every tab strip supports Left/Right/Home/End keyboard navigation with roving focus. Detected MAPI/HTTP and NSPI sessions also keep the protocol badge/filter in the session table.

## Publish a self-contained Windows executable

```powershell
dotnet publish .\SazViewer.Cli\SazViewer.Cli.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\publish
.\publish\saz-viewer.exe .\capture.saz .\capture.html
```

Use `win-arm64` instead of `win-x64` for Windows on ARM. The published executable includes the .NET runtime; only the generated executable is required to run the tool.

## Capture handling

- SAZ files are treated as ZIP archives, and sparse `raw/<id>_*` entries are supported.
- Individual missing or malformed records produce warnings instead of aborting the archive.
- Session-specific warnings remain visible in the affected session inspector. The aggregate warning section is currently omitted from generated HTML, while warning data remains available to the CLI/report model.
- HTTP body previews are bounded. Text uses a safely recognized charset; binary data is shown as a bounded hex preview.
- Declared chunked transfer framing is removed before content decoding. `gzip`, zlib-wrapped `deflate` (with a raw-DEFLATE compatibility fallback), and `br` are supported, including chained and repeated `Content-Encoding` fields in HTTP decoding order.
- Decoding is transactional and bounded: an HTTP entry is read through a 4 MiB protocol-payload limit plus bounded headers, each decoded stage is limited to at most 4 MiB and 100x expansion (with a 1 MiB floor), gzip content is capped at 128 members, displayed decoded bodies remain capped at 64 KiB, and captured encoded-byte views are capped at 16 KiB. Unsupported, corrupt, incomplete, or oversized content remains available as bounded captured hex with a warning.
- The inspector keeps captured headers unchanged. Successfully decoded bodies are shown as decoded text in the Raw tab (with decode status and removed encodings noted), and a compact "Captured bytes" control reveals the original pre-decode bytes when available.
- WebSocket frames are parsed with direction, opcode/type, length, timestamp when available, and safe text or hex previews; this data is retained internally but the WebSocket section is currently omitted from generated HTML.
- The tool does not execute captured content and does not make network requests.

The WebSocket reader follows the mixed pseudo-header/binary record layout [described by Fiddler's author](https://stackoverflow.com/a/29566732) and decodes each declared-length frame according to [RFC 6455](https://datatracker.ietf.org/doc/html/rfc6455#section-5.2). Unknown or malformed variants are retained as bounded undecoded summaries with warnings.

Concatenated gzip members are supported. The raw-DEFLATE compatibility path has no format checksum, so it provides structural validation but cannot provide the integrity guarantee available for gzip and zlib-wrapped DEFLATE.

## MAPI/HTTP and NSPI inspection

MAPI parsing uses the fully normalized HTTP entity bytes, not the 64 KiB display preview. Detection is based on `application/mapi-http`, `X-RequestType`, and `X-ResponseCode`. Parser state and budgets are capture-local; payloads are capped at 4 MiB, protocol trees at 25,000 nodes and depth 64, collections at 100,000 items, strings at 1 MiB, and retained raw nodes at 16 KiB.

Implemented transport coverage includes all 23 `X-RequestType` request and response envelope schemas, response additional headers/status, mailbox and NSPI structures, all 32 property-value forms, all 12 restriction forms (including SubObject, which upstream omits from its dispatcher), all 11 rule-action types and payload layouts, all 572 PidTag, 365 PidLid, and 131 string-named upstream property symbols, chained MS-OXCRPC extended buffers, XOR `0xA5`, bounded Direct2/LZ77 decompression, ROP list/handle-table framing, all 132 upstream ROP names, and all 40 upstream auxiliary payload dispatch mappings plus MS-OXCRPC `AUX_TYPE_SERVER_CAPABILITIES`, and safe unknown/raw fallback. Individual ROP semantics cover all 128 request and all 131 response dispatcher cases: 49 request/50 response fixed-width schemas plus 79 request/81 response variable, self-contained decoders for the MS-OXCFOLD/MS-OXCTABL (folder/table), MS-OXCPRPT/MS-OXCSTOR (property/stream/store), MS-OXCMSG/MS-OXORULE/MS-OXCPERM/MS-OXCNOTIF (message/rule/permission/notification), and MS-OXCFXICS (FastTransfer/ICS) families, dispatched centrally per operation. Capture-local FastTransfer state resolves each buffer-local handle index to a logical MAPI connection plus 32-bit server object handle, allowing bounded `varSizeValue` continuation across separate HTTP round trips while preventing joins across unrelated connections or reused handles. It tracks exact recursive marker pairs, classifies standalone ICS productions, and decodes context-special ProgressInformation and PropertyGroupInfo/PropertyGroup/GroupPropertyName structures, including bounded split values. State is completed from authoritative download status and invalidated on failure, `RopRelease`, or successful handle reuse; upload status is retained but not interpreted because the protocol requires clients to ignore it. ICS UploadStateStream Begin/Continue/End operations are correlated transactionally across HTTP round trips on the synchronization handle, bounded independently, and finalized as REPLGUID IDSET/CNSET data; failed, missing, mismatched, released, or reused operations discard state rather than splicing streams. Cross-operation shapes otherwise use bounded transactional state keyed by exact HTTP round trip or by logical MAPI connection as the protocol requires. This includes the five dispatcher shapes that require earlier request state, plus server-handle-keyed `RopSetColumns` reconstruction for `RopQueryRows`, `RopFindRow`, `RopExpandRow`, and `RopNotify` table rows; column state is committed only after success and invalidated on failure, `RopRelease`, `RopResetTable`, or table-handle reuse. Conflicting or unavailable correlation falls back to raw content plus a warning rather than guessing. NSPI `PtypString8` values use the capture's declared code page; unsupported or unavailable code pages retain bounded raw bytes and mark the parse partial. Extended-rule EntryID sizes and recipient/property counts follow the 32-bit MS-OXORULE definitions, correcting narrower reads in the pinned upstream implementation. Auxiliary-buffer offset-referenced trailer strings/bytes are resolved from validated, bounds-checked offsets rather than upstream's unchecked sequential pointer arithmetic. Complete FastTransfer/ICS production ordering and configure/copy-driven root selection, generic property arrays whose value type is not self-delimiting, and `RopReadRecipients` row properties without a deterministic column source are not yet parity-complete and remain explicit bounded raw fallback. See [`docs/mapi-parity.json`](docs/mapi-parity.json) for exact counts and named gaps.

The implementation was independently adapted with reference to the MIT-licensed [Office Inspectors for Fiddler](https://github.com/OfficeDev/Office-Inspectors-for-Fiddler) source at commit `c18dd66c99f3b5a96c2e1d31698c5cf2deb828e7` and Microsoft Open Specifications. See [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md). No Fiddler/FiddlerCore, HexBox, Outlook interop, EQATEC, Ionic Zip, or other upstream binary is included.
