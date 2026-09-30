using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace SazViewer.Core;

internal static class MapiHttpMessageParser
{
    public static MapiNode Parse(
        ReadOnlySpan<byte> bytes,
        string requestType,
        MapiDirection direction,
        MapiCaptureContext context,
        List<string> warnings,
        MapiNodeBudget budget,
        CancellationToken cancellationToken,
        out long parsedBytes,
        string? captureScope = null)
    {
        var reader = new MapiReader(bytes, cancellationToken);
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var fastTransferAssembler = context.FastTransferAssembler;
        if (direction == MapiDirection.Response)
        {
            ParseResponsePrefix(ref reader, children, warnings, budget);
            var status = ParseStatusCode(children);
            if (status != 0)
            {
                ParseAuxiliarySuffix(ref reader, children, warnings, budget, cancellationToken);
                parsedBytes = reader.Position;
                budget.Claim(0);
                return new MapiNode(requestType, MapiNodeKind.Operation, 0, bytes.Length, "Response", children.ToImmutable());
            }
            ReadUInt32(ref reader, children, "ErrorCode", budget, hex: true);
            ParseResponseOperation(ref reader, requestType, children, warnings, budget, cancellationToken, fastTransferAssembler, captureScope, context);
        }
        else
        {
            ParseRequestOperation(ref reader, requestType, children, warnings, budget, cancellationToken, fastTransferAssembler, captureScope, context);
        }

        if (!reader.End)
        {
            warnings.Add($"{requestType} {direction.ToString().ToLowerInvariant()} left {reader.Remaining:N0} unparsed bytes.");
            children.Add(ExtendedBufferParser.RawNode(
                "Unparsed operation data",
                reader.ReadRemaining("unparsed operation data"),
                reader.Position,
                budget));
        }
        parsedBytes = reader.Position;
        budget.Claim(0);
        return new MapiNode(
            requestType,
            MapiNodeKind.Operation,
            0,
            bytes.Length,
            direction.ToString(),
            children.ToImmutable());
    }

