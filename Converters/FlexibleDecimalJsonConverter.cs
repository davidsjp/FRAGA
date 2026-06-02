using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FRAGA.Converters;

public class FlexibleDecimalJsonConverter : JsonConverter<decimal>
{
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
            return reader.GetDecimal();

        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Valor decimal deve ser numero ou texto numerico.");

        var value = reader.GetString();
        if (string.IsNullOrWhiteSpace(value))
            return 0;

        return ParseDecimal(value);
    }

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value);
    }

    private static decimal ParseDecimal(string value)
    {
        var normalized = value.Trim().Replace("R$", "", StringComparison.OrdinalIgnoreCase).Replace(" ", "");

        if (normalized.Contains(','))
        {
            normalized = normalized.Replace(".", "").Replace(',', '.');
        }

        if (decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
            return parsed;

        throw new JsonException($"Valor decimal invalido: {value}");
    }
}
