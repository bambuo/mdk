using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mdk.Api.Domain;

/// <summary>
/// decimal 的 JSON 口径：**去掉无意义的标度与伪精度**。
///
/// 背景：decimal 会保留标度与运算产生的全部 28 位有效数字，直接序列化会把
/// 交易所报价写成 <c>84538.01000000</c>、把 ATR 推导的止损写成
/// <c>1.3561534575548120977675727906</c>。前者让报文与信号库（JSONL）凭空变大，
/// 后者把"除法残渣"伪装成精确到 1e-28 的价格，与"只陈述证据"的口径相悖。
///
/// 规则：按 8 位小数四舍五入（币安价格/数量精度上限即 1e-8，更细的位不可能是有效报价），
/// 再以定点格式写出（不用科学计数法、不留尾随零）。该口径只作用于**序列化**，
/// 内存中的 decimal 与信号计算全程保持原值。
/// </summary>
public sealed class DecimalJsonConverter : JsonConverter<decimal>
{
    /// <summary>币安最小报价/步进单位（1e-8）；比这更细的位数只可能是运算残渣。</summary>
    private const int MaxScale = 8;

    /// <summary>序列化口径：八位小数四舍五入 → 定点文本（无尾随零、无指数）。</summary>
    public static string Format(decimal value) =>
        decimal.Round(value, MaxScale, MidpointRounding.AwayFromZero)
            .ToString("0.########", CultureInfo.InvariantCulture);

    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetDecimal();

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
        writer.WriteRawValue(Format(value));
}

/// <summary>可空 decimal 的同口径版本（未定义值序列化为 null，不写 NaN 哨兵）。</summary>
public sealed class NullableDecimalJsonConverter : JsonConverter<decimal?>
{
    public override decimal? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : reader.GetDecimal();

    public override void Write(Utf8JsonWriter writer, decimal? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteRawValue(DecimalJsonConverter.Format(value.Value));
    }
}
