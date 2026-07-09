using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Hotpass.App.ViewModels;
using Hotpass.Core.Model;

namespace Hotpass.App.Controls;

/// <summary>
/// フレームグラフ(flame chart)。横軸=実時間、Graphics キューはネスト深さで行を掘り、
/// Async compute レーンを分離して重なりを可視化する(design.md §4.2)。
/// Ctrl+ホイールでズーム、ドラッグでパン、ダブルクリックでリセット。
/// </summary>
public sealed class TimelineControl : FrameworkElement
{
    private const double RulerHeight = 22;
    private const double GutterWidth = 96;
    private const double RowHeight = 30;
    private const double SpanHeight = 26;
    private const double AsyncGap = 10;
    private const double MinSpanPx = 1.5;

    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items), typeof(IReadOnlyList<TimelineItem>), typeof(TimelineControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            static (d, _) => ((TimelineControl)d).ResetView()));

    public IReadOnlyList<TimelineItem>? Items
    {
        get => (IReadOnlyList<TimelineItem>?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public double BudgetMs { get; set; } = 16.6;

    // ビュー状態(ms 単位)。_viewSpan = 表示幅に対応する時間
    private double _viewStart;
    private double _viewSpan = 20;
    private bool _viewInitialized;
    private Point? _dragOrigin;
    private double _dragStartView;

    // Instrument スキン: 予算線=アンバー、罫線はヘアライン
    private static readonly Brush InkBrush = Frozen("#E6E4DE");
    private static readonly Brush AccentBrush = Frozen("#EFA13C");
    private static readonly Brush MutedBrush = Frozen("#7D7C76");
    private static readonly Brush DarkText = Frozen("#101113");
    private static readonly Brush AsyncBandBrush = Frozen("#12C9A227");
    private static readonly Pen GridPen = FrozenPen("#232526", 1);
    private static readonly Pen HairPen = FrozenPen("#33342F", 1.2);

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

    public TimelineControl()
    {
        ClipToBounds = true;
        Focusable = true;
        ToolTipService.SetInitialShowDelay(this, 100);
    }

    private int MaxDepth => Items?.Where(i => i.Queue == GpuQueue.Graphics).Select(i => i.Depth).DefaultIfEmpty(0).Max() ?? 0;
    private double GfxBandHeight => (MaxDepth + 1) * RowHeight;
    private double AsyncBandHeight => RowHeight + AsyncGap;
    private double PlotWidth => Math.Max(ActualWidth - GutterWidth, 10);
    private double ContentEndMs => Items?.Select(i => i.StartMs + i.DurMs).DefaultIfEmpty(20).Max() ?? 20;

    private void ResetView()
    {
        _viewStart = 0;
        _viewSpan = Math.Max(ContentEndMs * 1.02, BudgetMs * 1.1);
        _viewInitialized = true;
        InvalidateVisual();
    }

    private double XOf(double ms) => GutterWidth + (ms - _viewStart) / _viewSpan * PlotWidth;
    private double MsOf(double x) => _viewStart + (x - GutterWidth) / PlotWidth * _viewSpan;

    protected override Size MeasureOverride(Size availableSize)
    {
        var h = RulerHeight + GfxBandHeight + AsyncBandHeight + 6;
        var w = double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width;
        return new Size(w, h);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (!_viewInitialized) ResetView();
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var items = Items;

        // 背景(ヒットテスト確保のため透明で塗る)
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

        var gfxTop = RulerHeight;
        var asyncTop = gfxTop + GfxBandHeight;

        // グリッドとルーラー(ズームに応じて 1/2/5/10ms 刻みを選ぶ)
        var step = PickTickStep(_viewSpan);
        var first = Math.Floor(_viewStart / step) * step;
        for (var t = first; t <= _viewStart + _viewSpan + step; t += step)
        {
            var x = XOf(t);
            if (x < GutterWidth - 1 || x > ActualWidth) continue;
            dc.DrawLine(GridPen, new Point(x, gfxTop), new Point(x, asyncTop + AsyncBandHeight));
            DrawText(dc, t.ToString(step < 1 ? "0.0" : "0"), x + 3, 2, MutedBrush, 10, dpi, mono: true);
        }

        // async レーン背景と境界
        dc.DrawRectangle(AsyncBandBrush, null, new Rect(GutterWidth, asyncTop + AsyncGap / 2, PlotWidth, RowHeight));
        var dashPen = new Pen(HairPen.Brush, 1) { DashStyle = new DashStyle([3, 3], 0) };
        dc.DrawLine(dashPen, new Point(GutterWidth, asyncTop + AsyncGap / 2), new Point(ActualWidth, asyncTop + AsyncGap / 2));

        // ガター
        dc.DrawLine(HairPen, new Point(GutterWidth - 8, gfxTop), new Point(GutterWidth - 8, asyncTop + AsyncBandHeight));
        DrawText(dc, "Graphics", 0, gfxTop + 4, InkBrush, 12.5, dpi, bold: true);
        DrawText(dc, "queue", 0, gfxTop + 21, MutedBrush, 10.5, dpi);
        DrawText(dc, "Async", 0, asyncTop + AsyncGap / 2 + 2, InkBrush, 12.5, dpi, bold: true);
        DrawText(dc, "compute", 0, asyncTop + AsyncGap / 2 + 19, MutedBrush, 10.5, dpi);

        if (items is null) return;

        // スパン描画(ガター右にクリップ)
        dc.PushClip(new RectangleGeometry(new Rect(GutterWidth, 0, PlotWidth, ActualHeight)));
        foreach (var it in items)
        {
            var x0 = XOf(it.StartMs);
            var x1 = XOf(it.StartMs + it.DurMs);
            if (x1 < GutterWidth || x0 > ActualWidth) continue;
            var wpx = Math.Max(x1 - x0, MinSpanPx);

            var top = it.Queue == GpuQueue.AsyncCompute
                ? asyncTop + AsyncGap / 2 + (RowHeight - SpanHeight) / 2
                : gfxTop + it.Depth * RowHeight + (RowHeight - SpanHeight) / 2;

            var baseColor = CategoryMeta.For(it.Category).Color;
            var color = CategoryMeta.Tint(baseColor, it.Depth);
            var brush = new SolidColorBrush(color);
            dc.DrawRectangle(brush, null, new Rect(x0, top, wpx, SpanHeight));

            if (wpx > 42)
            {
                var label = new FormattedText(it.Name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                    11.5, it.Depth == 0 ? DarkText : InkBrush, dpi)
                { MaxTextWidth = wpx - 12, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
                dc.DrawText(label, new Point(x0 + 7, top + (SpanHeight - label.Height) / 2));
            }
        }

        // 予算ライン(アンバー)
        var bx = XOf(BudgetMs);
        if (bx >= GutterWidth && bx <= ActualWidth)
        {
            dc.DrawRectangle(AccentBrush, null, new Rect(bx - 1, gfxTop, 2, GfxBandHeight + AsyncBandHeight));
            DrawText(dc, $"{BudgetMs:0.0} ms budget", bx + 6, gfxTop + 2, AccentBrush, 10.5, dpi, bold: true);
        }
        dc.Pop();
    }

    private static double PickTickStep(double span) => span switch
    {
        <= 3 => 0.5,
        <= 8 => 1,
        <= 16 => 2,
        <= 40 => 5,
        _ => 10,
    };

    private static readonly FontFamily RulerFont = new("Cascadia Mono, Consolas");
    private static readonly FontFamily LabelFont = new("Segoe UI");

    private static void DrawText(DrawingContext dc, string text, double x, double y, Brush brush, double size, double dpi, bool bold = false, bool mono = false)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(mono ? RulerFont : LabelFont, FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, brush, dpi);
        dc.DrawText(ft, new Point(x, y));
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        e.Handled = true;

        var pos = e.GetPosition(this);
        var anchor = MsOf(Math.Max(pos.X, GutterWidth));
        var factor = e.Delta > 0 ? 1 / 1.25 : 1.25;
        var newSpan = Math.Clamp(_viewSpan * factor, 0.2, Math.Max(ContentEndMs * 2, 40));
        // カーソル位置の時刻を固定してズーム
        _viewStart = anchor - (anchor - _viewStart) * (newSpan / _viewSpan);
        _viewSpan = newSpan;
        ClampView();
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ClickCount == 2) { ResetView(); return; }
        _dragOrigin = e.GetPosition(this);
        _dragStartView = _viewStart;
        CaptureMouse();
        Cursor = Cursors.SizeWE;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _dragOrigin = null;
        ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var pos = e.GetPosition(this);

        if (_dragOrigin is { } origin && e.LeftButton == MouseButtonState.Pressed)
        {
            var dxMs = (origin.X - pos.X) / PlotWidth * _viewSpan;
            _viewStart = _dragStartView + dxMs;
            ClampView();
            InvalidateVisual();
            return;
        }

        // ホバーで正確な時間(狭いスパンはラベルが出ないため)
        var hit = HitTestItem(pos);
        var tip = hit is null ? null
            : $"{hit.Name} · {hit.DurMs:0.00} ms · {hit.StartMs:0.0}–{hit.StartMs + hit.DurMs:0.0} ms";
        if (ToolTip as string != tip) ToolTip = tip;
    }

    private TimelineItem? HitTestItem(Point pos)
    {
        var items = Items;
        if (items is null || pos.X < GutterWidth) return null;
        var gfxTop = RulerHeight;
        var asyncTop = gfxTop + GfxBandHeight;
        var ms = MsOf(pos.X);

        if (pos.Y >= asyncTop + AsyncGap / 2 && pos.Y <= asyncTop + AsyncGap / 2 + RowHeight)
            return items.FirstOrDefault(i => i.Queue == GpuQueue.AsyncCompute && ms >= i.StartMs && ms < i.StartMs + i.DurMs);

        if (pos.Y >= gfxTop && pos.Y < asyncTop)
        {
            var depth = (int)((pos.Y - gfxTop) / RowHeight);
            // 深い行を優先し、無ければ祖先(深さの浅い方)へフォールバック
            for (var d = depth; d >= 0; d--)
            {
                var found = items.FirstOrDefault(i => i.Queue == GpuQueue.Graphics && i.Depth == d && ms >= i.StartMs && ms < i.StartMs + i.DurMs);
                if (d == depth && found is not null) return found;
                if (d == depth && found is null) break;
            }
            return items.FirstOrDefault(i => i.Queue == GpuQueue.Graphics && i.Depth == depth && ms >= i.StartMs && ms < i.StartMs + i.DurMs);
        }
        return null;
    }

    private void ClampView()
    {
        var maxEnd = Math.Max(ContentEndMs, BudgetMs) + _viewSpan * 0.2;
        _viewStart = Math.Clamp(_viewStart, -_viewSpan * 0.1, Math.Max(maxEnd - _viewSpan, 0));
    }
}
