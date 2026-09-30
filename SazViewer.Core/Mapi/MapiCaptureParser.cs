using System.Collections.Immutable;

namespace SazViewer.Core;

internal static class MapiCaptureParser
{
    private static readonly HashSet<string> NspiOperations = new(StringComparer.OrdinalIgnoreCase)
    {
        "Bind", "Unbind", "CompareMIds", "DNToMId", "GetMatches", "GetPropList",
        "GetProps", "GetSpecialTable", "GetTemplateInfo", "ModLinkAtt", "ModProps",
        "QueryRows", "QueryColumns", "ResolveNames", "ResortRestriction",
        "SeekEntries", "UpdateStat"
    };

    private static readonly string[] MailboxOperations =
    [
        "Connect", "Execute", "Disconnect", "NotificationWait",
        "GetMailboxUrl", "GetAddressBookUrl"
    ];

    public static MapiCapture Parse(
        IReadOnlyList<HttpSession> sessions,
        CancellationToken cancellationToken = default)
    {
        var context = new MapiCaptureContext();
        var parsed = ImmutableArray.CreateBuilder<MapiSession>();
        for (var index = 0; index < sessions.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var session = sessions[index];
            var requestType = session.Request?.Header("X-RequestType")?.Trim();
            var requestContentType = session.Request?.Header("Content-Type");
            var responseCode = session.Response?.Header("X-ResponseCode")?.Trim();
            var responseContentType = session.Response?.Header("Content-Type");
            var requestCandidate = IsMapiContentType(requestContentType);
            var responseCandidate = responseCode is not null
                && (responseCode != "0" || IsMapiContentType(responseContentType));
            if (!requestCandidate && !responseCandidate && string.IsNullOrWhiteSpace(requestType))
            {
                continue;
            }

            var warnings = new List<string>();
            if (string.IsNullOrWhiteSpace(requestType))
            {
                requestType = "Unknown";
                warnings.Add("MAPI/HTTP content was detected without an X-RequestType header.");
            }
            var endpoint = NspiOperations.Contains(requestType)
                || requestType.Equals("GetAddressBookUrl", StringComparison.OrdinalIgnoreCase)
                    ? MapiEndpoint.AddressBook
                    : MailboxOperations.Contains(requestType, StringComparer.OrdinalIgnoreCase)
                        ? MapiEndpoint.Mailbox
                        : MapiEndpoint.Unknown;
            var protocolError = responseCode is not null && responseCode != "0";
            context.RegisterLogonCorrelationScope(session.Id, BuildLogonCorrelationScope(session));

            var request = ParseMessage(
                session.Request,
                requestType,
                MapiDirection.Request,
                protocolError: false,
                context,
                warnings,
                session.Id,
                cancellationToken);
            var response = ParseMessage(
                session.Response,
                requestType,
                MapiDirection.Response,
                protocolError,
                context,
                warnings,
                session.Id,
                cancellationToken);
            context.CompleteHttpSession(session.Id);
            var result = new MapiSession(
                session.Id,
                index,
                endpoint,
                requestType,
                responseCode,
                protocolError,
                request,
                response,
                warnings.ToImmutableArray());
            session.Mapi = result;
            foreach (var warning in warnings)
            {
                session.Warnings.Add($"MAPI/HTTP: {warning}");
            }
            parsed.Add(result);
        }

        var resultSessions = parsed.ToImmutable();
        var supportedRequestTypes = MailboxOperations
            .Concat(NspiOperations)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
        var coverage = new MapiCoverage(
            resultSessions.Length,
            resultSessions.Count(item => item.Request?.Complete == true),
            resultSessions.Count(item => item.Response?.Complete == true),
            supportedRequestTypes,
            NspiOperations.Order(StringComparer.Ordinal).ToImmutableArray(),
            [
                "Envelope", "NSPI properties/restrictions", "Rule actions", "Auxiliary payloads", "Extended buffers", "ROP framing and fixed schemas",
                "Folder/table operations (MS-OXCFOLD/MS-OXCTABL)",
                "Property/stream/store operations (MS-OXCPRPT/MS-OXCSTOR)",
                "Message/rule/permission/notification operations (MS-OXCMSG/MS-OXORULE/MS-OXCPERM/MS-OXCNOTIF)",
                "Bulk data transfer / incremental change synchronization (MS-OXCFXICS)",
                "Capture-local table columns and row/notification reconstruction",
                "Cross-HTTP FastTransfer stream reconstruction by logical MAPI connection and server object handle",
                "FastTransfer typed syntactical markers plus ProgressInformation and PropertyGroupInfo structures",
                "Provenance-bound contentsSync, hierarchySync, state, messageList, and recursive topFolder production validation"
            ],
            [
                "RopReadRecipients response RecipientRow property internals remain bounded raw because that response carries no RecipientColumns array and the protocol does not identify a deterministic external column source",
                "In the property/stream/store family's generic property-value arrays (RopGetPropertiesSpecific/RopSetProperties and similar), PtypRestriction/PtypRuleAction values and any property type outside the fixed MS-OXCDATA 2.11.1 table are intentionally refused rather than guessed; the message/rule/permission family's own RuleData/PermissionData arrays and the folder/table family's own restrictions do fully decode both types with an explicit ROP-buffer-vs-extended-rule width boundary",
                "FastTransfer CopyTo/CopyProperties cannot yet select folderContent/messageContent/attachmentContent roots because their input server-object type is not yet reconstructed"
            ]);
        return new MapiCapture(
            resultSessions,
            resultSessions.ToImmutableDictionary(item => item.HttpSessionId, StringComparer.OrdinalIgnoreCase),
            coverage);
    }

