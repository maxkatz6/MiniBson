using MiniBson;

namespace MiniBson.Tests;

/// <summary>
/// Serializes twice — once to a seekable stream, where document lengths are patched in
/// afterwards, and once to a non-seekable stream, where generated code must compute them up
/// front — and asserts both produce identical bytes.
/// </summary>
/// <remarks>
/// Measure and write are independent walks of the same object graph, so they can disagree, and
/// a disagreement is invisible on a <see cref="MemoryStream"/>. Every generator test routes
/// through here for that reason.
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
