using Avalonia;

namespace HWEncParamViewer.App.Controls;

/// <summary>
/// 拡大・パンの状態。拡大率と「ビューポート中央に来るコンテンツ上の点」で表す。
/// 中心をコンテンツ座標で持つので、並べて表示 (2つのビューポート) でもそのまま共有できる。
/// </summary>
/// <param name="IsFit">ウィンドウに合わせる表示か。true のとき Scale と Center は使わない。</param>
/// <param name="Scale">拡大率 (1 = 表示上の1画素が画面の1画素)。</param>
/// <param name="Center">ビューポート中央に表示するコンテンツ上の点 (表示上の画素単位)。</param>
public readonly record struct ZoomState(bool IsFit, double Scale, Point Center)
{
    public static ZoomState Fit { get; } = new(true, 1.0, default);
}

/// <summary>拡大・パンの計算 (純粋な関数。設計書 18.3)。</summary>
public static class ZoomMath
{
    public const double MinScale = 0.25;
    public const double MaxScale = 32.0;
    public const double WheelStep = 1.25;

    /// <summary>ウィンドウに合わせるときの拡大率。</summary>
    public static double FitScale(Size viewport, Size content)
    {
        if (content.Width <= 0 || content.Height <= 0 || viewport.Width <= 0 || viewport.Height <= 0) return 1.0;
        return Math.Min(viewport.Width / content.Width, viewport.Height / content.Height);
    }

    /// <summary>実際に使う拡大率。</summary>
    public static double EffectiveScale(ZoomState state, Size viewport, Size content) =>
        state.IsFit ? FitScale(viewport, content) : state.Scale;

    /// <summary>コンテンツを描く矩形 (ビューポート座標)。</summary>
    public static Rect ContentRect(ZoomState state, Size viewport, Size content)
    {
        double scale = EffectiveScale(state, viewport, content);
        var size = new Size(content.Width * scale, content.Height * scale);
        if (state.IsFit)
        {
            return new Rect(new Point((viewport.Width - size.Width) / 2, (viewport.Height - size.Height) / 2), size);
        }
        var origin = new Point(viewport.Width / 2 - state.Center.X * scale, viewport.Height / 2 - state.Center.Y * scale);
        return new Rect(origin, size);
    }

    /// <summary>
    /// cursor (ビューポート座標) の下にあるコンテンツ上の点を動かさずに、拡大率を factor 倍する。
    /// 拡大率は [min(MinScale, fit), MaxScale] に収める。
    /// </summary>
    public static ZoomState ZoomAt(ZoomState state, Size viewport, Size content, Point cursor, double factor)
    {
        double fit = FitScale(viewport, content);
        double oldScale = EffectiveScale(state, viewport, content);
        double newScale = Math.Clamp(oldScale * factor, Math.Min(MinScale, fit), MaxScale);
        var rect = ContentRect(state, viewport, content);
        // カーソルの下のコンテンツ座標
        var anchor = new Point((cursor.X - rect.X) / oldScale, (cursor.Y - rect.Y) / oldScale);
        // 拡大後も anchor が cursor に来るように中心を決める
        var center = new Point(
            anchor.X + (viewport.Width / 2 - cursor.X) / newScale,
            anchor.Y + (viewport.Height / 2 - cursor.Y) / newScale);
        return new ZoomState(false, newScale, center);
    }

    /// <summary>等倍 (100%) にする。中心はいまビューポート中央にある点を保つ。</summary>
    public static ZoomState ActualSize(ZoomState state, Size viewport, Size content)
    {
        var rect = ContentRect(state, viewport, content);
        double scale = EffectiveScale(state, viewport, content);
        var center = new Point((viewport.Width / 2 - rect.X) / scale, (viewport.Height / 2 - rect.Y) / scale);
        return new ZoomState(false, 1.0, center);
    }

    /// <summary>画面上で delta だけドラッグしたときのパン。</summary>
    public static ZoomState Pan(ZoomState state, Size viewport, Size content, Vector delta)
    {
        double scale = EffectiveScale(state, viewport, content);
        var rect = ContentRect(state, viewport, content);
        // Fit 状態からドラッグしたら、その時点の表示を起点に自由なパンへ移る
        var center = new Point((viewport.Width / 2 - rect.X) / scale, (viewport.Height / 2 - rect.Y) / scale);
        return new ZoomState(false, scale, new Point(center.X - delta.X / scale, center.Y - delta.Y / scale));
    }

    /// <summary>表示用の拡大率の文字列 (例 "200%")。</summary>
    public static string Format(ZoomState state, Size viewport, Size content) =>
        $"{Math.Round(EffectiveScale(state, viewport, content) * 100):0}%";
}
