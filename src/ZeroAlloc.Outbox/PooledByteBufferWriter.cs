using System.Buffers;

namespace ZeroAlloc.Outbox;

/// <summary>
/// An <see cref="IBufferWriter{T}"/> whose buffer is rented from <see cref="ArrayPool{T}.Shared"/>.
/// <see cref="Reset"/> returns the buffer to the pool, so a writer kept between calls holds no memory.
/// </summary>
/// <remarks>Not thread-safe: one writer serves one serialization at a time.</remarks>
internal sealed class PooledByteBufferWriter : IBufferWriter<byte>
{
    private const int MinimumSize = 256;

    private byte[] _buffer = [];
    private int _written;

    /// <summary>The bytes written since the last <see cref="Reset"/>.</summary>
    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

    public void Advance(int count)
    {
        if ((uint)count > (uint)(_buffer.Length - _written))
            throw new ArgumentOutOfRangeException(nameof(count), "Cannot advance past the end of the buffer.");

        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_written);
    }

    /// <summary>Forgets the written bytes and returns the buffer to the pool.</summary>
    public void Reset()
    {
        var buffer = _buffer;
        _buffer = [];
        _written = 0;
        if (buffer.Length > 0)
            ArrayPool<byte>.Shared.Return(buffer);
    }

    private void EnsureCapacity(int sizeHint)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        if (sizeHint == 0)
            sizeHint = 1;

        if (sizeHint <= _buffer.Length - _written)
            return;

        var required = checked(_written + sizeHint);
        // Doubling keeps a large payload to a logarithmic number of regrowths.
        var doubled = (int)Math.Min((long)_buffer.Length * 2, Array.MaxLength);
        var grown = ArrayPool<byte>.Shared.Rent(Math.Max(required, Math.Max(doubled, MinimumSize)));

        var old = _buffer;
        old.AsSpan(0, _written).CopyTo(grown);
        _buffer = grown;
        if (old.Length > 0)
            ArrayPool<byte>.Shared.Return(old);
    }
}
