using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;

namespace SazViewer.Core;

public sealed class HtmlReportGenerator
{
    private readonly BodyFormatter bodyFormatter = new();

    private const int TreeMaxNodes = 4000;
    private const int TreeMaxDepth = 40;
    private const int TreeMaxChildrenPerNode = 300;
    private const int TreeMaxScalarLength = 300;
    private const int MaxCopyCharacters = 1024 * 1024;
    private const int MaxHydratedDisplayCharacters = 256 * 1024;
    private const int HexViewBytesLimit = 1024;
    private const int PayloadVersion = 1;
    private const int CopyPayloadMaxDecodedBytes = 32 * 1024 * 1024;
    private const int ProtocolPayloadMaxDecodedBytes = 32 * 1024 * 1024;
    private const int TreePayloadMaxDecodedBytes = 8 * 1024 * 1024;
    private const int WebSocketPayloadMaxDecodedBytes = 32 * 1024 * 1024;
    private const int MaxWebSocketMessagesPerSession = 5000;
    private const int MaxWebSocketRawPayloadBytes = 160 * 1024;
    private const int WebSocketPayloadContentBudgetBytes = 28 * 1024 * 1024;

    private sealed record CopySource(
        string AccessibleName,
        string Description,
        string? Text,
        string? Kind = null,
        string? Error = null,
        int BodyStart = -1,
        int BodyLength = 0,
        int CapturedStart = -1,
        int CapturedLength = 0);
    private readonly record struct BuiltCopyText(
        string? Text,
        bool TooLarge,
        int BodyStart = -1,
        int BodyLength = 0,
        int CapturedStart = -1,
        int CapturedLength = 0);
    private sealed record CompressedPayload(string Type, string Base64, int DecodedBytes);
    private sealed record ImageViewInfo(string MimeType, string Detection, string Animation, string? Warning);
    private sealed record AuthViewData(string Redacted, string Full);

