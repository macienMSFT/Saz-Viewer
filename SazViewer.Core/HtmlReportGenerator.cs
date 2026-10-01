using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;

namespace SazViewer.Core;

public sealed partial class HtmlReportGenerator
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
    private const int HttpSessionPayloadMaxDecodedBytes = 32 * 1024 * 1024;
    private const int ProtocolPayloadMaxDecodedBytes = 32 * 1024 * 1024;
    private const int TreePayloadMaxDecodedBytes = 8 * 1024 * 1024;
    private const int WebSocketPayloadMaxDecodedBytes = 32 * 1024 * 1024;
    private const int MaxWebSocketMessagesPerSession = 5000;
    private const int MaxWebSocketRawPayloadBytes = 160 * 1024;
    private const int WebSocketPayloadContentBudgetBytes = 28 * 1024 * 1024;

    private sealed record CompressedPayload(string Type, string Base64, int DecodedBytes);
    private sealed record ImageViewInfo(string MimeType, string Detection, string Animation, string? Warning);
    private sealed record AuthViewData(string Redacted, string Full);
    private sealed record SessionPayload(int Schema, MessagePayload? Request, MessagePayload? Response);
    private sealed record MessagePayload(
        string StartLine,
        HttpHeader[] Headers,
        BodyPayload Body,
        string Format,
        string Label,
        string Status,
        bool CanToggle,
        ImageViewInfo? Image,
        string? WebViewDetection,
        bool HasAuth);
    private sealed record BodyPayload(
        long Length,
        long CapturedLength,
        bool IsBinary,
        bool IsTruncated,
        string? Charset,
        string[] RemovedEncodings,
        string? DecodingStatus,
        string Captured,
        string? Decoded,
        string? FallbackText,
        string? FallbackCapturedText,
        bool? FallbackCapturedTruncated,
        bool ShowCapturedBytes);

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
    private static readonly JsonSerializerOptions SessionPayloadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
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
.controls{display:flex;gap:6px;align-items:center;flex-wrap:wrap;margin:0 0 4px}input,select,button{background:var(--panel);border:1px solid var(--line);border-radius:6px;color:var(--text);padding:7px 10px}
input{min-width:280px;flex:1}button{cursor:pointer}.filter-toggle{display:inline-flex;align-items:center;gap:6px;white-space:nowrap}.filter-toggle input{width:16px;height:16px;min-width:0;flex:none;margin:0;padding:0}
.theme-toggle{flex:0 0 36px;display:inline-flex;align-items:center;justify-content:center;width:36px;min-width:36px;height:36px;padding:6px;color:var(--text)}.theme-toggle svg{display:block;width:19px;height:19px;fill:none;stroke:currentColor;stroke-width:1.8;stroke-linecap:round;stroke-linejoin:round}.theme-toggle .theme-bulb-core{fill:transparent;stroke:none}.theme-toggle[aria-pressed=true]{color:var(--syn-number);border-color:var(--accent)}.theme-toggle[aria-pressed=true] .theme-bulb-core{fill:currentColor}.theme-toggle:focus-visible{outline:2px solid var(--accent);outline-offset:2px}
table{width:100%;border-collapse:collapse;background:var(--panel);font-size:13px}th{position:sticky;top:0;z-index:2;background:var(--panel2);text-align:left}
th,td{padding:8px;border:1px solid var(--line);vertical-align:top}tbody tr:hover{background:var(--hover)}#httpTable{table-layout:auto}#httpTable th,#httpTable td{padding:5px 7px;line-height:1.3}
#httpTable .http-time,#httpTable .http-id,#httpTable .http-result,#httpTable .http-method,#httpTable .http-elapsed,#httpTable .http-bytes{width:1%;white-space:nowrap}
#httpTable .http-url{width:100%;min-width:180px;max-width:0}#httpTable .http-url-value{display:block;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
#httpTable tbody tr{cursor:pointer}#httpTable tbody tr.hidden{display:table-row!important;visibility:collapse}
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
.primary-view-bar{display:flex;align-items:flex-end;gap:8px;padding:0 14px;background:var(--panel2);flex:0 0 auto}.primary-view-bar .primary-tab-strip{padding:0;flex:1}.http-layout-toggle{flex:0 0 36px;display:inline-flex;align-items:center;justify-content:center;width:36px;min-width:36px;height:36px;padding:7px}.http-layout-toggle svg{display:block;width:20px;height:20px;fill:none;stroke:currentColor;stroke-width:1.8;stroke-linejoin:round}.http-layout-toggle[aria-pressed=true]{border-color:var(--accent);background:var(--selected)}.http-layout-toggle:focus-visible{outline:2px solid var(--accent);outline-offset:2px}
.tab-panels.primary-panels{flex:1;min-height:0;display:flex;flex-direction:column;overflow:hidden;border:none;padding:0;background:transparent}
.primary-panel{flex:1;min-width:0;min-height:0;display:flex;flex-direction:column;padding:10px 14px;overflow:hidden}.http-pane-heading{display:none;margin:0 0 3px;font-size:13px;color:var(--muted)}
.inspector-body.http-split .primary-panels{flex-direction:row;gap:6px;padding:8px 10px}.inspector-body.http-split .primary-panel{padding:4px;min-width:320px;border:1px solid var(--line);background:var(--panel);flex:1 1 0}.inspector-body.http-split #primary-panel-request{flex:0 1 var(--http-left,50%)}.inspector-body.http-split .http-pane-heading{display:block}.http-splitter{flex:0 0 8px;align-self:stretch;border-radius:4px;background:var(--line);cursor:col-resize;touch-action:none;position:relative}.http-splitter::after{content:"";position:absolute;inset:0 2px;border-left:1px solid var(--muted);border-right:1px solid var(--muted)}.http-splitter:hover,.http-splitter:focus-visible{background:var(--accent);outline:2px solid var(--accent);outline-offset:1px}.http-resizing{cursor:col-resize!important;user-select:none!important}.inspector-body.http-split .http-view-search{margin:0 0 5px}
.message-panel{display:flex;flex-direction:column;flex:1;min-height:0}
.http-session-source{display:flex;flex:1;min-height:0;flex-direction:column}.http-session-loading{padding:20px;color:var(--muted)}
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
@media(max-width:900px){main{padding:2px}.ws-layout{display:grid;grid-template-columns:1fr;grid-template-rows:minmax(180px,38%) minmax(0,1fr);gap:10px;overflow:hidden}.ws-splitter{display:none}.ws-traffic-pane,.ws-detail-pane{min-width:0}.inspector-body.http-split .primary-panels{flex-direction:column;overflow:auto}.inspector-body.http-split .primary-panel{min-width:0;min-height:300px;flex:1 0 300px}.inspector-body.http-split #primary-panel-request{flex:1 0 300px}.http-splitter{display:none}}
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
<button type="button" id="inspectorLayoutToggle" class="http-layout-toggle hidden" aria-label="Switch to split view" title="Switch to split view" aria-pressed="false" aria-controls="inspectorBody"><svg aria-hidden="true" viewBox="0 0 24 24"><rect x="3.5" y="4" width="7" height="16" rx="1"/><rect x="13.5" y="4" width="7" height="16" rx="1"/></svg></button>
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
const inspectorLayoutToggle=document.getElementById('inspectorLayoutToggle');
const inspectorPosition=document.getElementById('inspectorPosition');
const inspectorOpenTab=document.getElementById('inspectorOpenTab');
const inspectorClose=document.getElementById('inspectorClose');
const inspectorOpenStatus=document.getElementById('inspectorOpenStatus');
const reportStatus=document.getElementById('reportStatus');
const THEME_STORAGE_KEY='saz-viewer-theme';
const THEME_VALUES=new Set(['light','dark']);
const HTTP_LAYOUT_STORAGE_KEY='saz-viewer.http-layout.v1';
const HTTP_LAYOUT_VALUES=new Set(['single','split']);
function readHttpLayout(){
  try{const value=localStorage.getItem(HTTP_LAYOUT_STORAGE_KEY);if(HTTP_LAYOUT_VALUES.has(value))return value}catch{}
  return'single';
}
let httpLayoutMode=readHttpLayout(),httpSplitRatio=.5;
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
  if(event.key===HTTP_LAYOUT_STORAGE_KEY||event.key===null){
    const value=event.key===null?'single':event.newValue;
    httpLayoutMode=HTTP_LAYOUT_VALUES.has(value)?value:'single';
    applyHttpLayout(inspectorBody,httpLayoutMode,false,true);
  }
});
let currentRow=null,originRow=null,renderGeneration=0,inspectorOnly=false,retainSelectionOnClose=false;
const PAYLOAD_VERSION='1';
const PAYLOAD_LIMITS={
  'http-session':{encoded:48*1024*1024,decoded:32*1024*1024},
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
function validateMessageModel(message){
  if(!message||typeof message!=='object'||Array.isArray(message)
    ||typeof message.startLine!=='string'||message.startLine.length>MAX_COPY_CHARACTERS
    ||!Array.isArray(message.headers)||message.headers.length>100000
    ||!message.body||typeof message.body!=='object'||Array.isArray(message.body)
    ||!['binary','json','xml','text'].includes(message.format)
    ||typeof message.label!=='string'||message.label.length>256
    ||typeof message.status!=='string'||message.status.length>2048
    ||typeof message.canToggle!=='boolean'||typeof message.hasAuth!=='boolean'
    ||(message.webViewDetection!==null&&message.webViewDetection!==undefined&&(typeof message.webViewDetection!=='string'||message.webViewDetection.length>2048)))
    throw new Error('session message data has an invalid format');
  if(message.image!==null&&message.image!==undefined&&(!message.image||typeof message.image!=='object'
    ||!['image/png','image/jpeg','image/gif','image/webp','image/bmp','image/x-icon'].includes(message.image.mimeType)
    ||typeof message.image.detection!=='string'||typeof message.image.animation!=='string'
    ||(message.image.warning!==null&&message.image.warning!==undefined&&typeof message.image.warning!=='string')))
    throw new Error('session image metadata has an invalid format');
  let headerCharacters=message.startLine.length;
  message.headers.forEach(header=>{
    if(!header||typeof header.name!=='string'||typeof header.value!=='string')throw new Error('session header data has an invalid format');
    headerCharacters+=header.name.length+header.value.length+3;
    if(headerCharacters>32*1024*1024)throw new Error('session header data exceeds its safety limit');
  });
  const body=message.body;
  if(!Number.isSafeInteger(body.length)||body.length<0||!Number.isSafeInteger(body.capturedLength)||body.capturedLength<0
    ||typeof body.isBinary!=='boolean'||typeof body.isTruncated!=='boolean'
    ||(body.charset!==null&&body.charset!==undefined&&typeof body.charset!=='string')
    ||!Array.isArray(body.removedEncodings)||body.removedEncodings.some(item=>typeof item!=='string')
    ||(body.decodingStatus!==null&&body.decodingStatus!==undefined&&typeof body.decodingStatus!=='string')
    ||typeof body.captured!=='string'||(body.decoded!==null&&body.decoded!==undefined&&typeof body.decoded!=='string')
    ||(body.fallbackText!==null&&body.fallbackText!==undefined&&(typeof body.fallbackText!=='string'||body.fallbackText.length>65536))
    ||(body.fallbackCapturedText!==null&&body.fallbackCapturedText!==undefined&&(typeof body.fallbackCapturedText!=='string'||body.fallbackCapturedText.length>256*1024))
    ||(body.fallbackCapturedTruncated!==null&&body.fallbackCapturedTruncated!==undefined&&typeof body.fallbackCapturedTruncated!=='boolean')
    ||typeof body.showCapturedBytes!=='boolean')throw new Error('session body data has an invalid format');
  return message;
}
async function loadMessageModel(panel){
  if(panel._messageModel)return panel._messageModel;
  if(panel._messageModelError)throw new Error(panel._messageModelError);
  if(panel._messageModelPromise)return panel._messageModelPromise;
  panel._messageModelPromise=(async()=>{
    const host=panel.closest('.http-session-source');
    if(!host)throw new Error('session payload host is unavailable');
    if(host.dataset.payloadError)throw new Error(host.dataset.payloadError);
    const store=await decodeCompressedPayload(host,'http-session');
    if(!store||store.schema!==1||typeof store!=='object'||Array.isArray(store))throw new Error('session data has an unsupported schema');
    const side=panel.dataset.messageSide;
    const message=side==='request'?store.request:side==='response'?store.response:null;
    if(!message)throw new Error(`${side||'message'} data is unavailable`);
    panel._messageModel=validateMessageModel(message);
    return panel._messageModel;
  })();
  try{return await panel._messageModelPromise}
  catch(error){panel._messageModelError=error instanceof Error?error.message:'session data could not be read';throw error}
  finally{panel._messageModelPromise=null}
}
function messageCapturedBytes(model){
  return decodeModelBytes(model.body,'captured');
}
function messageBodyBytes(model){
  return model.body.decoded===null||model.body.decoded===undefined
    ?messageCapturedBytes(model)
    :decodeModelBytes(model.body,'decoded');
}
function decodeBodyText(model){
  const body=model.body;
  if(typeof body.fallbackText==='string')return body.fallbackText;
  const bytes=messageBodyBytes(model);
  if(body.isBinary)return formatRawHexPreview(bytes);
  const charset=String(body.charset||'utf-8').trim().toLowerCase();
  if(charset==='iso-8859-1'||charset==='latin1'||charset==='latin-1'){
    let text='';for(let offset=0;offset<bytes.length;offset+=8192)text+=String.fromCharCode(...bytes.subarray(offset,offset+8192));
    return text;
  }
  if(charset==='us-ascii'||charset==='ascii'){
    if(bytes.some(value=>value>127))throw new Error('retained textual body is not valid US-ASCII');
    return String.fromCharCode(...bytes);
  }
  const label=charset==='utf-16'?'utf-16le':charset==='utf-16be'?'utf-16be':charset==='utf8'?'utf-8':charset;
  if(!['utf-8','utf-16le','utf-16be'].includes(label))throw new Error('retained textual body uses an unsupported charset');
  let failure;
  for(let trim=0;trim<=Math.min(body.isTruncated?4:0,bytes.length);trim++){
    try{return new TextDecoder(label,{fatal:true}).decode(bytes.subarray(0,bytes.length-trim))}
    catch(error){failure=error}
  }
  throw failure||new Error('retained textual body could not be decoded');
}
function formatRawHexPreview(bytes){
  const lines=[];
  for(let offset=0;offset<bytes.length;offset+=16){
    const slice=bytes.subarray(offset,Math.min(offset+16,bytes.length));
    const hex=[...slice].map(value=>value.toString(16).toUpperCase().padStart(2,'0'));
    const columns=[];for(let index=0;index<16;index++)columns.push(index<hex.length?hex[index]:'  ');
    const left=columns.slice(0,8).join(' '),right=columns.slice(8).join(' ');
    const ascii=[...slice].map(value=>value>=32&&value<=126?String.fromCharCode(value):'.').join('');
    lines.push(`${offset.toString(16).toUpperCase().padStart(8,'0')}  ${left}  ${right} |${ascii}|`);
  }
  return lines.length?`${lines.join('\n')}\n`:'';
}
function capturedPreviewText(model){
  return typeof model.body.fallbackCapturedText==='string'
    ?model.body.fallbackCapturedText
    :formatRawHexPreview(messageCapturedBytes(model));
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
  const kind=host.dataset.treeKind||((host._payloadType||host.dataset.payloadType)==='json-tree'?'json':'xml');
  const controls=host.closest('.structured-body')?.querySelectorAll('.tree-expand-all,.tree-collapse-all')||[];
  controls.forEach(control=>control.disabled=true);
  try{
    const payload=host._treeData||await decodeCompressedPayload(host,`${kind}-tree`);
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
const TREE_MAX_NODES=4000,TREE_MAX_DEPTH=40,TREE_MAX_CHILDREN=300,TREE_MAX_SCALAR=300;
function parseCanonicalJson(source){
  let index=0;
  const whitespace=()=>{while(index<source.length&&/\s/.test(source[index]))index++};
  const stringToken=()=>{
    const start=index++;
    while(index<source.length){
      const character=source[index++];
      if(character==='"\\'){
        if(index>=source.length)throw new Error('invalid JSON escape');
        if(source[index]==='u'){
          if(!/^[0-9A-Fa-f]{4}$/.test(source.slice(index+1,index+5)))throw new Error('invalid JSON Unicode escape');
          index+=5;
        }else index++;
      }else if(character==='"') {
        const raw=source.slice(start,index);
        return{raw,value:JSON.parse(raw)};
      }else if(character.charCodeAt(0)<32)throw new Error('invalid JSON string');
    }
    throw new Error('unterminated JSON string');
  };
  const value=depth=>{
    if(depth>64)throw new Error('JSON nesting exceeds the safe depth');
    whitespace();
    if(source[index]==='"'){
      const token=stringToken();return{kind:'string',raw:token.raw,value:token.value};
    }
    if(source[index]==='{'){
      index++;whitespace();const properties=[];
      if(source[index]==='}'){index++;return{kind:'object',properties}}
      while(true){
        whitespace();if(source[index]!=='"')throw new Error('invalid JSON property');
        const name=stringToken();whitespace();if(source[index++]!==':')throw new Error('invalid JSON property separator');
        properties.push({name:name.value,nameRaw:name.raw,node:value(depth+1)});
        whitespace();
        if(source[index]==='}'){index++;break}
        if(source[index++]!==',')throw new Error('invalid JSON object');
      }
      return{kind:'object',properties};
    }
    if(source[index]==='['){
      index++;whitespace();const items=[];
      if(source[index]===']'){index++;return{kind:'array',items}}
      while(true){
        items.push(value(depth+1));whitespace();
        if(source[index]===']'){index++;break}
        if(source[index++]!==',')throw new Error('invalid JSON array');
      }
      return{kind:'array',items};
    }
    const rest=source.slice(index);
    const number=/^-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?/.exec(rest)?.[0];
    if(number){index+=number.length;return{kind:'number',raw:number,value:number}}
    for(const literal of ['true','false','null']){
      if(rest.startsWith(literal)){index+=literal.length;return{kind:literal==='null'?'null':'boolean',raw:literal,value:literal}}
    }
    throw new Error('invalid JSON value');
  };
  const root=value(0);whitespace();
  if(index!==source.length)throw new Error('JSON has trailing content');
  return root;
}
function dotNetJsonString(value){
  let result='"';
  for(let index=0;index<value.length;index++){
    const character=value[index],code=value.charCodeAt(index);
    if(character==='"')result+='\\"';
    else if(character==='\\')result+='\\\\';
    else if(character==='\b')result+='\\b';
    else if(character==='\f')result+='\\f';
    else if(character==='\n')result+='\\n';
    else if(character==='\r')result+='\\r';
    else if(character==='\t')result+='\\t';
    else if(code<32||code>126||character==='<'||character==='>'||character==='&'||character==="'")
      result+=`\\u${code.toString(16).toUpperCase().padStart(4,'0')}`;
    else result+=character;
  }
  return`${result}"`;
}
function prettyCanonicalJson(node,depth=0){
  const indent='  '.repeat(depth),childIndent='  '.repeat(depth+1);
  if(node.kind==='object'){
    if(!node.properties.length)return'{}';
    return`{\r\n${node.properties.map(property=>`${childIndent}${dotNetJsonString(property.name)}: ${prettyCanonicalJson(property.node,depth+1)}`).join(',\r\n')}\r\n${indent}}`;
  }
  if(node.kind==='array'){
    if(!node.items.length)return'[]';
    return`[\r\n${node.items.map(item=>`${childIndent}${prettyCanonicalJson(item,depth+1)}`).join(',\r\n')}\r\n${indent}]`;
  }
  return node.kind==='string'?dotNetJsonString(node.value):node.raw;
}
function scalar(value){
  const text=String(value??'');
  return{text:text.slice(0,TREE_MAX_SCALAR),truncated:text.length>TREE_MAX_SCALAR};
}
function jsonTreeNode(node,name,isIndex,depth,budget){
  budget.count++;
  if(depth>=TREE_MAX_DEPTH&&(node.kind==='object'||node.kind==='array')){
    return{kind:node.kind,name,isIndex,count:node.kind==='object'?node.properties.length:node.items.length,depthLimited:true};
  }
  if(node.kind==='object'){
    const children=[],properties=node.properties;
    let omitted=0;
    for(let index=0;index<properties.length;index++){
      if(children.length>=TREE_MAX_CHILDREN||budget.count>=TREE_MAX_NODES){omitted=properties.length-children.length;break}
      const property=properties[index];children.push(jsonTreeNode(property.node,property.name,false,depth+1,budget));
    }
    return{kind:'object',name,isIndex,count:properties.length,omitted,children};
  }
  if(node.kind==='array'){
    const children=[];let omitted=0;
    for(let index=0;index<node.items.length;index++){
      if(children.length>=TREE_MAX_CHILDREN||budget.count>=TREE_MAX_NODES){omitted=node.items.length-children.length;break}
      children.push(jsonTreeNode(node.items[index],String(index),true,depth+1,budget));
    }
    return{kind:'array',name,isIndex,count:node.items.length,omitted,children};
  }
  const bounded=scalar(node.kind==='string'?node.value:node.value);
  return{kind:node.kind,name,isIndex,value:bounded.text,truncated:bounded.truncated};
}
function parseCanonicalXml(source){
  if(/<!DOCTYPE/i.test(source))throw new Error('XML document types are not allowed');
  const documentNode=new DOMParser().parseFromString(source,'application/xml');
  if(documentNode.documentElement?.localName==='parsererror'||documentNode.querySelector('parsererror'))throw new Error('XML parsing failed');
  return documentNode;
}
function escapeXmlText(value){return value.replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('>','&gt;')}
function escapeXmlAttribute(value){return escapeXmlText(value).replaceAll('"','&quot;')}
function prettyCanonicalXml(documentNode){
  function render(node,depth){
    const indent='  '.repeat(depth);
    if(node.nodeType===Node.COMMENT_NODE)return`${indent}<!--${node.nodeValue||''}-->`;
    if(node.nodeType===Node.CDATA_SECTION_NODE)return`${indent}<![CDATA[${node.nodeValue||''}]]>`;
    if(node.nodeType===Node.TEXT_NODE)return`${indent}${escapeXmlText(node.nodeValue||'')}`;
    if(node.nodeType!==Node.ELEMENT_NODE)return'';
    const attrs=[...node.attributes].map(attribute=>` ${attribute.name}="${escapeXmlAttribute(attribute.value)}"`).join('');
    const children=[...node.childNodes];
    if(!children.length)return`${indent}<${node.tagName}${attrs} />`;
    const mixed=children.some(child=>child.nodeType===Node.TEXT_NODE&&!/^\s*$/.test(child.nodeValue||''));
    if(mixed){
      const inner=children.map(child=>child.nodeType===Node.ELEMENT_NODE
        ?render(child,0).trim()
        :child.nodeType===Node.CDATA_SECTION_NODE?`<![CDATA[${child.nodeValue||''}]]>`
        :child.nodeType===Node.COMMENT_NODE?`<!--${child.nodeValue||''}-->`
        :escapeXmlText(child.nodeValue||'')).join('');
      return`${indent}<${node.tagName}${attrs}>${inner}</${node.tagName}>`;
    }
    const rendered=children.map(child=>render(child,depth+1)).filter(Boolean);
    return`${indent}<${node.tagName}${attrs}>\r\n${rendered.join('\r\n')}\r\n${indent}</${node.tagName}>`;
  }
  return[...documentNode.childNodes].map(node=>render(node,0)).filter(Boolean).join('\r\n');
}
function xmlRenderableChildren(node){
  return[...node.childNodes].filter(child=>child.nodeType===Node.ELEMENT_NODE||child.nodeType===Node.COMMENT_NODE
    ||child.nodeType===Node.CDATA_SECTION_NODE||(child.nodeType===Node.TEXT_NODE&&!/^\s*$/.test(child.nodeValue||'')));
}
function xmlTreeNode(node,depth,budget){
  budget.count++;
  if(node.nodeType===Node.COMMENT_NODE){const bounded=scalar(node.nodeValue||'');return{kind:'comment',value:bounded.text,truncated:bounded.truncated}}
  if(node.nodeType===Node.CDATA_SECTION_NODE){const bounded=scalar(node.nodeValue||'');return{kind:'cdata',value:bounded.text,truncated:bounded.truncated}}
  if(node.nodeType===Node.TEXT_NODE){const bounded=scalar(node.nodeValue||'');return{kind:'text',value:bounded.text,truncated:bounded.truncated}}
  const attrs=[...node.attributes].map(attribute=>{const bounded=scalar(attribute.value);return{kind:'attribute',name:attribute.name,value:bounded.text,truncated:bounded.truncated}});
  const sourceChildren=xmlRenderableChildren(node);
  if(depth>=TREE_MAX_DEPTH)return{kind:'element',name:node.tagName,attrs,count:sourceChildren.length,depthLimited:true};
  const children=[];let omitted=0;
  for(let index=0;index<sourceChildren.length;index++){
    if(children.length>=TREE_MAX_CHILDREN||budget.count>=TREE_MAX_NODES){omitted=sourceChildren.length-children.length;break}
    children.push(xmlTreeNode(sourceChildren[index],depth+1,budget));
  }
  return{kind:'element',name:node.tagName,attrs,count:sourceChildren.length,omitted,children};
}
function xmlTree(documentNode){
  const sourceChildren=xmlRenderableChildren(documentNode),budget={count:0},children=[];let omitted=0;
  for(let index=0;index<sourceChildren.length;index++){
    if(children.length>=TREE_MAX_CHILDREN||budget.count>=TREE_MAX_NODES){omitted=sourceChildren.length-children.length;break}
    children.push(xmlTreeNode(sourceChildren[index],0,budget));
  }
  return{kind:'document',count:sourceChildren.length,omitted,children};
}
async function hydrateStructuredBody(container,generation){
  if(container._hydrated||container._loading)return;
  container._loading=true;
  try{
    const model=await messageViewModel(container);
    if(generation!==renderGeneration||!container.isConnected)return;
    const source=decodeBodyText(model).replace(/^\uFEFF/,'');
    const format=container.dataset.format;
    let formatted,tree;
    if(format==='json'){
      const parsed=parseCanonicalJson(source);
      formatted=prettyCanonicalJson(parsed);
      tree=jsonTreeNode(parsed,null,false,0,{count:0});
    }else{
      const parsed=parseCanonicalXml(source);
      formatted=prettyCanonicalXml(parsed);
      tree=xmlTree(parsed);
    }
    if(formatted.length>256*1024)throw new Error('formatted body exceeds its display safety limit');
    container.querySelector('.formatted-view').textContent=formatted;
    const host=container.querySelector('.tree-subview');
    host.dataset.treeKind=format;host._treeData=tree;
    container._formattedText=formatted;container._hydrated=true;highlightSelected(container);
    if(!host.classList.contains('hidden')&&!host.closest('.tab-panel.hidden'))renderValueTree(host,generation);
  }catch(error){
    const message=`Structured view could not be prepared: ${error.message}`;
    container.querySelector('.formatted-view').textContent=message;
    const host=container.querySelector('.tree-subview');host.textContent=message;host.classList.add('warning');host._treeRendered=true;
  }finally{container._loading=false}
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
function setupHttpSplitter(root,layout,request,response,splitter){
  root._disposeHttpSplitter?.();
  const MIN_PANE=320;
  let draggingPointer=null;
  function metrics(){
    const style=getComputedStyle(layout);
    const gap=Number.parseFloat(style.columnGap||style.gap)||0;
    const available=Math.max(1,layout.clientWidth-splitter.offsetWidth-(2*gap));
    return{available,minLeft:Math.min(MIN_PANE,available),maxLeft:Math.max(Math.min(MIN_PANE,available),available-MIN_PANE)};
  }
  function applyRatio(ratio,remember){
    const narrow=matchMedia('(max-width:900px)').matches;
    if(!root.classList.contains('http-split')||narrow){
      splitter.setAttribute('aria-disabled','true');splitter.setAttribute('aria-hidden','true');return;
    }
    splitter.removeAttribute('aria-disabled');splitter.removeAttribute('aria-hidden');
    const size=metrics();
    const left=Math.min(size.maxLeft,Math.max(size.minLeft,size.available*ratio));
    const applied=left/size.available;
    layout.style.setProperty('--http-left',`${left}px`);
    if(remember)httpSplitRatio=applied;
    const min=Math.round((size.minLeft/size.available)*100),max=Math.round((size.maxLeft/size.available)*100),now=Math.round(applied*100);
    splitter.setAttribute('aria-valuemin',String(min));splitter.setAttribute('aria-valuemax',String(max));
    splitter.setAttribute('aria-valuenow',String(now));splitter.setAttribute('aria-valuetext',`Request pane ${now} percent; Response pane ${100-now} percent`);
  }
  function applyClientX(clientX){
    const size=metrics(),bounds=layout.getBoundingClientRect();
    applyRatio((clientX-bounds.left)/size.available,true);
  }
  splitter.addEventListener('pointerdown',event=>{
    if(event.button!==0||matchMedia('(max-width:900px)').matches||!root.classList.contains('http-split'))return;
    event.preventDefault();draggingPointer=event.pointerId;splitter.setPointerCapture(event.pointerId);
    document.body.classList.add('http-resizing');applyClientX(event.clientX);
  });
  splitter.addEventListener('pointermove',event=>{if(event.pointerId===draggingPointer)applyClientX(event.clientX)});
  function finishPointer(event){
    if(event.pointerId!==draggingPointer)return;
    draggingPointer=null;document.body.classList.remove('http-resizing');
    if(splitter.hasPointerCapture(event.pointerId))splitter.releasePointerCapture(event.pointerId);
  }
  splitter.addEventListener('pointerup',finishPointer);
  splitter.addEventListener('pointercancel',finishPointer);
  splitter.addEventListener('lostpointercapture',()=>{draggingPointer=null;document.body.classList.remove('http-resizing')});
  splitter.addEventListener('keydown',event=>{
    if(matchMedia('(max-width:900px)').matches||!root.classList.contains('http-split'))return;
    const size=metrics(),current=request.getBoundingClientRect().width;
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
    applyRatio(httpSplitRatio,false);
  });
  observer.observe(layout);
  root._applyHttpSplitRatio=()=>applyRatio(httpSplitRatio,false);
  root._disposeHttpSplitter=()=>{
    observer.disconnect();
    document.body.classList.remove('http-resizing');
    root._applyHttpSplitRatio=null;
    root._disposeHttpSplitter=null;
  };
}
function applyHttpLayout(root,mode,persist,hydrate){
  const bar=root.querySelector('.primary-view-bar');
  const panels=root.querySelector('.primary-panels');
  if(!bar||!panels)return;
  const tablist=bar.querySelector('.primary-tab-strip'),button=inspectorLayoutToggle;
  const request=panels.querySelector('#primary-panel-request'),response=panels.querySelector('#primary-panel-response');
  const splitter=panels.querySelector('.http-splitter');
  if(!tablist||!button||!request||!response||!splitter)return;
  root._disposeHttpViewSearch?.();root._clearHttpViewSearch=null;root._disposeHttpViewSearch=null;
  const next=mode==='split'?'split':'single';
  httpLayoutMode=next;
  const split=next==='split';
  root.classList.toggle('http-split',split);
  if(split&&bar.contains(document.activeElement))button.focus();
  bar.classList.toggle('hidden',split);
  button.setAttribute('aria-pressed',String(split));
  const label=split?'Switch to single-side view':'Switch to split view';
  button.setAttribute('aria-label',label);button.title=label;
  request.setAttribute('aria-labelledby',split?'request-pane-heading':'primary-tab-request');
  response.setAttribute('aria-labelledby',split?'response-pane-heading':'primary-tab-response');
  splitter.classList.toggle('hidden',!split);
  if(split){
    request.classList.remove('hidden');response.classList.remove('hidden');
  }else{
    const selected=tablist.querySelector('[role="tab"][aria-selected="true"]');
    const requestActive=selected?.dataset.tab!=='response';
    request.classList.toggle('hidden',!requestActive);response.classList.toggle('hidden',requestActive);
    deactivateDynamicViews(requestActive?response:request);
  }
  root._applyHttpSplitRatio?.();
  setupHttpViewSearch(root,renderGeneration);
  if(hydrate&&inspector.open)hydrateViewPayloads(split?panels:(request.classList.contains('hidden')?response:request),renderGeneration);
  if(persist){
    try{localStorage.setItem(HTTP_LAYOUT_STORAGE_KEY,next)}catch{}
  }
}
function setupHttpLayout(root){
  const bar=root.querySelector('.primary-view-bar');
  const panels=root.querySelector('.primary-panels');
  if(!bar||!panels)return false;
  const button=inspectorLayoutToggle,request=panels.querySelector('#primary-panel-request');
  const response=panels.querySelector('#primary-panel-response'),splitter=panels.querySelector('.http-splitter');
  if(!button||!request||!response||!splitter)return false;
  button.classList.remove('hidden');
  setupHttpSplitter(root,panels,request,response,splitter);
  applyHttpLayout(root,httpLayoutMode,false,false);
  return true;
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
    if(!target||token!==runToken||!options.searchRoot.isConnected)return;
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
        const treeSelected=panel.querySelector('.structured-body')?.dataset.viewMode!=='pretty';
        if(pretty&&!treeSelected)return{roots:[pretty]};
        if(!tree)return null;
        renderValueTree(tree,generation);
        while(!tree._treeRendered){
          await new Promise(resolve=>requestAnimationFrame(resolve));
          if(token!==currentToken()||!content.isConnected)return null;
        }
        return{roots:[tree],snapshot:()=>snapshotValueTree(tree),reveal:revealValueTreeMatches};
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
  const primaryTabs=root.querySelector('.primary-view-bar>.primary-tab-strip');
  const primaryPanels=root.querySelector('.primary-panels');
  if(!primaryTabs||!primaryPanels)return;
  const split=root.classList.contains('http-split');
  const targets=split?[...primaryPanels.querySelectorAll(':scope>.primary-panel')]:[null];
  const controllers=targets.map(fixedPrimaryPanel=>setupActiveViewSearch({
    toolbarClass:'http-view-search',inputClass:'http-view-search-input',previousClass:'http-view-search-prev',nextClass:'http-view-search-next',statusClass:'http-view-search-status',
    placeholder:fixedPrimaryPanel?`Search ${fixedPrimaryPanel.dataset.side} view...`:'Search active Request/Response view...',
    inputLabel:fixedPrimaryPanel?`Search active ${fixedPrimaryPanel.dataset.side} view`:'Search active Request or Response view',
    previousLabel:'Previous active-view search match',nextLabel:'Next active-view search match',
    matchClass:'http-search-match',currentClass:'http-search-match-current',
    searchRoot:fixedPrimaryPanel||root,viewEventTarget:fixedPrimaryPanel||root,generation,clearOnViewChange:true,
    insertToolbar:toolbar=>fixedPrimaryPanel
      ?fixedPrimaryPanel.insertBefore(toolbar,fixedPrimaryPanel.querySelector('.message-panel'))
      :primaryPanels.parentElement.insertBefore(toolbar,primaryPanels),
    resolveTarget:async(token,currentToken)=>{
      const primaryTab=primaryTabs.querySelector('[role="tab"][aria-selected="true"]');
      const primaryPanel=fixedPrimaryPanel||(primaryTab?root.querySelector(`#${CSS.escape(primaryTab.getAttribute('aria-controls'))}`):null);
      const messagePanel=primaryPanel?.querySelector('.message-panel');
      if(!messagePanel)return null;
      if(token!==currentToken()||!root.isConnected)return null;
      const tablist=messagePanel.querySelector(':scope>.tab-strip');
      const tab=tablist?.querySelector('[role="tab"][aria-selected="true"]');
      const panel=tab?messagePanel.querySelector(`#${CSS.escape(tab.getAttribute('aria-controls'))}`):null;
      if(!tab||!panel)return null;
      if(tab.dataset.tab==='json'||tab.dataset.tab==='xml'){
        const tree=panel.querySelector('.tree-subview'),pretty=panel.querySelector('.pretty-subview');
        const treeSelected=panel.querySelector('.structured-body')?.dataset.viewMode!=='pretty';
        if(pretty&&!treeSelected)return{roots:[pretty]};
        if(!tree)return null;
        renderValueTree(tree,generation);
        while(!tree._treeRendered){
          await new Promise(resolve=>requestAnimationFrame(resolve));
          if(token!==currentToken()||!root.isConnected)return null;
        }
        return{roots:[tree],snapshot:()=>snapshotValueTree(tree),reveal:revealValueTreeMatches};
      }
      if(tab.dataset.tab==='mapi'){
        const host=panel.querySelector('.protocol-block');
        if(host&&(host.dataset.compressedPayload||host._payloadData!==undefined))await renderProtocolTree(host,generation);
        if(token!==currentToken()||!root.isConnected)return null;
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
        if(token!==currentToken()||!root.isConnected)return null;
        return{roots:[...panel.querySelectorAll('.image-meta,.format-status,.warning,.image-load-status')]};
      }
      if(tab.dataset.tab==='webview'){
        const view=panel.querySelector('.webview');
        if(!view)return null;
        setWebViewMode(view,'source',false);
        await messageViewModel(view);
        if(token!==currentToken()||!root.isConnected)return null;
        return{roots:[...panel.querySelectorAll('.webview-source,.webview-status,.format-status,.warning')]};
      }
      if(tab.dataset.tab==='hex'){
        const view=panel.querySelector('.hex-view');
        if(view)await renderHexView(view,generation);
        if(token!==currentToken()||!root.isConnected)return null;
        return{roots:[...panel.querySelectorAll('.hex-source-label,.decode-status,.hex-dump,.hex-status,.warning')]};
      }
      if(tab.dataset.tab==='auth'){
        return{roots:[...panel.querySelectorAll('.warning,.auth-headers,.auth-status')]};
      }
      return{roots:[...panel.querySelectorAll('.headers')]};
    }
  }));
  root._clearHttpViewSearch=()=>controllers.forEach(controller=>controller.reset());
  root._disposeHttpViewSearch=()=>controllers.forEach(controller=>{controller.dispose();controller.toolbar.remove()});
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
  return loadMessageModel(panel);
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
    const bytes=messageBodyBytes(model);
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
const WEBVIEW_STYLE=`:root{color-scheme:dark;--bg:#0d1117;--panel:#161b22;--text:#e6edf3;--muted:#8b949e;--line:#30363d;--accent:#58a6ff}:root[data-theme=light]{color-scheme:light;--bg:#fff;--panel:#f6f8fa;--text:#1f2328;--muted:#59636e;--line:#d0d7de;--accent:#0969da}*{box-sizing:border-box}html,body{margin:0;min-height:100%;background:var(--bg);color:var(--text);font:14px/1.45 system-ui,Segoe UI,sans-serif}body{padding:12px}a{color:var(--accent);text-decoration:none;pointer-events:none}table{border-collapse:collapse;max-width:100%}th,td{border:1px solid var(--line);padding:5px 7px;vertical-align:top}pre,code,kbd,samp{font-family:ui-monospace,Consolas,monospace}pre{white-space:pre-wrap;overflow-wrap:anywhere;background:var(--panel);border:1px solid var(--line);padding:9px}blockquote{border-left:3px solid var(--line);margin-left:0;padding-left:12px;color:var(--muted)}hr{border:0;border-top:1px solid var(--line)}`;
const WEBVIEW_ALLOWED=new Set(['a','abbr','address','article','aside','b','bdi','bdo','blockquote','br','caption','cite','code','col','colgroup','data','dd','del','details','dfn','div','dl','dt','em','figcaption','figure','footer','h1','h2','h3','h4','h5','h6','header','hgroup','hr','i','ins','kbd','li','main','mark','nav','ol','p','pre','q','s','samp','section','small','span','strong','sub','summary','sup','table','tbody','td','tfoot','th','thead','time','tr','u','ul','var','wbr']);
const WEBVIEW_VOID=new Set(['br','col','hr','wbr']);
const WEBVIEW_REMOVED=new Set(['applet','audio','base','embed','fencedframe','frame','frameset','iframe','math','mathml','menu','meta','noscript','object','picture','portal','script','style','svg','template','title','video']);
const WEBVIEW_REMOVED_VOID=new Set(['area','base','embed','img','input','link','meta','param','source','track']);
const WEBVIEW_FOREIGN_SELF_CLOSING=new Set(['math','mathml','svg']);
const WEBVIEW_RAW_REMOVED=new Set(['script','style','title']);
function escapeHtmlText(value){return value.replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('>','&gt;')}
function sanitizeWebViewSource(source,xhtml){
  let output='',skipped=null,skippedDepth=0,position=0;
  function tagEnd(start){
    let quote='';
    for(let index=start;index<source.length;index++){
      const character=source[index];
      if(quote){if(character===quote)quote='';continue}
      if(character==='"'||character==="'"){quote=character;continue}
      if(character==='>')return index;
    }
    return-1;
  }
  function tag(token){
    let text=token.trim(),closing=false;
    if(text.startsWith('/')){closing=true;text=text.slice(1).trimStart()}
    const match=/^([A-Za-z][A-Za-z0-9:_-]*)/.exec(text);
    if(!match)return null;
    return{name:match[1].toLowerCase(),closing,selfClosing:/\/\s*$/.test(text)};
  }
  while(position<source.length){
    const start=source.indexOf('<',position);
    if(start<0){if(!skipped)output+=source.slice(position);break}
    if(!skipped&&start>position)output+=source.slice(position,start);
    if(source.startsWith('<!--',start)){
      const end=source.indexOf('-->',start+4);position=end<0?source.length:end+3;continue;
    }
    const end=tagEnd(start+1);
    if(end<0){if(!skipped)output+=`&lt;${escapeHtmlText(source.slice(start+1))}`;break}
    const parsed=tag(source.slice(start+1,end));
    if(!parsed){position=end+1;continue}
    const {name,closing,selfClosing}=parsed;
    if(skipped){
      if(name===skipped){
        if(closing){skippedDepth--;if(skippedDepth===0)skipped=null}
        else if(!selfClosing&&!WEBVIEW_RAW_REMOVED.has(skipped))skippedDepth++;
      }
      position=end+1;continue;
    }
    if(WEBVIEW_REMOVED_VOID.has(name)){
      if(name==='img')output+='<span>[image omitted]</span>';
      position=end+1;continue;
    }
    if(WEBVIEW_REMOVED.has(name)){
      const immediate=selfClosing&&(xhtml||WEBVIEW_FOREIGN_SELF_CLOSING.has(name));
      if(!closing&&!immediate){skipped=name;skippedDepth=1}
      position=end+1;continue;
    }
    if(WEBVIEW_ALLOWED.has(name)){
      if(closing){if(!WEBVIEW_VOID.has(name))output+=`</${name}>`}
      else output+=`<${name}>`;
    }
    position=end+1;
  }
  return output;
}
function deactivateWebView(view){
  view._webViewToken=(view._webViewToken||0)+1;
  view._webViewLoading=false;
  const frame=view._webViewFrame||view.querySelector('iframe');
  if(frame)frame.remove();
  view._webViewFrame=null;
}
function webViewDocument(model){
  const source=decodeBodyText(model);
  if(source.length>65536)throw new Error('the retained HTML source is oversized');
  const contentType=(model.headers.find(header=>header.name.toLowerCase()==='content-type')?.value||'').split(';',1)[0].trim().toLowerCase();
  const sanitized=sanitizeWebViewSource(source,contentType==='application/xhtml+xml');
  const documentText=`${WEBVIEW_DOCUMENT_PREFIX}<style>${WEBVIEW_STYLE}</style></head><body>${sanitized}</body></html>`;
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
    const source=view.querySelector('.webview-source');if(source)source.textContent=decodeBodyText(model);
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
    messageViewModel(view).then(model=>{
      if(view.dataset.mode==='source'&&view.isConnected){
        view.querySelector('.webview-source').textContent=decodeBodyText(model);
      }
    }).catch(error=>{
      if(view.dataset.mode==='source'&&view.isConnected){
        view.querySelector('.webview-status').textContent=`WebView source could not be loaded: ${error.message}`;
        view.querySelector('.webview-status').classList.add('warning');
      }
    });
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
    const sourceBytes=decoded?messageBodyBytes(model):messageCapturedBytes(model);
    const bytes=sourceBytes.subarray(0,Math.min(sourceBytes.length,1024));
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
  const pre=view.querySelector('.auth-headers'),button=view.querySelector('.auth-reveal');
  const copy=view.closest('[role="tabpanel"]')?.querySelector('.copy-button');
  if(typeof view._authRedactedText==='string')pre.textContent=view._authRedactedText;
  view.dataset.revealed='false';view._authFullText=null;
  button.disabled=false;
  button.textContent='Reveal values';button.setAttribute('aria-pressed','false');button.setAttribute('aria-label','Reveal full authentication header values');
  const status=view.querySelector('.auth-status');
  status.textContent='Values are redacted.';status.classList.remove('warning');
  if(copy){
    delete copy._copyText;copy.dataset.copyKey='auth';copy.dataset.copyKind='canonical';
    copy.setAttribute('aria-label','Copy redacted authentication headers');copy.dataset.copyDescription='redacted authentication headers';
  }
  if(notify)view.dispatchEvent(new CustomEvent('saz-view-change',{bubbles:true}));
}
const AUTH_HEADERS=new Set(['authorization','proxy-authorization','www-authenticate','proxy-authenticate']);
function digestParameterNames(value){
  const names=[];let start=0,quoted=false,escaped=false;
  for(let index=0;index<=value.length;index++){
    if(index<value.length){
      const character=value[index];
      if(quoted){
        if(escaped)escaped=false;
        else if(character==='\\')escaped=true;
        else if(character==='"')quoted=false;
        continue;
      }
      if(character==='"'){quoted=true;continue}
      if(character!==',')continue;
    }
    const segment=value.slice(start,index).trim(),equals=segment.indexOf('='),name=equals>0?segment.slice(0,equals).trim():'';
    if(name.length>0&&name.length<=64&&/^[A-Za-z][A-Za-z0-9!#$%&'*+\-.^_`|~]*$/.test(name))names.push(name);
    start=index+1;
  }
  return names;
}
function redactAuthValue(value){
  const trimmed=value.trim();
  if(!trimmed)return'[redacted]';
  const match=/^[^ \t,]+/.exec(trimmed),candidate=match?.[0]||'';
  const known={basic:'Basic',bearer:'Bearer',digest:'Digest',ntlm:'NTLM',negotiate:'Negotiate'},scheme=known[candidate.toLowerCase()];
  if(!scheme)return'[redacted]';
  if(scheme!=='Digest')return`${scheme} [redacted]`;
  const parameters=[...new Set(digestParameterNames(trimmed.slice(candidate.length).trim()))].slice(0,32);
  return parameters.length?`${scheme} ${parameters.map(name=>`${name}=[redacted]`).join(', ')}`:`${scheme} [redacted]`;
}
function authText(model,reveal){
  return model.headers.filter(header=>AUTH_HEADERS.has(header.name.toLowerCase()))
    .map(header=>`${header.name}: ${reveal?header.value:redactAuthValue(header.value)}\n`).join('');
}
async function renderAuthView(view,generation){
  if(view._authReady)return;
  try{
    const model=await messageViewModel(view);
    if(generation!==renderGeneration||!view.isConnected)return;
    const text=authText(model,false);
    view._authRedactedText=text;view.querySelector('.auth-headers').textContent=text;view._authReady=true;
  }catch(error){
    const status=view.querySelector('.auth-status');
    status.textContent=`Authentication headers could not be loaded: ${error.message}`;status.classList.add('warning');
  }
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
    const full=authText(model,true);
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
function formatByteCount(value){
  const units=['B','KiB','MiB','GiB'];let size=value,unit=0;
  while(size>=1024&&unit<units.length-1){size/=1024;unit++}
  return unit===0?`${value.toLocaleString('en-US')} ${units[unit]}`:`${size.toLocaleString('en-US',{minimumFractionDigits:1,maximumFractionDigits:1})} ${units[unit]}`;
}
function fullHeadersText(model){
  let text=`${model.startLine}\n`;
  for(const header of model.headers){
    text+=`${header.name}: ${header.value}\n`;
    if(text.length>MAX_COPY_CHARACTERS)return null;
  }
  return text;
}
function boundedDisplay(value,limit,marker){
  if(value.length<=limit)return value;
  const prefix=Math.max(0,limit-marker.length-1);
  return`${value.slice(0,prefix)}\n${marker}`;
}
function rawBodyText(model){return decodeBodyText(model)}
function rawCopyText(panel,model){
  const headers=fullHeadersText(model);
  if(headers===null)return null;
  const body=model.body,label=panel.dataset.bodyLabel||'Body',status=panel.dataset.bodyStatus||'';
  const size=body.removedEncodings.length
    ?`${formatByteCount(body.length)} decoded; ${formatByteCount(body.capturedLength)} captured`
    :formatByteCount(body.length);
  let text=`Original headers\n${headers}\n${body.removedEncodings.length?'Decoded body':'Body'} (${size})\nFormat: ${label}\nStatus: ${status}\n`;
  if(body.decodingStatus)text+=`Decode status: ${body.decodingStatus}\n`;
  text+=rawBodyText(model);
  if(body.isTruncated)text+='\n[Body preview truncated; the complete body is not retained in this report.]';
  if(body.showCapturedBytes){
    const captured=messageCapturedBytes(model);
    text+=`\n\nCaptured bytes (pre-decode)\n${capturedPreviewText(model)}`;
    if(body.fallbackCapturedTruncated||captured.length<body.capturedLength)text+='\n[Captured byte preview truncated]';
  }
  return text.length<=MAX_COPY_CHARACTERS?text:null;
}
async function renderHeadersView(pre,generation){
  if(pre._hydrated)return;
  try{
    const model=await messageViewModel(pre);
    if(generation!==renderGeneration||!pre.isConnected)return;
    const headers=fullHeadersText(model);
    const source=headers??`${model.startLine}\n${model.headers.map(header=>`${header.name}: ${header.value}\n`).join('')}`;
    pre.textContent=boundedDisplay(source,256*1024,'[Header display truncated at the 256 KiB rendering limit.]\n');
    pre._hydrated=true;
  }catch(error){pre.textContent=`Headers could not be loaded: ${error.message}`;pre.classList.add('warning')}
}
async function renderRawView(panel,generation){
  if(panel._hydrated)return;
  try{
    const model=await messageViewModel(panel);
    if(generation!==renderGeneration||!panel.isConnected)return;
    const headers=panel.querySelector('.headers');if(headers)await renderHeadersView(headers,generation);
    let body=rawBodyText(model);
    if(model.body.isTruncated)body+='\n[Body preview truncated; the complete body is not retained in this report.]';
    panel.querySelector('[data-copy-field="rawBody"]').textContent=boundedDisplay(body,256*1024,'[Body display truncated at the 256 KiB rendering limit.]');
    const captured=panel.querySelector('[data-copy-field="captured"]');
    if(captured){
      const bytes=messageCapturedBytes(model);
      let text=capturedPreviewText(model);
      if(model.body.fallbackCapturedTruncated||bytes.length<model.body.capturedLength)text+='\n[Captured byte preview truncated]';
      captured.textContent=boundedDisplay(text,256*1024,'[Captured-byte display truncated at the 256 KiB rendering limit.]');
    }
    panel._hydrated=true;
  }catch(error){
    panel.querySelectorAll('[data-copy-field]').forEach(target=>{target.textContent=`Content could not be displayed: ${error.message}`;target.classList.add('warning')});
  }
}
async function canonicalCopySource(button){
  const panel=button.closest('.message-panel');
  if(!panel)return{error:'Copy source is detached.'};
  let model;
  try{model=await loadMessageModel(panel)}
  catch(error){return{error:`Copy source could not be decoded. ${error.message}`}}
  const key=button.dataset.copyKey;
  if(key==='headers'){
    const text=fullHeadersText(model);return text===null?{error:'Copy source exceeds the 1 MiB safety limit.'}:{text};
  }
  if(key==='raw'){
    const text=rawCopyText(panel,model);return text===null?{error:'Copy source exceeds the 1 MiB safety limit.'}:{text};
  }
  if(key==='webview'){
    const text=decodeBodyText(model);return text.length>MAX_COPY_CHARACTERS?{error:'Copy source exceeds the 1 MiB safety limit.'}:{text};
  }
  if(key==='auth'){
    const view=button.closest('[role="tabpanel"]').querySelector('.auth-view');
    const text=view?.dataset.revealed==='true'?view._authFullText:authText(model,false);
    return typeof text==='string'?{text}:{error:'Authentication headers could not be prepared.'};
  }
  if(key==='json'||key==='xml'){
    const container=button.closest('[role="tabpanel"]').querySelector('.structured-body');
    await hydrateStructuredBody(container,renderGeneration);
    return typeof container._formattedText==='string'?{text:container._formattedText}:{error:`${key.toUpperCase()} text could not be prepared.`};
  }
  return{error:'Copy source is unavailable.'};
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
function viewsWithin(root,selector){
  const matches=[...root.querySelectorAll(selector)];
  if(root.matches?.(selector))matches.unshift(root);
  return matches;
}
function hydrateViewPayloads(root,generation){
  if(root.classList?.contains('hidden')||root.closest?.('.primary-panel.hidden'))return;
  viewsWithin(root,'.websocket-inspector').forEach(host=>renderWebSocketInspector(host,generation));
  viewsWithin(root,'.message-panel .structured-body').forEach(view=>{if(!view.closest('.tab-panel.hidden'))hydrateStructuredBody(view,generation)});
  viewsWithin(root,'.headers[data-copy-field="headers"]').forEach(view=>{if(!view.closest('.tab-panel.hidden'))renderHeadersView(view,generation)});
  viewsWithin(root,'.tab-panel-raw').forEach(view=>{if(!view.closest('.tab-panel.hidden'))renderRawView(view,generation)});
  viewsWithin(root,'.auth-view').forEach(view=>{if(!view.closest('.tab-panel.hidden'))renderAuthView(view,generation)});
  viewsWithin(root,'.image-view').forEach(view=>{if(!view.closest('.tab-panel.hidden'))renderImageView(view,generation)});
  viewsWithin(root,'.webview').forEach(view=>{if(!view.closest('.tab-panel.hidden'))renderWebView(view,generation)});
  viewsWithin(root,'.hex-view').forEach(view=>{if(!view.closest('.tab-panel.hidden'))renderHexView(view,generation)});
  root.querySelectorAll('.protocol-block').forEach(host=>{
    if((host.dataset.compressedPayload||host._payloadData!==undefined)&&!host.closest('.tab-panel.hidden'))renderProtocolTree(host,generation);
  });
  root.querySelectorAll('.tree-subview').forEach(host=>{
    if((host.dataset.compressedPayload||host._payloadData!==undefined||host._treeData!==undefined)&&!host.classList.contains('hidden')&&!host.closest('.tab-panel.hidden'))renderValueTree(host,generation);
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
    container.dataset.viewMode=treeView&&!treeView.classList.contains('hidden')?'tree':'pretty';
    toolbar.querySelectorAll('.view-toggle button').forEach(btn=>{
      btn.addEventListener('click',()=>{
        if(btn.disabled)return;
        const showTree=btn.dataset.view==='tree';
        container.dataset.viewMode=showTree?'tree':'pretty';
        treeView.classList.toggle('hidden',!showTree);
        prettyView.classList.toggle('hidden',showTree);
        toolbar.querySelectorAll('.view-toggle button').forEach(other=>other.setAttribute('aria-pressed',String(other===btn)));
        if(showTree){
          if(!treeView._treeRendered)treeView._treeBuildToken=null;
          renderValueTree(treeView,renderGeneration);
        }
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
async function copySource(button){
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
  if(button.dataset.copyKind==='canonical')return canonicalCopySource(button);
  return{error:'Copy source could not be decoded.'};
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
  root.querySelectorAll('.copy-button').forEach(button=>{
    button.addEventListener('click',async()=>{
      if(button.disabled)return;
      const status=button.parentElement.querySelector('.copy-status');
      const source=await copySource(button);
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
function bindFilter(inputId,selectId,checkboxId,tableId){
  const input=document.getElementById(inputId),select=document.getElementById(selectId),checkbox=document.getElementById(checkboxId),rows=document.querySelectorAll(`#${tableId} tbody tr`);
  function apply(){
    const query=input.value.toLowerCase(),filter=select.value;
    rows.forEach(row=>{
      const filterMatch=!filter||(filter==='mapi'?row.dataset.mapi==='true':filter==='websocket'?row.dataset.websocket==='true':row.dataset.filter===filter);
      const visible=(!query||row.dataset.search.includes(query))&&filterMatch&&(!checkbox.checked||row.dataset.method!=='connect');
      row.classList.toggle('hidden',!visible);
    });
  }
  input.addEventListener('input',apply);select.addEventListener('change',apply);checkbox.addEventListener('change',apply);
  return apply;
}
const httpSearch=document.getElementById('httpSearch');
const httpFilter=document.getElementById('httpFilter');
const hideConnect=document.getElementById('hideConnect');
const applyHttpFilter=bindFilter('httpSearch','httpFilter','hideConnect','httpTable');
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
  if(hideConnect.checked)params.set('hideConnect','1');
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
    if(entries.some(([key])=>!['v','session','q','filter','hideConnect'].includes(key)))return{error:'Inspector link state contains an unsupported setting.'};
    if(params.get('v')!=='1')return{error:'Inspector link version is not supported.'};
    const session=params.get('session')||'';
    const query=params.get('q')||'';
    const filter=params.get('filter')||'';
    const hideConnectValue=params.get('hideConnect');
    if(!/^http-detail-\d{1,9}$/.test(session))return{error:'Inspector link does not identify a valid session.'};
    if(query.length>MAX_INSPECTOR_QUERY_LENGTH)return{error:'Inspector search text is too long.'};
    if(!ALLOWED_INSPECTOR_FILTERS.has(filter))return{error:'Inspector link contains an unsupported filter.'};
    if(hideConnectValue!==null&&hideConnectValue!=='1')return{error:'Inspector link contains an unsupported CONNECT filter setting.'};
    return{session,query,filter,hideConnect:hideConnectValue==='1'};
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
  const owner=tablist.classList.contains('primary-tab-strip')?tablist.parentElement.parentElement:tablist.parentElement;
  const panels=owner.querySelectorAll(':scope>.tab-panels>.tab-panel');
  const split=tablist.classList.contains('primary-tab-strip')&&owner.classList.contains('http-split');
  let targetPanel=null;
  const previous=tabs.find(tab=>tab.getAttribute('aria-selected')==='true');
  if(previous&&previous!==target&&!split){
    const previousPanel=owner.querySelector(`#${CSS.escape(previous.getAttribute('aria-controls'))}`);
    if(previousPanel)deactivateDynamicViews(previousPanel);
  }
  tabs.forEach(tab=>{const active=tab===target;tab.setAttribute('aria-selected',String(active));tab.tabIndex=active?0:-1});
  panels.forEach(panel=>{const active=panel.id===target.getAttribute('aria-controls');panel.classList.toggle('hidden',split?false:!active);if(active)targetPanel=panel});
  if(targetPanel)hydrateViewPayloads(targetPanel,renderGeneration);
  if(options&&options.remember)preferredTab[tablist.dataset.side]=key;
  if(options&&options.focus)target.focus();
  if(!split)owner.dispatchEvent(new CustomEvent('saz-view-change',{bubbles:true}));
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
function httpElement(tag,className,text){
  const element=document.createElement(tag);
  if(className)element.className=className;
  if(text!==undefined)element.textContent=text;
  return element;
}
function httpCopyToolbar(key,enabled,accessibleName,description,kind='canonical'){
  const toolbar=httpElement('div','copy-toolbar');
  const button=httpElement('button','copy-button','Copy');button.type='button';
  button.setAttribute('aria-label',accessibleName);button.dataset.copyDescription=description;
  if(!enabled){button.disabled=true;button.setAttribute('aria-disabled','true')}
  else{button.dataset.copyKind=kind;if(kind==='canonical')button.dataset.copyKey=key}
  const status=httpElement('span','copy-status');status.setAttribute('role','status');status.setAttribute('aria-live','polite');
  toolbar.append(button,status);return toolbar;
}
function httpTabButton(side,key,label,enabled,selected){
  const button=httpElement('button','',label);button.type='button';button.id=`${side}-tab-${key}`;
  button.setAttribute('role','tab');button.setAttribute('aria-controls',`${side}-panel-${key}`);
  button.setAttribute('aria-selected',String(selected));button.dataset.tab=key;button.tabIndex=selected?0:-1;
  if(!enabled){button.disabled=true;button.setAttribute('aria-disabled','true')}
  return button;
}
function httpTabPanel(side,key,selected,enabled,copy,content,unavailable){
  const panel=httpElement('div',`tab-panel tab-panel-${key}${selected?'':' hidden'}`);
  panel.id=`${side}-panel-${key}`;panel.setAttribute('role','tabpanel');panel.setAttribute('aria-labelledby',`${side}-tab-${key}`);panel.tabIndex=0;
  panel.append(copy);
  if(enabled){if(content)panel.append(content)}
  else panel.append(httpElement('div','tab-empty',unavailable));
  return panel;
}
function formatMeta(model){
  const meta=httpElement('div','format-meta');
  meta.append(httpElement('span','format-badge',model.label),httpElement('span','format-status',model.status));
  return meta;
}
function createStructuredBody(model,format){
  const container=httpElement('div','structured-body');container.dataset.format=format;container.append(formatMeta(model));
  const toolbar=httpElement('div','tree-toolbar'),toggle=httpElement('div','view-toggle');
  toggle.setAttribute('role','group');toggle.setAttribute('aria-label',`${format.toUpperCase()} view mode`);
  const treeButton=httpElement('button','', 'Tree');treeButton.type='button';treeButton.dataset.view='tree';treeButton.setAttribute('aria-pressed','true');
  const prettyButton=httpElement('button','', 'Pretty Text');prettyButton.type='button';prettyButton.dataset.view='pretty';prettyButton.setAttribute('aria-pressed','false');
  toggle.append(treeButton,prettyButton);
  const expand=httpElement('button','tree-expand-all','Expand all');expand.type='button';
  const collapse=httpElement('button','tree-collapse-all','Collapse all');collapse.type='button';
  toolbar.append(toggle,expand,collapse);container.append(toolbar);
  const tree=httpElement('div','tree-subview','Tree loads when this session is selected.');
  const pretty=httpElement('div','pretty-subview hidden'),pre=httpElement('pre','body-view formatted-view');
  pre.dataset.format=format;pre.dataset.copyField=format;pretty.append(pre);container.append(tree,pretty);return container;
}
function createImageView(model){
  const info=model.image,view=httpElement('div','image-view');view.dataset.imageMime=info.mimeType;
  const meta=httpElement('div','image-meta');
  meta.append(httpElement('span','',info.mimeType),httpElement('span','',`${model.body.length.toLocaleString('en-US')} bytes`),
    httpElement('span','',info.animation),httpElement('span','image-dimensions','Dimensions load with the image.'));
  view.append(meta,httpElement('div','format-status',info.detection));
  if(info.warning)view.append(httpElement('div','warning',info.warning));
  const stage=httpElement('div','image-stage'),image=document.createElement('img');
  image.alt='Captured HTTP image';image.decoding='async';image.referrerPolicy='no-referrer';image.draggable=false;stage.append(image);
  const status=httpElement('div','image-load-status','Image loads when this tab is selected.');status.setAttribute('role','status');status.setAttribute('aria-live','polite');
  view.append(stage,status);return view;
}
function createWebView(model){
  const view=httpElement('div','webview');view.dataset.mode='rendered';
  const toolbar=httpElement('div','webview-toolbar'),modes=httpElement('div','webview-mode');
  modes.setAttribute('role','group');modes.setAttribute('aria-label','WebView display mode');
  for(const [key,label,pressed] of [['rendered','Rendered',true],['source','Source',false]]){
    const button=httpElement('button','',label);button.type='button';button.dataset.webviewMode=key;button.setAttribute('aria-pressed',String(pressed));modes.append(button);
  }
  toolbar.append(modes,httpElement('span','format-status',model.webViewDetection));
  const status=httpElement('div','webview-status','Inert preview loads only when this tab is selected.');status.setAttribute('role','status');status.setAttribute('aria-live','polite');
  const source=httpElement('pre','webview-source hidden');source.tabIndex=0;source.setAttribute('aria-label','Original decoded captured HTML source');source.dataset.copyField='webview';
  view.append(toolbar,status,httpElement('div','webview-frame-host'),source);return view;
}
function createHexView(model,decodedEnabled){
  const body=model.body,view=httpElement('div','hex-view');
  view.dataset.capturedLength=String(body.capturedLength);view.dataset.decodedLength=String(body.length);
  const toolbar=httpElement('div','hex-toolbar'),sourceLabel=httpElement('span','hex-source-label','Captured body bytes'),label=httpElement('label','','Source ');
  const select=httpElement('select','hex-source');select.setAttribute('aria-label','Hex byte source');
  const captured=httpElement('option','','Captured');captured.value='captured';
  const decoded=httpElement('option','','Decoded');decoded.value='decoded';decoded.disabled=!decodedEnabled;select.append(captured,decoded);label.append(select);toolbar.append(sourceLabel,label);view.append(toolbar);
  if(body.removedEncodings.length)view.append(httpElement('div','decode-status',`Removed encodings: ${body.removedEncodings.join(' -> ')}`));
  const dump=httpElement('pre','hex-dump');dump.tabIndex=0;dump.setAttribute('aria-label','Captured body byte hex dump');
  view.append(dump,httpElement('div','hex-status muted'));return view;
}
function createAuthView(){
  const view=httpElement('div','auth-view');view.dataset.revealed='false';
  view.append(httpElement('div','warning','Authentication values are redacted. Reveal only when it is safe to display captured credentials or challenge tokens.'));
  const reveal=httpElement('button','auth-reveal','Reveal values');reveal.type='button';reveal.setAttribute('aria-pressed','false');reveal.setAttribute('aria-label','Reveal full authentication header values');
  const pre=httpElement('pre','auth-headers');pre.dataset.copyField='auth';
  const status=httpElement('div','auth-status','Values are redacted.');status.setAttribute('role','status');status.setAttribute('aria-live','polite');
  view.append(reveal,pre,status);return view;
}
function createRawView(model){
  const fragment=document.createDocumentFragment();
  fragment.append(httpElement('h4','','Original headers'));
  const headers=httpElement('pre','headers');headers.dataset.copyField='headers';fragment.append(headers);
  const heading=httpElement('h4','',model.body.removedEncodings.length?'Decoded body ':'Body ');
  heading.append(httpElement('span','muted',`(${model.body.removedEncodings.length?`${formatByteCount(model.body.length)} decoded; ${formatByteCount(model.body.capturedLength)} captured`:formatByteCount(model.body.length)})`));
  fragment.append(heading,formatMeta(model));
  if(model.body.decodingStatus)fragment.append(httpElement('div',model.body.removedEncodings.length?'decode-status':'warning',model.body.decodingStatus));
  const body=httpElement('pre','body-view');body.dataset.copyField='rawBody';fragment.append(body);
  if(model.body.showCapturedBytes){
    const details=httpElement('details','captured-bytes'),summary=httpElement('summary','','Captured bytes (pre-decode)'),captured=httpElement('pre');
    captured.dataset.copyField='captured';details.append(summary,captured);fragment.append(details);
  }
  return fragment;
}
function moveProtocolSource(source){
  if(!source)return null;
  source.hidden=false;source.className='protocol-block';source.removeAttribute('data-message-side');
  source.textContent='Protocol tree loads when this view is selected.';source.firstChild&&source.firstChild.parentElement?.classList.add('muted');
  return source;
}
function createMessagePanel(side,title,model,protocolSource){
  const lower=title.toLowerCase(),panel=httpElement('section','message-panel');panel.dataset.messageSide=side;
  if(model){panel.dataset.bodyFormat=model.format;panel.dataset.bodyLabel=model.label;panel.dataset.bodyStatus=model.status}
  const flags={
    json:!!model&&model.format==='json'&&model.canToggle,
    xml:!!model&&model.format==='xml'&&model.canToggle,
    mapi:!!protocolSource,image:!!model?.image,webview:!!model?.webViewDetection,
    hex:!!model&&typeof model.body.captured==='string'&&model.body.captured.length>0,
    auth:!!model?.hasAuth,headers:!!model&&model.headers.length>0,raw:!!model
  };
  const initial=flags.mapi?'mapi':flags.json?'json':flags.xml?'xml':flags.raw?'raw':flags.headers?'headers':null;
  const strip=httpElement('div','tab-strip');strip.setAttribute('role','tablist');strip.setAttribute('aria-label',`${title} detail views`);
  strip.dataset.side=side;strip.dataset.priority='mapi,json,xml,raw,headers';
  const labels={json:'JSON',xml:'XML',mapi:'MAPI',image:'Image',webview:'WebView',hex:'HexView',auth:'Auth',headers:'Headers',raw:'Raw'};
  Object.entries(labels).forEach(([key,label])=>strip.append(httpTabButton(side,key,label,flags[key],initial===key)));panel.append(strip);
  const panels=httpElement('div','tab-panels');
  const descriptions={
    json:[`Copy ${lower} JSON pretty text`,`${lower} JSON pretty text`],xml:[`Copy ${lower} XML pretty text`,`${lower} XML pretty text`],
    mapi:[`Copy ${lower} MAPI protocol tree`,`${lower} MAPI protocol tree`],image:[`Copy ${lower} image metadata`,`${lower} image metadata`],
    webview:[`Copy ${lower} HTML source`,`${lower} HTML source`],hex:[`Copy ${lower} hex view`,`${lower} hex view`],
    auth:[`Copy redacted ${lower} authentication headers`,`redacted ${lower} authentication headers`],
    headers:[`Copy ${lower} headers`,`${lower} headers`],raw:[`Copy ${lower} raw message`,`${lower} raw message`]
  };
  const contents={
    json:flags.json?createStructuredBody(model,'json'):null,xml:flags.xml?createStructuredBody(model,'xml'):null,
    mapi:flags.mapi?moveProtocolSource(protocolSource):null,image:flags.image?createImageView(model):null,
    webview:flags.webview?createWebView(model):null,
    hex:flags.hex?createHexView(model,model.body.decoded!==null&&model.body.decoded!==undefined):null,
    auth:flags.auth?createAuthView():null,
    headers:flags.headers?httpElement('pre','headers'):null,
    raw:flags.raw?createRawView(model):null
  };
  if(contents.headers)contents.headers.dataset.copyField='headers';
  const unavailable={
    json:`JSON view is not available: the ${lower} body is not recognized, valid JSON.`,
    xml:`XML view is not available: the ${lower} body is not recognized, valid XML.`,
    mapi:`MAPI view is not available: no protocol tree was parsed for this ${lower}.`,
    image:`Image view is not available: the ${lower} body is not a complete retained PNG, JPEG, GIF, WebP, BMP, or ICO image.`,
    webview:`WebView is not available: the ${lower} body is not complete retained HTML or XHTML.`,
    hex:`HexView is not available: no captured ${lower} body bytes were retained.`,
    auth:`Auth view is not available: no Authorization, Proxy-Authorization, WWW-Authenticate, or Proxy-Authenticate header was captured for this ${lower}.`,
    headers:model?`No headers were captured for this ${lower}.`:`No ${lower} entry was captured.`,
    raw:`No ${lower} entry was captured.`
  };
  Object.keys(labels).forEach(key=>{
    const kind=key==='mapi'?'mapi':key==='image'||key==='hex'?key:'canonical';
    const copy=httpCopyToolbar(key,flags[key],descriptions[key][0],descriptions[key][1],kind);
    panels.append(httpTabPanel(side,key,initial===key,flags[key],copy,contents[key],unavailable[key]));
  });
  if(!Object.values(flags).some(Boolean))panels.append(httpElement('div','tab-empty',`No ${lower} entry was captured.`));
  panel.append(panels);return panel;
}
function primaryTab(side,label,selected){
  const button=httpTabButton('primary',side,label,true,selected);return button;
}
function buildHttpInspector(source,store){
  const request=store.request?validateMessageModel(store.request):null,response=store.response?validateMessageModel(store.response):null;
  const details=[...source.children].filter(child=>child.classList.contains('session-details'));
  const requestProtocol=source.querySelector('.mapi-source[data-message-side="request"]');
  const responseProtocol=source.querySelector('.mapi-source[data-message-side="response"]');
  const bar=httpElement('div','primary-view-bar'),tabs=httpElement('div','primary-tab-strip tab-strip');
  tabs.setAttribute('role','tablist');tabs.setAttribute('aria-label','Request or response');tabs.dataset.side='primary';tabs.dataset.priority='request,response';
  tabs.append(primaryTab('request','Request',true),primaryTab('response','Response',false));
  bar.append(tabs);
  const panels=httpElement('div','primary-panels tab-panels');
  const requestPanel=httpElement('div','tab-panel primary-panel');requestPanel.id='primary-panel-request';requestPanel.dataset.side='Request';
  requestPanel.setAttribute('role','tabpanel');requestPanel.setAttribute('aria-labelledby','primary-tab-request');requestPanel.tabIndex=0;
  const requestHeading=httpElement('h3','http-pane-heading','Request');requestHeading.id='request-pane-heading';
  requestPanel.append(requestHeading,createMessagePanel('request','Request',request,requestProtocol));
  const splitter=httpElement('div','http-splitter hidden');splitter.setAttribute('role','separator');splitter.setAttribute('aria-label','Resize Request and Response panes');
  splitter.setAttribute('aria-orientation','vertical');splitter.tabIndex=0;splitter.title='Drag or use Left and Right arrow keys to resize Request and Response panes';
  const responsePanel=httpElement('div','tab-panel primary-panel hidden');responsePanel.id='primary-panel-response';responsePanel.dataset.side='Response';
  responsePanel.setAttribute('role','tabpanel');responsePanel.setAttribute('aria-labelledby','primary-tab-response');responsePanel.tabIndex=0;
  const responseHeading=httpElement('h3','http-pane-heading','Response');responseHeading.id='response-pane-heading';
  responsePanel.append(responseHeading,createMessagePanel('response','Response',response,responseProtocol));
  panels.append(requestPanel,splitter,responsePanel);source.replaceChildren(...details,bar,panels);
}
function setupInspectorContent(root,generation){
  prepareLazyPayloads(root);setupTabs(root);setupTreeToggles(root);setupCopyControls(root);setupDynamicViewControls(root,generation);
  if(!setupHttpLayout(root))setupHttpViewSearch(root,generation);
  hydrateViewPayloads(root,generation);
}
async function initializeHttpInspector(root,generation,focusBody){
  const source=root.querySelector('.http-session-source');
  if(!source){
    setupInspectorContent(root,generation);
    if(focusBody)inspectorClose.focus();
    return;
  }
  try{
    if(source.dataset.payloadError)throw new Error(source.dataset.payloadError);
    const store=await decodeCompressedPayload(source,'http-session');
    if(generation!==renderGeneration||!source.isConnected)return;
    if(!store||store.schema!==1||typeof store!=='object'||Array.isArray(store))throw new Error('session data has an unsupported schema');
    buildHttpInspector(source,store);setupInspectorContent(root,generation);
    if(focusBody){
      const tab=root.querySelector('.primary-tab-strip [role="tab"][aria-selected="true"]');
      (tab||inspectorClose).focus();
    }
  }catch(error){
    if(generation!==renderGeneration||!source.isConnected)return;
    const warning=httpElement('div','warning',payloadFailureText(error,'HTTP session',' Regenerate the report with the current SAZ Viewer.'));
    warning.setAttribute('role','status');source.replaceChildren(warning);
    if(focusBody)inspectorClose.focus();
  }
}
function loadRow(row){
  const template=document.getElementById(row.dataset.detail);
  if(!template)return false;
  inspectorBody._disposeHttpViewSearch?.();
  inspectorBody._disposeHttpSplitter?.();
  deactivateDynamicViews(inspectorBody);
  inspectorBody._clearHttpViewSearch=null;
  inspectorBody._disposeHttpViewSearch=null;
  inspectorLayoutToggle.classList.add('hidden');
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
  initializeHttpInspector(inspectorBody,generation,focusWasInBody);
  updateNavState();
  // Request is always the default-selected primary tab after (re)loading a row (see
  // initialTabFor's "request,response" priority and the preferredTab reset above), so it is a
  // stable, guaranteed-enabled place to land focus when the previously focused element lived
  // inside the body content we just discarded (e.g. a secondary tab or tree item reached via the
  // Alt+Arrow inspector-navigation shortcut).
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
  inspectorBody._disposeHttpViewSearch?.();
  inspectorBody._disposeHttpSplitter?.();
  inspectorBody._clearHttpViewSearch=null;
  inspectorBody._disposeHttpViewSearch=null;
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
inspectorLayoutToggle.addEventListener('click',()=>applyHttpLayout(inspectorBody,httpLayoutMode==='split'?'single':'split',true,true));
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
  hideConnect.checked=state.hideConnect;
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

}
