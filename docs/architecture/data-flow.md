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

The desktop app reuses the same pipeline and generated HTML. It keeps each tab's report in memory and serves it to that tab's WebView2 from a synthetic origin, so there is no temp file and no `NavigateToString` size limit.

```mermaid
sequenceDiagram
    actor User
    participant Win as MainWindow
    participant Tabs as CaptureTabCollection
    participant Builder as ReportBuilder
    participant Core as Parser + Generator
    participant Tab as CaptureTab
    participant Session as SecureReportSession
    participant View as WebView2 report
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
        Builder->>Core: Parse then Generate
        Core-->>Builder: SazReport and HTML
        Builder-->>Win: ReportDocument (HTML + UTF-8 bytes)
        Win->>Tabs: Add(new CaptureTab), make active
        Win->>Tab: Activate
        Tab->>Session: First activation: create WebView2 on the shared environment, set document, navigate
    end
    View->>Session: GET https://saz-viewer.invalid/report.html
    Session-->>View: 200 bytes from memory
    View->>Session: Any other request or navigation
    Session-->>View: 403 or cancelled
    opt Open in new tab
        View->>Session: NewWindowRequested (report URL + fragment)
        Session->>Tab: Create ReportPopupWindow with its own SecureReportSession
    end
    User->>Win: Export HTML or Export scrubbed HTML (active tab)
    alt plain export
        Win->>Disk: Active tab's HTML, UTF-8 without BOM
    else scrubbed export
        Win->>Builder: Build(path, scrub: true)
        Builder->>Core: Parse, AuthScrubber.Scrub, Generate
        Win->>Disk: Scrubbed HTML, UTF-8 without BOM
    end
    User->>Win: Close tab (✕, middle-click, Ctrl+W)
    Win->>Tab: Dispose: popups, watcher, WebView2, report bytes
```

### Tab lifecycle, file changes, and reload

The tab records a `FileFingerprint` (length plus last-write time) just before each parse. `CaptureFileWatcher` watches the containing directory for the file name, including renames into or out of that name, so temp-file-then-rename saves are detected. It coalesces bursts with a 400 ms `Debouncer` and then raises one event, which the tab handles on the UI thread. `FileChangeTracker` compares the current fingerprint with the baseline. The app's own reads do not change the fingerprint, so they never raise a notice, and a dismissed state is not reported again.

```mermaid
stateDiagram-v2
    [*] --> Parsing: open (new path)
    Parsing --> [*]: parse failed / password cancelled (no tab)
    Parsing --> Loaded: tab added + activated
    state Loaded {
        [*] --> Lazy
        Lazy --> Live: first activation creates WebView2
        Live --> Live: activate / deactivate (Visibility)
    }
    Loaded --> Changed: watcher fingerprint != baseline
    Loaded --> Deleted: file missing
    Changed --> Loaded: Dismiss (remembered) / file restored
    Deleted --> Loaded: Dismiss / file reappears unchanged
    Deleted --> Changed: file reappears with new content
    Changed --> Reloading: Reload (user click)
    Reloading --> Loaded: success: replace document, close popups, new baseline
    Reloading --> ReloadFailed: parse error / wrong password
    ReloadFailed --> Reloading: Reload again
    ReloadFailed --> Loaded: Dismiss (old report kept)
    Loaded --> Closed: close tab
    Changed --> Closed: close tab
    Deleted --> Closed: close tab
    ReloadFailed --> Closed: close tab
    Closed --> [*]: dispose WebView2, report, watcher, popups
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
            A->>A: Keep only existing, normalized .saz paths (others counted as ignored)
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

