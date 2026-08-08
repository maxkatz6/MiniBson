using MiniBson;

namespace MiniBson.Tests;

/// <summary>
/// Assertions over a <see cref="BsonReader"/>.
/// </summary>
/// <remarks>
/// The reader is a ref struct. Thus a lambda cannot capture one, and <c>Assert.Throws</c> cannot
/// take an action that uses one. These helpers pass the reader as a by-ref parameter instead. The
/// lambda then receives the reader and does not capture it.
/// </remarks>
internal static class ReaderAssert
{
    public delegate void ReaderAction(ref BsonReader reader);

    public delegate T ReaderFunc<out T>(ref BsonReader reader);

    /// <summary>Asserts that <paramref name="action"/> throws <typeparamref name="TException"/>.</summary>
    public static TException Throws<TException>(ref BsonReader reader, ReaderAction action)
        where TException : Exception
    {
        try
        {
            action(ref reader);
        }
        catch (TException expected)
        {
            return expected;
        }
        catch (Exception other)
        {
            throw new AssertFailedException(
                $"Expected {typeof(TException).Name} but got {other.GetType().Name}: {other.Message}", other);
        }

        throw new AssertFailedException($"Expected {typeof(TException).Name} but no exception was thrown.");
    }
}
