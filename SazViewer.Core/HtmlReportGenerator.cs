using System.Globalization;
using System.Net;
using System.Text;

namespace SazViewer.Core;

public sealed class HtmlReportGenerator
{
    public string Generate(SazReport report)
    {
        var html = new StringBuilder(256 * 1024);
        html.Append("""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; img-src data:">
<title>SAZ report</title>
<style>
:root{color-scheme:light dark;--bg:#0d1117;--panel:#161b22;--text:#e6edf3;--muted:#8b949e;--line:#30363d;--accent:#58a6ff;--warn:#d29922}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px/1.45 system-ui,Segoe UI,sans-serif}
header,main{max-width:1500px;margin:auto;padding:20px}h1,h2{margin:.2em 0}.muted{color:var(--muted)}
.cards{display:flex;gap:12px;flex-wrap:wrap;margin:18px 0}.card{background:var(--panel);border:1px solid var(--line);border-radius:8px;padding:12px 18px;min-width:145px}.card b{font-size:24px;display:block}
.controls{display:flex;gap:10px;flex-wrap:wrap;margin:12px 0}input,select{background:var(--panel);border:1px solid var(--line);border-radius:6px;color:var(--text);padding:8px 10px}
input{min-width:300px;flex:1}table{width:100%;border-collapse:collapse;background:var(--panel);font-size:13px}th{position:sticky;top:0;background:#21262d;text-align:left}
th,td{padding:8px;border:1px solid var(--line);vertical-align:top}tr:hover{background:#1f2630}.url{max-width:520px;word-break:break-all}.num{text-align:right;white-space:nowrap}
details{margin:4px 0}summary{cursor:pointer;color:var(--accent)}pre{white-space:pre-wrap;overflow:auto;max-height:360px;background:var(--bg);border:1px solid var(--line);padding:10px;word-break:break-word}
.warning{border-left:4px solid var(--warn);padding:6px 10px;margin:5px 0;background:#2b2111}.hidden{display:none}.badge{padding:2px 6px;border:1px solid var(--line);border-radius:10px;white-space:nowrap}
@media(max-width:800px){table{display:block;overflow-x:auto}header,main{padding:12px}}
</style>
</head>
<body>
<header><h1>SAZ report</h1><div class="muted">
""");
        Text(html, report.SourceName);
        html.Append("</div><div class=\"cards\">");
        Card(html, "HTTP sessions", report.Sessions.Count);
        Card(html, "WebSocket messages", report.WebSocketMessages.Count);
        Card(html, "Warnings", report.Warnings.Count);
        Card(html, "Total HTTP bytes", report.Sessions.Sum(s => s.RequestBytes + s.ResponseBytes), bytes: true);
        html.Append("</div></header><main>");
        AppendWarnings(html, report.Warnings);
        AppendHttpSection(html, report.Sessions);
        AppendWebSocketSection(html, report.WebSocketMessages);
        html.Append("""
</main>
<script>
(()=>{function bind(inputId,selectId,tableId){const input=document.getElementById(inputId),select=document.getElementById(selectId),rows=document.querySelectorAll(`#${tableId} tbody tr`);
function apply(){const q=input.value.toLowerCase(),f=select.value;rows.forEach(r=>{const okText=!q||r.dataset.search.includes(q),okFilter=!f||r.dataset.filter===f;r.classList.toggle('hidden',!(okText&&okFilter))})}
input.addEventListener('input',apply);select.addEventListener('change',apply)}bind('httpSearch','httpFilter','httpTable');bind('wsSearch','wsFilter','wsTable')})();
</script>
</body></html>
""");
        return html.ToString();
    }

    private static void AppendWarnings(StringBuilder html, IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0)
        {
            return;
        }

