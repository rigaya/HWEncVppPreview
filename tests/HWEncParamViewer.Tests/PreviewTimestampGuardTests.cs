using HWEncParamViewer.Core;

namespace HWEncParamViewer.Tests;

public sealed class PreviewTimestampGuardTests
{
    [Fact]
    public void EqualAbsoluteTimestampsRequireLegacyReplay()
    {
        var guard = new PreviewTimestampGuard(30);
        var header = new Y4mHeader { Width = 2, Height = 2 };
        Assert.False(guard.IsInvalid(new(header, 1000, 1.0 / 30, new byte[6])));
        Assert.True(guard.IsInvalid(new(header, 1000, 1.0 / 30, new byte[6])));
        Assert.Contains("増加していません", guard.Reason);
    }
    [Fact]
    public void MissingTimestampDoesNotHideBackstep()
    {
        var guard = new PreviewTimestampGuard(30);
        var header = new Y4mHeader { Width = 2, Height = 2 };
        Assert.False(guard.IsInvalid(new(header, 100, 1.0 / 30, new byte[6])));
        Assert.False(guard.IsInvalid(new(header, null, 1.0 / 30, new byte[6])));
        Assert.True(guard.IsInvalid(new(header, 99, 1.0 / 30, new byte[6])));
    }
}
