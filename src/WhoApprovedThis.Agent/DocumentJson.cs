using System.Text.Json;
using Amazon.Runtime.Documents;

namespace WhoApprovedThis.Agent;

// Bridges System.Text.Json (MCP) and Amazon.Runtime.Documents.Document
// (Bedrock Converse) in both directions.
static class DocumentJson
{
    extension(JsonElement element)
    {
        public Document ToDocument() => element.ValueKind switch
        {
            JsonValueKind.Object => new Document(element.EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.ToDocument())),
            JsonValueKind.Array => new Document(element.EnumerateArray()
                .Select(item => item.ToDocument()).ToList()),
            JsonValueKind.String => new Document(element.GetString()),
            JsonValueKind.Number => element.TryGetInt64(out var value)
                ? new Document(value)
                : new Document(element.GetDouble()),
            JsonValueKind.True => new Document(true),
            JsonValueKind.False => new Document(false),
            _ => new Document(),
        };
    }

    extension(Document document)
    {
        public Dictionary<string, object?> ToArguments() =>
            !document.IsDictionary()
                ? []
                : document.AsDictionary().ToDictionary(
                    entry => entry.Key,
                    entry => (object?)ToJsonElement(entry.Value));
    }

    static JsonElement ToJsonElement(Document document)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(writer, document);
        }
        buffer.Position = 0;
        using var parsed = JsonDocument.Parse(buffer);
        return parsed.RootElement.Clone();
    }

    static void Write(Utf8JsonWriter writer, Document document)
    {
        if (document.IsDictionary())
        {
            writer.WriteStartObject();
            foreach (var entry in document.AsDictionary())
            {
                writer.WritePropertyName(entry.Key);
                Write(writer, entry.Value);
            }
            writer.WriteEndObject();
        }
        else if (document.IsList())
        {
            writer.WriteStartArray();
            foreach (var item in document.AsList()) Write(writer, item);
            writer.WriteEndArray();
        }
        else if (document.IsString()) writer.WriteStringValue(document.AsString());
        else if (document.IsBool()) writer.WriteBooleanValue(document.AsBool());
        else if (document.IsInt()) writer.WriteNumberValue(document.AsInt());
        else if (document.IsLong()) writer.WriteNumberValue(document.AsLong());
        else if (document.IsDouble()) writer.WriteNumberValue(document.AsDouble());
        else writer.WriteNullValue();
    }
}
