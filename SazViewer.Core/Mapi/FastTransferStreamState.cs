using System.Collections.Immutable;

namespace SazViewer.Core;

/// <summary>
/// Hard bounds that apply to FastTransfer (MS-OXCFXICS) stream lexing. These sit alongside - and
/// never relax - <see cref="MapiParseLimits"/>; every limit below is additionally clamped by the
/// shared payload, node, depth, collection and string limits enforced by <see cref="MapiReader"/>
/// and <see cref="MapiNodeBudget"/>.
/// </summary>
internal static class FastTransferLimits
{
    /// <summary>Maximum lexical elements decoded from a single transfer buffer.</summary>
    public const int MaxStreamElements = 20_000;

    /// <summary>Maximum syntactical marker nesting tracked across a capture-local stream.</summary>
    public const int MaxMarkerDepth = 64;

    /// <summary>Maximum element count accepted for an mvPropType value.</summary>
    public const int MaxMultiValueElements = 65_536;

    /// <summary>Maximum serialized IDSET/GLOBSET commands decoded inside a single value.</summary>
    public const int MaxGlobsetCommands = 8_192;

    /// <summary>Maximum SizedXid entries decoded inside a PredecessorChangeList value.</summary>
    public const int MaxSizedXidEntries = 4_096;

    public const int MaxPropertyGroups = 4_096;

    public const int MaxPropertyTagsPerGroup = 65_536;

    public const int MaxSpecialStructureBytes = 64 * 1024;

    public const int MaxStateStreamChunks = 4_096;

    /// <summary>Maximum distinct streams a single capture-local assembler will track.</summary>
    public const int MaxTrackedStreams = 4_096;

    /// <summary>Maximum declared length accepted for one varSizeValue lexeme.</summary>
    public const long MaxVarValueBytes = MapiParseLimits.MaxPayloadBytes;

}

/// <summary>
/// Identifies one logical FastTransfer stream <em>within a single capture</em>. Server object handle
/// table indices are local to one ROP buffer, while the resolved 32-bit handle value remains stable
/// for the object's lifetime. The connection scope prevents equal handle values on unrelated MAPI
/// connections from being joined.
/// </summary>
internal readonly record struct FastTransferStreamKey(
    string ConnectionScope,
    uint ServerObjectHandle,
    bool Provisional = false);

/// <summary>
/// The tail of a varSizeValue lexeme that a transfer buffer ended inside. MS-OXCFXICS 2.2.4.1 allows
/// a FastTransfer stream to be split "at any point inside a varSizeValue lexeme" (and nowhere else),
/// so this is the only lexical state that can legitimately cross a buffer boundary.
/// </summary>
/// <param name="PropertyType">The propType of the value being continued.</param>
/// <param name="PropertyId">The propId of the value being continued.</param>
/// <param name="DeclaredLength">The full declared length of the split value.</param>
/// <param name="RemainingLength">Bytes of the split value still expected in later buffers.</param>
/// <param name="RemainingMultiValueElements">
/// Elements of an mvPropType array still expected after the split value completes; zero for a
/// single-valued property.
/// </param>
internal readonly record struct FastTransferPendingValue(
    ushort PropertyType,
    ushort PropertyId,
    long DeclaredLength,
    long RemainingLength,
    int RemainingMultiValueElements,
    uint? SpecialMarker = null,
    ImmutableArray<byte> AccumulatedBytes = default);

internal readonly record struct FastTransferSyntaxFrame(
    uint StartTag,
    uint EndTag,
    string Production);

/// <summary>
/// The immutable lexical/syntactical state of one FastTransfer stream between transfer buffers.
/// Every transform returns a new instance; nothing here is mutable and nothing is static except the
/// canonical <see cref="Initial"/> value, so no parse can observe state from another parse.
/// </summary>
internal sealed record FastTransferStreamState
{
    /// <summary>The state a stream starts in, before any transfer buffer has been seen.</summary>
    public static FastTransferStreamState Initial { get; } = new();

