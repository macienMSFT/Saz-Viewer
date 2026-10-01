# Generated report runtime

The output is one HTML file. It contains no external scripts, styles, fonts, images, or data dependencies.

## Generator structure

`HtmlReportGenerator` is one partial class split by responsibility:

- `HtmlReportGenerator.cs` owns the outer document, CSS, browser JavaScript, list controls, inspector lifecycle, search, tabs, trees, themes, and popup mode.
- `HtmlReportGenerator.Payloads.cs` emits list rows/templates and builds compressed HTTP, MAPI, and WebSocket payload envelopes.

The split is source organization only; generated output remains deterministic.

## Data layout

```mermaid
flowchart TD
    REPORT["Generated HTML"] --> LIST["Inline session rows<br/>filter/search metadata"]
    REPORT --> TEMPLATES["Per-session template hosts"]
    TEMPLATES --> HTTP["gzip+base64<br/>http-session v1"]
    TEMPLATES --> MAPI["gzip+base64<br/>mapi-protocol v1"]
    TEMPLATES --> WS["gzip+base64<br/>websocket-session v1"]
    HTTP --> LAZY["Browser validates, decompresses,<br/>and hydrates selected views"]
    MAPI --> LAZY
    WS --> LAZY
```

An HTTP envelope contains ordered headers, start lines, format/detection metadata, retained captured bytes, and decoded bytes only when they differ. It does not store pre-rendered Headers, Raw, hex, JSON/XML trees, Auth text, WebView markup, or copy models. MAPI trees stay server-parsed because they depend on the C# protocol stack. WebSocket payloads stay independently compressed by session.

Before using an envelope, browser code checks its type, version, base64 form, declared decoded length, decompression limit, UTF-8/JSON validity, and model shape. A corrupt envelope produces an announced panel error rather than a blank inspector.

## HTTP inspector lifecycle

```mermaid
sequenceDiagram
    actor User
    participant List as Session list
    participant Inspector as Inspector shell
    participant Store as Canonical envelope
    participant View as Active secondary view

    User->>List: Select row
    List->>Inspector: Clone lightweight template
    Inspector->>Store: Validate + gzip-decompress once
    Store-->>Inspector: Request/response model
    Inspector->>Inspector: Build tabs and panes with DOM APIs
    User->>View: Select JSON / MAPI / Image / ...
    View->>View: Hydrate only selected representation
    User->>List: Previous / Next / Close
    Inspector->>View: Cancel builds, revoke Blob URLs,<br/>remove iframe, reset Auth reveal
```

Single view shows Request or Response tabs. The compact layout icon beside Previous/Next switches to split view; while both panes are visible, the redundant primary tabs are hidden and each pane has a visible heading. Each side owns independent secondary tabs, active-view search, copy status, and lazy state. The desktop divider supports pointer and keyboard resizing; narrow layouts stack the panes and disable the divider. The `single`/`split` preference is the only layout value stored.

Dynamic resources are tied to the current render generation. Navigation invalidates stale asynchronous work. Image Blob URLs are revoked, WebView iframes are removed, interrupted tree batches stop, and Auth reveal state is discarded.

```mermaid
stateDiagram-v2
    [*] --> Idle
    Idle --> Selected: select row
    Selected --> Hydrating: validate and decompress envelope
    Hydrating --> Ready: build request and response panes
    Hydrating --> PanelError: invalid envelope or decompression failure
    Ready --> Single: saved/default single layout
    Ready --> Split: saved split layout
    Single --> Split: layout toggle
    Split --> Single: layout toggle
    Single --> RenderingView: choose enabled secondary tab
    Split --> RenderingView: choose tab in either pane
    RenderingView --> Single: view ready in single layout
    RenderingView --> Split: view ready in split layout
    Single --> Single: search, copy, reveal, or primary-side change
    Split --> Split: independent search, copy, reveal, or divider resize
    Single --> Teardown: previous, next, close, or new row
    Split --> Teardown: previous, next, close, or new row
    PanelError --> Teardown: close or new row
    Teardown --> Idle: cancel work and release resources
    Teardown --> Selected: navigate to another session
```

`Selected` identifies the session but does not imply any expensive view exists. `Hydrating` runs once per selected session; both split panes share that validated model without double decompression. `RenderingView` represents generation-bound asynchronous tree, image, or WebView work. Any transition through `Teardown` invalidates its generation before releasing resources, so late work cannot update the next session.

## Secondary views

| View | Source |
|---|---|
| JSON/XML | Decoded text; client parser/formatter and bounded tree |
| MAPI | Precomputed compressed `MapiNode` model |
| Image | Complete validated retained raster bytes in a short-lived Blob URL |
| WebView | Complete retained HTML passed through the defensive tokenizer |
| HexView | Exact retained captured bytes, optionally distinct decoded bytes |
| Auth | Ordered auth headers; redacted until explicit per-view reveal |
| Headers | Start line and ordered captured headers |
| Raw | Captured headers plus decoded/body representation and byte provenance |

Copy text and active-search text are built from the active model on demand. Search never indexes unrevealed Auth values. JSON/XML/MAPI trees render in animation-frame batches with node/depth/child budgets.

## Browser state

Only two non-sensitive preferences use safely wrapped `localStorage`:

- `saz-viewer-theme`: explicit `light` or `dark`;
- `saz-viewer.http-layout.v1`: `single` or `split`.

No capture identity, URL, header, body, search query, Auth reveal state, or navigation position is persisted. Storage failure degrades to current-tab behavior.
