using System.Text.Json;
using Mdk.Api.Domain;

namespace Mdk.Tests;

/// <summary>
/// decimal 的 JSON 口径：序列化时去掉标度与伪精度（八位小数上限 + 去尾随零 + 不用指数），
/// 未定义值序列化为 null 而不是 NaN 哨兵。
/// </summary>
public static class DecimalJsonTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new DecimalJsonConverter(), new NullableDecimalJsonConverter() },
    };

    private static string Write(decimal value) => JsonSerializer.Serialize(value, Options);
    private static string WriteNullable(decimal? value) => JsonSerializer.Serialize(value, Options);

    public static void Register(TestKit t)
    {
        t.Case("JSON_标度冗余_交易所八位小数去掉尾随零", () =>
        {
            // 币安返回 "84538.01000000"，decimal 保留 8 位标度，序列化应还原为 84538.01
            Assert.Equal("84538.01", Write(decimal.Parse("84538.01000000")));
        });

        t.Case("JSON_整数标度_不输出小数点与尾零", () =>
        {
            Assert.Equal("83132", Write(decimal.Parse("83132.00000000")));
        });

        t.Case("JSON_伪精度_除法残渣截断到八位", () =>
        {
            // ATR 推导止损的典型值：decimal 除法给满 28 位有效数字，报价精度上限是 1e-8
            Assert.Equal("1.35615346", Write(decimal.Parse("1.3561534575548120977675727906")));
        });

        t.Case("JSON_极小值_低于报价精度归零而非写成指数", () =>
        {
            Assert.Equal("0", Write(decimal.Parse("0.000000001")));
            Assert.Equal("0", Write(decimal.Parse("-0.000000004")));
        });

        t.Case("JSON_最小报价单位_定点写出而非科学计数", () =>
        {
            // Utf8JsonWriter 对 decimal 会写成 1E-08；定点格式对前端与 JSONL 都更可读
            Assert.Equal("0.00000001", Write(decimal.Parse("0.00000001000")));
            Assert.Equal("0.00012345", Write(decimal.Parse("0.0001234500")));
        });

        t.Case("JSON_八位以内_原值不变", () =>
        {
            foreach (var expected in new[] { "0.00000001", "477.19429", "118.22", "83664.01", "-1234.56789012" })
                Assert.Equal(expected, Write(decimal.Parse(expected)));
        });

        t.Case("JSON_序列化不改变内存中的原值_仅输出侧生效", () =>
        {
            var original = decimal.Parse("1.3561534575548120977675727906");
            _ = Write(original);
            Assert.Equal("1.3561534575548120977675727906", original.ToString(System.Globalization.CultureInfo.InvariantCulture));
        });

        t.Case("JSON_未定义值_输出null而不是NaN哨兵", () =>
        {
            Assert.Equal("null", WriteNullable(null));
            Assert.Equal("42.5", WriteNullable(42.5m));
        });

        t.Case("JSON_往返_八位以内精度无损", () =>
        {
            foreach (var raw in new[] { "84538.01", "0.00000001", "-1234.56789012", "83132", "0" })
            {
                var value = decimal.Parse(raw);
                Assert.Equal(value, JsonSerializer.Deserialize<decimal>(Write(value), Options));
            }
        });

        t.Case("JSON_币安原始字符串_往返后数值相等", () =>
        {
            // 现货 K 线的 open/close 原样是 "84463.37000000"
            var fromExchange = decimal.Parse("84463.37000000");
            var json = Write(fromExchange);
            Assert.Equal("84463.37", json);
            Assert.Equal(fromExchange, JsonSerializer.Deserialize<decimal>(json, Options));
        });

        t.Case("JSON_结构体字段_随对象序列化同口径", () =>
        {
            var json = JsonSerializer.Serialize(new { price = decimal.Parse("1.36800000"), adx = (decimal?)null }, Options);
            Assert.Equal("{\"price\":1.368,\"adx\":null}", json);
        });
    }
}
