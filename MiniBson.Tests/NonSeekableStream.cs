namespace MiniBson.Tests;

/// <summary>
/// A write-only stream that refuses to seek or report its position, standing in for a socket or
/// pipe. Reaching for <see cref="Stream.Position"/> or <see cref="Stream.Seek"/> fails loudly
/// rather than quietly working because the test used a <see cref="MemoryStream"/>.
/// </summary>
internal sealed class NonSeekableStream(Stream inner) : Stream
{
    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException("Length is not available.");

    public override long Position
    {
        get => throw new NotSupportedException("Position is not available.");
        set => throw new NotSupportedException("Seeking is not supported.");
    }

    public override void Flush() => inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Reading is not supported.");

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException("Seeking is not supported.");

    public override void SetLength(long value) =>
        throw new NotSupportedException("SetLength is not supported.");

    public override void Write(byte[] buffer, int offset, int count) =>
        inner.Write(buffer, offset, count);

    public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
}