    private static void ParseRequestOperation(
        ref MapiReader reader,
        string requestType,
        ImmutableArray<MapiNode>.Builder nodes,
        List<string> warnings,
        MapiNodeBudget budget,
        CancellationToken cancellationToken,
        FastTransferStreamAssembler? fastTransferAssembler = null,
        string? captureScope = null,
        MapiCaptureContext? context = null)
    {
        switch (requestType.ToUpperInvariant())
        {
            case "CONNECT":
                ReadAsciiZ(ref reader, nodes, "UserDn", budget);
                ReadUInt32(ref reader, nodes, "Flags", budget, true);
                ReadUInt32(ref reader, nodes, "DefaultCodePage", budget);
                ReadUInt32(ref reader, nodes, "LcidSort", budget, true);
                ReadUInt32(ref reader, nodes, "LcidString", budget, true);
                ParseAuxiliarySuffix(ref reader, nodes, warnings, budget, cancellationToken);
                break;
            case "EXECUTE":
                ReadUInt32(ref reader, nodes, "Flags", budget, true);
                var ropSize = ReadSize32(ref reader, nodes, "RopBufferSize", budget);
                ParseExtended(
                    ref reader, nodes, ropSize, "RopInputBuffer", true, MapiDirection.Request, warnings, budget, cancellationToken,
                    fastTransferAssembler, captureScope, context);
                ReadUInt32(ref reader, nodes, "MaxRopOut", budget);
                ParseAuxiliarySuffix(ref reader, nodes, warnings, budget, cancellationToken);
                break;
            case "DISCONNECT":
                ParseAuxiliarySuffix(ref reader, nodes, warnings, budget, cancellationToken);
                break;
            case "NOTIFICATIONWAIT":
                ReadUInt32(ref reader, nodes, "Flags", budget, true);
                ParseAuxiliarySuffix(ref reader, nodes, warnings, budget, cancellationToken);
                break;
            case "GETMAILBOXURL":
                ReadUInt32(ref reader, nodes, "Flags", budget, true);
                ReadUnicodeZ(ref reader, nodes, "ServerDn", budget);
                ParseAuxiliarySuffix(ref reader, nodes, warnings, budget, cancellationToken);
                break;
            case "GETADDRESSBOOKURL":
                ReadUInt32(ref reader, nodes, "Flags", budget, true);
                ReadUnicodeZ(ref reader, nodes, "UserDn", budget);
                ParseAuxiliarySuffix(ref reader, nodes, warnings, budget, cancellationToken);
                break;
            case "BIND":
                ReadUInt32(ref reader, nodes, "Flags", budget, true);
                if (ReadBoolean(ref reader, nodes, "HasState", budget)) ParseStat(ref reader, nodes, budget);
                ParseAuxiliarySuffix(ref reader, nodes, warnings, budget, cancellationToken);
                break;
            case "UNBIND":
                ReadUInt32(ref reader, nodes, "Reserved", budget, true);
                ParseAuxiliarySuffix(ref reader, nodes, warnings, budget, cancellationToken);
                break;
            case "COMPAREMIDS":
                ReadUInt32(ref reader, nodes, "Reserved", budget, true);
                if (ReadBoolean(ref reader, nodes, "HasState", budget)) ParseStat(ref reader, nodes, budget);
                ReadUInt32(ref reader, nodes, "MinimalId1", budget, true);
                ReadUInt32(ref reader, nodes, "MinimalId2", budget, true);
                ParseAuxiliarySuffix(ref reader, nodes, warnings, budget, cancellationToken);
                break;
            case "DNTOMID":
                ReadUInt32(ref reader, nodes, "Reserved", budget, true);
                if (ReadBoolean(ref reader, nodes, "HasNames", budget))
                {
                    var count = ReadCount32(ref reader, nodes, "NameCount", budget);
                    for (var index = 0; index < count; index++) ReadAsciiZ(ref reader, nodes, $"Name[{index}]", budget);
                }
                ParseAuxiliarySuffix(ref reader, nodes, warnings, budget, cancellationToken);
                break;
            case "GETPROPLIST":
                ReadUInt32(ref reader, nodes, "Flags", budget, true);
                ReadUInt32(ref reader, nodes, "MinimalId", budget, true);
                ReadUInt32(ref reader, nodes, "CodePage", budget);
                ParseAuxiliarySuffix(ref reader, nodes, warnings, budget, cancellationToken);
                break;
            case "GETPROPS":
                ReadUInt32(ref reader, nodes, "Flags", budget, true);
                if (ReadBoolean(ref reader, nodes, "HasState", budget)) ParseStat(ref reader, nodes, budget);
                if (ReadBoolean(ref reader, nodes, "HasPropertyTags", budget)) ParsePropertyTagArray(ref reader, nodes, budget);
                ParseAuxiliarySuffix(ref reader, nodes, warnings, budget, cancellationToken);
                break;
            case "GETMATCHES":
            {
                ReadUInt32(ref reader, nodes, "Reserved", budget, true);
                uint? codePage = null;
                if (ReadBoolean(ref reader, nodes, "HasState", budget)) codePage = ParseStat(ref reader, nodes, budget);
                if (ReadBoolean(ref reader, nodes, "HasMinimalIds", budget)) ParseMinimalEntryIdArray(ref reader, nodes, "MinimalIds", budget);
                ReadUInt32(ref reader, nodes, "InterfaceOptionFlags", budget, true);
                if (ReadBoolean(ref reader, nodes, "HasFilter", budget))
                {
                    nodes.Add(NspiRestrictionParser.Parse(ref reader, budget, codePage: codePage, warnings: warnings));
                }
                if (ReadBoolean(ref reader, nodes, "HasPropertyName", budget))
                {
                    ReadGuid(ref reader, nodes, "PropertyNameGuid", budget);
                    ReadUInt32(ref reader, nodes, "PropertyNameId", budget, true);
                }
                ReadUInt32(ref reader, nodes, "RowCount", budget);
                if (ReadBoolean(ref reader, nodes, "HasColumns", budget)) ParsePropertyTagArray(ref reader, nodes, budget);
                ParseAuxiliarySuffix(ref reader, nodes, warnings, budget, cancellationToken);
                break;
            }
            case "GETSPECIALTABLE":
                ReadUInt32(ref reader, nodes, "Flags", budget, true);
                if (ReadBoolean(ref reader, nodes, "HasState", budget)) ParseStat(ref reader, nodes, budget);
                if (ReadBoolean(ref reader, nodes, "HasVersion", budget)) ReadUInt32(ref reader, nodes, "Version", budget);
                ParseAuxiliarySuffix(ref reader, nodes, warnings, budget, cancellationToken);
                break;
            case "GETTEMPLATEINFO":
                ReadUInt32(ref reader, nodes, "Flags", budget, true);
                ReadUInt32(ref reader, nodes, "DisplayType", budget, true);
                if (ReadBoolean(ref reader, nodes, "HasTemplateDn", budget)) ReadAsciiZ(ref reader, nodes, "TemplateDn", budget);
                ReadUInt32(ref reader, nodes, "CodePage", budget);
                ReadUInt32(ref reader, nodes, "LocaleId", budget, true);
                ParseAuxiliarySuffix(ref reader, nodes, warnings, budget, cancellationToken);
                break;
            case "QUERYCOLUMNS":
                ReadUInt32(ref reader, nodes, "Reserved", budget, true);
                ReadUInt32(ref reader, nodes, "MapiFlags", budget, true);
                ParseAuxiliarySuffix(ref reader, nodes, warnings, budget, cancellationToken);
                break;
            case "QUERYROWS":
                ReadUInt32(ref reader, nodes, "Flags", budget, true);
                if (ReadBoolean(ref reader, nodes, "HasState", budget)) ParseStat(ref reader, nodes, budget);
                ParseMinimalEntryIdArray(ref reader, nodes, "ExplicitTable", budget);
                ReadUInt32(ref reader, nodes, "RowCount", budget);
                if (ReadBoolean(ref reader, nodes, "HasColumns", budget)) ParsePropertyTagArray(ref reader, nodes, budget);
                ParseAuxiliarySuffix(ref reader, nodes, warnings, budget, cancellationToken);
                break;
            case "MODLINKATT":
                ReadUInt32(ref reader, nodes, "Flags", budget, true);
                ReadPropertyTag(ref reader, nodes, "PropertyTag", budget);
                ReadUInt32(ref reader, nodes, "MinimalId", budget, true);
                if (ReadBoolean(ref reader, nodes, "HasEntryIds", budget))
                {
                    var count = ReadCount32(ref reader, nodes, "EntryIdCount", budget);
                    for (var index = 0; index < count; index++)
                    {
                        ParseSizedEntryId(ref reader, nodes, index, budget);
                    }
                }
                ParseAuxiliarySuffix(ref reader, nodes, warnings, budget, cancellationToken);
                break;
            case "MODPROPS":
            {
                ReadUInt32(ref reader, nodes, "Reserved", budget, true);
                uint? codePage = null;
                if (ReadBoolean(ref reader, nodes, "HasState", budget)) codePage = ParseStat(ref reader, nodes, budget);
                if (ReadBoolean(ref reader, nodes, "HasPropertyTags", budget)) ParsePropertyTagArray(ref reader, nodes, budget);
                if (ReadBoolean(ref reader, nodes, "HasPropertyValues", budget))
                {
                    nodes.Add(NspiPropertyParser.ParseValueList(ref reader, "PropertyValues", budget, codePage: codePage, warnings: warnings));
                }
                ParseAuxiliarySuffix(ref reader, nodes, warnings, budget, cancellationToken);
                break;
            }
            case "RESOLVENAMES":
                ReadUInt32(ref reader, nodes, "Reserved", budget, true);
                if (ReadBoolean(ref reader, nodes, "HasState", budget)) ParseStat(ref reader, nodes, budget);
                if (ReadBoolean(ref reader, nodes, "HasPropertyTags", budget)) ParsePropertyTagArray(ref reader, nodes, budget);
                if (ReadBoolean(ref reader, nodes, "HasNames", budget))
                {
                    var count = ReadCount32(ref reader, nodes, "NameCount", budget);
                    for (var index = 0; index < count; index++) ReadUnicodeZ(ref reader, nodes, $"Name[{index}]", budget);
                }
                ParseAuxiliarySuffix(ref reader, nodes, warnings, budget, cancellationToken);
                break;
            case "SEEKENTRIES":
            {
                ReadUInt32(ref reader, nodes, "Reserved", budget, true);
                uint? codePage = null;
                if (ReadBoolean(ref reader, nodes, "HasState", budget)) codePage = ParseStat(ref reader, nodes, budget);
                if (ReadBoolean(ref reader, nodes, "HasTarget", budget))
                {
                    nodes.Add(NspiPropertyParser.ParseTaggedValue(ref reader, "Target", budget, codePage: codePage, warnings: warnings));
                }
                if (ReadBoolean(ref reader, nodes, "HasExplicitTable", budget))
                {
                    ParseMinimalEntryIdArray(ref reader, nodes, "ExplicitTable", budget);
                }
                if (ReadBoolean(ref reader, nodes, "HasColumns", budget)) ParsePropertyTagArray(ref reader, nodes, budget);
                ParseAuxiliarySuffix(ref reader, nodes, warnings, budget, cancellationToken);
                break;
            }
            case "RESORTRESTRICTION":
                ReadUInt32(ref reader, nodes, "Reserved", budget, true);
                if (ReadBoolean(ref reader, nodes, "HasState", budget)) ParseStat(ref reader, nodes, budget);
                if (ReadBoolean(ref reader, nodes, "HasMinimalIds", budget)) ParseMinimalEntryIdArray(ref reader, nodes, "MinimalIds", budget);
                ParseAuxiliarySuffix(ref reader, nodes, warnings, budget, cancellationToken);
                break;
            case "UPDATESTAT":
                ReadUInt32(ref reader, nodes, "Reserved", budget, true);
                if (ReadBoolean(ref reader, nodes, "HasState", budget)) ParseStat(ref reader, nodes, budget);
                ReadBoolean(ref reader, nodes, "DeltaRequested", budget);
                ParseAuxiliarySuffix(ref reader, nodes, warnings, budget, cancellationToken);
                break;
            default:
                warnings.Add($"{requestType} request fields are retained as raw because semantic decoding is not implemented.");
                break;
        }
    }

