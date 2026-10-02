# End-to-end data flow

## CLI to report

```mermaid
sequenceDiagram
    actor User
    participant CLI as CliApplication
    participant Detect as CaptureParser
    participant Archive as SazArchiveFactory / SazParser
    participant Har as HarParser
    participant Body as HTTP/WS/MAPI parsers
    participant Generator as HtmlReportGenerator
    participant Disk as Output HTML

    User->>CLI: saz-viewer capture.saz|capture.har [report.html]
    CLI->>Detect: Sniff ZIP signature or JSON log
    alt SAZ capture
        Detect->>Archive: Open archive
        alt unencrypted ZIP
            Archive->>Archive: Validate entries and use ZipArchive
        else supported encrypted ZIP
            Archive->>User: Request password through provider
            Archive->>Archive: Authenticate and cache plaintext entry
        end
        Archive->>Archive: Discover sparse raw/<id> entries
        Archive->>Body: Parse metadata, request, response, WebSocket
        Archive-->>Detect: Shared sessions
    else HAR capture
        Detect->>Har: Parse bounded log.entries JSON
        Har->>Body: Map decoded HTTP bodies, timings, flags, WebSocket messages
        Har-->>Detect: Shared sessions
    end
    Body->>Body: Parse MAPI sessions chronologically
    Detect-->>CLI: Shared SazReport
    CLI->>Generator: Generate(report)
    Generator-->>CLI: Self-contained HTML
    CLI->>Disk: UTF-8 without BOM
```

## Desktop app

