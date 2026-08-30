using System.Buffers;
using System.Text;
using System.Text.Json;
using AngleSharp.ReadOnlyDom.Compact.Projection;

namespace AngleSharp.ReadOnlyDom.Samples;

internal static class CompactProjectionJson
{
    internal static string SerializeFirst(CompactProjectionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            if (result.Rows.Count == 0)
                writer.WriteNullValue();
            else
                WriteRow(writer, result.Rows[0]);
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteRow(Utf8JsonWriter writer, CompactProjectionRow row)
    {
        writer.WriteStartObject();
        foreach (var field in row.Fields)
        {
            writer.WritePropertyName(field.Name);
            if (field.Value.Exists)
                writer.WriteStringValue(field.Value.Span);
            else
                writer.WriteNullValue();
        }
        writer.WriteEndObject();
    }
}
