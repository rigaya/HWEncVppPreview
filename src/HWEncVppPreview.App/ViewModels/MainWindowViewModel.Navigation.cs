using HWEncVppPreview.Core;
using static HWEncVppPreview.Core.EncoderPreviewSession;

namespace HWEncVppPreview.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    async Task IEncoderPreviewHost.ConvertAndPresentAsync(ImageWork image)
    {
        HdrDisplaySettings settings;
        long displaySettingsRevision;
        lock (_session.TaskSync) { settings = _hdrDisplaySettings; displaySettingsRevision = _hdrDisplaySettingsRevision; }
        await WithConvertedBgraAsync(image.Frame, settings, _session.LifetimeToken, async data =>
        {
            if (PerfTrace.Enabled) PerfTrace.Write("vm.convert.end", new { image.TraceId, image.Generation, image.Before, image.Frame.Xts, bytes = data.Length });
            await _session.PostAsync(() =>
            {
                if (displaySettingsRevision != Volatile.Read(ref _hdrDisplaySettingsRevision) && YuvToBgra.GetColorInfo(image.Frame.Header).IsHdr)
                    _session.RequeueForDisplaySettings(image);
                else ApplyImage(image, data);
            }).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private void ApplyImage(ImageWork image, byte[] data) => _session.ApplyImage(image, () =>
    {
        var bitmap = CreateBitmap(image.Frame, data, image.TraceId);
        return () =>
        {
            if (!image.DisplayOnly) RecalculateDisplaySizes();
            if (image.Before) BeforeImage = bitmap;
            else PreviewImage = bitmap;
        };
    });
}
