using System.Globalization;
using System.Net;
using SazViewer.Core;

namespace SazViewer.App.Model;

internal sealed record SessionColumnValue(string Display, IComparable? SortValue = null)
{
    public bool IsBlank => SortValue is null;
}

internal sealed record SessionColumnDefinition(
    SessionColumnSetting Setting,
    string Header,
    string Category,
    Func<SessionRow, SessionColumnValue> Read,
    bool Numeric = false)
{
    public string Id => Setting.Id;

    public bool IsCustom => Setting.Kind != SessionColumnSetting.BuiltInKind;
}

internal static class SessionColumnCatalog
{
    public static readonly IReadOnlyList<SessionColumnDefinition> BuiltIns =
    [
        Text("time", "Time", "Default", row => new(row.Time, row.Session.Timestamp)),
        Text("id", "ID", "Default", row => new(row.Id, NumericId(row.Id)), numeric: true),
        Text("result", "Result", "Default", row => new(row.Result, row.ResultCode), numeric: true),
        Text("method", "Method", "Default", row => Value(row.Method)),
        Text("url", "URL", "Default", row => Value(row.Url)),
        Text("elapsed", "Elapsed Time", "Default", row => Duration(row.Session.ElapsedMilliseconds), numeric: true),
        Text("request-size", "Req", "Default", row => Bytes(row.Session.RequestBytes), numeric: true),
        Text("response-size", "Resp", "Default", row => Bytes(row.Session.ResponseBytes), numeric: true),

        Text("protocol", "Protocol", "Basics", row => Value(Protocol(row.Session))),
        Text("scheme", "Scheme", "Basics", row => UriPart(row.Session.Url, uri => uri.Scheme)),
        Text("host", "Host", "Basics", row =>
        {
            var fromUrl = UriPart(row.Session.Url, uri => uri.Host);
            return fromUrl.IsBlank ? Header(row.Session.Request, "Host") : fromUrl;
        }),
        Text("path", "Path", "Basics", row => UriPart(row.Session.Url, uri => uri.AbsolutePath)),
        Text("query", "Query", "Basics", row => UriPart(row.Session.Url, uri => uri.Query.TrimStart('?'))),
        Text("content-type", "Content-Type", "Basics", row => Value(row.Session.ContentType)),
        Text("content-encoding", "Content-Encoding", "Basics", row => Header(row.Session.Response, "Content-Encoding")),
        Text("caching", "Caching", "Basics", row => Caching(row.Session)),
        Text("request-body-size", "Request body", "Basics", row => BodySize(row.Session.Request), numeric: true),
        Text("response-wire-size", "Response on wire", "Basics", row => ResponseWireSize(row.Session), numeric: true),
        Text("response-decoded-size", "Decoded response", "Basics", row => DecodedBodySize(row.Session.Response), numeric: true),
        Text("compression-ratio", "Compression ratio", "Basics", row => CompressionRatio(row.Session), numeric: true),
        Text("redirect-location", "Redirect Location", "Basics", row => Header(row.Session.Response, "Location")),

        Timer("client-connected", "ClientConnected"),
        Timer("client-begin-request", "ClientBeginRequest"),
        Timer("got-request-headers", "GotRequestHeaders"),
        Timer("client-done-request", "ClientDoneRequest"),
        Timer("server-connected", "ServerConnected"),
        Timer("fiddler-begin-request", "FiddlerBeginRequest"),
        Timer("server-got-request", "ServerGotRequest"),
        Timer("server-begin-response", "ServerBeginResponse"),
        Timer("got-response-headers", "GotResponseHeaders"),
        Timer("server-done-response", "ServerDoneResponse"),
        Timer("client-begin-response", "ClientBeginResponse"),
        Timer("client-done-response", "ClientDoneResponse"),
        DurationTimer("dns-time", "DNS time", "DNSTime", "x-dnstime"),
        DurationTimer("tcp-connect-time", "TCP connect", "TCPConnectTime", "x-tcpconnecttime"),
        DurationTimer("tls-handshake-time", "TLS handshake", "HTTPSHandshakeTime", "x-httpsHandshakeTime"),
        DurationTimer("gateway-time", "Gateway time", "GatewayTime", "x-gatewaytime"),
        Text("ttfb", "TTFB", "Timing", row => TimerDelta(row.Session, "ClientBeginRequest", "ServerBeginResponse"), numeric: true),
        Text("server-time", "Server time", "Timing", row => TimerDelta(row.Session, "ServerGotRequest", "ServerBeginResponse"), numeric: true),
        Text("download-time", "Download time", "Timing", row => TimerDelta(row.Session, "ServerBeginResponse", "ServerDoneResponse"), numeric: true),

        Text("client-endpoint", "Client IP:port", "Connection and process", row => Endpoint(row.Session.ClientEndpoint)),
        Text("server-ip", "Server IP", "Connection and process", row => Endpoint(Metadata(row.Session, "x-hostip"))),
        Text("egress-port", "Egress port", "Connection and process", row => Integer(Metadata(row.Session, "x-egressport")), numeric: true),
        Text("process", "Process", "Connection and process", row => Value(Metadata(row.Session, "x-processinfo"))),
        Text("process-name", "Process name", "Connection and process", row => Value(ProcessPart(row.Session, wantPid: false))),
        Text("process-id", "Process ID", "Connection and process", row => Integer(ProcessPart(row.Session, wantPid: true)), numeric: true),
        Text("sni-host", "SNI host", "Connection and process", row => Value(Metadata(row.Session, "https-client-snihostname"))),
        Text("tls-info", "TLS / certificate", "Connection and process", row => Value(TlsInfo(row.Session))),
        Text("comment", "Comment", "Connection and process", row => Value(Metadata(row.Session, "ui-comments"))),
        Text("mark-color", "Mark colour", "Connection and process", row => Value(Metadata(row.Session, "ui-color"))),
        Text("mark-background", "Mark background", "Connection and process", row => Value(Metadata(row.Session, "ui-backcolor"))),

        Text("websocket-count", "WebSocket messages", "Content-specific", row => Integer(row.WebSocketMessages.Count), numeric: true),
        Text("websocket-bytes", "WebSocket bytes", "Content-specific", row => Bytes(row.WebSocketMessages.Sum(message => Math.Max(0, message.PayloadLength))), numeric: true),
        Text("mapi-request-type", "MAPI request", "Content-specific", row => Value(row.Session.Mapi?.RequestType)),
        Text("mapi-rop-summary", "MAPI ROP summary", "Content-specific", row => Value(MapiSummary(row.Session.Mapi))),
        Text("auth-scheme", "Auth scheme", "Content-specific", row => Value(AuthScheme(row.Session)))
    ];

