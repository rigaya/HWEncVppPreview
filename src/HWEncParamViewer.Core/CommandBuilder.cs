using System.Globalization;

namespace HWEncParamViewer.Core;

/// <summary>プレビュー要求 (設計書 3.3)。</summary>
public sealed record PreviewRequest
{
    // 通常要求と順先読みで共通に使う、論理窓の既定余白。
    internal const double DefaultPreMarginSeconds = 2.0;
    internal const double DefaultPostMarginSeconds = 0.5;

    /// <summary>呼び出し側の要求識別子。通知へ引き継ぐが、エンコード引数と画像キャッシュのキーには含めない。</summary>
    public long CorrelationId { get; init; }
    /// <summary>この比較要求の背景取得を、独立backendで前景と同時に始めてよい。既定の移動は前景を優先する。</summary>
    public bool AllowConcurrentBackground { get; init; }
    /// <summary>helpで確認できた、プレビュー時に付加するオプション。</summary>
    public PreviewCapabilities Capabilities { get; init; } = new();
    /// <summary>フィルタなし・seekなしの先頭Xts。未取得なら要求を跨ぐ時計として使わない。</summary>
    public double? TimestampOriginSeconds { get; init; }
    /// <summary>原点がwrap近傍ならfalse。数値を保持したまま要求単位の相対時計へ戻す。</summary>
    public bool TimestampOriginTrusted { get; init; } = true;
    /// <summary>既知の入力長。有限取得が入力末尾へ届いたかの判定だけに使う。</summary>
    public double? InputDurationSeconds { get; init; }
    /// <summary>trueなら--framesを付けず、読み取り側の需要で出力を止める。</summary>
    public bool Continuous { get; init; }
    /// <summary>物理要求のseekを固定する。nullは通常計画、0は明示的な先頭取得。</summary>
    public double? SeekSecondsOverride { get; init; }
    /// <summary>物理要求で確定した暖機。論理要求や逆方向の既定値は従来の2秒。</summary>
    public double WarmupSeconds { get; internal init; } = PreviewWarmupPolicy.DefaultWarmupSeconds;
    /// <summary>当該引数で実受信したprobeの履歴依存判定。物理計画専用で画像内容のキーには含めない。</summary>
    internal bool? DependsOnHistory { get; init; }
    // 限定回復の物理窓。論理要求の通知・引数・世代は変えない。
    internal PreviewRequest? RecoveryPhysicalWindow { get; init; }

    /// <summary>エンコーダのexeパス。引数配列には含めない（起動側が使う）。</summary>
    public string EncoderPath { get; init; } = "";
    public string InputFile { get; init; } = "";
    /// <summary>入力の追加引数。</summary>
    public IReadOnlyList<string> InputArgs { get; init; } = new[] { "--avhw" };
    /// <summary>目標時刻 T (秒)。</summary>
    public double TargetSeconds { get; init; }
    /// <summary>前マージン Mb (秒)。</summary>
    public double PreMarginSeconds { get; init; } = DefaultPreMarginSeconds;
    /// <summary>後マージン Ma (秒)。</summary>
    public double PostMarginSeconds { get; init; } = DefaultPostMarginSeconds;
    /// <summary>入力のフレームレート。--frames の算出に使う。</summary>
    public double Fps { get; init; }
    /// <summary>vpp 引数 (OptionTextParser の出力)。</summary>
    public IReadOnlyList<string> VppArgs { get; init; } = Array.Empty<string>();
}

/// <summary>要求から決まる実行計画。</summary>
/// <param name="SeekSeconds">--seek に渡す時刻 (ms に丸め済み)。--seek を付けないとき null。</param>
/// <param name="ActualPreMargin">実際に使う前マージン。FrameSelector に渡す値。T が Mb より小さいときは T。</param>
/// <param name="FrameCount">--frames に渡す値。</param>
public sealed record PreviewPlan(double? SeekSeconds, double ActualPreMargin, int FrameCount);

