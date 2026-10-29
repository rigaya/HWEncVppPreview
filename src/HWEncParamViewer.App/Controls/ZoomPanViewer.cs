using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace HWEncParamViewer.App.Controls;

/// <summary>
/// 拡大・パンできる画像ビューア (設計書 18.3、18.6)。
/// フィルタ後 (After) とフィルタ前 (Before) の2枚を受け取り、どちらか一方、または左右分割で表示する。
/// 2枚は解像度が違っても、同じ表示矩形 (ContentWidth x ContentHeight) に合わせて描く。
/// 並べて表示は、このコントロールを2つ置いて Zoom を共有する。
/// </summary>
public sealed class ZoomPanViewer : Control
{
    public static readonly StyledProperty<Bitmap?> AfterSourceProperty =
        AvaloniaProperty.Register<ZoomPanViewer, Bitmap?>(nameof(AfterSource));
    public static readonly StyledProperty<Bitmap?> BeforeSourceProperty =
        AvaloniaProperty.Register<ZoomPanViewer, Bitmap?>(nameof(BeforeSource));
    public static readonly StyledProperty<double> ContentWidthProperty =
        AvaloniaProperty.Register<ZoomPanViewer, double>(nameof(ContentWidth), 1920);
    public static readonly StyledProperty<double> ContentHeightProperty =
        AvaloniaProperty.Register<ZoomPanViewer, double>(nameof(ContentHeight), 1080);
    public static readonly StyledProperty<bool> ShowBeforeProperty =
        AvaloniaProperty.Register<ZoomPanViewer, bool>(nameof(ShowBefore));
    public static readonly StyledProperty<bool> IsSplitProperty =
        AvaloniaProperty.Register<ZoomPanViewer, bool>(nameof(IsSplit));
    public static readonly StyledProperty<double> SplitPositionProperty =
        AvaloniaProperty.Register<ZoomPanViewer, double>(nameof(SplitPosition), 0.5, defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<ZoomState> ZoomProperty =
        AvaloniaProperty.Register<ZoomPanViewer, ZoomState>(nameof(Zoom), ZoomState.Fit, defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<string?> CornerLabelProperty =
        AvaloniaProperty.Register<ZoomPanViewer, string?>(nameof(CornerLabel));
    public static readonly DirectProperty<ZoomPanViewer, string> ZoomTextProperty =
        AvaloniaProperty.RegisterDirect<ZoomPanViewer, string>(nameof(ZoomText), o => o.ZoomText);

    // 分割線をつかめる範囲 (画面の画素)
    private const double SplitGrip = 6;

    private enum DragKind { None, Pan, Split }
    private DragKind _drag;
    private Point _lastPointer;
    private string _zoomText = "";

    static ZoomPanViewer()
    {
        AffectsRender<ZoomPanViewer>(AfterSourceProperty, BeforeSourceProperty, ContentWidthProperty, ContentHeightProperty,
            ShowBeforeProperty, IsSplitProperty, SplitPositionProperty, ZoomProperty, CornerLabelProperty);
        FocusableProperty.OverrideDefaultValue<ZoomPanViewer>(true);
    }

    public ZoomPanViewer()
    {
        ClipToBounds = true;
        // 透明でもヒットテストに当たるよう背景を持つ (背景色そのものは親の Border が描く)
        Background = Brushes.Transparent;
    }

    public IBrush? Background { get; set; }
    public Bitmap? AfterSource { get => GetValue(AfterSourceProperty); set => SetValue(AfterSourceProperty, value); }
    public Bitmap? BeforeSource { get => GetValue(BeforeSourceProperty); set => SetValue(BeforeSourceProperty, value); }
    /// <summary>表示上の幅 (SAR 考慮後のフィルタ後画像の幅)。前後の2枚ともこの矩形に描く。</summary>
    public double ContentWidth { get => GetValue(ContentWidthProperty); set => SetValue(ContentWidthProperty, value); }
    public double ContentHeight { get => GetValue(ContentHeightProperty); set => SetValue(ContentHeightProperty, value); }
    /// <summary>true ならフィルタ前を表示する (切替モード)。</summary>
    public bool ShowBefore { get => GetValue(ShowBeforeProperty); set => SetValue(ShowBeforeProperty, value); }
    /// <summary>true なら左をフィルタ前、右をフィルタ後で表示する (分割モード)。</summary>
    public bool IsSplit { get => GetValue(IsSplitProperty); set => SetValue(IsSplitProperty, value); }
    /// <summary>分割の境界 (画像の幅に対する割合 0〜1)。</summary>
    public double SplitPosition { get => GetValue(SplitPositionProperty); set => SetValue(SplitPositionProperty, value); }
    public ZoomState Zoom { get => GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
    /// <summary>左上に出すラベル (並べて表示の「前」「後」など)。null なら出さない。</summary>
    public string? CornerLabel { get => GetValue(CornerLabelProperty); set => SetValue(CornerLabelProperty, value); }
    /// <summary>現在の拡大率の表示 (例 "200%")。</summary>
    public string ZoomText { get => _zoomText; private set => SetAndRaise(ZoomTextProperty, ref _zoomText, value); }

    private Size ContentSize => new(Math.Max(1, ContentWidth), Math.Max(1, ContentHeight));

    /// <summary>ウィンドウに合わせる。</summary>
    public void ZoomToFit() => Zoom = ZoomState.Fit;

    /// <summary>等倍にする。</summary>
    public void ZoomToActualSize() => Zoom = ZoomMath.ActualSize(Zoom, Bounds.Size, ContentSize);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ZoomProperty || change.Property == ContentWidthProperty
            || change.Property == ContentHeightProperty || change.Property == BoundsProperty)
        {
            ZoomText = ZoomMath.Format(Zoom, Bounds.Size, ContentSize);
        }
    }

    public override void Render(DrawingContext context)
    {
        var viewport = Bounds.Size;
        if (Background != null) context.FillRectangle(Background, new Rect(viewport));
        var rect = ZoomMath.ContentRect(Zoom, viewport, ContentSize);
        double scale = ZoomMath.EffectiveScale(Zoom, viewport, ContentSize);
        // 等倍以上は画素をそのまま見せる (nearest)。縮小時は高品質に
        var mode = scale >= 1.0 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality;
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = mode }))
        {
            if (IsSplit)
            {
                DrawBitmap(context, AfterSource, rect);
                double splitX = rect.X + rect.Width * Math.Clamp(SplitPosition, 0, 1);
                if (BeforeSource != null)
                {
                    using (context.PushClip(new Rect(rect.X, rect.Y, Math.Max(0, splitX - rect.X), rect.Height)))
                    {
                        DrawBitmap(context, BeforeSource, rect);
                    }
                }
                DrawSplitLine(context, splitX, viewport);
                DrawLabel(context, "前", new Point(Math.Max(rect.X, 0) + 8, Math.Max(rect.Y, 0) + 8), alignRight: false);
                DrawLabel(context, "後", new Point(Math.Min(rect.Right, viewport.Width) - 8, Math.Max(rect.Y, 0) + 8), alignRight: true);
            }
            else
            {
                DrawBitmap(context, ShowBefore ? BeforeSource : AfterSource, rect);
            }
        }
        if (!IsSplit && CornerLabel is { Length: > 0 } label)
        {
            DrawLabel(context, label, new Point(Math.Max(rect.X, 0) + 8, Math.Max(rect.Y, 0) + 8), alignRight: false);
        }
    }

    private static void DrawBitmap(DrawingContext context, Bitmap? bitmap, Rect dest)
    {
        if (bitmap == null) return;
        context.DrawImage(bitmap, new Rect(bitmap.Size), dest);
    }

    private static void DrawSplitLine(DrawingContext context, double x, Size viewport)
    {
        // 明暗どちらの画像の上でも見えるよう、暗い縁取りの上に明るい線を重ねる
        context.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), 3), new Point(x, 0), new Point(x, viewport.Height));
        context.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)), 1), new Point(x, 0), new Point(x, viewport.Height));
    }

    private static void DrawLabel(DrawingContext context, string text, Point anchor, bool alignRight)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            Typeface.Default, 12, Brushes.White);
        var size = new Size(formatted.Width + 12, formatted.Height + 4);
        var origin = alignRight ? new Point(anchor.X - size.Width, anchor.Y) : anchor;
        context.FillRectangle(new SolidColorBrush(Color.FromArgb(176, 0, 0, 0)), new Rect(origin, size), 3);
        context.DrawText(formatted, new Point(origin.X + 6, origin.Y + 2));
    }

    private bool IsOnSplitLine(Point p)
    {
        if (!IsSplit) return false;
        var rect = ZoomMath.ContentRect(Zoom, Bounds.Size, ContentSize);
        double splitX = rect.X + rect.Width * Math.Clamp(SplitPosition, 0, 1);
        return Math.Abs(p.X - splitX) <= SplitGrip;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.Delta.Y == 0) return;
        double factor = e.Delta.Y > 0 ? ZoomMath.WheelStep : 1.0 / ZoomMath.WheelStep;
        Zoom = ZoomMath.ZoomAt(Zoom, Bounds.Size, ContentSize, e.GetPosition(this), factor);
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) return;
        Focus();
        if (e.ClickCount == 2)
        {
            // ダブルクリックでウィンドウに合わせる
            ZoomToFit();
            e.Handled = true;
            return;
        }
        _drag = IsOnSplitLine(point.Position) ? DragKind.Split : DragKind.Pan;
        _lastPointer = point.Position;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        Cursor = _drag == DragKind.Split || (_drag == DragKind.None && IsOnSplitLine(p))
            ? new Cursor(StandardCursorType.SizeWestEast)
            : _drag == DragKind.Pan ? new Cursor(StandardCursorType.SizeAll) : Cursor.Default;
        switch (_drag)
        {
            case DragKind.Pan:
                Zoom = ZoomMath.Pan(Zoom, Bounds.Size, ContentSize, p - _lastPointer);
                _lastPointer = p;
                e.Handled = true;
                break;
            case DragKind.Split:
                var rect = ZoomMath.ContentRect(Zoom, Bounds.Size, ContentSize);
                if (rect.Width > 0) SplitPosition = Math.Clamp((p.X - rect.X) / rect.Width, 0, 1);
                e.Handled = true;
                break;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == DragKind.None) return;
        _drag = DragKind.None;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _drag = DragKind.None;
    }
}
