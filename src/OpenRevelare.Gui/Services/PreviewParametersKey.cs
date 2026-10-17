using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenRevelare.Core;

namespace OpenRevelare.Gui.Services;

/// <summary>A detached key covering every writable parameter, including curves, matrices and
/// dust edits. Large LCC buffers are immutable loaded snapshots, so identity replaces a pixel hash.</summary>
internal static class PreviewParametersKey
{
    private static readonly PropertyInfo[] Properties = typeof(FrameParams).GetProperties()
        .Where(p => p.CanRead && p.CanWrite).OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
    private static readonly JsonSerializerOptions Options = new()
    {
        IncludeFields = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new MatrixConverter(), new FieldConverter(), new NumberConverter() },
    };
    private sealed record Identity(long Value);
    private static long _nextIdentity;
    private static readonly ConditionalWeakTable<ImageBuffer, Identity> Fields = new();

    internal static string Create(FrameParams parameters)
    {
        var key = new StringBuilder();
        foreach (var property in Properties)
        {
            key.Append(property.Name).Append(':');
            key.Append(JsonSerializer.Serialize(property.GetValue(parameters), property.PropertyType, Options));
            key.Append(';');
        }
        return key.ToString();
    }

    private sealed class MatrixConverter : JsonConverter<double[,]>
    {
        public override double[,] Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
            => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, double[,] value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(value.GetLength(0)); writer.WriteNumberValue(value.GetLength(1));
            foreach (double cell in value) JsonSerializer.Serialize(writer, cell, options);
            writer.WriteEndArray();
        }
    }

    private sealed class NumberConverter : JsonConverter<double>
    {
        public override double Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
            => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
        {
            // Slider round trips legitimately produce -0, whose rendered meaning is identical.
            if (double.IsFinite(value)) writer.WriteNumberValue(value == 0 ? 0 : value);
            else writer.WriteStringValue(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private sealed class FieldConverter : JsonConverter<ImageBuffer>
    {
        public override ImageBuffer Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
            => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, ImageBuffer value, JsonSerializerOptions options)
            => writer.WriteNumberValue(Fields.GetValue(value, _ => new(Interlocked.Increment(ref _nextIdentity))).Value);
    }
}
