using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mdk.Api.Domain;

/// <summary>TradingPair 以币安连写字符串（"BTCUSDT"）参与 JSON 序列化/反序列化。</summary>
public sealed class TradingPairJsonConverter : JsonConverter<TradingPair>
{
    public override TradingPair Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.GetString();
        return raw is null
            ? throw new JsonException("交易对不能为空")
            : TradingPair.Parse(raw);
    }

    public override void Write(Utf8JsonWriter writer, TradingPair value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Symbol);
}
