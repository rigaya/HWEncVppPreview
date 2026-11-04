using System.Globalization;
using System.Text.RegularExpressions;
using static HWEncParamViewer.Core.FilterLineSyntax;

namespace HWEncParamViewer.Core;

/// <summary>GOPの前余白と、監査済みの処理列に必要な暖機を同じ物理計画で管理する。</summary>
public static class PreviewWarmupPolicy
{
    public const double GopMarginSeconds = 1;
    public const double DefaultWarmupSeconds = 2;

    // QSVEncのdispatchと各run_filter/kernelを監査し、当該入力と固定パラメータだけを使うものを確認した。
    // crop/pad/colorspace/tweak/curves、各空間フィルタ、transform/rotateは前フレームを参照しない。
    // NNEDI/NNEDI-upscaleも当該入力のfield/picstructと固定weightsだけを使い、出力slotを毎回初期化する。
    private static readonly HashSet<string> StatelessFilters = new(StringComparer.Ordinal)
    {
        "--crop", "--vpp-pad", "--vpp-colorspace", "--vpp-tweak", "--vpp-curves", "--vpp-unsharp",
        "--vpp-edgelevel", "--vpp-warpsharp", "--vpp-pmd", "--vpp-denoise-dct", "--vpp-nnedi",
        "--vpp-transform", "--vpp-rotate", "--vpp-nnedi-upscale", "--vpp-hqdering", "--vpp-dehalo", "--vpp-finedehalo",
    };
    // CLIが共通でもSDK/AI/libplaceboへ分岐する値は含めず、監査済みOpenCL値だけを列挙する。
    private static readonly HashSet<string> SpatialResizeAlgorithms = new(StringComparer.Ordinal)
    {
        "bilinear", "bicubic", "spline16", "spline36", "spline64", "lanczos2", "lanczos3", "lanczos4",
        "lanczos5", "lanczos6", "lanczos7", "lanczos8", "mitchell", "catmull-rom", "hermite", "gauss",
        "fsr1", "nis", "jinc36", "jinc64", "jinc144", "jinc256", "area", "dpid",
    };
    private static readonly HelpSchema EmptySchema = new("");

