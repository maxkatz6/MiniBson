using MiniBson;

namespace MiniBson.Tests;

/// <summary>
/// Serializes a value two times and asserts that the two results have the same bytes. The first
/// serialization uses a stream that can seek, where the writer writes each document length
/// later. The second uses a stream that cannot seek, where the generated code computes each
/// length first.
/// </summary>
/// <remarks>
/// The measure pass and the write pass read the same object graph separately, so they can
/// disagree. You cannot see a disagreement on a <see cref="MemoryStream"/>. Thus each generator
/// test uses this helper.
/// </remarks>
internal static class DualPathWriter
{
    public static byte[] Serialize(Action<BsonWriter> serialize)
    {
        var patched = Write(serialize, seekable: true);
        var measured = Write(serialize, seekable: false);

        CollectionAssert.AreEqual(
            patched,
            measured,
            "Sizes computed for the non-seekable path disagree with the bytes actually written.");

        return patched;
    }

    private static byte[] Write(Action<BsonWriter> serialize, bool seekable)
    {
        using var buffer = new MemoryStream();

        if (seekable)
        {
            using var writer = new BsonWriter(buffer, leaveOpen: true);
            serialize(writer);
        }
        else
        {
            using var nonSeekable = new NonSeekableStream(buffer);
            using var writer = new BsonWriter(nonSeekable, leaveOpen: true);
            serialize(writer);
        }

        return buffer.ToArray();
    }
}