    private static void ParseResponseOperation(
        ref MapiReader reader,
        string requestType,
        ImmutableArray<MapiNode>.Builder nodes,
        List<string> warnings,
        MapiNodeBudget budget,
        CancellationToken cancellationToken,
        FastTransferStreamAssembler? fastTransferAssembler = null,
        string? captureScope = null,
        MapiCaptureContext? context = null)
    {
        switch (requestType.ToUpperInvariant())
        {
            case "CONNECT":
                ReadUInt32(ref reader, nodes, "PollsMax", budget);
                ReadUInt32(ref reader, nodes, "RetryCount", budget);
                ReadUInt32(ref reader, nodes, "RetryDelay", budget);
                ReadAsciiZ(ref reader, nodes, "DnPrefix", budget);
                ReadUnicodeZ(ref reader, nodes, "DisplayName", budget);
                ParseAuxiliarySuffix(ref reader, nodes, warnings, budget, cancellationToken);
                break;
            case "EXECUTE":
                ReadUInt32(ref reader, nodes, "Flags", budget, true);
                var ropSize = ReadSize32(ref reader, nodes, "RopBufferSize", budget);
                ParseExtended(
                    ref reader, nodes, ropSize, "RopOutputBuffer", true, MapiDirection.Response, warnings, budget, cancellationToken,
                    fastTransferAssembler, captureScope, context);
                if (!reader.End) ParseAuxiliarySuffix(ref reader, nodes, warnings, budget, cancellationToken);
                break;
            case "NOTIFICATIONWAIT":
                ReadUInt32(ref reader, nodes, "EventPending", budget);
                ParseAuxiliarySuffix(ref reader, nodes, warnings, budget, cancellationToken);
                break;
            case "GETMAILBOXURL":
            case "GETADDRESSBOOKURL":
                ReadUnicodeZ(ref reader, nodes, "ServerUrl", budget);
                ParseAuxiliarySuffix(ref reader, nodes, warnings, budget, cancellationToken);
                break;
            case "BIND":
                ReadGuid(ref reader, nodes, "ServerGuid", budget);
                ParseAuxiliarySuffix(ref reader, nodes, warnings, budget, cancellationToken);
                break;
            case "COMPAREMIDS":
                ReadInt32(ref reader, nodes, "Result", budget);
                ParseAuxiliarySuffix(ref reader, nodes, warnings, budget, cancellationToken);
                break;
            case "DNTOMID":
                if (ReadBoolean(ref reader, nodes, "HasMinimalIds", budget))
                {
                    ParseMinimalEntryIdArray(ref reader, nodes, "MinimalIds", budget);
                }
                ParseAuxiliarySuffix(ref reader, nodes, warnings, budget, cancellationToken);
                break;
            case "GETPROPLIST":
            case "QUERYCOLUMNS":
                if (ReadBoolean(ref reader, nodes, "HasPropertyTags", budget))
                {
                    ParsePropertyTagArray(ref reader, nodes, budget);
                }
                ParseAuxiliarySuffix(ref reader, nodes, warnings, budget, cancellationToken);
                break;
            case "GETPROPS":
            {
                var codePage = ReadUInt32(ref reader, nodes, "CodePage", budget);
                if (ReadBoolean(ref reader, nodes, "HasPropertyValues", budget))
                {
                    nodes.Add(NspiPropertyParser.ParseValueList(ref reader, "PropertyValues", budget, codePage: codePage, warnings: warnings));
                }
                ParseAuxiliarySuffix(ref reader, nodes, warnings, budget, cancellationToken);
                break;
            }
            case "GETSPECIALTABLE":
            {
                var codePage = ReadUInt32(ref reader, nodes, "CodePage", budget);
                if (ReadBoolean(ref reader, nodes, "HasVersion", budget)) ReadUInt32(ref reader, nodes, "Version", budget);
                if (ReadBoolean(ref reader, nodes, "HasRows", budget))
                {
                    ParsePropertyValueListArray(ref reader, nodes, "Rows", budget, codePage, warnings);
                }
                ParseAuxiliarySuffix(ref reader, nodes, warnings, budget, cancellationToken);
                break;
            }
            case "GETTEMPLATEINFO":
            {
                var codePage = ReadUInt32(ref reader, nodes, "CodePage", budget);
                if (ReadBoolean(ref reader, nodes, "HasRow", budget))
                {
                    nodes.Add(NspiPropertyParser.ParseValueList(ref reader, "Row", budget, codePage: codePage, warnings: warnings));
                }
                ParseAuxiliarySuffix(ref reader, nodes, warnings, budget, cancellationToken);
                break;
            }
            case "GETMATCHES":
                ParseRowsResponse(
                    ref reader,
                    nodes,
                    includeMinimalIds: true,
                    hasColumnsRequiresState: false,
                    budget: budget,
                    warnings: warnings);
                ParseAuxiliarySuffix(ref reader, nodes, warnings, budget, cancellationToken);
                break;
            case "QUERYROWS":
                ParseRowsResponse(
                    ref reader,
                    nodes,
                    includeMinimalIds: false,
                    hasColumnsRequiresState: false,
                    budget: budget,
                    warnings: warnings);
                ParseAuxiliarySuffix(ref reader, nodes, warnings, budget, cancellationToken);
                break;
            case "RESOLVENAMES":
            {
                var codePage = ReadUInt32(ref reader, nodes, "CodePage", budget);
                if (ReadBoolean(ref reader, nodes, "HasMinimalIds", budget)) ParseMinimalEntryIdArray(ref reader, nodes, "MinimalIds", budget);
                ParseColumnsAndRows(ref reader, nodes, "HasRowsAndCols", budget, codePage, warnings);
                ParseAuxiliarySuffix(ref reader, nodes, warnings, budget, cancellationToken);
                break;
            }
            case "SEEKENTRIES":
            {
                var hasState = ReadBoolean(ref reader, nodes, "HasState", budget);
                uint? codePage = null;
                if (hasState)
                {
                    codePage = ParseStat(ref reader, nodes, budget);
                }
                ParseColumnsAndRows(ref reader, nodes, "HasColsAndRows", budget, codePage, warnings);
                ParseAuxiliarySuffix(ref reader, nodes, warnings, budget, cancellationToken);
                break;
            }
            case "UPDATESTAT":
                var hasUpdatedState = ReadBoolean(ref reader, nodes, "HasState", budget);
                if (hasUpdatedState)
                {
                    ParseStat(ref reader, nodes, budget);
                }
                if (ReadBoolean(ref reader, nodes, "HasDelta", budget)) ReadInt32(ref reader, nodes, "Delta", budget);
                ParseAuxiliarySuffix(ref reader, nodes, warnings, budget, cancellationToken);
                break;
            case "RESORTRESTRICTION":
                if (ReadBoolean(ref reader, nodes, "HasState", budget)) ParseStat(ref reader, nodes, budget);
                if (ReadBoolean(ref reader, nodes, "HasMinimalIds", budget)) ParseMinimalEntryIdArray(ref reader, nodes, "MinimalIds", budget);
                ParseAuxiliarySuffix(ref reader, nodes, warnings, budget, cancellationToken);
                break;
            case "UNBIND":
            case "DISCONNECT":
            case "MODLINKATT":
            case "MODPROPS":
                ParseAuxiliarySuffix(ref reader, nodes, warnings, budget, cancellationToken);
                break;
            default:
                warnings.Add($"{requestType} response fields are retained as raw because semantic decoding is not implemented.");
                break;
        }
    }