    private static readonly IReadOnlyDictionary<string, SessionColumnDefinition> ById =
        BuiltIns.ToDictionary(column => column.Id, StringComparer.Ordinal);

    public static IReadOnlyList<SessionColumnDefinition> Resolve(IEnumerable<SessionColumnSetting> settings)
    {
        var resolved = settings.Select(Resolve).Where(column => column is not null).Cast<SessionColumnDefinition>().ToArray();
        return resolved.Any(column => column.Setting.Visible) ? resolved : BuiltIns.Take(8).ToArray();
    }

    public static SessionColumnDefinition? Resolve(SessionColumnSetting setting)
    {
        if (setting.Kind == SessionColumnSetting.BuiltInKind)
        {
            return ById.TryGetValue(setting.Source, out var builtIn)
                ? builtIn with { Setting = setting, Header = string.IsNullOrWhiteSpace(setting.Header) ? builtIn.Header : setting.Header }
                : null;
        }
        var header = string.IsNullOrWhiteSpace(setting.Header) ? setting.Source : setting.Header;
        return setting.Kind switch
        {
            SessionColumnSetting.RequestHeaderKind => new(
                setting, header, "Custom request headers",
                row => Header(row.Session.Request, setting.Source, MaskSensitive(setting.Source))),
            SessionColumnSetting.ResponseHeaderKind => new(
                setting, header, "Custom response headers",
                row => Header(row.Session.Response, setting.Source, MaskSensitive(setting.Source))),
            SessionColumnSetting.SessionFlagKind => new(
                setting, header, "Custom session flags",
                row => Value(Metadata(row.Session, setting.Source))),
            _ => null
        };
    }

    public static SessionColumnSetting CreateCustom(string kind, string source, string? header = null) =>
        new($"custom-{Guid.NewGuid():N}", kind, source.Trim(), string.IsNullOrWhiteSpace(header) ? source.Trim() : header.Trim());

    public static int Compare(SessionColumnValue left, SessionColumnValue right)
    {
        if (left.IsBlank != right.IsBlank)
        {
            return left.IsBlank ? 1 : -1;
        }
        if (left.IsBlank)
        {
            return 0;
        }
        if (left.SortValue is not null && right.SortValue is not null
            && left.SortValue.GetType() == right.SortValue.GetType())
        {
            return left.SortValue.CompareTo(right.SortValue);
        }
        return string.Compare(left.Display, right.Display, StringComparison.OrdinalIgnoreCase);
    }

