using static HWEncVppPreview.Core.EncoderPreviewSession;

namespace HWEncVppPreview.Core;

public enum PreviewProgress { Processing, ProbingInput, SelectEncoderAndInput, Done, DoneCache, DoneTargetNotReached, Start, End, Error }

internal enum PreviewFailureKind { NoDisplayableFrames, ImageDisplayFailed }

internal interface IEncoderPreviewHost
{
    bool IsBusy { get; set; }
    string? ErrorText { set; }
    PreviewProgress Progress { set; }
    string EncoderPath { get; }
    string InputPath { get; }
    EncoderPreviewConfiguration GetConfiguration(string encoder, string input);
    IReadOnlyList<string> GetInputArgs();
    void ApplyMetadata(string encoder, EncoderPreviewMetadata metadata);
    void MetadataUnavailable(string message);
    void MetadataReady();
    void ShowInputInfo(InputInfo info);
    void ShowExceptionError(string message, string diagnostics, Exception? exception);
    void CancelInputEdit();
    bool IsBeforeBusy { get; set; }
    string LogText { get; set; }
    Task ConvertAndPresentAsync(ImageWork image);
    void ClearBeforeImage();
    void StateChanged(string property);
    void ShowTime(double seconds);
    void ShowDuration(double? seconds);
    void ShowElapsed(double seconds);
    void ShowPreviewFailure(PreviewFailureKind kind, string diagnostics, Exception? exception);
    void BeginShutdown();
    void ClearAfterImage();
    void RefreshColorInfo();
}
