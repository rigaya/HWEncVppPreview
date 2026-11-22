namespace HWEncVppPreview.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private Task StartEncoderInitialization(string encoder)
    {
        CancelEncoderInitialization();
        Task completion = _session.StartEncoderInitialization(encoder, _session._lifetime.Token);
        _session.Track(completion);
        return completion;
    }

    private void CancelEncoderInitialization()
    {
        if (_session.CancelEncoderInitialization() is { } cancellation) _session.Track(cancellation);
    }
}
