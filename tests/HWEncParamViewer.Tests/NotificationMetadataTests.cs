using HWEncParamViewer.Core;

namespace HWEncParamViewer.Tests;

public sealed class NotificationMetadataTests
{
    private static readonly Y4mHeader Header = new() { Width = 2, Height = 2, FpsNum = 30, FpsDen = 1 };
    private static PreviewRequest Request => new() { EncoderPath = "fake", InputFile = "input", TargetSeconds = 0, Fps = 30 };

    [Fact]
    public void FrameReadyは親区間のmetadataを残して選択一枚だけを所有する()
    {
        var scope = new RawOwnershipScope();
        var producers = Frames(scope, 4);
        var frames = producers.Select(producer => producer.Frame).ToArray();
        var segment = Segment(frames, 12);
        long before = scope.Capture().TotalRetains;
        using var notification = new OwnedPreviewEnvelope(frame: new(Request, frames[2], false) { Segment = segment });
        Assert.Equal(before + 1, scope.Capture().TotalRetains);
        foreach (var producer in producers) producer.Dispose();
        Assert.Equal(1, scope.Capture().LiveOwners);
        Assert.Equal(1, scope.Capture().LiveTokens);
        long retained = scope.Capture().TotalRetains;
        using var posted = notification.Retain("UiPost");
        Assert.Equal(retained + 1, scope.Capture().TotalRetains);
        notification.Dispose();
        Assert.Equal(1, scope.Capture().LiveTokens);
        Assert.Same(frames[2], posted.FrameReady!.Frame);
        Assert.Equal(4, posted.FrameReady.Segment!.Frames.Count);
        Assert.Equal(18, posted.FrameReady.Frame.RawPixels[0]);
        for (int index = 0; index < frames.Length; ++index)
        {
            Assert.Same(frames[index], posted.FrameReady.Segment.Frames[index]);
            Assert.Equal(index / 30d, posted.FrameReady.Segment.Frames[index].Xts!.Value);
            if (index != 2) Assert.Throws<ObjectDisposedException>(() => frames[index].RawPixels);
        }
        posted.Dispose();
        AssertReleased(scope);
    }

    [Fact]
    public void 正IDのSegmentUpdatedは通知の作成とRetainの両方でmetadataだけを渡す()
    {
        var scope = new RawOwnershipScope();
        var producers = Frames(scope, 3);
        var frames = producers.Select(producer => producer.Frame).ToArray();
        long retained = scope.Capture().TotalRetains;
        using var notification = new OwnedPreviewEnvelope(update: new(Request, Segment(frames, 21), false, false));
        using var posted = notification.Retain("UiPost");
        Assert.Equal(retained, scope.Capture().TotalRetains);
        foreach (var producer in producers) producer.Dispose();
        Assert.True(scope.Capture().IsEmpty);
        for (int index = 0; index < frames.Length; ++index)
        {
            Assert.Same(frames[index], posted.SegmentUpdated!.Segment.Frames[index]);
            Assert.Equal(2, posted.SegmentUpdated.Segment.Frames[index].Header.Width);
            Assert.Equal(frames[index].FrameId, posted.SegmentUpdated.Segment.Frames[index].FrameId);
            Assert.Throws<ObjectDisposedException>(() => frames[index].RawPixels);
        }
        posted.Dispose();
        notification.Dispose();
        AssertReleased(scope);
    }

