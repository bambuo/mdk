namespace Mdk.Api.Domain;

/// <summary>来自币安 exchangeInfo 的交易对静态信息（现货与合约通用），交易币/计价币拆分的权威来源。</summary>
public sealed record SymbolInfo(TradingPair Pair, string Status);
