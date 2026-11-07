using HWEncParamViewer.Core;

namespace HWEncParamViewer.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly BgraBufferPool _bgraPool = new();

    // callbackの完了は実UIコピー/世代棄却の完了。Postを投げただけで貸出を返さない。
    internal async Task WithConvertedBgraAsync(Y4mFrame frame, HdrDisplaySettings settings,
        CancellationToken token, Func<byte[], Task> copyToUi)
    {
        if (_imageConverter != null)
        {
            // 注入converterの配列は呼出側所有。poolへ入れたり返したりしない。
            byte[] external = await _imageConverter(frame, settings, token).ConfigureAwait(false);
            await copyToUi(external).ConfigureAwait(false);
            return;
        }
        token.ThrowIfCancellationRequested();
        long epoch = _bgraPool.RetirementEpoch;
        // 大きい配列の初回確保も従来どおりworker上で行う。UIではepochだけ読む。
        using var lease = await Task.Run(() =>
        {
            var rented = _bgraPool.Rent(frame.Header, epoch);
            try
            {
                if (PerfTrace.Enabled) PerfTrace.Write("vm.convert.worker.start", new { frame.Xts });
                try { YuvToBgra.ConvertInto(frame, rented.Buffer, settings); }
                finally { if (PerfTrace.Enabled) PerfTrace.Write("vm.convert.worker.end", new { frame.Xts }); }
                return rented;
            }
            catch { rented.Dispose(); throw; }
        }, token).ConfigureAwait(false);
        await copyToUi(lease.Buffer).ConfigureAwait(false);
    }
}