    private static string BuildLogonCorrelationScope(HttpSession session)
    {
        var request = session.Request;
        var requestPath = session.Url ?? request?.StartLine ?? session.Id;
        session.Metadata.TryGetValue("x-processinfo", out var localProcess);
        return string.Join(
            '\u001F',
            requestPath.Trim(),
            localProcess?.Trim() ?? string.Empty,
            request?.Header("X-ClientInfo")?.Trim() ?? string.Empty,
            request?.Header("X-ClientApplication")?.Trim() ?? string.Empty);
    }

    private static MapiMessageParse? ParseMessage(
        HttpMessage? message,
        string requestType,
        MapiDirection direction,
        bool protocolError,
        MapiCaptureContext context,
        List<string> sessionWarnings,
        string captureScope,
        CancellationToken cancellationToken)
    {
        if (message is null)
        {
            return null;
        }
        if (protocolError && direction == MapiDirection.Response)
        {
            var bytes = message.Body.NormalizedBytes.Span;
            var budget = new MapiNodeBudget();
            var raw = ExtendedBufferParser.RawNode("Protocol error body", bytes, 0, budget);
            return new MapiMessageParse(direction, raw, [], false, bytes.Length, bytes.Length);
        }
        if (message.Body.NormalizedBytes.IsEmpty && message.Body.CapturedLength != 0)
        {
            var warning = $"{direction} entity bytes were unavailable because the body was incomplete, exceeded {MapiParseLimits.MaxPayloadBytes:N0} bytes, or HTTP decoding failed.";
            sessionWarnings.Add(warning);
            return null;
        }

        var localWarnings = new List<string>();
        var bytesToParse = message.Body.NormalizedBytes.Span;
        var budgetForMessage = new MapiNodeBudget();
        try
        {
            var root = MapiHttpMessageParser.Parse(
                bytesToParse,
                requestType,
                direction,
                context,
                localWarnings,
                budgetForMessage,
                cancellationToken,
                out var parsedBytes,
                captureScope);
            sessionWarnings.AddRange(localWarnings);
            var semanticComplete = localWarnings.Count == 0 && !ContainsRaw(root);
            return new MapiMessageParse(
                direction,
                root,
                localWarnings.ToImmutableArray(),
                parsedBytes == bytesToParse.Length && semanticComplete,
                parsedBytes,
                bytesToParse.Length);
        }
        catch (Exception exception) when (exception is MapiParseException or OverflowException)
        {
            var warning = $"{direction} parsing stopped safely: {exception.Message}";
            localWarnings.Add(warning);
            sessionWarnings.Add(warning);
            var raw = ExtendedBufferParser.RawNode(
                "Undecoded MAPI/HTTP entity",
                bytesToParse,
                0,
                new MapiNodeBudget());
            return new MapiMessageParse(
                direction,
                raw,
                localWarnings.ToImmutableArray(),
                false,
                0,
                bytesToParse.Length);
        }
    }

    private static bool IsMapiContentType(string? value) =>
        value?.Split(';', 2)[0].Trim()
            .Equals("application/mapi-http", StringComparison.OrdinalIgnoreCase) == true;

    private static bool ContainsRaw(MapiNode node) =>
        node.Kind == MapiNodeKind.Raw || node.Children.Any(ContainsRaw);
}
