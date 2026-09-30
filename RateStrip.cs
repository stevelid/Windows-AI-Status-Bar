using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using StatusBar.Core.Usage;
using Color = System.Windows.Media.Color;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using ToolTip = System.Windows.Controls.ToolTip;

namespace ClaudeUsageWidget;

/// <summary>
/// A compact strip showing how fast one quota window was used over the last two hours: a smooth area whose
/// height is the burn rate, a dashed line at the even pace a five-hour window can sustain, and the part
/// above that line in a warm colour.
/// </summary>
internal sealed class RateStrip : FrameworkElement
{
    const double TopPadding = 3;
    const double LabelSize = 8.5;

    UsageRateSeries? _series;
    Color _color = Colors.Gray;
    int _hover = -1;
    readonly ToolTip _tip = new() { Placement = PlacementMode.Relative, StaysOpen = true, Padding = new Thickness(8, 5, 8, 6) };
    readonly TextBlock _tipText = new() { FontSize = 11, LineHeight = 15 };

    /// <summary>Describes one slice for the hover tooltip (time, rate, what was running); null turns the hover off.</summary>
    public Func<RatePoint, string>? Describe { get; set; }

    public RateStrip()
    {
        _tip.Content = _tipText;
        _tip.BorderThickness = new Thickness(1);
        MouseLeave += (_, _) => EndHover();
        Unloaded += (_, _) => EndHover();
    }

    /// <summary>Sets the series and the colour of the calm part; redraws.</summary>
    public void Update(UsageRateSeries? series, Color color)
    {
        _series = series;
        _color = color;
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var series = _series;
        if (series is null || !series.HasData || Describe is null || ActualWidth < 20) return;
        var position = e.GetPosition(this);
        var index = Math.Clamp((int)(position.X / ActualWidth * series.Points.Count), 0, series.Points.Count - 1);
        if (index != _hover)
        {
            _hover = index;
            InvalidateVisual();
        }

        // Match the pane's own colours rather than the system tooltip's.
        var surface = ThemeManager.IsLight ? Color.FromRgb(0xFA, 0xFA, 0xFC) : Color.FromRgb(0x2A, 0x2A, 0x34);
        _tip.Background = ThemeManager.Brush(surface);
        _tip.BorderBrush = ThemeManager.Brush(ThemeManager.IsLight ? Color.FromRgb(0xB8, 0xB9, 0xC3) : Color.FromRgb(0x5A, 0x5B, 0x67));
        _tipText.Foreground = ThemeManager.Brush(ThemeManager.TitleText);
        _tipText.Text = Describe(series.Points[index]);
        _tip.PlacementTarget = this;
        _tip.HorizontalOffset = Math.Min(position.X + 10, Math.Max(0, ActualWidth - 200));
        _tip.VerticalOffset = ActualHeight + 4;
        _tip.IsOpen = true;
    }

    void EndHover()
    {
        _tip.IsOpen = false;
        if (_hover < 0) return;
        _hover = -1;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 20 || height < 10) return;

        // A transparent fill so the hover works over the whole strip, not only where something is drawn.
        dc.DrawRectangle(System.Windows.Media.Brushes.Transparent, null, new Rect(0, 0, width, height));

        var subtle = ThemeManager.SubtleText;
        var baseline = height - 0.5;
        dc.DrawLine(new Pen(ThemeManager.Brush(Color.FromArgb(0x60, subtle.R, subtle.G, subtle.B)), 0.6), new Point(0, baseline), new Point(width, baseline));

        var series = _series;
        if (series is null || !series.HasData)
        {
            DrawLabel(dc, L10n.T("pane_rate_collecting"), width, height, subtle, alignRight: false);
            return;
        }

        var points = series.Points;
        var yMax = Math.Max(UsageRateHistory.EvenPacePerHour * 1.5, (series.PeakPerHour ?? 0) * 1.2);
        var plotHeight = baseline - TopPadding;
        double YOf(double perHour) => baseline - plotHeight * Math.Min(perHour, yMax) / yMax;
        double XOf(int index) => width * (index + 0.5) / points.Count;

        var evenY = YOf(UsageRateHistory.EvenPacePerHour);
        var hot = ThemeManager.IsLight ? Color.FromRgb(0xD9, 0x4A, 0x2B) : Color.FromRgb(0xF2, 0x6B, 0x4E);

