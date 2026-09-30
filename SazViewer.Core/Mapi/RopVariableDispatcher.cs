namespace SazViewer.Core;

internal static class RopVariableDispatcher
{
    public static bool Supports(MapiDirection direction, byte ropId) => false;

    public static MapiNode Parse(
        ref MapiReader reader,
        int operationIndex,
        MapiDirection direction,
        List<RopHandleReference> handleReferences,
        MapiNodeBudget budget,
        CancellationToken cancellationToken) =>
        throw new MapiParseException(
            reader.Position,
            $"No variable {direction.ToString().ToLowerInvariant()} schema is registered for ROP 0x{reader.PeekByte("RopId"):X2}.");
}
