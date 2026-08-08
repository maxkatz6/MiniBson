using System.Buffers;

namespace MiniBson.Tests;

/// <summary>
/// Builds a <see cref="ReadOnlySequence{T}"/> from real pieces. Thus the reader must cross
/// segment boundaries.
/// </summary>
/// <remarks>
/// A sequence from one array has one segment, and the reader does not run its segment code for
/// it. Each helper here builds a chain of <see cref="ReadOnlySequenceSegment{T}"/> instead. That
/// is the only way to reach the code where a length prefix, a name, or a payload lies across two
/// segments.
/// </remarks>
internal static class SequenceFactory
{
    /// <summary>Segments of <paramref name="chunkSize"/> bytes, the last one shorter.</summary>
    public static ReadOnlySequence<byte> Chunked(byte[] data, int chunkSize)
    {
        var pieces = new List<ReadOnlyMemory<byte>>();
        for (var offset = 0; offset < data.Length; offset += chunkSize)
            pieces.Add(new ReadOnlyMemory<byte>(data, offset, Math.Min(chunkSize, data.Length - offset)));

        return Build(pieces);
    }

    /// <summary>Two segments split at <paramref name="at"/>. Both ends are allowed.</summary>
    public static ReadOnlySequence<byte> SplitAt(byte[] data, int at) =>
        Build([
            new ReadOnlyMemory<byte>(data, 0, at),
            new ReadOnlyMemory<byte>(data, at, data.Length - at)
        ]);

    /// <summary>Every two-segment split of <paramref name="data"/>, including the empty ends.</summary>
    public static IEnumerable<ReadOnlySequence<byte>> EverySplit(byte[] data)
    {
        for (var at = 0; at <= data.Length; at++)
            yield return SplitAt(data, at);
    }

    /// <summary>
    /// Chunks, with an empty segment before each chunk and one at the end. A sequence is allowed
    /// to hold an empty segment at any place. A reader that reads an empty segment as the end of
    /// the input fails here and in no other test.
    /// </summary>
    public static ReadOnlySequence<byte> WithEmptySegments(byte[] data, int chunkSize)
    {
        var pieces = new List<ReadOnlyMemory<byte>>();

        for (var offset = 0; offset < data.Length; offset += chunkSize)
        {
            pieces.Add(ReadOnlyMemory<byte>.Empty);
            pieces.Add(new ReadOnlyMemory<byte>(data, offset, Math.Min(chunkSize, data.Length - offset)));
        }

        pieces.Add(ReadOnlyMemory<byte>.Empty);
        return Build(pieces);
    }

    /// <summary>The shapes that a test should cover. The name goes into the failure message.</summary>
    public static IEnumerable<(string Name, ReadOnlySequence<byte> Sequence)> AllShapes(byte[] data)
    {
        yield return ("single segment", new ReadOnlySequence<byte>(data));
        yield return ("chunked(1)", Chunked(data, 1));
        yield return ("chunked(2)", Chunked(data, 2));
        yield return ("chunked(3)", Chunked(data, 3));
        yield return ("chunked(7)", Chunked(data, 7));
        yield return ("chunked(4096)", Chunked(data, 4096));
        yield return ("empty segments(1)", WithEmptySegments(data, 1));
        yield return ("empty segments(5)", WithEmptySegments(data, 5));
    }

    private static ReadOnlySequence<byte> Build(List<ReadOnlyMemory<byte>> pieces)
    {
        if (pieces.Count == 0)
            return ReadOnlySequence<byte>.Empty;

        var first = new Segment(pieces[0], runningIndex: 0);
        var last = first;

        for (var i = 1; i < pieces.Count; i++)
            last = last.Append(pieces[i]);

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