        html.Append("<section><h2>Warnings</h2><details");
        if (warnings.Count <= 5) html.Append(" open");
        html.Append("><summary>").Append(warnings.Count).Append(" warning(s)</summary>");
        foreach (var warning in warnings.Take(500))
        {
            html.Append("<div class=\"warning\">");
            Text(html, warning);
            html.Append("</div>");
        }
        if (warnings.Count > 500)
        {
            html.Append("<div class=\"warning\">Additional warnings omitted from display.</div>");
        }
        html.Append("</details></section>");
    }

    private static void AppendHttpSection(StringBuilder html, IReadOnlyList<HttpSession> sessions)
    {
        html.Append("""
<section><h2>HTTP sessions</h2>
<div class="controls"><input id="httpSearch" type="search" placeholder="Search method, URL, status, content type, endpoints...">
<select id="httpFilter"><option value="">All statuses</option><option value="2">2xx</option><option value="3">3xx</option><option value="4">4xx</option><option value="5">5xx</option><option value="0">Missing/other</option></select></div>
<table id="httpTable"><thead><tr><th>Time</th><th>ID</th><th>Method</th><th>URL</th><th>Status</th><th>Type</th><th class="num">Req</th><th class="num">Resp</th><th>Details</th></tr></thead><tbody>
""");
        foreach (var session in sessions)
        {
            var filter = session.StatusCode is >= 200 and <= 599
                ? (session.StatusCode.Value / 100).ToString(CultureInfo.InvariantCulture)
                : "0";
            var search = string.Join(' ', new[]
            {
                session.Id, session.Method, session.Url, session.StatusCode?.ToString(CultureInfo.InvariantCulture),
                session.StatusText, session.ContentType, session.ClientEndpoint, session.ServerEndpoint
            }.Where(value => !string.IsNullOrWhiteSpace(value))).ToLowerInvariant();
            html.Append("<tr data-filter=\"").Append(filter).Append("\" data-search=\"");
            Attribute(html, search);
            html.Append("\"><td>");
            Text(html, FormatTimestamp(session.Timestamp));
            html.Append("</td><td>");
            Text(html, session.Id);
            html.Append("</td><td><span class=\"badge\">");
            Text(html, session.Method ?? "-");
            html.Append("</span></td><td class=\"url\">");
            Text(html, session.Url ?? "-");
            html.Append("</td><td>");
            Text(html, session.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? "-");
            if (!string.IsNullOrWhiteSpace(session.StatusText))
            {
                html.Append(' ');
                Text(html, session.StatusText);
            }
            html.Append("</td><td>");
            Text(html, session.ContentType ?? "-");
            html.Append("</td><td class=\"num\">").Append(FormatBytes(session.RequestBytes))
                .Append("</td><td class=\"num\">").Append(FormatBytes(session.ResponseBytes))
                .Append("</td><td>");
            AppendSessionDetails(html, session);
            html.Append("</td></tr>");
        }
        html.Append("</tbody></table></section>");
    }

    private static void AppendSessionDetails(StringBuilder html, HttpSession session)
    {
        html.Append("<details><summary>Inspect</summary>");
        if (session.ClientEndpoint is not null || session.ServerEndpoint is not null)
        {
            html.Append("<p><b>Endpoints:</b> ");
            Text(html, $"{session.ClientEndpoint ?? "?"} -> {session.ServerEndpoint ?? "?"}");
            html.Append("</p>");
        }
        AppendMessage(html, "Request", session.Request);
        AppendMessage(html, "Response", session.Response);
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

    private static void AppendMessage(StringBuilder html, string title, HttpMessage? message)
    {
        if (message is null)
        {
            return;
        }

        html.Append("<details><summary>").Append(title).Append(" headers</summary><pre>");
        Text(html, message.StartLine + "\n");
        foreach (var header in message.Headers)
        {
            Text(html, $"{header.Name}: {header.Value}\n");
        }
        html.Append("</pre></details>");
        if (message.Body.Length > 0 || message.Body.Preview.Length > 0)
        {
            html.Append("<details><summary>").Append(title).Append(" body (")
                .Append(FormatBytes(message.Body.Length));
            if (message.Body.IsBinary) html.Append(", binary/hex");
            if (message.Body.IsTruncated) html.Append(", preview truncated");
            html.Append(")</summary><pre>");
            Text(html, message.Body.Preview);
            html.Append("</pre></details>");
        }
    }

    private static void AppendWebSocketSection(
        StringBuilder html,
        IReadOnlyList<WebSocketMessage> messages)
    {
        html.Append("""
<section><h2>WebSocket messages</h2>
<div class="controls"><input id="wsSearch" type="search" placeholder="Search WebSocket session, direction, type, preview...">
<select id="wsFilter"><option value="">All directions</option><option value="Client">Client to server</option><option value="Server">Server to client</option><option value="Unknown">Unknown</option></select></div>
<table id="wsTable"><thead><tr><th>Time</th><th>Session</th><th>#</th><th>Direction</th><th>Type</th><th class="num">Length</th><th>Preview</th></tr></thead><tbody>
""");
        foreach (var message in messages)
        {
            var search = $"{message.SessionId} {message.Direction} {message.Type} {message.Preview}".ToLowerInvariant();
            html.Append("<tr data-filter=\"");
            Attribute(html, message.Direction);
            html.Append("\" data-search=\"");
            Attribute(html, search);
            html.Append("\"><td>");
            Text(html, FormatTimestamp(message.Timestamp));
            html.Append("</td><td>");
            Text(html, message.SessionId);
            html.Append("</td><td>").Append(message.RecordIndex).Append("</td><td>");
            Text(html, message.Direction);
            html.Append("</td><td>");
            Text(html, message.Type);
            html.Append("</td><td class=\"num\">").Append(FormatBytes(message.PayloadLength))
                .Append("</td><td><pre>");
            Text(html, message.Preview);
            html.Append("</pre>");
            if (message.Warning is not null)
            {
                html.Append("<div class=\"warning\">");
                Text(html, message.Warning);
                html.Append("</div>");
            }
            html.Append("</td></tr>");
        }
        html.Append("</tbody></table></section>");
    }

    private static void Card(StringBuilder html, string label, long value, bool bytes = false)
    {
        html.Append("<div class=\"card\"><b>").Append(bytes ? FormatBytes(value) : value).Append("</b>");
        Text(html, label);
        html.Append("</div>");
    }

    private static string FormatTimestamp(DateTimeOffset? value) =>
        value?.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture) ?? "-";

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
