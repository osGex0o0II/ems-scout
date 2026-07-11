using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace EmsScout.Infrastructure.Importing;

public static class CollectionSnapshotCanonicalJson
{
    public static byte[] SerializeBuildings(JsonElement buildings)
    {
        if (buildings.ValueKind != JsonValueKind.Array)
        {
            throw new CollectionSnapshotContractException("CollectionSnapshot buildings must be an array.");
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = false,
            SkipValidation = false,
        }))
        {
            WriteCanonical(writer, buildings);
        }
        return stream.ToArray();
    }

    public static string ComputeSha256(ReadOnlySpan<byte> payload) =>
        Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                var number = value.GetDouble();
                if (!double.IsFinite(number))
                {
                    throw new CollectionSnapshotContractException("CollectionSnapshot contains a non-finite number.");
                }
                writer.WriteNumberValue(number == 0 ? 0d : number);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new CollectionSnapshotContractException(
                    $"CollectionSnapshot contains unsupported JSON kind {value.ValueKind}.");
        }
    }
}
