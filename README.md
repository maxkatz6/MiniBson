# MiniBson

A minimal, trimmable, AOT-compatible BSON reader/writer library for .NET without reflection.

## Features

- **No reflection** - Fully compatible with .NET Native AOT and trimming
- **Low-level API** - Forward-only reader and writer for maximum control
- **Standards compliant** - Implements the [BSON specification](https://bsonspec.org/)
- **Minimal dependencies** - Only `System.Memory` for netstandard2.0

## Installation

### Regular Package
```bash
dotnet add package MiniBson
```

### Source-Only Package
For scenarios where you want the source compiled directly into your assembly (useful for avoiding dependency conflicts or further customization):
```bash
dotnet add package MiniBson.Source
```

## Usage

### Writing BSON

```csharp
using MiniBson;

using var stream = new MemoryStream();
using var writer = new BsonWriter(stream);

writer.WriteStartDocument();
writer.WriteString("name", "John Doe");
writer.WriteInt32("age", 30);
writer.WriteBoolean("active", true);
writer.WriteStartArray("tags");
writer.WriteString("developer");
writer.WriteString("gamer");
writer.WriteEndArray();
writer.WriteEndDocument();

byte[] bsonData = stream.ToArray();
```

### Reading BSON

```csharp
using MiniBson;

using var reader = new BsonReader(bsonData);
reader.ReadStartDocument();

while (reader.Read())
{
    Console.WriteLine($"{reader.CurrentName}: {reader.CurrentType}");
    
    switch (reader.CurrentType)
    {
        case BsonType.String:
            Console.WriteLine($"  Value: {reader.ReadString()}");
            break;
        case BsonType.Int32:
            Console.WriteLine($"  Value: {reader.ReadInt32()}");
            break;
        case BsonType.Boolean:
            Console.WriteLine($"  Value: {reader.ReadBoolean()}");
            break;
        case BsonType.Array:
            reader.ReadStartArray();
            while (reader.Read())
            {
                Console.WriteLine($"    Item: {reader.ReadString()}");
            }
            reader.ReadEndDocument();
            break;
        default:
            reader.Skip();
            break;
    }
}

reader.ReadEndDocument();
```

## Supported BSON Types

| Type | Write Method | Read Method |
|------|-------------|-------------|
| Double | `WriteDouble` | `ReadDouble` |
| String | `WriteString` | `ReadString` |
| Document | `WriteStartDocument` | `ReadStartDocument` / `ReadStartNestedDocument` |
| Array | `WriteStartArray` | `ReadStartArray` |
| Binary | `WriteBinary` | `ReadBinary` |
| ObjectId | `WriteObjectId` | `ReadObjectId` |
| Boolean | `WriteBoolean` | `ReadBoolean` |
| DateTime | `WriteDateTime` | `ReadDateTime` |
| Null | `WriteNull` | (check `CurrentType`) |
| Regex | `WriteRegex` | `ReadRegex` |
| JavaScript | `WriteJavaScript` | `ReadJavaScript` |
| Int32 | `WriteInt32` | `ReadInt32` |
| Timestamp | `WriteTimestamp` | `ReadTimestamp` |
| Int64 | `WriteInt64` | `ReadInt64` |
| GUID | `WriteGuid` | `ReadGuid` |

## Acknowledgments

A substantial part of the code in this project was generated with assistance from [Claude](https://www.anthropic.com/claude).

## License

MIT License - see LICENSE file for details.

