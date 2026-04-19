using IBApi;
using IBCollective2Sync;

namespace IBCollective2Sync.Tests;

public class ConfigurationTests
{
    private static Configuration MakeConfig(Dictionary<string, string>? mappings = null)
    {
        var cfg = new Configuration();
        if (mappings != null)
            cfg.SymbolMappings = mappings;
        return cfg;
    }

    private static Contract FuturesContract(string symbol, string localSymbol, string lastTradeDate = "202506")
        => new Contract { SecType = "FUT", Symbol = symbol, LocalSymbol = localSymbol, LastTradeDateOrContractMonth = lastTradeDate };

    private static Contract EquityContract(string symbol)
        => new Contract { SecType = "STK", Symbol = symbol };

    [Fact]
    public void Equity_symbol_returned_as_is()
    {
        var config = MakeConfig();
        var contract = EquityContract("AAPL");
        Assert.Equal("AAPL", config.GetC2Symbol(contract));
    }

    [Fact]
    public void Futures_local_symbol_gets_at_prefix()
    {
        var config = MakeConfig();
        var contract = FuturesContract("ES", "ESH6");
        Assert.Equal("@ESH6", config.GetC2Symbol(contract));
    }

    [Fact]
    public void Futures_without_local_symbol_uses_root_plus_date()
    {
        var config = MakeConfig();
        var contract = FuturesContract("ES", "", "202506");
        Assert.Equal("@ES202506", config.GetC2Symbol(contract));
    }

    [Fact]
    public void Symbol_mapping_replaces_root_in_local_symbol()
    {
        var config = MakeConfig(new Dictionary<string, string> { ["MGC"] = "@QMGC" });
        var contract = FuturesContract("MGC", "MGCG6");
        Assert.Equal("@QMGCG6", config.GetC2Symbol(contract));
    }

    [Fact]
    public void Hardcoded_MGC_fallback_applies_when_no_mapping()
    {
        var config = MakeConfig();
        var contract = FuturesContract("MGC", "MGCG6");
        Assert.Equal("@QMGCG6", config.GetC2Symbol(contract));
    }

    [Fact]
    public void Symbol_already_prefixed_with_at_is_not_double_prefixed()
    {
        var config = MakeConfig(new Dictionary<string, string> { ["MBT"] = "@MBT" });
        var contract = FuturesContract("MBT", "MBTG6");
        Assert.Equal("@MBTG6", config.GetC2Symbol(contract));
    }
}
