using HWEncVppPreview.Core;
using static HWEncVppPreview.Core.EncoderPreviewSession;

namespace HWEncVppPreview.App.ViewModels;

public sealed partial class MainWindowViewModel
{

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
            PreviewProgress.Error => AppMessages.Error,
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };
    }

    EncoderPreviewConfiguration IEncoderPreviewHost.GetConfiguration(string encoder, string input) => new(encoder, input,
        OptionTextParser.ParseText(InputArgsText),
        OptionTextParser.Parse(FilterLines.Select(line => new OptionLine(line.Text, line.IsEnabled))));

    IReadOnlyList<string> IEncoderPreviewHost.GetInputArgs() => OptionTextParser.ParseText(InputArgsText);
    void IEncoderPreviewHost.ApplyMetadata(string encoder, EncoderPreviewMetadata metadata)
    {
        EncoderDescription = DescribeEncoder(metadata.Version, encoder);
        _helpIndex = metadata.HelpIndex;
        HelpSchema = new HelpSchema(metadata.Help);
    }

    void IEncoderPreviewHost.MetadataUnavailable(string message)
    {
        EncoderDescription = Path.GetFileName(EncoderPath);
        HelpText = AppMessages.HelpUnavailable + message;
    }

    void IEncoderPreviewHost.MetadataReady() => UpdateHelp();
    void IEncoderPreviewHost.ShowInputInfo(InputInfo info)
    {
        bool squarePixel = info.SarNum <= 0 || info.SarDen <= 0 || info.SarNum == info.SarDen;
        string sar = squarePixel ? "" : $" (SAR {info.SarNum}:{info.SarDen})";
        InputInfoText = $"{info.Width}x{info.Height}{sar}  {info.Fps:0.###}fps";
    }

    void IEncoderPreviewHost.ShowExceptionError(string message, string diagnostics, Exception? exception)
        => ShowExceptionError(message, diagnostics, exception);

    void IEncoderPreviewHost.CancelInputEdit() => CancelInputEdit();
    void IEncoderPreviewHost.ClearBeforeImage() => BeforeImage = null;

    void IEncoderPreviewHost.StateChanged(string property) => OnPropertyChanged(property);
    void IEncoderPreviewHost.ShowTime(double seconds) => CurrentTimeText = FormatTime(seconds);
    void IEncoderPreviewHost.ShowDuration(double? seconds)
        => DurationText = seconds is double value ? FormatTime(value, milliseconds: false) : "";
    void IEncoderPreviewHost.ShowElapsed(double seconds) => ElapsedText = AppMessages.Elapsed(seconds);
    void IEncoderPreviewHost.ShowPreviewFailure(PreviewFailureKind kind, string diagnostics, Exception? exception)
        => ShowExceptionError(kind == PreviewFailureKind.NoDisplayableFrames
            ? AppMessages.NoDisplayableFrames : AppMessages.ImageDisplayFailed, diagnostics, exception);
    void IEncoderPreviewHost.BeginShutdown()
    {
        _bgraPool.Dispose();
        UiLanguage.Changed -= OnLanguageChanged;
    }

    void IEncoderPreviewHost.ClearAfterImage() => PreviewImage = null;
    void IEncoderPreviewHost.RefreshColorInfo() => RefreshColorInfo();

    internal IReadOnlyList<PreviewFrameReference> NavigationFrames(PreviewRequest request, PreviewSegmentSnapshot? segment)
        => _session.NavigationFrames(request, segment);
}
