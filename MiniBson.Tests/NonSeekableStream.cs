namespace MiniBson.Tests;

/// <summary>
/// A stream that refuses to seek or report its position, standing in for a socket or pipe.
/// Reaching for <see cref="Stream.Position"/> or <see cref="Stream.Seek"/> fails loudly rather
/// than quietly working because the test used a <see cref="MemoryStream"/>.
/// </summary>
/// <remarks>
/// <paramref name="chunkSize"/> caps how much a single <see cref="Read"/> returns. Real network
/// streams hand back short reads, and code that assumes one call fills the request is a common
/// way to get this wrong.
/// </remarks>
internal sealed class NonSeekableStream(Stream inner, int chunkSize = int.MaxValue) : Stream
{
    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => inner.CanWrite;

    public override long Length => throw new NotSupportedException("Length is not available.");

    public override long Position
    {
        get => throw new NotSupportedException("Position is not available.");
        set => throw new NotSupportedException("Seeking is not supported.");
    }

    public override void Flush() => inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) =>
        inner.Read(buffer, offset, Math.Min(count, chunkSize));

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException("Seeking is not supported.");

    public override void SetLength(long value) =>
        throw new NotSupportedException("SetLength is not supported.");

    public override void Write(byte[] buffer, int offset, int count) =>
        inner.Write(buffer, offset, count);

    public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
}
