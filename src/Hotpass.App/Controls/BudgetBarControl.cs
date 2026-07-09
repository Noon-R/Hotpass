using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Hotpass.App.ViewModels;

namespace Hotpass.App.Controls;

/// <summary>
/// フレーム予算スタックバー(design.md §4.2)。
/// パスを時間比で並べ、16.6ms 予算マーカーと超過ハッチング、0/5/10/15/20ms ルーラーを描く。
/// </summary>
public sealed class BudgetBarControl : FrameworkElement
{
    private const double BarHeight = 34;
    private const double RulerGap = 7;
    private const double RulerHeight = 18;

    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IReadOnlyList<BarSegment>), typeof(BudgetBarControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowRulerProperty = DependencyProperty.Register(
        nameof(ShowRuler), typeof(bool), typeof(BudgetBarControl),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<BarSegment>? Segments
    {
        get => (IReadOnlyList<BarSegment>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public bool ShowRuler
    {
        get => (bool)GetValue(ShowRulerProperty);
        set => SetValue(ShowRulerProperty, value);
    }

    public double TrackMs { get; set; } = 20.0;
    public double BudgetMs { get; set; } = 16.6;

    // Instrument スキン: トラック=raise、予算線=アンバー、区切りは背景色の細線
    private static readonly Brush TrackBrush = Frozen("#1A1C20");
    private static readonly Brush BudgetBrush = Frozen("#EFA13C");
    private static readonly Brush MutedBrush = Frozen("#7D7C76");
    private static readonly Pen OutlinePen = FrozenPen("#33342F", 1);
    private static readonly Pen SeparatorPen = FrozenPen("#B30D0E10", 1);
    private static readonly Pen TickPen = FrozenPen("#33342F", 1);
    private static readonly Brush HatchBrush = CreateHatch();

    private static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    private static Pen FrozenPen(string hex, double w)
    {
        var p = new Pen(Frozen(hex), w);
        p.Freeze();
        return p;
    }

    private static Brush CreateHatch()
    {
        // 予算超過分に重ねる斜線ハッチ
        var stripe = new GeometryDrawing(
            Frozen("#8CF0604A"),
            null,
            new RectangleGeometry(new Rect(0, 0, 4, 10)));
        var bg = new GeometryDrawing(Frozen("#1FF0604A"), null, new RectangleGeometry(new Rect(0, 0, 10, 10)));
        var brush = new DrawingBrush(new DrawingGroup { Children = { bg, stripe } })
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 10, 10),
            ViewportUnits = BrushMappingMode.Absolute,
            Transform = new RotateTransform(-45),
        };
        brush.Freeze();
        return brush;
    }

    public BudgetBarControl()
    {
        ToolTipService.SetInitialShowDelay(this, 100);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var h = BarHeight + (ShowRuler ? RulerGap + RulerHeight : 0);
        var w = double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width;
        return new Size(w, h);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        if (w <= 0) return;
        var barRect = new Rect(0, 0, w, BarHeight);

        // トラック(直角・アウトラインのみ)
        dc.PushClip(new RectangleGeometry(barRect));
        dc.DrawRectangle(TrackBrush, null, barRect);

        var segs = Segments;
        double x = 0;
        if (segs is not null)
        {
            foreach (var s in segs)
            {
                var sw = s.Ms / TrackMs * w;
                dc.DrawRectangle(s.Brush, null, new Rect(x, 0, sw, BarHeight));
                if (x > 0) dc.DrawLine(SeparatorPen, new Point(x, 0), new Point(x, BarHeight));
                x += sw;
            }

            // 予算超過ハッチ
            var budgetX = BudgetMs / TrackMs * w;
            if (x > budgetX)
                dc.DrawRectangle(HatchBrush, null, new Rect(budgetX, 0, x - budgetX, BarHeight));
        }
        dc.Pop();
        dc.DrawRectangle(null, OutlinePen, barRect);

        // 予算マーカー(アンバー・バーから上下にはみ出す縦線)
        var bx = BudgetMs / TrackMs * w;
        dc.DrawRectangle(BudgetBrush, null, new Rect(bx - 1, -5, 2, BarHeight + 10));

        if (ShowRuler)
        {
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var ry = BarHeight + RulerGap;
            for (var v = 0; v <= (int)TrackMs; v += 5)
            {
                var tx = v / TrackMs * w;
                dc.DrawLine(TickPen, new Point(tx, ry), new Point(tx, ry + 5));
                DrawText(dc, v.ToString(), tx, ry + 5, MutedBrush, 11, dpi, center: true);
            }
            DrawText(dc, BudgetMs.ToString("0.0"), bx, ry + 5, BudgetBrush, 11, dpi, center: true, bold: true);
        }
    }

    private void DrawText(DrawingContext dc, string text, double x, double y, Brush brush, double size, double dpi, bool center = false, bool bold = false)
    {
        // ルーラー数値はモノスペース(Instrument スキン)
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal),
            size, brush, dpi);
        dc.DrawText(ft, new Point(center ? x - ft.Width / 2 : x, y));
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var segs = Segments;
        if (segs is null || ActualWidth <= 0) return;
        var pos = e.GetPosition(this);
        if (pos.Y > BarHeight) { ToolTip = null; return; }

        double x = 0;
        foreach (var s in segs)
        {
            var sw = s.Ms / TrackMs * ActualWidth;
            if (pos.X >= x && pos.X < x + sw)
            {
                var tip = $"{s.Name} · {s.Ms:0.0} ms · {s.PctOfFrame:0}%";
                if (ToolTip as string != tip) ToolTip = tip;
                return;
            }
            x += sw;
        }
        ToolTip = null;
    }
}
