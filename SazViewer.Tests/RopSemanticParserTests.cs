using System.Buffers.Binary;
using System.Collections.Immutable;
using SazViewer.Core;

namespace SazViewer.Tests;

/// <summary>
/// Exhaustive synthetic coverage for the first semantic ROP dispatcher stage
/// (<see cref="RopSemanticParser"/> and its integration into <see cref="RopBufferParser"/>). These
/// tests are entirely synthetic byte buffers - they do not touch AUX parsing, MapiParserTests.cs, or
/// any parity fixtures. Every implemented request and response schema is exercised at least once
/// through the real public entry point (<see cref="RopBufferParser.Parse"/>) or the dispatcher's own
/// entry point (<see cref="RopSemanticParser.ParseOperations"/>).
/// </summary>
public sealed class RopSemanticParserTests
{
    // ---------------------------------------------------------------------------------------------
    // Multi-operation boundary parsing (the core of the "first semantic stage").
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ParsesMultipleChainedRequestOperationsWithDistinctFixedWidths()
    {
        var ropList = Concat(
            [0x01, 0x00, 0x00], // RopRelease: RopId, LogonId, InputHandleIndex
            [0x16, 0x00, 0x01], // RopGetStatus
            Concat([0x2F, 0x00, 0x02], Le((ulong)4096))); // RopSetStreamSize + StreamSize

        var buffer = Frame(ropList, 0x11111111u, 0x22222222u, 0x33333333u);
        var warnings = new List<string>();
        var nodes = RopBufferParser.Parse(buffer, 0, MapiDirection.Request, warnings, new MapiNodeBudget(), CancellationToken.None);

        Assert.Empty(warnings);
        var ropListNode = Find(nodes, "ROP list");
        Assert.Equal(3, ropListNode.Children.Length);
        Assert.Equal("Operation 0", ropListNode.Children[0].Name);
        Assert.StartsWith("RopRelease", ropListNode.Children[0].Value);
        Assert.Equal("Operation 1", ropListNode.Children[1].Name);
        Assert.StartsWith("RopGetStatus", ropListNode.Children[1].Value);
        Assert.Equal("Operation 2", ropListNode.Children[2].Name);
        Assert.StartsWith("RopSetStreamSize", ropListNode.Children[2].Value);
        Assert.Equal("4096", Find(ropListNode.Children[2].Children, "StreamSize").Value);

        var handleTable = Find(nodes, "Server object handle table");
        Assert.Equal(3, handleTable.Children.Length);
        Assert.Equal("0x11111111", handleTable.Children[0].Value);
        Assert.Equal("0x22222222", handleTable.Children[1].Value);
        Assert.Equal("0x33333333", handleTable.Children[2].Value);
    }

    [Fact]
    public void ParsesMultipleChainedResponseOperationsAndRespectsReturnValueBoundaries()
    {
        var ropList = Concat(
            [0x0E, 0x00, 0x00, 0x00, 0x00, 0x00], // RopModifyRecipients: success, universal 6 bytes
            Concat([0x5E, 0x01, 0x00, 0x00, 0x00, 0x00], Le((uint)8192)), // RopGetStreamSize: success -> +4
            Concat([0x16, 0x02], Le(0x80000001))); // RopGetStatus: failure -> stays at 6 bytes

        var buffer = Frame(ropList, 0x00u, 0x01u, 0x02u);
        var warnings = new List<string>();
        var nodes = RopBufferParser.Parse(buffer, 0, MapiDirection.Response, warnings, new MapiNodeBudget(), CancellationToken.None);

        Assert.Empty(warnings);
        var ropListNode = Find(nodes, "ROP list");
        Assert.Equal(3, ropListNode.Children.Length);
        Assert.Equal("8192", Find(ropListNode.Children[1].Children, "StreamSize").Value);
        Assert.Equal(4, ropListNode.Children[1].Children.Length);

        // The failed RopGetStatus response must not gain a TableStatus byte, and the operation must
        // still consume exactly 6 bytes so the boundary walk lands correctly (proven implicitly by
        // there being no leftover raw/undecoded operation and no warnings at all).
        Assert.Equal(3, ropListNode.Children[2].Children.Length);
        Assert.DoesNotContain(ropListNode.Children[2].Children, child => child.Name == "TableStatus");
    }

