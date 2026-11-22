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
    void RecordNavigationIntent();
    void SetBeforeProtectionDirection(int direction);
    void QueueBefore(PreviewRequest request, long generation);
    void CancelBefore();
    void QueueConcurrentBefore(PreviewRequest request, long generation);
    void CheckBeforeInterval(PreviewRequest request);
    void ClearBeforeInterval();
    bool ContainsBeforeFrameTarget(PreviewRequest request, IReadOnlyList<Y4mFrame> frames, double target, PreviewSegmentSnapshot? segment);
    bool ContainsFrameTarget(PreviewRequest request, IReadOnlyList<Y4mFrame> frames, double target, PreviewClockMap? clock);
    void OnFrameReady(PreviewFrameResult result);
    void OnCompletedWithWindow(PreviewCompletion completion, OwnedUncachedWindow? window);
}
