using HWEncVppPreview.Core;
using HWEncVppPreview.Tests.Testing;

namespace HWEncVppPreview.Tests;

public sealed class PreviewTimeoutsTests
{
    [Fact]
    public void DefaultTimeoutsPreserveProductionValues()
    {
        Assert.Equal(new[]
        {
            TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30),
        }, Values(PreviewTimeouts.Default));
        Assert.All(Values(PreviewTimeouts.Infinite), value => Assert.Equal(Timeout.InfiniteTimeSpan, value));
    }

    [Fact]
    public async Task PublicTimeoutsRemainFiniteAndOnlyInternalConstructionAcceptsInfinite()
    {
        TimeSpan infinite = Timeout.InfiniteTimeSpan;
        Assert.Equal("readyTimeout", Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ServerBackend(readyTimeout: infinite)).ParamName);
        Assert.Equal("probeTimeout", Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ServerBackend(probeTimeout: infinite)).ParamName);
        Assert.Equal("cancelTimeout", Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ServerBackend(cancelTimeout: infinite)).ParamName);
        Assert.Equal("stopTimeout", Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ServerBackend(stopTimeout: infinite)).ParamName);
        Assert.Equal("timeout", Assert.Throws<ArgumentOutOfRangeException>(() =>
            new InputProbe(timeout: infinite)).ParamName);

        Assert.Equal("timeout", Assert.Throws<ArgumentOutOfRangeException>(() =>
            new InputProbe(timeout: TimeSpan.Zero)).ParamName);

        Assert.Equal("watchdog", Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PreviewController(watchdog: infinite)).ParamName);
        Assert.Equal("disposeGrace", Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PreviewController(disposeGrace: infinite)).ParamName);
        Assert.Equal("disposeTimeout", Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PreviewController(disposeTimeout: infinite)).ParamName);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PreviewTimeouts.Validate(TimeSpan.FromTicks(-1), "timeout"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PreviewTimeouts.Validate(TimeSpan.Zero, "timeout"));

        await using var probe = TestProcesses.CreateInputProbe();
        await using var controller = TestProcesses.CreateController(TestProcesses.CreateServerBackend());
    }

    [Fact]
    public void CleanupDeadlineKeepsInfiniteAndClampsFiniteUsingInjectedClock()
    {
        var clock = new FakeTimeProvider();
        var unlimited = new CleanupDeadline(clock, Timeout.InfiniteTimeSpan);
        var finite = new CleanupDeadline(clock, TimeSpan.FromSeconds(5));
        var quit = new CleanupDeadline(clock, CleanupDeadline.Half(TimeSpan.FromSeconds(5)));
        Assert.Equal(Timeout.InfiniteTimeSpan, CleanupDeadline.Half(unlimited.Remaining));
        Assert.Equal(finite.Remaining, CleanupDeadline.Shorter(finite.Remaining, unlimited.Remaining));
        Assert.Equal(quit.Remaining, CleanupDeadline.Shorter(unlimited.Remaining, quit.Remaining));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromMilliseconds(500), quit.Remaining);
        Assert.Equal(TimeSpan.FromSeconds(3), finite.Remaining);
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(TimeSpan.Zero, finite.Remaining);
        Assert.Equal(Timeout.InfiniteTimeSpan, unlimited.Remaining);
    }

    private static TimeSpan[] Values(PreviewTimeouts timeouts) =>
    [
        timeouts.Ready, timeouts.Probe, timeouts.Cancel, timeouts.Stop,
        timeouts.Input, timeouts.InputCleanup, timeouts.Watchdog,
        timeouts.DisposeGrace, timeouts.ControllerDispose, timeouts.Metadata,
        timeouts.ViewModelDispose, timeouts.CliHelp, timeouts.HelpCleanup, timeouts.Initialization,
    ];
}
