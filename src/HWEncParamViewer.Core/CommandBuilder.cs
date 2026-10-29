using System.Globalization;

namespace HWEncParamViewer.Core;

/// <summary>プレビュー要求 (設計書 3.3)。</summary>
public sealed record PreviewRequest
{
    /// <summary>呼び出し側の要求識別子。通知へ引き継ぐが、エンコード引数と画像キャッシュのキーには含めない。</summary>
    public long CorrelationId { get; init; }

    /// <summary>エンコーダのexeパス。引数配列には含めない（起動側が使う）。</summary>
    public string EncoderPath { get; init; } = "";
    public string InputFile { get; init; } = "";
    /// <summary>入力の追加引数。</summary>
    public IReadOnlyList<string> InputArgs { get; init; } = new[] { "--avhw" };
    /// <summary>目標時刻 T (秒)。</summary>
    public double TargetSeconds { get; init; }
    /// <summary>前マージン Mb (秒)。</summary>
    public double PreMarginSeconds { get; init; } = 2.0;
    /// <summary>後マージン Ma (秒)。</summary>
    public double PostMarginSeconds { get; init; } = 0.5;
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
    public static PreviewPlan Plan(PreviewRequest req)
    {
        if (req.Fps <= 0) throw new ArgumentException("Fps は正の値が必要です", nameof(req));
        if (req.TargetSeconds < 0 || req.PreMarginSeconds < 0 || req.PostMarginSeconds < 0)
        {
            throw new ArgumentException("時刻とマージンは 0 以上が必要です", nameof(req));
        }
        // T < Mb なら先頭から処理し、前マージンは T にする
        double mb = Math.Min(req.PreMarginSeconds, req.TargetSeconds);
        // ms に丸めてから前マージンを逆算する (実際に --seek へ渡す値と整合させる)
        long seekMs = (long)Math.Round((req.TargetSeconds - mb) * 1000.0, MidpointRounding.AwayFromZero);
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
        args.AddRange(req.InputArgs);
        args.Add("-i");
        args.Add(req.InputFile);
        if (plan.SeekSeconds is double s)
        {
            args.Add("--seek");
            args.Add(FormatSeek(s));
        }
        args.Add("--frames");
        args.Add(plan.FrameCount.ToString(CultureInfo.InvariantCulture));
        if (includeOutput)
        {
            args.AddRange(new[] { "-c", "raw", "--y4m-timestamp", "-o", "-" });
        }
        args.AddRange(req.VppArgs);
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
