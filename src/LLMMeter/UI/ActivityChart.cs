using System.Globalization;
using System.Windows;
using System.Windows.Media;
using LLMMeter.Core;

namespace LLMMeter.UI;

/// <summary>Lightweight code-native line plot for a rolling five-minute rate series.</summary>
public sealed class ActivityChart : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IReadOnlyList<ActivityPoint>), typeof(ActivityChart),
        new FrameworkPropertyMetadata(Array.Empty<ActivityPoint>(), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(ActivityChart),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<ActivityPoint> Points
    {
        get => (IReadOnlyList<ActivityPoint>)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth < 20 || ActualHeight < 20) return;

        var dim = TryFindResource("DimBrush") as Brush ?? Brushes.Gray;
        var grid = new Pen(dim, 0.6) { DashStyle = DashStyles.Dot };
        grid.Freeze();

        const double labelGap = 4;
        const double top = 2;
        const double right = 4;
        const double bottom = 2;
        var recent = Points.Where(p => p.Timestamp >= DateTimeOffset.Now - RateHistory.Window).ToArray();
        var available = recent.Where(p => p.Value.HasValue).ToArray();
        double max = NiceScaleMaximum(available.Length == 0
            ? 0
            : available.Max(p => Math.Max(0, p.Value!.Value)));
        var labels = new[]
        {
            CreateText(Fmt.Rate(max), dim),
            CreateText(Fmt.Rate(max / 2), dim),
            CreateText(Fmt.Rate(0), dim),
        };
        double left = Math.Ceiling(labels.Max(label => label.Width)) + labelGap + 2;
        double width = Math.Max(1, ActualWidth - left - right);
        double height = Math.Max(1, ActualHeight - top - bottom);

        for (int i = 0; i <= 2; i++)
        {
            double y = top + height * i / 2;
            var label = labels[i];
            dc.DrawText(label, new Point(left - labelGap - label.Width,
                Math.Clamp(y - label.Height / 2, 0, Math.Max(0, ActualHeight - label.Height))));
            dc.PushOpacity(0.24);
            dc.DrawLine(grid, new Point(left, y), new Point(left + width, y));
            dc.Pop();
        }

        if (available.Length == 0)
        {
            DrawText(dc, "No samples yet", dim, left, top + height / 2 - 7);
            return;
        }

        var now = DateTimeOffset.Now;
        var start = now - RateHistory.Window;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            bool figureOpen = false;
            foreach (var point in recent)
            {
                if (!point.Value.HasValue)
                {
                    figureOpen = false;
                    continue;
                }

                double elapsed = (point.Timestamp - start).TotalMilliseconds;
                double x = left + width * Math.Clamp(elapsed / RateHistory.Window.TotalMilliseconds, 0, 1);
                double y = top + height * (1 - Math.Clamp(point.Value.Value / max, 0, 1));
                if (!figureOpen)
                {
                    context.BeginFigure(new Point(x, y), false, false);
                    figureOpen = true;
                }
                else context.LineTo(new Point(x, y), true, false);
            }
        }
        geometry.Freeze();
        var pen = new Pen(Stroke, 0.9) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        // FrameworkElement drawing is not clipped to an arbitrary plot region.
        // Clip explicitly so a stroke centered on y=0 cannot paint beneath the
        // zero-axis line and visually imply a negative rate.
        dc.PushClip(new RectangleGeometry(new Rect(left, top, width, height)));
        dc.DrawGeometry(null, pen, geometry);
        dc.Pop();
    }

    private void DrawText(DrawingContext dc, string value, Brush brush, double x, double y)
    {
        dc.DrawText(CreateText(value, brush, 8.5), new Point(x, y));
    }

    private FormattedText CreateText(string value, Brush brush, double size = 7.5) =>
        new(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    /// <summary>Rounds the visible peak upward to a readable, data-driven Y-axis maximum.</summary>
    internal static double NiceScaleMaximum(double dataMaximum)
    {
        if (!double.IsFinite(dataMaximum) || dataMaximum <= 0) return 1;

        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(dataMaximum)));
        double normalized = dataMaximum / magnitude;
        double nice = normalized switch
        {
            <= 1 => 1,
            <= 2 => 2,
            <= 2.5 => 2.5,
            <= 5 => 5,
            <= 7.5 => 7.5,
            _ => 10,
        };
        return nice * magnitude;
    }
}
