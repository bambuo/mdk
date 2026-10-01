using System.Globalization;
using System.Text.Json;
using Mdk.Api.Analysis;
using Mdk.Api.Binance;
using Mdk.Api.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Mdk.Tests;

/// <summary>
/// 位点"守住/跌破"检验的执行入口（研究用，不属于自带测试；自带测试仍由 <c>dotnet run</c> 无参执行）：
///
///   dotnet run --project backend/Mdk.Tests -- levels-study [起始日] [结束日] [标的列表] [周期列表]
///   例：dotnet run --project backend/Mdk.Tests -- levels-study 2025-01-01 2026-01-01 BTCUSDT,ETHUSDT 1h,4h
///
/// 用**现货最新价K线**（与线上位点共用同一套纯函数），预注册参数与判据见 <see cref="LevelHoldStudyOptions"/>。
/// 结果写入 tools/verify/levels-study.json，供复核与留档。
/// </summary>
public static class LevelsStudyCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var from = args.Length > 1 ? Date(args[1]) : Date("2025-01-01");
        var to = args.Length > 2 ? Date(args[2]) : Date("2026-01-01");
        var symbols = (args.Length > 3 ? args[3] : "BTCUSDT,ETHUSDT,SOLUSDT,BNBUSDT").Split(',');
        var intervals = (args.Length > 4 ? args[4] : "1h,4h").Split(',');
        var market = args.Length > 5 && args[5].Equals("futures", StringComparison.OrdinalIgnoreCase)
            ? MarketKind.Futures
            : MarketKind.Spot;
        var series = args.Length > 6 && args[6].Equals("mark", StringComparison.OrdinalIgnoreCase)
            ? "mark"
            : "last";
        if (series == "mark" && market != MarketKind.Futures)
        {
            Console.WriteLine("标记价只有合约有（--series mark 需要 market=futures）");
            return 1;
        }

        var options = new LevelHoldStudyOptions();
        var barSeconds = intervals.ToDictionary(i => i, IntervalSeconds, StringComparer.Ordinal);

        Console.WriteLine("位点守住/跌破检验（预注册判据见 LevelHoldStudyOptions）");
        Console.WriteLine($"市场：{market.ToString().ToLowerInvariant()} · 价格序列：{series}"
            + (series == "mark" ? "（标记价——线上合约位点用的就是它）" : "（最新成交价）"));
        Console.WriteLine($"窗口：{args.ElementAtOrDefault(1) ?? "2025-01-01"} ~ {args.ElementAtOrDefault(2) ?? "2026-01-01"}（不含）"
            + $" · 步长 {options.StepBars} 根 · 触碰窗 {options.TouchWindow} 根 · 持有窗 {options.HoldWindow} 根");
        Console.WriteLine($"守住 = 触碰后向位点方向走 {options.BounceAtr}×ATR；跌破 = 收盘穿过位点 {options.BreakAtr}×ATR（同根两者都满足时按跌破）");
        Console.WriteLine();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var rest = new BinanceRestClient(http, Options.Create(new BinanceOptions()), NullLogger<BinanceRestClient>.Instance);

        var events = new List<LevelHoldEvent>();
        foreach (var symbolText in symbols)
        {
            var pair = TradingPair.Parse(symbolText.Trim());
            foreach (var interval in intervals)
            {
                var candles = series == "mark"
                    ? await rest.GetMarkPriceKlinesRangeAsync(pair, interval, from, to)
                    : await rest.GetKlinesRangeAsync(market, pair, interval, from, to);
                var found = LevelHoldStudy.Collect(pair.Symbol, interval, candles, options);
                events.AddRange(found);
                Console.WriteLine($"  {pair.Symbol,-10} {interval,-4} K线 {candles.Length,6} 根 · 事件 {found.Count,6}"
                    + $"（其中对照 {found.Count(e => e.IsControl),6}）");
            }
        }

        Console.WriteLine();
        var result = LevelHoldStudy.Summarize(events, options, barSeconds);
        Print(result, options);

        var suffix = market == MarketKind.Spot ? "" : $"-{market.ToString().ToLowerInvariant()}-{series}";
        var outPath = Path.Combine("tools", "verify", $"levels-study{suffix}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        var payload = new
        {
            generatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            window = new { from = args.ElementAtOrDefault(1) ?? "2025-01-01", to = args.ElementAtOrDefault(2) ?? "2026-01-01" },
            market = market.ToString().ToLowerInvariant(),
            series,
            symbols,
            intervals,
            criteria = new
            {
                minEdge = options.MinEdge,
                minEpisodes = options.MinEpisodes,
                maxTopSymbolShare = options.MaxTopSymbolShare,
                bounceAtr = options.BounceAtr,
                breakAtr = options.BreakAtr,
                touchWindow = options.TouchWindow,
                holdWindow = options.HoldWindow,
            },
            topSymbolShare = result.TopSymbolShare,
            stratifiedEdge = result.StratifiedEdge,
            buckets = result.Buckets,
            positiveBuckets = result.PositiveBuckets,
            coverage = result.Coverage,
            passed = result.Passed,
            reasons = result.Reasons,
            groups = result.Groups.Select(g => new
            {
                label = g.Label, events = g.Events, episodes = g.Episodes,
                held = g.Held, broke = g.Broke, rate = g.Rate, low = g.Low, high = g.High,
            }),
            control = new
            {
                label = result.Control.Label, events = result.Control.Events, episodes = result.Control.Episodes,
                held = result.Control.Held, broke = result.Control.Broke,
                rate = result.Control.Rate, low = result.Control.Low, high = result.Control.High,
            },
        };
        File.WriteAllText(outPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine();
        Console.WriteLine($"结果已写入 {outPath}");
        return 0;
    }

    private static void Print(LevelHoldStudyResult result, LevelHoldStudyOptions options)
    {
        Console.WriteLine("分组（比例按独立波次；95% Wilson 区间）");
        Console.WriteLine("  组别                    事件     波次   守住   跌破   守住率         95% 区间");
        foreach (var g in result.Groups)
        {
            Console.WriteLine($"  {g.Label,-22} {g.Events,6} {g.Episodes,6} {g.Held,6} {g.Broke,6}   {g.Rate,6:P1}   [{g.Low:P1}, {g.High:P1}]");
        }

        var c = result.Control;
        Console.WriteLine($"  {c.Label,-22} {c.Events,6} {c.Episodes,6} {c.Held,6} {c.Broke,6}   {c.Rate,6:P1}   [{c.Low:P1}, {c.High:P1}]");
        Console.WriteLine();
        Console.WriteLine($"距离分层优势：{result.StratifiedEdge:P1}"
            + $"（{result.Buckets} 个档位，其中 {result.PositiveBuckets} 个为正；覆盖 {result.Coverage:P0} 真实波次）");
        Console.WriteLine($"单一标的占比：{result.TopSymbolShare:P0}");
        Console.WriteLine($"判据：分层优势 ≥ {options.MinEdge:P0} · 为正档位 ≥ 2/3 · 波次 ≥ {options.MinEpisodes} · 单标的占比 ≤ {options.MaxTopSymbolShare:P0}");
        Console.WriteLine(result.Passed
            ? "结论：通过（位点的位置比同距离的任意价位更常挡住价格）"
            : "结论：未通过 —— " + string.Join("；", result.Reasons));
    }

    private static long Date(string text) =>
        DateTimeOffset.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal).ToUnixTimeSeconds();

    private static long IntervalSeconds(string interval) => interval switch
    {
        "1m" => 60,
        "5m" => 300,
        "15m" => 900,
        "30m" => 1800,
        "1h" => 3600,
        "4h" => 14_400,
        "1d" => 86_400,
        "1w" => 604_800,
        _ => 3600,
    };
}
