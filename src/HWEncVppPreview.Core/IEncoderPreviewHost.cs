using static HWEncVppPreview.Core.EncoderPreviewSession;

namespace HWEncVppPreview.Core;

internal enum PreviewProgress { Processing, ProbingInput, SelectEncoderAndInput, Done, DoneCache, DoneTargetNotReached, Start, End }

// 移動の入口を共有し、段階的に画像選択と表示の境界に絞る。
internal interface IEncoderPreviewHost
{
    bool IsDisposed { get; }
    bool IsInitialized { get; }
    bool IsSuspended { get; }
    double CurrentSeconds { get; }
    bool HasDuration { get; }
    double DurationSeconds { get; }
    bool IsBusy { get; set; }
    string? ErrorText { set; }
    PreviewProgress Progress { set; }
    string EncoderPath { get; }
    string InputPath { get; }
    EncoderPreviewConfiguration GetConfiguration(string encoder, string input);
    Task LoadMetadataAsync(string encoder, CancellationToken token);
    void ApplyInputInfo(InputInfo info);
    void ShowInputInfo(InputInfo info);
    void SetTimeOnly(double seconds);
    bool TrySetSliderTime(double seconds);
    void ShowSliderTime();
    void DisplayFrame(Y4mFrame frame, RequestContext context);
    void ShowExceptionError(string message, string diagnostics, Exception? exception);
    void DropPendingAfterImage();
    void CancelInputEdit();
    bool HasBeforeImage { get; }
    Y4mFrame? BeforeDisplayedFrame { get; }
    bool IsBeforeBusy { get; set; }
    string LogText { get; set; }
    void BeforeUnavailableForDuration();
    void QueueDurationAfterComparison(bool before, long generation);
    void EnqueueImage(ImageWork image);
    void DropPendingBeforeImage();
    void ClearBeforeDisplayedRaw();
    void ClearBeforeImage();
    void OnFrameReady(PreviewFrameResult result);
    void OnCompletedWithWindow(PreviewCompletion completion, OwnedUncachedWindow? window);
}
