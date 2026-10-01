# Testing and extension guide

## Test layers

| Test area | Main files | What it proves |
|---|---|---|
| Archive/encryption | `EncryptedSazTests.cs` | AES/ZipCrypto authentication, retries, corruption, limits, password handling |
| Export scrubbing | `AuthScrubberTests.cs`, `AuthScrubberBrowserTests.cs` | Typed-marker detection, parser-generated canaries, retained-byte leak checks, CLI aliases, and Edge view rendering |
| SAZ and HTTP | `SazParserTests.cs`, `HttpBodyDecodingTests.cs` | sparse discovery, metadata, ordering, body boundaries, codings, byte retention |
| WebSocket | `SazParserTests.cs` plus browser tests | Fiddler records, RFC frames, fragmentation, limits, inspector behavior |
| MAPI primitives | `MapiReaderTests.cs`, NSPI/rule/action tests | byte widths, offsets, restrictions, properties, entry IDs |
| ROP families | `Rop*Tests.cs` | fixed/variable dispatch, handles, state transitions, family semantics |
| FastTransfer | `FastTransferParserTests.cs` | lexical continuation, grammar phases, provenance, ICS state |
| Report generator | `HtmlReportGeneratorTests.cs`, `HtmlReportPayloadCompressionTests.cs` | encoded markup, envelope schemas, limits, deterministic models |
| Real browser | `HtmlReportBrowserTests.cs` | Edge layout, keyboard/ARIA, popup, theme, split view, lazy lifecycle, CSP/no-network |

Synthetic ZIP/SAZ fixtures are created during tests. Proprietary captures and generated reports stay outside git.

## Validation commands

```powershell
dotnet build .\SazViewer.sln -c Release
dotnet test .\SazViewer.sln -c Release --no-build
```

During development, run the narrowest relevant filter first, then the full suite before committing. Browser tests use installed Microsoft Edge and may take about a minute. A final CLI conversion should verify session/warning counts and open the generated file through the existing Edge smoke coverage.

## Add an HTTP list field

1. Parse it into `HttpSession` in `SazParser`.
2. Keep archive-derived text out of attributes unless it is encoded by `Attribute`.
3. Add the row/header metadata in `HtmlReportGenerator.Payloads.cs`.
4. Include it in browser filtering only if users reasonably search it.
5. Test missing, malformed, hostile, and width/ordering cases.

## Add an HTTP inspector view

1. Extend the canonical `MessagePayload` only when the existing bytes/headers cannot derive the view.
2. Add identical Request/Response tabs in the fixed order.
3. Define deterministic enablement and an explicit unavailable reason.
4. Build captured text with DOM/text-node APIs; never parent-DOM `innerHTML`.
5. Integrate copy and active-view search from the visible representation.
6. Add teardown for Blob URLs, iframes, promises, or reveal state.
7. Test single/split, desktop/narrow, popup, keyboard/ARIA, both themes, malformed envelopes, and zero console/network errors.

## Add an HTTP body decoder

Transfer and content codings are removed in reverse declaration order by `HttpBodyDecoder`; `chunked` is a transfer coding, while compression codings can appear in either header. To add a coding:

1. Extend `TryDecodeCompression` with the normalized lower-case token produced by `ParseCodings`. Do not silently alias an undocumented token.
2. Decode from the supplied byte array into a fresh bounded result. Route stream-based formats through the shared bounded read helpers or apply the same `OutputLimit` rule: no more than 4 MiB and no more than 100× the encoded length, with the existing 1 MiB floor.
3. Validate framing, checksums, trailers, and end-of-stream semantics offered by the format. A partial or ambiguous decode must return an explicit error; never expose partial decoded bytes as success.
4. Preserve wire provenance: `CapturedBytes` remains the pre-decoding prefix, `DecodedBytes` is the bounded final representation, `RemovedEncodings` records the actual removal order, and `NormalizedBytes` is retained only for an eligible complete MAPI body.
5. Keep failure behavior data-preserving: add a session warning and return the bounded captured hex representation rather than dropping the body.
6. Add tests for content and transfer headers, multiple coding layers/order, valid smallest and boundary payloads, corrupt/truncated framing, expansion-limit rejection, and retained-byte ownership. Include a synthetic SAZ conversion and report-envelope parity case.

## Add a MAPI operation

1. Confirm whether the operation has a fixed schema or needs a family decoder.
2. Register the ROP ID centrally in `RopSemanticParser`/`RopVariableDispatcher`.
3. Use `MapiReader` and preserve absolute offsets.
4. Emit every node through the shared `MapiNodeBudget`.
5. If request data is needed by the response, queue bounded state in `MapiCaptureContext` and commit it only on success.
6. Add request and response tests for success, protocol failure, truncation, unknown flags/types, handle reuse, and state cleanup.
7. Update `docs\mapi-parity.json` when coverage changes.

## Add FastTransfer syntax or grammar

Keep byte recognition and production validation separate:

- lexical changes belong in `FastTransferStreamLexer` and must preserve forward progress and continuation state;
- production ordering belongs in `FastTransferGrammar` and must consume only complete lexical elements;
- cross-buffer ownership belongs in `FastTransferStreamAssembler`/`MapiCaptureContext`.

Test a complete buffer, every legal split point, malformed length/marker, state invalidation, unrelated connection/handle reuse, and end-of-stream completeness.

## Add a WebSocket variant

Do not scan arbitrary binary data for a plausible next frame. Extend pseudo-header recognition only from documented evidence, preserve declared record lengths, and retain unsupported variants as undecoded records with warnings. Test both directions, control-frame interleaving, fragmented messages, malformed masks/opcodes, truncation, and retention limits.

## Performance and size checks

When a change affects report storage or rendering:

1. compare generated byte size on the same corpus;
2. compare decompressed envelope schemas semantically;
3. time initial load and the first open of the largest affected view;
4. verify lazy views do not duplicate body/header models;
5. use a backing-buffer or allocation assertion when the optimization is about memory ownership;
6. keep report and parser limits unchanged unless the requirement explicitly changes them.