    // ---------------------------------------------------------------------------------------------
    // Hostile input hardening: unknown RopIds, known-but-unimplemented RopIds, and truncation must
    // never guess an operation boundary and must fall back to raw transactionally.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void StopsAtGenuinelyUnknownRopIdAndRetainsRopIdFieldPlusRemainderAsRaw()
    {
        byte[] ropList = [0x01, 0x00, 0x00, 0xC8, 0xAA, 0xBB, 0xCC]; // RopRelease, then unknown 0xC8 + trailing bytes
        var buffer = Frame(ropList);
        var warnings = new List<string>();
        var nodes = RopBufferParser.Parse(buffer, 0, MapiDirection.Request, warnings, new MapiNodeBudget(), CancellationToken.None);

        var ropListNode = Find(nodes, "ROP list");
        Assert.Equal(2, ropListNode.Children.Length);
        Assert.Equal("Operation 0", ropListNode.Children[0].Name);
        Assert.Equal("Operation 1 (undecoded)", ropListNode.Children[1].Name);
        Assert.Equal("0xC8 (Unknown ROP)", Find(ropListNode.Children[1].Children, "RopId").Value);
        Assert.Equal(MapiNodeKind.Raw, Find(ropListNode.Children[1].Children, "Operation bytes").Kind);
        Assert.Contains(
            warnings,
            w => w.Contains("individual ROP fields", StringComparison.Ordinal) &&
                 w.Contains("not recognized", StringComparison.Ordinal));
    }

    [Fact]
    public void StopsAtKnownButUnimplementedRopIdWithoutGuessingFurtherOperationBoundaries()
    {
        // RopOpenFolder (0x02) is a real, named RopId whose request is variable-length (a FolderId
        // plus a boolean), so it is intentionally not in the fixed-width catalog. Bytes after it must
        // never be interpreted as another operation.
        byte[] ropList = [0x01, 0x00, 0x00, 0x02, 0x00, 0x00, 0x01, 0x00];
        var buffer = Frame(ropList);
        var warnings = new List<string>();
        var nodes = RopBufferParser.Parse(buffer, 0, MapiDirection.Request, warnings, new MapiNodeBudget(), CancellationToken.None);

        var ropListNode = Find(nodes, "ROP list");
        Assert.Equal(2, ropListNode.Children.Length);
        Assert.Equal("0x02 (RopOpenFolder)", Find(ropListNode.Children[1].Children, "RopId").Value);
        Assert.Contains(
            warnings,
            w => w.Contains("no fixed-width request schema is implemented", StringComparison.Ordinal));
    }

    [Fact]
    public void TruncatedOperationFallsBackToRawWithoutLosingPriorDecodedOperations()
    {
        // RopRelease (3 bytes, decodes cleanly) followed by a RopSetStreamSize that needs 11 bytes but
        // only has 5 remaining.
        byte[] ropList = [0x01, 0x00, 0x00, 0x2F, 0x00, 0x00, 0xAA, 0xBB];
        var buffer = Frame(ropList);
        var warnings = new List<string>();
        var nodes = RopBufferParser.Parse(buffer, 0, MapiDirection.Request, warnings, new MapiNodeBudget(), CancellationToken.None);

        var ropListNode = Find(nodes, "ROP list");
        Assert.Equal(2, ropListNode.Children.Length);
        Assert.Equal("Operation 0", ropListNode.Children[0].Name);
        Assert.Equal("Operation 1 (malformed)", ropListNode.Children[1].Name);
        Assert.Equal(MapiNodeKind.Raw, ropListNode.Children[1].Kind);
        Assert.Equal(5, ropListNode.Children[1].Length);
        Assert.Contains(warnings, w => w.Contains("could not be parsed", StringComparison.Ordinal));
    }

