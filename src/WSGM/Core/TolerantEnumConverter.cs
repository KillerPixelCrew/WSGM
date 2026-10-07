using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WSGM.Core;

/// <summary>Reads unknown enum names as repair defaults or recovery sentinels while retaining the string-enum wire format.</summary>
internal sealed class TolerantEnumConverterFactory : JsonConverterFactory
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert)
    {
        return typeToConvert.IsEnum;
    }

    /// <inheritdoc />
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        return (JsonConverter)Activator.CreateInstance(
            typeof(TolerantEnumConverter<>).MakeGenericType(typeToConvert), options)!;
    }

    private sealed class TolerantEnumConverter<T> : JsonConverter<T> where T : struct, Enum
    {
        private readonly JsonConverter<T> _wire;

        public TolerantEnumConverter(JsonSerializerOptions options)
        {
            _wire = (JsonConverter<T>)new JsonStringEnumConverter<T>().CreateConverter(typeof(T), options);
        }

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType is JsonTokenType.String
                && !Enum.TryParse<T>(reader.GetString(), true, out _))
            {
                return ConfigRepair.IsRecovery(typeof(T)) ? (T)Enum.ToObject(typeof(T), int.MaxValue) : default;
            }

            return _wire.Read(ref reader, typeToConvert, options);
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            _wire.Write(writer, value, options);
        }

        public override T ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert,
            JsonSerializerOptions options)
        {
            return _wire.ReadAsPropertyName(ref reader, typeToConvert, options);
        }

        public override void WriteAsPropertyName(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            _wire.WriteAsPropertyName(writer, value, options);
        }
    }
}
