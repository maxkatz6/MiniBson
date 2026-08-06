using MiniBson;

namespace MiniBson.Tests;

/// <summary>
/// Reads the same bytes twice — once from a seekable stream, where skipping seeks, and once from
/// one that cannot seek, where skipping consumes bytes instead — and returns both results for
/// the caller to compare.
/// </summary>
/// <remarks>
/// The two paths track document ends the same way but move over skipped values differently, so
/// a bug in the consuming path shows up only on a stream that refuses to seek.
/// </remarks>
internal static class DualPathReader
{
    public static (T Seekable, T Streamed) Read<T>(byte[] document, Func<BsonReader, T> read)
    {
        using var seekableStream = new MemoryStream(document, writable: false);
        using var seekableReader = new BsonReader(seekableStream, leaveOpen: true);
        var seekable = read(seekableReader);

        using var backing = new MemoryStream(document, writable: false);
        using var nonSeekable = new NonSeekableStream(backing);
        using var streamedReader = new BsonReader(nonSeekable, leaveOpen: true);
        var streamed = read(streamedReader);

        return (seekable, streamed);
    }

    /// <summary>Drains a stream from its current position.</summary>
    public static byte[] Drain(Stream input)
    {
        using var buffer = new MemoryStream();
        input.CopyTo(buffer);
        return buffer.ToArray();
    }
}
