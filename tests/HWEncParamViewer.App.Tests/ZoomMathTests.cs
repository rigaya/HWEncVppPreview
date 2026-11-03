using Avalonia;
using HWEncParamViewer.App.Controls;

namespace HWEncParamViewer.App.Tests;

public sealed class ZoomMathTests
{
    [Theory]
    [InlineData(800, 600, 1920, 1080, 0.4166666666666667, 0, 75, 800, 450)]
    [InlineData(800, 600, 600, 1200, 0.5, 250, 0, 300, 600)]
    [InlineData(1200, 800, 600, 400, 2, 0, 0, 1200, 800)]
    [InlineData(1000, 1000, 1920, 1080, 0.5208333333333334, 0, 218.75, 1000, 562.5)]
    public void FitPreservesAspectRatioAndCentersTheImage(double vw, double vh, double cw, double ch,
        double scale, double x, double y, double width, double height)
    {
        var viewport = new Size(vw, vh);
        var content = new Size(cw, ch);
        Assert.Equal(scale, ZoomMath.FitScale(viewport, content), 10);
        AssertRect(new Rect(x, y, width, height), ZoomMath.ContentRect(ZoomState.Fit, viewport, content));
    }

    [Theory]
    [InlineData(0, 600, 1920, 1080)]
    [InlineData(800, 0, 1920, 1080)]
    [InlineData(800, 600, 0, 1080)]
    [InlineData(800, 600, 1920, 0)]
    public void AnEmptyViewportOrImageHasNeutralFitScale(double vw, double vh, double cw, double ch)
    {
        Assert.Equal(1, ZoomMath.FitScale(new(vw, vh), new(cw, ch)));
    }

    [Fact]
    public void FitMayShowALargeImageBelowTheManualZoomMinimum()
    {
        // 自動フィットは大きな画像を全体表示できるようにし、25%には切り上げない。
        var viewport = new Size(100, 100);
        var content = new Size(1000, 1000);
        Assert.Equal(0.1, ZoomMath.FitScale(viewport, content), 10);
        AssertRect(new Rect(0, 0, 100, 100), ZoomMath.ContentRect(ZoomState.Fit, viewport, content));
    }

    [Fact]
    public void ZoomingOutFromASmallFitUsesFitAsTheManualLowerBound()
    {
        // 自動フィットが25%未満なら、その表示まで手動で戻れる。
        var viewport = new Size(100, 100);
        var content = new Size(1000, 1000);
        var cursor = new Point(75, 25);
        var zoomed = ZoomMath.ZoomAt(ZoomState.Fit, viewport, content, cursor, 1 / 1.25);
        Assert.False(zoomed.IsFit);
        Assert.Equal(0.1, zoomed.Scale, 10);
        AssertRect(new Rect(0, 0, 100, 100), ZoomMath.ContentRect(zoomed, viewport, content));
        AssertPoint(cursor, Project(new(750, 250), zoomed, viewport, content));
    }

    [Fact]
    public void ZoomingInFromASmallFitDoesNotJumpToTwentyFivePercent()
    {
        var viewport = new Size(100, 100);
        var content = new Size(1000, 1000);
        var cursor = new Point(75, 25);
        var zoomed = ZoomMath.ZoomAt(ZoomState.Fit, viewport, content, cursor, 1.25);
        Assert.False(zoomed.IsFit);
        Assert.Equal(0.125, zoomed.Scale, 10);
        AssertPoint(new(550, 450), zoomed.Center);
        AssertPoint(cursor, Project(new(750, 250), zoomed, viewport, content));
    }

    [Fact]
    public void ManualZoomBelowTwentyFivePercentClampsAtTheSmallFit()
    {
        var viewport = new Size(100, 100);
        var content = new Size(1000, 1000);
        var state = new ZoomState(false, 0.125, new(400, 600));
        var cursor = new Point(75, 25);
        // 画面上の(75,25)には、拡大前の画像上の(600,400)がある。
        var zoomed = ZoomMath.ZoomAt(state, viewport, content, cursor, 0.5);
        Assert.Equal(0.1, zoomed.Scale, 10);
        AssertPoint(new(350, 650), zoomed.Center);
        AssertPoint(cursor, Project(new(600, 400), zoomed, viewport, content));
    }

