using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OnvifSim.World;

// Heightmaps are tens of thousands of floats; as base64 of little-endian float32 they stay compact in
// world.json and the editor decodes them straight into a Float32Array. Every platform .NET 10 runs on
// is little-endian, so the bytes are the in-memory floats.
public sealed class FloatArrayBase64Converter : JsonConverter<float[]>
{
    public override float[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.StartArray)
            return JsonSerializer.Deserialize<float[]>(ref reader) ?? [];
        var bytes = reader.GetBytesFromBase64();
        if (bytes.Length % sizeof(float) != 0)
            throw new JsonException("Heightmap byte length is not a multiple of 4.");
        return MemoryMarshal.Cast<byte, float>(bytes).ToArray();
    }

    public override void Write(Utf8JsonWriter writer, float[] value, JsonSerializerOptions options)
    {
        writer.WriteBase64StringValue(MemoryMarshal.AsBytes(value.AsSpan()));
    }
}
