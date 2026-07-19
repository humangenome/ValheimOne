using System.Collections.Generic;

namespace ValheimOne.Networking;

internal sealed class AckGatedChunkQueue<T>
    where T : class, IAcknowledgedChunk
{
    private readonly Queue<T> _pending = new Queue<T>();

    public bool IsAwaitingAcknowledgement { get; private set; }

    public int AwaitingIndex { get; private set; } = -1;

    public int AcknowledgedCount { get; private set; }

    public int PendingCount => _pending.Count;

    public bool IsIdle => !IsAwaitingAcknowledgement && _pending.Count == 0;

    public void Enqueue(T chunk) => _pending.Enqueue(chunk);

    public bool TryStartNext(out T? chunk)
    {
        if (IsAwaitingAcknowledgement || _pending.Count == 0)
        {
            chunk = default;
            return false;
        }

        chunk = _pending.Dequeue();
        IsAwaitingAcknowledgement = true;
        AwaitingIndex = chunk.Index;
        return true;
    }

    public bool TryAcknowledge(int index)
    {
        if (!IsAwaitingAcknowledgement || AwaitingIndex != index)
        {
            return false;
        }

        IsAwaitingAcknowledgement = false;
        AwaitingIndex = -1;
        AcknowledgedCount++;
        return true;
    }

    public void Reset()
    {
        _pending.Clear();
        IsAwaitingAcknowledgement = false;
        AwaitingIndex = -1;
        AcknowledgedCount = 0;
    }
}