    [Fact]
    public void FitIgnoresTheDormantManualScaleAndCenter()
    {
        var state = new ZoomState(true, 12, new(-500, 2000));
        Assert.Equal(0.5, ZoomMath.EffectiveScale(state, new(800, 600), new(1600, 900)), 10);
        AssertRect(new Rect(0, 75, 800, 450), ZoomMath.ContentRect(state, new(800, 600), new(1600, 900)));
    }

    [Fact]
    public void ManualRectPlacesTheChosenContentPointAtTheViewportCenter()
    {
        var state = new ZoomState(false, 2, new(400, 300));
        AssertRect(new Rect(-300, -250, 1600, 1200), ZoomMath.ContentRect(state, new(1000, 700), new(800, 600)));
        AssertPoint(new(500, 350), Project(state.Center, state, new(1000, 700), new(800, 600)));
    }

    [Fact]
    public void ZoomUsesTheSpecifiedWheelStepAndKeepsTheCursorAnchor()
    {
        Assert.Equal(1.25, ZoomMath.WheelStep);
        var viewport = new Size(1000, 700);
        var content = new Size(1600, 900);
        var state = new ZoomState(false, 2, new(400, 300));
        var cursor = new Point(700, 150);
        // 元の中心と2倍表示から、このカーソルの下はコンテンツの(500,200)と分かる。
        var anchor = new Point(500, 200);
        AssertPoint(cursor, Project(anchor, state, viewport, content));
        var zoomed = ZoomMath.ZoomAt(state, viewport, content, cursor, ZoomMath.WheelStep);
        Assert.False(zoomed.IsFit);
        Assert.Equal(2.5, zoomed.Scale, 10);
        AssertPoint(new(420, 280), zoomed.Center);
        AssertPoint(cursor, Project(anchor, zoomed, viewport, content));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(800, 600)]
    [InlineData(400, 300)]
    [InlineData(650, 200)]
    public void ZoomFromLetterboxedFitKeepsTheCursorAnchor(double x, double y)
    {
        var viewport = new Size(800, 600);
        var content = new Size(1920, 1080);
        var cursor = new Point(x, y);
        // フィット後は800x450、上余白75。逆写像は画像の画素比から独立に求める。
        var anchor = new Point(x * 2.4, (y - 75) * 2.4);
        var zoomed = ZoomMath.ZoomAt(ZoomState.Fit, viewport, content, cursor, 1.25);
        Assert.Equal(0.5208333333333334, zoomed.Scale, 10);
        AssertPoint(cursor, Project(anchor, zoomed, viewport, content));
    }

    [Fact]
    public void OppositeZoomStepsRestoreTheOriginalGeometry()
    {
        var viewport = new Size(800, 600);
        var content = new Size(1600, 900);
        var state = new ZoomState(false, 2, new(500, 350));
        var cursor = new Point(650, 100);
        var zoomed = ZoomMath.ZoomAt(state, viewport, content, cursor, 1.25);
        var restored = ZoomMath.ZoomAt(zoomed, viewport, content, cursor, 1 / 1.25);
        Assert.Equal(state.Scale, restored.Scale, 10);
        AssertPoint(state.Center, restored.Center);
        AssertRect(ZoomMath.ContentRect(state, viewport, content), ZoomMath.ContentRect(restored, viewport, content));
    }

    [Fact]
    public void OppositeZoomStepsFromFitRestoreTheLetterbox()
    {
        var viewport = new Size(800, 600);
        var content = new Size(1920, 1080);
        var cursor = new Point(650, 200);
        var zoomed = ZoomMath.ZoomAt(ZoomState.Fit, viewport, content, cursor, 1.25);
        var restored = ZoomMath.ZoomAt(zoomed, viewport, content, cursor, 1 / 1.25);
        Assert.False(restored.IsFit);
        AssertRect(new Rect(0, 75, 800, 450), ZoomMath.ContentRect(restored, viewport, content));
    }

