using SpireLens.Core.Pad;
using Xunit;

namespace SpireLens.Core.Tests;

public class RightStickClickArbiterTests
{
    [Fact]
    public void SteamInputOn_GameInputAloneDispatches()
    {
        var arbiter = new RightStickClickArbiter();

        Assert.True(arbiter.OnGameInputPress());
        Assert.True(arbiter.OnGameInputPress());
    }

    [Fact]
    public void SteamInputOff_RawPressGameInputSeesIsDroppedAndGameInputDispatches()
    {
        var arbiter = new RightStickClickArbiter();

        Assert.False(arbiter.OnRawPress(gameInputSeesDown: true));
        Assert.True(arbiter.OnGameInputPress());
        arbiter.OnRawRelease();
    }

    [Fact]
    public void PadGameInputCannotSee_RawDispatchesEveryClick()
    {
        var arbiter = new RightStickClickArbiter();

        Assert.True(arbiter.OnRawPress(gameInputSeesDown: false));
        arbiter.OnRawRelease();
        Assert.True(arbiter.OnRawPress(gameInputSeesDown: false));
    }

    [Fact]
    public void GameInputCatchingUpOnARawClick_DoesNotToggleTwice()
    {
        var arbiter = new RightStickClickArbiter();

        Assert.True(arbiter.OnRawPress(gameInputSeesDown: false));
        Assert.False(arbiter.OnGameInputPress());
        arbiter.OnRawRelease();

        Assert.True(arbiter.OnGameInputPress());
    }
}