    /// <summary>The varSizeValue tail a previous buffer ended inside, if any.</summary>
    public FastTransferPendingValue? Pending { get; init; }

    /// <summary>Open deterministic syntactical productions carried across transfer buffers.</summary>
    public ImmutableArray<FastTransferSyntaxFrame> SyntaxStack { get; init; } =
        ImmutableArray<FastTransferSyntaxFrame>.Empty;

    public int MarkerDepth => SyntaxStack.Length;

    /// <summary>A marker whose context determines the shape of the immediately following propValue.</summary>
    public uint? PendingSpecialMarker { get; init; }

    /// <summary>Number of transfer buffers folded into this state.</summary>
    public int BufferCount { get; init; }

    /// <summary>Total transfer bytes folded into this state.</summary>
    public long TotalBytes { get; init; }

    /// <summary>Total lexical elements decoded across all folded buffers.</summary>
    public long TotalElements { get; init; }

    /// <summary>
    /// True once a buffer could not be lexed coherently. A desynchronized stream is never guessed at
    /// again: every later buffer for the same key is retained as bounded raw bytes.
    /// </summary>
    public bool Desynchronized { get; init; }

    /// <summary>True once the producing ROP reported that the final buffer was delivered.</summary>
    public bool Complete { get; init; }

    public FastTransferStreamState WithBuffer(int byteCount) => this with
    {
        BufferCount = checked(BufferCount + 1),
        TotalBytes = checked(TotalBytes + byteCount),
    };

    public FastTransferStreamState WithElements(int count) => this with
    {
        TotalElements = checked(TotalElements + count),
    };

    public FastTransferStreamState WithPending(FastTransferPendingValue? pending) => this with
    {
        Pending = pending,
    };

    public FastTransferStreamState WithSyntaxStack(ImmutableArray<FastTransferSyntaxFrame> stack) => this with
    {
        SyntaxStack = stack,
    };

    public FastTransferStreamState WithPendingSpecialMarker(uint? marker) => this with
    {
        PendingSpecialMarker = marker,
    };

    public FastTransferStreamState AsDesynchronized() => this with
    {
        Desynchronized = true,
        Pending = null,
        PendingSpecialMarker = null,
    };

    public FastTransferStreamState AsComplete() => this with { Complete = true };
}

/// <summary>
/// The result of lexing one transfer buffer: the decoded nodes, the next immutable stream state, the
/// warnings raised, and exactly how many bytes were accounted for. Callers apply the state
/// transactionally - the returned <see cref="State"/> is only ever adopted as a whole.
/// </summary>
internal sealed record FastTransferLexResult(
    ImmutableArray<MapiNode> Nodes,
    FastTransferStreamState State,
    ImmutableArray<string> Warnings,
    int ConsumedBytes,
    int ElementCount,
    bool EndedInsideValue);

/// <summary>
/// A capture-local, instance-scoped reassembler for FastTransfer streams that span several
/// RopFastTransferSourceGetBuffer / RopFastTransferDestinationPutBuffer operations.
/// <para>
/// There is deliberately no static/mutable state anywhere in this type: an assembler is created by
/// whoever owns a single capture, is threaded explicitly, and dies with that capture. Two captures
/// parsed concurrently cannot influence one another, and nothing survives a parse.
/// </para>
/// <para>
/// This is wired into the live capture-local parse path: <c>MapiCaptureContext</c>
/// (<c>MapiModels.cs</c>) owns exactly one <c>FastTransferAssembler</c> instance per capture.
/// <c>MapiCaptureParser</c> threads the owning session's id down as an opaque capture scope, and
/// <c>MapiHttpMessageParser.Parse</c>/<c>ParseRequestOperation</c>/<c>ParseResponseOperation</c>/
/// <c>ParseExtended</c> forward both the assembler and that scope through
/// <c>ExtendedBufferParser.ParseSequence</c> and <c>RopBufferParser.Parse</c> into
/// <c>RopSemanticParser.ParseOperations</c>, which passes them to
/// <see cref="RopVariableDispatcher.Parse"/>. The dispatcher routes every MS-OXCFXICS RopId to
/// <see cref="RopFastTransferDecoders.Parse(ref MapiReader, int, MapiDirection, List{RopHandleReference}, MapiNodeBudget, CancellationToken, List{string}?, FastTransferStreamAssembler?, string?)"/>,
/// which resolves each operation's buffer-local handle index through <c>MapiCaptureContext</c> to a
/// stable logical-connection/server-handle key before calling <see cref="Continue"/>,
/// <see cref="Complete"/>, or <see cref="Forget"/>.
/// </para>
/// <para>
/// <see cref="Snapshot"/> is surfaced so partially reconstructed streams can be reported as partial
/// rather than success-shaped.
/// </para>
/// </summary>
internal sealed class FastTransferStreamAssembler
{
    private ImmutableDictionary<FastTransferStreamKey, FastTransferStreamState> states =
        ImmutableDictionary<FastTransferStreamKey, FastTransferStreamState>.Empty;