    [Theory]
    [InlineData(31, 1.25, 32)]
    [InlineData(0.26, 0.8, 0.25)]
    public void ManualZoomClampsAtTheSpecifiedBoundsWhenFitIsLarger(double initial, double factor, double expected)
    {
        Assert.Equal(0.25, ZoomMath.MinScale);
        Assert.Equal(32, ZoomMath.MaxScale);
        var viewport = new Size(800, 600);
        var content = new Size(800, 600);
        var state = new ZoomState(false, initial, new(300, 200));
        var cursor = new Point(500, 400);
        var anchor = new Point(300 + 100 / initial, 200 + 100 / initial);
        var zoomed = ZoomMath.ZoomAt(state, viewport, content, cursor, factor);
        Assert.Equal(expected, zoomed.Scale, 10);
        AssertPoint(cursor, Project(anchor, zoomed, viewport, content));
    }

    [Fact]
    public void ActualSizeFromFitShowsOneContentPixelPerScreenPixel()
    {
        var viewport = new Size(800, 600);
        var content = new Size(1920, 1080);
        var actual = ZoomMath.ActualSize(ZoomState.Fit, viewport, content);
        Assert.False(actual.IsFit);
        Assert.Equal(1, actual.Scale);
        AssertPoint(new(960, 540), actual.Center);
        AssertRect(new Rect(-560, -240, 1920, 1080), ZoomMath.ContentRect(actual, viewport, content));
    }

    [Fact]
    public void ActualSizePreservesAnAlreadyPannedContentCenter()
    {
        var state = new ZoomState(false, 3, new(300, 200));
        var actual = ZoomMath.ActualSize(state, new(1000, 700), new(1600, 900));
        Assert.Equal(1, actual.Scale);
        AssertPoint(new(300, 200), actual.Center);
    }

    [Fact]
    public void PanMovesTheImageByThePointerDelta()
    {
        var viewport = new Size(1000, 700);
        var content = new Size(800, 600);
        var state = new ZoomState(false, 2, new(400, 300));
        var delta = new Vector(120, -60);
        var moved = ZoomMath.Pan(state, viewport, content, delta);
        Assert.False(moved.IsFit);
        Assert.Equal(2, moved.Scale);
        AssertPoint(new(340, 330), moved.Center);
        AssertRect(new Rect(-180, -310, 1600, 1200), ZoomMath.ContentRect(moved, viewport, content));
        var before = Project(new(100, 100), state, viewport, content);
        var after = Project(new(100, 100), moved, viewport, content);
        AssertPoint(new(before.X + delta.X, before.Y + delta.Y), after);
    }

    [Fact]
    public void PanFromFitStartsAtTheCurrentLetterboxedPosition()
    {
        var viewport = new Size(800, 600);
        var content = new Size(1920, 1080);
        var moved = ZoomMath.Pan(ZoomState.Fit, viewport, content, new(100, -50));
        Assert.False(moved.IsFit);
        Assert.Equal(0.4166666666666667, moved.Scale, 10);
        AssertPoint(new(720, 660), moved.Center);
        AssertRect(new Rect(100, 25, 800, 450), ZoomMath.ContentRect(moved, viewport, content));
    }

    [Fact]
    public void SharedStateKeepsTheSameImagePointAtBothViewportCenters()
    {
        var content = new Size(1920, 1080);
        var left = new Size(800, 600);
        var right = new Size(420, 400);
        var state = new ZoomState(false, 1.5, new(1000, 600));
        AssertPoint(new(400, 300), Project(state.Center, state, left, content));
        AssertPoint(new(210, 200), Project(state.Center, state, right, content));
        var imagePoint = new Point(1100, 650);
        var lp = Project(imagePoint, state, left, content);
        var rp = Project(imagePoint, state, right, content);
        Assert.Equal(lp.X - 400, rp.X - 210, 10);
        Assert.Equal(lp.Y - 300, rp.Y - 200, 10);
        Assert.Equal(1.5, ZoomMath.EffectiveScale(state, left, content));
        Assert.Equal(1.5, ZoomMath.EffectiveScale(state, right, content));
    }

    [Theory]
    [InlineData(0.25, "25%")]
    [InlineData(1, "100%")]
    [InlineData(2, "200%")]
    [InlineData(32, "3200%")]
    public void FormatShowsTheManualPercentage(double scale, string expected)
    {
        Assert.Equal(expected, ZoomMath.Format(new(false, scale, new(10, 20)), new(800, 600), new(1600, 900)));
    }

    [Fact]
    public void FormatShowsTheEffectiveFitPercentage()
    {
        Assert.Equal("50%", ZoomMath.Format(ZoomState.Fit, new(800, 600), new(1600, 900)));
    }