    // Each tree level round-trips through two JSON.NET-serializer nesting levels (an object, then
    // its "children" array before the next object), so a TreeMaxDepth-limited document can need
    // roughly double that many levels here. Give a generous safety margin above 2*TreeMaxDepth so
    // serialization itself never silently fails (and thereby disables the Tree toggle) for content
    // that our own depth budget is specifically designed to still show, truncated, in the tree.
    private static readonly JsonSerializerOptions TreePayloadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = (2 * TreeMaxDepth) + 32
    };

    private static readonly HashSet<string> AuthHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Proxy-Authorization",
        "WWW-Authenticate",
        "Proxy-Authenticate"
    };

    public string Generate(SazReport report)
    {
        var html = new StringBuilder(256 * 1024);
        html.Append("""
<!doctype html>
<html lang="en" data-theme="system">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; base-uri 'none'; form-action 'none'; object-src 'none'; connect-src 'none'; worker-src 'none'; media-src 'none'; font-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; img-src blob:">
<title>SAZ capture</title>
<script>
(()=>{try{const value=localStorage.getItem('saz-viewer-theme');if(value==='light'||value==='dark')document.documentElement.dataset.theme=value}catch{}})();
</script>
<style>
:root{color-scheme:dark;--bg:#0d1117;--panel:#161b22;--panel2:#21262d;--text:#e6edf3;--muted:#8b949e;--line:#30363d;--accent:#58a6ff;--warn:#d29922;--warning-bg:#2b2111;--info-bg:#13233a;--protocol-kind:#d2a8ff;--protocol-binary:#ffa657;--syn-blue:#79c0ff;--syn-string:#a5d6ff;--syn-number:#ffa657;--syn-red:#ff7b72;--syn-punct:#8b949e;--syn-green:#7ee787;--syn-purple:#d2a8ff;--direction-client:#58a6ff;--direction-server:#3fb950;--selected:#1f6feb55;--hover:#1f2630}
:root[data-theme=system]{color-scheme:light dark}
:root[data-theme=light]{color-scheme:light;--bg:#fff;--panel:#f6f8fa;--panel2:#eaeef2;--text:#1f2328;--muted:#59636e;--line:#d0d7de;--accent:#0969da;--warn:#9a6700;--warning-bg:#fff8c5;--info-bg:#ddf4ff;--protocol-kind:#6639ba;--protocol-binary:#805000;--syn-blue:#0550ae;--syn-string:#0a3069;--syn-number:#953800;--syn-red:#cf222e;--syn-punct:#59636e;--syn-green:#116329;--syn-purple:#6639ba;--direction-client:#0969da;--direction-server:#1a7f37;--selected:#ddf4ff;--hover:#f3f4f6}
@media(prefers-color-scheme:light){:root[data-theme=system]{--bg:#fff;--panel:#f6f8fa;--panel2:#eaeef2;--text:#1f2328;--muted:#59636e;--line:#d0d7de;--accent:#0969da;--warn:#9a6700;--warning-bg:#fff8c5;--info-bg:#ddf4ff;--protocol-kind:#6639ba;--protocol-binary:#805000;--syn-blue:#0550ae;--syn-string:#0a3069;--syn-number:#953800;--syn-red:#cf222e;--syn-punct:#59636e;--syn-green:#116329;--syn-purple:#6639ba;--direction-client:#0969da;--direction-server:#1a7f37;--selected:#ddf4ff;--hover:#f3f4f6}}
@media print{:root,:root[data-theme=system],:root[data-theme=light],:root[data-theme=dark]{color-scheme:light;--bg:#fff;--panel:#fff;--panel2:#f6f8fa;--text:#1f2328;--muted:#59636e;--line:#d0d7de;--accent:#0969da;--warn:#9a6700;--warning-bg:#fff8c5;--info-bg:#ddf4ff;--protocol-kind:#6639ba;--protocol-binary:#805000;--syn-blue:#0550ae;--syn-string:#0a3069;--syn-number:#953800;--syn-red:#cf222e;--syn-punct:#59636e;--syn-green:#116329;--syn-purple:#6639ba;--direction-client:#0969da;--direction-server:#1a7f37;--selected:#ddf4ff;--hover:#f3f4f6}}
*{box-sizing:border-box}html{scrollbar-gutter:stable}body{margin:0;background:var(--bg);color:var(--text);font:14px/1.45 system-ui,Segoe UI,sans-serif}
body.inspector-open{overflow:hidden}
body.inspector-only main{display:none}
main{width:100%;padding:4px}h2,h3,h4{margin:.25em 0}.muted,.format-status{color:var(--muted)}
.controls{display:flex;gap:6px;flex-wrap:wrap;margin:0 0 4px}input,select,button{background:var(--panel);border:1px solid var(--line);border-radius:6px;color:var(--text);padding:7px 10px}
input{min-width:280px;flex:1}button{cursor:pointer}
.theme-toggle{flex:0 0 36px;display:inline-flex;align-items:center;justify-content:center;width:36px;min-width:36px;height:36px;padding:6px;color:var(--text)}.theme-toggle svg{display:block;width:19px;height:19px;fill:none;stroke:currentColor;stroke-width:1.8;stroke-linecap:round;stroke-linejoin:round}.theme-toggle .theme-bulb-core{fill:transparent;stroke:none}.theme-toggle[aria-pressed=true]{color:var(--syn-number);border-color:var(--accent)}.theme-toggle[aria-pressed=true] .theme-bulb-core{fill:currentColor}.theme-toggle:focus-visible{outline:2px solid var(--accent);outline-offset:2px}
table{width:100%;border-collapse:collapse;background:var(--panel);font-size:13px}th{position:sticky;top:0;z-index:2;background:var(--panel2);text-align:left}
th,td{padding:8px;border:1px solid var(--line);vertical-align:top}tbody tr:hover{background:var(--hover)}#httpTable{min-width:1140px;table-layout:auto}#httpTable th,#httpTable td{padding:5px 7px;line-height:1.3}
#httpTable .http-time{width:184px;min-width:184px;white-space:nowrap}#httpTable .http-id{width:60px}#httpTable .http-method{width:84px}#httpTable .http-protocol{width:96px}
#httpTable .http-url{width:52%;min-width:420px;word-break:normal;overflow-wrap:anywhere}#httpTable .http-status{width:140px}#httpTable .http-bytes{width:72px}
#httpTable tbody tr{cursor:pointer}
#httpTable tbody tr:focus{outline:2px solid var(--accent);outline-offset:-2px}#httpTable tbody tr.selected{background:var(--selected);box-shadow:inset 4px 0 var(--accent)}
.num{text-align:right;white-space:nowrap}.badge,.format-badge{padding:2px 7px;border:1px solid var(--line);border-radius:10px;white-space:nowrap}
details{margin:4px 0}summary{cursor:pointer;color:var(--accent)}pre{white-space:pre-wrap;overflow:auto;background:var(--bg);border:1px solid var(--line);padding:10px;word-break:break-word;tab-size:2}
.warning{border-left:4px solid var(--warn);padding:6px 10px;margin:5px 0;background:var(--warning-bg)}.hidden{display:none!important}
.http-workspace{min-height:420px}.http-table-scroll{min-height:180px;overflow:auto;border:1px solid var(--line)}
dialog#httpInspector{position:fixed;inset:0;width:100vw;height:100vh;max-width:100vw;max-height:100vh;margin:0;padding:0;border:none;background:var(--bg);color:var(--text)}
dialog#httpInspector::backdrop{background:#000c}
dialog#httpInspector[open]{display:flex;flex-direction:column}
.inspector-header{display:flex;align-items:flex-start;gap:10px;padding:8px 14px;border-bottom:1px solid var(--line);background:var(--panel2);flex-wrap:wrap}
.inspector-heading{flex:1;min-width:220px;margin:0;font-size:16px;overflow-wrap:anywhere}
.inspector-nav{display:flex;align-items:center;gap:6px}
.inspector-position{color:var(--muted);font-size:12px;white-space:nowrap;min-width:70px;text-align:center}
.inspector-close{font-size:16px;line-height:1;padding:6px 10px}
.inspector-open-status{flex-basis:100%;min-height:0;color:var(--muted)}.inspector-open-status.warning{color:var(--text)}
.session-details{flex:0 0 auto;max-height:30vh;overflow:auto;margin:2px 14px 0}.session-details summary{font-size:12px}
.inspector-body{flex:1;min-height:0;display:flex;flex-direction:column;overflow:hidden}
.primary-tab-strip{padding:0 14px;background:var(--panel2);flex:0 0 auto}
.tab-panels.primary-panels{flex:1;min-height:0;display:flex;flex-direction:column;overflow:hidden;border:none;padding:0;background:transparent}
.primary-panel{flex:1;min-height:0;display:flex;flex-direction:column;padding:10px 14px;overflow:hidden}
.message-panel{display:flex;flex-direction:column;flex:1;min-height:0}
.headers{white-space:pre-wrap}
.tab-strip{display:flex;gap:2px;flex-wrap:wrap;border-bottom:1px solid var(--line);margin:8px 0 0;flex:0 0 auto}
.tab-strip [role=tab]{background:transparent;border:1px solid transparent;border-bottom:none;border-radius:6px 6px 0 0;padding:6px 12px;color:var(--muted);cursor:pointer;font:inherit}
.tab-strip [role=tab][aria-selected=true]{color:var(--text);background:var(--panel);border-color:var(--line);border-bottom:2px solid var(--accent);margin-bottom:-1px}
.tab-strip [role=tab]:disabled{color:var(--muted);cursor:not-allowed;opacity:.65}
.tab-strip [role=tab]:focus-visible{outline:2px solid var(--accent);outline-offset:-2px;z-index:1}
.tab-panels{flex:1;min-height:0;overflow:auto;border:1px solid var(--line);border-top:none;background:var(--panel);padding:10px 12px}
.tab-panel.hidden{display:none!important}.tab-panel-mapi{height:100%;min-height:340px;display:flex;flex-direction:column;overflow:hidden}.tab-panel-mapi>.copy-toolbar,.tab-panel-mapi>.protocol-meta,.tab-panel-mapi>.warning{flex:none}.tab-panel-mapi>.protocol-block{flex:1}.tab-empty{color:var(--muted);padding:14px 4px}
.tab-panel-image,.tab-panel-webview,.tab-panel-hex,.tab-panel-auth{min-height:340px}.image-view,.webview,.hex-view,.auth-view{height:calc(100% - 34px);min-height:300px;display:flex;flex-direction:column;gap:7px}.image-meta{display:flex;flex-wrap:wrap;gap:6px 14px;color:var(--muted)}.image-stage{flex:1;min-height:220px;overflow:auto;display:flex;align-items:center;justify-content:center;border:1px solid var(--line);background-color:var(--panel2);background-image:linear-gradient(45deg,var(--line) 25%,transparent 25%),linear-gradient(-45deg,var(--line) 25%,transparent 25%),linear-gradient(45deg,transparent 75%,var(--line) 75%),linear-gradient(-45deg,transparent 75%,var(--line) 75%);background-size:20px 20px;background-position:0 0,0 10px,10px -10px,-10px 0}.image-stage img{display:block;max-width:100%;max-height:100%;object-fit:contain}.image-load-status{color:var(--muted);min-height:1.4em}.webview-toolbar{display:flex;align-items:center;gap:7px;flex-wrap:wrap}.webview-mode{display:flex;gap:2px}.webview-mode button[aria-pressed=true]{border-color:var(--accent);color:var(--text)}.webview-status{color:var(--muted);min-height:1.4em}.webview-frame-host{flex:1;min-height:240px;border:1px solid var(--line);background:var(--panel2);overflow:hidden}.webview-frame{display:block;width:100%;height:100%;min-height:240px;border:0;background:var(--bg)}.webview-source{flex:1;min-height:240px;margin:0;white-space:pre-wrap;overflow:auto}.hex-toolbar{display:flex;align-items:center;justify-content:space-between;gap:8px;flex-wrap:wrap}.hex-toolbar label{display:flex;align-items:center;gap:6px}.hex-toolbar select{padding:4px 24px 4px 7px}.hex-dump{flex:1;min-height:240px;margin:0;white-space:pre;word-break:normal;font:12px/1.4 ui-monospace,SFMono-Regular,Consolas,monospace}.auth-reveal{align-self:flex-start}.auth-headers{flex:1;min-height:220px;margin:0}.auth-status{color:var(--muted)}
.format-meta{display:flex;gap:8px;align-items:center;flex-wrap:wrap;margin:0 0 6px}.format-meta .format-status{flex:1;min-width:180px}
.captured-bytes{margin-top:8px}.captured-bytes summary{cursor:pointer;color:var(--accent)}
.body-view{margin:6px 0}.decode-status{margin:6px 0;padding:6px 8px;border-left:3px solid var(--accent);background:var(--info-bg)}.session-meta pre{max-height:180px}
.protocol-meta{margin-bottom:4px}.protocol-block{margin:0;min-height:0;display:flex;flex-direction:column}.protocol-toolbar{position:sticky;top:0;z-index:2;display:flex;gap:5px;align-items:center;min-height:30px;margin:0;padding:3px 0 4px;background:var(--panel)}.protocol-toolbar button{padding:3px 8px}.protocol-load-status{margin-left:auto;color:var(--muted);font-size:11px}.protocol-tree{flex:1;min-height:240px;overflow:auto;border:1px solid var(--line);padding:3px 4px 8px;background:var(--panel);font:12px/1.35 ui-monospace,Consolas,monospace}.protocol-tree>.tree-item{min-width:max-content}.protocol-tree .tree-item{margin:0}.protocol-tree .tree-row{position:relative;display:grid;grid-template-columns:14px minmax(240px,1fr) max-content;gap:4px;align-items:baseline;min-height:20px;padding:1px 4px 1px 0;border-radius:2px}.protocol-tree .tree-row:hover{background:color-mix(in srgb,var(--accent) 10%,transparent)}.protocol-tree .tree-group{position:relative;margin-left:7px;padding-left:13px;border-left:1px dotted var(--muted)}.protocol-tree .tree-group>.tree-item>.tree-row::before{content:"";position:absolute;left:-13px;top:10px;width:11px;border-top:1px dotted var(--muted)}.protocol-tree .tree-caret{width:13px;height:16px;border:1px solid var(--line);border-radius:2px;text-align:center;line-height:13px;background:var(--panel2);color:var(--text);font-size:11px}.protocol-tree .tree-caret-leaf{visibility:hidden}.protocol-main{white-space:pre-wrap;overflow-wrap:anywhere}.protocol-name{color:var(--accent);font-weight:700}.protocol-separator{color:var(--muted)}.protocol-value{color:var(--text)}.protocol-value-binary{color:var(--protocol-binary)}.protocol-technical{color:var(--muted);white-space:nowrap;font-size:11px}.protocol-kind{color:var(--protocol-kind)}.protocol-tree-loading{color:var(--muted);font-style:italic;padding:3px 18px}
.syn-key{color:var(--syn-blue)}.syn-string{color:var(--syn-string)}.syn-number{color:var(--syn-number)}.syn-literal{color:var(--syn-red)}.syn-punct{color:var(--syn-punct)}.syn-tag{color:var(--syn-green)}.syn-attr{color:var(--syn-purple)}.syn-comment{color:var(--syn-punct);font-style:italic}.syn-value{color:var(--syn-string)}
.tree-toolbar{display:flex;gap:6px;align-items:center;flex-wrap:wrap;margin:0 0 6px}.view-toggle{display:flex;gap:2px}.view-toggle button[aria-pressed=true]{border-color:var(--accent);color:var(--text)}
.copy-toolbar{display:flex;gap:8px;align-items:center;justify-content:flex-end;margin:0 0 6px}.copy-toolbar button{padding:4px 9px}.copy-status{min-height:1.2em;color:var(--muted);font-size:12px}.copy-toolbar button:focus-visible{outline:2px solid var(--accent);outline-offset:2px}
.tree-view{font-family:ui-monospace,Consolas,monospace;font-size:12.5px}
.tree-item{margin:1px 0}.tree-row{display:flex;gap:6px;align-items:baseline;cursor:default;border-radius:4px;padding:1px 4px}
.tree-item[role=treeitem]{outline:none}.tree-item[role=treeitem]:focus-visible>.tree-row,.tree-status:focus-visible{outline:2px solid var(--accent);outline-offset:-1px}
.tree-caret{width:1em;display:inline-block;color:var(--muted)}.tree-caret-leaf{visibility:hidden}
.tree-group{margin-left:16px;padding-left:8px;border-left:1px dotted var(--line)}.tree-group[hidden]{display:none}
.tree-label-object,.tree-label-array,.tree-label-element{color:var(--accent)}.tree-label-string,.tree-label-text,.tree-label-cdata{color:var(--syn-string)}.tree-label-number,.tree-label-boolean{color:var(--syn-number)}.tree-label-null{color:var(--syn-red)}.tree-label-attribute{color:var(--syn-purple)}.tree-label-comment{color:var(--syn-punct);font-style:italic}
.tree-truncated{color:var(--warn);font-size:11px}.tree-status{color:var(--muted);font-style:italic;padding:2px 4px}
.sr-only{position:absolute;width:1px;height:1px;padding:0;margin:-1px;overflow:hidden;clip:rect(0,0,0,0);white-space:nowrap;border:0}
.websocket-inspector{flex:1;min-height:0;display:flex;flex-direction:column;padding:10px 14px}.ws-layout{flex:1;min-height:0;display:flex;gap:6px;overflow:hidden}
.ws-traffic-pane,.ws-detail-pane{min-height:0;display:flex;flex-direction:column;border:1px solid var(--line);background:var(--panel)}.ws-traffic-pane{min-width:280px;flex:0 1 var(--ws-left,38%)}.ws-detail-pane{min-width:320px;flex:1 1 0}.ws-pane-heading{font-size:14px;padding:8px 10px;margin:0;border-bottom:1px solid var(--line)}
.ws-splitter{flex:0 0 8px;align-self:stretch;border-radius:4px;background:var(--line);cursor:col-resize;touch-action:none;position:relative}.ws-splitter::after{content:"";position:absolute;inset:0 2px;border-left:1px solid var(--muted);border-right:1px solid var(--muted)}.ws-splitter:hover,.ws-splitter:focus-visible{background:var(--accent);outline:2px solid var(--accent);outline-offset:1px}.ws-resizing{cursor:col-resize!important;user-select:none!important}
.ws-search-controls{display:flex;align-items:center;gap:8px;padding:6px;border-bottom:1px solid var(--line)}.ws-search-controls input{min-width:0;width:100%;padding:5px 8px}.ws-search-status{flex:none;color:var(--muted);font-size:12px;white-space:nowrap}
.ws-message-scroll{flex:1;min-height:0;overflow:auto}.ws-message-tracks{display:grid;grid-template-columns:max-content max-content max-content minmax(180px,1fr);min-width:100%}
.ws-message-header,.ws-message-list,.ws-message-row{display:grid;grid-template-columns:subgrid;grid-column:1/-1}
.ws-message-header{position:sticky;top:0;z-index:1;background:var(--panel2);font-size:12px;font-weight:600}.ws-message-header>span{padding:3px 5px;border-right:1px solid var(--line);border-bottom:1px solid var(--line)}
.ws-message-list{min-height:0}.ws-message-row{width:auto;min-width:0;text-align:left;border:0;border-bottom:1px solid var(--line);border-radius:0;padding:0;background:var(--bg);color:var(--text);font:12px/1.25 ui-monospace,Consolas,monospace}
.ws-message-row>span{min-width:0;padding:3px 5px;border-right:1px solid var(--line);overflow:hidden}.ws-message-row:hover{background:var(--hover)}.ws-message-row:focus-visible{outline:2px solid var(--accent);outline-offset:-2px;z-index:1}.ws-message-row[aria-selected=true]{background:var(--selected);box-shadow:inset 3px 0 var(--accent)}
.ws-message-row.ws-filtered{height:0;min-height:0;border:0;visibility:hidden;overflow:hidden}.ws-message-empty{grid-column:1/-1;padding:14px;color:var(--muted);text-align:center}
.ws-id,.ws-type,.ws-body{white-space:nowrap}.ws-body{text-align:right;font-variant-numeric:tabular-nums}.ws-body-truncated{color:var(--warn);font-weight:700}.ws-client .ws-arrow{color:var(--direction-client)}.ws-server .ws-arrow{color:var(--direction-server)}.ws-unknown .ws-arrow{color:var(--warn)}.ws-arrow{font-size:16px;font-weight:800;line-height:1}.ws-message-preview{white-space:nowrap;text-overflow:ellipsis}
.ws-detail-content{flex:1;min-width:0;min-height:0;display:flex;flex-direction:column;padding:0 10px 10px}.ws-detail-content>.tab-panels{overflow:auto}.ws-detail-summary{padding:7px 0;color:var(--muted)}.ws-loading{padding:20px;color:var(--muted)}
.active-view-search{display:flex;align-items:center;gap:5px}.active-view-search input{min-width:0;width:100%;padding:5px 8px}.active-view-search button{padding:4px 8px}.active-view-search-status{flex:none;min-width:88px;color:var(--muted);font-size:12px;text-align:right;white-space:nowrap}.active-search-match{background:#9e6a03;color:#fff;border-radius:2px;padding:0}.active-search-match-current{background:#f2cc60;color:#111;outline:2px solid var(--accent);outline-offset:1px}
.http-view-search{margin:6px 14px 0;flex:0 0 auto}.ws-view-search{margin:0 0 6px}
@media(max-width:900px){main{padding:2px}.ws-layout{display:grid;grid-template-columns:1fr;grid-template-rows:minmax(180px,38%) minmax(0,1fr);gap:10px;overflow:hidden}.ws-splitter{display:none}.ws-traffic-pane,.ws-detail-pane{min-width:0}}
</style>
</head>
<body><main>
""");
        AppendHttpSection(html, report.Sessions, report.WebSocketMessages);
        html.Append("""
</main>
<dialog id="httpInspector" aria-labelledby="inspectorTitle" aria-modal="true">
<header class="inspector-header">
<h2 id="inspectorTitle" class="inspector-heading"></h2>
<div class="inspector-nav">
<button type="button" id="inspectorPrev" aria-label="Previous session">&#9664; Previous</button>
<span id="inspectorPosition" class="inspector-position" aria-live="polite"></span>
<button type="button" id="inspectorNext" aria-label="Next session">Next &#9654;</button>
</div>
<button type="button" class="theme-toggle" aria-label="Switch theme" title="Switch theme" aria-pressed="false"><svg aria-hidden="true" viewBox="0 0 24 24"><path d="M9 18h6M10 21h4M8.5 15.5A6 6 0 1 1 15.5 15.5C14.6 16.2 14 17 14 18h-4c0-1-.6-1.8-1.5-2.5Z"/><circle class="theme-bulb-core" cx="12" cy="11" r="2.4"/></svg></button>
<button type="button" id="inspectorOpenTab" aria-label="Open in new tab: this session inspector" title="Open this session inspector in a new tab">Open in new tab</button>
<button type="button" id="inspectorClose" class="inspector-close" aria-label="Close session inspector">&#10005;</button>
<span id="inspectorOpenStatus" class="inspector-open-status" role="status" aria-live="polite"></span>
</header>
<div id="inspectorBody" class="inspector-body"></div>
</dialog>
<script>
(()=>{
const httpRows=[...document.querySelectorAll('#httpTable tbody tr')];
const inspector=document.getElementById('httpInspector');
const inspectorBody=document.getElementById('inspectorBody');
const inspectorTitle=document.getElementById('inspectorTitle');
const inspectorPrev=document.getElementById('inspectorPrev');
const inspectorNext=document.getElementById('inspectorNext');
const inspectorPosition=document.getElementById('inspectorPosition');
const inspectorOpenTab=document.getElementById('inspectorOpenTab');
const inspectorClose=document.getElementById('inspectorClose');
const inspectorOpenStatus=document.getElementById('inspectorOpenStatus');
const reportStatus=document.getElementById('reportStatus');
const THEME_STORAGE_KEY='saz-viewer-theme';
const THEME_VALUES=new Set(['light','dark']);
const systemTheme=matchMedia('(prefers-color-scheme:dark)');
const themeButtons=[...document.querySelectorAll('.theme-toggle')];
function effectiveTheme(){
  const theme=document.documentElement.dataset.theme;
  return THEME_VALUES.has(theme)?theme:(systemTheme.matches?'dark':'light');
}
function syncThemeButtons(){
  const current=effectiveTheme();
  const next=current==='dark'?'light':'dark';
  themeButtons.forEach(button=>{
    const label=`Switch to ${next} theme`;
    button.setAttribute('aria-label',label);
    button.title=label;
    button.setAttribute('aria-pressed',current==='dark'?'true':'false');
  });
}
function applyTheme(value,persist){
  const theme=THEME_VALUES.has(value)?value:'system';
  document.documentElement.dataset.theme=theme;
  syncThemeButtons();
  refreshWebViewThemes();
  if(persist&&THEME_VALUES.has(theme)){try{localStorage.setItem(THEME_STORAGE_KEY,theme)}catch{}}
}
applyTheme(document.documentElement.dataset.theme,false);
themeButtons.forEach(button=>button.addEventListener('click',()=>{
  applyTheme(effectiveTheme()==='dark'?'light':'dark',true);
}));
systemTheme.addEventListener?.('change',()=>{if(document.documentElement.dataset.theme==='system'){syncThemeButtons();refreshWebViewThemes()}});
window.addEventListener('storage',event=>{
  if(event.key===THEME_STORAGE_KEY||event.key===null)applyTheme(event.key===null?null:event.newValue,false);
});
let currentRow=null,originRow=null,renderGeneration=0,inspectorOnly=false,retainSelectionOnClose=false;
const PAYLOAD_VERSION='1';
const PAYLOAD_LIMITS={
  'copy-model':{encoded:48*1024*1024,decoded:32*1024*1024},
  'mapi-protocol':{encoded:48*1024*1024,decoded:32*1024*1024},
  'json-tree':{encoded:12*1024*1024,decoded:8*1024*1024},
  'xml-tree':{encoded:12*1024*1024,decoded:8*1024*1024},
  'websocket-session':{encoded:48*1024*1024,decoded:32*1024*1024}
};
async function decodeCompressedPayload(host,expectedType){
  if(host._payloadData!==undefined){
    if(host._payloadType!==expectedType)throw new Error('cached payload type does not match the requested view');
    return host._payloadData;
  }
  if(host._payloadError)throw host._payloadError;
  if(host._payloadPromise)return host._payloadPromise;
  host._payloadPromise=(async()=>{
    const type=host.dataset.payloadType;
    const version=host.dataset.payloadVersion;
    const encoded=host.dataset.compressedPayload;
    const declared=Number.parseInt(host.dataset.payloadDecodedBytes||'',10);
    const limits=PAYLOAD_LIMITS[expectedType];
    if(!limits||type!==expectedType||version!==PAYLOAD_VERSION)throw new Error('unsupported payload envelope');
    if(!encoded||encoded.length>limits.encoded||encoded.length%4!==0||!/^[A-Za-z0-9+/]*={0,2}$/.test(encoded))throw new Error('invalid or oversized base64 payload');
    if(!Number.isSafeInteger(declared)||declared<0||declared>limits.decoded)throw new Error('invalid or oversized decoded length');
    if(typeof DecompressionStream!=='function')throw new Error('gzip decompression is not supported by this browser');
    let binary;
    try{binary=atob(encoded)}catch{throw new Error('invalid base64 payload')}
    const compressed=new Uint8Array(binary.length);
    for(let index=0;index<binary.length;index++)compressed[index]=binary.charCodeAt(index);
    let reader;
    try{reader=new Blob([compressed]).stream().pipeThrough(new DecompressionStream('gzip')).getReader()}
    catch{throw new Error('compressed payload could not be opened')}
    const chunks=[];let total=0;
    try{
      while(true){
        const result=await reader.read();
        if(result.done)break;
        total+=result.value.byteLength;
        if(total>limits.decoded||total>declared){await reader.cancel();throw new Error('decoded payload exceeds its declared safety limit')}
        chunks.push(result.value);
      }
    }catch(error){
      if(error instanceof Error&&error.message.includes('safety limit'))throw error;
      throw new Error('compressed payload is corrupt or truncated');
    }
    if(total!==declared)throw new Error('decoded payload length does not match its envelope');
    const bytes=new Uint8Array(total);let offset=0;
    chunks.forEach(chunk=>{bytes.set(chunk,offset);offset+=chunk.byteLength});
    let text;
    try{text=new TextDecoder('utf-8',{fatal:true}).decode(bytes)}
    catch{throw new Error('decoded payload is not valid UTF-8')}
    let data;
    try{data=JSON.parse(text)}
    catch{throw new Error('decoded payload is not valid JSON')}
    host.removeAttribute('data-compressed-payload');
    host.removeAttribute('data-payload-type');
    host.removeAttribute('data-payload-version');
    host.removeAttribute('data-payload-decoded-bytes');
    host._payloadType=type;
    host._payloadData=data;
    return data;
  })();
  try{return await host._payloadPromise}
  catch(error){host._payloadError=error;throw error}
  finally{host._payloadPromise=null}
}
function decodeModelBytes(model,key){
  const encoded=model?.[key];
  if(typeof encoded!=='string'||encoded.length>128*1024||encoded.length%4!==0||!/^[A-Za-z0-9+/]*={0,2}$/.test(encoded))throw new Error('retained byte data is invalid or oversized');
  let binary;try{binary=atob(encoded)}catch{throw new Error('retained byte data is not valid base64')}
  if(binary.length>65536)throw new Error('retained byte data exceeds the 64 KiB view limit');
  const bytes=new Uint8Array(binary.length);
  for(let index=0;index<binary.length;index++)bytes[index]=binary.charCodeAt(index);
  return bytes;
}
async function decodeEmbeddedUtf8(model){
  const encoded=model?.authSecret,declared=Number.parseInt(model?.authSecretBytes||'',10);
  if(typeof encoded!=='string'||encoded.length>2*1024*1024||encoded.length%4!==0||!/^[A-Za-z0-9+/]*={0,2}$/.test(encoded)
    ||!Number.isSafeInteger(declared)||declared<0||declared>MAX_COPY_CHARACTERS)throw new Error('authentication secret payload is invalid or oversized');
  let binary;try{binary=atob(encoded)}catch{throw new Error('authentication secret payload is not valid base64')}
  const compressed=new Uint8Array(binary.length);
  for(let index=0;index<binary.length;index++)compressed[index]=binary.charCodeAt(index);
  if(typeof DecompressionStream!=='function')throw new Error('local gzip decompression is unavailable');
  const reader=new Blob([compressed]).stream().pipeThrough(new DecompressionStream('gzip')).getReader();
  const chunks=[];let total=0;
  try{
    while(true){
      const result=await reader.read();if(result.done)break;
      total+=result.value.byteLength;
      if(total>declared||total>MAX_COPY_CHARACTERS){await reader.cancel();throw new Error('authentication secret exceeds its safety limit')}
      chunks.push(result.value);
    }
  }catch(error){
    if(error instanceof Error&&error.message.includes('safety limit'))throw error;
    throw new Error('authentication secret payload is corrupt');
  }
  if(total!==declared)throw new Error('authentication secret length does not match its envelope');
  const bytes=new Uint8Array(total);let offset=0;
  chunks.forEach(chunk=>{bytes.set(chunk,offset);offset+=chunk.byteLength});
  try{return new TextDecoder('utf-8',{fatal:true}).decode(bytes)}
  catch{throw new Error('authentication secret is not valid UTF-8')}
}
function payloadFailureText(error,subject,alternative){
  if(error instanceof Error&&error.message.includes('not supported by this browser')){
    return `${subject} could not be loaded because this browser does not support local gzip decompression. Open the report in a current Microsoft Edge or Google Chrome release.`;
  }
  return `${subject} could not be loaded because its compressed report payload is corrupt, unsupported, or exceeds safety limits.${alternative}`;
}
function safeProtocolText(value){
  let result='';
  for(let index=0;index<value.length;index++){
    const code=value.charCodeAt(index);
    if(code===0){result+='\\0';continue}
    if(code===9){result+='\\t';continue}
    if(code===10){result+='\\n';continue}
    if(code===13){result+='\\r';continue}
    if(code<32||code===127||(code>=128&&code<=159)){
      result+=code<=255?`\\x${code.toString(16).toUpperCase().padStart(2,'0')}`:`\\u${code.toString(16).toUpperCase().padStart(4,'0')}`;
      continue;
    }
    if(code===0x061C||code===0x200E||code===0x200F||code===0x2028||code===0x2029
      ||code===0xFEFF||(code>=0x202A&&code<=0x202E)||(code>=0x2066&&code<=0x2069)
      ||(code>=0xD800&&code<=0xDFFF&&!((code<=0xDBFF)&&index+1<value.length&&value.charCodeAt(index+1)>=0xDC00&&value.charCodeAt(index+1)<=0xDFFF))){
      result+=`\\u${code.toString(16).toUpperCase().padStart(4,'0')}`;
      continue;
    }
    result+=value[index];
    if(code>=0xD800&&code<=0xDBFF)result+=value[++index];
  }
  return result;
}
function protocolDisplayValue(node){
  if(node.value===null||node.value===undefined)return null;
  const text=String(node.value);
  if(node.kind==='Raw'){
    const match=/^([0-9A-Fa-f]*)(?: \.\.\. \[([0-9,]+) more bytes\])?$/.exec(text);
    if(match){
      const pairs=match[1].match(/../g)||[];
      const suffix=match[2]?` \u2026 (${match[2]} more bytes)`:node.length>pairs.length?` \u2026 (${node.length-pairs.length} more bytes)`:'';
      return{value:`Binary (${node.length} byte${node.length===1?'':'s'}): ${pairs.join(' ')}${suffix}`,binary:true};
    }
    return{value:`Binary (${node.length} byte${node.length===1?'':'s'}): ${safeProtocolText(text)}`,binary:true};
  }
  const semantic=/^0x([0-9A-Fa-f]+) \(([^()]+)\)$/.exec(text);
  return{value:semantic?`${safeProtocolText(semantic[2])} = 0x${semantic[1].toUpperCase()}`:safeProtocolText(text),binary:false};
}
async function renderProtocolTree(host,generation){
  if(host._protocolData&&host._protocolRendered)return host._protocolData;
  if(host._protocolPromise)return host._protocolPromise;
  host._protocolLoading=true;
  host._protocolRendered=false;
  const copyButton=host.closest('[role="tabpanel"]')?.querySelector('.copy-button[data-copy-kind="mapi"]');
  if(copyButton){copyButton.disabled=true;copyButton.setAttribute('aria-disabled','true');copyButton.setAttribute('aria-busy','true')}
  const buildToken={};host._protocolBuildToken=buildToken;
  const promise=(async()=>{
   try{
    const data=host._protocolData??await decodeCompressedPayload(host,'mapi-protocol');
    if(generation!==renderGeneration||host._protocolBuildToken!==buildToken||host.closest('.tab-panel.hidden'))return;
    host._protocolData=data;
    const toolbar=document.createElement('div');toolbar.className='protocol-toolbar';
    const expand=document.createElement('button');expand.type='button';expand.textContent='Expand all';
    const collapse=document.createElement('button');collapse.type='button';collapse.textContent='Collapse all';
    const status=document.createElement('span');status.className='protocol-load-status';status.setAttribute('role','status');status.setAttribute('aria-live','polite');status.textContent='Loading tree\u2026';
    toolbar.append(expand,collapse,status);
    const tree=document.createElement('div');tree.className='protocol-tree tree-view';tree.setAttribute('role','tree');tree.setAttribute('aria-label','MAPI protocol structure');
    const loading=document.createElement('div');loading.className='protocol-tree-loading';loading.textContent='Expanding retained protocol nodes\u2026';tree.append(loading);
    host.replaceChildren(toolbar,tree);
    let desiredExpanded=true,activeItem=null,rendered=0;
    const queue=[{node:data.root,parent:tree}];
    function isVisible(item){
      let current=item.parentElement;
      while(current&&current!==tree){if(current.classList.contains('tree-group')&&current.hidden)return false;current=current.parentElement}
      return true;
    }
    function visibleItems(){return [...tree.querySelectorAll('.tree-item')].filter(isVisible)}
    function ownerItem(item){const group=item.parentElement;return group?.classList.contains('tree-group')?group.parentElement:null}
    function setActive(item,focus){
      if(!item)return;
      if(activeItem&&activeItem!==item)activeItem.tabIndex=-1;
      item.tabIndex=0;activeItem=item;if(focus)item.focus();
    }
    function restoreActive(focus){
      if(activeItem&&isVisible(activeItem))return;
      let candidate=activeItem;
      while(candidate&&!isVisible(candidate))candidate=ownerItem(candidate);
      setActive(candidate||visibleItems()[0],focus);
    }
    function toggle(item,expanded){
      const group=item.querySelector(':scope>.tree-group');if(!group)return;
      const next=expanded??item.getAttribute('aria-expanded')!=='true';
      item.setAttribute('aria-expanded',String(next));group.hidden=!next;
      const caret=item.querySelector(':scope>.tree-row>.tree-caret');if(caret)caret.textContent=next?'\u2212':'+';
    }
    function setExpanded(expanded){
      const hadFocus=tree.contains(document.activeElement);desiredExpanded=expanded;
      tree.querySelectorAll('.tree-item[aria-expanded]').forEach(item=>toggle(item,expanded));
      if(!expanded)restoreActive(hadFocus);
    }
    tree._setDesiredExpanded=setExpanded;tree._restoreActive=restoreActive;host._setDesiredExpanded=setExpanded;
    function handleKey(event,item){
      if((event.key==='Enter'||event.key===' ')&&item.hasAttribute('aria-expanded')){event.preventDefault();toggle(item);return}
      if(event.altKey||event.ctrlKey||event.metaKey||event.shiftKey)return;
      const items=visibleItems(),index=items.indexOf(item);
      if(event.key==='ArrowDown'&&index>=0&&index<items.length-1){event.preventDefault();setActive(items[index+1],true)}
      else if(event.key==='ArrowUp'&&index>0){event.preventDefault();setActive(items[index-1],true)}
      else if(event.key==='Home'&&items.length){event.preventDefault();setActive(items[0],true)}
      else if(event.key==='End'&&items.length){event.preventDefault();setActive(items[items.length-1],true)}
      else if(event.key==='ArrowRight'&&item.hasAttribute('aria-expanded')){
        event.preventDefault();
        if(item.getAttribute('aria-expanded')!=='true')toggle(item,true);
        else{const child=item.querySelector(':scope>.tree-group>.tree-item');if(child)setActive(child,true)}
      }else if(event.key==='ArrowLeft'){
        const owner=ownerItem(item);
        if(item.getAttribute('aria-expanded')==='true'){event.preventDefault();toggle(item,false)}
        else if(owner){event.preventDefault();setActive(owner,true)}
      }
    }
    function appendNode(node,parent){
      if(!node||typeof node.name!=='string'||typeof node.kind!=='string'||!Array.isArray(node.children))throw new Error('invalid protocol node');
      const hasChildren=node.children.length>0;
      const item=document.createElement('div');item.className='tree-item protocol-tree-item';item.setAttribute('role','treeitem');item.tabIndex=-1;
      if(hasChildren)item.setAttribute('aria-expanded',String(desiredExpanded));
      const row=document.createElement('div');row.className='tree-row protocol-row';
      const caret=document.createElement('span');caret.className='tree-caret'+(hasChildren?'':' tree-caret-leaf');caret.setAttribute('aria-hidden','true');caret.textContent=hasChildren?'\u2212':'\u00b7';
      const main=document.createElement('span');main.className='protocol-main';
      const name=document.createElement('span');name.className='protocol-name';name.textContent=safeProtocolText(node.name);main.append(name);
      const display=protocolDisplayValue(node);
      if(display){
        const separator=document.createElement('span');separator.className='protocol-separator';separator.textContent=': ';
        const value=document.createElement('span');value.className='protocol-value'+(display.binary?' protocol-value-binary':'');value.textContent=display.value;main.append(separator,value);
      }
      const technical=document.createElement('span');technical.className='protocol-technical';
      const kind=document.createElement('span');kind.className='protocol-kind';kind.textContent=node.kind;
      const location=document.createElement('span');location.textContent=` @${node.offset} +${node.length}`;
      technical.append(kind,location);row.append(caret,main,technical);item.append(row);
      item.setAttribute('aria-label',`${safeProtocolText(node.name)}${display?`: ${display.value}`:''}; ${node.kind}; offset ${node.offset}; length ${node.length}`);
      if(hasChildren){
        const group=document.createElement('div');group.className='tree-group';group.setAttribute('role','group');group.hidden=!desiredExpanded;item.append(group);
        node.children.forEach(child=>queue.push({node:child,parent:group}));
      }
      parent.append(item);rendered++;
      if(!activeItem)setActive(item,false);
      row.addEventListener('click',event=>{event.stopPropagation();setActive(item,true);if(hasChildren)toggle(item)});
      item.addEventListener('keydown',event=>{if(event.target===item)handleKey(event,item)});
    }
    expand.addEventListener('click',()=>setExpanded(true));
    collapse.addEventListener('click',()=>setExpanded(false));
    await new Promise(resolve=>{
      function step(){
        if(generation!==renderGeneration||host._protocolBuildToken!==buildToken||host.closest('.tab-panel.hidden')){
          host._protocolBuildToken=null;resolve();return;
        }
        try{
          let processed=0;
          while(processed<150&&queue.length){const entry=queue.shift();appendNode(entry.node,entry.parent);processed++}
        }catch(error){
          host._protocolBuildToken=null;host._protocolRendered=true;tree.replaceChildren();
          const warning=document.createElement('div');warning.className='warning';warning.textContent='Protocol tree could not be rendered because its decoded structure is invalid. Regenerate the report with the current SAZ Viewer.';tree.append(warning);
          status.textContent='Tree unavailable';resolve();return;
        }
        status.textContent=queue.length?`Loading\u2026 ${rendered.toLocaleString()} nodes`:`${rendered.toLocaleString()} nodes`;
        if(queue.length)requestAnimationFrame(step);
        else{loading.remove();host._protocolBuildToken=null;host._protocolRendered=true;resolve()}
      }
      requestAnimationFrame(step);
    });
    if(copyButton&&host._protocolRendered){copyButton.disabled=false;copyButton.removeAttribute('aria-disabled');copyButton.removeAttribute('aria-busy')}
    return data;
  }catch(error){
    host.textContent=payloadFailureText(error,'Protocol tree',' Regenerate the report with the current SAZ Viewer.');
    host.className='protocol-block warning';
    if(copyButton){copyButton.disabled=true;copyButton.setAttribute('aria-disabled','true');copyButton.removeAttribute('aria-busy')}
  }finally{
    if(host._protocolPromise===promise)host._protocolLoading=false;
  }
  })();
  host._protocolPromise=promise;
  try{return await promise}
  finally{if(host._protocolPromise===promise)host._protocolPromise=null}
}
function appendStatus(parent,text){
  const status=document.createElement('div');status.className='tree-status';status.setAttribute('role','treeitem');status.tabIndex=-1;status.textContent=text;
  parent.append(status);
  return status;
}
function treeLabelText(node,kind){
  if(kind==='json'){
    const prefix=node.isIndex?`[${node.name}] `:(node.name!==null&&node.name!==undefined?`${node.name}: `:'');
    if(node.kind==='object')return `${prefix}{} (${node.count??0} propert${node.count===1?'y':'ies'})`;
    if(node.kind==='array')return `${prefix}[] (${node.count??0} item${node.count===1?'':'s'})`;
    if(node.kind==='string')return `${prefix}"${node.value??''}"`;
    if(node.kind==='null')return `${prefix}null`;
    return `${prefix}${node.value}`;
  }
  if(node.kind==='attribute')return `@${node.name}="${node.value??''}"`;
  if(node.kind==='element'){
    const suffix=node.count!==null&&node.count!==undefined?` (${node.count} child${node.count===1?'':'ren'})`:'';
    return `<${node.name}>${suffix}`;
  }
  if(node.kind==='text')return `"${node.value??''}"`;
  if(node.kind==='cdata')return `CDATA[[${node.value??''}]]`;
  if(node.kind==='comment')return `<!--${node.value??''}-->`;
  return node.kind;
}
function buildTree(host,rootNode,kind,generation){
  const buildToken={};
  host._treeBuildToken=buildToken;
  host._treeRendered=false;
  const tree=document.createElement('div');tree.className='tree-view';tree.setAttribute('role','tree');
  tree.setAttribute('aria-label',kind==='json'?'JSON structure':'XML structure');
  host.replaceChildren(tree);
  // True roving tabindex: exactly one treeitem/status stop in this tree has tabIndex 0 at a
  // time (the rest are -1, still individually focusable programmatically for arrow-key
  // traversal, but out of the page Tab order). This keeps trees with thousands of nodes (or many
  // truncation/status entries) from creating thousands of Tab stops.
  let activeItem=null;
  function isVisible(el){
    let node=el.parentElement;
    while(node&&node!==tree){
      if(node.classList.contains('tree-group')&&node.hidden)return false;
      node=node.parentElement;
    }
    return true;
  }
  function stops(){return [...tree.querySelectorAll('.tree-item,.tree-status')]}
  function visibleStops(){return stops().filter(isVisible)}
  function ownerItem(el){
    const container=el.parentElement;
    return container&&container.classList.contains('tree-group')?container.parentElement:null;
  }
  function setActive(item,focus){
    if(!item)return;
    if(activeItem&&activeItem!==item)activeItem.tabIndex=-1;
    item.tabIndex=0;activeItem=item;
    if(focus)item.focus();
  }
  function restoreVisibleActive(focus){
    if(activeItem&&isVisible(activeItem))return;
    let candidate=activeItem;
    while(candidate&&!isVisible(candidate))candidate=ownerItem(candidate);
    if(!candidate)candidate=visibleStops()[0];
    setActive(candidate,focus);
  }
  // Desired expansion state is consulted by appendItem for nodes that haven't been created yet
  // (still queued for a future batch), so Expand/Collapse all takes effect immediately for
  // already-rendered nodes and is honored by the rest of a still-streaming large tree too.
  let desiredExpanded=true;
  host._setDesiredExpanded=value=>{
    const focusWasInTree=tree.contains(document.activeElement);
    desiredExpanded=value;
    tree.querySelectorAll('.tree-item[aria-expanded]').forEach(item=>{
      item.setAttribute('aria-expanded',String(value));
      const group=item.querySelector(':scope>.tree-group');if(group)group.hidden=!value;
      const caret=item.querySelector(':scope>.tree-row>.tree-caret');if(caret)caret.textContent=value?'\u25be':'\u25b8';
    });
    if(!value)restoreVisibleActive(focusWasInTree);
  };
  function handleTreeKeydown(event,item){
    const key=event.key;
    if(key==='Enter'||key===' '){
      if(item._toggle){event.preventDefault();item._toggle()}
      return;
    }
    if(event.altKey||event.ctrlKey||event.metaKey||event.shiftKey)return;
    if(key==='ArrowDown'){
      const list=visibleStops(),index=list.indexOf(item);
      if(index>=0&&index<list.length-1){event.preventDefault();setActive(list[index+1],true)}
      return;
    }
    if(key==='ArrowUp'){
      const list=visibleStops(),index=list.indexOf(item);
      if(index>0){event.preventDefault();setActive(list[index-1],true)}
      return;
    }
    if(key==='ArrowRight'){
      if(!item._toggle)return;
      event.preventDefault();
      const expanded=item.getAttribute('aria-expanded')==='true';
      if(!expanded){item._toggle();return}
      const group=item.querySelector(':scope>.tree-group');
      const first=group&&[...group.children].find(child=>child.classList.contains('tree-item')||child.classList.contains('tree-status'));
      if(first)setActive(first,true);
      return;
    }
    if(key==='ArrowLeft'){
      event.preventDefault();
      if(item._toggle&&item.getAttribute('aria-expanded')==='true'){item._toggle();return}
      const owner=ownerItem(item);
      if(owner)setActive(owner,true);
      return;
    }
    if(key==='Home'){
      const list=visibleStops();
      if(list.length){event.preventDefault();setActive(list[0],true)}
      return;
    }
    if(key==='End'){
      const list=visibleStops();
      if(list.length){event.preventDefault();setActive(list[list.length-1],true)}
    }
  }
  const queue=[];
  if(rootNode.kind==='document'){
    (rootNode.children||[]).forEach(child=>queue.push({node:child,parent:tree,depth:0}));
    if(rootNode.omitted>0)queue.push({status:true,parent:tree,text:`+${rootNode.omitted} more not shown here \u2014 use Pretty Text to view the full content.`});
  }else{
    queue.push({node:rootNode,parent:tree,depth:0});
  }
  const BATCH=150;
  function pushChildren(node,group,depth){
    const kids=[];
    if(Array.isArray(node.attrs))kids.push(...node.attrs);
    if(Array.isArray(node.children))kids.push(...node.children);
    kids.forEach(kid=>queue.push({node:kid,parent:group,depth:depth+1}));
    // Status entries are queued (not appended immediately) so they always land after their real
    // sibling nodes in the DOM, even though those siblings are themselves appended later, in a
    // future batch.
    if(node.omitted>0)queue.push({status:true,parent:group,text:`+${node.omitted} more not shown here \u2014 use Pretty Text to view the full content.`});
    if(node.depthLimited)queue.push({status:true,parent:group,text:'Maximum nesting depth reached; deeper content is not shown here \u2014 use Pretty Text to view the full content.'});
  }
  function appendStatusEntry(parent,text){
    const status=appendStatus(parent,text);
    status.addEventListener('keydown',event=>{if(event.target===status)handleTreeKeydown(event,status)});
    status.addEventListener('click',()=>setActive(status,true));
    if(!activeItem)setActive(status,false);
  }
  function appendItem(node,parent,depth){
    const hasKids=(Array.isArray(node.children)&&node.children.length>0)||(Array.isArray(node.attrs)&&node.attrs.length>0)||node.omitted>0||node.depthLimited;
    const item=document.createElement('div');item.className='tree-item';item.setAttribute('role','treeitem');item.tabIndex=-1;
    if(hasKids)item.setAttribute('aria-expanded',String(desiredExpanded));
    const row=document.createElement('div');row.className='tree-row';
    const caret=document.createElement('span');caret.className='tree-caret'+(hasKids?'':' tree-caret-leaf');caret.setAttribute('aria-hidden','true');caret.textContent=hasKids?(desiredExpanded?'\u25be':'\u25b8'):'\u2022';
    row.append(caret);
    const label=document.createElement('span');label.className='tree-label tree-label-'+node.kind;label.textContent=treeLabelText(node,kind);
    row.append(label);
    if(node.truncated){const truncated=document.createElement('span');truncated.className='tree-truncated';truncated.textContent=' (truncated)';row.append(truncated)}
    item.append(row);
    let group=null;
    if(hasKids){
      group=document.createElement('div');group.className='tree-group';group.setAttribute('role','group');
      group.hidden=!desiredExpanded;
      item.append(group);
      pushChildren(node,group,depth);
    }
    parent.append(item);
    if(!activeItem)setActive(item,false);
    if(hasKids){
      const toggle=()=>{
        const expanded=item.getAttribute('aria-expanded')==='true';
        item.setAttribute('aria-expanded',String(!expanded));
        group.hidden=expanded;
        caret.textContent=expanded?'\u25b8':'\u25be';
      };
      item._toggle=toggle;
      row.addEventListener('click',event=>{event.stopPropagation();setActive(item,true);toggle()});
    }else{
      row.addEventListener('click',event=>{event.stopPropagation();setActive(item,true)});
    }
    item.addEventListener('keydown',event=>{
      if(event.target!==item)return;
      handleTreeKeydown(event,item);
    });
  }
  function step(){
    if(generation!==renderGeneration||host._treeBuildToken!==buildToken)return;
    if(host.classList.contains('hidden')||host.closest('.tab-panel.hidden')){
      host._treeBuildToken=null;
      return;
    }
    try{
      let processed=0;
      while(processed<BATCH&&queue.length){
        const entry=queue.shift();
        if(entry.status)appendStatusEntry(entry.parent,entry.text);
        else appendItem(entry.node,entry.parent,entry.depth);
        processed++;
      }
    }catch{
      queue.length=0;
      host.textContent='Tree view could not be rendered because its decoded structure is invalid. Use Pretty Text or regenerate the report with the current SAZ Viewer.';
      host.className='tree-subview warning';
      host._treeBuildToken=null;
      host._treeRendered=true;
      return;
    }
    if(queue.length&&generation===renderGeneration)requestAnimationFrame(step);
    else{host._treeBuildToken=null;host._treeRendered=true}
  }
  step();
}
function setAllExpanded(container,expanded){
  if(!container)return;
  if(typeof container._setDesiredExpanded==='function'){container._setDesiredExpanded(expanded);return}
  const focusWasInTree=container.contains(document.activeElement);
  container.querySelectorAll('.tree-item[aria-expanded]').forEach(item=>{
    item.setAttribute('aria-expanded',String(expanded));
    const group=item.querySelector(':scope>.tree-group');
    if(group)group.hidden=!expanded;
    const caret=item.querySelector(':scope>.tree-row>.tree-caret');
    if(caret)caret.textContent=expanded?'\u25be':'\u25b8';
  });
  if(!expanded){
    const current=container.querySelector('.tree-item[tabindex="0"],.tree-status[tabindex="0"]');
    if(current&&current.closest('.tree-group[hidden]')){
      let candidate=current.parentElement?.closest('.tree-item');
      while(candidate&&candidate.closest('.tree-group[hidden]'))candidate=candidate.parentElement?.closest('.tree-item');
      candidate??=container.querySelector('.tree-view>.tree-item,.tree-view>.tree-status');
      current.tabIndex=-1;
      if(candidate){candidate.tabIndex=0;if(focusWasInTree)candidate.focus()}
    }
  }
}
async function renderValueTree(host,generation){
  if(host._treeRendered||host._treeLoading||host._treeBuildToken)return;
  host._treeLoading=true;
  const kind=(host._payloadType||host.dataset.payloadType)==='json-tree'?'json':'xml';
  const controls=host.closest('.structured-body')?.querySelectorAll('.tree-expand-all,.tree-collapse-all')||[];
  controls.forEach(control=>control.disabled=true);
  try{
    const payload=await decodeCompressedPayload(host,`${kind}-tree`);
    if(generation!==renderGeneration||host.classList.contains('hidden')||host.closest('.tab-panel.hidden'))return;
    buildTree(host,payload,kind,generation);
    controls.forEach(control=>control.disabled=false);
  }catch(error){
    host.textContent=payloadFailureText(error,'Tree view',' Use Pretty Text or regenerate the report with the current SAZ Viewer.');
    host.className='tree-subview warning';
    host._treeRendered=true;
  }finally{
    host._treeLoading=false;
  }
}
function wsElement(tag,className,text){
  const element=document.createElement(tag);
  if(className)element.className=className;
  if(text!==undefined&&text!==null)element.textContent=String(text);
  return element;
}
function wsCopyToolbar(accessibleName,description,text){
  const toolbar=wsElement('div','copy-toolbar');
  const button=wsElement('button','copy-button','Copy');
  button.type='button';button.dataset.copyKey='websocket';button.dataset.copyDescription=description;
  button.setAttribute('aria-label',accessibleName);button._copyText=text;
  const status=wsElement('span','copy-status');status.setAttribute('role','status');status.setAttribute('aria-live','polite');
  toolbar.append(button,status);return toolbar;
}
function wsTab(key,label,enabled,selected,suffix){
  const button=wsElement('button','',label);button.type='button';button.id=`ws-${key}-tab-${suffix}`;
  button.setAttribute('role','tab');button.dataset.tab=key;button.setAttribute('aria-controls',`ws-${key}-panel-${suffix}`);
  button.setAttribute('aria-selected',String(selected&&enabled));button.tabIndex=selected&&enabled?0:-1;
  button.disabled=!enabled;if(!enabled)button.setAttribute('aria-disabled','true');
  return button;
}
function wsPanel(key,suffix){
  const panel=wsElement('div','tab-panel');panel.id=`ws-${key}-panel-${suffix}`;
  panel.setAttribute('role','tabpanel');panel.setAttribute('aria-labelledby',`ws-${key}-tab-${suffix}`);panel.tabIndex=0;
  return panel;
}
let webSocketSplitRatio=.38;
function setupWebSocketSplitter(layout,traffic,detail,splitter){
  const MIN_LEFT=280,MIN_RIGHT=320;
  let draggingPointer=null;
  function metrics(){
    const style=getComputedStyle(layout);
    const gap=Number.parseFloat(style.columnGap||style.gap)||0;
    const available=Math.max(1,layout.clientWidth-splitter.offsetWidth-(2*gap));
    return{available,minLeft:Math.min(MIN_LEFT,available),maxLeft:Math.max(Math.min(MIN_LEFT,available),available-MIN_RIGHT)};
  }
  function applyRatio(ratio,remember){
    if(matchMedia('(max-width:900px)').matches){
      splitter.setAttribute('aria-disabled','true');return;
    }
    splitter.removeAttribute('aria-disabled');
    const size=metrics();
    const left=Math.min(size.maxLeft,Math.max(size.minLeft,size.available*ratio));
    const applied=left/size.available;
    layout.style.setProperty('--ws-left',`${left}px`);
    if(remember)webSocketSplitRatio=applied;
    const min=Math.round((size.minLeft/size.available)*100),max=Math.round((size.maxLeft/size.available)*100),now=Math.round(applied*100);
    splitter.setAttribute('aria-valuemin',String(min));splitter.setAttribute('aria-valuemax',String(max));
    splitter.setAttribute('aria-valuenow',String(now));splitter.setAttribute('aria-valuetext',`Left pane ${now} percent; right pane ${100-now} percent`);
  }
  function applyClientX(clientX){
    const size=metrics(),bounds=layout.getBoundingClientRect();
    applyRatio((clientX-bounds.left)/size.available,true);
  }
  splitter.addEventListener('pointerdown',event=>{
    if(event.button!==0||matchMedia('(max-width:900px)').matches)return;
    event.preventDefault();draggingPointer=event.pointerId;splitter.setPointerCapture(event.pointerId);
    document.body.classList.add('ws-resizing');applyClientX(event.clientX);
  });
  splitter.addEventListener('pointermove',event=>{if(event.pointerId===draggingPointer)applyClientX(event.clientX)});
  function finishPointer(event){
    if(event.pointerId!==draggingPointer)return;
    draggingPointer=null;document.body.classList.remove('ws-resizing');
    if(splitter.hasPointerCapture(event.pointerId))splitter.releasePointerCapture(event.pointerId);
  }
  splitter.addEventListener('pointerup',finishPointer);
  splitter.addEventListener('pointercancel',finishPointer);
  splitter.addEventListener('lostpointercapture',()=>{draggingPointer=null;document.body.classList.remove('ws-resizing')});
  splitter.addEventListener('keydown',event=>{
    if(matchMedia('(max-width:900px)').matches)return;
    const size=metrics(),current=traffic.getBoundingClientRect().width;
    let next=current;
    if(event.key==='ArrowLeft')next=current-(event.shiftKey?40:12);
    else if(event.key==='ArrowRight')next=current+(event.shiftKey?40:12);
    else if(event.key==='Home')next=size.minLeft;
    else if(event.key==='End')next=size.maxLeft;
    else return;
    event.preventDefault();applyRatio(next/size.available,true);
  });
  const observer=new ResizeObserver(()=>{
    if(!layout.isConnected){observer.disconnect();return}
    applyRatio(webSocketSplitRatio,false);
  });
  observer.observe(layout);
  applyRatio(webSocketSplitRatio,false);
}
function removeActiveSearchMarks(root,matchClass){
  root.querySelectorAll(`mark.${matchClass}`).forEach(mark=>mark.replaceWith(document.createTextNode(mark.textContent||'')));
  root.normalize();
}
function foldActiveSearchText(text){
  const folded=text.toLowerCase();
  let offset=0;const starts=[],ends=[];
  for(const character of text){
    const lower=character.toLowerCase(),end=offset+character.length;
    for(let index=0;index<lower.length;index++){starts.push(offset);ends.push(end)}
    offset=end;
  }
  return{folded,starts,ends};
}
function activeSearchTextNodeAllowed(node){
  const parent=node.parentElement;
  return !!parent&&!parent.closest('.active-view-search,.copy-toolbar,.tree-toolbar,.protocol-toolbar,.tab-strip,button,.sr-only,.tab-empty,[aria-hidden="true"]');
}
function highlightActiveSearchRoots(roots,query,matchClass){
  const MAX_MATCHES=5000,segmentsByNode=new Map(),matches=[];
  let capped=false;
  outer:for(const root of roots){
    const walker=document.createTreeWalker(root,NodeFilter.SHOW_TEXT,{acceptNode:node=>activeSearchTextNodeAllowed(node)?NodeFilter.FILTER_ACCEPT:NodeFilter.FILTER_REJECT});
    const nodes=[];let text='',node;
    while((node=walker.nextNode())){nodes.push({node,start:text.length,end:text.length+node.data.length});text+=node.data}
    const folded=foldActiveSearchText(text);let offset=0,nodeIndex=0;
    while(offset<=folded.folded.length-query.length){
      const foldedStart=folded.folded.indexOf(query,offset);if(foldedStart<0)break;
      if(matches.length>=MAX_MATCHES){capped=true;break outer}
      const found=folded.starts[foldedStart],end=folded.ends[foldedStart+query.length-1];
      const matchIndex=matches.length;matches.push([]);
      while(nodeIndex<nodes.length&&nodes[nodeIndex].end<=found)nodeIndex++;
      for(let scan=nodeIndex;scan<nodes.length&&nodes[scan].start<end;scan++){
        const entry=nodes[scan];
        const start=Math.max(found,entry.start),stop=Math.min(end,entry.end);
        if(start>=stop)continue;
        const list=segmentsByNode.get(entry.node)||[];
        list.push({start:start-entry.start,end:stop-entry.start,index:matchIndex});segmentsByNode.set(entry.node,list);
      }
      offset=foldedStart+query.length;
    }
  }
  segmentsByNode.forEach((segments,node)=>{
    const fragment=document.createDocumentFragment();let offset=0;
    segments.forEach(segment=>{
      if(segment.start>offset)fragment.append(document.createTextNode(node.data.slice(offset,segment.start)));
      const mark=document.createElement('mark');mark.className=`active-search-match ${matchClass}`;mark.dataset.matchIndex=String(segment.index);
      mark.textContent=node.data.slice(segment.start,segment.end);fragment.append(mark);matches[segment.index].push(mark);offset=segment.end;
    });
    if(offset<node.data.length)fragment.append(document.createTextNode(node.data.slice(offset)));
    node.replaceWith(fragment);
  });
  return{matches,capped};
}
function expandedTreeCaret(item,expanded){return item.closest('.protocol-tree')?(expanded?'\u2212':'+'):(expanded?'\u25be':'\u25b8')}
function snapshotValueTree(tree){
  const snapshot=[...tree.querySelectorAll('.tree-item[aria-expanded]')].map(item=>({item,expanded:item.getAttribute('aria-expanded')}));
  return()=>{
    const hadFocus=tree.contains(document.activeElement);
    snapshot.forEach(entry=>{
      if(!entry.item.isConnected)return;
      entry.item.setAttribute('aria-expanded',entry.expanded);
      const group=entry.item.querySelector(':scope>.tree-group');if(group)group.hidden=entry.expanded!=='true';
      const caret=entry.item.querySelector(':scope>.tree-row>.tree-caret');if(caret)caret.textContent=expandedTreeCaret(entry.item,entry.expanded==='true');
    });
    tree._restoreActive?.(hadFocus);
  };
}
function revealValueTreeMatches(matches){
  matches.flat().forEach(mark=>{
    let item=mark.closest('.tree-item');
    while(item){
      if(item.hasAttribute('aria-expanded')){
        item.setAttribute('aria-expanded','true');
        const group=item.querySelector(':scope>.tree-group');if(group)group.hidden=false;
        const caret=item.querySelector(':scope>.tree-row>.tree-caret');if(caret)caret.textContent=expandedTreeCaret(item,true);
      }
      item=item.parentElement?.closest('.tree-item');
    }
  });
}
function snapshotDetails(details){
  const snapshot=details.map(item=>({item,open:item.open}));
  return()=>snapshot.forEach(entry=>{if(entry.item.isConnected)entry.item.open=entry.open});
}
function revealDetailMatches(matches){
  matches.flat().forEach(mark=>{
    let detail=mark.parentElement?.closest('details');
    while(detail){detail.open=true;detail=detail.parentElement?.closest('details')}
  });
}
function setupActiveViewSearch(options){
  const toolbar=document.createElement('div');toolbar.className=`active-view-search ${options.toolbarClass}`;
  const input=document.createElement('input');input.className=options.inputClass;input.type='search';input.maxLength=4096;
  input.placeholder=options.placeholder;input.setAttribute('aria-label',options.inputLabel);
  const previous=document.createElement('button');previous.className=options.previousClass;previous.type='button';previous.textContent='Previous';previous.setAttribute('aria-label',options.previousLabel);
  const next=document.createElement('button');next.className=options.nextClass;next.type='button';next.textContent='Next';next.setAttribute('aria-label',options.nextLabel);
  const status=document.createElement('span');status.className=`active-view-search-status ${options.statusClass}`;status.textContent='0 matches';status.setAttribute('role','status');status.setAttribute('aria-live','polite');
  previous.disabled=true;next.disabled=true;toolbar.append(input,previous,next,status);
  options.insertToolbar(toolbar);
  let runToken=0,current=0,matches=[],restoreTarget=null;
  function restore(){
    if(!restoreTarget)return;
    restoreTarget();restoreTarget=null;
  }
  function clear(){
    restore();removeActiveSearchMarks(options.searchRoot,options.matchClass);matches=[];current=0;previous.disabled=true;next.disabled=true;status.textContent='0 matches';
  }
  function showCurrent(){
    options.searchRoot.querySelectorAll(`.${options.currentClass}`).forEach(mark=>mark.classList.remove('active-search-match-current',options.currentClass));
    if(!matches.length)return;
    matches[current].forEach(mark=>mark.classList.add('active-search-match-current',options.currentClass));
    matches[current][0]?.scrollIntoView({block:'nearest',inline:'nearest'});
    const total=`${matches.length}${toolbar.dataset.capped==='true'?'+':''}`;
    status.textContent=`${current+1} of ${total} matches${toolbar.dataset.capped==='true'?' (capped)':''}`;
  }
  async function run(){
    const token=++runToken;clear();toolbar.dataset.capped='false';
    const query=foldActiveSearchText(input.value.trim()).folded;if(!query)return;
    const target=await options.resolveTarget(token,()=>runToken);
    if(!target||token!==runToken||options.generation!==renderGeneration||!options.searchRoot.isConnected)return;
    restoreTarget=target.snapshot?.()||null;
    const result=highlightActiveSearchRoots(target.roots,query,options.matchClass);matches=result.matches;toolbar.dataset.capped=String(result.capped);
    if(matches.length)target.reveal?.(matches);
    previous.disabled=!matches.length;next.disabled=!matches.length;
    if(matches.length)showCurrent();else status.textContent='0 matches';
  }
  function move(delta){
    if(!matches.length)return;
    current=(current+delta+matches.length)%matches.length;showCurrent();
  }
  input.addEventListener('input',run);
  input.addEventListener('keydown',event=>{
    if(event.defaultPrevented||event.key!=='Enter'||event.isComposing||event.keyCode===229||event.ctrlKey||event.altKey||event.metaKey)return;
    event.preventDefault();move(event.shiftKey?-1:1);input.focus({preventScroll:true});
  });
  previous.addEventListener('click',()=>move(-1));
  next.addEventListener('click',()=>move(1));
  const viewChangeHandler=options.clearOnViewChange?()=>reset():run;
  options.viewEventTarget.addEventListener('saz-view-change',viewChangeHandler);
  function reset(){runToken++;input.value='';toolbar.dataset.capped='false';clear()}
  function dispose(){reset();options.viewEventTarget.removeEventListener('saz-view-change',viewChangeHandler)}
  return{toolbar,input,run,reset,dispose};
}
function setupWebSocketViewSearch(content,generation){
  const tablist=content.querySelector(':scope>.tab-strip');
  const controller=setupActiveViewSearch({
    toolbarClass:'ws-view-search',inputClass:'ws-view-search-input',previousClass:'ws-view-search-prev',nextClass:'ws-view-search-next',statusClass:'ws-view-search-status',
    placeholder:'Search selected payload view...',inputLabel:'Search selected WebSocket payload view',
    previousLabel:'Previous payload search match',nextLabel:'Next payload search match',
    matchClass:'ws-search-match',currentClass:'ws-search-match-current',
    searchRoot:content,viewEventTarget:content,generation,clearOnViewChange:false,
    insertToolbar:toolbar=>content.insertBefore(toolbar,tablist),
    resolveTarget:async(token,currentToken)=>{
      const tab=tablist.querySelector('[role="tab"][aria-selected="true"]');
      const panel=tab?content.querySelector(`#${CSS.escape(tab.getAttribute('aria-controls'))}`):null;
      if(!panel)return null;
      if(tab.dataset.tab==='json'){
        const tree=panel.querySelector('.tree-subview'),pretty=panel.querySelector('.pretty-subview');
        if(pretty&&!pretty.classList.contains('hidden'))return{roots:[pretty]};
        if(!tree)return null;
        renderValueTree(tree,generation);
        while(!tree._treeRendered){
          await new Promise(resolve=>requestAnimationFrame(resolve));
          if(token!==currentToken()||generation!==renderGeneration||!content.isConnected)return null;
        }
        return{roots:[...tree.querySelectorAll('.tree-label,.tree-truncated')],snapshot:()=>snapshotValueTree(tree),reveal:revealValueTreeMatches};
      }
      if(tab.dataset.tab==='text'){
        const target=panel.querySelector('.ws-text-view');return target?{roots:[target]}:null;
      }
      const target=panel.querySelector('.ws-raw-view');return target?{roots:[target]}:null;
    }
  });
  content._clearWebSocketViewSearch=controller.reset;
}
function setupHttpViewSearch(root,generation){
  const primaryTabs=root.querySelector(':scope>.primary-tab-strip');
  const primaryPanels=root.querySelector(':scope>.primary-panels');
  if(!primaryTabs||!primaryPanels)return;
  const controller=setupActiveViewSearch({
    toolbarClass:'http-view-search',inputClass:'http-view-search-input',previousClass:'http-view-search-prev',nextClass:'http-view-search-next',statusClass:'http-view-search-status',
    placeholder:'Search active Request/Response view...',inputLabel:'Search active Request or Response view',
    previousLabel:'Previous active-view search match',nextLabel:'Next active-view search match',
    matchClass:'http-search-match',currentClass:'http-search-match-current',
    searchRoot:root,viewEventTarget:root,generation,clearOnViewChange:true,
    insertToolbar:toolbar=>root.insertBefore(toolbar,primaryPanels),
    resolveTarget:async(token,currentToken)=>{
      const primaryTab=primaryTabs.querySelector('[role="tab"][aria-selected="true"]');
      const primaryPanel=primaryTab?root.querySelector(`#${CSS.escape(primaryTab.getAttribute('aria-controls'))}`):null;
      const messagePanel=primaryPanel?.querySelector('.message-panel');
      if(!messagePanel)return null;
      if(messagePanel._copyReadyPromise)await messagePanel._copyReadyPromise;
      if(token!==currentToken()||generation!==renderGeneration||!root.isConnected)return null;
      const tablist=messagePanel.querySelector(':scope>.tab-strip');
      const tab=tablist?.querySelector('[role="tab"][aria-selected="true"]');
      const panel=tab?messagePanel.querySelector(`#${CSS.escape(tab.getAttribute('aria-controls'))}`):null;
      if(!tab||!panel)return null;
      if(tab.dataset.tab==='json'||tab.dataset.tab==='xml'){
        const tree=panel.querySelector('.tree-subview'),pretty=panel.querySelector('.pretty-subview');
        if(pretty&&!pretty.classList.contains('hidden'))return{roots:[pretty]};
        if(!tree)return null;
        renderValueTree(tree,generation);
        while(!tree._treeRendered){
          await new Promise(resolve=>requestAnimationFrame(resolve));
          if(token!==currentToken()||generation!==renderGeneration||!root.isConnected)return null;
        }
        return{roots:[...tree.querySelectorAll('.tree-label,.tree-truncated')],snapshot:()=>snapshotValueTree(tree),reveal:revealValueTreeMatches};
      }
      if(tab.dataset.tab==='mapi'){
        const host=panel.querySelector('.protocol-block');
        if(host&&(host.dataset.compressedPayload||host._payloadData!==undefined))await renderProtocolTree(host,generation);
        if(token!==currentToken()||generation!==renderGeneration||!root.isConnected)return null;
        const tree=host?.querySelector('.protocol-tree');
        if(!tree)return{roots:[...panel.querySelectorAll('.protocol-meta,.warning')]};
        const roots=[...panel.querySelectorAll('.protocol-meta,.warning'),...tree.querySelectorAll('.tree-row')];
        return{roots,snapshot:()=>snapshotValueTree(tree),reveal:revealValueTreeMatches};
      }
      if(tab.dataset.tab==='raw'){
        const details=[...panel.querySelectorAll('details')];
        const roots=[...panel.querySelectorAll(':scope>h4,:scope>.headers,:scope>.format-meta,:scope>.decode-status,:scope>.warning,:scope>.body-view,:scope>.captured-bytes>summary,:scope>.captured-bytes>pre')];
        return{roots,snapshot:()=>snapshotDetails(details),reveal:revealDetailMatches};
      }
      if(tab.dataset.tab==='image'){
        const view=panel.querySelector('.image-view');
        if(view)await renderImageView(view,generation);
        if(token!==currentToken()||generation!==renderGeneration||!root.isConnected)return null;
        return{roots:[...panel.querySelectorAll('.image-meta,.format-status,.warning,.image-load-status')]};
      }
      if(tab.dataset.tab==='webview'){
        const view=panel.querySelector('.webview');
        if(!view)return null;
        setWebViewMode(view,'source',false);
        await messageViewModel(view);
        if(token!==currentToken()||generation!==renderGeneration||!root.isConnected)return null;
        return{roots:[...panel.querySelectorAll('.webview-source,.webview-status,.format-status,.warning')]};
      }
      if(tab.dataset.tab==='hex'){
        const view=panel.querySelector('.hex-view');
        if(view)await renderHexView(view,generation);
        if(token!==currentToken()||generation!==renderGeneration||!root.isConnected)return null;
        return{roots:[...panel.querySelectorAll('.hex-source-label,.decode-status,.hex-dump,.hex-status,.warning')]};
      }
      if(tab.dataset.tab==='auth'){
        return{roots:[...panel.querySelectorAll('.warning,.auth-headers,.auth-status')]};
      }
      return{roots:[...panel.querySelectorAll('.headers')]};
    }
  });
  root._clearHttpViewSearch=controller.reset;
  root._disposeHttpViewSearch=controller.dispose;
}
function renderWebSocketMessageDetail(container,message,generation){
  container.replaceChildren();
  const heading=wsElement('h3','ws-pane-heading',`Selected ${message.type} message`);
  const content=wsElement('div','ws-detail-content');
  const summary=wsElement('div','ws-detail-summary',
    `${message.timestamp} \u2022 ${message.direction==='Client'?'Client to server':message.direction==='Server'?'Server to client':'Unknown direction'} \u2022 ${message.payloadLength} bytes \u2022 ${message.frameCount} frame${message.frameCount===1?'':'s'}`);
  content.append(summary);
  if(message.warning)content.append(wsElement('div','warning',message.warning));
  const suffix=String(message.messageIndex);
  const hasJson=typeof message.jsonPretty==='string'&&typeof message.jsonTree==='string';
  const hasText=typeof message.text==='string';
  const tablist=wsElement('div','tab-strip');tablist.setAttribute('role','tablist');
  tablist.setAttribute('aria-label','WebSocket message views');tablist.dataset.side=`websocket-${suffix}`;tablist.dataset.priority='json,text,raw';
  tablist.append(
    wsTab('json','JSON',hasJson,hasJson,suffix),
    wsTab('text','Text',hasText,!hasJson&&hasText,suffix),
    wsTab('raw','Raw',true,!hasJson&&!hasText,suffix));
  const panels=wsElement('div','tab-panels');
  const jsonPanel=wsPanel('json',suffix);
  if(hasJson){
    jsonPanel.append(wsCopyToolbar('Copy WebSocket JSON pretty text','WebSocket JSON pretty text',message.jsonPretty));
    const structured=wsElement('div','structured-body');
    const toolbar=wsElement('div','tree-toolbar');
    const toggle=wsElement('div','view-toggle');toggle.setAttribute('role','group');toggle.setAttribute('aria-label','JSON view');
    const treeButton=wsElement('button','','Tree');treeButton.type='button';treeButton.dataset.view='tree';treeButton.setAttribute('aria-pressed','true');
    const prettyButton=wsElement('button','','Pretty Text');prettyButton.type='button';prettyButton.dataset.view='pretty';prettyButton.setAttribute('aria-pressed','false');
    toggle.append(treeButton,prettyButton);
    const expand=wsElement('button','tree-expand-all','Expand all');expand.type='button';
    const collapse=wsElement('button','tree-collapse-all','Collapse all');collapse.type='button';
    toolbar.append(toggle,expand,collapse);
    const tree=wsElement('div','tree-subview');tree._payloadType='json-tree';
    try{tree._payloadData=JSON.parse(message.jsonTree)}
    catch{tree.textContent='JSON tree data is invalid.';tree.classList.add('warning');tree._treeRendered=true}
    const pretty=wsElement('pre','pretty-subview formatted-view hidden',message.jsonPretty);pretty.dataset.format='json';
    structured.append(toolbar,tree,pretty);jsonPanel.append(structured);
  }else jsonPanel.append(wsElement('div','tab-empty','JSON is unavailable for this message.'));
  const textPanel=wsPanel('text',suffix);
  if(hasText){
    textPanel.append(wsCopyToolbar('Copy WebSocket text message','WebSocket text message',message.text));
    textPanel.append(wsElement('pre','ws-text-view',message.text));
  }else textPanel.append(wsElement('div','tab-empty','Decoded UTF-8 text is unavailable for this message.'));
  const rawPanel=wsPanel('raw',suffix);
  rawPanel.append(wsCopyToolbar('Copy WebSocket raw representation','WebSocket raw representation',message.raw));
  rawPanel.append(wsElement('pre','ws-raw-view',message.raw));
  panels.append(jsonPanel,textPanel,rawPanel);content.append(tablist,panels);container.append(heading,content);
  setupTabs(content);setupTreeToggles(content);setupCopyControls(content);highlightSelected(content);
  setupWebSocketViewSearch(content,generation);
  hydrateViewPayloads(content,generation);
}
async function renderWebSocketInspector(host,generation){
  if(host._wsRendered||host._wsLoading)return;
  host._wsLoading=true;
  try{
    if(host.dataset.payloadError)throw new Error(host.dataset.payloadError);
    const data=await decodeCompressedPayload(host,'websocket-session');
    if(generation!==renderGeneration||host.closest('.hidden'))return;
    if(!data||!Array.isArray(data.messages)||data.messages.length>5000)throw new Error('WebSocket message data has an invalid format');
    const messages=data.messages;
    if(messages.some(message=>!message||typeof message!=='object'||typeof message.direction!=='string'||typeof message.type!=='string'||typeof message.raw!=='string'||!Array.isArray(message.frames))){
      throw new Error('WebSocket message data has an invalid format');
    }
    const searchKeys=messages.map(message=>message.type==='Text'&&typeof message.text==='string'?message.text.toLowerCase():'');
    const hasTruncatedSearchablePayload=messages.some((message,index)=>message.isPayloadTruncated&&searchKeys[index]);
    const layout=wsElement('div','ws-layout');
    const traffic=wsElement('section','ws-traffic-pane');traffic.setAttribute('aria-label','Chronological WebSocket traffic');
    traffic.append(wsElement('h3','ws-pane-heading','WebSocket traffic'));
    if(data.omittedMessages>0)traffic.append(wsElement('div','warning',`${data.omittedMessages} additional message(s) were omitted by the report safety limit.`));
    const searchHelpId=`ws-search-help-${generation}`;
    const searchHelp=wsElement('span','sr-only',hasTruncatedSearchablePayload
      ?'Searches retained decoded text and JSON payload content only. Binary payloads and bytes omitted by safety truncation are not searched.'
      :'Searches retained decoded text and JSON payload content only. Binary payloads are not searched.');
    searchHelp.id=searchHelpId;
    const search=wsElement('input','ws-payload-search');search.type='search';search.maxLength=4096;
    search.placeholder='Search WebSocket payloads...';search.setAttribute('aria-label','Search WebSocket payloads');
    search.setAttribute('aria-describedby',searchHelpId);search.title=searchHelp.textContent;
    const searchStatus=wsElement('span','ws-search-status');searchStatus.setAttribute('role','status');searchStatus.setAttribute('aria-live','polite');
    const searchControls=wsElement('div','ws-search-controls');searchControls.append(search,searchStatus,searchHelp);traffic.append(searchControls);
    const scroll=wsElement('div','ws-message-scroll');
    const grid=wsElement('div','ws-message-tracks');
    const header=wsElement('div','ws-message-header');header.setAttribute('role','row');
    ['ID','Type','Body','Preview'].forEach(label=>{const cell=wsElement('span','',label);cell.setAttribute('role','columnheader');header.append(cell)});
    const list=wsElement('div','ws-message-list');list.setAttribute('role','listbox');list.setAttribute('aria-label','WebSocket logical messages');
    const empty=wsElement('div','ws-message-empty hidden','No WebSocket messages match this payload search.');
    const detail=wsElement('section','ws-detail-pane');detail.setAttribute('aria-label','Selected WebSocket message');
    const splitter=wsElement('div','ws-splitter');splitter.tabIndex=0;splitter.setAttribute('role','separator');
    splitter.setAttribute('aria-label','Resize WebSocket traffic and payload panes');splitter.setAttribute('aria-orientation','vertical');
    splitter.title='Drag or use Left and Right arrow keys to resize the WebSocket panes';
    list.append(empty);grid.append(header,list);scroll.append(grid);traffic.append(scroll);layout.append(traffic,splitter,detail);host.replaceChildren(layout);
    setupWebSocketSplitter(layout,traffic,detail,splitter);
    const rows=[];
    let visibleIndices=[];
    let selectedIndex=-1;
    function select(index,focus){
      if(index<0||index>=messages.length||!visibleIndices.includes(index))return;
      selectedIndex=index;
      rows.forEach((row,rowIndex)=>{const selected=rowIndex===index;row.setAttribute('aria-selected',String(selected));row.tabIndex=selected?0:-1});
      renderWebSocketMessageDetail(detail,messages[index],generation);
      if(focus)rows[index].focus();
    }
    function showEmptyDetail(){
      selectedIndex=-1;
      rows.forEach(row=>{row.setAttribute('aria-selected','false');row.tabIndex=-1});
      detail.replaceChildren(
        wsElement('h3','ws-pane-heading','Selected WebSocket message'),
        wsElement('div','warning','No WebSocket messages match this payload search.'));
    }
    function applySearch(){
      const query=search.value.trim().toLowerCase();
      visibleIndices=[];
      rows.forEach((row,index)=>{
        const visible=!query||searchKeys[index].includes(query);
        row.classList.toggle('ws-filtered',!visible);
        row.setAttribute('aria-hidden',String(!visible));
        if(visible)visibleIndices.push(index);
      });
      empty.classList.toggle('hidden',visibleIndices.length!==0);
      searchStatus.textContent=`${visibleIndices.length} of ${messages.length} messages`;
      if(!visibleIndices.length)showEmptyDetail();
      else if(!visibleIndices.includes(selectedIndex))select(visibleIndices[0],false);
    }
    messages.forEach((message,index)=>{
      const direction=message.direction==='Client'?'Client to server':message.direction==='Server'?'Server to client':'Unknown direction';
      const row=wsElement('button',`ws-message-row ws-${message.direction.toLowerCase()}`);row.type='button';row.setAttribute('role','option');
      const logicalId=index+1;
      const limited=message.isPayloadTruncated||!message.isComplete;
      row.setAttribute('aria-label',`${direction}, logical message ${logicalId}, ${message.listType}, ${message.payloadLengthText} bytes${limited?', retained content is limited or partial':''}, ${message.listPreview}`);
      row.title=direction;row.tabIndex=index===0?0:-1;
      const idCell=wsElement('span','ws-id');idCell.title=direction;idCell.setAttribute('aria-label',`${direction}, logical message ${logicalId}`);
      const arrow=wsElement('span','ws-arrow',message.direction==='Client'?'\u2191':message.direction==='Server'?'\u2193':'\u2194');arrow.setAttribute('aria-hidden','true');
      idCell.append(arrow,document.createTextNode(` ${logicalId}`));
      const typeCell=wsElement('span','ws-type',message.listType);
      const bodyCell=wsElement('span',`ws-body${limited?' ws-body-truncated':''}`,`${message.payloadLengthText}${limited?'*':''}`);
      bodyCell.title=limited?'Original declared logical payload bytes; retained content is truncated or the message is incomplete.':'Logical payload bytes.';
      const preview=wsElement('span','ws-message-preview',message.listPreview);preview.title=message.listPreview;
      row.append(idCell,typeCell,bodyCell,preview);
      row.addEventListener('click',()=>select(index,false));
      row.addEventListener('keydown',event=>{
        const position=visibleIndices.indexOf(index);
        let target=index;
        if(event.key==='ArrowDown')target=visibleIndices[Math.min(visibleIndices.length-1,position+1)];
        else if(event.key==='ArrowUp')target=visibleIndices[Math.max(0,position-1)];
        else if(event.key==='Home')target=visibleIndices[0];
        else if(event.key==='End')target=visibleIndices[visibleIndices.length-1];
        else if(event.key==='Enter'||event.key===' '){event.preventDefault();select(index,false);return}
        else return;
        event.preventDefault();select(target,true);
      });
      rows.push(row);list.append(row);
    });
    search.addEventListener('input',applySearch);
    if(messages.length)applySearch();
    else detail.append(wsElement('div','warning','No WebSocket logical messages are available for this session.'));
    host._wsRendered=true;
  }catch(error){
    host.textContent=payloadFailureText(error,'WebSocket traffic',' Regenerate the report with the current SAZ Viewer.');
    host.className='websocket-inspector warning';host._wsRendered=true;
  }finally{host._wsLoading=false}
}
async function messageViewModel(view){
  const panel=view.closest('.message-panel');
  if(!panel)throw new Error('message view is detached');
  if(panel._copyReadyPromise)await panel._copyReadyPromise;
  if(panel._copyModelError)throw new Error(panel._copyModelError);
  return panel._copyModel||{};
}
function imageMetadata(view){
  return [...view.querySelectorAll('.image-meta span,.format-status,.warning')]
    .map(item=>item.textContent?.trim()).filter(Boolean).join('\n');
}
function prepareDynamicCopy(button,text){
  if(!button)return;
  delete button.dataset.copyError;button._copyText=text;
  button.disabled=false;button.removeAttribute('aria-disabled');button.removeAttribute('aria-busy');
}
function failDynamicCopy(button,error){
  if(!button)return;
  delete button._copyText;button.dataset.copyError=error;
  button.disabled=false;button.removeAttribute('aria-disabled');button.removeAttribute('aria-busy');
}
async function renderImageView(view,generation){
  if(view._rendered||view._loading)return;
  view._loading=true;
  const status=view.querySelector('.image-load-status'),image=view.querySelector('img');
  const copy=view.closest('[role="tabpanel"]')?.querySelector('.copy-button[data-copy-kind="image"]');
  try{
    const model=await messageViewModel(view);
    if(generation!==renderGeneration||!view.isConnected||view.closest('.tab-panel.hidden'))return;
    const bytes=decodeModelBytes(model,view.dataset.byteField);
    const mime=view.dataset.imageMime;
    if(!mime||!['image/png','image/jpeg','image/gif','image/webp','image/bmp','image/x-icon'].includes(mime))throw new Error('image type is unsupported');
    const url=URL.createObjectURL(new Blob([bytes],{type:mime}));
    view._blobUrl=url;
    image.onload=()=>{
      if(view._blobUrl!==url)return;
      const dimensions=view.querySelector('.image-dimensions');
      if(dimensions)dimensions.textContent=`${image.naturalWidth}\u00d7${image.naturalHeight} pixels`;
      status.textContent='Image decoded locally from retained body bytes.';status.classList.remove('warning');
      prepareDynamicCopy(copy,imageMetadata(view));
      URL.revokeObjectURL(url);view._blobUrl=null;
    };
    image.onerror=()=>{
      if(view._blobUrl!==url)return;
      status.textContent='The browser could not decode the retained image bytes.';
      status.classList.add('warning');
      prepareDynamicCopy(copy,imageMetadata(view)+'\nBrowser decode failed.');
      URL.revokeObjectURL(url);view._blobUrl=null;
    };
    image.src=url;
    prepareDynamicCopy(copy,imageMetadata(view));
    view._rendered=true;
  }catch(error){
    status.textContent=`Image could not be loaded: ${error.message}`;
    status.classList.add('warning');
    failDynamicCopy(copy,`Image metadata could not be prepared. ${error.message}`);
    view._rendered=true;
  }finally{view._loading=false}
}
const WEBVIEW_DOCUMENT_PREFIX=`<!doctype html><html data-theme="system"><head><meta charset="utf-8"><meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src 'none'; media-src 'none'; font-src 'none'; style-src 'unsafe-inline'; script-src 'none'; connect-src 'none'; frame-src 'none'; child-src 'none'; object-src 'none'; form-action 'none'; base-uri 'none'"><meta name="referrer" content="no-referrer">`;
function deactivateWebView(view){
  view._webViewToken=(view._webViewToken||0)+1;
  view._webViewLoading=false;
  const frame=view._webViewFrame||view.querySelector('iframe');
  if(frame)frame.remove();
  view._webViewFrame=null;
}
function webViewDocument(model){
  const documentText=model?.webViewDocument;
  if(typeof documentText!=='string'||documentText.length>MAX_COPY_CHARACTERS||!documentText.startsWith(WEBVIEW_DOCUMENT_PREFIX))
    throw new Error('the inert HTML document is invalid or oversized');
  return documentText.replace('<html data-theme="system">',`<html data-theme="${effectiveTheme()}">`);
}
async function renderWebView(view,generation){
  if(view._webViewFrame||view._webViewLoading||view.dataset.mode==='source')return;
  view._webViewLoading=true;
  const token=(view._webViewToken||0)+1;
  view._webViewToken=token;
  const current=()=>view._webViewToken===token&&generation===renderGeneration&&view.isConnected
    &&!view.closest('.tab-panel.hidden')&&!view.closest('.primary-panel.hidden')&&view.dataset.mode!=='source';
  const status=view.querySelector('.webview-status'),host=view.querySelector('.webview-frame-host');
  try{
    const model=await messageViewModel(view);
    if(!current())return;
    const frame=document.createElement('iframe');
    frame.className='webview-frame';
    frame.title='Inert locally rendered captured HTML';
    frame.setAttribute('sandbox','');
    frame.setAttribute('referrerpolicy','no-referrer');
    frame.setAttribute('loading','lazy');
    let loads=0;
    frame.addEventListener('load',()=>{
      if(!current()||view._webViewFrame!==frame)return;
      loads++;
      if(loads>1){
        deactivateWebView(view);
        status.textContent='The inert preview attempted an unexpected navigation and was closed.';
        status.classList.add('warning');
        return;
      }
      status.textContent='Inert preview rendered locally. Scripts, forms, navigation, storage, and subresources are blocked.';
      status.classList.remove('warning');
    });
    frame.srcdoc=webViewDocument(model);
    view._webViewFrame=frame;
    host.replaceChildren(frame);
  }catch(error){
    if(!current())return;
    status.textContent=`WebView could not be loaded: ${error.message}`;
    status.classList.add('warning');
  }finally{
    if(view._webViewToken===token)view._webViewLoading=false;
  }
}
function setWebViewMode(view,mode,notify){
  const sourceMode=mode==='source';
  view.dataset.mode=sourceMode?'source':'rendered';
  view.querySelector('.webview-source').classList.toggle('hidden',!sourceMode);
  view.querySelector('.webview-frame-host').classList.toggle('hidden',sourceMode);
  view.querySelectorAll('.webview-mode button').forEach(button=>button.setAttribute('aria-pressed',String(button.dataset.webviewMode===view.dataset.mode)));
  if(sourceMode){
    deactivateWebView(view);
    view.querySelector('.webview-status').textContent='Source view is active. Search and Copy use the original decoded captured source.';
  }else{
    renderWebView(view,renderGeneration);
  }
  if(notify)view.dispatchEvent(new CustomEvent('saz-view-change',{bubbles:true}));
}
function refreshWebViewThemes(){
  document.querySelectorAll('.webview[data-mode="rendered"]').forEach(view=>{
    if(view.closest('.tab-panel.hidden')||view.closest('.primary-panel.hidden'))return;
    deactivateWebView(view);renderWebView(view,renderGeneration);
  });
}
function formatHexDump(bytes,source,total,retained,removed){
  const lines=[`${source} body bytes`,`${total.toLocaleString()} total; ${retained.toLocaleString()} retained${retained<total?' (truncated)':''}`];
  if(removed)lines.push(`Removed encodings: ${removed}`);
  lines.push('', 'Offset    00 01 02 03 04 05 06 07  08 09 0A 0B 0C 0D 0E 0F  ASCII');
  for(let offset=0;offset<bytes.length;offset+=16){
    const slice=bytes.subarray(offset,Math.min(offset+16,bytes.length));
    const hex=[...slice].map(value=>value.toString(16).toUpperCase().padStart(2,'0'));
    const left=hex.slice(0,8).join(' ').padEnd(23,' '),right=hex.slice(8).join(' ').padEnd(23,' ');
    const ascii=[...slice].map(value=>value>=32&&value<=126?String.fromCharCode(value):'.').join('');
    lines.push(`${offset.toString(16).toUpperCase().padStart(8,'0')}  ${left}  ${right}  |${ascii.padEnd(16,' ')}|`);
  }
  if(retained<total)lines.push(`\n[HexView truncated: ${retained.toLocaleString()} of ${total.toLocaleString()} bytes retained.]`);
  return lines.join('\n');
}
async function renderHexView(view,generation){
  const selection=view.querySelector('.hex-source')?.value||'captured';
  const pre=view.querySelector('.hex-dump'),status=view.querySelector('.hex-status');
  const copy=view.closest('[role="tabpanel"]')?.querySelector('.copy-button[data-copy-kind="hex"]');
  try{
    const model=await messageViewModel(view);
    if(generation!==renderGeneration||!view.isConnected||view.closest('.tab-panel.hidden'))return;
    const decoded=selection==='decoded';
    const bytes=decodeModelBytes(model,decoded?'decodedBytes':'capturedBytes');
    const total=Number.parseInt(decoded?view.dataset.decodedLength:view.dataset.capturedLength,10);
    const retained=bytes.length,source=decoded?'Decoded':'Captured';
    const removed=decoded?view.querySelector('.decode-status')?.textContent?.replace(/^Removed encodings:\s*/,'')||'':'';
    const text=formatHexDump(bytes,source,total,retained,removed);
    pre.textContent=text;pre.setAttribute('aria-label',`${source} body byte hex dump`);
    view.querySelector('.hex-source-label').textContent=`${source} body bytes`;
    status.textContent=retained<total?`Showing a truncated retained prefix (${retained.toLocaleString()} of ${total.toLocaleString()} bytes).`:`Showing all ${total.toLocaleString()} bytes.`;
    status.classList.remove('warning');
    if(copy){
      copy.setAttribute('aria-label',`Copy ${source.toLowerCase()} body hex view`);
      copy.dataset.copyDescription=`${source.toLowerCase()} body hex view`;
    }
    prepareDynamicCopy(copy,text);
    view._rendered=true;
  }catch(error){
    pre.textContent='';status.textContent=`HexView could not be loaded: ${error.message}`;status.classList.add('warning');
    failDynamicCopy(copy,`HexView could not be prepared. ${error.message}`);
  }
}
function resetAuthView(view,notify){
  view._authRevealToken=(view._authRevealToken||0)+1;
  const panel=view.closest('.message-panel'),model=panel?._copyModel||{};
  const pre=view.querySelector('.auth-headers'),button=view.querySelector('.auth-reveal');
  const copy=view.closest('[role="tabpanel"]')?.querySelector('.copy-button');
  if(typeof model.auth==='string')pre.textContent=model.auth;
  view.dataset.revealed='false';view._authFullText=null;
  button.disabled=false;
  button.textContent='Reveal values';button.setAttribute('aria-pressed','false');button.setAttribute('aria-label','Reveal full authentication header values');
  const status=view.querySelector('.auth-status');
  status.textContent='Values are redacted.';status.classList.remove('warning');
  if(copy){
    delete copy._copyText;copy.dataset.copyKey='auth';
    copy.setAttribute('aria-label','Copy redacted authentication headers');copy.dataset.copyDescription='redacted authentication headers';
  }
  if(notify)view.dispatchEvent(new CustomEvent('saz-view-change',{bubbles:true}));
}
async function toggleAuthView(view){
  if(view.dataset.revealed==='true'){resetAuthView(view,true);return}
  const status=view.querySelector('.auth-status'),button=view.querySelector('.auth-reveal');
  const token=(view._authRevealToken||0)+1,generation=renderGeneration;
  view._authRevealToken=token;
  const isCurrent=()=>view._authRevealToken===token&&generation===renderGeneration&&view.isConnected
    &&!view.closest('.tab-panel.hidden')&&!view.closest('.primary-panel.hidden');
  button.disabled=true;
  try{
    const model=await messageViewModel(view);
    if(!isCurrent())return;
    const full=await decodeEmbeddedUtf8(model);
    if(!isCurrent())return;
    view.querySelector('.auth-headers').textContent=full;
    view.dataset.revealed='true';view._authFullText=full;
    button.textContent='Hide values';button.setAttribute('aria-pressed','true');button.setAttribute('aria-label','Hide full authentication header values');
    status.textContent='Full captured authentication values are visible.';
    const copy=view.closest('[role="tabpanel"]')?.querySelector('.copy-button');
    if(copy){
      delete copy.dataset.copyKey;copy._copyText=full;
      copy.setAttribute('aria-label','Copy revealed authentication headers');copy.dataset.copyDescription='revealed authentication headers';
    }
    view.dispatchEvent(new CustomEvent('saz-view-change',{bubbles:true}));
  }catch(error){
    if(!isCurrent())return;
    status.textContent=`Authentication values could not be revealed: ${error.message}`;
    status.classList.add('warning');
  }finally{if(isCurrent())button.disabled=false}
}
function setupDynamicViewControls(root,generation){
  root.querySelectorAll('.webview').forEach(view=>{
    view.querySelectorAll('.webview-mode button').forEach(button=>button.addEventListener('click',()=>setWebViewMode(view,button.dataset.webviewMode,true)));
  });
  root.querySelectorAll('.hex-view').forEach(view=>{
    view.querySelector('.hex-source')?.addEventListener('change',()=>{view._rendered=false;renderHexView(view,generation);view.dispatchEvent(new CustomEvent('saz-view-change',{bubbles:true}))});
  });
  root.querySelectorAll('.auth-view').forEach(view=>{
    view.querySelector('.auth-reveal')?.addEventListener('click',()=>toggleAuthView(view));
  });
}
function deactivateDynamicViews(root){
  root.querySelectorAll('.image-view').forEach(view=>{
    if(view._blobUrl){URL.revokeObjectURL(view._blobUrl);view._blobUrl=null}
    const image=view.querySelector('img');if(image)image.removeAttribute('src');
    view._rendered=false;
  });
  root.querySelectorAll('.auth-view').forEach(view=>resetAuthView(view,false));
  root.querySelectorAll('.webview').forEach(deactivateWebView);
}
function hydrateViewPayloads(root,generation){
  if(root.classList?.contains('hidden')||root.closest?.('.primary-panel.hidden'))return;
  root.querySelectorAll('.websocket-inspector').forEach(host=>renderWebSocketInspector(host,generation));
  root.querySelectorAll('.image-view').forEach(view=>{if(!view.closest('.tab-panel.hidden'))renderImageView(view,generation)});
  root.querySelectorAll('.webview').forEach(view=>{if(!view.closest('.tab-panel.hidden'))renderWebView(view,generation)});
  root.querySelectorAll('.hex-view').forEach(view=>{if(!view.closest('.tab-panel.hidden'))renderHexView(view,generation)});
  root.querySelectorAll('.protocol-block').forEach(host=>{
    if((host.dataset.compressedPayload||host._payloadData!==undefined)&&!host.closest('.tab-panel.hidden'))renderProtocolTree(host,generation);
  });
  root.querySelectorAll('.tree-subview').forEach(host=>{
    if((host.dataset.compressedPayload||host._payloadData!==undefined)&&!host.classList.contains('hidden')&&!host.closest('.tab-panel.hidden'))renderValueTree(host,generation);
  });
}
function prepareLazyPayloads(root){
  root.querySelectorAll('.protocol-block').forEach(host=>{
    const button=host.closest('[role="tabpanel"]')?.querySelector('.copy-button[data-copy-kind="mapi"]');
    if(button){button.disabled=true;button.setAttribute('aria-disabled','true')}
  });
  root.querySelectorAll('.copy-button[data-copy-kind="image"],.copy-button[data-copy-kind="hex"]').forEach(button=>{
    button.disabled=true;button.setAttribute('aria-disabled','true');button.setAttribute('aria-busy','true');
  });
}
function setupTreeToggles(root){
  root.querySelectorAll('.structured-body').forEach(container=>{
    const toolbar=container.querySelector('.tree-toolbar');
    const treeView=container.querySelector('.tree-subview');
    const prettyView=container.querySelector('.pretty-subview');
    if(!toolbar)return;
    toolbar.querySelectorAll('.view-toggle button').forEach(btn=>{
      btn.addEventListener('click',()=>{
        if(btn.disabled)return;
        const showTree=btn.dataset.view==='tree';
        treeView.classList.toggle('hidden',!showTree);
        prettyView.classList.toggle('hidden',showTree);
        toolbar.querySelectorAll('.view-toggle button').forEach(other=>other.setAttribute('aria-pressed',String(other===btn)));
        if(showTree)renderValueTree(treeView,renderGeneration);
        container.dispatchEvent(new CustomEvent('saz-view-change',{bubbles:true}));
      });
    });
    toolbar.querySelector('.tree-expand-all')?.addEventListener('click',()=>setAllExpanded(treeView,true));
    toolbar.querySelector('.tree-collapse-all')?.addEventListener('click',()=>setAllExpanded(treeView,false));
  });
}
const MAX_COPY_CHARACTERS=1048576;
function protocolCopyText(data){
  let text='',exceeded=false;
  function append(line){
    const addition=(text?'\n':'')+line;
    if(text.length+addition.length>MAX_COPY_CHARACTERS){exceeded=true;return false}
    text+=addition;return true;
  }
  const state=data.complete?'complete':'partial';
  if(!append(`MAPI protocol (${state}; ${data.parsedBytes} of ${data.totalBytes} bytes)`))return{error:'MAPI copy exceeds the 1 MiB safety limit.'};
  (data.warnings||[]).forEach(warning=>append(`[Warning] ${warning}`));
  if(data.omittedWarnings>0)append(`[Warning] ${data.omittedWarnings} additional warning(s) omitted from the report.`);
  function visit(node,depth){
    if(exceeded)return;
    const indent='  '.repeat(Math.min(depth,64));
    const value=node.value===null||node.value===undefined?'':` = ${String(node.value)}`;
    if(!append(`${indent}${node.name} [${node.kind}] @${node.offset} +${node.length}${value}`))return;
    (node.children||[]).forEach(child=>visit(child,depth+1));
  }
  visit(data.root,0);
  return exceeded?{error:'MAPI copy exceeds the 1 MiB safety limit.'}:{text};
}
function copySource(button){
  if(typeof button._copyText==='string'){
    return button._copyText.length>MAX_COPY_CHARACTERS
      ?{error:'Copy source exceeds the 1 MiB safety limit.'}
      :{text:button._copyText};
  }
  if(button.dataset.copyError)return{error:button.dataset.copyError};
  if(button.dataset.copyKind==='mapi'){
    const host=button.closest('[role="tabpanel"]').querySelector('.protocol-block');
    if(!host)return{error:'MAPI protocol data is unavailable.'};
    const data=host._protocolData;
    return data?protocolCopyText(data):{error:'MAPI protocol data is unavailable.'};
  }
  const panel=button.closest('.message-panel');
  if(panel?._copyModelError)return{error:panel._copyModelError};
  const text=panel?._copyModel?.[button.dataset.copyKey];
  if(typeof text!=='string')return{error:'Copy source could not be decoded.'};
  if(text.length>MAX_COPY_CHARACTERS)return{error:'Copy source exceeds the 1 MiB safety limit.'};
  return{text};
}
async function loadCopyModel(panel){
  if(panel.dataset.payloadError)throw new Error(panel.dataset.payloadError);
  if(!panel.dataset.compressedPayload&&panel._payloadData===undefined)return{};
  const model=await decodeCompressedPayload(panel,'copy-model');
  if(!model||typeof model!=='object'||Array.isArray(model))throw new Error('Copy data has an invalid format.');
  Object.values(model).forEach(value=>{
    if(typeof value!=='string'||value.length>MAX_COPY_CHARACTERS)throw new Error('Copy data exceeds the 1 MiB safety limit.');
  });
  return model;
}
function hydrateCopyModel(panel,model){
  panel.querySelectorAll('[data-copy-field]').forEach(target=>{
    const text=model[target.dataset.copyField];
    if(typeof text!=='string')return;
    const start=Number.parseInt(target.dataset.copyStart||'0',10);
    const length=Number.parseInt(target.dataset.copyLength||String(text.length),10);
    if(!Number.isSafeInteger(start)||!Number.isSafeInteger(length)||start<0||length<0||start+length>text.length)return;
    target.textContent=text.slice(start,start+length);
  });
}
function fallbackCopyText(text,button){
  const active=document.activeElement;
  const selection=document.getSelection();
  const ranges=[];
  if(selection)for(let index=0;index<selection.rangeCount;index++)ranges.push(selection.getRangeAt(index).cloneRange());
  const textarea=document.createElement('textarea');
  textarea.value=text;textarea.readOnly=true;textarea.setAttribute('aria-hidden','true');
  textarea.style.position='fixed';textarea.style.left='-10000px';textarea.style.top='0';
  const host=button.closest('dialog')||document.body;
  host.append(textarea);textarea.focus();textarea.select();
  let copied=false,transferred=false;
  const transfer=event=>{
    if(!event.clipboardData)return;
    event.preventDefault();
    event.clipboardData.setData('text/plain',text);
    transferred=true;
  };
  document.addEventListener('copy',transfer);
  if(document.activeElement===textarea){
    try{copied=document.execCommand('copy')&&transferred}catch{}
  }
  document.removeEventListener('copy',transfer);
  textarea.remove();
  if(selection){selection.removeAllRanges();ranges.forEach(range=>selection.addRange(range))}
  const restore=active&&active!==document.body&&host.contains(active)?active:button;
  restore.focus({preventScroll:true});
  return copied;
}
async function writeClipboardText(text,button){
  if(navigator.clipboard&&typeof navigator.clipboard.writeText==='function'){
    try{await navigator.clipboard.writeText(text);return true}catch{}
  }
  return fallbackCopyText(text,button);
}
function setupCopyControls(root){
  root.querySelectorAll('.message-panel').forEach(panel=>{
    const buttons=[...panel.querySelectorAll('.copy-button[data-copy-key]')];
    buttons.forEach(button=>{button.disabled=true;button.setAttribute('aria-disabled','true');button.setAttribute('aria-busy','true')});
    panel._copyReadyPromise=loadCopyModel(panel).then(model=>{
      panel._copyModel=model;
      hydrateCopyModel(panel,model);
      highlightSelected(panel);
      buttons.forEach(button=>{button.disabled=false;button.removeAttribute('aria-disabled');button.removeAttribute('aria-busy')});
    }).catch(error=>{
      panel._copyModelError=`Copy data could not be prepared. ${error.message}`;
      panel.querySelectorAll('[data-copy-field]').forEach(target=>{
        target.textContent='Content could not be displayed because this browser could not read the compressed local report data.';
        target.classList.add('warning');
      });
      buttons.forEach(button=>{button.disabled=false;button.removeAttribute('aria-disabled');button.removeAttribute('aria-busy')});
    });
  });
  root.querySelectorAll('.copy-button').forEach(button=>{
    button.addEventListener('click',async()=>{
      if(button.disabled)return;
      const status=button.parentElement.querySelector('.copy-status');
      const source=copySource(button);
      if(source.error){
        status.textContent=source.error;
        button.textContent='Copy';
        return;
      }
      const copied=await writeClipboardText(source.text,button);
      if(!copied){
        status.textContent='Copy failed. Check clipboard permissions or select and copy the content manually.';
        button.textContent='Copy';
        return;
      }
      button.textContent='Copied';
      status.textContent=`Copied ${button.dataset.copyDescription}.`;
      clearTimeout(button._copyResetTimer);
      button._copyResetTimer=setTimeout(()=>{button.textContent='Copy'},1500);
    });
  });
}
function bindFilter(inputId,selectId,tableId){
  const input=document.getElementById(inputId),select=document.getElementById(selectId),rows=document.querySelectorAll(`#${tableId} tbody tr`);
  function apply(){
    const query=input.value.toLowerCase(),filter=select.value;
    rows.forEach(row=>{
      const filterMatch=!filter||(filter==='mapi'?row.dataset.mapi==='true':filter==='websocket'?row.dataset.websocket==='true':row.dataset.filter===filter);
      const visible=(!query||row.dataset.search.includes(query))&&filterMatch;
      row.classList.toggle('hidden',!visible);
    });
  }
  input.addEventListener('input',apply);select.addEventListener('change',apply);
  return apply;
}
const httpSearch=document.getElementById('httpSearch');
const httpFilter=document.getElementById('httpFilter');
const applyHttpFilter=bindFilter('httpSearch','httpFilter','httpTable');
const INSPECTOR_HASH_PREFIX='#saz-inspector?';
const MAX_INSPECTOR_HASH_LENGTH=4096;
const MAX_INSPECTOR_QUERY_LENGTH=512;
const ALLOWED_INSPECTOR_FILTERS=new Set(['','websocket','mapi','0','2','3','4','5']);
function serializeInspectorState(row){
  if(httpSearch.value.length>MAX_INSPECTOR_QUERY_LENGTH)return null;
  const params=new URLSearchParams();
  params.set('v','1');
  params.set('session',row.dataset.detail);
  if(httpSearch.value)params.set('q',httpSearch.value);
  if(httpFilter.value)params.set('filter',httpFilter.value);
  const hash=`${INSPECTOR_HASH_PREFIX}${params.toString()}`;
  return hash.length<=MAX_INSPECTOR_HASH_LENGTH?hash:null;
}
function parseInspectorState(hash){
  if(!hash.startsWith(INSPECTOR_HASH_PREFIX))return null;
  if(hash.length>MAX_INSPECTOR_HASH_LENGTH)return{error:'Inspector link state is too large. Return to the session table and open the session again.'};
  try{
    const params=new URLSearchParams(hash.slice(INSPECTOR_HASH_PREFIX.length));
    const entries=[...params.entries()];
    if(entries.length>8)return{error:'Inspector link state has too many settings.'};
    if(entries.some(([key])=>!['v','session','q','filter'].includes(key)))return{error:'Inspector link state contains an unsupported setting.'};
    if(params.get('v')!=='1')return{error:'Inspector link version is not supported.'};
    const session=params.get('session')||'';
    const query=params.get('q')||'';
    const filter=params.get('filter')||'';
    if(!/^http-detail-\d{1,9}$/.test(session))return{error:'Inspector link does not identify a valid session.'};
    if(query.length>MAX_INSPECTOR_QUERY_LENGTH)return{error:'Inspector search text is too long.'};
    if(!ALLOWED_INSPECTOR_FILTERS.has(filter))return{error:'Inspector link contains an unsupported filter.'};
    return{session,query,filter};
  }catch{
    return{error:'Inspector link state could not be read.'};
  }
}
function inspectorUrl(row){
  const state=serializeInspectorState(row);
  if(!state)return null;
  const url=new URL(window.location.href);
  url.hash=state.slice(1);
  return url.href;
}
const preferredTab={};
function tabsOf(tablist){return [...tablist.querySelectorAll('[role="tab"]')]}
function activateTab(tablist,key,options){
  const tabs=tabsOf(tablist),target=tabs.find(tab=>tab.dataset.tab===key&&!tab.disabled);
  if(!target)return;
  const panels=tablist.parentElement.querySelectorAll(':scope>.tab-panels>.tab-panel');
  let targetPanel=null;
  const previous=tabs.find(tab=>tab.getAttribute('aria-selected')==='true');
  if(previous&&previous!==target){
    const previousPanel=tablist.parentElement.querySelector(`#${CSS.escape(previous.getAttribute('aria-controls'))}`);
    if(previousPanel)deactivateDynamicViews(previousPanel);
  }
  tabs.forEach(tab=>{const active=tab===target;tab.setAttribute('aria-selected',String(active));tab.tabIndex=active?0:-1});
  panels.forEach(panel=>{const active=panel.id===target.getAttribute('aria-controls');panel.classList.toggle('hidden',!active);if(active)targetPanel=panel});
  if(targetPanel)hydrateViewPayloads(targetPanel,renderGeneration);
  if(options&&options.remember)preferredTab[tablist.dataset.side]=key;
  if(options&&options.focus)target.focus();
  tablist.parentElement.dispatchEvent(new CustomEvent('saz-view-change',{bubbles:true}));
}
function initialTabFor(tablist){
  const tabs=tabsOf(tablist);
  const enabled=key=>tabs.some(tab=>tab.dataset.tab===key&&!tab.disabled);
  const remembered=preferredTab[tablist.dataset.side];
  if(remembered&&enabled(remembered))return remembered;
  for(const key of tablist.dataset.priority.split(',')){if(enabled(key))return key}
  return null;
}
function setupTabList(tablist){
  const key=initialTabFor(tablist);
  if(key)activateTab(tablist,key);
  tablist.addEventListener('click',event=>{
    const tab=event.target.closest('[role="tab"]');
    if(!tab||tab.disabled||tab.parentElement!==tablist)return;
    activateTab(tablist,tab.dataset.tab,{remember:true});
  });
  tablist.addEventListener('keydown',event=>{
    if(event.altKey||event.ctrlKey||event.metaKey)return;
    if(!['ArrowLeft','ArrowRight','Home','End'].includes(event.key))return;
    const enabledTabs=tabsOf(tablist).filter(tab=>!tab.disabled);
    if(enabledTabs.length===0)return;
    const currentIndex=Math.max(0,enabledTabs.indexOf(document.activeElement));
    let nextIndex=currentIndex;
    if(event.key==='ArrowRight')nextIndex=(currentIndex+1)%enabledTabs.length;
    else if(event.key==='ArrowLeft')nextIndex=(currentIndex-1+enabledTabs.length)%enabledTabs.length;
    else if(event.key==='Home')nextIndex=0;
    else if(event.key==='End')nextIndex=enabledTabs.length-1;
    event.preventDefault();
    activateTab(tablist,enabledTabs[nextIndex].dataset.tab,{focus:true,remember:true});
  });
}
function setupTabs(root){root.querySelectorAll('[role="tablist"]').forEach(setupTabList)}
function appendSpan(fragment,className,text){
  const span=document.createElement('span');span.className=className;span.textContent=text;fragment.append(span);
}
function appendText(fragment,text){fragment.append(document.createTextNode(text))}
function highlightJson(pre){
  const value=pre.textContent,fragment=document.createDocumentFragment();let index=0;
  while(index<value.length){
    const start=index,character=value[index];
    if(character==='"'){
      index++;let escaped=false;
      while(index<value.length){
        const current=value[index++];
        if(current==='"'&&!escaped)break;
        escaped=current==='\\'&&!escaped;if(current!=='\\')escaped=false;
      }
      let lookahead=index;while(lookahead<value.length&&/\s/.test(value[lookahead]))lookahead++;
      appendSpan(fragment,lookahead<value.length&&value[lookahead]===':'?'syn-key':'syn-string',value.slice(start,index));
    }else if(/[0-9-]/.test(character)){
      index++;while(index<value.length&&/[0-9.eE+-]/.test(value[index]))index++;
      appendSpan(fragment,'syn-number',value.slice(start,index));
    }else if(/[A-Za-z]/.test(character)){
      index++;while(index<value.length&&/[A-Za-z]/.test(value[index]))index++;
      appendSpan(fragment,'syn-literal',value.slice(start,index));
    }else if('{}[]:,'.includes(character)){
      appendSpan(fragment,'syn-punct',character);index++;
    }else{appendText(fragment,character);index++}
  }
  pre.replaceChildren(fragment);
}
function highlightXml(pre){
  const value=pre.textContent,fragment=document.createDocumentFragment();let index=0;
  while(index<value.length){
    if(value[index]!=='<'){
      const next=value.indexOf('<',index),end=next<0?value.length:next;
      appendText(fragment,value.slice(index,end));index=end;continue;
    }
    if(value.startsWith('<!--',index)||value.startsWith('<![CDATA[',index)){
      const terminator=value.startsWith('<!--',index)?'-->':']]>',end=value.indexOf(terminator,index);
      const stop=end<0?value.length:end+terminator.length;
      appendSpan(fragment,'syn-comment',value.slice(index,stop));index=stop;continue;
    }
    let end=index+1,quote='';
    while(end<value.length){
      const current=value[end++];
      if(quote){if(current===quote)quote=''}
      else if(current==='"'||current==="'")quote=current;
      else if(current==='>')break;
    }
    appendSpan(fragment,'syn-tag',value.slice(index,end));index=end;
  }
  pre.replaceChildren(fragment);
}
function highlightSelected(root){
  root.querySelectorAll('.formatted-view[data-format]').forEach(pre=>{
    if(pre.dataset.format==='json')highlightJson(pre);
    else if(pre.dataset.format==='xml')highlightXml(pre);
  });
}
function visibleRows(){return httpRows.filter(row=>!row.classList.contains('hidden'))}
function setInspectorStatus(message,warning){
  inspectorOpenStatus.textContent=message||'';
  inspectorOpenStatus.classList.toggle('warning',Boolean(message&&warning));
}
function showReportStatus(message){
  reportStatus.textContent=message;
  reportStatus.classList.toggle('hidden',!message);
}
function updateNavState(){
  const rows=visibleRows();
  const index=rows.indexOf(currentRow);
  const prevDisabled=index<=0;
  const nextDisabled=index<0||index>=rows.length-1;
  const activeWasPrev=document.activeElement===inspectorPrev;
  const activeWasNext=document.activeElement===inspectorNext;
  if((activeWasPrev&&prevDisabled)||(activeWasNext&&nextDisabled)){
    if(activeWasPrev&&!nextDisabled)inspectorNext.focus();
    else if(activeWasNext&&!prevDisabled)inspectorPrev.focus();
    else focusStableInspectorControl();
  }
  inspectorPrev.disabled=prevDisabled;
  inspectorNext.disabled=nextDisabled;
  inspectorPosition.textContent=index<0?'':`${index+1} of ${rows.length}`;
}
function focusStableInspectorControl(){
  const primaryRequestTab=inspectorBody.querySelector('.primary-tab-strip [role="tab"][aria-selected="true"]');
  if(primaryRequestTab&&!primaryRequestTab.disabled){primaryRequestTab.focus();return}
  const websocketMessage=inspectorBody.querySelector('.ws-message-row[aria-selected="true"]');
  if(websocketMessage){websocketMessage.focus();return}
  inspectorClose.focus();
}
function loadRow(row){
  const template=document.getElementById(row.dataset.detail);
  if(!template)return false;
  inspectorBody._disposeHttpViewSearch?.();
  deactivateDynamicViews(inspectorBody);
  inspectorBody._clearHttpViewSearch=null;
  inspectorBody._disposeHttpViewSearch=null;
  const focusWasInBody=inspectorBody.contains(document.activeElement);
  if(currentRow){currentRow.classList.remove('selected');currentRow.setAttribute('aria-selected','false')}
  currentRow=row;row.classList.add('selected');row.setAttribute('aria-selected','true');
  row.scrollIntoView({block:'nearest'});
  inspectorTitle.textContent=row.dataset.summary||'';
  setInspectorStatus('',false);
  Object.keys(preferredTab).forEach(key=>delete preferredTab[key]);
  renderGeneration++;
  const generation=renderGeneration;
  inspectorBody.replaceChildren(template.content.cloneNode(true));
  prepareLazyPayloads(inspectorBody);
  setupTabs(inspectorBody);
  setupTreeToggles(inspectorBody);
  setupCopyControls(inspectorBody);
  setupDynamicViewControls(inspectorBody,generation);
  setupHttpViewSearch(inspectorBody,generation);
  hydrateViewPayloads(inspectorBody,generation);
  updateNavState();
  // Request is always the default-selected primary tab after (re)loading a row (see
  // initialTabFor's "request,response" priority and the preferredTab reset above), so it is a
  // stable, guaranteed-enabled place to land focus when the previously focused element lived
  // inside the body content we just discarded (e.g. a secondary tab or tree item reached via the
  // Alt+Arrow inspector-navigation shortcut).
  if(focusWasInBody){
    const primaryRequestTab=inspectorBody.querySelector('.primary-tab-strip [role="tab"][aria-selected="true"]');
    if(primaryRequestTab)primaryRequestTab.focus();
    else inspectorClose.focus();
  }
  return true;
}
function openInspector(row){
  if(!loadRow(row))return;
  originRow=row;
  document.body.classList.add('inspector-open');
  inspector.showModal();
}
function openInspectorInNewTab(){
  if(!currentRow)return;
  const url=inspectorUrl(currentRow);
  if(!url){
    setInspectorStatus('The current search is too long to preserve safely in a new-tab link. Shorten it, then try again.',true);
    return;
  }
  let popup=null;
  try{popup=window.open(url,'_blank')}catch{}
  if(!popup){
    setInspectorStatus('The browser blocked the new tab. Allow popups for this local report, then try again.',true);
    return;
  }
  try{popup.opener=null}catch{}
  retainSelectionOnClose=true;
  originRow=currentRow;
  closeInspector();
}
function leaveInspectorOnlyMode(){
  inspectorOnly=false;
  document.body.classList.remove('inspector-only');
  inspectorOpenTab.hidden=false;
  inspectorClose.textContent='\u2715';
  inspectorClose.setAttribute('aria-label','Close session inspector');
  inspectorClose.title='';
  const cleanUrl=new URL(window.location.href);
  cleanUrl.hash='';
  try{history.replaceState(null,'',cleanUrl.href)}catch{window.location.hash=''}
}
function closeInspector(){
  inspectorBody._clearHttpViewSearch?.();
  deactivateDynamicViews(inspectorBody);
  inspectorBody.querySelectorAll('.protocol-block').forEach(host=>{
    if(!host._protocolBuildToken)return;
    host._protocolBuildToken=null;
    host._protocolRendered=false;
    host._protocolPromise=null;
    host._protocolLoading=false;
  });
  inspector.close();
}
inspector.addEventListener('close',()=>{
  if(inspectorOnly)leaveInspectorOnlyMode();
  document.body.classList.remove('inspector-open');
  const focusRow=retainSelectionOnClose?currentRow:originRow;
  if(currentRow&&!retainSelectionOnClose){
    currentRow.classList.remove('selected');
    currentRow.setAttribute('aria-selected','false');
    currentRow=null;
  }
  retainSelectionOnClose=false;
  focusRow?.focus();
});
inspectorOpenTab.addEventListener('click',openInspectorInNewTab);
inspectorClose.addEventListener('click',closeInspector);
function navigate(delta){
  const rows=visibleRows();
  const index=rows.indexOf(currentRow);
  if(index<0)return;
  const nextIndex=index+delta;
  if(nextIndex<0||nextIndex>=rows.length)return;
  loadRow(rows[nextIndex]);
}
inspectorPrev.addEventListener('click',()=>navigate(-1));
inspectorNext.addEventListener('click',()=>navigate(1));
inspector.addEventListener('keydown',event=>{
  if(!event.altKey)return;
  if(event.key==='ArrowLeft'){event.preventDefault();navigate(-1)}
  else if(event.key==='ArrowRight'){event.preventDefault();navigate(1)}
});
httpRows.forEach(row=>{
  row.addEventListener('click',()=>openInspector(row));
  row.addEventListener('keydown',event=>{
    if(event.key==='Enter'||event.key===' '){event.preventDefault();openInspector(row)}
  });
});
function enterInspectorOnlyMode(state){
  inspectorOnly=true;
  document.body.classList.add('inspector-only');
  inspectorOpenTab.hidden=true;
  inspectorClose.textContent='Back to sessions';
  inspectorClose.setAttribute('aria-label','Back to sessions');
  inspectorClose.title='Return to the session table in this tab';
  httpSearch.value=state.query;
  httpFilter.value=state.filter;
  applyHttpFilter();
  const rows=visibleRows();
  let row=rows.find(candidate=>candidate.dataset.detail===state.session);
  let status='';
  if(!row&&rows.length){
    row=rows[0];
    status='The requested session is not visible under the restored filters; showing the first matching session.';
  }
  if(row){
    openInspector(row);
    if(status)setInspectorStatus(status,true);
    return;
  }
  inspectorTitle.textContent='Session unavailable';
  inspectorBody.replaceChildren();
  const warning=document.createElement('div');
  warning.className='warning';
  warning.textContent='No HTTP sessions match the restored inspector filters. Use Back to sessions to adjust the filters.';
  inspectorBody.append(warning);
  inspectorPrev.disabled=true;
  inspectorNext.disabled=true;
  inspectorPosition.textContent='0 of 0';
  setInspectorStatus('The requested session could not be opened.',true);
  document.body.classList.add('inspector-open');
  inspector.showModal();
}
const initialInspectorState=parseInspectorState(window.location.hash);
if(initialInspectorState?.error)showReportStatus(initialInspectorState.error);
else if(initialInspectorState)enterInspectorOnlyMode(initialInspectorState);
})();
</script>
</body></html>
""");
        return html.ToString();
    }

    private void AppendHttpSection(
        StringBuilder html,
        IReadOnlyList<HttpSession> sessions,
        IReadOnlyList<WebSocketMessage> webSocketMessages)
    {
        var webSocketsBySession = webSocketMessages
            .GroupBy(message => message.SessionId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<WebSocketMessage>)group.ToArray(), StringComparer.Ordinal);
        html.Append("""
<section class="http-workspace" aria-label="HTTP sessions">
<div class="controls"><input id="httpSearch" type="search" aria-label="Search HTTP sessions" placeholder="Search method, URL, status, content type, endpoints...">
<select id="httpFilter" aria-label="Filter HTTP status or protocol"><option value="">All sessions</option><option value="websocket">WebSocket only</option><option value="mapi">MAPI/NSPI only</option><option value="2">2xx</option><option value="3">3xx</option><option value="4">4xx</option><option value="5">5xx</option><option value="0">Missing/other</option></select>
<button type="button" class="theme-toggle" aria-label="Switch theme" title="Switch theme" aria-pressed="false"><svg aria-hidden="true" viewBox="0 0 24 24"><path d="M9 18h6M10 21h4M8.5 15.5A6 6 0 1 1 15.5 15.5C14.6 16.2 14 17 14 18h-4c0-1-.6-1.8-1.5-2.5Z"/><circle class="theme-bulb-core" cx="12" cy="11" r="2.4"/></svg></button></div>
<div id="reportStatus" class="warning hidden" role="status" aria-live="polite"></div>
<div class="http-table-scroll"><table id="httpTable"><thead><tr><th class="http-time">Time</th><th class="http-id">ID</th><th class="http-method">Method</th><th class="http-protocol">Protocol</th><th class="http-url">URL</th><th class="http-status">Status</th><th class="http-bytes num">Req</th><th class="http-bytes num">Resp</th></tr></thead><tbody>
""");
        for (var index = 0; index < sessions.Count; index++)
        {
            webSocketsBySession.TryGetValue(sessions[index].Id, out var sessionWebSockets);
            AppendHttpRow(html, sessions[index], index, sessionWebSockets ?? []);
        }
        html.Append("</tbody></table></div>\n<div class=\"session-templates\" hidden>\n");
        for (var index = 0; index < sessions.Count; index++)
        {
            webSocketsBySession.TryGetValue(sessions[index].Id, out var sessionWebSockets);
            AppendSessionTemplate(html, sessions[index], index, sessionWebSockets ?? []);
        }
        html.Append("</div></section>");
    }

    private static void AppendHttpRow(
        StringBuilder html,
        HttpSession session,
        int index,
        IReadOnlyList<WebSocketMessage> webSocketMessages)
    {
        var filter = session.StatusCode is >= 200 and <= 599
            ? (session.StatusCode.Value / 100).ToString(CultureInfo.InvariantCulture)
            : "0";
        var search = string.Join(' ', new[]
        {
            session.Id, session.Method, session.Url, session.StatusCode?.ToString(CultureInfo.InvariantCulture),
            session.StatusText, session.ContentType, session.ClientEndpoint, session.ServerEndpoint,
            session.Mapi?.RequestType, session.Mapi?.Endpoint.ToString(),
            webSocketMessages.Count > 0 ? "websocket" : null,
            string.Join(' ', webSocketMessages.Take(100).Select(message =>
                $"{message.Direction} {message.Type} {message.Preview} {message.Warning}"))
        }.Where(value => !string.IsNullOrWhiteSpace(value))).ToLowerInvariant();
        var summary = $"Session {session.Id}: {session.Method ?? "-"} {session.Url ?? "-"}";
        html.Append("<tr tabindex=\"0\" aria-selected=\"false\" aria-label=\"Inspect HTTP session ");
        Attribute(html, session.Id);
        html.Append("\" data-detail=\"http-detail-").Append(index).Append("\" data-filter=\"")
            .Append(filter).Append("\" data-mapi=\"").Append(session.Mapi is not null ? "true" : "false")
            .Append("\" data-websocket=\"").Append(webSocketMessages.Count > 0 ? "true" : "false")
            .Append("\" data-summary=\"");
        Attribute(html, summary);
        html.Append("\" data-search=\"");
        Attribute(html, search);
        html.Append("\"><td class=\"http-time\">");
        AppendHttpTimestamp(html, session.Timestamp);
        html.Append("</td><td class=\"http-id\">");
        Text(html, session.Id);
        html.Append("</td><td class=\"http-method\"><span class=\"badge\">");
        Text(html, session.Method ?? "-");
        html.Append("</span></td><td class=\"http-protocol\">");
        if (session.Mapi is not null)
        {
            html.Append("<span class=\"badge\">");
            Text(html, session.Mapi.Endpoint == MapiEndpoint.AddressBook ? "NSPI" : "MAPI");
            html.Append("</span>");
        }
        else
        {
            html.Append("-");
        }
        html.Append("</td><td class=\"http-url\">");
        Text(html, session.Url ?? "-");
        html.Append("</td><td class=\"http-status\">");
        Text(html, session.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? "-");
        if (!string.IsNullOrWhiteSpace(session.StatusText))
        {
            html.Append(' ');
            Text(html, session.StatusText);
        }
        html.Append("</td><td class=\"http-bytes num\">").Append(FormatBytes(session.RequestBytes))
            .Append("</td><td class=\"http-bytes num\">").Append(FormatBytes(session.ResponseBytes))
            .Append("</td></tr>");
    }

    private void AppendSessionTemplate(
        StringBuilder html,
        HttpSession session,
        int index,
        IReadOnlyList<WebSocketMessage> webSocketMessages)
    {
        html.Append("<template id=\"http-detail-").Append(index).Append("\">");
        if (webSocketMessages.Count > 0)
        {
            AppendWebSocketInspector(html, webSocketMessages);
            html.Append("</template>");
            return;
        }
        AppendSessionDetails(html, session);
        html.Append("<div class=\"primary-tab-strip tab-strip\" role=\"tablist\" aria-label=\"Request or response\" data-side=\"primary\" data-priority=\"request,response\">");
        AppendTabButton(html, "primary", "request", "Request", true, true);
        AppendTabButton(html, "primary", "response", "Response", true, false);
        html.Append("</div><div class=\"primary-panels tab-panels\">");
        html.Append("<div role=\"tabpanel\" id=\"primary-panel-request\" aria-labelledby=\"primary-tab-request\" tabindex=\"0\" class=\"tab-panel primary-panel\">");
        AppendMessagePanel(html, "Request", "request", session.Request, session.Mapi?.Request);
        html.Append("</div>");
        html.Append("<div role=\"tabpanel\" id=\"primary-panel-response\" aria-labelledby=\"primary-tab-response\" tabindex=\"0\" class=\"tab-panel primary-panel hidden\">");
        AppendMessagePanel(html, "Response", "response", session.Response, session.Mapi?.Response);
        html.Append("</div></div></template>");
    }

    private void AppendWebSocketInspector(
        StringBuilder html,
        IReadOnlyList<WebSocketMessage> messages)
    {
        var bounded = new List<WebSocketPayloadMessage>();
        var estimatedBytes = 0;
        foreach (var message in messages.OrderBy(message => message.RecordIndex).Take(MaxWebSocketMessagesPerSession))
        {
            var candidate = BuildWebSocketPayloadMessage(message);
            var candidateBytes = EstimateWebSocketPayloadBytes(candidate);
            if (estimatedBytes + candidateBytes > WebSocketPayloadContentBudgetBytes)
            {
                break;
            }
            bounded.Add(candidate);
            estimatedBytes += candidateBytes;
        }
        var payloadBytes = SerializeWebSocketPayload(bounded, messages.Count);
        while (payloadBytes.Length > WebSocketPayloadMaxDecodedBytes && bounded.Count > 0)
        {
            var keep = bounded.Count / 2;
            bounded.RemoveRange(keep, bounded.Count - keep);
            payloadBytes = SerializeWebSocketPayload(bounded, messages.Count);
        }
        var payload = CreateCompressedPayload(
            "websocket-session",
            payloadBytes,
            WebSocketPayloadMaxDecodedBytes);

        html.Append("<div class=\"websocket-inspector\"");
        if (payload is not null)
        {
            AppendCompressedPayloadAttributes(html, payload);
            html.Append("><div class=\"ws-loading\" role=\"status\">Loading WebSocket traffic...</div>");
        }
        else
        {
            html.Append(" data-payload-error=\"WebSocket traffic exceeds the 32 MiB report safety limit.\">")
                .Append("<div class=\"warning\">WebSocket traffic is too large to include safely in this report.</div>");
        }
        html.Append("</div>");
    }

    private static byte[] SerializeWebSocketPayload(
        IReadOnlyList<WebSocketPayloadMessage> messages,
        int totalMessageCount) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            messages,
            omittedMessages = Math.Max(0, totalMessageCount - messages.Count)
        }, TreePayloadOptions);

    private static int EstimateWebSocketPayloadBytes(WebSocketPayloadMessage message)
    {
        var total = 1024L + (message.Frames.Count * 320L);
        foreach (var value in new[]
        {
            message.Timestamp, message.Direction, message.Type, message.Preview, message.Warning,
            message.Text, message.JsonPretty, message.JsonTree, message.Raw
        })
        {
            if (value is not null) total += Encoding.UTF8.GetByteCount(value);
        }
        return (int)Math.Min(int.MaxValue, total);
    }

    private WebSocketPayloadMessage BuildWebSocketPayloadMessage(WebSocketMessage message)
    {
        BodyPresentation? json = null;
        if (message.Text is not null)
        {
            var body = new BodyPreview
            {
                Length = message.PayloadLength,
                CapturedLength = message.Payload.Length,
                Preview = message.Text,
                IsTruncated = message.IsPayloadTruncated
            };
            var formatted = bodyFormatter.Format(body, null);
            if (formatted.Format == BodyFormat.Json
                && !formatted.IsTruncated
                && BuildJsonTreePayload(formatted.Formatted) is { } tree)
            {
                json = formatted with { Raw = tree };
            }
        }

        return new WebSocketPayloadMessage(
            message.MessageIndex,
            message.RecordIndex,
            FormatWebSocketTimestamp(message.Timestamp),
            message.Direction,
            message.Type,
            BuildWebSocketListType(message),
            message.PayloadLength,
            message.PayloadLength.ToString("N0", CultureInfo.InvariantCulture),
            message.Frames.Count,
            message.IsComplete,
            message.IsFragmented,
            message.IsPayloadTruncated,
            message.Preview,
            BuildWebSocketListPreview(message),
            message.Warning,
            message.Text,
            json?.Formatted,
            json?.Raw,
            BuildWebSocketRaw(message),
            message.Frames.Select(frame => new WebSocketPayloadFrame(
                frame.RecordIndex,
                frame.FiddlerId,
                frame.BitFlags,
                FormatWebSocketTimestamp(frame.Timestamp),
                frame.Direction,
                frame.Opcode,
                frame.Type,
                frame.Final,
                frame.Masked,
                frame.PayloadLength,
                frame.CapturedPayloadLength,
                frame.IsDecoded,
                frame.IsPayloadTruncated,
                frame.Warning)).ToArray());
    }

    private static string BuildWebSocketListPreview(WebSocketMessage message)
    {
        const int maxCharacters = 180;
        string preview;
        if (message.Text is not null)
        {
            preview = CompactWebSocketWhitespace(message.Text);
            if (preview.Length == 0) preview = "(empty text message)";
        }

        else if (message.Type is "Binary")
        {
            preview = CompactWebSocketHex(message.Payload.Span, "Binary");
        }
        else if (message.Type is "Ping" or "Pong" or "Close")
        {
            preview = CompactWebSocketHex(message.Payload.Span, $"{message.Type} control");
        }
        else if (!string.IsNullOrWhiteSpace(message.Warning))
        {
            preview = $"Invalid/partial: {CompactWebSocketWhitespace(message.Warning)}";
        }
        else
        {
            preview = "Invalid or partial WebSocket message";
        }

        return preview.Length <= maxCharacters
            ? preview
            : string.Concat(preview.AsSpan(0, maxCharacters - 1), "\u2026");
    }

    private static string BuildWebSocketListType(WebSocketMessage message)
    {
        if (message.Type is not ("Text" or "Binary" or "Ping" or "Pong" or "Close"))
        {
            return "Invalid";
        }
        return message.IsComplete ? message.Type : "Partial";
    }

    private static string CompactWebSocketWhitespace(string value)
    {
        var output = new StringBuilder(Math.Min(value.Length, 256));
        var whitespace = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                whitespace = output.Length > 0;
                continue;
            }
            if (whitespace)
            {
                output.Append(' ');
                whitespace = false;
            }
            output.Append(character);
            if (output.Length > 512) break;
        }
        return output.ToString().Trim();
    }

    private static string CompactWebSocketHex(ReadOnlySpan<byte> payload, string label)
    {
        const int maxBytes = 16;
        var shown = Math.Min(payload.Length, maxBytes);
        var output = new StringBuilder(label).Append(" (")
            .Append(payload.Length.ToString("N0", CultureInfo.InvariantCulture))
            .Append(payload.Length == 1 ? " byte)" : " bytes)");
        if (shown > 0)
        {
            output.Append(": ");
            for (var index = 0; index < shown; index++)
            {
                if (index > 0) output.Append(' ');
                output.Append(payload[index].ToString("X2", CultureInfo.InvariantCulture));
            }
            if (shown < payload.Length) output.Append(" \u2026");
        }
        return output.ToString();
    }

    private static string BuildWebSocketRaw(WebSocketMessage message)
    {
        var text = new StringBuilder();
        text.Append("Direction: ").Append(WebSocketDirectionLabel(message.Direction)).Append('\n')
            .Append("Type: ").Append(message.Type).Append('\n')
            .Append("Timestamp: ").Append(FormatWebSocketTimestamp(message.Timestamp)).Append('\n')
            .Append("Logical payload length: ").Append(message.PayloadLength.ToString("N0", CultureInfo.InvariantCulture)).Append(" bytes\n")
            .Append("Frames: ").Append(message.Frames.Count.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append("Fragmented: ").Append(message.IsFragmented ? "yes" : "no").Append('\n')
            .Append("Complete: ").Append(message.IsComplete ? "yes" : "no").Append('\n');
        if (!string.IsNullOrWhiteSpace(message.Warning))
        {
            text.Append("Warnings: ").Append(message.Warning).Append('\n');
        }

        text.Append("\nFrame metadata:\n");
        foreach (var frame in message.Frames)
        {
            text.Append("  Frame ").Append(frame.RecordIndex.ToString(CultureInfo.InvariantCulture))
                .Append(" ID=").Append(frame.FiddlerId?.ToString(CultureInfo.InvariantCulture) ?? "?")
                .Append(" BitFlags=").Append(frame.BitFlags?.ToString(CultureInfo.InvariantCulture) ?? "?")
                .Append(": ").Append(frame.Type)
                .Append(" opcode=0x").Append(frame.Opcode < 0 ? "?" : frame.Opcode.ToString("X", CultureInfo.InvariantCulture))
                .Append(" FIN=").Append(frame.Final ? '1' : '0')
                .Append(" masked=").Append(frame.Masked ? "yes" : "no")
                .Append(" payload=").Append(frame.PayloadLength.ToString("N0", CultureInfo.InvariantCulture))
                .Append(" captured=").Append(frame.CapturedPayloadLength.ToString("N0", CultureInfo.InvariantCulture))
                .Append(" direction=").Append(WebSocketDirectionLabel(frame.Direction))
                .Append(" timestamp=").Append(FormatWebSocketTimestamp(frame.Timestamp));
            if (!string.IsNullOrWhiteSpace(frame.Warning))
            {
                text.Append(" warning=").Append(frame.Warning);
            }
            text.Append('\n');
        }

        text.Append("\nPayload:\n");
        if (message.Type == "Text" && message.Text is not null)
        {
            AppendBoundedWebSocketText(text, message.Text);
        }
        else
        {
            var bytes = message.Payload.Span;
            var shown = Math.Min(bytes.Length, MaxWebSocketRawPayloadBytes);
            text.Append(HttpMessageParser.HexPreview(bytes[..shown]));
            if (shown < bytes.Length)
            {
                text.Append("[Payload hex truncated after ")
                    .Append(shown.ToString("N0", CultureInfo.InvariantCulture))
                    .Append(" of ").Append(bytes.Length.ToString("N0", CultureInfo.InvariantCulture))
                    .Append(" retained bytes]\n");
            }
        }
        if (message.IsPayloadTruncated)
        {
            text.Append("[Captured logical payload was truncated by report safety limits]\n");
        }
        return text.Length <= MaxCopyCharacters
            ? text.ToString()
            : string.Concat(
                text.ToString(0, MaxCopyCharacters - 80),
                "\n[Raw WebSocket representation truncated at the 1 MiB copy safety limit]\n");
    }

    private static void AppendBoundedWebSocketText(StringBuilder output, string value)
    {
        var remaining = Math.Max(0, MaxCopyCharacters - output.Length - 96);
        if (value.Length <= remaining)
        {
            output.Append(value);
            if (!value.EndsWith('\n')) output.Append('\n');
            return;
        }
        output.Append(value.AsSpan(0, remaining))
            .Append("\n[Text payload truncated in Raw view at the 1 MiB copy safety limit]\n");
    }

    private static string FormatWebSocketTimestamp(DateTimeOffset? timestamp) =>
        timestamp?.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture) ?? "Unknown";

    private static string WebSocketDirectionLabel(string direction) => direction switch
    {
        "Client" => "Client to server",
        "Server" => "Server to client",
        _ => "Unknown direction"
    };

    private sealed record WebSocketPayloadMessage(
        int MessageIndex,
        int RecordIndex,
        string Timestamp,
        string Direction,
        string Type,
        string ListType,
        long PayloadLength,
        string PayloadLengthText,
        int FrameCount,
        bool IsComplete,
        bool IsFragmented,
        bool IsPayloadTruncated,
        string Preview,
        string ListPreview,
        string? Warning,
        string? Text,
        string? JsonPretty,
        string? JsonTree,
        string Raw,
        IReadOnlyList<WebSocketPayloadFrame> Frames);

    private sealed record WebSocketPayloadFrame(
        int RecordIndex,
        int? FiddlerId,
        int? BitFlags,
        string Timestamp,
        string Direction,
        int Opcode,
        string Type,
        bool Final,
        bool Masked,
        long PayloadLength,
        long CapturedPayloadLength,
        bool IsDecoded,
        bool IsPayloadTruncated,
        string? Warning);

    private static void AppendSessionDetails(StringBuilder html, HttpSession session)
    {
        var hasEndpoints = session.ClientEndpoint is not null || session.ServerEndpoint is not null;
        if (!hasEndpoints && session.Timers.Count == 0 && session.Warnings.Count == 0)
        {
            return;
        }

        html.Append("<details class=\"session-details\"><summary>Session details</summary>");
        if (hasEndpoints)
        {
            html.Append("<p><b>Endpoints:</b> ");
            Text(html, $"{session.ClientEndpoint ?? "?"} -> {session.ServerEndpoint ?? "?"}");
            html.Append("</p>");
        }
        if (session.Timers.Count > 0)
        {
            html.Append("<details><summary>Timers</summary><pre>");
            foreach (var timer in session.Timers)
            {
                Text(html, $"{timer.Key}: {timer.Value}\n");
            }
            html.Append("</pre></details>");
        }
        foreach (var warning in session.Warnings)
        {
            html.Append("<div class=\"warning\">");
            Text(html, warning);
            html.Append("</div>");
        }
        html.Append("</details>");
    }

    private static ImageViewInfo? DetectImageView(HttpMessage message)
    {
        var body = message.Body;
        var bytes = body.DecodedBytes.Span;
        if (body.IsTruncated || bytes.IsEmpty || body.Length != bytes.Length)
        {
            return null;
        }

        var sniffed = SniffImageMime(bytes);
        if (sniffed is null)
        {
            return null;
        }

        var declared = NormalizeMediaType(message.Header("Content-Type"));
        var warning = declared.Length > 0 && !declared.Equals(sniffed, StringComparison.OrdinalIgnoreCase)
            ? $"Declared Content-Type '{declared}' does not match the retained bytes; rendering as '{sniffed}'."
            : null;
        var detection = declared.Equals(sniffed, StringComparison.OrdinalIgnoreCase)
            ? $"Content-Type and retained-byte signature agree on {sniffed}."
            : $"Detected {sniffed} from the retained-byte signature.";
        return new ImageViewInfo(sniffed, detection, ImageAnimation(bytes, sniffed), warning);
    }

    private static string? SniffImageMime(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8
            && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })
            && ValidatePng(bytes))
        {
            return "image/png";
        }
        if (ValidateJpeg(bytes))
        {
            return "image/jpeg";
        }
        if (bytes.Length >= 6
            && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8))
            && ValidateGif(bytes))
        {
            return "image/gif";
        }
        if (ValidateWebP(bytes))
        {
            return "image/webp";
        }
        if (ValidateBmp(bytes))
        {
            return "image/bmp";
        }
        if (ValidateIco(bytes))
        {
            return "image/x-icon";
        }
        return null;
    }

    private static bool ValidatePng(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 20
            || !bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return false;
        }
        var offset = 8;
        var first = true;
        while (offset <= bytes.Length - 12)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);
            if (length > int.MaxValue)
            {
                return false;
            }
            var end = offset + 12L + length;
            if (end > bytes.Length)
            {
                return false;
            }
            var type = bytes.Slice(offset + 4, 4);
            if (first && (!type.SequenceEqual("IHDR"u8) || length != 13))
            {
                return false;
            }
            first = false;
            offset = checked((int)end);
            if (type.SequenceEqual("IEND"u8))
            {
                return length == 0 && offset == bytes.Length;
            }
        }
        return false;
    }

    private static bool ValidateJpeg(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8 || bytes[0] != 0xFF || bytes[1] != 0xD8)
        {
            return false;
        }
        var offset = 2;
        var hasFrame = false;
        var hasScan = false;
        while (offset < bytes.Length)
        {
            if (bytes[offset++] != 0xFF)
            {
                return false;
            }
            while (offset < bytes.Length && bytes[offset] == 0xFF)
            {
                offset++;
            }
            if (offset >= bytes.Length)
            {
                return false;
            }
            var marker = bytes[offset++];
            if (marker == 0xD9)
            {
                return hasFrame && hasScan && offset == bytes.Length;
            }
            if (marker == 0xD8 || marker == 0x00)
            {
                return false;
            }
            if (marker is 0x01 or >= 0xD0 and <= 0xD7)
            {
                continue;
            }
            if (offset > bytes.Length - 2)
            {
                return false;
            }
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);
            if (length < 2 || offset + length > bytes.Length)
            {
                return false;
            }
            if (marker is >= 0xC0 and <= 0xC3 or >= 0xC5 and <= 0xC7 or >= 0xC9 and <= 0xCB or >= 0xCD and <= 0xCF)
            {
                hasFrame = true;
            }
            offset += length;
            if (marker != 0xDA)
            {
                continue;
            }
            hasScan = true;
            while (offset < bytes.Length)
            {
                if (bytes[offset] != 0xFF)
                {
                    offset++;
                    continue;
                }
                if (offset + 1 >= bytes.Length)
                {
                    return false;
                }
                var next = bytes[offset + 1];
                if (next == 0x00 || next is >= 0xD0 and <= 0xD7)
                {
                    offset += 2;
                    continue;
                }
                break;
            }
        }
        return false;
    }

    private static bool ValidateGif(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 14)
        {
            return false;
        }
        var offset = 13;
        if ((bytes[10] & 0x80) != 0)
        {
            offset += 3 * (1 << ((bytes[10] & 0x07) + 1));
        }
        var images = 0;
        while (offset < bytes.Length)
        {
            var introducer = bytes[offset++];
            if (introducer == 0x3B)
            {
                return images > 0 && offset == bytes.Length;
            }
            if (introducer == 0x21)
            {
                if (offset >= bytes.Length)
                {
                    return false;
                }
                offset++;
                if (!SkipGifSubBlocks(bytes, ref offset))
                {
                    return false;
                }
                continue;
            }
            if (introducer != 0x2C || offset > bytes.Length - 9)
            {
                return false;
            }
            var packed = bytes[offset + 8];
            offset += 9;
            if ((packed & 0x80) != 0)
            {
                offset += 3 * (1 << ((packed & 0x07) + 1));
            }
            if (offset >= bytes.Length)
            {
                return false;
            }
            offset++;
            if (!SkipGifSubBlocks(bytes, ref offset))
            {
                return false;
            }
            images++;
        }
        return false;
    }

    private static bool SkipGifSubBlocks(ReadOnlySpan<byte> bytes, ref int offset)
    {
        while (offset < bytes.Length)
        {
            var size = bytes[offset++];
            if (size == 0)
            {
                return true;
            }
            if (offset > bytes.Length - size)
            {
                return false;
            }
            offset += size;
        }
        return false;
    }

    private static bool ValidateWebP(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 20 || !bytes[..4].SequenceEqual("RIFF"u8)
            || !bytes[8..12].SequenceEqual("WEBP"u8)
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) + 8L != bytes.Length)
        {
            return false;
        }
        var offset = 12;
        var imageChunk = false;
        while (offset <= bytes.Length - 8)
        {
            var type = bytes.Slice(offset, 4);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 4)..]);
            var end = offset + 8L + length + (length & 1);
            if (end > bytes.Length)
            {
                return false;
            }
            imageChunk |= type.SequenceEqual("VP8 "u8) || type.SequenceEqual("VP8L"u8) || type.SequenceEqual("VP8X"u8);
            offset = checked((int)end);
        }
        return imageChunk && offset == bytes.Length;
    }

    private static bool ValidateBmp(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 26 || !bytes[..2].SequenceEqual("BM"u8)
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[2..]) != bytes.Length)
        {
            return false;
        }
        var pixelOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes[10..]);
        var dibSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[14..]);
        return dibSize is >= 12 and <= 124
            && 14L + dibSize <= pixelOffset
            && pixelOffset < bytes.Length;
    }

    private static bool ValidateIco(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 22 || !bytes[..4].SequenceEqual(new byte[] { 0, 0, 1, 0 }))
        {
            return false;
        }
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
        var directoryEnd = 6 + (count * 16);
        if (count is 0 or > 256 || directoryEnd > bytes.Length)
        {
            return false;
        }
        var ranges = new List<(uint Offset, uint End)>(count);
        for (var index = 0; index < count; index++)
        {
            var entry = bytes[(6 + (index * 16))..];
            var size = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            if (size == 0 || offset < directoryEnd || offset + (ulong)size > (ulong)bytes.Length)
            {
                return false;
            }
            var image = bytes.Slice(checked((int)offset), checked((int)size));
            if (!ValidatePng(image) && !ValidateIcoDib(image))
            {
                return false;
            }
            ranges.Add((offset, checked(offset + size)));
        }
        ranges.Sort((left, right) => left.Offset.CompareTo(right.Offset));
        if (ranges[0].Offset != directoryEnd)
        {
            return false;
        }
        for (var index = 1; index < ranges.Count; index++)
        {
            if (ranges[index].Offset != ranges[index - 1].End)
            {
                return false;
            }
        }
        return ranges[^1].End == bytes.Length;
    }

    private static bool ValidateIcoDib(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 40)
        {
            return false;
        }
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        var width = BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]);
        var height = BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);
        return headerSize is >= 40 and <= 124
            && headerSize <= bytes.Length
            && width > 0
            && height != 0;
    }

    private static string ImageAnimation(ReadOnlySpan<byte> bytes, string mimeType)
    {
        if (mimeType == "image/gif")
        {
            return "Animation status is not determined for GIF.";
        }
        if (mimeType == "image/png")
        {
            return bytes.IndexOf("acTL"u8) >= 0 ? "Animated image." : "No animation detected.";
        }
        if (mimeType == "image/webp")
        {
            return bytes.IndexOf("ANIM"u8) >= 0 ? "Animated image." : "No animation detected.";
        }
        return "No animation detected.";
    }

    private static AuthViewData? BuildAuthView(HttpMessage message)
    {
        var headers = message.Headers.Where(header => AuthHeaderNames.Contains(header.Name)).ToArray();
        if (headers.Length == 0)
        {
            return null;
        }
        var redacted = new StringBuilder();
        var full = new StringBuilder();
        foreach (var header in headers)
        {
            redacted.Append(header.Name).Append(": ").Append(RedactAuthValue(header.Value)).Append('\n');
            full.Append(header.Name).Append(": ").Append(header.Value).Append('\n');
        }
        return new AuthViewData(redacted.ToString(), full.ToString());
    }

    private static string RedactAuthValue(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return "[redacted]";
        }
        var separator = trimmed.IndexOfAny([' ', '\t', ',']);
        var candidate = separator < 0 ? trimmed : trimmed[..separator];
        var scheme = candidate.ToLowerInvariant() switch
        {
            "basic" => "Basic",
            "bearer" => "Bearer",
            "digest" => "Digest",
            "ntlm" => "NTLM",
            "negotiate" => "Negotiate",
            _ => null
        };
        if (scheme is null)
        {
            return "[redacted]";
        }
        if (scheme != "Digest")
        {
            return $"{scheme} [redacted]";
        }
        var parameterNames = DigestParameterNames(separator < 0 ? string.Empty : trimmed[(separator + 1)..])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(32)
            .Select(name => $"{name}=[redacted]")
            .ToArray();
        return parameterNames.Length == 0
            ? $"{scheme} [redacted]"
            : $"{scheme} {string.Join(", ", parameterNames)}";
    }

    private static IEnumerable<string> DigestParameterNames(string value)
    {
        var segmentStart = 0;
        var quoted = false;
        var escaped = false;
        for (var index = 0; index <= value.Length; index++)
        {
            if (index < value.Length)
            {
                var character = value[index];
                if (quoted)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (character == '\\')
                    {
                        escaped = true;
                    }
                    else if (character == '"')
                    {
                        quoted = false;
                    }
                    continue;
                }
                if (character == '"')
                {
                    quoted = true;
                    continue;
                }
                if (character != ',')
                {
                    continue;
                }
            }

            var segment = value.AsSpan(segmentStart, index - segmentStart).Trim();
            var equals = segment.IndexOf('=');
            var name = equals > 0 ? segment[..equals].Trim() : [];
            if (name.Length is > 0 and <= 64 && IsAuthToken(name))
            {
                yield return name.ToString();
            }
            segmentStart = index + 1;
        }
    }

    private static bool IsAuthToken(ReadOnlySpan<char> value)
    {
        if (!char.IsAsciiLetter(value[0]))
        {
            return false;
        }
        foreach (var character in value[1..])
        {
            if (!char.IsAsciiLetterOrDigit(character)
                && character is not '!' and not '#' and not '$' and not '%' and not '&' and not '\''
                    and not '*' and not '+' and not '-' and not '.' and not '^' and not '_' and not '`'
                    and not '|' and not '~')
            {
                return false;
            }
        }
        return true;
    }

    private static string NormalizeMediaType(string? contentType) =>
        contentType?.Split(';', 2)[0].Trim().ToLowerInvariant() ?? string.Empty;

    private void AppendMessagePanel(
        StringBuilder html,
        string title,
        string side,
        HttpMessage? message,
        MapiMessageParse? protocol)
    {
        var lowerTitle = title.ToLowerInvariant();
        var body = message is null ? null : bodyFormatter.Format(message.Body, message.Header("Content-Type"));
        var jsonEnabled = body is { Format: BodyFormat.Json, CanToggle: true };
        var xmlEnabled = body is { Format: BodyFormat.Xml, CanToggle: true };
        var mapiEnabled = protocol is not null;
        var image = message is null ? null : DetectImageView(message);
        var imageEnabled = image is not null;
        var webView = message is null ? null : SafeHtmlPreviewBuilder.TryCreate(message);
        var webViewEnabled = webView is not null;
        var hexEnabled = message is not null && !message.Body.CapturedBytes.IsEmpty;
        var decodedHexEnabled = hexEnabled
            && message!.Body.WasDecoded
            && !message.Body.DecodedBytes.IsEmpty
            && !message.Body.DecodedBytes.Span.SequenceEqual(message.Body.CapturedBytes.Span);
        var auth = message is null ? null : BuildAuthView(message);
        var authEnabled = auth is not null;
        var headersEnabled = message is not null && message.Headers.Count > 0;
        var rawEnabled = message is not null;
        var anyEnabled = jsonEnabled || xmlEnabled || mapiEnabled || imageEnabled || webViewEnabled
            || hexEnabled || authEnabled || headersEnabled || rawEnabled;
        var jsonCopy = CopyText(
            $"Copy {lowerTitle} JSON pretty text",
            $"{lowerTitle} JSON pretty text",
            jsonEnabled ? body!.Formatted : null);
        var xmlCopy = CopyText(
            $"Copy {lowerTitle} XML pretty text",
            $"{lowerTitle} XML pretty text",
            xmlEnabled ? body!.Formatted : null);
        var mapiCopy = new CopySource(
            $"Copy {lowerTitle} MAPI protocol tree",
            $"{lowerTitle} MAPI protocol tree",
            null,
            mapiEnabled ? "mapi" : null);
        var imageCopy = new CopySource(
            $"Copy {lowerTitle} image metadata",
            $"{lowerTitle} image metadata",
            null,
            imageEnabled ? "image" : null);
        var webViewCopy = CopyText(
            $"Copy {lowerTitle} HTML source",
            $"{lowerTitle} HTML source",
            webView?.Source);
        var hexCopy = new CopySource(
            $"Copy {lowerTitle} hex view",
            $"{lowerTitle} hex view",
            null,
            hexEnabled ? "hex" : null);
        var authCopy = CopyText(
            $"Copy redacted {lowerTitle} authentication headers",
            $"redacted {lowerTitle} authentication headers",
            auth?.Redacted);
        var headersCopy = CopyText(
            $"Copy {lowerTitle} headers",
            $"{lowerTitle} headers",
            message is not null ? BuildHeadersText(message) : default);
        var rawCopy = CopyText(
            $"Copy {lowerTitle} raw message",
            $"{lowerTitle} raw message",
            rawEnabled ? BuildRawText(message!, body!) : default);
        html.Append("<section class=\"message-panel\"");
        AppendCopyModelAttribute(
            html,
            jsonCopy,
            xmlCopy,
            webViewCopy,
            webView,
            authCopy,
            auth?.Full,
            headersCopy,
            rawCopy,
            message,
            body);
        html.Append('>');

        string? initial = !anyEnabled
            ? null
            : mapiEnabled
                ? "mapi"
                : jsonEnabled
                    ? "json"
                    : xmlEnabled
                        ? "xml"
                        : rawEnabled
                            ? "raw"
                            : "headers";

        AppendTabStrip(
            html,
            title,
            side,
            jsonEnabled,
            xmlEnabled,
            mapiEnabled,
            imageEnabled,
            webViewEnabled,
            hexEnabled,
            authEnabled,
            headersEnabled,
            rawEnabled,
            initial);

        html.Append("<div class=\"tab-panels\">");
        AppendTabPanel(
            html, side, "json", initial == "json", jsonEnabled, jsonCopy,
            $"JSON view is not available: the {lowerTitle} body is not recognized, valid JSON.",
            jsonEnabled ? inner => AppendStructuredBody(inner, body!, "json") : null);
        AppendTabPanel(
            html, side, "xml", initial == "xml", xmlEnabled, xmlCopy,
            $"XML view is not available: the {lowerTitle} body is not recognized, valid XML.",
            xmlEnabled ? inner => AppendStructuredBody(inner, body!, "xml") : null);
        AppendTabPanel(
            html, side, "mapi", initial == "mapi", mapiEnabled, mapiCopy,
            $"MAPI view is not available: no protocol tree was parsed for this {lowerTitle}.",
            mapiEnabled ? inner => AppendProtocol(inner, protocol!) : null);
        AppendAuxiliaryTabPanel(
            html, side, "image", false, imageEnabled, imageCopy,
            $"Image view is not available: the {lowerTitle} body is not a complete retained PNG, JPEG, GIF, WebP, BMP, or ICO image.",
            imageEnabled ? inner => AppendImageView(inner, image!, message!.Body) : null);
        AppendAuxiliaryTabPanel(
            html, side, "webview", false, webViewEnabled, webViewCopy,
            $"WebView is not available: the {lowerTitle} body is not complete retained HTML or XHTML.",
            webViewEnabled ? inner => AppendWebView(inner, webView!) : null);
        AppendAuxiliaryTabPanel(
            html, side, "hex", false, hexEnabled, hexCopy,
            $"HexView is not available: no captured {lowerTitle} body bytes were retained.",
            hexEnabled ? inner => AppendHexView(inner, message!.Body, decodedHexEnabled) : null);
        AppendAuxiliaryTabPanel(
            html, side, "auth", false, authEnabled, authCopy,
            $"Auth view is not available: no Authorization, Proxy-Authorization, WWW-Authenticate, or Proxy-Authenticate header was captured for this {lowerTitle}.",
            authEnabled ? AppendAuthView : null);
        AppendTabPanel(
            html, side, "headers", initial == "headers", headersEnabled, headersCopy,
            message is null
                ? $"No {lowerTitle} entry was captured."
                : $"No headers were captured for this {lowerTitle}.",
            headersEnabled ? inner => AppendHeadersOnly(inner, headersCopy) : null);
        AppendTabPanel(
            html, side, "raw", initial == "raw", rawEnabled, rawCopy,
            $"No {lowerTitle} entry was captured.",
            rawEnabled ? inner => AppendRawView(inner, message!, body!, headersCopy, rawCopy) : null);
        if (!anyEnabled)
        {
            html.Append("<div class=\"tab-empty\">No ").Append(lowerTitle).Append(" entry was captured.</div>");
        }
        html.Append("</div></section>");
    }

    private static void AppendTabStrip(
        StringBuilder html,
        string title,
        string side,
        bool jsonEnabled,
        bool xmlEnabled,
        bool mapiEnabled,
        bool imageEnabled,
        bool webViewEnabled,
        bool hexEnabled,
        bool authEnabled,
        bool headersEnabled,
        bool rawEnabled,
        string? initial)
    {
        html.Append("<div class=\"tab-strip\" role=\"tablist\" aria-label=\"").Append(title)
            .Append(" detail views\" data-side=\"").Append(side).Append("\" data-priority=\"mapi,json,xml,raw,headers\">");
        AppendTabButton(html, side, "json", "JSON", jsonEnabled, initial == "json");
        AppendTabButton(html, side, "xml", "XML", xmlEnabled, initial == "xml");
        AppendTabButton(html, side, "mapi", "MAPI", mapiEnabled, initial == "mapi");
        AppendTabButton(html, side, "image", "Image", imageEnabled, false);
        AppendTabButton(html, side, "webview", "WebView", webViewEnabled, false);
        AppendTabButton(html, side, "hex", "HexView", hexEnabled, false);
        AppendTabButton(html, side, "auth", "Auth", authEnabled, false);
        AppendTabButton(html, side, "headers", "Headers", headersEnabled, initial == "headers");
        AppendTabButton(html, side, "raw", "Raw", rawEnabled, initial == "raw");
        html.Append("</div>");
    }

    private static void AppendTabButton(
        StringBuilder html,
        string side,
        string key,
        string label,
        bool enabled,
        bool selected)
    {
        html.Append("<button type=\"button\" role=\"tab\" id=\"").Append(side).Append("-tab-").Append(key)
            .Append("\" aria-controls=\"").Append(side).Append("-panel-").Append(key)
            .Append("\" aria-selected=\"").Append(selected ? "true" : "false")
            .Append("\" data-tab=\"").Append(key)
            .Append("\" tabindex=\"").Append(selected ? "0" : "-1").Append('"');
        if (!enabled)
        {
            html.Append(" disabled aria-disabled=\"true\"");
        }
        html.Append('>').Append(label).Append("</button>");
    }

    private static void AppendTabPanel(
        StringBuilder html,
        string side,
        string key,
        bool selected,
        bool enabled,
        CopySource copy,
        string unavailableMessage,
        Action<StringBuilder>? content)
    {
        html.Append("<div role=\"tabpanel\" id=\"").Append(side).Append("-panel-").Append(key)
            .Append("\" aria-labelledby=\"").Append(side).Append("-tab-").Append(key)
            .Append("\" tabindex=\"0\" class=\"tab-panel tab-panel-").Append(key);
        if (!selected)
        {
            html.Append(" hidden");
        }
        html.Append('"').Append('>');
        AppendCopyToolbar(html, key, enabled, copy);
        if (enabled && content is not null)
        {
            content(html);
        }
        else
        {
            html.Append("<div class=\"tab-empty\">");
            Text(html, unavailableMessage);
            html.Append("</div>");
        }
        html.Append("</div>");
    }

    private static void AppendAuxiliaryTabPanel(
        StringBuilder html,
        string side,
        string key,
        bool selected,
        bool enabled,
        CopySource copy,
        string unavailableMessage,
        Action<StringBuilder>? content)
    {
        if (enabled)
        {
            AppendTabPanel(html, side, key, selected, true, copy, unavailableMessage, content);
            return;
        }
        html.Append("<div role=\"tabpanel\" id=\"").Append(side).Append("-panel-").Append(key)
            .Append("\" aria-labelledby=\"").Append(side).Append("-tab-").Append(key)
            .Append("\" tabindex=\"0\" class=\"tab-panel tab-panel-").Append(key).Append(" hidden\">")
            .Append("<div class=\"tab-empty\">");
        Text(html, unavailableMessage);
        html.Append("</div></div>");
    }

    private static void AppendCopyToolbar(StringBuilder html, string key, bool enabled, CopySource source)
    {
        html.Append("<div class=\"copy-toolbar\"><button type=\"button\" class=\"copy-button\" aria-label=\"");
        Attribute(html, source.AccessibleName);
        html.Append("\" data-copy-description=\"");
        Attribute(html, source.Description);
        html.Append('"');
        if (!enabled)
        {
            html.Append(" disabled aria-disabled=\"true\"");
        }
        else if (source.Kind is not null)
        {
            html.Append(" data-copy-kind=\"");
            Attribute(html, source.Kind);
            html.Append('"');
        }
        else if (source.Error is not null)
        {
            html.Append(" data-copy-error=\"");
            Attribute(html, source.Error);
            html.Append('"');
        }
        else if (source.Text is not null)
        {
            html.Append(" data-copy-key=\"");
            Attribute(html, key);
            html.Append('"');
        }
        html.Append(">Copy</button><span class=\"copy-status\" role=\"status\" aria-live=\"polite\"></span></div>");
    }

    private static void AppendCopyModelAttribute(
        StringBuilder html,
        CopySource json,
        CopySource xml,
        CopySource webView,
        SafeHtmlPreview? webViewData,
        CopySource auth,
        string? authFull,
        CopySource headers,
        CopySource raw,
        HttpMessage? message,
        BodyPresentation? body)
    {
        var model = new Dictionary<string, string>(StringComparer.Ordinal);
        Add("json", json);
        Add("xml", xml);
        Add("webview", webView);
        Add("auth", auth);
        Add("headers", headers);
        Add("raw", raw);
        if (webViewData is not null)
        {
            model.Add("webViewDocument", webViewData.Document);
        }
        if (authFull is not null)
        {
            var secret = CreateCompressedPayload(
                "auth-secret",
                Encoding.UTF8.GetBytes(authFull),
                MaxCopyCharacters);
            if (secret is not null)
            {
                model.Add("authSecret", secret.Base64);
                model.Add("authSecretBytes", secret.DecodedBytes.ToString(CultureInfo.InvariantCulture));
            }
        }
        if (message is not null && !message.Body.CapturedBytes.IsEmpty)
        {
            model.Add("capturedBytes", Convert.ToBase64String(message.Body.CapturedBytes.Span));
        }
        if (message is not null && message.Body.WasDecoded && !message.Body.DecodedBytes.IsEmpty)
        {
            model.Add(
                "decodedBytes",
                Convert.ToBase64String(message.Body.DecodedBytes.Span[..Math.Min(
                    message.Body.DecodedBytes.Length,
                    HexViewBytesLimit)]));
        }
        if (message is not null && DetectImageView(message) is not null)
        {
            model.Add("imageBytes", Convert.ToBase64String(message.Body.DecodedBytes.Span));
        }
        if (message is not null && headers.Text is null)
        {
            model.Add("displayHeaders", BuildHeadersDisplayText(message));
        }
        if (message is not null && body is not null && raw.Text is null)
        {
            model.Add("displayRawBody", BoundDisplayText(
                body.Raw,
                "[Body display truncated at the 256 KiB rendering limit.]"));
            if (message.Body.CapturedBytesPreview is not null)
            {
                var captured = message.Body.CapturedBytesPreview
                    + (message.Body.CapturedBytesPreviewTruncated
                        ? "\n[Captured byte preview truncated]"
                        : string.Empty);
                model.Add("displayCaptured", BoundDisplayText(
                    captured,
                    "[Captured-byte display truncated at the 256 KiB rendering limit.]"));
            }
        }
        if (model.Count == 0)
        {
            return;
        }

        var payload = CreateCompressedPayload(
            "copy-model",
            JsonSerializer.SerializeToUtf8Bytes(model),
            CopyPayloadMaxDecodedBytes);
        if (payload is not null)
        {
            AppendCompressedPayloadAttributes(html, payload);
        }
        else
        {
            html.Append(" data-payload-error=\"Copy/display data exceeds the compressed report safety limit.\"");
        }

        void Add(string key, CopySource source)
        {
            if (source.Text is not null)
            {
                model.Add(key, source.Text);
            }
        }
    }

    private static string BuildHeadersDisplayText(HttpMessage message)
    {
        const string marker = "[Header display truncated at the 256 KiB rendering limit.]\n";
        var text = new StringBuilder(Math.Min(MaxHydratedDisplayCharacters, 16 * 1024));
        if (!Append(message.StartLine + "\n"))
        {
            return text.ToString();
        }
        foreach (var header in message.Headers)
        {
            if (!Append($"{header.Name}: {header.Value}\n"))
            {
                break;
            }
        }
        return text.ToString();

        bool Append(string value)
        {
            var remaining = MaxHydratedDisplayCharacters - marker.Length - text.Length;
            if (remaining <= 0)
            {
                text.Append(marker);
                return false;
            }
            if (value.Length <= remaining)
            {
                text.Append(value);
                return true;
            }
            text.Append(value.AsSpan(0, remaining)).Append(marker);
            return false;
        }
    }

    private static string BoundDisplayText(string value, string marker)
    {
        if (value.Length <= MaxHydratedDisplayCharacters)
        {
            return value;
        }
        var prefixLength = MaxHydratedDisplayCharacters - marker.Length - 1;
        return string.Concat(value.AsSpan(0, prefixLength), "\n", marker);
    }

    private static CompressedPayload? CreateCompressedPayload(
        string type,
        string json,
        int maxDecodedBytes) =>
        CreateCompressedPayload(type, Encoding.UTF8.GetBytes(json), maxDecodedBytes);

    private static CompressedPayload? CreateCompressedPayload(
        string type,
        byte[] json,
        int maxDecodedBytes)
    {
        if (json.Length > maxDecodedBytes)
        {
            return null;
        }

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(json);
        }
        return new CompressedPayload(
            type,
            Convert.ToBase64String(output.GetBuffer(), 0, checked((int)output.Length)),
            json.Length);
    }

    private static void AppendCompressedPayloadAttributes(StringBuilder html, CompressedPayload payload)
    {
        html.Append(" data-compressed-payload=\"").Append(payload.Base64)
            .Append("\" data-payload-type=\"");
        Attribute(html, payload.Type);
        html.Append("\" data-payload-version=\"").Append(PayloadVersion)
            .Append("\" data-payload-decoded-bytes=\"").Append(payload.DecodedBytes).Append('"');
    }

    private static CopySource CopyText(string accessibleName, string description, string? text)
    {
        if (text is null)
        {
            return new CopySource(accessibleName, description, null);
        }
        return text.Length <= MaxCopyCharacters
            ? new CopySource(accessibleName, description, text)
            : new CopySource(
                accessibleName,
                description,
                null,
                Error: "Copy source exceeds the 1 MiB safety limit.");
    }

    private static CopySource CopyText(string accessibleName, string description, BuiltCopyText text) =>
        text.TooLarge
            ? new CopySource(
                accessibleName,
                description,
                null,
                Error: "Copy source exceeds the 1 MiB safety limit.")
            : new CopySource(
                accessibleName,
                description,
                text.Text,
                BodyStart: text.BodyStart,
                BodyLength: text.BodyLength,
                CapturedStart: text.CapturedStart,
                CapturedLength: text.CapturedLength);

    private static BuiltCopyText BuildHeadersText(HttpMessage message)
    {
        var text = new StringBuilder();
        if (!AppendCopyText(text, message.StartLine + "\n"))
        {
            return new BuiltCopyText(null, true);
        }
        foreach (var header in message.Headers)
        {
            if (!AppendCopyText(text, $"{header.Name}: {header.Value}\n"))
            {
                return new BuiltCopyText(null, true);
            }
        }
        return new BuiltCopyText(text.ToString(), false);
    }

    private static BuiltCopyText BuildRawText(HttpMessage message, BodyPresentation body)
    {
        var source = message.Body;
        var text = new StringBuilder();
        if (!AppendCopyText(text, "Original headers\n")
            || !AppendCopyText(text, message.StartLine + "\n"))
        {
            return new BuiltCopyText(null, true);
        }
        foreach (var header in message.Headers)
        {
            if (!AppendCopyText(text, $"{header.Name}: {header.Value}\n"))
            {
                return new BuiltCopyText(null, true);
            }
        }

        var size = source.WasDecoded
            ? $"{FormatBytes(source.Length)} decoded; {FormatBytes(source.CapturedLength)} captured"
            : FormatBytes(source.Length);
        if (!AppendCopyText(text, $"\n{(source.WasDecoded ? "Decoded body" : "Body")} ({size})\n")
            || !AppendCopyText(text, $"Format: {body.Label}\n")
            || !AppendCopyText(text, $"Status: {body.Status}\n"))
        {
            return new BuiltCopyText(null, true);
        }
        if (source.DecodingStatus is not null
            && !AppendCopyText(text, $"Decode status: {source.DecodingStatus}\n"))
        {
            return new BuiltCopyText(null, true);
        }
        var bodyStart = text.Length;
        if (!AppendCopyText(text, body.Raw))
        {
            return new BuiltCopyText(null, true);
        }
        var bodyLength = text.Length - bodyStart;
        if (body.IsTruncated
            && !AppendCopyText(text, "\n[Body preview truncated; the complete body is not retained in this report.]"))
        {
            return new BuiltCopyText(null, true);
        }
        var capturedStart = -1;
        var capturedLength = 0;
        if (source.CapturedBytesPreview is not null)
        {
            if (!AppendCopyText(text, "\n\nCaptured bytes (pre-decode)\n"))
            {
                return new BuiltCopyText(null, true);
            }
            capturedStart = text.Length;
            if (!AppendCopyText(text, source.CapturedBytesPreview)
                || (source.CapturedBytesPreviewTruncated
                    && !AppendCopyText(text, "\n[Captured byte preview truncated]")))
            {
                return new BuiltCopyText(null, true);
            }
            capturedLength = text.Length - capturedStart;
        }
        return new BuiltCopyText(
            text.ToString(),
            false,
            bodyStart,
            bodyLength,
            capturedStart,
            capturedLength);
    }

    private static bool AppendCopyText(StringBuilder target, string value)
    {
        if (target.Length + (long)value.Length > MaxCopyCharacters)
        {
            return false;
        }
        target.Append(value);
        return true;
    }

    private static void AppendHeadersOnly(StringBuilder html, CopySource headersCopy)
    {
        html.Append("<pre class=\"headers\" data-copy-field=\"")
            .Append(headersCopy.Text is null ? "displayHeaders" : "headers")
            .Append("\"></pre>");
    }

    private static void AppendStructuredBody(StringBuilder html, BodyPresentation body, string format)
    {
        var treeJson = format == "json" ? BuildJsonTreePayload(body.Formatted) : BuildXmlTreePayload(body.Formatted);
        var treePayload = treeJson is null
            ? null
            : CreateCompressedPayload($"{format}-tree", treeJson, TreePayloadMaxDecodedBytes);
        var treeAvailable = treePayload is not null;

        html.Append("<div class=\"structured-body\" data-format=\"").Append(format).Append("\">");
        html.Append("<div class=\"format-meta\"><span class=\"format-badge\">");
        Text(html, body.Label);
        html.Append("</span><span class=\"format-status\">");
        Text(html, body.Status);
        html.Append("</span></div>");

        html.Append("<div class=\"tree-toolbar\"><div class=\"view-toggle\" role=\"group\" aria-label=\"")
            .Append(format == "json" ? "JSON" : "XML").Append(" view mode\">")
            .Append("<button type=\"button\" data-view=\"tree\" aria-pressed=\"").Append(treeAvailable ? "true" : "false").Append('"');
        if (!treeAvailable)
        {
            html.Append(" disabled");
        }
        html.Append(">Tree</button>")
            .Append("<button type=\"button\" data-view=\"pretty\" aria-pressed=\"").Append(treeAvailable ? "false" : "true").Append("\">Pretty Text</button></div>");
        if (treeAvailable)
        {
            html.Append("<button type=\"button\" class=\"tree-expand-all\">Expand all</button>")
                .Append("<button type=\"button\" class=\"tree-collapse-all\">Collapse all</button>");
        }
        html.Append("</div>");

        html.Append("<div class=\"tree-subview\"");
        if (treeAvailable)
        {
            AppendCompressedPayloadAttributes(html, treePayload!);
        }
        else
        {
            html.Append(" hidden");
        }
        html.Append('>');
        html.Append(treeAvailable
            ? "<span class=\"muted\">Tree loads when this session is selected.</span>"
            : "<div class=\"tab-empty\">Tree view is not available for this body; showing Pretty Text.</div>");
        html.Append("</div>");

        html.Append("<div class=\"pretty-subview").Append(treeAvailable ? " hidden" : "")
            .Append("\"><pre class=\"body-view formatted-view\" data-format=\"").Append(format)
            .Append("\" data-copy-field=\"").Append(format).Append("\"></pre></div></div>");
    }

    private static void AppendRawView(
        StringBuilder html,
        HttpMessage message,
        BodyPresentation body,
        CopySource headersCopy,
        CopySource rawCopy)
    {
        var source = message.Body;
        html.Append("<h4>Original headers</h4>");
        AppendHeadersOnly(html, headersCopy);
        html.Append("<h4>").Append(source.WasDecoded ? "Decoded body" : "Body").Append(" <span class=\"muted\">(");
        if (source.WasDecoded)
        {
            html.Append(FormatBytes(source.Length)).Append(" decoded; ")
                .Append(FormatBytes(source.CapturedLength)).Append(" captured");
        }
        else
        {
            html.Append(FormatBytes(source.Length));
        }
        html.Append(")</span></h4><div class=\"format-meta\"><span class=\"format-badge\">");
        Text(html, body.Label);
        html.Append("</span><span class=\"format-status\">");
        Text(html, body.Status);
        html.Append("</span></div>");
        if (source.DecodingStatus is not null)
        {
            html.Append(source.WasDecoded ? "<div class=\"decode-status\">" : "<div class=\"warning\">");
            Text(html, source.DecodingStatus);
            html.Append("</div>");
        }
        html.Append("<pre class=\"body-view\" data-copy-field=\"");
        if (rawCopy.Text is null)
        {
            html.Append("displayRawBody");
        }
        else
        {
            html.Append("raw\" data-copy-start=\"").Append(rawCopy.BodyStart)
                .Append("\" data-copy-length=\"").Append(rawCopy.BodyLength);
        }
        html.Append("\"></pre>");
        if (source.CapturedBytesPreview is not null)
        {
            html.Append("<details class=\"captured-bytes\"><summary>Captured bytes (pre-decode)</summary><pre data-copy-field=\"");
            if (rawCopy.Text is null)
            {
                html.Append("displayCaptured");
            }
            else
            {
                html.Append("raw\" data-copy-start=\"").Append(rawCopy.CapturedStart)
                    .Append("\" data-copy-length=\"").Append(rawCopy.CapturedLength);
            }
            html.Append("\"></pre></details>");
        }
    }

    private static void AppendImageView(
        StringBuilder html,
        ImageViewInfo image,
        BodyPreview body)
    {
        html.Append("<div class=\"image-view\" data-byte-field=\"")
            .Append("imageBytes")
            .Append("\" data-image-mime=\"");
        Attribute(html, image.MimeType);
        html.Append("\"><div class=\"image-meta\"><span>");
        Text(html, image.MimeType);
        html.Append("</span><span>");
        Text(html, $"{body.Length:N0} bytes");
        html.Append("</span><span>");
        Text(html, image.Animation);
        html.Append("</span><span class=\"image-dimensions\">Dimensions load with the image.</span></div>");
        html.Append("<div class=\"format-status\">");
        Text(html, image.Detection);
        html.Append("</div>");
        if (image.Warning is not null)
        {
            html.Append("<div class=\"warning\">");
            Text(html, image.Warning);
            html.Append("</div>");
        }
        html.Append("<div class=\"image-stage\"><img alt=\"Captured HTTP image\" decoding=\"async\" referrerpolicy=\"no-referrer\" draggable=\"false\"></div>")
            .Append("<div class=\"image-load-status\" role=\"status\" aria-live=\"polite\">Image loads when this tab is selected.</div></div>");
    }

    private static void AppendWebView(StringBuilder html, SafeHtmlPreview preview)
    {
        html.Append("<div class=\"webview\" data-mode=\"rendered\"><div class=\"webview-toolbar\">")
            .Append("<div class=\"webview-mode\" role=\"group\" aria-label=\"WebView display mode\">")
            .Append("<button type=\"button\" data-webview-mode=\"rendered\" aria-pressed=\"true\">Rendered</button>")
            .Append("<button type=\"button\" data-webview-mode=\"source\" aria-pressed=\"false\">Source</button></div>")
            .Append("<span class=\"format-status\">");
        Text(html, preview.Detection);
        html.Append("</span></div>")
            .Append("<div class=\"webview-status\" role=\"status\" aria-live=\"polite\">Inert preview loads only when this tab is selected.</div>")
            .Append("<div class=\"webview-frame-host\"></div>")
            .Append("<pre class=\"webview-source hidden\" tabindex=\"0\" aria-label=\"Original decoded captured HTML source\" data-copy-field=\"webview\"></pre></div>");
    }

    private static void AppendHexView(
        StringBuilder html,
        BodyPreview body,
        bool decodedEnabled)
    {
        html.Append("<div class=\"hex-view\" data-captured-length=\"")
            .Append(body.CapturedLength)
            .Append("\" data-decoded-length=\"").Append(body.Length)
            .Append("\" data-captured-retained=\"").Append(body.CapturedBytes.Length)
            .Append("\" data-decoded-retained=\"").Append(Math.Min(body.DecodedBytes.Length, HexViewBytesLimit))
            .Append("\"><div class=\"hex-toolbar\"><span class=\"hex-source-label\">Captured body bytes</span>")
            .Append("<label>Source <select class=\"hex-source\" aria-label=\"Hex byte source\"><option value=\"captured\">Captured</option><option value=\"decoded\"");
        if (!decodedEnabled)
        {
            html.Append(" disabled");
        }
        html.Append(">Decoded</option></select></label></div>");
        if (body.RemovedEncodings.Count > 0)
        {
            html.Append("<div class=\"decode-status\">Removed encodings: ");
            Text(html, string.Join(" -> ", body.RemovedEncodings));
            html.Append("</div>");
        }
        html.Append("<pre class=\"hex-dump\" tabindex=\"0\" aria-label=\"Captured body byte hex dump\"></pre>")
            .Append("<div class=\"hex-status muted\"></div></div>");
    }

    private static void AppendAuthView(StringBuilder html)
    {
        html.Append("<div class=\"auth-view\" data-revealed=\"false\"><div class=\"warning\">Authentication values are redacted. Reveal only when it is safe to display captured credentials or challenge tokens.</div>")
            .Append("<button type=\"button\" class=\"auth-reveal\" aria-pressed=\"false\" aria-label=\"Reveal full authentication header values\">Reveal values</button>")
            .Append("<pre class=\"auth-headers\" data-copy-field=\"auth\"></pre>")
            .Append("<div class=\"auth-status\" role=\"status\" aria-live=\"polite\">Values are redacted.</div></div>");
    }

    private static void AppendProtocol(StringBuilder html, MapiMessageParse protocol)
    {
        html.Append("<div class=\"protocol-meta muted\">(")
            .Append(protocol.Complete ? "complete" : "partial").Append(", ")
            .Append(protocol.ParsedBytes.ToString("N0", CultureInfo.InvariantCulture)).Append(" of ")
            .Append(protocol.TotalBytes.ToString("N0", CultureInfo.InvariantCulture))
            .Append(" bytes)</div>");
        var warnings = protocol.Warnings.Take(50).ToArray();
        foreach (var warning in warnings)
        {
            html.Append("<div class=\"warning\">");
            Text(html, warning);
            html.Append("</div>");
        }
        var omittedWarnings = protocol.Warnings.Length - warnings.Length;
        if (omittedWarnings > 0)
        {
            html.Append("<div class=\"warning\">");
            Text(html, $"{omittedWarnings:N0} additional protocol warning(s) omitted from the report.");
            html.Append("</div>");
        }
        var payload = JsonSerializer.Serialize(new
        {
            root = ToProtocolData(protocol.Root),
            complete = protocol.Complete,
            parsedBytes = protocol.ParsedBytes,
            totalBytes = protocol.TotalBytes,
            warnings,
            omittedWarnings
        });
        var compressed = CreateCompressedPayload(
            "mapi-protocol",
            payload,
            ProtocolPayloadMaxDecodedBytes);
        if (compressed is null)
        {
            html.Append("<div class=\"protocol-block warning\">Protocol tree exceeds the 32 MiB report safety limit and was not embedded.</div>");
            return;
        }
        html.Append("<div class=\"protocol-block\"");
        AppendCompressedPayloadAttributes(html, compressed);
        html.Append("><span class=\"muted\">Protocol tree loads when this view is selected.</span></div>");
    }

    private static object ToProtocolData(MapiNode node) => new
    {
        name = node.Name,
        kind = node.Kind.ToString(),
        offset = node.Offset,
        length = node.Length,
        value = node.Value,
        children = node.Children.Select(ToProtocolData).ToArray()
    };

    private sealed class TreeNode
    {
        public required string Kind { get; init; }
        public string? Name { get; init; }
        public bool IsIndex { get; init; }
        public string? Value { get; init; }
        public int? Count { get; init; }
        public int Omitted { get; init; }
        public bool Truncated { get; init; }
        public bool DepthLimited { get; init; }
        public List<TreeNode>? Attrs { get; init; }
        public List<TreeNode>? Children { get; init; }
    }

    private sealed class TreeBudget
    {
        public int NodeCount;
    }

    private static string? BuildJsonTreePayload(string formattedJson)
    {
        try
        {
            using var document = JsonDocument.Parse(formattedJson);
            var budget = new TreeBudget();
            var root = BuildJsonNode(document.RootElement, null, false, 0, budget);
            return JsonSerializer.Serialize(root, TreePayloadOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static TreeNode BuildJsonNode(JsonElement element, string? name, bool isIndex, int depth, TreeBudget budget)
    {
        budget.NodeCount++;

        if (depth >= TreeMaxDepth && element.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            // The subtree itself is not descended into (that's the whole point of the depth
            // limit), but the immediate property/item count is cheap to report accurately here
            // (no recursion needed) so the truncated label doesn't falsely read "0 properties"/
            // "0 items".
            var immediateCount = element.ValueKind == JsonValueKind.Object
                ? element.EnumerateObject().Count()
                : element.GetArrayLength();
            return new TreeNode
            {
                Kind = element.ValueKind == JsonValueKind.Object ? "object" : "array",
                Name = name,
                IsIndex = isIndex,
                Count = immediateCount,
                DepthLimited = true
            };
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var properties = element.EnumerateObject().ToList();
                var children = new List<TreeNode>();
                var omitted = 0;
                foreach (var property in properties)
                {
                    if (children.Count >= TreeMaxChildrenPerNode || budget.NodeCount >= TreeMaxNodes)
                    {
                        omitted = properties.Count - children.Count;
                        break;
                    }
                    children.Add(BuildJsonNode(property.Value, property.Name, false, depth + 1, budget));
                }
                return new TreeNode { Kind = "object", Name = name, IsIndex = isIndex, Count = properties.Count, Omitted = omitted, Children = children };
            }
            case JsonValueKind.Array:
            {
                var items = element.EnumerateArray().ToList();
                var children = new List<TreeNode>();
                var omitted = 0;
                for (var i = 0; i < items.Count; i++)
                {
                    if (children.Count >= TreeMaxChildrenPerNode || budget.NodeCount >= TreeMaxNodes)
                    {
                        omitted = items.Count - children.Count;
                        break;
                    }
                    children.Add(BuildJsonNode(items[i], i.ToString(CultureInfo.InvariantCulture), true, depth + 1, budget));
                }
                return new TreeNode { Kind = "array", Name = name, IsIndex = isIndex, Count = items.Count, Omitted = omitted, Children = children };
            }
            case JsonValueKind.String:
            {
                var (value, truncated) = BoundScalar(element.GetString() ?? string.Empty);
                return new TreeNode { Kind = "string", Name = name, IsIndex = isIndex, Value = value, Truncated = truncated };
            }
            case JsonValueKind.Number:
            {
                var (value, truncated) = BoundScalar(element.GetRawText());
                return new TreeNode { Kind = "number", Name = name, IsIndex = isIndex, Value = value, Truncated = truncated };
            }
            case JsonValueKind.True:
            case JsonValueKind.False:
                return new TreeNode { Kind = "boolean", Name = name, IsIndex = isIndex, Value = element.GetRawText() };
            default:
                return new TreeNode { Kind = "null", Name = name, IsIndex = isIndex, Value = "null" };
        }
    }

    private static (string Value, bool Truncated) BoundScalar(string raw) =>
        raw.Length <= TreeMaxScalarLength ? (raw, false) : (raw[..TreeMaxScalarLength], true);

    private static string? BuildXmlTreePayload(string formattedXml)
    {
        try
        {
            using var textReader = new StringReader(formattedXml);
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            };
            using var reader = XmlReader.Create(textReader, settings);
            var budget = new TreeBudget();
            var children = new List<TreeNode>();
            var count = 0;
            var omitted = 0;
            while (reader.Read())
            {
                if (!IsRenderableXmlNode(reader))
                {
                    continue;
                }
                count++;
                if (children.Count >= TreeMaxChildrenPerNode || budget.NodeCount >= TreeMaxNodes)
                {
                    omitted++;
                    if (reader.NodeType == XmlNodeType.Element && !reader.IsEmptyElement)
                    {
                        SkipElementSubtree(reader);
                    }
                    continue;
                }
                children.Add(BuildXmlNode(reader, 0, budget));
            }
            var root = new TreeNode { Kind = "document", Count = count, Omitted = omitted, Children = children };
            return JsonSerializer.Serialize(root, TreePayloadOptions);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static TreeNode BuildXmlNode(XmlReader reader, int depth, TreeBudget budget)
    {
        budget.NodeCount++;
        switch (reader.NodeType)
        {
            case XmlNodeType.Comment:
            {
                var (value, truncated) = BoundScalar(reader.Value);
                return new TreeNode { Kind = "comment", Value = value, Truncated = truncated };
            }
            case XmlNodeType.CDATA:
            {
                var (value, truncated) = BoundScalar(reader.Value);
                return new TreeNode { Kind = "cdata", Value = value, Truncated = truncated };
            }
            case XmlNodeType.Text:
            case XmlNodeType.SignificantWhitespace:
            {
                var (value, truncated) = BoundScalar(reader.Value);
                return new TreeNode { Kind = "text", Value = value, Truncated = truncated };
            }
            case XmlNodeType.Element:
                return BuildXmlElement(reader, depth, budget);
            default:
                return new TreeNode { Kind = "unknown" };
        }
    }

    private static TreeNode BuildXmlElement(XmlReader reader, int depth, TreeBudget budget)
    {
        var name = string.IsNullOrEmpty(reader.Prefix) ? reader.LocalName : $"{reader.Prefix}:{reader.LocalName}";
        List<TreeNode>? attrs = null;
        if (reader.HasAttributes)
        {
            attrs = new List<TreeNode>();
            reader.MoveToFirstAttribute();
            do
            {
                var attrName = string.IsNullOrEmpty(reader.Prefix) ? reader.LocalName : $"{reader.Prefix}:{reader.LocalName}";
                var (attrValue, attrTruncated) = BoundScalar(reader.Value);
                attrs.Add(new TreeNode { Kind = "attribute", Name = attrName, Value = attrValue, Truncated = attrTruncated });
            } while (reader.MoveToNextAttribute());
            reader.MoveToElement();
        }

        if (reader.IsEmptyElement)
        {
            return new TreeNode { Kind = "element", Name = name, Attrs = attrs, Count = 0, Children = [] };
        }

        if (depth >= TreeMaxDepth)
        {
            // As with the JSON depth limit, report the true immediate child count while skipping
            // the (not descended into) subtree, instead of leaving it unset and rendering a
            // misleading "0 children" label.
            var immediateCount = SkipElementSubtreeCountingImmediateChildren(reader);
            return new TreeNode { Kind = "element", Name = name, Attrs = attrs, Count = immediateCount, DepthLimited = true };
        }

        var children = new List<TreeNode>();
        var count = 0;
        var omitted = 0;
        while (reader.Read() && reader.NodeType != XmlNodeType.EndElement)
        {
            if (!IsRenderableXmlNode(reader))
            {
                continue;
            }
            count++;
            if (children.Count >= TreeMaxChildrenPerNode || budget.NodeCount >= TreeMaxNodes)
            {
                omitted++;
                if (reader.NodeType == XmlNodeType.Element && !reader.IsEmptyElement)
                {
                    SkipElementSubtree(reader);
                }
                continue;
            }
            children.Add(BuildXmlNode(reader, depth + 1, budget));
        }

        return new TreeNode { Kind = "element", Name = name, Attrs = attrs, Count = count, Omitted = omitted, Children = children };
    }

    private static void SkipElementSubtree(XmlReader reader)
    {
        var depth = 0;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && !reader.IsEmptyElement)
            {
                depth++;
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (depth == 0)
                {
                    return;
                }
                depth--;
            }
        }
    }

    private static int SkipElementSubtreeCountingImmediateChildren(XmlReader reader)
    {
        var depth = 0;
        var immediateCount = 0;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (depth == 0)
                {
                    return immediateCount;
                }
                depth--;
                continue;
            }
            if (depth == 0 && IsRenderableXmlNode(reader))
            {
                immediateCount++;
            }
            if (reader.NodeType == XmlNodeType.Element && !reader.IsEmptyElement)
            {
                depth++;
            }
        }
        return immediateCount;
    }

    private static bool IsRenderableXmlNode(XmlReader reader) => reader.NodeType switch
    {
        XmlNodeType.Element or XmlNodeType.Comment or XmlNodeType.CDATA => true,
        // Without a DTD/schema, XmlReader cannot tell whether inter-element whitespace is
        // significant, so pretty-printed indentation is reported as (Significant)Whitespace.
        // Skip whitespace-only text so it doesn't flood the tree with indentation noise;
        // Pretty Text remains available for exact formatting fidelity.
        XmlNodeType.Text or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace => !string.IsNullOrWhiteSpace(reader.Value),
        _ => false
    };

    private static string FormatTimestamp(DateTimeOffset? value) =>
        value?.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture) ?? "-";

    private static void AppendHttpTimestamp(StringBuilder html, DateTimeOffset? value)
    {
        if (value is null)
        {
            html.Append('-');
            return;
        }

        var fullTimestamp = FormatTimestamp(value);
        html.Append("<time datetime=\"");
        Attribute(html, value.Value.ToString("O", CultureInfo.InvariantCulture));
        html.Append("\" title=\"Captured timestamp: ");
        Attribute(html, fullTimestamp);
        html.Append("\" aria-label=\"Captured timestamp ");
        Attribute(html, fullTimestamp);
        html.Append("\">");
        Text(html, value.Value.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
        html.Append("</time>");
    }

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KiB", "MiB", "GiB"];
        var size = (double)value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{value.ToString("N0", CultureInfo.InvariantCulture)} {units[unit]}"
            : $"{size.ToString("N1", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    private static void Text(StringBuilder html, string? value) =>
        html.Append(WebUtility.HtmlEncode(value ?? string.Empty));

    private static void Attribute(StringBuilder html, string? value) =>
        html.Append(WebUtility.HtmlEncode(value ?? string.Empty));
}
