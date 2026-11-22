namespace HWEncVppPreview.Core;

/// <summary>同じ操作で最後に送信した両側の画像を、画素の所有権を増やさずに追跡する。</summary>
internal sealed class EncoderPreviewViewDelivery
{
    private WeakReference<Y4mFrame>? _after;
    private WeakReference<Y4mFrame>? _before;

    internal bool ReuseAfter(EncoderPreviewFrame frame) => IsSame(_after, frame);
    internal bool ReuseBefore(EncoderPreviewFrame frame) => IsSame(_before, frame);

    internal void Remember(EncoderPreviewFrame? after, EncoderPreviewFrame? before)
    {
        Remember(ref _after, after);
        Remember(ref _before, before);
    }

    private static bool IsSame(WeakReference<Y4mFrame>? previous, EncoderPreviewFrame frame)
        => previous is not null && previous.TryGetTarget(out var sent) && ReferenceEquals(sent, frame.Frame);

    private static void Remember(ref WeakReference<Y4mFrame>? previous, EncoderPreviewFrame? frame)
    {
        if (frame is null) previous = null;
        else if (previous is null) previous = new(frame.Frame);
        else previous.SetTarget(frame.Frame);
    }
}