    // ---- 高 DPI (OS の表示倍率 150% など。拡大率は物理画素で数える) ----

    [Fact]
    public void ActualSizeAt150PercentMapsOneImagePixelToOneDevicePixel()
    {
        var viewport = new Size(800, 600);
        var content = new Size(1920, 1080);
        var actual = ZoomMath.ActualSize(ZoomState.Fit, viewport, content, 1.5);
        Assert.Equal(1.0, actual.Scale);
        // DIP では 1/1.5 の大きさに描く = 物理画素では 1920x1080
        var rect = ZoomMath.ContentRect(actual, viewport, content, 1.5);
        Assert.Equal(1920, rect.Width * 1.5, 9);
        Assert.Equal(1080, rect.Height * 1.5, 9);
        Assert.Equal("100%", ZoomMath.Format(actual, viewport, content, 1.5));
        Assert.Equal(1.0, ZoomMath.DeviceScale(actual, viewport, content, 1.5), 9);
    }

    [Fact]
    public void TheFitPercentageCountsDevicePixels()
    {
        // 800 DIP の幅に 1600 を合わせると DIP では 50%、150% 表示の物理画素では 75%
        Assert.Equal("75%", ZoomMath.Format(ZoomState.Fit, new(800, 600), new(1600, 900), 1.5));
    }

    [Fact]
    public void ZoomAndPanAt150PercentKeepTheDeviceScaleAndTheAnchor()
    {
        var viewport = new Size(1000, 700);
        var content = new Size(1600, 900);
        var cursor = new Point(320, 240);
        var state = new ZoomState(false, 2, new(500, 350));
        var zoomed = ZoomMath.ZoomAt(state, viewport, content, cursor, 1.25, 1.5);
        Assert.Equal(2.5, zoomed.Scale, 9);
        // カーソルの下の点は動かない
        var before = ZoomMath.ContentRect(state, viewport, content, 1.5);
        var after = ZoomMath.ContentRect(zoomed, viewport, content, 1.5);
        double beforeScale = ZoomMath.EffectiveScale(state, viewport, content, 1.5);
        double afterScale = ZoomMath.EffectiveScale(zoomed, viewport, content, 1.5);
        AssertPoint(new Point((cursor.X - before.X) / beforeScale, (cursor.Y - before.Y) / beforeScale),
            new Point((cursor.X - after.X) / afterScale, (cursor.Y - after.Y) / afterScale));

        var moved = ZoomMath.Pan(zoomed, viewport, content, new Vector(30, -10), 1.5);
        Assert.Equal(2.5, moved.Scale, 9);
        var movedRect = ZoomMath.ContentRect(moved, viewport, content, 1.5);
        AssertPoint(new Point(after.X + 30, after.Y - 10), movedRect.Position);
    }

    [Fact]
    public void SnappingPutsTheOriginOnADevicePixelBoundary()
    {
        var snapped = ZoomMath.SnapToDevicePixels(new Rect(10.3, 20.45, 100, 50), 1.5);
        Assert.Equal(0, snapped.X * 1.5 - Math.Round(snapped.X * 1.5), 9);
        Assert.Equal(0, snapped.Y * 1.5 - Math.Round(snapped.Y * 1.5), 9);
        Assert.True(Math.Abs(snapped.X - 10.3) <= 0.5 / 1.5 && Math.Abs(snapped.Y - 20.45) <= 0.5 / 1.5);
        Assert.Equal(new Size(100, 50), snapped.Size);
    }

    private static Point Project(Point imagePoint, ZoomState state, Size viewport, Size content)
    {
        var rect = ZoomMath.ContentRect(state, viewport, content);
        double scale = ZoomMath.EffectiveScale(state, viewport, content);
        return new Point(rect.X + imagePoint.X * scale, rect.Y + imagePoint.Y * scale);
    }

    private static void AssertPoint(Point expected, Point actual)
    {
        Assert.Equal(expected.X, actual.X, 9);
        Assert.Equal(expected.Y, actual.Y, 9);
    }

    private static void AssertRect(Rect expected, Rect actual)
    {
        Assert.Equal(expected.X, actual.X, 9);
        Assert.Equal(expected.Y, actual.Y, 9);
        Assert.Equal(expected.Width, actual.Width, 9);
        Assert.Equal(expected.Height, actual.Height, 9);
    }
}