    private static SessionColumnDefinition Text(
        string id,
        string header,
        string category,
        Func<SessionRow, SessionColumnValue> read,
        bool numeric = false) =>
        new(SessionColumnSetting.BuiltIn(id), header, category, read, numeric);

    private static SessionColumnDefinition Timer(string id, string timer) =>
        Text(id, timer, "Timing", row => Timestamp(row.Session.Timers.GetValueOrDefault(timer)));

    private static SessionColumnDefinition DurationTimer(string id, string header, string timer, string flag) =>
        Text(id, header, "Timing", row => Duration(ParseMilliseconds(
            row.Session.Timers.GetValueOrDefault(timer) ?? row.Session.Metadata.GetValueOrDefault(flag))), numeric: true);

    private static SessionColumnValue Value(string? value) =>
        string.IsNullOrWhiteSpace(value) ? new("") : new(value, value);

    private static SessionColumnValue Integer(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? new(parsed.ToString("N0", CultureInfo.InvariantCulture), parsed)
            : Value(value);

    private static SessionColumnValue Integer(long value) =>
        new(value.ToString("N0", CultureInfo.InvariantCulture), value);

    private static SessionColumnValue Bytes(long? value) =>
        value is { } bytes && bytes >= 0 ? new(HtmlReportGenerator.FormatBytes(bytes), bytes) : new("");

    private static SessionColumnValue Duration(long? milliseconds) =>
        milliseconds is { } value ? new(SessionRow.FormatElapsed(value), value) : new("");

    private static SessionColumnValue Timestamp(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out var timestamp)
            ? new(SessionRow.FormatTime(timestamp), timestamp)
            : Value(value);

    private static SessionColumnValue Endpoint(string? value) =>
        string.IsNullOrWhiteSpace(value) ? new("") : new(value, new IpSortKey(value));

    private static SessionColumnValue UriPart(string? value, Func<Uri, string> select) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) ? Value(select(uri)) : new("");

    private static SessionColumnValue Header(HttpMessage? message, string name, bool mask = false)
    {
        if (message is null)
        {
            return new("");
        }
        var values = message.HeaderValues(name).ToArray();
        if (values.Length == 0)
        {
            return new("");
        }
        var joined = string.Join(", ", values);
        return Value(mask ? MaskHeaderValue(name, joined) : joined);
    }

    private static string? Metadata(HttpSession session, string name) =>
        session.Metadata.TryGetValue(name, out var value) ? value : null;

    private static string? Protocol(HttpSession session)
    {
        var start = session.Request?.StartLine ?? session.Response?.StartLine;
        return start?.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
    }

    private static SessionColumnValue Caching(HttpSession session)
    {
        var cacheControl = session.Response?.Header("Cache-Control");
        var expires = session.Response?.Header("Expires");
        return Value(string.Join("; ", new[] { cacheControl, expires is null ? null : $"Expires={expires}" }
            .Where(value => !string.IsNullOrWhiteSpace(value))));
    }

