using IBCollective2Sync;

namespace IBCollective2Sync.Tests;

public class SyncActionTests
{
    private const double Threshold = 0.01;

    [Fact]
    public void No_action_when_both_sides_zero()
    {
        var result = SyncLogic.DetermineAction(0, 0, Threshold);
        Assert.Null(result);
    }

    [Fact]
    public void STC_when_IB_flat_and_C2_long()
    {
        var result = SyncLogic.DetermineAction(0, 5, Threshold);
        Assert.NotNull(result);
        Assert.Equal("STC", result!.Value.action);
        Assert.Equal(5, result!.Value.quantity);
    }

    [Fact]
    public void BTC_when_IB_flat_and_C2_short()
    {
        var result = SyncLogic.DetermineAction(0, -3, Threshold);
        Assert.NotNull(result);
        Assert.Equal("BTC", result!.Value.action);
        Assert.Equal(3, result!.Value.quantity);
    }

    [Fact]
    public void BTO_when_IB_long_and_C2_flat()
    {
        var result = SyncLogic.DetermineAction(10, 0, Threshold);
        Assert.NotNull(result);
        Assert.Equal("BTO", result!.Value.action);
        Assert.Equal(10, result!.Value.quantity);
    }

    [Fact]
    public void BTC_when_IB_long_and_C2_short()
    {
        var result = SyncLogic.DetermineAction(5, -3, Threshold);
        Assert.NotNull(result);
        Assert.Equal("BTC", result!.Value.action);
        Assert.Equal(8, result!.Value.quantity);
    }

    [Fact]
    public void STC_when_reducing_existing_long()
    {
        var result = SyncLogic.DetermineAction(3, 5, Threshold);
        Assert.NotNull(result);
        Assert.Equal("STC", result!.Value.action);
        Assert.Equal(2, result!.Value.quantity);
    }

    [Fact]
    public void STO_when_IB_short_and_C2_flat()
    {
        var result = SyncLogic.DetermineAction(-5, 0, Threshold);
        Assert.NotNull(result);
        Assert.Equal("STO", result!.Value.action);
        Assert.Equal(5, result!.Value.quantity);
    }

    [Fact]
    public void No_action_when_C2_also_zero_and_IB_below_threshold()
    {
        var result = SyncLogic.DetermineAction(0.005, 0, Threshold);
        Assert.Null(result);
    }
}
