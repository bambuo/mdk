using Mdk.Api.Domain;

namespace Mdk.Tests;

/// <summary>测试入口：dotnet run --project backend/Mdk.Tests。退出码 0 = 全部通过。</summary>
public static class Program
{
    public static int Main()
    {
        Console.WriteLine("MDK 自带测试（零外部依赖）");
        Console.WriteLine();

        var kit = new TestKit();
        TradingPairTests.Register(kit);
        DecimalJsonTests.Register(kit);
        SignalStoreTests.Register(kit);
        EvidenceTests.Register(kit);
        StructurePositionTests.Register(kit);
        ConfigGuardTests.Register(kit);
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
