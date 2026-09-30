using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace SazViewer.Core;

/// <summary>
/// A bounded, hostile-input-safe lexer for the FastTransfer stream carried inside
/// RopFastTransferSourceGetBuffer / RopFastTransferDestinationPutBuffer(Extended) transfer buffers.
/// <para>
/// The grammar implemented here is MS-OXCFXICS 2.2.4.1 (lexical structure):
/// <c>stream = 1*element</c>, <c>element = marker / propValue</c>,
/// <c>propValue = fixedPropType propInfo fixedSizeValue</c> /
/// <c>varPropType propInfo length varSizeValue</c> /
/// <c>mvPropType propInfo length *( fixedSizeValue / length varSizeValue )</c>,
/// <c>propInfo = taggedPropId / ( namedPropId namedPropInfo )</c>. Marker and meta-property tag
/// values are taken from MS-OXCFXICS 2.2.4.1.4 and 2.2.4.1.5 and cross-checked against the pinned
/// MIT upstream (Office-Inspectors-for-Fiddler <c>MSOXCFXICS</c> parsers).
/// </para>
/// <para>
/// Safety contract: the lexer never guesses a length it cannot derive from bytes it has already
/// read; it enforces element, collection, string, depth, node and forward-progress limits; it honours
/// cancellation; and any incoherence stops lexing and retains the untouched remainder as bounded raw
/// bytes rather than resynchronizing on a guess. Every state transform is transactional - callers
/// receive one immutable <see cref="FastTransferLexResult"/> that is adopted as a whole or not at all.
/// </para>
/// <para>
/// Known divergences from the pinned upstream, both deliberate: (1) upstream's
/// <c>MetaPropValue</c> maps MetaTagDnPrefix to FolderReplicaInfo and MetaTagNewFXFolder to a
/// String8, which inverts MS-OXCFXICS 2.2.4.1.5.3/2.2.4.1.5.6 (0x40110102 is PtypBinary, 0x4008001E
/// is PtypString8); this lexer follows the specification. (2) upstream's GLOBSET reader pops an
/// empty common-prefix stack and ignores pushes that complete a six-byte prefix; this lexer tracks
/// the prefix stack defensively and stops rather than faulting.
/// </para>
/// </summary>
internal static class FastTransferStreamLexer
{
    private const ushort MultiValueFlag = 0x1000;
    private const uint MetaTagIdsetGivenTag = 0x40170003;
    private const uint ObjectLengthSentinel = 0xFFFFFFFF;

    // ---- Public entry point ----------------------------------------------------------------------

    /// <summary>
    /// Lexes one transfer buffer, resuming any varSizeValue that a previous buffer of the same stream
    /// ended inside.
    /// </summary>
    /// <param name="buffer">The exact transfer-buffer bytes; the lexer never reads outside them.</param>
    /// <param name="absoluteOffset">Absolute capture offset of <paramref name="buffer"/>[0].</param>
    /// <param name="state">Immutable inbound stream state; use <see cref="FastTransferStreamState.Initial"/> for a standalone buffer.</param>
    /// <param name="budget">Shared node/depth budget.</param>
    /// <param name="depth">Tree depth at which the produced elements sit.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    public static FastTransferLexResult Lex(
        ReadOnlySpan<byte> buffer,
        long absoluteOffset,
        FastTransferStreamState state,
        MapiNodeBudget budget,
        int depth,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(budget);

        var nodes = ImmutableArray.CreateBuilder<MapiNode>();
        var warnings = new List<string>();
        var next = state.WithBuffer(buffer.Length);
        var elements = 0;
        var endedInsideValue = false;

        if (buffer.IsEmpty)
        {
            return new FastTransferLexResult(nodes.ToImmutable(), next, [.. warnings], 0, 0, false);
        }

        if (next.Desynchronized)
        {
            nodes.Add(ExtendedBufferParser.RawNode("Unsynchronized stream bytes", buffer, absoluteOffset, budget));
            warnings.Add(
                $"FastTransfer stream is desynchronized from an earlier buffer; {buffer.Length:N0} byte(s) were retained as raw.");
            return new FastTransferLexResult(nodes.ToImmutable(), next, [.. warnings], buffer.Length, 0, false);
        }

        var reader = new MapiReader(buffer, cancellationToken, checked((int)absoluteOffset));
        var elementStart = reader;
        var syntaxStack = next.SyntaxStack;
        var grammar = next.Grammar;
        var pendingSpecialMarker = next.PendingSpecialMarker;
        FastTransferPendingValue? pending = next.Pending;

        if (grammar.IsConfigured
            && grammar.Root == FastTransferRootKind.Unknown
            && !grammar.AmbiguityReported)
        {
            warnings.Add(
                $"FastTransfer root could not be selected uniquely from {grammar.Provenance}; lexical decoding " +
                "continues, but root production and phase ordering are not validated.");
            grammar = grammar with { AmbiguityReported = true };
        }

