namespace SazViewer.Core;

/// <summary>
/// Routes every RopId/direction pair that <see cref="RopSemanticParser"/> has no fixed-width schema
/// for to whichever self-contained family decoder module claims it. This class performs no
/// field-level parsing of its own; it only picks among:
/// <list type="bullet">
/// <item><description><see cref="RopFolderTableDecoders"/> - MS-OXCFOLD/MS-OXCTABL (folders/tables).</description></item>
/// <item><description><see cref="RopPropertyStoreDecoders"/> - MS-OXCPRPT/MS-OXCSTOR (properties/streams/store).</description></item>
/// <item><description><see cref="RopMessageRulesDecoders"/> - MS-OXCMSG/MS-OXORULE/MS-OXCPERM/MS-OXCNOTIF.</description></item>
/// <item><description><see cref="RopFastTransferDecoders"/> - MS-OXCFXICS (bulk transfer/ICS).</description></item>
/// </list>
/// The four modules were each independently authored against disjoint RopId/direction sets; this is
/// verified for every RopId 0x00-0xFF in both directions by <c>RopVariableDispatcherTests</c>
/// (including cross-checking against <see cref="RopSemanticParser"/>'s fixed schema catalog, which
/// always takes precedence and is never shadowed).
/// <para>
/// The trailing <paramref name="warnings"/>/<paramref name="fastTransferAssembler"/>/
/// <paramref name="captureScope"/> parameters on <see cref="Parse"/> are optional and exist solely to
/// let <see cref="RopFastTransferDecoders"/> reassemble a FastTransfer stream that spans several
/// buffers within the same capture; every other family ignores them. When omitted, FastTransfer ROPs
/// still decode correctly - each buffer is simply lexed independently instead of being joined to its
/// capture-local predecessor.
/// </para>
/// </summary>
internal static class RopVariableDispatcher
{
    public static bool Supports(MapiDirection direction, byte ropId) =>
        RopFolderTableDecoders.Supports(direction, ropId) ||
        RopPropertyStoreDecoders.Supports(direction, ropId) ||
        RopMessageRulesDecoders.Supports(direction, ropId) ||
        RopFastTransferDecoders.Supports(direction, ropId);

    public static MapiNode Parse(
        ref MapiReader reader,
        int operationIndex,
        MapiDirection direction,
        List<RopHandleReference> handleReferences,
        MapiNodeBudget budget,
        CancellationToken cancellationToken,
        List<string>? warnings = null,
        FastTransferStreamAssembler? fastTransferAssembler = null,
        string? captureScope = null)
    {
        var ropId = reader.PeekByte("RopId");
        if (RopFolderTableDecoders.Supports(direction, ropId))
        {
            return RopFolderTableDecoders.Parse(ref reader, operationIndex, direction, handleReferences, budget, cancellationToken);
        }

        if (RopPropertyStoreDecoders.Supports(direction, ropId))
        {
            return RopPropertyStoreDecoders.Parse(ref reader, operationIndex, direction, handleReferences, budget, cancellationToken);
        }

        if (RopMessageRulesDecoders.Supports(direction, ropId))
        {
            return RopMessageRulesDecoders.Parse(ref reader, operationIndex, direction, handleReferences, budget, cancellationToken);
        }

        if (RopFastTransferDecoders.Supports(direction, ropId))
        {
            return RopFastTransferDecoders.Parse(
                ref reader,
                operationIndex,
                direction,
                handleReferences,
                budget,
                cancellationToken,
                warnings,
                fastTransferAssembler,
                captureScope);
        }

        throw new MapiParseException(
            reader.Position,
            $"No variable {direction.ToString().ToLowerInvariant()} schema is registered for ROP 0x{ropId:X2}.");
    }
}
