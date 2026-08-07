using System;

namespace MiniBson;

/// <summary>
/// Makes a partial class a BSON context and names one type that gets generated serialization
/// code.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
#if MINIBSON_PUBLIC
public sealed class BsonSerializableAttribute : Attribute
#else
internal sealed class BsonSerializableAttribute : Attribute
#endif
{
    /// <summary>
    /// The type that gets the generated serialization code.
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