The desktop app reuses the same parse (and optional scrub) pipeline but renders the `SazReport` model in native WPF views. Per-session work happens only when a session is selected; see [lazy loading](desktop-app.md#lazy-loading). The report HTML is generated only on Export.

```mermaid
sequenceDiagram
    actor User
    participant Win as MainWindow
    participant Tabs as CaptureTabCollection
    participant Builder as ReportBuilder
    participant Core as Parser (+ AuthScrubber)
    participant Tab as CaptureTab
    participant VM as CaptureViewModel
    participant Disk as Exported HTML

    User->>Win: File › Open, drop, recent file, argument, or forwarded path
    Win->>Tabs: Find(path)
    alt already open
        Tabs-->>Win: existing tab
        Win->>Tab: Activate (no re-parse)
    else new capture
        Win->>Builder: Build(path, scrub: false) on a worker thread (serialized)
        opt encrypted archive
            Core->>Win: Request password (DialogPasswordProvider, max 3)
            Win->>User: Modal masked password dialog
        end
        Builder->>Core: Parse
        Core-->>Builder: SazReport
        Builder-->>Win: ReportDocument (model only)
        Win->>Tabs: Add(new CaptureTab), make active
        Tab->>VM: Build session rows, show the grid
    end
    User->>VM: Select a session
    VM->>VM: Build the inspector panes and the selected tab's content on demand
    opt View scrubbed
        User->>Win: File › View scrubbed
        Win->>Builder: Build(path, scrub: true)
        Builder->>Core: Parse, AuthScrubber.Scrub
        Win->>Tab: Replace document, show the scrub banner
    end
    User->>Win: Export HTML or Export scrubbed HTML (active tab)
    alt export matches the tab's scrub state
        Win->>Builder: Generate HTML from the tab's document (first use)
    else scrub state differs
        Win->>Builder: Build(path, scrub), then Generate
    end
    Win->>Disk: UTF-8 without BOM, identical to the CLI (with or without --scrub-auth)
    User->>Win: Close tab (✕, middle-click, Ctrl+W)
    Win->>Tab: Dispose: pop-out windows, watcher, view-models, report
```

### Tab lifecycle, file changes, and reload

The tab records a `FileFingerprint` (length plus last-write time) just before each parse. `CaptureFileWatcher` watches the containing directory for the file name, including renames into or out of that name, so temp-file-then-rename saves are detected. It coalesces bursts with a 400 ms `Debouncer` and then raises one event, which the tab handles on the UI thread. `FileChangeTracker` compares the current fingerprint with the baseline. The app's own reads do not change the fingerprint, so they never raise a notice, and a dismissed state is not reported again.

```mermaid
stateDiagram-v2
    [*] --> Parsing: open (new path)
    Parsing --> [*]: parse failed / password cancelled (no tab)
    Parsing --> Loaded: tab added + activated
    state Loaded {
        [*] --> Grid
        Grid --> Inspecting: select a session
        Inspecting --> Grid: Esc / Close
    }
    Loaded --> Changed: watcher fingerprint != baseline
    Loaded --> Deleted: file missing
    Changed --> Loaded: Dismiss (remembered) / file restored
    Deleted --> Loaded: Dismiss / file reappears unchanged
    Deleted --> Changed: file reappears with new content
    Changed --> Reloading: Reload (user click)
    Reloading --> Loaded: success: replace document, close pop-outs, new baseline
    Reloading --> ReloadFailed: parse error / wrong password
    ReloadFailed --> Reloading: Reload again
    ReloadFailed --> Loaded: Dismiss (old report kept)
    Loaded --> Closed: close tab
    Changed --> Closed: close tab
    Deleted --> Closed: close tab
    ReloadFailed --> Closed: close tab
    Closed --> [*]: dispose view-models, report, watcher, pop-outs
```

### Single-instance hand-off

```mermaid
sequenceDiagram
    actor User
    participant Shell as Explorer
    participant B as Second launch (App)
    participant M as Local\ mutex
    participant Pipe as Named pipe (current-user ACL)
    participant A as Running instance
    participant Win as MainWindow

    User->>Shell: Double-click capture.saz
    Shell->>B: SazViewer.App.exe "C:\…\capture.saz"
    B->>B: Parse args, resolve relative paths to full paths
    B->>M: WaitOne(0)
    alt mutex acquired (no running instance)
        B->>Pipe: Start server
        B->>B: Become primary and open the paths as tabs
    else mutex held by A
        B->>Pipe: Connect (CurrentUserOnly, 3 s timeout)
        alt pipe not listening yet (A starting)
            B->>M: WaitOne(500 ms), then retry (up to 4 attempts)
        end
        B->>B: AllowSetForegroundWindow(A's pipe server PID)
        B->>Pipe: "SAZV" + v1 + length + {"paths":[...]}
        Pipe->>A: Request
        A->>A: Strict decode (header, version, size, JSON schema)
        alt well-formed request
            A->>A: Keep only existing, normalized .saz/.har paths (others counted as ignored)
            A-->>B: 0x00 Accepted
            A->>Win: Dispatcher: open each path as a tab (dedupe), bring window to front
            B->>B: Exit 0
        else malformed request
            A-->>B: 0x01 Rejected
            B->>User: "refused the request" message, exit 1
        end
    end
    Note over B,A: If A exits mid-handshake, B retries and becomes primary once the mutex is released or abandoned. If no role is settled after 4 attempts, B runs standalone.
```

The forwarding client finds the server process with `GetNamedPipeServerProcessId` and calls `AllowSetForegroundWindow` for it. This lets the running window come to the front even though the second launch owns the foreground. No password, option, or working directory is ever sent.

## Archive discovery

`SazParser` recognizes these independent entries without assuming contiguous IDs:

| Entry | Meaning |
|---|---|
| `raw/<id>_c.txt` | Captured client request |
| `raw/<id>_s.txt` | Captured server response |
| `raw/<id>_m.xml` | Fiddler metadata and timers |
| `raw/<id>_w.txt` | Fiddler WebSocket record stream |

Entries are grouped case-insensitively. The first duplicate wins and produces a warning. Each group remembers its first archive position for stable fallback ordering. HTTP sessions are sorted by the best parsed Fiddler timestamp, then by archive order or numeric ID fallback. WebSocket messages use timestamp and stable source order.

## HAR mapping

`HarParser` reads a bounded HTTP Archive 1.2 JSON document and maps each `log.entries` item to the same `HttpSession` model. `startedDateTime` provides chronological order; original entry index supplies stable IDs and fallback order. HAR `content.text` is already decoded, including base64 decoding when `content.encoding` is `base64`, so `Content-Encoding` is never applied again and original wire bytes remain explicitly unavailable. HAR timing values populate typed timer metadata (`wait` → TTFB, `receive` → download, plus DNS/connect/SSL), while creator/browser, page, initiator, resource type, priority, connection, and server address remain available through report metadata and desktop columns. Chrome/Edge `_webSocketMessages` map to the shared WebSocket model.

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
