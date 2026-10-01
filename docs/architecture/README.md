# Architecture guide

This guide explains SAZ Viewer from the outside in. Start here, then follow the links for the subsystem you need to change.

```mermaid
flowchart LR
    SAZ["Fiddler SAZ<br/>ZIP archive"] --> CLI["SazViewer.Cli"]
    SAZ --> APP["SazViewer.App<br/>WPF desktop host"]
    CLI --> AF["SazArchiveFactory"]
    APP --> AF
    AF --> SP["SazParser"]
    SP --> HTTP["HTTP messages<br/>and bounded bodies"]
    SP --> WS["WebSocket frames<br/>and logical messages"]
    HTTP --> MAPI["Capture-local<br/>MAPI parser"]
    HTTP --> MODEL["SazReport model"]
    WS --> MODEL
    MAPI --> MODEL
    MODEL --> GEN["HtmlReportGenerator"]
    GEN --> HTML["One self-contained<br/>offline HTML file"]
    HTML --> UI["Browser session list<br/>and lazy inspectors"]
    GEN --> MEM["In-memory report<br/>served to WebView2"]
    MEM --> UI
    APP -.->|Export HTML| HTML
```

The CLI writes the report to disk for any browser. The desktop app runs the same parse/scrub/generate pipeline, serves the generated HTML from memory to a locked-down WebView2 control, and writes it to disk only on explicit Export.

## Documents

| Document | Read it when you need to |
|---|---|
| [End-to-end data flow](data-flow.md) | Follow an archive entry from disk through parsing to the report |
| [MAPI parsing](mapi-parsing.md) | Understand envelopes, ROP dispatch, capture-local state, FastTransfer, and safety budgets |
| [Report runtime](report-runtime.md) | Change the generated HTML, payload envelopes, inspectors, split view, search, or copy behavior |
| [Security model](security.md) | Review archive, password, captured-content, CSP, WebView, and Auth trust boundaries |
| [Testing and extension guide](testing-and-extension.md) | Find the right tests or add a parser, ROP, inspector view, or report field |

## Project and file map

| Path / principal type | Responsibility |
|---|---|
| `SazViewer.Cli\Program.cs` | Minimal process entry point that delegates to `CliApplication`. |
| `SazViewer.Cli\CliApplication.cs` | CLI parsing, help/errors/exit codes, secure interactive or redirected password input, parsing, and report writing. |
| `SazViewer.App\App.xaml.cs` / `AppArguments.cs` | Desktop entry point and `[--] [capture.saz]` / `--help` argument parsing. |
| `SazViewer.App\MainWindow.xaml.cs` | File menu (Open, Open Recent, Export, Export scrubbed), drag-and-drop, background parse with busy state, title/status, and popup window tracking. |
| `SazViewer.App\ReportBuilder.cs` | CLI-equivalent parse → optional `AuthScrubber` → `HtmlReportGenerator` pipeline, UTF-8 (no BOM) export, and failure wording. |
| `SazViewer.App\SecureReportSession.cs` | Shared per-user WebView2 environment and all WebView2 lockdown: in-memory report serving, request/navigation/new-window/download/permission/context-menu policy. |
| `SazViewer.App\ReportWebViewPolicy.cs` | Pure URI, frame, dropped-file, and context-menu allowlist decisions used by `SecureReportSession`. |
| `SazViewer.App\PasswordDialog.xaml.cs` | Modal masked password dialog and the three-attempt `ISazPasswordProvider` adapter. |
| `SazViewer.App\RecentFilesStore.cs` / `AppPaths.cs` | Per-user `%LOCALAPPDATA%\SazViewer` paths and the bounded, path-only `recent.json` list. |
| `SazViewer.App\ReportPopupWindow.xaml.cs` | App-controlled window for the report's **Open in new tab** inspector. |
| `SazViewer.Core\Models.cs` | Public report, HTTP, retained-body, WebSocket message, and frame models. |
| `SazViewer.Core\SazArchive.cs` / `SazArchiveFactory` | ZIP inspection, plain/encrypted reader selection, archive limits, integrity checks, and bounded entry access. |
| `SazViewer.Core\SazPasswordProvider.cs` | Password-provider contract, password limit, and archive/password exception taxonomy. |
| `SazViewer.Core\SazParser.cs` / `SazParser` | Sparse `raw/<id>_*` discovery, per-session orchestration, metadata/timers, WebSocket association, MAPI handoff, and chronological ordering. |
| `SazViewer.Core\HttpMessageParser.cs` | HTTP start line, ordered headers, CRLF body boundary, charset-safe preview, and hex rendering. |
| `SazViewer.Core\HttpBodyDecoder.cs` | Transfer/content coding removal, decompression validation, retained-byte ownership, and decoding warnings. |
| `SazViewer.Core\BodyFormatter.cs` | Server-side JSON/XML/text format detection and bounded pretty formatting used by parity tests and fallbacks. |
| `SazViewer.Core\SafeHtmlPreview.cs` / `SafeHtmlPreviewBuilder` | Conservative HTML recognition and inert WebView document construction. |
| `SazViewer.Core\WebSocketParser.cs` | Fiddler `_w.txt` record parsing and bounded RFC 6455 frame extraction. |
| `SazViewer.Core\WebSocketMessageAssembler.cs` | Fragment/control-frame handling and frame-to-logical-message reassembly. |
| `SazViewer.Core\HtmlReportGenerator.cs` | Static HTML/CSS/JavaScript shell, CSP, browser-side list, inspector, tabs, search, copy, theme, popup, and split-view behavior. |
| `SazViewer.Core\HtmlReportGenerator.Payloads.cs` | Session-list markup plus versioned compressed HTTP, MAPI, and WebSocket payload construction. |
| `SazViewer.Tests` | Unit, integration, generator/envelope, encryption, CLI, and real Edge coverage. |
| `SazViewer.App.Tests` | Desktop argument parsing, recent-files storage, CLI byte-for-byte export parity, WebView2 policy, and a launched-app WebView2 smoke test. |
| `docs\mapi-parity.json` | Machine-readable MAPI protocol coverage inventory. |

