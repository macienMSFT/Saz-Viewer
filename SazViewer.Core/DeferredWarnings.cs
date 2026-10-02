namespace SazViewer.Core;

/// <summary>
/// Inserts warnings produced by deferred body decoding into a session's warning list at the positions an
/// eager parse would have produced them, so the final order is independent of when bodies are decoded.
/// </summary>
internal sealed class DeferredWarnings(List<string> target)
{
    private readonly List<(int Position, int Sequence, int Count)> inserted = [];
    private int nextSequence;

    public List<string> Target => target;

    /// <summary>Records the current (eager-order) position for warnings that will be produced later.</summary>
    public (int Position, int Sequence) Reserve()
    {
        lock (target)
        {
            return (target.Count, nextSequence++);
        }
    }

    public void Insert((int Position, int Sequence) slot, List<string> warnings)
    {
        if (warnings.Count == 0)
        {
            return;
        }

        lock (target)
        {
            var index = slot.Position;
            foreach (var previous in inserted)
            {
                if (previous.Position < slot.Position
                    || (previous.Position == slot.Position && previous.Sequence < slot.Sequence))
                {
                    index += previous.Count;
                }
            }
            target.InsertRange(index, warnings);
            inserted.Add((slot.Position, slot.Sequence, warnings.Count));
        }
    }
}