    [Fact]
    public void NodeBudgetLimitsRunawayOperationDecodingAndFallsBackSafely()
    {
        const int repeatCount = 9000; // 4 node claims/op * 9000 comfortably exceeds MaxNodes (25,000).
        var ropList = new byte[repeatCount * 3];
        for (var i = 0; i < repeatCount; i++)
        {
            ropList[i * 3] = 0x01; // RopRelease
        }

        var warnings = new List<string>();
        var handleReferences = new List<RopHandleReference>();
        var ops = RopSemanticParser.ParseOperations(
            ropList, 0, MapiDirection.Request, warnings, new MapiNodeBudget(), handleReferences, CancellationToken.None);

        Assert.True(ops.Length < repeatCount, "The node budget must stop decoding before every operation is processed.");
        Assert.Equal(MapiNodeKind.Raw, ops[^1].Kind);
        Assert.Contains(
            warnings,
            w => w.Contains("exceeds", StringComparison.OrdinalIgnoreCase) && w.Contains("nodes", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void HonorsCancellationRequestedBeforeDecodingAnyOperation()
    {
        byte[] ropList = [0x01, 0x00, 0x00, 0x16, 0x00, 0x00];
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            RopSemanticParser.ParseOperations(
                ropList, 0, MapiDirection.Request, new List<string>(), new MapiNodeBudget(), new List<RopHandleReference>(), cts.Token));
    }

    // ---------------------------------------------------------------------------------------------
    // ReturnValue-gated responses: exercise all three width tiers of RopSetMessageReadFlag explicitly,
    // each immediately followed by a sentinel operation to prove the boundary was not over/under-read.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void SetMessageReadFlagResponseFailureStaysAtSixBytesAndNextOperationAligns()
    {
        var ropList = Concat(
            Concat([0x11, 0x00], Le(0x80000001)), // failure -> exactly 6 bytes, no ReadStatusChanged
            [0x89, 0x01, 0x00, 0x00, 0x00, 0x00]); // sentinel: RopFreeBookmark response, success

        AssertTwoCleanOperations(ropList, MapiDirection.Response, firstChildCount: 3, secondName: "RopFreeBookmark");
    }

    [Fact]
    public void SetMessageReadFlagResponseSuccessWithoutChangeIsSevenBytes()
    {
        var ropList = Concat(
            Concat([0x11, 0x00], Le(0u), new byte[] { 0x00 }), // success, ReadStatusChanged = false -> 7 bytes
            [0x89, 0x01, 0x00, 0x00, 0x00, 0x00]);

        var nodes = AssertTwoCleanOperations(ropList, MapiDirection.Response, firstChildCount: 4, secondName: "RopFreeBookmark");
        Assert.Equal("false", Find(nodes, "ReadStatusChanged").Value);
    }

    [Fact]
    public void SetMessageReadFlagResponseSuccessWithChangeIsThirtyTwoBytes()
    {
        var clientData = new byte[24];
        for (var i = 0; i < clientData.Length; i++)
        {
            clientData[i] = (byte)(i + 1);
        }

        var ropList = Concat(
            Concat([0x11, 0x00], Le(0u), new byte[] { 0x01, 0x07 }, clientData), // success, changed -> 32 bytes
            [0x89, 0x01, 0x00, 0x00, 0x00, 0x00]);

        var nodes = AssertTwoCleanOperations(ropList, MapiDirection.Response, firstChildCount: 6, secondName: "RopFreeBookmark");
        Assert.Equal("true", Find(nodes, "ReadStatusChanged").Value);
        Assert.Equal("0x07", Find(nodes, "LogonId").Value);
        Assert.Equal(MapiNodeKind.Raw, Find(nodes, "ClientData").Kind);
    }

    // ---------------------------------------------------------------------------------------------
    // Bespoke non-standard field ordering.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ParsesSaveChangesAttachmentRequestNonStandardHandleFieldOrder()
    {
        byte[] ropList = [0x25, 0x00, 0x05, 0x02, 0x01]; // RopId,LogonId,ResponseHandleIndex=5,InputHandleIndex=2,SaveFlags=1
        var buffer = Frame(ropList, 0u, 0u, 0u, 0u, 0u, 0u); // 6 entries: covers indexes up to 5
        var warnings = new List<string>();
        var nodes = RopBufferParser.Parse(buffer, 0, MapiDirection.Request, warnings, new MapiNodeBudget(), CancellationToken.None);

        Assert.Empty(warnings);
        var op = Find(nodes, "Operation 0");
        Assert.Equal("5", Find(op.Children, "ResponseHandleIndex").Value);
        Assert.Equal("2", Find(op.Children, "InputHandleIndex").Value);
        Assert.Equal("0x01", Find(op.Children, "SaveFlags").Value);
    }

    // ---------------------------------------------------------------------------------------------
    // Handle table preservation and cross-validation.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void PreservesHandleTableAndFlagsOutOfRangeHandleIndexWithoutAlteringTableContents()
    {
        // RopCreateAttachment: RopId, LogonId, InputHandleIndex=0 (in range), OutputHandleIndex=5 (out of range).
        byte[] ropList = [0x23, 0x00, 0x00, 0x05];
        var buffer = Frame(ropList, 0xAAAAAAAAu, 0xBBBBBBBBu); // only indexes 0 and 1 are valid
        var warnings = new List<string>();
        var nodes = RopBufferParser.Parse(buffer, 0, MapiDirection.Request, warnings, new MapiNodeBudget(), CancellationToken.None);

        var handleTable = Find(nodes, "Server object handle table");
        Assert.Equal(2, handleTable.Children.Length);
        Assert.Equal("0xAAAAAAAA", handleTable.Children[0].Value);
        Assert.Equal("0xBBBBBBBB", handleTable.Children[1].Value);

        Assert.Contains(
            warnings,
            w => w.Contains("OutputHandleIndex", StringComparison.Ordinal) && w.Contains("index 5", StringComparison.Ordinal));
        Assert.DoesNotContain(warnings, w => w.Contains("InputHandleIndex", StringComparison.Ordinal));
    }

    [Fact]
    public void DoesNotWarnWhenAllHandleIndexesAreWithinRange()
    {
        byte[] ropList = [0x23, 0x00, 0x00, 0x01]; // InputHandleIndex=0, OutputHandleIndex=1; both < 2
        var buffer = Frame(ropList, 0x1u, 0x2u);
        var warnings = new List<string>();
        RopBufferParser.Parse(buffer, 0, MapiDirection.Request, warnings, new MapiNodeBudget(), CancellationToken.None);

        Assert.Empty(warnings);
    }

    // ---------------------------------------------------------------------------------------------
    // Exhaustive, schema-derived coverage: every implemented dispatcher entry is exercised through
    // the dispatcher's own public entry point, and the exact reported counts are locked in as a
    // regression test.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ImplementsExactlyFiftyRequestAndFiftyTwoResponseRopSchemas()
    {
        Assert.Equal(50, RopSemanticParser.RequestSchemas.Count);
        Assert.Equal(52, RopSemanticParser.ResponseSchemas.Count);

        // No RopId is accidentally implemented for both directions under a different assumption, and
        // no two entries in the same direction resolve to the same ROP name (a transcription guard).
        Assert.Equal(
            RopSemanticParser.RequestSchemas.Count,
            RopSemanticParser.RequestSchemas.Keys.Select(RopBufferParser.Name).Distinct().Count());
        Assert.Equal(
            RopSemanticParser.ResponseSchemas.Count,
            RopSemanticParser.ResponseSchemas.Keys.Select(RopBufferParser.Name).Distinct().Count());
        Assert.All(RopSemanticParser.RequestSchemas.Keys, id => Assert.True(RopBufferParser.IsKnownRopId(id)));
        Assert.All(RopSemanticParser.ResponseSchemas.Keys, id => Assert.True(RopBufferParser.IsKnownRopId(id)));
    }

    [Fact]
    public void EveryRequestSchemaParsesAtItsExactWidthWithoutWarnings()
    {
        foreach (var (ropId, schema) in RopSemanticParser.RequestSchemas)
        {
            var bytes = BuildSchemaBytes(schema, takeGateBranches: true);
            var warnings = new List<string>();
            var ops = RopSemanticParser.ParseOperations(
                bytes, 0, MapiDirection.Request, warnings, new MapiNodeBudget(), new List<RopHandleReference>(), CancellationToken.None);

            var op = Assert.Single(ops);
            Assert.True(warnings.Count == 0, $"RopId 0x{ropId:X2} ({RopBufferParser.Name(ropId)}) produced warnings: {string.Join("; ", warnings)}");
            Assert.Equal(MapiNodeKind.Operation, op.Kind);
            Assert.Equal(bytes.Length, op.Length);
            Assert.StartsWith(RopBufferParser.Name(ropId), op.Value);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EveryResponseSchemaParsesAtEachDiscreteWidthWithoutWarnings(bool takeGateBranches)
    {
        foreach (var (ropId, schema) in RopSemanticParser.ResponseSchemas)
        {
            var bytes = BuildSchemaBytes(schema, takeGateBranches);
            var warnings = new List<string>();
            var ops = RopSemanticParser.ParseOperations(
                bytes, 0, MapiDirection.Response, warnings, new MapiNodeBudget(), new List<RopHandleReference>(), CancellationToken.None);

            var op = Assert.Single(ops);
            Assert.True(warnings.Count == 0, $"RopId 0x{ropId:X2} ({RopBufferParser.Name(ropId)}) produced warnings: {string.Join("; ", warnings)}");
            Assert.Equal(MapiNodeKind.Operation, op.Kind);
            Assert.Equal(bytes.Length, op.Length);
            Assert.StartsWith(RopBufferParser.Name(ropId), op.Value);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------

    private static ImmutableArray<MapiNode> AssertTwoCleanOperations(
        byte[] ropList, MapiDirection direction, int firstChildCount, string secondName)
    {
        var buffer = Frame(ropList, 0x00u, 0x01u);
        var warnings = new List<string>();
        var nodes = RopBufferParser.Parse(buffer, 0, direction, warnings, new MapiNodeBudget(), CancellationToken.None);

        Assert.Empty(warnings);
        var ropListNode = Find(nodes, "ROP list");
        Assert.Equal(2, ropListNode.Children.Length);
        Assert.Equal(firstChildCount, ropListNode.Children[0].Children.Length);
        Assert.StartsWith(secondName, ropListNode.Children[1].Value);
        return ropListNode.Children[0].Children;
    }

    /// <summary>
    /// Builds a byte buffer matching the exact declarative shape of <paramref name="schema"/>. When
    /// <paramref name="takeGateBranches"/> is true, every ReturnValue field is written as Success and
    /// every Bool gate field is written as true, so the schema's deepest (widest) tier is produced;
    /// when false, the first gate encountered is written as failure/false and the walk stops there,
    /// producing the schema's narrowest tier. Ungated schemas produce the same bytes either way.
    /// </summary>
    private static byte[] BuildSchemaBytes(RopOperationSchema schema, bool takeGateBranches)
    {
        var bytes = new List<byte>();

        void WriteField(RopField field)
        {
            switch (field.Kind)
            {
                case RopFieldKind.RopId:
                    bytes.Add(schema.RopId);
                    break;
                case RopFieldKind.ReturnValue:
                    bytes.AddRange(takeGateBranches ? Le(0u) : Le(0xFFFFFFFFu));
                    break;
                case RopFieldKind.Bool:
                    bytes.Add((byte)(takeGateBranches ? 1 : 0));
                    break;
                case RopFieldKind.Compound:
                    foreach (var member in field.Members)
                    {
                        WriteField(member);
                    }
                    break;
                default:
                    bytes.AddRange(new byte[field.Size]);
                    break;
            }
        }

        void WriteTier(RopFieldTier tier)
        {
            foreach (var field in tier.Fields)
            {
                WriteField(field);
            }
            if (takeGateBranches && tier.WhenTrue is { } next)
            {
                WriteTier(next);
            }
        }

        WriteTier(schema.Root);
        return bytes.ToArray();
    }

    private static byte[] Le(ushort value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Le(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Le(ulong value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static byte[] Frame(byte[] ropList, params uint[] handles)
    {
        var bytes = new List<byte>();
        bytes.AddRange(Le((ushort)(ropList.Length + 2)));
        bytes.AddRange(ropList);
        foreach (var handle in handles)
        {
            bytes.AddRange(Le(handle));
        }
        return bytes.ToArray();
    }

    private static MapiNode Find(IEnumerable<MapiNode> nodes, string name)
    {
        var found = FindAll(nodes, name).FirstOrDefault();
        Assert.True(found is not null, $"Expected to find a node named '{name}'.");
        return found!;
    }

    private static IEnumerable<MapiNode> FindAll(IEnumerable<MapiNode> nodes, string name)
    {
        foreach (var node in nodes)
        {
            if (node.Name == name)
            {
                yield return node;
            }
            foreach (var match in FindAll(node.Children, name))
            {
                yield return match;
            }
        }
    }
}