public static class CommandBuilder
{
    /// <summary>正常に取得したhelpの索引だけから、追加引数の対応を判定する。</summary>
    public static PreviewCapabilities CapabilitiesFromHelp(HelpIndex? help) => help == null ? new() : new(
        help.OptionNames.Contains("--timestamp-passthrough", StringComparer.Ordinal),
        help.OptionNames.Contains("--opencl-task-threads", StringComparer.Ordinal),
        help.OptionNames.Contains("--colormatrix", StringComparer.Ordinal),
        help.OptionNames.Contains("--colorprim", StringComparer.Ordinal),
        help.OptionNames.Contains("--transfer", StringComparer.Ordinal),
        help.OptionNames.Contains("--colorrange", StringComparer.Ordinal),
        help.OptionNames.Contains("--chromaloc", StringComparer.Ordinal));
    public static PreviewPlan Plan(PreviewRequest req)
    {
        if (req.Fps <= 0) throw new ArgumentException(CoreText.FpsPositive, nameof(req));
        if (req.TargetSeconds < 0 || req.PreMarginSeconds < 0 || req.PostMarginSeconds < 0)
        {
            throw new ArgumentException(CoreText.TimeMarginNonnegative, nameof(req));
        }
        // T < Mb なら先頭から処理し、前マージンは T にする
        double mb = Math.Min(req.PreMarginSeconds, req.TargetSeconds);
        // ms に丸めてから前マージンを逆算する (実際に --seek へ渡す値と整合させる)
        long seekMs = (long)Math.Round((req.TargetSeconds - mb) * 1000.0, MidpointRounding.AwayFromZero);
        if (req.SeekSecondsOverride is double forced)
        {
            if (!double.IsFinite(forced) || forced < 0) throw new ArgumentException(CoreText.SeekNonnegative, nameof(req));
            seekMs = (long)Math.Round(forced * 1000.0, MidpointRounding.AwayFromZero);
        }
        double? seek = seekMs > 0 ? seekMs / 1000.0 : null;
        double actualMb = req.TargetSeconds - (seek ?? 0.0);
        // 1e-9 は (0.7+0.1)*30=24.000000000000004 のような浮動小数の誤差で ceil が1つ増えるのを防ぐ
        int frames = (int)Math.Ceiling((actualMb + req.PostMarginSeconds) * req.Fps - 1e-9) + 1;
        return new PreviewPlan(seek, actualMb, frames);
    }

    /// <summary>都度起動用の引数。出力関係 (-c raw --y4m-timestamp -o -) を含む。</summary>
    public static string[] BuildOneShot(PreviewRequest req) => Build(req, includeOutput: true);

    /// <summary>常駐サーバ用の引数。出力関係はサーバが付けるので含めない (含めるとサーバがエラーにする)。</summary>
    public static string[] BuildServer(PreviewRequest req) => Build(req, includeOutput: false);

    private static string[] Build(PreviewRequest req, bool includeOutput)
    {
        var plan = Plan(req);
        var args = new List<string>();
        // 入力の色情報を既定で引き継ぎ、後続の利用者の明示指定は上書きしない。
        if (req.Capabilities.ColorMatrix) args.AddRange(new[] { "--colormatrix", "auto" });
        if (req.Capabilities.ColorPrimaries) args.AddRange(new[] { "--colorprim", "auto" });
        if (req.Capabilities.Transfer) args.AddRange(new[] { "--transfer", "auto" });
        if (req.Capabilities.ColorRange) args.AddRange(new[] { "--colorrange", "auto" });
        if (req.Capabilities.ChromaLocation) args.AddRange(new[] { "--chromaloc", "auto" });
        args.AddRange(req.InputArgs);
        args.Add("-i");
        args.Add(req.InputFile);
        if (plan.SeekSeconds is double s)
        {
            args.Add("--seek");
            args.Add(FormatSeek(s));
        }
        if (!req.Continuous)
        {
            args.Add("--frames");
            args.Add(plan.FrameCount.ToString(CultureInfo.InvariantCulture));
        }
        if (includeOutput)
        {
            args.AddRange(new[] { "-c", "raw", "--y4m-timestamp", "-o", "-" });
        }
        args.AddRange(req.VppArgs);
        // --y4m-timestamp(2026-08)を前提にしているため、color(2025-05)も必ず使える。
        // ログ末尾の色リセットが常駐サーバの制御行へ混ざらないよう、最後に色を無効化する。
        args.AddRange(new[] { "--log-opt", "color=false" });
        // 入力/VPP引数より後に置き、Viewerが確認したプレビュー用設定を適用する。
        if (req.Capabilities.TimestampPassthrough) args.Add("--timestamp-passthrough");
        if (req.Capabilities.OpenClTaskThreads) args.AddRange(new[] { "--opencl-task-threads", "0" });
        return args.ToArray();
    }

    /// <summary>秒を h:mm:ss.fff に整形する。1時間以上でも時は桁を増やすだけ (例: 100:00:00.000)。</summary>
    public static string FormatSeek(double seconds)
    {
        long totalMs = (long)Math.Round(seconds * 1000.0, MidpointRounding.AwayFromZero);
        long h = totalMs / 3_600_000;
        long m = totalMs / 60_000 % 60;
        long s = totalMs / 1000 % 60;
        long ms = totalMs % 1000;
        return string.Create(CultureInfo.InvariantCulture, $"{h}:{m:00}:{s:00}.{ms:000}");
    }
}
