using System.Buffers;

namespace HaDesktop.Core.Ha;

/// <summary>
/// Accumulates one WebSocket message as a chain of fixed-size chunks instead of one contiguous,
/// doubling buffer. Each chunk stays under the large-object-heap threshold, so even a multi-megabyte
/// get_states response never lands there — a growing MemoryStream used to leave tens of megabytes
/// of dead LOH space behind after every such response. The first chunk is reused for the lifetime
/// of the connection (nearly every message fits in it); extra chunks are dropped on the next reset.
/// </summary>
internal sealed class ReceiveBuffer
{
    private const int ChunkSize = 32 * 1024;

    private readonly List<byte[]> _chunks = new() { new byte[ChunkSize] };
    private int _lastChunkLength;

    public void Reset()
    {
        if (_chunks.Count > 1)
            _chunks.RemoveRange(1, _chunks.Count - 1);
        _lastChunkLength = 0;
    }

    /// <summary>Free space to receive into; always non-empty.</summary>
    public Memory<byte> GetMemory()
    {
        if (_lastChunkLength == ChunkSize)
        {
            _chunks.Add(new byte[ChunkSize]);
            _lastChunkLength = 0;
        }

        return _chunks[^1].AsMemory(_lastChunkLength);
    }

    public void Advance(int count) => _lastChunkLength += count;

    public ReadOnlySequence<byte> AsSequence()
    {
        if (_chunks.Count == 1)
            return new ReadOnlySequence<byte>(_chunks[0], 0, _lastChunkLength);

        var first = new Segment(_chunks[0], 0);
        var last = first;
        for (var i = 1; i < _chunks.Count; i++)
        {
            var memory = i == _chunks.Count - 1 ? _chunks[i].AsMemory(0, _lastChunkLength) : _chunks[i];
            last = last.Append(memory);
        }

        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory, RunningIndex + Memory.Length);
            Next = next;
            return next;
        }
    }
}
