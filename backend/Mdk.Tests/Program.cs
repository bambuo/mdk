using Mdk.Api.Domain;

namespace Mdk.Tests;

/// <summary>
/// 入口：无参数 = 跑自带测试（exit 0 全绿）；`levels-study ...` = 跑位点守住/跌破检验（研究用，见 LevelsStudyCommand）。
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "levels-study")
            return LevelsStudyCommand.RunAsync(args).GetAwaiter().GetResult();

        Console.WriteLine("MDK 自带测试（零外部依赖）");
        Console.WriteLine();

        var kit = new TestKit();
        TradingPairTests.Register(kit);
        DecimalJsonTests.Register(kit);
        SignalStoreTests.Register(kit);
        EvidenceTests.Register(kit);
        StructurePositionTests.Register(kit);
        PriceLevelTests.Register(kit);
        LeverageTests.Register(kit);
        VolumeProfileTests.Register(kit);
        ConfigGuardTests.Register(kit);
        LocalSettingsTests.Register(kit);
        AttentionTests.Register(kit);
        WatchlistSignalBroadcasterTests.Register(kit);
        FeishuNotifierTests.Register(kit);
        TradingPairEmptyTests.Register(kit);
        WilderSmoothingTests.Register(kit);
        RsiTests.Register(kit);
        AtrTests.Register(kit);
        AdxDmiTests.Register(kit);
        MacdTests.Register(kit);
        BollingerBandsTests.Register(kit);
        AnalysisEngineTests.Register(kit);
        JointScoreTests.Register(kit);
        ChanTests.Register(kit);
        ChanBackfillTests.Register(kit);
        ConfluenceTaggerTests.Register(kit);
        ChanLevelMapperTests.Register(kit);

        return kit.RunAll();
    }
}