### MAPI files

| Path / principal type | Responsibility |
|---|---|
| `Mapi\MapiModels.cs` | Public MAPI tree/report models, `MapiCaptureContext` transactional state, and shared parse limits. |
| `Mapi\MapiCaptureParser.cs` | Detects MAPI/HTTP sessions, owns capture-local state, and correlates request/response parsing. |
| `Mapi\MapiHttpMessageParser.cs` | Parses MAPI/HTTP request types and response envelopes before protocol dispatch. |
| `Mapi\MapiReader.cs` | Offset-aware bounded primitive reader, parse exception, and shared `MapiNodeBudget`. |
| `Mapi\AuxiliaryPayloadParser.cs` | Parses MAPI auxiliary blocks and performance/session metadata. |
| `Mapi\ExtendedBufferParser.cs` | Validates and unwraps compressed/XOR extended buffers before ROP parsing. |
| `Mapi\RopBufferParser.cs` | Splits ROP buffers, validates handle tables, and coordinates semantic operation parsing. |
| `Mapi\RopSemanticParser.cs` | Central fixed-schema ROP catalog and operation dispatcher. |
| `Mapi\RopVariableDispatcher.cs` | Routes variable-shape ROPs to family-specific decoders. |
| `Mapi\RopFolderTableDecoders.cs` | Folder, hierarchy, contents-table, row, and related table operations. |
| `Mapi\RopMessageRulesDecoders.cs` | Message, attachment, recipient, stream, rules, synchronization, and import operations. |
| `Mapi\RopPropertyStoreDecoders.cs` | Property, named-property, notification, and store/logon operations. |
| `Mapi\RopFastTransferDecoders.cs` | FastTransfer/ICS ROP semantics and capture-local stream/state transitions. |
| `Mapi\FastTransferStreamLexer.cs` | Incremental MS-OXCFXICS token/value decoding with split-value continuation. |
| `Mapi\FastTransferGrammar.cs` | Validates root-specific FastTransfer production order and completion. |
| `Mapi\FastTransferStreamState.cs` | Immutable lexer/grammar state, hard limits, and capture-local multi-buffer assembler. |
| `Mapi\NspiPropertyParser.cs` | NSPI property rows/values with wire-width context. |
| `Mapi\NspiRestrictionParser.cs` | Bounded recursive NSPI restriction parsing. |
| `Mapi\MapiEntryIdParser.cs` | Store, folder, message, address-book, and one-off EntryID structures. |
| `Mapi\MapiServerIdParser.cs` | ServerId and folder/message identifier variants. |
| `Mapi\RuleActionParser.cs` | Rule action blocks, action-specific payloads, and embedded restrictions. |
| `Mapi\MapiPropertyNames.Generated.cs` | Generated property-tag-to-symbol lookup used for readable trees. |

## Core data model

`SazReport` is the handoff between parsing and rendering. It owns chronological `HttpSession` objects, logical `WebSocketMessage` objects, aggregate warnings, and the optional `MapiCapture`. An `HttpMessage` preserves ordered headers and a `BodyPreview`. The body model distinguishes:

- captured bytes before transfer/content decoding;
- decoded bytes used by ordinary inspectors;
- fully normalized bytes retained only for bounded MAPI candidates;
- original and decoded lengths, truncation, charset, and removed encodings.

MAPI output is a bounded immutable `MapiNode` tree. Browser code never reparses MAPI wire bytes.

## Design invariants

1. **One bad entry does not discard the archive.** Session-local failures become warnings whenever safe continuation is possible.
2. **Bytes are bounded before interpretation.** Archive, HTTP, WebSocket, report, tree, and search layers each enforce their own limits.
3. **State is capture-local and transactional.** MAPI correlation is committed only after the protocol response establishes success.
4. **Captured content is data, never application code.** The outer report uses text nodes/encoding; WebView is a separate deny-all sandbox.
5. **The report is portable and offline.** CSS, JavaScript, models, and compressed payloads are embedded; no network dependency exists.
6. **Expensive views are lazy.** The list metadata is immediately available, while per-session envelopes, trees, images, and frames hydrate only when selected.
