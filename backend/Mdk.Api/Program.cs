using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mdk.Api.Analysis;
using Mdk.Api.Analysis.Chan;
using Mdk.Api.Binance;
using Mdk.Api.Domain;
using Mdk.Api.Endpoints;
using Mdk.Api.Notify;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<BinanceOptions>(builder.Configuration.GetSection(BinanceOptions.SectionName));
builder.Services.Configure<SignalOptions>(builder.Configuration.GetSection(SignalOptions.SectionName));
builder.Services.Configure<ChanOptions>(builder.Configuration.GetSection(ChanOptions.SectionName));
builder.Services.Configure<BackfillOptions>(builder.Configuration.GetSection(BackfillOptions.SectionName));
builder.Services.Configure<FeishuOptions>(builder.Configuration.GetSection(FeishuOptions.SectionName));
// exchangeInfo（现货约 17MB）必须启用压缩传输并放宽超时，否则会下载超时
builder.Services.AddHttpClient<BinanceRestClient>(client => client.Timeout = TimeSpan.FromSeconds(60))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
    });
builder.Services.AddSingleton<SymbolCatalog>();
builder.Services.AddSingleton<KlineStreamService>();
builder.Services.AddSingleton<AnalysisService>();
builder.Services.AddSingleton(sp => new SignalStore(
    Path.Combine(sp.GetRequiredService<IHostEnvironment>().ContentRootPath, "data"),
    sp.GetRequiredService<ILogger<SignalStore>>()));
builder.Services.AddHostedService<SignalOutcomeService>();
// 监控列表（用户指定、持久化）与后台监控服务
builder.Services.AddSingleton(sp => new WatchlistStore(
    Path.Combine(sp.GetRequiredService<IHostEnvironment>().ContentRootPath, "data"),
    sp.GetRequiredService<ILogger<WatchlistStore>>()));
builder.Services.AddSingleton<WatchlistMonitorService>();
// 监控列表信号广播器：订阅台账入库事件，供 SSE 端点推送
builder.Services.AddSingleton<WatchlistSignalBroadcaster>();
// 飞书提醒：未配置 WebhookUrl 时惰性禁用（见 FeishuNotifier）。
// **必须是单例**：AddHttpClient<T> 会把 T 注册为 transient，而每个实例都会订阅广播事件 →
// 自检端点每被调用一次就多一个订阅者（重复发送 + 泄漏）。故用工厂 HttpClient + 单例注册。
builder.Services.AddHttpClient(nameof(FeishuNotifier));
builder.Services.AddSingleton(sp => new FeishuNotifier(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(FeishuNotifier)),
    sp.GetRequiredService<IOptions<FeishuOptions>>(),
    sp.GetRequiredService<WatchlistSignalBroadcaster>(),
    sp.GetRequiredService<ILogger<FeishuNotifier>>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<FeishuNotifier>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<WatchlistMonitorService>());
builder.Services.AddSingleton<SignalBackfillService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SignalBackfillService>());

// 枚举以小写参与 JSON（market: "spot" | "futures"）；decimal 去掉标度与伪精度（见 DecimalJsonConverter）
var enumConverter = new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(enumConverter);
    options.SerializerOptions.Converters.Add(new DecimalJsonConverter());
    options.SerializerOptions.Converters.Add(new NullableDecimalJsonConverter());
});

// 前端开发服务器来源（生产同源部署时无需 CORS）
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();

app.UseCors();
app.UseWebSockets(); // 端点内 AcceptWebSocketAsync 的前置要求

app.MapGet("/api/health", () => Results.Ok(new
{
    status = "ok",
    time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
}));

RestEndpoints.Map(app);
WatchlistEndpoints.Map(app);
WsEndpoints.Map(app);

app.Run();
