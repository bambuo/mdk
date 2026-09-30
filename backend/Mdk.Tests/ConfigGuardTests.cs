using System.Reflection;
using System.Text.Json;
using Mdk.Api.Analysis;
using Mdk.Api.Analysis.Chan;
using Mdk.Api.Binance;

namespace Mdk.Tests;

/// <summary>
/// 配置守卫：appsettings.json 只允许写**真实覆盖项**。
///
/// 为什么需要它（2026-09-30 的教训）：配置里曾有一份与代码默认值完全相同的副本
/// （`Binance.FuturesWsBaseUrl`），代码改成正确的分区地址后**被配置静默覆盖**，
/// 于是"修了却不生效"。副本还会掩盖重命名——键名拼错/属性改名后配置被静默忽略。
///
/// 两道断言：
///   ① 每个配置键必须在对应 Options 类型上存在（防拼写错误与属性改名后的残留）；
///   ② 每个配置值必须与代码默认值**不同**（防冗余副本掩盖代码修复）。
/// </summary>
public static class ConfigGuardTests
{
    public static void Register(TestKit t)
    {
        t.Case("配置守卫_只允许真实覆盖项且键名有效", () =>
        {
            var path = FindAppSettings();
            Assert.True(path is not null, "未找到 Mdk.Api/appsettings.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path!));
            var root = doc.RootElement;

            // 非 Options 的框架配置节（不属于本项目 Options 体系）
            var frameworkSections = new HashSet<string>(StringComparer.Ordinal) { "Logging", "AllowedHosts", "_comment" };

            var optionsSections = new Dictionary<string, Type>(StringComparer.Ordinal)
            {
                ["Binance"] = typeof(BinanceOptions),
                ["Signal"] = typeof(SignalOptions),
                ["Chan"] = typeof(ChanOptions),
                ["Backfill"] = typeof(BackfillOptions),
            };

            foreach (var section in root.EnumerateObject())
            {
                if (frameworkSections.Contains(section.Name)) continue;
                Assert.True(optionsSections.ContainsKey(section.Name), $"配置节 “{section.Name}” 没有对应的 Options 类型");
                var type = optionsSections[section.Name];
                var defaults = Activator.CreateInstance(type)!;

                foreach (var entry in section.Value.EnumerateObject())
                {
                    var prop = type.GetProperty(entry.Name, BindingFlags.Public | BindingFlags.Instance);
                    Assert.True(prop is not null, $"{section.Name}.{entry.Name} 在 {type.Name} 上不存在（拼写错误或属性已改名）");

                    var configured = ToComparable(entry.Value);
                    var defaultValue = ToComparable(prop!.GetValue(defaults));
                    Assert.True(configured != defaultValue,
                        $"{section.Name}.{entry.Name} = {configured} 与代码默认值相同——冗余副本会掩盖代码修复，请删除该配置项");
                }
            }
        });
    }

    /// <summary>把 JSON 值与本机值都规整成同一字符串形式，便于比较（去引号、统一小写、数组按元素拼接）。</summary>
    private static string ToComparable(object? value)
    {
        switch (value)
        {
            case null: return "null";
            case JsonElement el when el.ValueKind == JsonValueKind.Array:
                return "[" + string.Join(",", el.EnumerateArray().Select(x => x.ToString().Trim('"'))) + "]";
            case JsonElement el when el.ValueKind == JsonValueKind.String:
                return el.GetString() ?? "";
            case JsonElement el:
                return el.ToString();
            case Array arr:
                return "[" + string.Join(",", arr.Cast<object>()) + "]";
            case bool b:
                return b ? "True" : "False";
            default:
                return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        }
    }

    /// <summary>从测试输出目录向上找到 backend/Mdk.Api/appsettings.json。</summary>
    private static string? FindAppSettings()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "Mdk.Api", "appsettings.json");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
