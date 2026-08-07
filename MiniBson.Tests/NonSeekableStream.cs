namespace MiniBson.Tests;

/// <summary>
/// A stream that cannot seek and cannot give its position. It replaces a socket or a pipe in a
/// test. A call to <see cref="Stream.Position"/> or <see cref="Stream.Seek"/> throws an
/// exception. With a <see cref="MemoryStream"/>, the same call works and hides the error.
/// </summary>
/// <remarks>
/// <paramref name="chunkSize"/> limits the number of bytes that one <see cref="Read"/> returns.
/// A real network stream returns short reads. Code that expects one call to fill the request is
/// a common error.
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