        try
        {
            if (pending is { } resume)
            {
                pending = ResumePendingValue(ref reader, resume, nodes, warnings, budget, depth);
                if (pending is not null)
                {
                    endedInsideValue = true;
                }
            }

            while (pending is null && !reader.End)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (elements >= FastTransferLimits.MaxStreamElements)
                {
                    var overflowOffset = reader.Position;
                    nodes.Add(ExtendedBufferParser.RawNode(
                        "Undecoded stream remainder",
                        reader.ReadRemaining("stream remainder"),
                        overflowOffset,
                        budget));
                    warnings.Add(
                        $"FastTransfer buffer reached the {FastTransferLimits.MaxStreamElements:N0}-element limit; " +
                        "the remaining bytes were retained as raw.");
                    break;
                }

                elementStart = reader;
                var before = reader.LocalPosition;
                if (reader.Remaining < 4)
                {
                    var tailOffset = reader.Position;
                    var tail = reader.ReadRemaining("trailing atom");
                    nodes.Add(ExtendedBufferParser.RawNode("Trailing partial atom", tail, tailOffset, budget));
                    warnings.Add(
                        $"FastTransfer buffer ends with {tail.Length} byte(s), which is shorter than the four-byte atom " +
                        "MS-OXCFXICS 2.2.4.1 forbids splitting; the bytes were retained as raw.");
                    endedInsideValue = true;
                    break;
                }

                var node = LexElement(
                    ref reader,
                    elements,
                    ref syntaxStack,
                    ref grammar,
                    ref pendingSpecialMarker,
                    out pending,
                    warnings,
                    budget,
                    depth,
                    cancellationToken);
                if (reader.LocalPosition <= before)
                {
                    // Defensive: every element consumes at least four bytes. Guard anyway so hostile
                    // input can never spin this loop.
                    throw new MapiParseException(
                        reader.Position,
                        "Internal FastTransfer element decoder made no progress.");
                }
                nodes.Add(node);
                elements++;
                if (pending is not null)
                {
                    endedInsideValue = true;
                }
            }
        }
        catch (MapiParseException exception)
        {
            var remainderOffset = elementStart.Position;
            if (!elementStart.End)
            {
                try
                {
                    nodes.Add(ExtendedBufferParser.RawNode(
                        "Unlexed stream remainder",
                        elementStart.ReadRemaining("unlexed remainder"),
                        remainderOffset,
                        budget));
                }
                catch (MapiParseException)
                {
                    // The shared node budget is exhausted; the remainder is reported by warning only.
                }
            }
            warnings.Add(
                $"FastTransfer element {elements} could not be lexed: {exception.Message}; the remaining bytes were " +
                "retained as raw and the stream is marked desynchronized.");
            return new FastTransferLexResult(
                nodes.ToImmutable(),
                next.WithElements(elements).WithSyntaxStack(syntaxStack).WithGrammar(grammar).AsDesynchronized(),
                [.. warnings],
                buffer.Length,
                elements,
                true);
        }

        if (pending is null && !reader.End)
        {
            var leftoverOffset = reader.Position;
            var leftover = reader.ReadRemaining("stream remainder");
            nodes.Add(ExtendedBufferParser.RawNode("Undecoded stream remainder", leftover, leftoverOffset, budget));
        }

        var committed = next
            .WithElements(elements)
            .WithSyntaxStack(syntaxStack)
            .WithGrammar(grammar)
            .WithPendingSpecialMarker(pendingSpecialMarker)
            .WithPending(pending);
        return new FastTransferLexResult(
            nodes.ToImmutable(), committed, [.. warnings], buffer.Length, elements, endedInsideValue);
    }

    // ---- Element dispatch ------------------------------------------------------------------------

    private static MapiNode LexElement(
        ref MapiReader reader,
        int index,
        ref ImmutableArray<FastTransferSyntaxFrame> syntaxStack,
        ref FastTransferGrammarState grammar,
        ref uint? pendingSpecialMarker,
        out FastTransferPendingValue? pending,
        List<string> warnings,
        MapiNodeBudget budget,
        int depth,
        CancellationToken cancellationToken)
    {
        pending = null;
        budget.Claim(depth);
        var probe = reader;
        var tag = probe.ReadUInt32("Element tag");

        if (MarkerNames.TryGetValue(tag, out var markerName))
        {
            var isRecoveryError = tag == 0x40180003
                && grammar.Root is FastTransferRootKind.ContentsSync or FastTransferRootKind.MessageList;
            if (isRecoveryError)
            {
                pendingSpecialMarker = null;
            }
            else
            {
                WarnIfSpecialValueMissing(ref pendingSpecialMarker, markerName, index, warnings);
            }
            var depthBefore = syntaxStack.Length;
            var marker = LexMarker(ref reader, index, markerName, tag, ref syntaxStack, warnings, budget, depth);
            if (isRecoveryError)
            {
                syntaxStack = ImmutableArray<FastTransferSyntaxFrame>.Empty;
            }
            grammar = FastTransferGrammar.AdvanceMarker(
                grammar, tag, marker.Offset, depthBefore, syntaxStack.Length);
            if (tag is 0x4074000B or 0x407B0102)
            {
                pendingSpecialMarker = tag;
            }
            return marker;
        }

        if (MetaPropertyNames.ContainsKey(tag))
        {
            WarnIfSpecialValueMissing(ref pendingSpecialMarker, MetaPropertyNames[tag], index, warnings);
            var meta = LexMetaProperty(ref reader, index, tag, budget, depth, warnings);
            grammar = FastTransferGrammar.AdvanceMetaProperty(
                grammar, tag, meta.Offset, syntaxStack.Length);
            return meta;
        }

        var specialMarker = pendingSpecialMarker;
        pendingSpecialMarker = null;
        var property = LexPropValue(
            ref reader,
            index,
            tag,
            specialMarker,
            out pending,
            warnings,
            budget,
            depth,
            cancellationToken);
        grammar = FastTransferGrammar.AdvanceProperty(grammar, tag, property.Offset);
        return property;
    }

    private static void WarnIfSpecialValueMissing(
        ref uint? pendingSpecialMarker,
        string nextElement,
        int index,
        List<string> warnings)
    {
        if (pendingSpecialMarker is not { } marker)
        {
            return;
        }

        warnings.Add(
            $"FastTransfer element {index} is {nextElement}, but {MarkerNames[marker]} requires its special " +
            "property value immediately after the marker.");
        pendingSpecialMarker = null;
    }

    private static MapiNode LexMarker(
        ref MapiReader reader,
        int index,
        string markerName,
        uint tag,
        ref ImmutableArray<FastTransferSyntaxFrame> syntaxStack,
        List<string> warnings,
        MapiNodeBudget budget,
        int depth)
    {
        var offset = reader.Position;
        _ = reader.ReadUInt32("Marker");
        var depthBefore = syntaxStack.Length;
        string syntax;
        if (TryGetOpeningFrame(tag, out var opening))
        {
            if (syntaxStack.Length >= FastTransferLimits.MaxMarkerDepth)
            {
                throw new MapiParseException(
                    offset,
                    $"FastTransfer marker nesting exceeds {FastTransferLimits.MaxMarkerDepth}.");
            }
            syntaxStack = syntaxStack.Add(opening);
            syntax = $"opens {opening.Production}";
        }
        else if (IsClosingMarker(tag))
        {
            if (syntaxStack.IsEmpty)
            {
                warnings.Add(
                    $"FastTransfer element {index} is the end marker {markerName} but no matching start marker is open.");
                syntax = "unmatched end marker";
            }
            else
            {
                var expected = syntaxStack[^1];
                if (expected.EndTag != tag)
                {
                    warnings.Add(
                        $"FastTransfer element {index} is {markerName}, but the open {expected.Production} " +
                        $"production requires {MarkerNames[expected.EndTag]}; the production was left open.");
                    syntax = $"mismatched end marker for {expected.Production}";
                }
                else
                {
                    syntaxStack = syntaxStack.RemoveAt(syntaxStack.Length - 1);
                    syntax = $"closes {expected.Production}";
                }
            }
        }
        else
        {
            syntax = StandaloneProductionNames.TryGetValue(tag, out var production)
                ? $"starts {production}"
                : "standalone marker";
        }

        budget.Claim(depth);
        return MapiNode.Leaf(
            "Marker",
            MapiNodeKind.Field,
            offset,
            4,
            $"{markerName} (0x{tag:X8}); {syntax}; nesting {depthBefore} -> {syntaxStack.Length}");
    }

    private static MapiNode LexMetaProperty(
        ref MapiReader reader,
        int index,
        uint tag,
        MapiNodeBudget budget,
        int depth,
        List<string> warnings)
    {
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var typeOffset = reader.Position;
        var propertyType = reader.ReadUInt16("MetaPropValue.PropType");
        var idOffset = reader.Position;
        var propertyId = reader.ReadUInt16("MetaPropValue.PropID");
        ExtendedBufferParser.AddField(
            children, "PropType", typeOffset, 2, NspiPropertyParser.PropertyTypeName(propertyType), budget);
        ExtendedBufferParser.AddField(
            children, "PropID", idOffset, 2, $"0x{propertyId:X4} {MetaPropertyNames[tag]}", budget);

        switch (tag)
        {
            case 0x4008001E: // MetaTagDnPrefix - PtypString8 (MS-OXCFXICS 2.2.4.1.5.6).
            {
                var length = ReadVarLength(ref reader, "MetaTagDnPrefix.Length");
                ExtendedBufferParser.AddField(
                    children,
                    "Length",
                    reader.Position - 4,
                    4,
                    length.ToString(CultureInfo.InvariantCulture),
                    budget);
                var valueOffset = reader.Position;
                var value = reader.ReadBytes(checked((int)length), "MetaTagDnPrefix.Value");
                children.Add(DecodeString8(value, valueOffset, "Value", budget, warnings));
                break;
            }
            case 0x40110102: // MetaTagNewFXFolder - PtypBinary holding a FolderReplicaInfo.
            {
                var length = ReadVarLength(ref reader, "MetaTagNewFXFolder.Length");
                ExtendedBufferParser.AddField(
                    children,
                    "Length",
                    reader.Position - 4,
                    4,
                    length.ToString(CultureInfo.InvariantCulture),
                    budget);
                var slice = reader.SliceReader(checked((int)length), "MetaTagNewFXFolder.Value");
                children.Add(ParseFolderReplicaInfo(ref slice, budget, depth + 1, warnings));
                AppendUnconsumed(ref slice, children, "FolderReplicaInfo trailing bytes", budget, warnings);
                break;
            }
            default:
            {
                var valueOffset = reader.Position;
                var value = reader.ReadUInt32("MetaPropValue.Value");
                var rendered = tag == 0x40160003
                    ? $"0x{value:X8} ({NspiPropertyParser.PropertyTypeName((ushort)(value & 0xFFFF))} / " +
                      $"{MapiPropertyNames.FormatPidTag((ushort)(value >> 16))})"
                    : $"0x{value:X8} ({value:N0})";
                ExtendedBufferParser.AddField(children, "Value", valueOffset, 4, rendered, budget);
                break;
            }
        }

        budget.Claim(depth);
        return new MapiNode(
            "MetaPropValue",
            MapiNodeKind.Property,
            start,
            reader.Position - start,
            MetaPropertyNames[tag],
            children.ToImmutable());
    }

    private static MapiNode LexPropValue(
        ref MapiReader reader,
        int index,
        uint tag,
        uint? specialMarker,
        out FastTransferPendingValue? pending,
        List<string> warnings,
        MapiNodeBudget budget,
        int depth,
        CancellationToken cancellationToken)
    {
        pending = null;
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var typeOffset = reader.Position;
        var propertyType = reader.ReadUInt16("PropValue.PropType");
        var idOffset = reader.Position;
        var propertyId = reader.ReadUInt16("PropValue.PropID");
        if (specialMarker is 0x4074000B or 0x407B0102
            && (propertyType != 0x0102 || propertyId != 0x0000))
        {
            warnings.Add(
                $"{MarkerNames[specialMarker.Value]} requires the immediately following property to be the " +
                $"special PtypBinary tag 0x00000102, but found 0x{propertyId:X4}:{propertyType:X4}; " +
                "the ordinary property grammar was used instead.");
            specialMarker = null;
        }
        ExtendedBufferParser.AddField(children, "PropType", typeOffset, 2, DescribeType(propertyType), budget);
        ExtendedBufferParser.AddField(
            children, "PropID", idOffset, 2, DescribePropertyId(propertyId, tag, specialMarker), budget);

        string? namedSetName = null;
        string? namedSymbol = null;
        if (propertyId >= 0x8000)
        {
            children.Add(ParseNamedPropInfo(ref reader, budget, depth + 1, out namedSetName, out namedSymbol));
        }

        var isIdsetGiven = tag == MetaTagIdsetGivenTag;
        string summary;
        if (!isIdsetGiven && IsFixedType(propertyType))
        {
            summary = ParseFixedValue(ref reader, propertyType, propertyId, children, budget, depth + 1);
        }
        else if (isIdsetGiven || IsVarType(propertyType) || IsCodePageType(propertyType))
        {
            summary = ParseVarValue(
                ref reader,
                propertyType,
                propertyId,
                multiValueRemaining: 0,
                specialMarker: specialMarker,
                children,
                warnings,
                budget,
                depth + 1,
                out pending);
        }
        else if (IsMultiValueType(propertyType))
        {
            summary = ParseMultiValue(
                ref reader,
                propertyType,
                propertyId,
                children,
                warnings,
                budget,
                depth + 1,
                cancellationToken,
                out pending);
        }
        else
        {
            throw new MapiParseException(
                typeOffset,
                $"propType 0x{propertyType:X4} is not a FastTransfer fixedPropType, varPropType, code page type or " +
                "mvPropType, so the element length cannot be determined.");
        }

        budget.Claim(depth);
        var label = namedSymbol ?? MapiPropertyNames.PidTag(propertyId);
        var value = label is null
            ? $"0x{propertyId:X4}:{propertyType:X4} = {summary}"
            : $"{label} (0x{propertyId:X4}:{propertyType:X4}) = {summary}";
        if (namedSetName is not null && namedSymbol is null)
        {
            value = $"{namedSetName} {value}";
        }
        return new MapiNode(
            "PropValue",
            MapiNodeKind.Property,
            start,
            reader.Position - start,
            value,
            children.ToImmutable());
    }

    // ---- propInfo --------------------------------------------------------------------------------

    private static MapiNode ParseNamedPropInfo(
        ref MapiReader reader,
        MapiNodeBudget budget,
        int depth,
        out string? propertySetName,
        out string? symbol)
    {
        budget.Claim(depth);
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var setOffset = reader.Position;
        var propertySet = reader.ReadGuid("NamedPropInfo.PropertySet");
        propertySetName = MapiPropertyNames.PropertySetName(propertySet);
        ExtendedBufferParser.AddField(
            children,
            "PropertySet",
            setOffset,
            16,
            propertySetName is null ? propertySet.ToString() : $"{propertySet} ({propertySetName})",
            budget);

        var kindOffset = reader.Position;
        var kind = reader.ReadByte("NamedPropInfo.Kind");
        symbol = null;
        string kindName;
        switch (kind)
        {
            case 0x00:
            {
                kindName = "0x00 LID";
                ExtendedBufferParser.AddField(children, "Kind", kindOffset, 1, kindName, budget);
                var dispidOffset = reader.Position;
                var dispid = reader.ReadUInt32("NamedPropInfo.Dispid");
                symbol = propertySetName is null || dispid > ushort.MaxValue
                    ? null
                    : MapiPropertyNames.PidLid(propertySetName, (ushort)dispid);
                ExtendedBufferParser.AddField(
                    children,
                    "Dispid",
                    dispidOffset,
                    4,
                    symbol is null ? $"0x{dispid:X8}" : $"0x{dispid:X8} {symbol}",
                    budget);
                break;
            }
            case 0x01:
            {
                kindName = "0x01 Name";
                ExtendedBufferParser.AddField(children, "Kind", kindOffset, 1, kindName, budget);
                var nameOffset = reader.Position;
                var name = reader.ReadNullTerminatedUnicode("NamedPropInfo.Name");
                symbol = propertySetName is null ? null : MapiPropertyNames.PidName(propertySetName, name);
                ExtendedBufferParser.AddField(
                    children,
                    "Name",
                    nameOffset,
                    reader.Position - nameOffset,
                    symbol is null ? name : $"{name} ({symbol})",
                    budget);
                break;
            }
            default:
                throw new MapiParseException(
                    kindOffset,
                    $"NamedPropInfo.Kind 0x{kind:X2} is neither LID (0x00) nor Name (0x01).");
        }

        budget.Claim(depth);
        return new MapiNode(
            "NamedPropInfo",
            MapiNodeKind.Structure,
            start,
            reader.Position - start,
            symbol,
            children.ToImmutable());
    }

    // ---- fixedSizeValue --------------------------------------------------------------------------

    private static string ParseFixedValue(
        ref MapiReader reader,
        ushort propertyType,
        ushort propertyId,
        ImmutableArray<MapiNode>.Builder children,
        MapiNodeBudget budget,
        int depth)
    {
        var offset = reader.Position;
        switch (propertyType)
        {
            case 0x0002:
            {
                var value = reader.ReadInt16("FixedValue");
                return AddFixed(children, offset, 2, value.ToString(CultureInfo.InvariantCulture), budget);
            }
            case 0x0003:
            {
                var value = reader.ReadInt32("FixedValue");
                return AddFixed(children, offset, 4, value.ToString(CultureInfo.InvariantCulture), budget);
            }
            case 0x000A:
            {
                var value = reader.ReadUInt32("FixedValue");
                return AddFixed(children, offset, 4, $"0x{value:X8}", budget);
            }
            case 0x0004:
            {
                var value = reader.ReadSingle("FixedValue");
                return AddFixed(children, offset, 4, value.ToString("R", CultureInfo.InvariantCulture), budget);
            }
            case 0x0005:
            case 0x0007:
            {
                var value = reader.ReadDouble("FixedValue");
                return AddFixed(children, offset, 8, value.ToString("R", CultureInfo.InvariantCulture), budget);
            }
            case 0x0006:
            {
                var value = reader.ReadInt64("FixedValue");
                return AddFixed(children, offset, 8, value.ToString(CultureInfo.InvariantCulture), budget);
            }
            case 0x000B:
            {
                // MS-OXCFXICS 2.2.4.1.3: PtypBoolean is two bytes in a FastTransfer stream.
                var value = reader.ReadUInt16("FixedValue");
                return AddFixed(children, offset, 2, value == 0 ? "false" : "true", budget);
            }
            case 0x0014:
                return ParseInteger64Value(ref reader, propertyId, children, budget, depth);
            case 0x0040:
            {
                var value = reader.ReadInt64("FixedValue");
                var rendered = TryFormatFileTime(value, out var formatted)
                    ? $"{formatted} (0x{value:X16})"
                    : $"0x{value:X16}";
                return AddFixed(children, offset, 8, rendered, budget);
            }
            case 0x0048:
            {
                var value = reader.ReadGuid("FixedValue");
                return AddFixed(children, offset, 16, value.ToString(), budget);
            }
            default:
                throw new MapiParseException(offset, $"propType 0x{propertyType:X4} is not a fixedPropType.");
        }
    }

    private static string ParseInteger64Value(
        ref MapiReader reader,
        ushort propertyId,
        ImmutableArray<MapiNode>.Builder children,
        MapiNodeBudget budget,
        int depth)
    {
        // PidTagMid/PidTagFolderId/PidTagParentFolderId carry a MessageID/FolderID and
        // PidTagChangeNumber carries a CN; all three are ReplicaId(2) + GlobalCounter(6).
        var label = propertyId switch
        {
            0x674A => "MessageID",
            0x6748 => "FolderID",
            0x6749 => "FolderID",
            0x67A4 => "CN",
            _ => null,
        };
        var offset = reader.Position;
        if (label is null)
        {
            var value = reader.ReadInt64("FixedValue");
            return AddFixed(children, offset, 8, value.ToString(CultureInfo.InvariantCulture), budget);
        }

        budget.Claim(depth);
        var members = ImmutableArray.CreateBuilder<MapiNode>();
        var replicaOffset = reader.Position;
        var replicaId = reader.ReadUInt16($"{label}.ReplicaId");
        ExtendedBufferParser.AddField(
            members, "ReplicaId", replicaOffset, 2, $"{replicaId} (0x{replicaId:X4})", budget);
        var counterOffset = reader.Position;
        var counter = reader.ReadBytes(6, $"{label}.GlobalCounter");
        ExtendedBufferParser.AddField(
            members, "GlobalCounter", counterOffset, 6, Convert.ToHexString(counter), budget);
        var summary = $"{label} {replicaId}-{Convert.ToHexString(counter)}";
        children.Add(new MapiNode(
            "FixedValue", MapiNodeKind.Structure, offset, 8, summary, members.ToImmutable()));
        return summary;
    }

    private static string AddFixed(
        ImmutableArray<MapiNode>.Builder children,
        long offset,
        long length,
        string value,
        MapiNodeBudget budget)
    {
        ExtendedBufferParser.AddField(children, "FixedValue", offset, length, value, budget);
        return value;
    }

    // ---- varSizeValue ----------------------------------------------------------------------------

    private static string ParseVarValue(
        ref MapiReader reader,
        ushort propertyType,
        ushort propertyId,
        int multiValueRemaining,
        uint? specialMarker,
        ImmutableArray<MapiNode>.Builder children,
        List<string> warnings,
        MapiNodeBudget budget,
        int depth,
        out FastTransferPendingValue? pending)
    {
        pending = null;
        var lengthOffset = reader.Position;
        var length = ReadVarLength(ref reader, "Length");

        if (propertyType == 0x000D && length == ObjectLengthSentinel)
        {
            ExtendedBufferParser.AddField(
                children, "Length", lengthOffset, 4, "0xFFFFFFFF (PtypObject placeholder)", budget);
            return "embedded object placeholder";
        }

        ExtendedBufferParser.AddField(
            children, "Length", lengthOffset, 4, length.ToString(CultureInfo.InvariantCulture), budget);

        if (specialMarker == 0x4074000B && length != 32)
        {
            warnings.Add(
                $"ProgressInformation declares {length:N0} byte(s), but version 0 requires exactly 32; " +
                "the value will remain raw.");
            specialMarker = null;
        }
        else if (specialMarker == 0x407B0102 && length > FastTransferLimits.MaxSpecialStructureBytes)
        {
            warnings.Add(
                $"PropertyGroupInfo declares {length:N0} byte(s), exceeding the " +
                $"{FastTransferLimits.MaxSpecialStructureBytes:N0}-byte reconstruction limit; the value will remain raw.");
            specialMarker = null;
        }

        if (length > reader.Remaining)
        {
            // MS-OXCFXICS 2.2.4.1: this is the only legal split point. Record the tail transactionally
            // so the next buffer of the same capture-local stream can continue it.
            var partialOffset = reader.Position;
            var partial = reader.ReadRemaining("PartialValue");
            children.Add(ExtendedBufferParser.RawNode("PartialValue", partial, partialOffset, budget));
            pending = new FastTransferPendingValue(
                propertyType,
                propertyId,
                length,
                length - partial.Length,
                multiValueRemaining,
                specialMarker,
                specialMarker is null ? default : [.. partial]);
            return $"{partial.Length:N0} of {length:N0} byte(s); value continues in a later buffer";
        }

        var slice = reader.SliceReader(checked((int)length), "Value");
        var summary = DecodeVarValue(
            ref slice, propertyType, propertyId, specialMarker, children, budget, depth, warnings);
        AppendUnconsumed(ref slice, children, "Value trailing bytes", budget, warnings);
        return summary;
    }

    private static string DecodeVarValue(
        ref MapiReader slice,
        ushort propertyType,
        ushort propertyId,
        uint? specialMarker,
        ImmutableArray<MapiNode>.Builder children,
        MapiNodeBudget budget,
        int depth,
        List<string> warnings)
    {
        var offset = slice.Position;
        var length = slice.Remaining;
        if (length == 0)
        {
            ExtendedBufferParser.AddField(children, "Value", offset, 0, "empty", budget);
            return "empty";
        }

        if (IsCodePageType(propertyType))
        {
            var bytes = slice.ReadRemaining("Value");
            if (propertyType is 0x84B0 or 0x94B0)
            {
                children.Add(DecodeUnicodeString(bytes, offset, "Value", budget, warnings));
                return "code page Unicode string";
            }
            children.Add(DecodeString8(bytes, offset, "Value", budget, warnings));
            return "code page 8-bit string";
        }

        switch (propertyType)
        {
            case 0x001F:
            {
                var bytes = slice.ReadRemaining("Value");
                children.Add(DecodeUnicodeString(bytes, offset, "Value", budget, warnings));
                return $"{length:N0} byte(s) of PtypString";
            }
            case 0x001E:
            {
                var bytes = slice.ReadRemaining("Value");
                children.Add(DecodeString8(bytes, offset, "Value", budget, warnings));
                return $"{length:N0} byte(s) of PtypString8";
            }
            case 0x000D:
            {
                var bytes = slice.ReadRemaining("Value");
                children.Add(ExtendedBufferParser.RawNode("Value", bytes, offset, budget));
                return $"{length:N0} byte(s) of PtypObject";
            }
            case 0x00FB:
            case 0x0102:
            case 0x0003:
            default:
            {
                var decoded = TryDecodeKnownBinary(
                    ref slice, propertyType, propertyId, specialMarker, children, budget, depth, warnings);
                if (decoded is not null)
                {
                    return decoded;
                }
                var bytes = slice.ReadRemaining("Value");
                children.Add(ExtendedBufferParser.RawNode("Value", bytes, offset, budget));
                return $"{length:N0} byte(s)";
            }
        }
    }

    /// <summary>
    /// Decodes the binary-valued MS-OXCFXICS structures whose shape is fully determined by their
    /// property id: XIDs, PredecessorChangeList and the serialized IDSET/CNSET forms.
    /// </summary>
    private static string? TryDecodeKnownBinary(
        ref MapiReader slice,
        ushort propertyType,
        ushort propertyId,
        uint? specialMarker,
        ImmutableArray<MapiNode>.Builder children,
        MapiNodeBudget budget,
        int depth,
        List<string> warnings)
    {
        var attempt = slice;
        var speculativeWarnings = new List<string>();
        MapiNode node;
        string summary;
        try
        {
            switch (propertyId)
            {
                case 0x0000 when specialMarker == 0x4074000B && propertyType == 0x0102:
                    node = ParseProgressInformation(ref attempt, budget, depth, speculativeWarnings);
                    summary = "ProgressInformation";
                    break;
                case 0x0000 when specialMarker == 0x407B0102 && propertyType == 0x0102:
                    node = ParsePropertyGroupInfo(ref attempt, budget, depth, speculativeWarnings);
                    summary = "PropertyGroupInfo";
                    break;
                case 0x65E0: // PidTagSourceKey
                case 0x65E1: // PidTagParentSourceKey
                case 0x65E2: // PidTagChangeKey
                    node = ParseXid(ref attempt, attempt.Remaining, budget, depth);
                    summary = "XID";
                    break;
                case 0x65E3: // PidTagPredecessorChangeList
                    node = ParsePredecessorChangeList(ref attempt, budget, depth);
                    summary = "PredecessorChangeList";
                    break;
                case 0x402D: // MetaTagIdsetRead
                case 0x402E: // MetaTagIdsetUnread
                case 0x4021: // MetaTagIdsetNoLongerInScope
                case 0x6793: // MetaTagIdsetExpired
                case 0x67E5: // MetaTagIdsetDeleted
                    node = ParseIdsetList(ref attempt, replicaGuid: false, budget, depth);
                    summary = "serialized IDSET (REPLID)";
                    break;
                case 0x4017: // MetaTagIdsetGiven
                case 0x6796: // MetaTagCnsetSeen
                case 0x67DA: // MetaTagCnsetSeenFAI
                case 0x67D2: // MetaTagCnsetRead
                    node = ParseIdsetList(ref attempt, replicaGuid: true, budget, depth);
                    summary = "serialized IDSET (REPLGUID)";
                    break;
                default:
                    return null;
            }
        }
        catch (MapiParseException exception)
        {
            // Transactional: the caller's reader was never advanced, so the value falls back to raw.
            warnings.Add(
                $"0x{propertyId:X4} could not be decoded as its MS-OXCFXICS structure ({exception.Message}); " +
                "the value was retained as raw bytes.");
            return null;
        }

        slice = attempt;
        warnings.AddRange(speculativeWarnings);
        children.Add(node);
        return summary;
    }

    // ---- mvPropType ------------------------------------------------------------------------------

    private static string ParseMultiValue(
        ref MapiReader reader,
        ushort propertyType,
        ushort propertyId,
        ImmutableArray<MapiNode>.Builder children,
        List<string> warnings,
        MapiNodeBudget budget,
        int depth,
        CancellationToken cancellationToken,
        out FastTransferPendingValue? pending)
    {
        var countOffset = reader.Position;
        var count = reader.ReadCount32("Count");
        if (count > FastTransferLimits.MaxMultiValueElements)
        {
            throw new MapiParseException(
                countOffset,
                $"mvPropType element count {count:N0} exceeds the {FastTransferLimits.MaxMultiValueElements:N0} limit.");
        }
        ExtendedBufferParser.AddField(
            children, "Count", countOffset, 4, count.ToString(CultureInfo.InvariantCulture), budget);

        var baseType = (ushort)(propertyType & ~MultiValueFlag);
        var consumed = ParseMultiValueElements(
            ref reader,
            baseType,
            propertyId,
            count,
            0,
            children,
            warnings,
            budget,
            depth,
            cancellationToken,
            out pending);
        return pending is null
            ? $"{count:N0} element(s)"
            : $"{consumed:N0} of {count:N0} element(s); array continues in a later buffer";
    }

    private static int ParseMultiValueElements(
        ref MapiReader reader,
        ushort baseType,
        ushort propertyId,
        int total,
        int alreadyDone,
        ImmutableArray<MapiNode>.Builder children,
        List<string> warnings,
        MapiNodeBudget budget,
        int depth,
        CancellationToken cancellationToken,
        out FastTransferPendingValue? pending)
    {
        pending = null;
        var done = 0;
        for (var index = 0; index < total; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var elementChildren = ImmutableArray.CreateBuilder<MapiNode>();
            var start = reader.Position;
            if (IsFixedType(baseType))
            {
                ParseFixedValue(ref reader, baseType, propertyId, elementChildren, budget, depth + 1);
            }
            else if (IsVarType(baseType) || IsCodePageType(baseType))
            {
                var summary = ParseVarValue(
                    ref reader,
                    baseType,
                    propertyId,
                    multiValueRemaining: total - index - 1,
                    specialMarker: null,
                    elementChildren,
                    warnings,
                    budget,
                    depth + 1,
                    out pending);
                _ = summary;
            }
            else
            {
                throw new MapiParseException(
                    start,
                    $"mvPropType base type 0x{baseType:X4} is not a FastTransfer fixedPropType or varPropType.");
            }

            budget.Claim(depth);
            children.Add(new MapiNode(
                $"[{alreadyDone + index}]",
                MapiNodeKind.Property,
                start,
                reader.Position - start,
                null,
                elementChildren.ToImmutable()));
            done++;
            if (pending is not null)
            {
                break;
            }
        }
        return done;
    }

    // ---- Cross-buffer continuation ---------------------------------------------------------------

    private static FastTransferPendingValue? ResumePendingValue(
        ref MapiReader reader,
        FastTransferPendingValue pending,
        ImmutableArray<MapiNode>.Builder nodes,
        List<string> warnings,
        MapiNodeBudget budget,
        int depth)
    {
        var take = (int)Math.Min(pending.RemainingLength, reader.Remaining);
        var offset = reader.Position;
        var bytes = reader.ReadBytes(take, "PartialValueContinuation");
        var remaining = pending.RemainingLength - take;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var accumulated = pending.AccumulatedBytes.IsDefault
            ? ImmutableArray<byte>.Empty
            : pending.AccumulatedBytes;
        var reconstructed = pending.SpecialMarker is null
            ? default
            : accumulated.AddRange(bytes.ToArray());
        if (pending.SpecialMarker is { } specialMarker && remaining == 0)
        {
            var reconstructedReader = new MapiReader(
                reconstructed.AsSpan(),
                CancellationToken.None,
                0);
            var reconstructedChildren = ImmutableArray.CreateBuilder<MapiNode>();
            var decoded = TryDecodeKnownBinary(
                ref reconstructedReader,
                pending.PropertyType,
                pending.PropertyId,
                specialMarker,
                reconstructedChildren,
                budget,
                depth + 1,
                warnings);
            if (decoded is null)
            {
                reconstructedChildren.Add(ExtendedBufferParser.RawNode(
                    "Reconstructed bytes", reconstructed.AsSpan(), 0, budget));
            }
            else
            {
                AppendUnconsumed(
                    ref reconstructedReader,
                    reconstructedChildren,
                    "Value trailing bytes",
                    budget,
                    warnings);
            }
            children.AddRange(reconstructedChildren.Select(node => MarkReconstructed(node, offset, annotate: true)));
        }
        else
        {
            children.Add(ExtendedBufferParser.RawNode("Bytes", bytes, offset, budget));
        }
        budget.Claim(depth);
        nodes.Add(new MapiNode(
            "PartialValueContinuation",
            MapiNodeKind.Property,
            offset,
            take,
            $"0x{pending.PropertyId:X4}:{pending.PropertyType:X4} " +
            $"{pending.DeclaredLength - remaining:N0}/{pending.DeclaredLength:N0} byte(s)" +
            (remaining > 0 ? "; value still continues" : "; value complete"),
            children.ToImmutable()));

        if (remaining > 0)
        {
            return pending with
            {
                RemainingLength = remaining,
                AccumulatedBytes = pending.SpecialMarker is null ? default : reconstructed,
            };
        }

        if (pending.RemainingMultiValueElements > 0)
        {
            var baseType = (ushort)(pending.PropertyType & ~MultiValueFlag);
            var arrayChildren = ImmutableArray.CreateBuilder<MapiNode>();
            var arrayStart = reader.Position;
            var done = ParseMultiValueElements(
                ref reader,
                baseType,
                pending.PropertyId,
                pending.RemainingMultiValueElements,
                0,
                arrayChildren,
                warnings,
                budget,
                depth + 1,
                CancellationToken.None,
                out var nested);
            budget.Claim(depth);
            nodes.Add(new MapiNode(
                "PartialMultiValueContinuation",
                MapiNodeKind.Array,
                arrayStart,
                reader.Position - arrayStart,
                $"{done:N0} of {pending.RemainingMultiValueElements:N0} remaining element(s)",
                arrayChildren.ToImmutable()));
            return nested;
        }

        return null;
    }

    private static MapiNode MarkReconstructed(MapiNode node, long currentBufferOffset, bool annotate) =>
        node with
        {
            Offset = currentBufferOffset,
            Length = 0,
            Value = annotate
                ? node.Value is null ? "reconstructed across transfer buffers" : $"{node.Value}; reconstructed"
                : node.Value,
            Children = [.. node.Children.Select(child => MarkReconstructed(child, currentBufferOffset, annotate: false))],
        };

    // ---- MS-OXCFXICS structures ------------------------------------------------------------------

    private static MapiNode ParseProgressInformation(
        ref MapiReader reader,
        MapiNodeBudget budget,
        int depth,
        List<string> warnings)
    {
        const int serializedSize = 32;
        var start = reader.Position;
        if (reader.Remaining != serializedSize)
        {
            throw new MapiParseException(
                start,
                $"ProgressInformation must be exactly {serializedSize} bytes, not {reader.Remaining:N0}.");
        }

        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var versionOffset = reader.Position;
        var version = reader.ReadUInt16("Version");
        ExtendedBufferParser.AddField(children, "Version", versionOffset, 2, $"0x{version:X4}", budget);
        if (version != 0)
        {
            throw new MapiParseException(versionOffset, $"ProgressInformation version 0x{version:X4} is not defined.");
        }

        var padding1Offset = reader.Position;
        var padding1 = reader.ReadUInt16("Padding1");
        ExtendedBufferParser.AddField(children, "Padding1", padding1Offset, 2, $"0x{padding1:X4}", budget);
        if (padding1 != 0)
        {
            warnings.Add($"ProgressInformation Padding1 is 0x{padding1:X4}; the protocol recommends zero.");
        }

        AddUInt32(children, "FAIMessageCount", ref reader, budget);
        AddUInt64(children, "FAIMessageTotalSize", ref reader, budget);
        AddUInt32(children, "NormalMessageCount", ref reader, budget);
        var padding2Offset = reader.Position;
        var padding2 = reader.ReadUInt32("Padding2");
        ExtendedBufferParser.AddField(children, "Padding2", padding2Offset, 4, $"0x{padding2:X8}", budget);
        if (padding2 != 0)
        {
            warnings.Add($"ProgressInformation Padding2 is 0x{padding2:X8}; the protocol recommends zero.");
        }
        AddUInt64(children, "NormalMessageTotalSize", ref reader, budget);

        budget.Claim(depth);
        return new MapiNode(
            "ProgressInformation",
            MapiNodeKind.Structure,
            start,
            serializedSize,
            "version 0",
            children.ToImmutable());
    }

    private static MapiNode ParsePropertyGroupInfo(
        ref MapiReader reader,
        MapiNodeBudget budget,
        int depth,
        List<string> warnings)
    {
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        AddUInt32(children, "GroupId", ref reader, budget);
        var reservedOffset = reader.Position;
        var reserved = reader.ReadUInt32("Reserved");
        ExtendedBufferParser.AddField(children, "Reserved", reservedOffset, 4, $"0x{reserved:X8}", budget);
        if (reserved != 0)
        {
            warnings.Add($"PropertyGroupInfo Reserved is 0x{reserved:X8}; the protocol requires zero.");
        }

        var countOffset = reader.Position;
        var count = reader.ReadUInt32("GroupCount");
        ExtendedBufferParser.AddField(children, "GroupCount", countOffset, 4, count.ToString(CultureInfo.InvariantCulture), budget);
        if (count == 0)
        {
            warnings.Add("PropertyGroupInfo GroupCount is zero, contrary to the protocol.");
        }
        if (count > FastTransferLimits.MaxPropertyGroups || count > reader.Remaining / 4)
        {
            throw new MapiParseException(
                countOffset,
                $"PropertyGroupInfo GroupCount {count:N0} exceeds the safe or remaining extent.");
        }

        var groupsStart = reader.Position;
        var groups = ImmutableArray.CreateBuilder<MapiNode>();
        for (var index = 0; index < count; index++)
        {
            groups.Add(ParsePropertyGroup(ref reader, index, budget, depth + 2, warnings));
        }
        budget.Claim(depth + 1);
        children.Add(new MapiNode(
            "Groups",
            MapiNodeKind.Array,
            groupsStart,
            reader.Position - groupsStart,
            $"{count:N0} group(s)",
            groups.ToImmutable()));
        budget.Claim(depth);
        return new MapiNode(
            "PropertyGroupInfo",
            MapiNodeKind.Structure,
            start,
            reader.Position - start,
            $"{count:N0} group(s)",
            children.ToImmutable());
    }

    private static MapiNode ParsePropertyGroup(
        ref MapiReader reader,
        int groupIndex,
        MapiNodeBudget budget,
        int depth,
        List<string> warnings)
    {
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var countOffset = reader.Position;
        var count = reader.ReadUInt32("PropertyTagCount");
        ExtendedBufferParser.AddField(
            children, "PropertyTagCount", countOffset, 4, count.ToString(CultureInfo.InvariantCulture), budget);
        if (count == 0)
        {
            warnings.Add($"PropertyGroup[{groupIndex}] PropertyTagCount is zero, contrary to the protocol.");
        }
        if (count > FastTransferLimits.MaxPropertyTagsPerGroup || count > reader.Remaining / 4)
        {
            throw new MapiParseException(
                countOffset,
                $"PropertyTagCount {count:N0} exceeds the safe or remaining extent.");
        }

        var tagsStart = reader.Position;
        var tags = ImmutableArray.CreateBuilder<MapiNode>();
        for (var index = 0; index < count; index++)
        {
            tags.Add(ParsePropertyTagWithGroupName(ref reader, index, budget, depth + 2, warnings));
        }
        budget.Claim(depth + 1);
        children.Add(new MapiNode(
            "PropertyTags",
            MapiNodeKind.Array,
            tagsStart,
            reader.Position - tagsStart,
            $"{count:N0} tag(s)",
            tags.ToImmutable()));
        budget.Claim(depth);
        return new MapiNode(
            $"PropertyGroup[{groupIndex}]",
            MapiNodeKind.Structure,
            start,
            reader.Position - start,
            $"{count:N0} tag(s)",
            children.ToImmutable());
    }

    private static MapiNode ParsePropertyTagWithGroupName(
        ref MapiReader reader,
        int index,
        MapiNodeBudget budget,
        int depth,
        List<string> warnings)
    {
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var typeOffset = reader.Position;
        var propertyType = reader.ReadUInt16("PropertyType");
        ExtendedBufferParser.AddField(
            children, "PropertyType", typeOffset, 2, NspiPropertyParser.PropertyTypeName(propertyType), budget);
        var idOffset = reader.Position;
        var propertyId = reader.ReadUInt16("PropertyId");
        ExtendedBufferParser.AddField(
            children, "PropertyId", idOffset, 2, MapiPropertyNames.FormatPidTag(propertyId), budget);
        if (propertyId >= 0x8000)
        {
            children.Add(ParseGroupPropertyName(ref reader, budget, depth + 1, warnings));
        }

        budget.Claim(depth);
        return new MapiNode(
            $"PropertyTag[{index}]",
            MapiNodeKind.Property,
            start,
            reader.Position - start,
            $"0x{propertyId:X4}:{propertyType:X4}",
            children.ToImmutable());
    }

    private static MapiNode ParseGroupPropertyName(
        ref MapiReader reader,
        MapiNodeBudget budget,
        int depth,
        List<string> warnings)
    {
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var guidOffset = reader.Position;
        var guid = reader.ReadGuid("PropertySet");
        ExtendedBufferParser.AddField(
            children,
            "PropertySet",
            guidOffset,
            16,
            MapiPropertyNames.PropertySetName(guid) is { } name ? $"{guid} ({name})" : guid.ToString(),
            budget);
        var kindOffset = reader.Position;
        var kind = reader.ReadUInt32("Kind");
        ExtendedBufferParser.AddField(
            children, "Kind", kindOffset, 4, kind switch { 0 => "0x00000000 (LID)", 1 => "0x00000001 (Name)", _ => $"0x{kind:X8}" }, budget);
        switch (kind)
        {
            case 0:
                AddUInt32(children, "LID", ref reader, budget, hex: true);
                break;
            case 1:
            {
                var sizeOffset = reader.Position;
                var size = reader.ReadUInt32("NameSize");
                ExtendedBufferParser.AddField(
                    children, "NameSize", sizeOffset, 4, size.ToString(CultureInfo.InvariantCulture), budget);
                if (size > MapiParseLimits.MaxStringBytes || size > reader.Remaining || (size & 1) != 0)
                {
                    throw new MapiParseException(
                        sizeOffset,
                        $"GroupPropertyName NameSize {size:N0} is odd or exceeds the safe or remaining extent.");
                }
                var valueOffset = reader.Position;
                var bytes = reader.ReadBytes(checked((int)size), "Name");
                children.Add(DecodeUnicodeString(bytes, valueOffset, "Name", budget, warnings));
                break;
            }
            default:
                throw new MapiParseException(
                    kindOffset, $"GroupPropertyName Kind 0x{kind:X8} is not defined.");
        }

        budget.Claim(depth);
        return new MapiNode(
            "GroupPropertyName",
            MapiNodeKind.Structure,
            start,
            reader.Position - start,
            kind == 0 ? "LID" : "Name",
            children.ToImmutable());
    }

    private static void AddUInt32(
        ImmutableArray<MapiNode>.Builder children,
        string name,
        ref MapiReader reader,
        MapiNodeBudget budget,
        bool hex = false)
    {
        var offset = reader.Position;
        var value = reader.ReadUInt32(name);
        ExtendedBufferParser.AddField(
            children,
            name,
            offset,
            4,
            hex ? $"0x{value:X8}" : value.ToString(CultureInfo.InvariantCulture),
            budget);
    }

    private static void AddUInt64(
        ImmutableArray<MapiNode>.Builder children,
        string name,
        ref MapiReader reader,
        MapiNodeBudget budget)
    {
        var offset = reader.Position;
        var value = reader.ReadUInt64(name);
        ExtendedBufferParser.AddField(
            children, name, offset, 8, value.ToString(CultureInfo.InvariantCulture), budget);
    }

    private static MapiNode ParseFolderReplicaInfo(
        ref MapiReader reader,
        MapiNodeBudget budget,
        int depth,
        List<string> warnings)
    {
        budget.Claim(depth);
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var flagsOffset = reader.Position;
        var flags = reader.ReadUInt32("FolderReplicaInfo.Flags");
        ExtendedBufferParser.AddField(children, "Flags", flagsOffset, 4, $"0x{flags:X8}", budget);
        var depthOffset = reader.Position;
        var folderDepth = reader.ReadUInt32("FolderReplicaInfo.Depth");
        ExtendedBufferParser.AddField(
            children, "Depth", depthOffset, 4, folderDepth.ToString(CultureInfo.InvariantCulture), budget);
        children.Add(ParseLongTermId(ref reader, "FolderLongTermId", budget, depth + 1));
        var serverCountOffset = reader.Position;
        var serverCount = reader.ReadCount32("FolderReplicaInfo.ServerDNCount");
        ExtendedBufferParser.AddField(
            children, "ServerDNCount", serverCountOffset, 4, serverCount.ToString(CultureInfo.InvariantCulture), budget);
        var cheapOffset = reader.Position;
        var cheapCount = reader.ReadUInt32("FolderReplicaInfo.CheapServerDNCount");
        ExtendedBufferParser.AddField(
            children, "CheapServerDNCount", cheapOffset, 4, cheapCount.ToString(CultureInfo.InvariantCulture), budget);
        if (serverCount > reader.Remaining)
        {
            throw new MapiParseException(
                serverCountOffset,
                $"FolderReplicaInfo.ServerDNCount {serverCount:N0} exceeds the {reader.Remaining:N0} remaining byte(s).");
        }
        for (var index = 0; index < serverCount; index++)
        {
            var dnOffset = reader.Position;
            var dn = reader.ReadNullTerminatedAscii($"FolderReplicaInfo.ServerDNArray[{index}]");
            ExtendedBufferParser.AddField(
                children, $"ServerDNArray[{index}]", dnOffset, reader.Position - dnOffset, dn, budget);
        }
        _ = warnings;
        budget.Claim(depth);
        return new MapiNode(
            "FolderReplicaInfo",
            MapiNodeKind.Structure,
            start,
            reader.Position - start,
            $"{serverCount:N0} server DN(s)",
            children.ToImmutable());
    }

    private static MapiNode ParseLongTermId(
        ref MapiReader reader,
        string name,
        MapiNodeBudget budget,
        int depth)
    {
        budget.Claim(depth);
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var guidOffset = reader.Position;
        var databaseGuid = reader.ReadGuid($"{name}.DatabaseGuid");
        ExtendedBufferParser.AddField(children, "DatabaseGuid", guidOffset, 16, databaseGuid.ToString(), budget);
        var counterOffset = reader.Position;
        var counter = reader.ReadBytes(6, $"{name}.GlobalCounter");
        var counterHex = Convert.ToHexString(counter);
        ExtendedBufferParser.AddField(children, "GlobalCounter", counterOffset, 6, counterHex, budget);
        var padOffset = reader.Position;
        var pad = reader.ReadUInt16($"{name}.Pad");
        ExtendedBufferParser.AddField(children, "Pad", padOffset, 2, $"0x{pad:X4}", budget);
        return new MapiNode(
            name,
            MapiNodeKind.Structure,
            start,
            reader.Position - start,
            $"{databaseGuid}-{counterHex}",
            children.ToImmutable());
    }

    private static MapiNode ParseXid(ref MapiReader reader, int length, MapiNodeBudget budget, int depth)
    {
        budget.Claim(depth);
        var start = reader.Position;
        if (length < 17 || length > 24)
        {
            throw new MapiParseException(
                start,
                $"XID length {length} is outside the 17..24 byte range required by MS-OXCFXICS 2.2.2.2.");
        }
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var guidOffset = reader.Position;
        var namespaceGuid = reader.ReadGuid("XID.NamespaceGuid");
        ExtendedBufferParser.AddField(children, "NamespaceGuid", guidOffset, 16, namespaceGuid.ToString(), budget);
        var localOffset = reader.Position;
        var localId = reader.ReadBytes(length - 16, "XID.LocalId");
        var localHex = Convert.ToHexString(localId);
        ExtendedBufferParser.AddField(children, "LocalId", localOffset, localId.Length, localHex, budget);
        return new MapiNode(
            "XID",
            MapiNodeKind.Structure,
            start,
            reader.Position - start,
            $"{namespaceGuid}-{localHex}",
            children.ToImmutable());
    }

    private static MapiNode ParsePredecessorChangeList(ref MapiReader reader, MapiNodeBudget budget, int depth)
    {
        budget.Claim(depth);
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var index = 0;
        while (!reader.End)
        {
            if (index >= FastTransferLimits.MaxSizedXidEntries)
            {
                throw new MapiParseException(
                    reader.Position,
                    $"PredecessorChangeList exceeds {FastTransferLimits.MaxSizedXidEntries:N0} SizedXid entries.");
            }
            budget.Claim(depth + 1);
            var entryStart = reader.Position;
            var size = reader.ReadByte($"SizedXid[{index}].XidSize");
            var entryChildren = ImmutableArray.CreateBuilder<MapiNode>();
            ExtendedBufferParser.AddField(
                entryChildren, "XidSize", entryStart, 1, size.ToString(CultureInfo.InvariantCulture), budget);
            entryChildren.Add(ParseXid(ref reader, size, budget, depth + 2));
            children.Add(new MapiNode(
                $"SizedXid[{index}]",
                MapiNodeKind.Structure,
                entryStart,
                reader.Position - entryStart,
                null,
                entryChildren.ToImmutable()));
            index++;
        }
        budget.Claim(depth);
        return new MapiNode(
            "PredecessorChangeList",
            MapiNodeKind.Array,
            start,
            reader.Position - start,
            $"{index:N0} SizedXid entr{(index == 1 ? "y" : "ies")}",
            children.ToImmutable());
    }

    private static MapiNode ParseIdsetList(
        ref MapiReader reader,
        bool replicaGuid,
        MapiNodeBudget budget,
        int depth)
    {
        budget.Claim(depth);
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var index = 0;
        while (!reader.End)
        {
            if (index >= FastTransferLimits.MaxSizedXidEntries)
            {
                throw new MapiParseException(
                    reader.Position,
                    $"Serialized IDSET exceeds {FastTransferLimits.MaxSizedXidEntries:N0} entries.");
            }
            budget.Claim(depth + 1);
            var entryStart = reader.Position;
            var entryChildren = ImmutableArray.CreateBuilder<MapiNode>();
            string label;
            if (replicaGuid)
            {
                var guidOffset = reader.Position;
                var guid = reader.ReadGuid($"IDSET_REPLGUID[{index}].REPLGUID");
                ExtendedBufferParser.AddField(entryChildren, "REPLGUID", guidOffset, 16, guid.ToString(), budget);
                label = guid.ToString();
            }
            else
            {
                var replIdOffset = reader.Position;
                var replId = reader.ReadUInt16($"IDSET_REPLID[{index}].REPLID");
                ExtendedBufferParser.AddField(
                    entryChildren, "REPLID", replIdOffset, 2, $"{replId} (0x{replId:X4})", budget);
                label = replId.ToString(CultureInfo.InvariantCulture);
            }
            entryChildren.Add(ParseGlobset(ref reader, budget, depth + 2));
            children.Add(new MapiNode(
                replicaGuid ? $"IDSET_REPLGUID[{index}]" : $"IDSET_REPLID[{index}]",
                MapiNodeKind.Structure,
                entryStart,
                reader.Position - entryStart,
                label,
                entryChildren.ToImmutable()));
            index++;
        }
        budget.Claim(depth);
        return new MapiNode(
            replicaGuid ? "IDSET_REPLGUID list" : "IDSET_REPLID list",
            MapiNodeKind.Array,
            start,
            reader.Position - start,
            $"{index:N0} entr{(index == 1 ? "y" : "ies")}",
            children.ToImmutable());
    }

    /// <summary>
    /// Decodes a GLOBSET command stream (MS-OXCFXICS 2.2.2.6). The common-prefix stack is tracked so
    /// Range commands can size their bounds; unlike the pinned upstream, popping an empty stack and
    /// pushes that complete a six-byte prefix are handled instead of faulting.
    /// </summary>
    private static MapiNode ParseGlobset(ref MapiReader reader, MapiNodeBudget budget, int depth)
    {
        budget.Claim(depth);
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var stack = new List<int>();
        var prefixLength = 0;
        var commands = 0;
        while (true)
        {
            if (commands >= FastTransferLimits.MaxGlobsetCommands)
            {
                throw new MapiParseException(
                    reader.Position,
                    $"GLOBSET exceeds {FastTransferLimits.MaxGlobsetCommands:N0} commands.");
            }
            var commandOffset = reader.Position;
            var command = reader.ReadByte("GLOBSET.Command");
            commands++;
            switch (command)
            {
                case 0x00:
                    ExtendedBufferParser.AddField(children, $"[{commands - 1}] End", commandOffset, 1, "0x00", budget);
                    budget.Claim(depth);
                    return new MapiNode(
                        "GLOBSET",
                        MapiNodeKind.Array,
                        start,
                        reader.Position - start,
                        $"{commands:N0} command(s)",
                        children.ToImmutable());
                case >= 0x01 and <= 0x06:
                {
                    var completedLength = prefixLength + command;
                    if (completedLength > 6)
                    {
                        throw new MapiParseException(
                            commandOffset,
                            $"GLOBSET push of {command} byte(s) would exceed the six-byte common prefix.");
                    }
                    var bytes = reader.ReadBytes(command, "GLOBSET.CommonBytes");
                    var completesGlobcnt = completedLength == 6;
                    ExtendedBufferParser.AddField(
                        children,
                        $"[{commands - 1}] Push",
                        commandOffset,
                        1 + command,
                        $"{command} byte(s): {Convert.ToHexString(bytes)}" +
                        (completesGlobcnt ? "; completes one GLOBCNT" : string.Empty),
                        budget);
                    if (!completesGlobcnt)
                    {
                        stack.Add(command);
                        prefixLength = completedLength;
                    }
                    break;
                }
                case 0x42:
                {
                    var startValue = reader.ReadByte("GLOBSET.StartValue");
                    var bitmask = reader.ReadByte("GLOBSET.Bitmask");
                    ExtendedBufferParser.AddField(
                        children,
                        $"[{commands - 1}] Bitmask",
                        commandOffset,
                        3,
                        $"StartValue 0x{startValue:X2}, Bitmask 0x{bitmask:X2}",
                        budget);
                    break;
                }
                case 0x50:
                {
                    if (stack.Count == 0)
                    {
                        throw new MapiParseException(
                            commandOffset, "GLOBSET pop command has no matching push command.");
                    }
                    prefixLength -= stack[^1];
                    stack.RemoveAt(stack.Count - 1);
                    ExtendedBufferParser.AddField(
                        children, $"[{commands - 1}] Pop", commandOffset, 1, "0x50", budget);
                    break;
                }
                case 0x52:
                {
                    var width = 6 - prefixLength;
                    if (width <= 0)
                    {
                        throw new MapiParseException(
                            commandOffset, "GLOBSET range command has no remaining GLOBCNT bytes to range over.");
                    }
                    var low = reader.ReadBytes(width, "GLOBSET.LowValue");
                    var lowHex = Convert.ToHexString(low);
                    var high = reader.ReadBytes(width, "GLOBSET.HighValue");
                    ExtendedBufferParser.AddField(
                        children,
                        $"[{commands - 1}] Range",
                        commandOffset,
                        1 + (2 * width),
                        $"{lowHex}..{Convert.ToHexString(high)}",
                        budget);
                    break;
                }
                default:
                    throw new MapiParseException(
                        commandOffset, $"GLOBSET command 0x{command:X2} is not defined by MS-OXCFXICS 2.2.2.6.");
            }
        }
    }

    // ---- Structure decoders reused by the FastTransfer ROP family --------------------------------

    /// <summary>Decodes a PredecessorChangeList (MS-OXCFXICS 2.2.2.3) from the whole reader extent.</summary>
    internal static MapiNode ParsePredecessorChangeListValue(
        ref MapiReader reader, MapiNodeBudget budget, int depth) =>
        ParsePredecessorChangeList(ref reader, budget, depth);

    /// <summary>Decodes an XID (MS-OXCFXICS 2.2.2.2) of the given total byte length.</summary>
    internal static MapiNode ParseXidValue(
        ref MapiReader reader, int length, MapiNodeBudget budget, int depth) =>
        ParseXid(ref reader, length, budget, depth);

    /// <summary>Decodes a 24-byte LongTermID (MS-OXCDATA 2.2.1.3.1).</summary>
    internal static MapiNode ParseLongTermIdValue(
        ref MapiReader reader, string name, MapiNodeBudget budget, int depth) =>
        ParseLongTermId(ref reader, name, budget, depth);

    /// <summary>Decodes a persisted ICS-state IDSET/CNSET in REPLGUID form.</summary>
    internal static MapiNode ParseIdsetReplGuidValue(
        ref MapiReader reader, MapiNodeBudget budget, int depth) =>
        ParseIdsetList(ref reader, replicaGuid: true, budget, depth);

    // ---- Shared helpers --------------------------------------------------------------------------

    private static void AppendUnconsumed(
        ref MapiReader slice,
        ImmutableArray<MapiNode>.Builder children,
        string name,
        MapiNodeBudget budget,
        List<string> warnings)
    {
        if (slice.End)
        {
            return;
        }
        var offset = slice.Position;
        var bytes = slice.ReadRemaining(name);
        children.Add(ExtendedBufferParser.RawNode(name, bytes, offset, budget));
        warnings.Add($"{name}: {bytes.Length:N0} byte(s) were left unparsed inside the declared value extent.");
    }

    private static long ReadVarLength(ref MapiReader reader, string field)
    {
        var offset = reader.Position;
        var value = reader.ReadUInt32(field);
        if (value == ObjectLengthSentinel)
        {
            return value;
        }
        if (value > FastTransferLimits.MaxVarValueBytes)
        {
            throw new MapiParseException(
                offset, $"{field} {value:N0} exceeds the {FastTransferLimits.MaxVarValueBytes:N0}-byte limit.");
        }
        return value;
    }

    private static MapiNode DecodeUnicodeString(
        ReadOnlySpan<byte> bytes,
        long offset,
        string name,
        MapiNodeBudget budget,
        List<string> warnings)
    {
        if ((bytes.Length & 1) != 0)
        {
            warnings.Add($"{name} has an odd UTF-16LE length of {bytes.Length:N0} byte(s); it was retained as raw.");
            return ExtendedBufferParser.RawNode(name, bytes, offset, budget);
        }
        var trimmed = bytes;
        if (trimmed.Length >= 2 && trimmed[^1] == 0 && trimmed[^2] == 0)
        {
            trimmed = trimmed[..^2];
        }
        try
        {
            var text = new UnicodeEncoding(false, false, true).GetString(trimmed);
            budget.Claim(0);
            return MapiNode.Leaf(name, MapiNodeKind.Field, offset, bytes.Length, text);
        }
        catch (DecoderFallbackException)
        {
            warnings.Add($"{name} is not valid UTF-16LE; it was retained as raw.");
            return ExtendedBufferParser.RawNode(name, bytes, offset, budget);
        }
    }

    private static MapiNode DecodeString8(
        ReadOnlySpan<byte> bytes,
        long offset,
        string name,
        MapiNodeBudget budget,
        List<string> warnings)
    {
        var trimmed = bytes;
        if (trimmed.Length >= 1 && trimmed[^1] == 0)
        {
            trimmed = trimmed[..^1];
        }
        foreach (var value in trimmed)
        {
            if (value >= 0x80)
            {
                // The FastTransfer SendOptions code page is negotiated outside this buffer, so a
                // non-ASCII 8-bit string is never guessed at: the bytes are retained verbatim.
                warnings.Add(
                    $"{name} contains non-ASCII 8-bit characters and no code page is available in the transfer " +
                    "buffer; the bytes were retained as raw.");
                return ExtendedBufferParser.RawNode(name, bytes, offset, budget);
            }
        }
        budget.Claim(0);
        return MapiNode.Leaf(name, MapiNodeKind.Field, offset, bytes.Length, Encoding.ASCII.GetString(trimmed));
    }

    private static bool TryFormatFileTime(long value, out string formatted)
    {
        formatted = string.Empty;
        if (value < 0)
        {
            return false;
        }
        try
        {
            formatted = DateTime.FromFileTimeUtc(value).ToString("O", CultureInfo.InvariantCulture);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static string DescribeType(ushort propertyType) =>
        CodePageTypeNames.TryGetValue(propertyType, out var name)
            ? $"0x{propertyType:X4} {name}"
            : NspiPropertyParser.PropertyTypeName(propertyType);

    private static string DescribePropertyId(ushort propertyId, uint tag, uint? specialMarker = null)
    {
        if (propertyId == 0 && specialMarker is 0x4074000B or 0x407B0102)
        {
            return specialMarker == 0x4074000B
                ? "0x0000 ProgressInformation (special)"
                : "0x0000 PropertyGroupInfo (special)";
        }
        if (IcsPropertyNames.TryGetValue(tag, out var icsName))
        {
            return $"0x{propertyId:X4} {icsName}";
        }
        return propertyId >= 0x8000
            ? $"0x{propertyId:X4} (named property)"
            : MapiPropertyNames.FormatPidTag(propertyId);
    }

    // ---- Type classification ---------------------------------------------------------------------

    internal static bool IsFixedType(ushort propertyType) => propertyType is
        0x0002 or 0x0003 or 0x0004 or 0x0005 or 0x0006 or 0x0007 or 0x000A or 0x000B or 0x0014 or 0x0040 or 0x0048;

    internal static bool IsVarType(ushort propertyType) => propertyType is
        0x000D or 0x001E or 0x001F or 0x00FB or 0x0102;

    internal static bool IsCodePageType(ushort propertyType) => CodePageTypeNames.ContainsKey(propertyType);

    internal static bool IsMultiValueType(ushort propertyType) => propertyType is
        0x1002 or 0x1003 or 0x1004 or 0x1005 or 0x1006 or 0x1007 or 0x1014 or 0x1040 or 0x1048
        or 0x101E or 0x101F or 0x1102;

    internal static bool IsMarker(uint tag) => MarkerNames.ContainsKey(tag);

    internal static bool IsMetaProperty(uint tag) => MetaPropertyNames.ContainsKey(tag);

    private static bool TryGetOpeningFrame(uint tag, out FastTransferSyntaxFrame frame)
    {
        frame = tag switch
        {
            0x40090003 => new(tag, 0x400B0003, "TopFolder"),
            0x400A0003 => new(tag, 0x400B0003, "SubFolder"),
            0x400C0003 => new(tag, 0x400D0003, "Message"),
            0x40100003 => new(tag, 0x400D0003, "FAI Message"),
            0x40010003 => new(tag, 0x40020003, "EmbeddedMessage"),
            0x40030003 => new(tag, 0x40040003, "Recipient"),
            0x40000003 => new(tag, 0x400E0003, "Attachment"),
            0x403A0003 => new(tag, 0x403B0003, "State"),
            _ => default,
        };
        return frame != default;
    }

    private static bool IsClosingMarker(uint tag) => tag is
        0x400B0003 or 0x400D0003 or 0x40020003 or 0x40040003 or 0x400E0003 or 0x403B0003;

    private static readonly ImmutableDictionary<uint, string> StandaloneProductionNames =
        new Dictionary<uint, string>
        {
            [0x40120003] = "IncrementalSyncChange",
            [0x407D0003] = "IncrementalSyncChangePartial",
            [0x40130003] = "Deletions",
            [0x40140003] = "IncrementalSyncEnd",
            [0x402F0003] = "ReadStateChanges",
            [0x4074000B] = "ProgressTotal",
            [0x4075000B] = "ProgressPerMessage",
            [0x40150003] = "IncrementalSyncMessage",
            [0x407B0102] = "GroupInfo",
            [0x40180003] = "ErrorInfo",
        }.ToImmutableDictionary();

    // ---- Tag catalogs ----------------------------------------------------------------------------
    // MS-OXCFXICS 2.2.4.1.4 (markers) and 2.2.4.1.5 (meta-properties), cross-checked against the
    // pinned upstream Markers/MetaProperties enumerations. All tables are immutable and built once.

    internal static readonly ImmutableDictionary<uint, string> MarkerNames =
        new Dictionary<uint, string>
        {
            [0x40090003] = "StartTopFld",
            [0x400A0003] = "StartSubFld",
            [0x400B0003] = "EndFolder",
            [0x400C0003] = "StartMessage",
            [0x400D0003] = "EndMessage",
            [0x40100003] = "StartFAIMsg",
            [0x40010003] = "StartEmbed",
            [0x40020003] = "EndEmbed",
            [0x40030003] = "StartRecip",
            [0x40040003] = "EndToRecip",
            [0x40000003] = "NewAttach",
            [0x400E0003] = "EndAttach",
            [0x40120003] = "IncrSyncChg",
            [0x407D0003] = "IncrSyncChgPartial",
            [0x40130003] = "IncrSyncDel",
            [0x40140003] = "IncrSyncEnd",
            [0x402F0003] = "IncrSyncRead",
            [0x403A0003] = "IncrSyncStateBegin",
            [0x403B0003] = "IncrSyncStateEnd",
            [0x4074000B] = "IncrSyncProgressMode",
            [0x4075000B] = "IncrSyncProgressPerMsg",
            [0x40150003] = "IncrSyncMessage",
            [0x407B0102] = "IncrSyncGroupInfo",
            [0x40180003] = "FXErrorInfo",
        }.ToImmutableDictionary();

    internal static readonly ImmutableDictionary<uint, string> MetaPropertyNames =
        new Dictionary<uint, string>
        {
            [0x4008001E] = "MetaTagDnPrefix",
            [0x400F0003] = "MetaTagEcWarning",
            [0x40110102] = "MetaTagNewFXFolder",
            [0x40160003] = "MetaTagFXDelProp",
            [0x407A0003] = "MetaTagIncrementalSyncMessagePartial",
            [0x407C0003] = "MetaTagIncrSyncGroupId",
        }.ToImmutableDictionary();

    /// <summary>
    /// ICS meta-property tags that are lexed as ordinary propValue elements (they are not part of the
    /// 2.2.4.1.5 meta-property set but do not appear in the shared PidTag catalog either).
    /// </summary>
    internal static readonly ImmutableDictionary<uint, string> IcsPropertyNames =
        new Dictionary<uint, string>
        {
            [0x40170003] = "MetaTagIdsetGiven",
            [0x40210102] = "MetaTagIdsetNoLongerInScope",
            [0x402D0102] = "MetaTagIdsetRead",
            [0x402E0102] = "MetaTagIdsetUnread",
            [0x67930102] = "MetaTagIdsetExpired",
            [0x67E50102] = "MetaTagIdsetDeleted",
            [0x67960102] = "MetaTagCnsetSeen",
            [0x67DA0102] = "MetaTagCnsetSeenFAI",
            [0x67D20102] = "MetaTagCnsetRead",
        }.ToImmutableDictionary();

    internal static readonly ImmutableDictionary<ushort, string> CodePageTypeNames =
        new Dictionary<ushort, string>
        {
            [0x84B0] = "PtypCodePageUnicode",
            [0x84B1] = "PtypCodePageUnicodeBigendian",
            [0x84E4] = "PtypCodePageWesternEuropean",
            [0x94B0] = "PtypCodePageUnicode52",
        }.ToImmutableDictionary();
}
