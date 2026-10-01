using Microsoft.Extensions.Configuration;
using Mdk.Api.Configuration;

namespace Mdk.Tests;

/// <summary>
/// 本机密钥文件（<c>appsettings.Local.json</c>）：必须被读入、必须被环境变量覆盖、缺失时必须静默跳过。
///
/// 用 <see cref="ConfigurationManager"/>（与 Program.cs 的 <c>builder.Configuration</c> 同类型）而非裸
/// <c>ConfigurationBuilder</c>：本实现的优先级靠**插入位置**（环境变量之前）而非调用顺序，
/// 若插入位置不生效，文件值会被环境变量源压掉——该行为只在 ConfigurationManager 上成立，故在此锁定。
/// </summary>
public static class LocalSettingsTests
{
    public static void Register(TestKit t)
    {
        t.Case("本机配置_文件存在时键值被读入", () =>
        {
            using var root = TempRoot.Create();
            File.WriteAllText(Path.Combine(root.Path, LocalSettings.FileName), """
                { "Feishu": { "WebhookUrl": "https://example.com/hook/aaa", "Secret": "s3cret" } }
                """);

            var config = Load(root.Path, withEnvVars: false);

            Assert.Equal("https://example.com/hook/aaa", config["Feishu:WebhookUrl"]!);
            Assert.Equal("s3cret", config["Feishu:Secret"]!);
        });

        t.Case("本机配置_环境变量优先于本机文件", () =>
        {
            using var root = TempRoot.Create();
            File.WriteAllText(Path.Combine(root.Path, LocalSettings.FileName), """
                { "Feishu": { "WebhookUrl": "https://example.com/from-file" } }
                """);
            Environment.SetEnvironmentVariable("Feishu__WebhookUrl", "https://example.com/from-env");
            try
            {
                var config = Load(root.Path, withEnvVars: true);

                Assert.Equal("https://example.com/from-env", config["Feishu:WebhookUrl"]!);
            }
            finally
            {
                Environment.SetEnvironmentVariable("Feishu__WebhookUrl", null);
            }
        });

        t.Case("本机配置_文件缺失时静默跳过", () =>
        {
            using var root = TempRoot.Create();

            var config = Load(root.Path, withEnvVars: false);

            Assert.True(string.IsNullOrEmpty(config["Feishu:WebhookUrl"]),
                "密钥文件缺失时应视为未配置（飞书提醒禁用），而不是报错");
        });
    }

    /// <summary>模拟 Program.cs 的加载顺序：默认源（含环境变量）先注册，再把本机文件插到环境变量之前。</summary>
    private static IConfigurationRoot Load(string contentRoot, bool withEnvVars)
    {
        var config = new ConfigurationManager();
        config.AddInMemoryCollection(new Dictionary<string, string?> { ["Feishu:WebhookUrl"] = "" }); // 站位于受版本控制的 appsettings.json
        if (withEnvVars) config.AddEnvironmentVariables();                                            // WebApplicationBuilder 默认已注册
        LocalSettings.Add(config, contentRoot);
        return config;
    }

    /// <summary>临时内容根：用完即删（不注册文件监视，故不存在句柄占用）。</summary>
    private sealed class TempRoot : IDisposable
    {
        public string Path { get; private init; } = "";

        public static TempRoot Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mdk-localsettings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TempRoot { Path = path };
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录清理失败不影响断言结果
            }
        }
    }
}
