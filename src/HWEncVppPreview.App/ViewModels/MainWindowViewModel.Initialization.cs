namespace HWEncVppPreview.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private EncoderInitialization? _encoderInitialization;
    private sealed record EncoderInitialization(CancellationTokenSource Cancellation, Task Completion);

    private Task StartEncoderInitialization(string encoder)
    {
        CancelEncoderInitialization();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        Task completion = _controller.InitializeEncoderAsync(encoder, cancellation.Token);
        Interlocked.Exchange(ref _encoderInitialization, new(cancellation, completion));
        Track(completion);
        return completion;
    }

    private void CancelEncoderInitialization()
    {
        var previous = Interlocked.Exchange(ref _encoderInitialization, null);
        if (previous == null) return;
        // 変更・終了時に古い初期化を止め、consumerが終わってから取消源を破棄する。
        Track(Task.Run(async () =>
        {
            try
            {
                await previous.Cancellation.CancelAsync().ConfigureAwait(false);
                await previous.Completion.ConfigureAwait(false);
            }
            finally { previous.Cancellation.Dispose(); }
        }));
    }
}
