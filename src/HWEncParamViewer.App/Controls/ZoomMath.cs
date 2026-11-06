using Avalonia;

namespace HWEncParamViewer.App.Controls;

/// <summary>
/// 拡大・パンの状態。拡大率と「ビューポート中央に来るコンテンツ上の点」で表す。
/// 中心をコンテンツ座標で持つので、並べて表示 (2つのビューポート) でもそのまま共有できる。
/// </summary>
/// <param name="IsFit">ウィンドウに合わせる表示か。true のとき Scale と Center は使わない。</param>
/// <param name="Scale">
/// 拡大率 (1 = 表示上の1画素が画面の物理的な1画素)。OS の表示倍率 (150% など) には左右されない。
/// 描画は論理単位 (DIP) で行うので、DIP あたりの拡大率は Scale / pixelsPerDip になる。
/// </param>
/// <param name="Center">ビューポート中央に表示するコンテンツ上の点 (表示上の画素単位)。</param>
public readonly record struct ZoomState(bool IsFit, double Scale, Point Center)
{
    public static ZoomState Fit { get; } = new(true, 1.0, default);
}

/// <summary>
/// 拡大・パンの計算 (純粋な関数。設計書 18.3)。
/// pixelsPerDip は OS の表示倍率 (TopLevel.RenderScaling、150% なら 1.5)。座標と大きさは DIP、拡大率は物理画素で数える。
/// </summary>
public static class ZoomMath
{
    /// <summary>保存画素を縦横それぞれ整数倍で描く手動表示だけ、画素を補間せずに表示する。</summary>
    public static bool UseNearest(bool isFit, Size drawnDipSize, PixelSize pixels, double pixelsPerDip = 1)
    {
        if (isFit || pixels.Width <= 0 || pixels.Height <= 0) return false;
        double x = drawnDipSize.Width * PixelsPerDip(pixelsPerDip) / pixels.Width;
        double y = drawnDipSize.Height * PixelsPerDip(pixelsPerDip) / pixels.Height;
        const double tolerance = 1e-6;
        // SARが異なっても、両軸が整数なら各保存画素の表示幅にムラが出ない。
        return double.IsFinite(x) && double.IsFinite(y) && x >= 1 - tolerance && y >= 1 - tolerance
            && Math.Abs(x - Math.Round(x)) <= tolerance
            && Math.Abs(y - Math.Round(y)) <= tolerance;
    }

    /// <summary>表示幅の変更後も、中央にある画像上の点を保つ。</summary>
    public static ZoomState ResizeContentWidth(ZoomState state, double oldWidth, double newWidth)
        => !state.IsFit && oldWidth > 0 && newWidth > 0 && double.IsFinite(oldWidth) && double.IsFinite(newWidth)
            ? state with { Center = new Point(state.Center.X * newWidth / oldWidth, state.Center.Y) } : state;

    public const double MinScale = 0.25;
    public const double MaxScale = 32.0;
    public const double WheelStep = 1.25;

    /// <summary>ウィンドウに合わせるときの拡大率。</summary>
    public static double FitScale(Size viewport, Size content)
    {
        if (content.Width <= 0 || content.Height <= 0 || viewport.Width <= 0 || viewport.Height <= 0) return 1.0;
        return Math.Min(viewport.Width / content.Width, viewport.Height / content.Height);
    }

    /// <summary>実際に使う拡大率 (DIP あたり。描画の座標計算に使う)。</summary>
    public static double EffectiveScale(ZoomState state, Size viewport, Size content, double pixelsPerDip = 1.0) =>
        state.IsFit ? FitScale(viewport, content) : state.Scale / PixelsPerDip(pixelsPerDip);

    /// <summary>物理画素あたりの拡大率 (表示や「等倍以上か」の判定に使う)。</summary>
    public static double DeviceScale(ZoomState state, Size viewport, Size content, double pixelsPerDip = 1.0) =>
        EffectiveScale(state, viewport, content, pixelsPerDip) * PixelsPerDip(pixelsPerDip);

    /// <summary>描く矩形の左上を物理画素の境目にそろえる (等倍・整数倍で画素がにじんだり幅が揃わなくなったりしないように)。</summary>
    public static Rect SnapToDevicePixels(Rect rect, double pixelsPerDip)
    {
        double ppd = PixelsPerDip(pixelsPerDip);
        var origin = new Point(Math.Round(rect.X * ppd) / ppd, Math.Round(rect.Y * ppd) / ppd);
        return new Rect(origin, rect.Size);
    }

    private static double PixelsPerDip(double value) => double.IsFinite(value) && value > 0 ? value : 1.0;

    /// <summary>コンテンツを描く矩形 (ビューポート座標)。</summary>
    public static Rect ContentRect(ZoomState state, Size viewport, Size content, double pixelsPerDip = 1.0)
    {
        double scale = EffectiveScale(state, viewport, content, pixelsPerDip);
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
    public static ZoomState ZoomAt(ZoomState state, Size viewport, Size content, Point cursor, double factor, double pixelsPerDip = 1.0)
    {
        double ppd = PixelsPerDip(pixelsPerDip);
        // 上限・下限は物理画素の拡大率で決め、座標の計算は DIP で行う
        double fitDevice = FitScale(viewport, content) * ppd;
        double oldScale = EffectiveScale(state, viewport, content, ppd);
        double newDevice = Math.Clamp(oldScale * ppd * factor, Math.Min(MinScale, fitDevice), MaxScale);
        double newScale = newDevice / ppd;
        var rect = ContentRect(state, viewport, content, ppd);
        // カーソルの下のコンテンツ座標
        var anchor = new Point((cursor.X - rect.X) / oldScale, (cursor.Y - rect.Y) / oldScale);
        // 拡大後も anchor が cursor に来るように中心を決める
        var center = new Point(
            anchor.X + (viewport.Width / 2 - cursor.X) / newScale,
            anchor.Y + (viewport.Height / 2 - cursor.Y) / newScale);
        return new ZoomState(false, newDevice, center);
    }

    /// <summary>等倍 (表示上の1画素 = 物理的な1画素) にする。中心はいまビューポート中央にある点を保つ。</summary>
    public static ZoomState ActualSize(ZoomState state, Size viewport, Size content, double pixelsPerDip = 1.0)
    {
        var rect = ContentRect(state, viewport, content, pixelsPerDip);
        double scale = EffectiveScale(state, viewport, content, pixelsPerDip);
        var center = new Point((viewport.Width / 2 - rect.X) / scale, (viewport.Height / 2 - rect.Y) / scale);
        return new ZoomState(false, 1.0, center);
    }

    /// <summary>画面上で delta だけドラッグしたときのパン。</summary>
    public static ZoomState Pan(ZoomState state, Size viewport, Size content, Vector delta, double pixelsPerDip = 1.0)
    {
        double ppd = PixelsPerDip(pixelsPerDip);
        double scale = EffectiveScale(state, viewport, content, ppd);
        var rect = ContentRect(state, viewport, content, ppd);
        // Fit 状態からドラッグしたら、その時点の表示を起点に自由なパンへ移る
        var center = new Point((viewport.Width / 2 - rect.X) / scale, (viewport.Height / 2 - rect.Y) / scale);
        return new ZoomState(false, scale * ppd, new Point(center.X - delta.X / scale, center.Y - delta.Y / scale));
    }

    /// <summary>表示用の拡大率の文字列 (例 "200%")。物理画素で数える。</summary>
    public static string Format(ZoomState state, Size viewport, Size content, double pixelsPerDip = 1.0) =>
        $"{Math.Round(DeviceScale(state, viewport, content, pixelsPerDip) * 100):0}%";
}
