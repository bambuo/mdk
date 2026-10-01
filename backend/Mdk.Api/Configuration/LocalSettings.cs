using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;

namespace Mdk.Api.Configuration;

/// <summary>
/// 本机密钥文件 <c>appsettings.Local.json</c>：承载**不入版本控制**的配置（当前为飞书 Webhook 与签名密钥）。
///
/// 为什么单列一个文件：受版本控制的 appsettings.json 会随提交进入 git 历史，而飞书 Webhook 等价于
/// "该群的发消息权限"——写进去即等于公开（2026-09-30 的教训：只能靠轮换地址补救）。
///
/// 优先级：本机文件插在**环境变量之前**，故顺序为
/// <c>appsettings.json &lt; appsettings.{环境}.json &lt; appsettings.Local.json &lt; 环境变量 &lt; 命令行</c>。
/// 生产环境用环境变量注入（<c>Feishu__WebhookUrl</c> / <c>Feishu__Secret</c>），不部署该文件。
/// </summary>
public static class LocalSettings
{
    public const string FileName = "appsettings.Local.json";

    /// <summary>载入本机密钥文件；文件缺失即跳过（未配置飞书提醒是正常状态，不是错误）。</summary>
    public static void Add(IConfigurationBuilder builder, string contentRootPath)
    {
        var sources = builder.Sources;
        var at = sources.Count;
        for (var i = 0; i < sources.Count; i++)
        {
            if (sources[i] is EnvironmentVariablesConfigurationSource)
            {
                at = i;
                break;
            }
        }

        sources.Insert(at, new JsonConfigurationSource
        {
            Path = FileName,
            Optional = true,
            FileProvider = new PhysicalFileProvider(contentRootPath),
        });
    }
}