        // Resets: a faint vertical line where the window's allowance came back.
        var start = points[0].At - UsageRateHistory.Bucket / 2;
        foreach (var reset in series.Resets)
        {
            var x = width * (reset - start) / UsageRateHistory.Span;
            if (x is > 0 and < 1e6) dc.DrawLine(new Pen(ThemeManager.Brush(Color.FromArgb(0x70, subtle.R, subtle.G, subtle.B)), 0.8), new Point(x, TopPadding), new Point(x, baseline));
        }

        // One run per stretch of consecutive slices that have data, so a gap is left as a gap.
        var runs = new List<List<Point>>();
        List<Point>? current = null;
        for (var i = 0; i < points.Count; i++)
        {
            if (points[i].PerHour is not double value)
            {
                current = null;
                continue;
            }

            if (current is null)
            {
                current = new List<Point>();
                runs.Add(current);
            }
            current.Add(new Point(XOf(i), YOf(value)));
        }

        var calmFill = ThemeManager.Brush(Color.FromArgb(0x50, _color.R, _color.G, _color.B));
        var hotFill = ThemeManager.Brush(Color.FromArgb(0x85, hot.R, hot.G, hot.B));
        var calmPen = new Pen(ThemeManager.Brush(_color), 1.3) { LineJoin = PenLineJoin.Round };
        var hotPen = new Pen(ThemeManager.Brush(hot), 1.3) { LineJoin = PenLineJoin.Round };
        var below = new RectangleGeometry(new Rect(0, evenY, width, Math.Max(0, baseline - evenY)));
        var above = new RectangleGeometry(new Rect(0, 0, width, evenY));

        foreach (var run in runs)
        {
            if (run.Count == 1)
            {
                dc.DrawEllipse(ThemeManager.Brush(_color), null, run[0], 1.6, 1.6);
                continue;
            }

            var line = SmoothLine(run, TopPadding, baseline);
            var area = SmoothLine(run, TopPadding, baseline, closeTo: baseline);
            dc.PushClip(below);
            dc.DrawGeometry(calmFill, null, area);
            dc.DrawGeometry(null, calmPen, line);
            dc.Pop();
            dc.PushClip(above);
            dc.DrawGeometry(hotFill, null, area);
            dc.DrawGeometry(null, hotPen, line);
            dc.Pop();
        }

        dc.DrawLine(
            new Pen(ThemeManager.Brush(Color.FromArgb(0xB0, subtle.R, subtle.G, subtle.B)), 0.7) { DashStyle = new DashStyle([3, 3], 0) },
            new Point(0, evenY),
            new Point(width, evenY));

        if (_hover >= 0 && _hover < points.Count)
        {
            var x = XOf(_hover);
            dc.DrawLine(new Pen(ThemeManager.Brush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)), 0.8), new Point(x, TopPadding), new Point(x, baseline));
        }

        if (series.PeakPerHour is double peak)
            DrawLabel(dc, L10n.F("pane_rate_peak", Math.Round(peak).ToString("0", CultureInfo.CurrentCulture)), width, height, subtle, alignRight: true);
    }

    // Catmull-Rom through the points as cubic Béziers, clamped to the plot so the curve never dips below the baseline.
    static StreamGeometry SmoothLine(IReadOnlyList<Point> points, double top, double baseline, double? closeTo = null)
    {
        double Clamp(double y) => Math.Clamp(y, top, baseline);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var first = points[0];
            context.BeginFigure(closeTo is double floor ? new Point(first.X, floor) : first, closeTo is not null, closeTo is not null);
            if (closeTo is not null) context.LineTo(first, false, false);
            for (var i = 0; i < points.Count - 1; i++)
            {
                var p0 = i > 0 ? points[i - 1] : points[i];
                var p1 = points[i];
                var p2 = points[i + 1];
                var p3 = i + 2 < points.Count ? points[i + 2] : p2;
                var c1 = new Point(p1.X + (p2.X - p0.X) / 6, Clamp(p1.Y + (p2.Y - p0.Y) / 6));
                var c2 = new Point(p2.X - (p3.X - p1.X) / 6, Clamp(p2.Y - (p3.Y - p1.Y) / 6));
                context.BezierTo(c1, c2, p2, true, true);
            }

            if (closeTo is double bottom)
            {
                context.LineTo(new Point(points[^1].X, bottom), false, false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    void DrawLabel(DrawingContext dc, string text, double width, double height, Color color, bool alignRight)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            System.Windows.FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            LabelSize,
            ThemeManager.Brush(color),
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var x = alignRight ? width - formatted.Width - 1 : 2;
        dc.DrawText(formatted, new Point(x, alignRight ? 0 : (height - formatted.Height) / 2));
    }
}