    private static SessionColumnValue BodySize(HttpMessage? message)
    {
        if (long.TryParse(message?.Header("Content-Length"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var length))
        {
            return Bytes(length);
        }
        return message?.IsBodyDecoded == true ? Bytes(message.Body.Length) : new("");
    }

    private static SessionColumnValue ResponseWireSize(HttpSession session)
    {
        var transfer = Metadata(session, "x-responsebodytransferlength");
        if (long.TryParse(transfer, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length))
        {
            return Bytes(length);
        }
        return long.TryParse(session.Response?.Header("Content-Length"), NumberStyles.Integer, CultureInfo.InvariantCulture, out length)
            ? Bytes(length)
            : new("");
    }

    private static SessionColumnValue DecodedBodySize(HttpMessage? message) =>
        message?.IsBodyDecoded == true ? Bytes(message.Body.Length) : new("");

    private static SessionColumnValue CompressionRatio(HttpSession session)
    {
        if (session.Response?.IsBodyDecoded != true)
        {
            return new("");
        }
        var wire = ResponseWireSize(session).SortValue as long?;
        var decoded = session.Response.Body.Length;
        if (wire is not > 0 || decoded <= 0)
        {
            return new("");
        }
        var ratio = (double)decoded / wire.Value;
        return new($"{ratio:0.00}\u00d7", ratio);
    }

    private static SessionColumnValue TimerDelta(HttpSession session, string startName, string endName)
    {
        if (!session.Timers.TryGetValue(startName, out var startValue)
            || !session.Timers.TryGetValue(endName, out var endValue)
            || !DateTimeOffset.TryParse(startValue, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out var start)
            || !DateTimeOffset.TryParse(endValue, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out var end)
            || end < start)
        {
            return new("");
        }
        return Duration((long)(end - start).TotalMilliseconds);
    }

    private static long? ParseMilliseconds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var trimmed = value.Trim();
        if (trimmed.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^2].Trim();
        }
        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? (long)Math.Round(number, MidpointRounding.AwayFromZero)
            : null;
    }

    private static string? ProcessPart(HttpSession session, bool wantPid)
    {
        var value = Metadata(session, "x-processinfo");
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var separator = value.LastIndexOf(':');
        if (separator < 0)
        {
            separator = value.LastIndexOf('(');
        }
        if (separator < 0)
        {
            return wantPid ? null : value;
        }
        var suffix = value[(separator + 1)..].Trim(' ', ')');
        return wantPid && long.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            ? suffix
            : wantPid ? null : value[..separator].Trim();
    }

    private static string? TlsInfo(HttpSession session)
    {
        string[] keys =
        [
            "https-protocol", "https-cipher", "https-client-sessionid", "https-client-certificate",
            "https-client-cert", "https-server-certificate", "https-server-cert"
        ];
        var parts = keys.Select(key => Metadata(session, key))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return string.Join("; ", parts);
    }

    private static string? MapiSummary(MapiSession? mapi)
    {
        if (mapi?.Request?.Root is not { } root)
        {
            return mapi?.RequestType;
        }
        var operations = new List<string>();
        var pending = new Stack<MapiNode>();
        pending.Push(root);
        while (pending.Count > 0 && operations.Count < 8)
        {
            var node = pending.Pop();
            if (node.Kind == MapiNodeKind.Operation)
            {
                operations.Add(node.Name);
            }
            for (var index = node.Children.Length - 1; index >= 0; index--)
            {
                pending.Push(node.Children[index]);
            }
        }
        return operations.Count == 0 ? mapi.RequestType : string.Join(", ", operations.Distinct(StringComparer.Ordinal));
    }

    private static string? AuthScheme(HttpSession session)
    {
        string?[] values =
        [
            session.Request?.Header("Authorization"),
            session.Request?.Header("Proxy-Authorization"),
            session.Response?.Header("WWW-Authenticate"),
            session.Response?.Header("Proxy-Authenticate")
        ];
        return string.Join(", ", values.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value =>
            {
                var scheme = value!.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0];
                return scheme.StartsWith("[REDACTED:", StringComparison.Ordinal) ? null : scheme;
            })
            .Where(scheme => scheme is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static bool MaskSensitive(string name) =>
        name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase);

    private static string MaskHeaderValue(string name, string value)
    {
        if (value == "******" || value.Contains("[REDACTED:", StringComparison.Ordinal))
        {
            return value;
        }
        if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase))
        {
            var scheme = value.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return string.IsNullOrWhiteSpace(scheme) ? "[hidden]" : $"{scheme} [hidden]";
        }
        return "[hidden]";
    }

    private static long NumericId(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : long.MaxValue;

    private sealed class IpSortKey : IComparable
    {
        private readonly byte[] address;
        private readonly int port;

        public IpSortKey(string value)
        {
            address = Parse(value, out port);
        }

        public int CompareTo(object? obj)
        {
            if (obj is not IpSortKey other)
            {
                return 1;
            }
            var length = address.Length.CompareTo(other.address.Length);
            if (length != 0)
            {
                return length;
            }
            for (var index = 0; index < address.Length; index++)
            {
                var compared = address[index].CompareTo(other.address[index]);
                if (compared != 0)
                {
                    return compared;
                }
            }
            return port.CompareTo(other.port);
        }

        private static byte[] Parse(string value, out int port)
        {
            port = 0;
            var candidate = value.Trim();
            if (candidate.StartsWith('['))
            {
                var close = candidate.IndexOf(']');
                if (close > 0)
                {
                    _ = int.TryParse(candidate[(close + 1)..].TrimStart(':'), out port);
                    candidate = candidate[1..close];
                }
            }
            else
            {
                var colon = candidate.LastIndexOf(':');
                if (colon > 0 && candidate.Count(character => character == ':') == 1)
                {
                    _ = int.TryParse(candidate[(colon + 1)..], out port);
                    candidate = candidate[..colon];
                }
            }
            return IPAddress.TryParse(candidate, out var ip) ? ip.GetAddressBytes() : [];
        }
    }
}