    private static void ParseResponsePrefix(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder nodes,
        List<string> warnings,
        MapiNodeBudget budget)
    {
        var lines = ImmutableArray.CreateBuilder<MapiNode>();
        var started = reader.Position;
        var count = 0;
        while (!reader.End && count++ < 256)
        {
            var lineStart = reader.Position;
            var bytes = new List<byte>();
            var terminated = false;
            while (!reader.End && bytes.Count < 16 * 1024)
            {
                var value = reader.ReadByte("additional response header");
                if (value == (byte)'\n')
                {
                    terminated = true;
                    break;
                }
                if (value != (byte)'\r') bytes.Add(value);
            }
            if (!terminated)
            {
                throw new MapiParseException(lineStart, "MAPI/HTTP response additional headers are not blank-line terminated.");
            }
            var text = Encoding.ASCII.GetString(bytes.ToArray());
            if (text.Length == 0) break;
            var kind = text is "PROCESSING" or "PENDING" or "DONE" ? MapiNodeKind.Field : MapiNodeKind.Field;
            budget.Claim(0);
            lines.Add(MapiNode.Leaf(
                text is "PROCESSING" or "PENDING" or "DONE" ? "MetaTag" : "AdditionalHeader",
                kind,
                lineStart,
                reader.Position - lineStart,
                text));
        }
        budget.Claim(0);
        nodes.Add(new MapiNode(
            "Additional response headers",
            MapiNodeKind.Array,
            started,
            reader.Position - started,
            null,
            lines.ToImmutable()));
        ReadUInt32(ref reader, nodes, "StatusCode", budget, true);
    }

