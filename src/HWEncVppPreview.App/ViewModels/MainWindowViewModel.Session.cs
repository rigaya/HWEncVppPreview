using HWEncVppPreview.Core;
using static HWEncVppPreview.Core.EncoderPreviewSession;

namespace HWEncVppPreview.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    bool IEncoderPreviewHost.IsDisposed => _disposed;
    bool IEncoderPreviewHost.IsInitialized => _initialized;
    bool IEncoderPreviewHost.IsSuspended => _suspendChanges;

    PreviewProgress IEncoderPreviewHost.Progress
    {
        set => StatusText = value switch
        {
            PreviewProgress.Processing => AppMessages.Processing,
            PreviewProgress.ProbingInput => AppMessages.ProbingInput,
            PreviewProgress.SelectEncoderAndInput => AppMessages.SelectEncoderAndInput,
            PreviewProgress.Done => AppMessages.Done,
            PreviewProgress.DoneCache => AppMessages.DoneCache,
            PreviewProgress.DoneTargetNotReached => AppMessages.DoneTargetNotReached,
            PreviewProgress.Start => AppMessages.Start,
            PreviewProgress.End => AppMessages.End,
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };
    }

    EncoderPreviewConfiguration IEncoderPreviewHost.GetConfiguration(string encoder, string input) => new(encoder, input,
        OptionTextParser.ParseText(InputArgsText),
        OptionTextParser.Parse(FilterLines.Select(line => new OptionLine(line.Text, line.IsEnabled))));
    Task IEncoderPreviewHost.LoadMetadataAsync(string encoder, CancellationToken token) => LoadMetadataAsync(encoder, token);
    void IEncoderPreviewHost.ApplyInputInfo(InputInfo info) => ApplyInputInfo(info);
    void IEncoderPreviewHost.ShowInputInfo(InputInfo info)
    {
        bool squarePixel = info.SarNum <= 0 || info.SarDen <= 0 || info.SarNum == info.SarDen;
        string sar = squarePixel ? "" : $" (SAR {info.SarNum}:{info.SarDen})";
        InputInfoText = $"{info.Width}x{info.Height}{sar}  {info.Fps:0.###}fps";
    }

    void IEncoderPreviewHost.SetTimeOnly(double seconds) => SetTimeOnly(seconds);
    bool IEncoderPreviewHost.TrySetSliderTime(double seconds) => SetProperty(ref _currentSeconds, seconds, nameof(CurrentSeconds));
    void IEncoderPreviewHost.ShowSliderTime() => CurrentTimeText = FormatTime(_currentSeconds);
    void IEncoderPreviewHost.DisplayFrame(Y4mFrame frame, RequestContext context) => DisplayFrame(frame, context);
    void IEncoderPreviewHost.ShowExceptionError(string message, string diagnostics, Exception? exception)
        => ShowExceptionError(message, diagnostics, exception);
    void IEncoderPreviewHost.DropPendingAfterImage()
    {
        lock (_session._taskLock) DropImage(ref _pendingAfterImage);
    }

    void IEncoderPreviewHost.CancelInputEdit() => CancelInputEdit();
    void IEncoderPreviewHost.RecordNavigationIntent() => RecordNavigationIntent();
    void IEncoderPreviewHost.SetBeforeProtectionDirection(int direction) => SetBeforeProtectionDirection(direction);
    void IEncoderPreviewHost.QueueBefore(PreviewRequest request, long generation) => QueueBefore(request, generation);
    void IEncoderPreviewHost.CancelBefore() => CancelBefore();
    void IEncoderPreviewHost.QueueConcurrentBefore(PreviewRequest request, long generation) => QueueConcurrentBefore(request, generation);
    void IEncoderPreviewHost.CheckBeforeInterval(PreviewRequest request) => CheckBeforeInterval(request);
    void IEncoderPreviewHost.ClearBeforeInterval() => ClearBeforeInterval();
    bool IEncoderPreviewHost.ContainsBeforeFrameTarget(PreviewRequest request, IReadOnlyList<Y4mFrame> frames, double target, PreviewSegmentSnapshot? segment)
        => ContainsBeforeFrameTarget(request, frames, target, segment);
    bool IEncoderPreviewHost.ContainsFrameTarget(PreviewRequest request, IReadOnlyList<Y4mFrame> frames, double target, PreviewClockMap? clock)
        => ContainsFrameTarget(request, frames, target, clock);
    void IEncoderPreviewHost.OnFrameReady(PreviewFrameResult result) => OnFrameReady(result);
    void IEncoderPreviewHost.OnCompletedWithWindow(PreviewCompletion completion, OwnedUncachedWindow? window)
        => OnCompletedWithWindow(completion, window);

    internal IReadOnlyList<PreviewFrameReference> NavigationFrames(PreviewRequest request, PreviewSegmentSnapshot? segment)
        => _session.NavigationFrames(request, segment);
}
