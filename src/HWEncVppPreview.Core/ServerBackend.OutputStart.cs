namespace HWEncVppPreview.Core;

public sealed partial class ServerBackend
{
    // 旧サーバへ未知の字段を送らず、入力Sと同じ時計が信頼できるときだけ暖機の出力を省く。
    internal static long? OutputStartMicroseconds(PreviewRequest request,bool supported,bool windowSeek)
    {
        double seek=CommandBuilder.Plan(request).SeekSeconds??0;
        if (request.DisableOutputStart || !supported || !windowSeek || !request.Capabilities.TimestampPassthrough
            || !request.TimestampOriginTrusted || request.TimestampOriginSeconds is not double origin
            || PreviewClockMap.IsNearTimestampWrap(origin) || seek<=0 || request.WarmupSeconds<=0) return null;
        double rate=request.InputFormatHint?.Fps>0?request.InputFormatHint.Fps:request.Fps;
        double interval=request.OutputFrameIntervalHint??(rate>0?1/rate:0);
        if(!double.IsFinite(interval) || interval<=0)return null;
        // 計画safeStartの手前にも実在するframeを残し、先頭の量子化で安全域を繰り上げない。
        double margin=1.5*interval;
        if(margin>=request.WarmupSeconds)return null;
        double microseconds=Math.Floor((origin+seek+request.WarmupSeconds-margin)*1_000_000);
        if (!double.IsFinite(microseconds) || microseconds<=long.MinValue || microseconds>=long.MaxValue) return null;
        // safeの2µsはここで引かない。Nativeが転送の量子化を保守的に一度だけ吸収する。
        return (long)microseconds;
    }
}