    private static uint ParseStatusCode(ImmutableArray<MapiNode>.Builder nodes)
    {
        var value = nodes[^1].Value;
        return value is not null && value.StartsWith("0x", StringComparison.Ordinal)
            ? uint.Parse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : 0;
    }

    private static void ParseAuxiliarySuffix(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder nodes,
        List<string> warnings,
        MapiNodeBudget budget,
        CancellationToken cancellationToken)
    {
        var size = ReadSize32(ref reader, nodes, "AuxiliaryBufferSize", budget);
        ParseExtended(ref reader, nodes, size, "AuxiliaryBuffer", false, MapiDirection.Request, warnings, budget, cancellationToken);
    }

    private static void ParseExtended(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder nodes,
        int size,
        string name,
        bool parseRops,
        MapiDirection direction,
        List<string> warnings,
        MapiNodeBudget budget,
        CancellationToken cancellationToken,
        FastTransferStreamAssembler? fastTransferAssembler = null,
        string? captureScope = null,
        MapiCaptureContext? context = null)
    {
        var start = reader.Position;
        var bytes = reader.ReadBytes(size, name);
        var children = ExtendedBufferParser.ParseSequence(
            bytes,
            start,
            warnings,
            parseRops,
            direction,
            budget,
            cancellationToken,
            fastTransferAssembler,
            captureScope,
            context);
        budget.Claim(0);
        nodes.Add(new MapiNode(name, MapiNodeKind.Array, start, size, null, children));
    }

