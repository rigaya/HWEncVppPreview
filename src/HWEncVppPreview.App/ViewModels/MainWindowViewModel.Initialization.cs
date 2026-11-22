namespace HWEncVppPreview.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private Task StartEncoderInitialization(string encoder)
    {
        CancelEncoderInitialization();
        Task completion = _session.StartEncoderInitialization(encoder, _lifetime.Token);
        Track(completion);
        return completion;
    }

    private void CancelEncoderInitialization()
    {
        if (_session.CancelEncoderInitialization() is { } cancellation) Track(cancellation);
    }
}
