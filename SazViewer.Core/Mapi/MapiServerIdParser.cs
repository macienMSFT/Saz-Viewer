using System.Collections.Immutable;
using System.Globalization;

namespace SazViewer.Core;

internal static class MapiServerIdParser
{
    private const int ServerDefinedPayloadSize = 21;

    public static MapiNode ParseCounted16(
        ref MapiReader reader,
        string name,
        MapiNodeBudget budget,
        List<string>? warnings = null,
        int depth = 0)
    {
        budget.Claim(depth, 0);
        var start = reader.Position;
        var countOffset = reader.Position;
        var count = reader.ReadCount16($"{name}.Count");
        var children = ImmutableArray.CreateBuilder<MapiNode>(2);
        ExtendedBufferParser.AddField(
            children,
            "Count",
            countOffset,
            2,
            count.ToString(CultureInfo.InvariantCulture),
            budget);
        var payload = reader.SliceReader(count, $"{name}.ServerId");
        children.Add(ParsePayload(ref payload, "ServerId", budget, warnings, depth + 1));
        budget.Claim(depth);
        return new MapiNode(
            name,
            MapiNodeKind.Property,
            start,
            reader.Position - start,
            children[^1].Value ?? $"{count:N0} byte(s)",
            children.ToImmutable());
    }

    public static MapiNode ParsePayload(
        ref MapiReader reader,
        string name,
        MapiNodeBudget budget,
        List<string>? warnings = null,
        int depth = 0)
    {
        budget.Claim(depth, 0);
        var start = reader.Position;
        var length = reader.Remaining;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        if (length == 0)
        {
            children.Add(ExtendedBufferParser.RawNode(
                "Malformed ServerId",
                reader.ReadRemaining($"{name}.Malformed"),
                start,
                budget));
            warnings?.Add($"{name} is empty; its bounded payload was retained raw.");
            budget.Claim(depth);
            return new MapiNode(
                name,
                MapiNodeKind.Structure,
                start,
                0,
                "malformed",
                children.ToImmutable());
        }
        var oursOffset = reader.Position;
        var ours = reader.ReadByte($"{name}.Ours");
        ExtendedBufferParser.AddField(
            children,
            "Ours",
            oursOffset,
            1,
            ours == 1 ? "0x01 (server-defined)" : $"0x{ours:X2} (client-defined)",
            budget);
        if (ours == 1)
        {
            if (length != ServerDefinedPayloadSize)
            {
                var malformedOffset = reader.Position;
                var malformed = reader.ReadRemaining($"{name}.MalformedServerData");
                children.Add(ExtendedBufferParser.RawNode(
                    "Malformed server-defined data",
                    malformed,
                    malformedOffset,
                    budget));
                warnings?.Add(
                    $"{name} has Ours=0x01 and declares {length:N0} bytes; {ServerDefinedPayloadSize} are required. " +
                    "Its bounded payload was retained raw.");
                budget.Claim(depth);
                return new MapiNode(
                    name,
                    MapiNodeKind.Structure,
                    start,
                    reader.Position - start,
                    "malformed server-defined",
                    children.ToImmutable());
            }
            children.Add(ParseFixedId(ref reader, "FolderId", budget, depth + 1));
            children.Add(ParseFixedId(ref reader, "MessageId", budget, depth + 1));
            var instanceOffset = reader.Position;
            var instance = reader.ReadUInt32($"{name}.Instance");
            ExtendedBufferParser.AddField(
                children,
                "Instance",
                instanceOffset,
                4,
                instance.ToString(CultureInfo.InvariantCulture),
                budget);
            budget.Claim(depth);
            return new MapiNode(
                name,
                MapiNodeKind.Structure,
                start,
                reader.Position - start,
                "server-defined",
                children.ToImmutable());
        }

        var clientDataOffset = reader.Position;
        var clientData = reader.ReadRemaining($"{name}.ClientData");
        children.Add(ExtendedBufferParser.RawNode("ClientData", clientData, clientDataOffset, budget));
        if (ours is not 0)
        {
            warnings?.Add($"{name}.Ours has reserved value 0x{ours:X2}; its remaining client-defined bytes were retained raw.");
        }
        budget.Claim(depth);
        return new MapiNode(
            name,
            MapiNodeKind.Structure,
            start,
            reader.Position - start,
            "client-defined",
            children.ToImmutable());
    }

    private static MapiNode ParseFixedId(
        ref MapiReader reader,
        string name,
        MapiNodeBudget budget,
        int depth)
    {
        budget.Claim(depth, 0);
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>(2);
        var replicaOffset = reader.Position;
        var replicaId = reader.ReadUInt16($"{name}.ReplicaId");
        ExtendedBufferParser.AddField(
            children,
            "ReplicaId",
            replicaOffset,
            2,
            $"0x{replicaId:X4}",
            budget);
        var counterOffset = reader.Position;
        var counter = reader.ReadBytes(6, $"{name}.GlobalCounter");
        ExtendedBufferParser.AddField(
            children,
            "GlobalCounter",
            counterOffset,
            counter.Length,
            $"0x{Convert.ToHexString(counter)}",
            budget);
        budget.Claim(depth);
        return new MapiNode(
            name,
            MapiNodeKind.Structure,
            start,
            reader.Position - start,
            null,
            children.ToImmutable());
    }
}