    private static uint ParseStat(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder nodes,
        MapiNodeBudget budget)
    {
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        uint codePage = 0;
        foreach (var name in new[]
        {
            "SortType", "ContainerID", "CurrentRec", "Delta", "NumPos", "TotalRecs",
            "CodePage", "TemplateLocale", "SortLocale"
        })
        {
            if (name == "Delta")
            {
                ReadInt32(ref reader, children, name, budget);
            }
            else
            {
                var value = ReadUInt32(ref reader, children, name, budget, name.Contains("Locale", StringComparison.Ordinal) || name is "ContainerID" or "CurrentRec");
                if (name == "CodePage") codePage = value;
            }
        }
        budget.Claim(0);
        nodes.Add(new MapiNode("State", MapiNodeKind.Structure, start, reader.Position - start, null, children.ToImmutable()));
        return codePage;
    }

    private static IReadOnlyList<uint> ParsePropertyTagArray(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder nodes,
        MapiNodeBudget budget)
    {
        var start = reader.Position;
        var count = reader.ReadCount32("PropertyTagCount");
        var tags = new uint[count];
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        ExtendedBufferParser.AddField(children, "Count", start, 4, count.ToString(), budget);
        for (var index = 0; index < count; index++)
        {
            var offset = reader.Position;
            var tag = reader.ReadUInt32($"PropertyTag[{index}]");
            tags[index] = tag;
            ExtendedBufferParser.AddField(children, $"PropertyTag[{index}]", offset, 4, FormatPropertyTag(tag), budget);
        }

        budget.Claim(0);
        nodes.Add(new MapiNode("PropertyTags", MapiNodeKind.Array, start, reader.Position - start, null, children.ToImmutable()));
        return tags;
    }

