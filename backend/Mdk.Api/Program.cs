using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mdk.Api.Analysis;
using Mdk.Api.Analysis.Chan;
using Mdk.Api.Binance;
using Mdk.Api.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<BinanceOptions>(builder.Configuration.GetSection(BinanceOptions.SectionName));
builder.Services.Configure<SignalOptions>(builder.Configuration.GetSection(SignalOptions.SectionName));
builder.Services.Configure<ChanOptions>(builder.Configuration.GetSection(ChanOptions.SectionName));
builder.Services.Configure<BackfillOptions>(builder.Configuration.GetSection(BackfillOptions.SectionName));
// exchangeInfo（现货约 17MB）必须启用压缩传输并放宽超时，否则会下载超时
builder.Services.AddHttpClient<BinanceRestClient>(client => client.Timeout = TimeSpan.FromSeconds(60))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
    });
builder.Services.AddSingleton<SymbolCatalog>();
builder.Services.AddSingleton<KlineStreamService>();
builder.Services.AddSingleton<AnalysisService>();
builder.Services.AddSingleton<SignalJournal>();
builder.Services.AddHostedService<SignalOutcomeService>();
builder.Services.AddHostedService<WatchlistAnalysisService>();
builder.Services.AddSingleton<SignalBackfillService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SignalBackfillService>());

// 枚举以小写参与 JSON（market: "spot" | "futures"）
var enumConverter = new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(enumConverter);
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
WsEndpoints.Map(app);

app.Run();
