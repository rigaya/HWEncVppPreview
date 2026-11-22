using Avalonia.Threading;
namespace HWEncVppPreview.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private CancellationTokenSource? _inputEditCancellation;

    /// <summary>ファイル選択やドロップで確定した入力を、文字編集の待機なしで開く。</summary>
    public void OpenInput(string path) => SetConfirmedPath(() => InputPath = path);
    /// <summary>ファイル選択やドロップで確定したエンコーダを、文字編集の待機なしで選ぶ。</summary>
    public void OpenEncoder(string path) => SetConfirmedPath(() => EncoderPath = path);

    private void SetConfirmedPath(Action setPath)
    {
        bool suspended = _session.IsSuspended;
        _session.IsSuspended = true;
        try { setPath(); }
        finally { _session.IsSuspended = suspended; }
        InputChanged(bypassDebounce: true);
    }

    private void CancelInputEdit()
    {
        lock (_session.TaskSync) _inputEditCancellation?.Cancel();
    }

    private void ScheduleInputEdit()
    {
        CancelInputEdit();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_session.LifetimeToken);
        lock (_session.TaskSync) _inputEditCancellation = cancellation;
        _session.Track(PrepareEditedInputAsync());

        async Task PrepareEditedInputAsync()
        {
            try
            {
                // パスや入力引数の文字入力では、metadata/probeのプロセス起動より前に待つ。
                await Task.Delay(_inputEditDelay, _clock, cancellation.Token).ConfigureAwait(false);
                Task preparation = Task.CompletedTask;
                await _session.PostAsync(() =>
                {
                    if (_session.IsDisposed || cancellation.IsCancellationRequested) return;
                    lock (_session.TaskSync)
                        if (ReferenceEquals(_inputEditCancellation, cancellation)) _inputEditCancellation = null;
                    preparation = _session.QueuePreview(bypassDebounce: true);
                }).ConfigureAwait(false);
                await preparation.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            finally
            {
                lock (_session.TaskSync)
                    if (ReferenceEquals(_inputEditCancellation, cancellation)) _inputEditCancellation = null;
                cancellation.Dispose();
            }
        }
    }
}
