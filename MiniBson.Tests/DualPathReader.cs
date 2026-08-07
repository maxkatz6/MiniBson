using MiniBson;

namespace MiniBson.Tests;

/// <summary>
/// Reads the same bytes two times and returns both results for the caller to compare. The first
/// read uses a stream that can seek, where a skip does a seek. The second uses a stream that
/// cannot seek, where a skip consumes the bytes.
/// </summary>
/// <remarks>
/// The two paths find the end of a document in the same manner, but they move across a skipped
/// value differently. Thus you see a bug in the second path only on a stream that cannot seek.
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

    /// <summary>Reads all bytes of a stream from its current position.</summary>
    public static byte[] Drain(Stream input)
    {
        using var buffer = new MemoryStream();
        input.CopyTo(buffer);
        return buffer.ToArray();
    }
}