    private static void ParseRowsResponse(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder nodes,
        bool includeMinimalIds,
        bool hasColumnsRequiresState,
        MapiNodeBudget budget,
        List<string> warnings)
    {
        var hasState = ReadBoolean(ref reader, nodes, "HasState", budget);
        uint? codePage = null;
        if (hasState) codePage = ParseStat(ref reader, nodes, budget);
        if (includeMinimalIds && ReadBoolean(ref reader, nodes, "HasMinimalIds", budget))
        {
            ParseMinimalEntryIdArray(ref reader, nodes, "MinimalIds", budget);
        }
        if (!hasColumnsRequiresState || hasState)
        {
            ParseColumnsAndRows(ref reader, nodes, "HasColsAndRows", budget, codePage, warnings);
        }
    }

    private static void ParseColumnsAndRows(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder nodes,
        string presenceName,
        MapiNodeBudget budget,
        uint? codePage = null,
        List<string>? warnings = null)
    {
        if (!ReadBoolean(ref reader, nodes, presenceName, budget))
        {
            return;
        }
        var columns = ParsePropertyTagArray(ref reader, nodes, budget);
        var rowStart = reader.Position;
        var rowCount = reader.ReadCount32("RowCount");
        var rows = ImmutableArray.CreateBuilder<MapiNode>();
        ExtendedBufferParser.AddField(rows, "RowCount", rowStart, 4, rowCount.ToString(CultureInfo.InvariantCulture), budget);
        for (var index = 0; index < rowCount; index++)
        {
            rows.Add(NspiPropertyParser.ParseRow(
                ref reader,
                columns,
                $"Row[{index}]",
                budget,
                codePage: codePage,
                warnings: warnings));
        }
        budget.Claim(0);
        nodes.Add(new MapiNode("RowData", MapiNodeKind.Array, rowStart, reader.Position - rowStart, null, rows.ToImmutable()));
    }

    private static void ParsePropertyValueListArray(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder nodes,
        string name,
        MapiNodeBudget budget,
        uint? codePage = null,
        List<string>? warnings = null)
    {
        var start = reader.Position;
        var count = reader.ReadCount32($"{name}Count");
        var rows = ImmutableArray.CreateBuilder<MapiNode>();
        ExtendedBufferParser.AddField(rows, "Count", start, 4, count.ToString(CultureInfo.InvariantCulture), budget);
        for (var index = 0; index < count; index++)
        {
            rows.Add(NspiPropertyParser.ParseValueList(
                ref reader,
                $"{name}[{index}]",
                budget,
                codePage: codePage,
                warnings: warnings));
        }
        budget.Claim(0);
        nodes.Add(new MapiNode(name, MapiNodeKind.Array, start, reader.Position - start, null, rows.ToImmutable()));
    }

    private static void ParseSizedEntryId(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder nodes,
        int index,
        MapiNodeBudget budget)
    {
        var start = reader.Position;
        var size = reader.ReadUInt32($"EntryId[{index}].Size");
        if (size > MapiParseLimits.MaxPayloadBytes || size > reader.Remaining)
        {
            throw new MapiParseException(
                start,
                $"EntryId[{index}] declares {size:N0} bytes but only {reader.Remaining:N0} remain.");
        }
        var payloadOffset = reader.Position;
        var payload = reader.ReadBytes((int)size, $"EntryId[{index}].Value");
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        ExtendedBufferParser.AddField(children, "Size", start, 4, size.ToString(CultureInfo.InvariantCulture), budget);
        children.Add(ExtendedBufferParser.RawNode("Value", payload, payloadOffset, budget));
        budget.Claim(0);
        nodes.Add(new MapiNode(
            $"EntryId[{index}]",
            MapiNodeKind.Structure,
            start,
            reader.Position - start,
            payload.IsEmpty ? "Empty" : payload[0] switch
            {
                0x87 => "Ephemeral",
                0x00 => "Permanent",
                _ => "Unknown form"
            },
            children.ToImmutable()));
    }

    private static void ParseMinimalEntryIdArray(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder nodes,
        string name,
        MapiNodeBudget budget)
    {
        var start = reader.Position;
        var count = reader.ReadCount32($"{name}Count");
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        ExtendedBufferParser.AddField(children, "Count", start, 4, count.ToString(), budget);
        for (var index = 0; index < count; index++)
        {
            var entryStart = reader.Position;
            var value = reader.ReadUInt32($"MinimalEntryId[{index}]");
            var entryChildren = ImmutableArray.CreateBuilder<MapiNode>();
            ExtendedBufferParser.AddField(
                entryChildren,
                "MinEntryID",
                entryStart,
                4,
                $"0x{value:X8}",
                budget);
            budget.Claim(0);
            children.Add(new MapiNode(
                $"MinimalEntryID[{index}]",
                MapiNodeKind.Structure,
                entryStart,
                4,
                null,
                entryChildren.ToImmutable()));
        }
        budget.Claim(0);
        nodes.Add(new MapiNode(name, MapiNodeKind.Array, start, reader.Position - start, null, children.ToImmutable()));
    }

