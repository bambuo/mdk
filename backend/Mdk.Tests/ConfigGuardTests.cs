using System.Reflection;
using System.Text.Json;
using Mdk.Api.Analysis;
using Mdk.Api.Analysis.Chan;
using Mdk.Api.Binance;
using Mdk.Api.Configuration;
using Mdk.Api.Notify;

namespace Mdk.Tests;

/// <summary>
/// 配置守卫：appsettings.json 只允许写**真实覆盖项**。
///
/// 为什么需要它（2026-09-30 的教训）：配置里曾有一份与代码默认值完全相同的副本
/// （`Binance.FuturesWsBaseUrl`），代码改成正确的分区地址后**被配置静默覆盖**，
/// 于是"修了却不生效"。副本还会掩盖重命名——键名拼错/属性改名后配置被静默忽略。
///
/// 四道断言：
///   ① 每个配置键必须在对应 Options 类型上存在（防拼写错误与属性改名后的残留）；
///   ② 每个配置值必须与代码默认值**不同**（防冗余副本掩盖代码修复）；
///   ③ 受版本控制的配置里不得出现凭据（飞书 Webhook 等价于该群的发消息权限，随提交进 git 历史即等于公开）；
///   ④ 本机密钥文件必须在 .gitignore 里——③④ 是一对：凭据只能待在不被跟踪的文件或环境变量中。
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
                ["Levels"] = typeof(PriceLevelOptions),
                ["Backfill"] = typeof(BackfillOptions),
                ["Feishu"] = typeof(FeishuOptions),
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

        t.Case("配置守卫_受版本控制的配置不得含凭据", () =>
        {
            var path = FindAppSettings();
            Assert.True(path is not null, "未找到 Mdk.Api/appsettings.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path!));

            AssertNoCredentials(doc.RootElement, "");
        });

        t.Case("配置守卫_本机密钥文件必须被 gitignore 忽略", () =>
        {
            var path = FindRepoFile(".gitignore");
            Assert.True(path is not null, "未找到仓库根 .gitignore");
            var text = File.ReadAllText(path!);

            Assert.True(text.Contains(LocalSettings.FileName, StringComparison.Ordinal),
                $".gitignore 必须忽略 {LocalSettings.FileName}——凭据文件一旦被跟踪，提交即等于公开");
        });
    }

    /// <summary>递归扫描：键名疑似凭据而值非空即失败（凭据只允许出现在本机密钥文件或环境变量中）。</summary>
    private static void AssertNoCredentials(JsonElement element, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                    AssertNoCredentials(prop.Value, path.Length == 0 ? prop.Name : $"{path}:{prop.Name}");
                break;
            case JsonValueKind.String:
                if (LooksLikeCredential(path) && !string.IsNullOrWhiteSpace(element.GetString()))
                {
                    throw new InvalidOperationException(
                        $"受版本控制的配置里出现凭据 “{path}”——请移入 {LocalSettings.FileName}（已 gitignore）" +
                        "或改用环境变量（如 Feishu__WebhookUrl）");
                }
                break;
        }
    }

    private static bool LooksLikeCredential(string path)
    {
        var key = path.Split(':')[^1];
        return key.Contains("Secret", StringComparison.OrdinalIgnoreCase)
            || key.Contains("Webhook", StringComparison.OrdinalIgnoreCase)
            || key.Contains("Token", StringComparison.OrdinalIgnoreCase)
            || key.Contains("Password", StringComparison.OrdinalIgnoreCase)
            || key.Contains("ApiKey", StringComparison.OrdinalIgnoreCase);
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

    /// <summary>从测试输出目录向上找到仓库根下的文件（.gitignore 等）。</summary>
    private static string? FindRepoFile(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, fileName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