    [Fact]
    public void 正IDのCompletedは全窓を取得せず元の所有がなくなってもmetadataを保持する()
    {
        var scope = new RawOwnershipScope();
        var producers = Frames(scope, 3);
        var frames = producers.Select(producer => producer.Frame).ToArray();
        var segment = Segment(frames, 31);
        var mutable = frames.ToList();
        long retained = scope.Capture().TotalRetains;
        using var notification = new OwnedPreviewEnvelope(completed: new(Request, new(0, ""), false, false)
        { Frames = mutable, Segment = segment });
        Assert.Equal(retained, scope.Capture().TotalRetains);
        Assert.Null(notification.ShareFiniteWindow());
        mutable.Clear();
        foreach (var producer in producers) producer.Dispose();
        Assert.True(scope.Capture().IsEmpty);
        using var posted = notification.Retain("UiPost");
        Assert.Equal(retained, scope.Capture().TotalRetains);
        Assert.Null(posted.ShareFiniteWindow());
        notification.Dispose();
        Assert.Equal(frames.Length, posted.Completed!.Frames.Count);
        Assert.Equal(segment.SegmentId, posted.Completed.Segment!.SegmentId);
        for (int index = 0; index < frames.Length; ++index)
        {
            Assert.Same(frames[index], posted.Completed.Frames[index]);
            Assert.Same(frames[index], posted.Completed.Segment.Frames[index]);
            Assert.Equal(index / 30d, posted.Completed.Frames[index].Xts!.Value);
            Assert.Equal(frames[index].FrameId, posted.Completed.Frames[index].FrameId);
            Assert.Equal(2, posted.Completed.Frames[index].Header.Width);
            Assert.Throws<ObjectDisposedException>(() => posted.Completed.Frames[index].RawPixels);
        }
        Assert.Throws<ObjectDisposedException>(() => notification.ShareFiniteWindow());
        posted.Dispose();
        AssertReleased(scope);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ID0更新とCompletedは有限窓を失わず画素を再取得せず共有する(bool completion)
    {
        var scope = new RawOwnershipScope();
        var producers = Frames(scope, 3);
        var frames = producers.Select(producer => producer.Frame).ToArray();
        long retained = scope.Capture().TotalRetains;
        using var notification = completion
            ? new OwnedPreviewEnvelope(completed: new(Request, new(0, ""), false, false)
                { Frames = frames, Segment = Segment(frames, 0) })
            : new OwnedPreviewEnvelope(update: new(Request, Segment(frames, 0), false, false));
        Assert.Equal(retained + frames.Length, scope.Capture().TotalRetains);
        foreach (var producer in producers) producer.Dispose();
        retained = scope.Capture().TotalRetains;
        using var posted = notification.Retain("UiPost");
        Assert.Equal(retained, scope.Capture().TotalRetains);
        Assert.False(scope.Capture().OwnerKinds.ContainsKey("UiPost"));
        notification.Dispose();
        Assert.Equal(frames.Length, scope.Capture().LiveOwners);
        Assert.Equal(frames.Length, scope.Capture().LiveTokens);
        IReadOnlyList<Y4mFrame> metadata = completion ? posted.Completed!.Frames : posted.SegmentUpdated!.Segment.Frames;
        for (int index = 0; index < frames.Length; ++index)
        {
            Assert.Same(frames[index], metadata[index]);
            Assert.Equal(16 + index, metadata[index].RawPixels[0]);
        }
        posted.Dispose();
        AssertReleased(scope);
    }

    [Fact]
    public void Completedは元の可変列を閉じても凍結した完了窓を一回だけ引き継ぐ()
    {
        var scope = new RawOwnershipScope();
        var producers = Frames(scope, 3);
        var frames = producers.Select(producer => producer.Frame).ToArray();
        using var source = new RawFrameList("Finite");
        foreach (var frame in frames) source.Add(frame);
        using var notification = new OwnedPreviewEnvelope(completed: new(Request, new(0, ""), false, false)
        { Frames = source.AsReadOnly() });
        var frozen = notification.Completed!.Frames;
        source.Dispose();
        foreach (var producer in producers) producer.Dispose();
        Assert.Equal(frames.Length, frozen.Count);
        long retained = scope.Capture().TotalRetains;
        using var posted = notification.Retain("UiPost");
        Assert.Equal(retained, scope.Capture().TotalRetains);
        Assert.False(scope.Capture().OwnerKinds.ContainsKey("UiPost"));
        notification.Dispose();
        Assert.Equal(frames.Length, posted.Completed!.Frames.Count);
        Assert.Equal(frames.Length, scope.Capture().LiveTokens);
        for (int index = 0; index < frames.Length; ++index)
        {
            Assert.Same(frames[index], frozen[index]);
            Assert.Same(frames[index], posted.Completed.Frames[index]);
            Assert.Equal(16 + index, posted.Completed.Frames[index].RawPixels[0]);
        }
        posted.Dispose();
        AssertReleased(scope);
    }

    [Fact]
    public void 有限窓は通知を閉じた後も共有先に残り最後の返却で解放される()
    {
        var scope = new RawOwnershipScope();
        var producers = Frames(scope, 3);
        var frames = producers.Select(producer => producer.Frame).ToArray();
        using var notification = new OwnedPreviewEnvelope(completed: new(Request, new(0, ""), false, false)
        { Frames = frames });
        foreach (var producer in producers) producer.Dispose();
        long retained = scope.Capture().TotalRetains;
        using var window = notification.ShareFiniteWindow();
        Assert.NotNull(window);
        Assert.Equal(retained, scope.Capture().TotalRetains);
        Assert.Equal(frames.Length, scope.Capture().OwnerKinds["NotificationCompletionWindow"]);
        notification.Dispose();
        Assert.Throws<ObjectDisposedException>(() => notification.ShareFiniteWindow());
        Assert.Throws<ObjectDisposedException>(() => notification.Retain("LateUi"));
        for (int index = 0; index < frames.Length; ++index)
        {
            Assert.Same(frames[index], window.Frames[index]);
            Assert.Equal(16 + index, window.Frames[index].RawPixels[0]);
        }
        window.Dispose();
        AssertReleased(scope);
    }

    [Fact]
    public void 一枚通知と正ID通知は有限窓を返さず破棄済み通知の共有を拒否する()
    {
        var scope = new RawOwnershipScope();
        using var producer = Frames(scope, 1)[0];
        using var frame = new OwnedPreviewEnvelope(frame: new(Request, producer.Frame, false));
        using var metadata = new OwnedPreviewEnvelope(update: new(Request, Segment(new[] { producer.Frame }, 8), false, false));
        using var completed = new OwnedPreviewEnvelope(completed: new(Request, new(0, ""), false, false)
        { Frames = new[] { producer.Frame }, Segment = Segment(new[] { producer.Frame }, 8) });
        Assert.Null(frame.ShareFiniteWindow());
        Assert.Null(metadata.ShareFiniteWindow());
        Assert.Null(completed.ShareFiniteWindow());
        frame.Dispose();
        metadata.Dispose();
        completed.Dispose();
        Assert.Throws<ObjectDisposedException>(() => frame.ShareFiniteWindow());
        Assert.Throws<ObjectDisposedException>(() => metadata.ShareFiniteWindow());
        Assert.Throws<ObjectDisposedException>(() => completed.ShareFiniteWindow());
        producer.Dispose();
        AssertReleased(scope);
    }

    [Fact]
    public void Try生成した一枚通知は一度の取得だけで共有し親全体を固定しない()
    {
        const int ParentFrameCount = 4;
        const int SelectedIndex = 2;
        const int SharingIterations = 32;
        var scope = new RawOwnershipScope();
        var producers = Frames(scope, ParentFrameCount);
        var frames = producers.Select(producer => producer.Frame).ToArray();
        var segment = Segment(frames, 12);
        var value = new PreviewFrameResult(Request, frames[SelectedIndex], false) { Segment = segment };
        var initial = scope.Capture();
        Assert.True(OwnedPreviewEnvelope.TryCreateFrame(value, out var notification));
        Assert.NotNull(notification);
        using var captured = notification;
        Assert.Equal(initial.TryRetainAttempts + 1, scope.Capture().TryRetainAttempts);
        Assert.Equal(initial.TryRetainSuccesses + 1, scope.Capture().TryRetainSuccesses);
        Assert.Equal(initial.TryRetainFailures, scope.Capture().TryRetainFailures);
        Assert.Equal(initial.TotalRetains + 1, scope.Capture().TotalRetains);
        foreach (var producer in producers) producer.Dispose();
        Assert.Equal(1, scope.Capture().LiveOwners);
        Assert.Equal(1, scope.Capture().LiveTokens);
        for (int iteration = 0; iteration < SharingIterations; ++iteration)
        {
            using var copy = captured.Retain("UiPost");
            Assert.Same(value, copy.FrameReady);
            Assert.Equal(initial.TotalRetains + 1, scope.Capture().TotalRetains);
            Assert.Equal(1, scope.Capture().LiveTokens);
        }
        using var posted = captured.Retain("LastUi");
        captured.Dispose();
        Assert.Equal(1, scope.Capture().OwnerKinds["Notification"]);
        Assert.False(scope.Capture().OwnerKinds.ContainsKey("LastUi"));
        Assert.Same(frames[SelectedIndex], posted.FrameReady!.Frame);
        Assert.Equal(frames[SelectedIndex].FrameId, posted.FrameReady.Frame.FrameId);
        Assert.Equal(frames[SelectedIndex].RawRunId, posted.FrameReady.Frame.RawRunId);
        Assert.Equal(16 + SelectedIndex, posted.FrameReady.Frame.RawPixels[0]);
        Assert.Equal(ParentFrameCount, posted.FrameReady.Segment!.Frames.Count);
        for (int index = 0; index < ParentFrameCount; ++index)
        {
            Assert.Same(frames[index], posted.FrameReady.Segment.Frames[index]);
            Assert.Equal(index / 30d, posted.FrameReady.Segment.Frames[index].Xts);
            if (index != SelectedIndex) Assert.Throws<ObjectDisposedException>(() => frames[index].RawPixels);
        }
        posted.Dispose();
        Assert.Throws<ObjectDisposedException>(() => captured.Retain("LateUi"));
        Assert.Throws<ObjectDisposedException>(() => frames[SelectedIndex].RawPixels);
        AssertReleased(scope);
    }

    [Fact]
    public void 解放済みの選択候補からはTry通知を生成せず親の生存画素も取得しない()
    {
        const int ParentFrameCount = 3;
        const int SelectedIndex = 1;
        var scope = new RawOwnershipScope();
        var producers = Frames(scope, ParentFrameCount);
        var frames = producers.Select(producer => producer.Frame).ToArray();
        producers[SelectedIndex].Dispose();
        var initial = scope.Capture();
        var value = new PreviewFrameResult(Request, frames[SelectedIndex], false) { Segment = Segment(frames, 17) };
        Assert.False(OwnedPreviewEnvelope.TryCreateFrame(value, out var notification));
        Assert.Null(notification);
        Assert.Equal(initial.TryRetainAttempts + 1, scope.Capture().TryRetainAttempts);
        Assert.Equal(initial.TryRetainFailures + 1, scope.Capture().TryRetainFailures);
        Assert.Equal(initial.TryRetainSuccesses, scope.Capture().TryRetainSuccesses);
        Assert.Equal(initial.TotalRetains, scope.Capture().TotalRetains);
        Assert.Equal(initial.LiveTokens, scope.Capture().LiveTokens);
        Assert.False(scope.Capture().OwnerKinds.ContainsKey("Notification"));
        foreach (var producer in producers) producer.Dispose();
        AssertReleased(scope);
        foreach (var frame in frames) Assert.Throws<ObjectDisposedException>(() => frame.RawPixels);
    }

    [Fact]
    public void legacyのTry通知は元のGC画素を共有しraw台帳を増やさない()
    {
        const int PixelBytes = 6;
        byte[] pixels = Enumerable.Repeat((byte)32, PixelBytes).ToArray();
        var frame = new Y4mFrame(Header, 0, 1d / 30, pixels);
        var scope = new RawOwnershipScope();
        var initial = scope.Capture();
        Assert.True(OwnedPreviewEnvelope.TryCreateFrame(new(Request, frame, false), out var notification));
        Assert.NotNull(notification);
        using var captured = notification;
        using var posted = captured.Retain("UiPost");
        captured.Dispose();
        Assert.Same(frame, posted.FrameReady!.Frame);
        Assert.Same(pixels, posted.FrameReady.Frame.Data);
        posted.Dispose();
        Assert.Same(pixels, frame.Data);
        Assert.Equal(initial.TotalRetains, scope.Capture().TotalRetains);
        Assert.Equal(initial.TryRetainAttempts, scope.Capture().TryRetainAttempts);
        AssertReleased(scope);
    }

    private static RawFrameLease[] Frames(RawOwnershipScope scope, int count)
    {
        long run = scope.NewRunId();
        return Enumerable.Range(0, count).Select(index => scope.Create(Header, index / 30d, 1d / 30,
            Enumerable.Repeat((byte)(16 + index), 6).ToArray(), run, "Reader")).ToArray();
    }

    private static PreviewSegmentSnapshot Segment(IReadOnlyList<Y4mFrame> frames, long id)
        => new(id, Request, new(0, 0, true), frames, 0, 1, false, false);

    private static void AssertReleased(RawOwnershipScope scope)
    {
        var state = scope.Capture();
        Assert.True(state.IsEmpty);
        Assert.Equal(0, state.LiveBytes);
        Assert.Equal(state.TotalRetains, state.TotalTokenReleases);
        Assert.Equal(state.NewOwners, state.FinalReleases);
        Assert.Equal(state.NewBytes, state.FinalReleasedBytes);
    }
}
