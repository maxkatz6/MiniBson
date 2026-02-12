using System;

namespace MiniBson;

/// <summary>
/// Marks a partial class as a BSON serialization context and specifies which types should have
/// serialization code generated.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
#if MINIBSON_PUBLIC
public sealed class BsonSerializableAttribute : Attribute
#else
internal sealed class BsonSerializableAttribute : Attribute
#endif
{
    /// <summary>
    /// The type to generate serialization code for.
    /// </summary>
    public Type Type { get; }

    /// <summary>
    /// Creates a new instance of <see cref="BsonSerializableAttribute"/>.
    /// </summary>
    /// <param name="type">The type to generate serialization code for.</param>
    public BsonSerializableAttribute(Type type)
    {
        Type = type;
    }
}

