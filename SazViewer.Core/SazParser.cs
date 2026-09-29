using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace SazViewer.Core;

public sealed partial class SazParser
{
    private const int MaxMetadataBytes = 1024 * 1024;

    public SazReport Parse(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"SAZ archive was not found: {fullPath}", fullPath);
        }

        var report = new SazReport { SourceName = Path.GetFileName(fullPath) };
        try
        {
            using var file = File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false);
            ParseArchive(archive, report);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException(
                $"'{fullPath}' is not a readable ZIP/SAZ archive: {exception.Message}",
                exception);
        }

        return report;
    }

    public SazReport Parse(Stream stream, string sourceName = "capture.saz")
    {
        var report = new SazReport { SourceName = sourceName };
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        ParseArchive(archive, report);
        return report;
    }

    private static void ParseArchive(ZipArchive archive, SazReport report)
    {
        var groups = Discover(archive, report.Warnings);
        foreach (var group in groups.OrderBy(g => g.ArchiveOrder))
        {
            var session = new HttpSession { Id = group.Id, ArchiveOrder = group.ArchiveOrder };
            ParseMetadata(group.Metadata, session);
            ParseRequest(group.Request, session);
            ParseResponse(group.Response, session);
            CompleteSession(session);
            report.Sessions.Add(session);

            if (group.WebSocket is not null)
            {
                WebSocketParser.Parse(
                    group.WebSocket,
                    session.Id,
                    session.ArchiveOrder,
                    report.WebSocketMessages,
                    session.Warnings);
            }

            foreach (var warning in session.Warnings)
            {
                report.Warnings.Add($"Session {session.Id}: {warning}");
            }
        }

        report.Sessions.Sort(CompareSessions);
        report.WebSocketMessages.Sort(CompareWebSocketMessages);
        if (groups.Count == 0)
        {
            report.Warnings.Add("No raw/<id>_c.txt, _s.txt, _m.xml, or _w.txt entries were found.");
        }
    }

    private static List<EntryGroup> Discover(ZipArchive archive, List<string> warnings)
    {
        var groups = new Dictionary<string, EntryGroup>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < archive.Entries.Count; index++)
        {
            var entry = archive.Entries[index];
            var normalized = entry.FullName.Replace('\\', '/');
            var match = RawEntryPattern().Match(normalized);
            if (!match.Success)
            {
                continue;
            }

            var id = match.Groups["id"].Value;
            var kind = match.Groups["kind"].Value.ToLowerInvariant();
            var extension = match.Groups["ext"].Value.ToLowerInvariant();
            if ((kind == "m") != (extension == "xml"))
            {
                warnings.Add($"Ignored unexpected raw entry '{entry.FullName}'.");
                continue;
            }

            if (!groups.TryGetValue(id, out var group))
            {
                group = new EntryGroup(id, index);
                groups.Add(id, group);
            }

            ref var slot = ref group.Entry(kind);
            if (slot is not null)
            {
                warnings.Add($"Duplicate '{kind}' entry for session {id}; using '{slot.FullName}'.");
                continue;
            }

            slot = entry;
        }

        return groups.Values.ToList();
    }

    private static void ParseRequest(ZipArchiveEntry? entry, HttpSession session)
    {
        if (entry is null)
        {
            session.Warnings.Add("request entry is missing.");
            return;
        }

        session.RequestBytes = entry.Length;
        try
        {
            using var stream = entry.Open();
            session.Request = HttpMessageParser.Parse(stream, entry.Length, "request", session.Warnings);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            session.Warnings.Add($"request could not be read: {exception.Message}");
        }
    }

    private static void ParseResponse(ZipArchiveEntry? entry, HttpSession session)
    {
        if (entry is null)
        {
            session.Warnings.Add("response entry is missing.");
            return;
        }

        session.ResponseBytes = entry.Length;
        try
        {
            using var stream = entry.Open();
            session.Response = HttpMessageParser.Parse(stream, entry.Length, "response", session.Warnings);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            session.Warnings.Add($"response could not be read: {exception.Message}");
        }
    }

    private static void ParseMetadata(ZipArchiveEntry? entry, HttpSession session)
    {
        if (entry is null)
        {
            session.Warnings.Add("metadata entry is missing.");
            return;
        }

        if (entry.Length > MaxMetadataBytes)
        {
            session.Warnings.Add($"metadata entry exceeds {MaxMetadataBytes:N0} bytes and was skipped.");
            return;
        }

        try
        {
            using var stream = entry.Open();
            using var limited = ReadLimited(stream, MaxMetadataBytes + 1);
            if (limited.Length > MaxMetadataBytes)
            {
                session.Warnings.Add($"metadata expanded beyond {MaxMetadataBytes:N0} bytes and was skipped.");
                return;
            }
            limited.Position = 0;
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxMetadataBytes
            };
            using var reader = XmlReader.Create(limited, settings);
            var document = XDocument.Load(reader, LoadOptions.None);

            foreach (var element in document.Descendants())
            {
                if (element.Name.LocalName.Equals("SessionTimers", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var attribute in element.Attributes())
                    {
                        session.Timers[attribute.Name.LocalName] = attribute.Value;
                    }
                }

                else if (element.Name.LocalName.Equals("SessionFlag", StringComparison.OrdinalIgnoreCase))
                {
                    var name = Attribute(element, "N") ?? Attribute(element, "Name");
                    var value = Attribute(element, "V") ?? Attribute(element, "Value");
                    if (!string.IsNullOrWhiteSpace(name) && value is not null)
                    {
                        session.Metadata[name] = value;
                    }
                }
                else if (element.Name.LocalName.Equals("PipeInfo", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var attribute in element.Attributes())
                    {
                        session.Metadata[$"PipeInfo.{attribute.Name.LocalName}"] = attribute.Value;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is XmlException or InvalidDataException or IOException)
        {
            session.Warnings.Add($"metadata could not be parsed: {exception.Message}");
        }
    }

    private static MemoryStream ReadLimited(Stream stream, int limit)
    {
        var output = new MemoryStream(Math.Min(limit, 64 * 1024));
        var buffer = new byte[16 * 1024];
        while (output.Length < limit)
        {
            var wanted = (int)Math.Min(buffer.Length, limit - output.Length);
            var read = stream.Read(buffer, 0, wanted);
            if (read == 0)
            {
                break;
            }
            output.Write(buffer, 0, read);
        }
        return output;
    }

    private static void CompleteSession(HttpSession session)
    {
        if (session.Request is not null)
        {
            var parts = session.Request.StartLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                session.Method = parts[0];
                session.Url = BuildUrl(parts[0], parts[1], session.Request.Header("Host"), session.Metadata);
            }
            else
            {
                session.Warnings.Add("request start line is malformed.");
            }
        }

        if (session.Response is not null)
        {
            var parts = session.Response.StartLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var status))
            {
                session.StatusCode = status;
                session.StatusText = parts.Length == 3 ? parts[2] : null;
            }
            else
            {
                session.Warnings.Add("response status line is malformed.");
            }

            session.ContentType = session.Response.Header("Content-Type");
        }

        session.ClientEndpoint = Endpoint(session.Metadata, "x-clientip", "x-clientport")
            ?? Value(session.Metadata, "x-clientip");
        session.ServerEndpoint = ServerEndpoint(session.Metadata, session.Url);
        session.Timestamp = BestTimestamp(session.Timers, session.Metadata);
    }

    private static string BuildUrl(
        string method,
        string target,
        string? host,
        IReadOnlyDictionary<string, string> metadata)
    {
        if (Uri.TryCreate(target, UriKind.Absolute, out _))
        {
            return target;
        }

        if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            return "https://" + target;
        }

        var scheme = Value(metadata, "x-securepipe") is not null
            || Value(metadata, "x-https") is not null
            ? "https"
            : "http";
        return string.IsNullOrWhiteSpace(host) ? target : $"{scheme}://{host}{(target.StartsWith('/') ? "" : "/")}{target}";
    }

    private static DateTimeOffset? BestTimestamp(
        IReadOnlyDictionary<string, string> timers,
        IReadOnlyDictionary<string, string> metadata)
    {
        string[] priority =
        [
            "ClientBeginRequest",
            "ClientConnected",
            "FiddlerBeginRequest",
            "ServerGotRequest",
            "ServerConnected",
            "ClientDoneRequest"
        ];
        foreach (var key in priority)
        {
            if (timers.TryGetValue(key, out var value) && TryParseTimestamp(value, out var timestamp))
            {
                return timestamp;
            }
        }

        foreach (var key in new[] { "ui-comments-timestamp", "x-timestamp" })
        {
            var value = Value(metadata, key);
            if (value is not null && TryParseTimestamp(value, out var timestamp))
            {
                return timestamp;
            }
        }

        return null;
    }

    internal static bool TryParseTimestamp(string value, out DateTimeOffset timestamp)
    {
        if (DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
            out timestamp))
        {
            return true;
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
        {
            try
            {
                timestamp = ticks > 62_135_596_800_000_0000L
                    ? new DateTimeOffset(ticks, TimeSpan.Zero)
                    : DateTimeOffset.FromUnixTimeMilliseconds(ticks);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
            }
        }

        timestamp = default;
        return false;
    }

    private static int CompareSessions(HttpSession left, HttpSession right)
    {
        if (left.Timestamp.HasValue && right.Timestamp.HasValue)
        {
            var result = left.Timestamp.Value.CompareTo(right.Timestamp.Value);
            return result != 0 ? result : left.ArchiveOrder.CompareTo(right.ArchiveOrder);
        }

        if (left.Timestamp.HasValue)
        {
            return -1;
        }

        if (right.Timestamp.HasValue)
        {
            return 1;
        }

        var numeric = CompareIds(left.Id, right.Id);
        return numeric != 0 ? numeric : left.ArchiveOrder.CompareTo(right.ArchiveOrder);
    }

    private static int CompareWebSocketMessages(WebSocketMessage left, WebSocketMessage right)
    {
        if (left.Timestamp.HasValue && right.Timestamp.HasValue)
        {
            var result = left.Timestamp.Value.CompareTo(right.Timestamp.Value);
            if (result != 0)
            {
                return result;
            }
        }
        else if (left.Timestamp.HasValue)
        {
            return -1;
        }
        else if (right.Timestamp.HasValue)
        {
            return 1;
        }

        return left.SourceOrder.CompareTo(right.SourceOrder);
    }

    private static int CompareIds(string left, string right) =>
        long.TryParse(left, out var leftNumber) && long.TryParse(right, out var rightNumber)
            ? leftNumber.CompareTo(rightNumber)
            : StringComparer.OrdinalIgnoreCase.Compare(left, right);

    private static string? Endpoint(
        IReadOnlyDictionary<string, string> values,
        string addressKey,
        string portKey)
    {
        var address = Value(values, addressKey);
        var port = Value(values, portKey);
        return address is null ? null : port is null ? address : FormatEndpoint(address, port);
    }

    private static string? ServerEndpoint(
        IReadOnlyDictionary<string, string> values,
        string? url)
    {
        var address = Value(values, "x-hostip");
        if (address is null)
        {
            return null;
        }

        var port = Value(values, "x-serverport");
        if (port is null
            && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Port > 0)
        {
            port = uri.Port.ToString(CultureInfo.InvariantCulture);
        }

        return port is null ? address : FormatEndpoint(address, port);
    }

    private static string FormatEndpoint(string address, string port) =>
        address.Contains(':') && !address.StartsWith('[')
            ? $"[{address}]:{port}"
            : $"{address}:{port}";

    private static string? Value(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static string? Attribute(XElement element, string name) =>
        element.Attributes().FirstOrDefault(
            attribute => attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    [GeneratedRegex(
        @"^raw/(?<id>[^/]+)_(?<kind>[csmw])\.(?<ext>txt|xml)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RawEntryPattern();

    private sealed class EntryGroup(string id, int archiveOrder)
    {
        public string Id { get; } = id;
        public int ArchiveOrder { get; } = archiveOrder;
        public ZipArchiveEntry? Request;
        public ZipArchiveEntry? Response;
        public ZipArchiveEntry? Metadata;
        public ZipArchiveEntry? WebSocket;

        public ref ZipArchiveEntry? Entry(string kind)
        {
            if (kind == "c") return ref Request;
            if (kind == "s") return ref Response;
            if (kind == "m") return ref Metadata;
            return ref WebSocket;
        }
    }
}
