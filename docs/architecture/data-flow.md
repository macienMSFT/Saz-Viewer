# End-to-end data flow

## CLI to report

```mermaid
sequenceDiagram
    actor User
    participant CLI as CliApplication
    participant Archive as SazArchiveFactory
    participant Parser as SazParser
    participant Body as HTTP/WS/MAPI parsers
    participant Generator as HtmlReportGenerator
    participant Disk as Output HTML

    User->>CLI: saz-viewer capture.saz [report.html]
    CLI->>Archive: Open archive
    alt unencrypted ZIP
        Archive->>Archive: Validate entries and use ZipArchive
    else supported encrypted ZIP
        Archive->>User: Request password through provider
        Archive->>Archive: Authenticate and cache plaintext entry
    end
    CLI->>Parser: Parse archive
    Parser->>Parser: Discover sparse raw/<id> entries
    loop archive order
        Parser->>Body: Parse metadata, request, response, WebSocket
    end
    Parser->>Body: Parse MAPI sessions chronologically
    Parser-->>CLI: SazReport
    CLI->>Generator: Generate(report)
    Generator-->>CLI: Self-contained HTML
    CLI->>Disk: UTF-8 without BOM
```

## Desktop app to WebView2

The desktop app reuses the same pipeline and generated HTML. It keeps the report in memory and serves it to WebView2 from a synthetic origin, so there is no temp file and no `NavigateToString` size limit.

```mermaid
sequenceDiagram
    actor User
    participant Win as MainWindow
    participant Builder as ReportBuilder
    participant Core as Parser + Generator
    participant Session as SecureReportSession
    participant View as WebView2 report
    participant Disk as Exported HTML

    User->>Win: File › Open, drag-and-drop, recent file, or argument
    Win->>Builder: Build(path, scrub: false) on a worker thread
    opt encrypted archive
        Core->>Win: Request password (DialogPasswordProvider, max 3)
        Win->>User: Modal masked password dialog
    end
    Builder->>Core: Parse then Generate
    Core-->>Builder: SazReport and HTML
    Builder-->>Win: ReportDocument (HTML + UTF-8 bytes)
    Win->>Session: Set document, navigate
    View->>Session: GET https://saz-viewer.invalid/report.html
    Session-->>View: 200 bytes from memory
    View->>Session: Any other request or navigation
    Session-->>View: 403 or cancelled
    opt Open in new tab
        View->>Session: NewWindowRequested (report URL + fragment)
        Session->>Win: Create ReportPopupWindow with its own SecureReportSession
    end
    User->>Win: Export HTML or Export scrubbed HTML
    alt plain export
        Win->>Disk: Current HTML, UTF-8 without BOM
    else scrubbed export
        Win->>Builder: Build(path, scrub: true)
        Builder->>Core: Parse, AuthScrubber.Scrub, Generate
        Win->>Disk: Scrubbed HTML, UTF-8 without BOM
    end
```

## Archive discovery

`SazParser` recognizes these independent entries without assuming contiguous IDs:

| Entry | Meaning |
|---|---|
| `raw/<id>_c.txt` | Captured client request |
| `raw/<id>_s.txt` | Captured server response |
| `raw/<id>_m.xml` | Fiddler metadata and timers |
| `raw/<id>_w.txt` | Fiddler WebSocket record stream |

Entries are grouped case-insensitively. The first duplicate wins and produces a warning. Each group remembers its first archive position for stable fallback ordering. HTTP sessions are sorted by the best parsed Fiddler timestamp, then by archive order or numeric ID fallback. WebSocket messages use timestamp and stable source order.

## HTTP path

```mermaid
flowchart TD
    E["*_c.txt or *_s.txt"] --> READ["Read bounded entry prefix"]
    READ --> BOUNDARY{"Header/body boundary?"}
    BOUNDARY -- no --> HEADER["Header-only recovery<br/>plus warning"]
    BOUNDARY -- yes --> PARSE["Latin-1 header decode<br/>start line + ordered headers"]
    PARSE --> TRANSFER["Remove transfer codings<br/>including chunked"]
    TRANSFER --> CONTENT["Remove gzip / deflate / br<br/>in wire-removal order"]
    CONTENT --> TYPE{"Safe text charset?"}
    TYPE -- yes --> TEXT["Bounded decoded text"]
    TYPE -- no --> HEX["Bounded binary hex"]
    TEXT --> RETAIN["Captured / decoded / normalized<br/>bounded byte model"]
    HEX --> RETAIN
```

Decoding is transactional: a malformed, unsupported, incomplete, or over-limit stage does not expose partial decoded data. It falls back to the retained captured-byte representation and adds a warning. MAPI candidates retain normalized bytes up to the protocol payload limit; ordinary display remains limited to 64 KiB.

## WebSocket path

`WebSocketParser` treats `_w.txt` as alternating pseudo-header blocks and declared-length binary records, not as a text transcript. It:

1. identifies request/server direction from `Request-Length` or `Response-Length`;
2. parses one RFC 6455 frame from the bounded record prefix;
3. preserves FIN, opcode, mask, timestamp, lengths, and warnings;
4. retains malformed records as undecoded frames when safe resynchronization is impossible;
5. passes frames to `WebSocketMessageAssembler`, which reassembles fragmented data separately by direction while retaining control frames.

## Rendering handoff

The generated report has two data layers:

- **inline list metadata** for immediate sorting/filtering/navigation;
- **independently compressed envelopes** for HTTP sessions, MAPI trees, and WebSocket sessions.

Every HTTP session stores one versioned canonical request/response envelope. Headers, Raw, JSON/XML, Auth, Image, WebView, HexView, search, and copy text are derived in the browser only when needed. This keeps report size and initial browser work proportional to the session list rather than every possible view.