    private static bool ReadBoolean(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder nodes,
        string name,
        MapiNodeBudget budget)
    {
        var offset = reader.Position;
        var value = reader.ReadByte(name);
        ExtendedBufferParser.AddField(nodes, name, offset, 1, value == 0 ? "false" : value == 1 ? "true" : $"true (noncanonical 0x{value:X2})", budget);
        return value != 0;
    }

    private static void ReadPropertyTag(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder nodes,
        string name,
        MapiNodeBudget budget)
    {
        var offset = reader.Position;
        var value = reader.ReadUInt32(name);
        ExtendedBufferParser.AddField(nodes, name, offset, 4, FormatPropertyTag(value), budget);
    }

    private static uint ReadUInt32(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder nodes,
        string name,
        MapiNodeBudget budget,
        bool hex = false)
    {
        var offset = reader.Position;
        var value = reader.ReadUInt32(name);
        ExtendedBufferParser.AddField(nodes, name, offset, 4, hex ? $"0x{value:X8}" : value.ToString(CultureInfo.InvariantCulture), budget);
        return value;
    }

    private static int ReadInt32(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder nodes,
        string name,
        MapiNodeBudget budget)
    {
        var offset = reader.Position;
        var value = reader.ReadInt32(name);
        ExtendedBufferParser.AddField(nodes, name, offset, 4, value.ToString(CultureInfo.InvariantCulture), budget);
        return value;
    }

    private static int ReadSize32(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder nodes,
        string name,
        MapiNodeBudget budget)
    {
        var offset = reader.Position;
        var value = reader.ReadUInt32(name);
        if (value > MapiParseLimits.MaxPayloadBytes || value > reader.Remaining)
        {
            throw new MapiParseException(offset, $"{name} declares {value:N0} bytes but {reader.Remaining:N0} remain and the limit is {MapiParseLimits.MaxPayloadBytes:N0}.");
        }
        ExtendedBufferParser.AddField(nodes, name, offset, 4, value.ToString(CultureInfo.InvariantCulture), budget);
        return (int)value;
    }

    private static int ReadCount32(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder nodes,
        string name,
        MapiNodeBudget budget)
    {
        var offset = reader.Position;
        var value = reader.ReadCount32(name);
        ExtendedBufferParser.AddField(nodes, name, offset, 4, value.ToString(CultureInfo.InvariantCulture), budget);
        return value;
    }

    private static void ReadGuid(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder nodes,
        string name,
        MapiNodeBudget budget)
    {
        var offset = reader.Position;
        var value = reader.ReadGuid(name);
        ExtendedBufferParser.AddField(nodes, name, offset, 16, value.ToString(), budget);
    }

    private static void ReadAsciiZ(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder nodes,
        string name,
        MapiNodeBudget budget)
    {
        var offset = reader.Position;
        var value = reader.ReadNullTerminatedAscii(name);
        ExtendedBufferParser.AddField(nodes, name, offset, reader.Position - offset, value, budget);
    }

    private static void ReadUnicodeZ(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder nodes,
        string name,
        MapiNodeBudget budget)
    {
        var offset = reader.Position;
        var value = reader.ReadNullTerminatedUnicode(name);
        ExtendedBufferParser.AddField(nodes, name, offset, reader.Position - offset, value, budget);
    }

    private static string FormatPropertyTag(uint tag)
    {
        var propertyId = (ushort)(tag >> 16);
        var propertyType = (ushort)tag;
        return $"0x{tag:X8} (id {MapiPropertyNames.FormatPidTag(propertyId)}, {PropertyTypeName(propertyType)})";
    }

    private static string PropertyTypeName(ushort type) => type switch
    {
        0x0002 => "PtypInteger16",
        0x0003 => "PtypInteger32",
        0x0004 => "PtypFloating32",
        0x0005 => "PtypFloating64",
        0x000B => "PtypBoolean",
        0x0014 => "PtypInteger64",
        0x001E => "PtypString8",
        0x001F => "PtypString",
        0x0040 => "PtypTime",
        0x0048 => "PtypGuid",
        0x0102 => "PtypBinary",
        _ when (type & 0x1000) != 0 => $"multivalue 0x{type:X4}",
        _ => $"type 0x{type:X4}"
    };
}