    /// <summary>An immutable snapshot of every stream this assembler currently tracks.</summary>
    public ImmutableDictionary<FastTransferStreamKey, FastTransferStreamState> Snapshot => states;

    /// <summary>True when the tracking table has hit its bound and is no longer accepting new keys.</summary>
    public bool AtCapacity => states.Count >= FastTransferLimits.MaxTrackedStreams;

    public FastTransferStreamState StateFor(FastTransferStreamKey key) =>
        states.TryGetValue(key, out var state) ? state : FastTransferStreamState.Initial;

    public void Commit(FastTransferStreamKey key, FastTransferStreamState state)
    {
        if (states.ContainsKey(key) || !AtCapacity)
        {
            states = states.SetItem(key, state);
        }
    }

    /// <summary>
    /// Lexes <paramref name="buffer"/> as the next slice of the stream identified by
    /// <paramref name="key"/> and commits the resulting state transactionally: the table is replaced
    /// with a new immutable dictionary only after lexing has produced a complete result.
    /// </summary>
    public FastTransferLexResult Continue(
        FastTransferStreamKey key,
        ReadOnlySpan<byte> buffer,
        long absoluteOffset,
        MapiNodeBudget budget,
        int depth,
        CancellationToken cancellationToken)
    {
        var known = states.ContainsKey(key);
        if (!known && AtCapacity)
        {
            // Never grow without bound: lex the buffer statelessly instead of tracking a new stream.
            return FastTransferStreamLexer.Lex(
                buffer,
                absoluteOffset,
                FastTransferStreamState.Initial,
                budget,
                depth,
                cancellationToken);
        }

        var result = FastTransferStreamLexer.Lex(
            buffer,
            absoluteOffset,
            StateFor(key),
            budget,
            depth,
            cancellationToken);
        states = states.SetItem(key, result.State);
        return result;
    }

    /// <summary>Marks a stream complete (its producer reported the final buffer).</summary>
    public void Complete(FastTransferStreamKey key)
    {
        if (states.TryGetValue(key, out var state))
        {
            states = states.SetItem(key, state.AsComplete());
        }
    }

    /// <summary>Forgets a stream entirely, e.g. when its Server object handle is released.</summary>
    public void Forget(FastTransferStreamKey key) => states = states.Remove(key);

    /// <summary>
    /// Resolves same-request state accumulated under an output-slot sentinel to the server-assigned
    /// handle returned by a successful context-creation response. Any old state for the reused
    /// handle is discarded before the provisional state is moved.
    /// </summary>
    public void ResolveProvisional(FastTransferStreamKey provisional, FastTransferStreamKey resolved)
    {
        states = states.Remove(resolved);
        if (states.TryGetValue(provisional, out var state))
        {
            states = states.Remove(provisional).SetItem(resolved, state);
        }
    }

    /// <summary>Discards any provisional stream state that belongs only to one HTTP round trip.</summary>
    public void ForgetProvisional(string captureScope)
    {
        states = states.RemoveRange(states.Keys.Where(
            key => key.Provisional && StringComparer.Ordinal.Equals(key.ConnectionScope, captureScope)));
    }
}