    /// <summary>入力側に隠れた処理指定も含め、確実に空間処理だけと分かる列に限り暖機を0にする。</summary>
    public static double RequiredWarmupSeconds(PreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        // この一覧はOpenCL実装だけの監査結果。CUDA実装や未確認の常駐、都度起動には流用しない。
        if (!AllowsZeroWarmup(request.ReadyEncoder)) return DefaultWarmupSeconds;
        bool interlaceSeen = false;
        for (int index = 0; index < request.InputArgs.Count; index++)
        {
            string option = request.InputArgs[index];
            if (option is "--avhw" or "--avsw") continue;
            if (option != "--interlace" || interlaceSeen || ++index >= request.InputArgs.Count
                || request.InputArgs[index] is not ("tff" or "bff")) return DefaultWarmupSeconds;
            interlaceSeen = true;
        }
        bool spatialResize = false, outputResolution = false;
        for (int index = 0; index < request.VppArgs.Count; index++)
        {
            string option = request.VppArgs[index];
            string? argument = index + 1 < request.VppArgs.Count && !request.VppArgs[index + 1].StartsWith('-')
                ? request.VppArgs[++index] : null;
            if (StatelessFilters.Contains(option)) continue;
            if (option is "--vpp-nlmeans" or "--vpp-knn")
            {
                if (!ReadParameters(option, argument, out var parameters)) return DefaultWarmupSeconds;
                string? value = parameters.GetValueOrDefault("d");
                if (value is null)
                {
                    var parameter = request.HelpSchema?.Lookup(option)?.Parameters.FirstOrDefault(item => item.Name == "d");
                    if (parameter is null || parameter.IsNameOnly || parameter.IsDerivedDefault) return DefaultWarmupSeconds;
                    value = parameter.DefaultValue;
                }
                if (!int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int d) || d != 0)
                    return DefaultWarmupSeconds;
                continue;
            }
            if (option == "--vpp-resize")
            {
                // 省略時のautoはSDK経路もあるため、既定値を補わず明示されたalgoだけを採用する。
                if (!ReadParameters(option, argument, out var parameters)
                    || !parameters.TryGetValue("algo", out string? algo) || !SpatialResizeAlgorithms.Contains(algo))
                    return DefaultWarmupSeconds;
                spatialResize = true;
                continue;
            }
            if (option == "--output-res")
            {
                if (argument is null || !Regex.IsMatch(argument, @"^[1-9][0-9]*x[1-9][0-9]*$", RegexOptions.CultureInvariant))
                    return DefaultWarmupSeconds;
                outputResolution = true;
                continue;
            }
            return DefaultWarmupSeconds;
        }
        // サイズだけではautoのSDK resizeへ入る可能性があるため、明示OpenCL指定との組合せに限定する。
        return outputResolution && !spatialResize ? DefaultWarmupSeconds : 0;
    }

    internal static bool AllowsZeroWarmup(string? encoder) => encoder is "QSVEnc" or "VCEEnc" or "rkmppenc";

    private static bool ReadParameters(string option, string? argument, out Dictionary<string, string> values)
    {
        values = new(StringComparer.OrdinalIgnoreCase);
        if (argument is null) return true;
        string text = option + " " + argument;
        if (text.IndexOfAny(['\r', '\n']) >= 0) return false;
        // 共通パーサーは編集中の未閉鎖引用符も読むので、実行可否の判定では先に保守的に除外する。
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '"') continue;
            if (quoted && i + 1 < text.Length && text[i + 1] == '"') { i++; continue; }
            quoted = !quoted;
        }
        if (quoted) return false;
        var line = ReadLine(text, 0, EmptySchema)!;
        foreach (var segment in line.Segments)
        {
            int start = SkipSpace(text, segment.Start, segment.End);
            int end = TrimSpace(text, start, segment.End);
            if (segment.Equal < start) return false;
            string key = text[start..TrimSpace(text, start, segment.Equal)];
            string raw = text[SkipSpace(text, segment.Equal + 1, end)..end];
            IReadOnlyList<string> tokens = line.ArgumentQuoted ? new[] { raw.Replace("\"\"", "\"") } : Tokenize(raw, allowComment: false);
            if (key.Length == 0 || tokens.Count != 1 || tokens[0].Contains(',') || !values.TryAdd(key, tokens[0])) return false;
        }
        return true;
    }

    internal static PreviewRequest PreparePhysical(PreviewRequest request, bool background)
    {
        if (!request.Capabilities.TimestampPassthrough || !request.TimestampOriginSeconds.HasValue)
            return request with { WarmupSeconds = DefaultWarmupSeconds };
        // 有限でも通常の順方向窓は対象にする。逆方向の拡張窓と明示seekは従来の2秒暖機を維持する。
        bool forwardWindow = request.Continuous || background
            || request.PreMarginSeconds <= DefaultWarmupSeconds && request.SeekSecondsOverride is null;
        bool canSkip = forwardWindow && request.TimestampOriginTrusted
            && RequiredWarmupSeconds(request) == 0;
        double warmup = canSkip ? 0 : DefaultWarmupSeconds;
        // 既存の背景要求の非ゼロ暖機はseekや安全域を変更しない。通常のBeforeはfilterなしなので上の0秒対象。
        if (background && !canSkip) return request with { WarmupSeconds = warmup };
        double displayMargin = Math.Max(GopMarginSeconds, request.PreMarginSeconds - DefaultWarmupSeconds);
        double physicalMargin = canSkip ? displayMargin : Math.Max(GopMarginSeconds + warmup, request.PreMarginSeconds);
        return request with
        {
            WarmupSeconds = warmup,
            PreMarginSeconds = canSkip ? displayMargin : request.PreMarginSeconds,
            SeekSecondsOverride = Math.Max(0, request.TargetSeconds - physicalMargin),
        };
    }

    internal static double SafeStartSeconds(PreviewRequest physical, bool background)
    {
        double seek = CommandBuilder.Plan(physical).SeekSeconds ?? 0;
        bool guarded = !background && physical.Capabilities.TimestampPassthrough && physical.TimestampOriginSeconds.HasValue;
        // 入力先頭は従来どおり除外せず、seekした区間だけ確定済みの暖機を除く。
        return guarded && seek > 0 ? seek + physical.WarmupSeconds : seek;
    }
}
