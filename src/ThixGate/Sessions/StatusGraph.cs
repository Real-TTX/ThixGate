using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ThixGate.Sessions;

/// <summary>
/// The last minute of a remote desktop as Matgate's web app draws it: the data rate as a faint area, frames a second and latency as lines,
/// each scaled to its own minute (frames at least to 30, latency at least to 100 ms). <see cref="IsSpark"/>: the small version in the
/// status bar - frames only, no grid. Colours come from the theme (set with <c>Use</c>); a gap where a value was not known.
/// </summary>
public sealed class StatusGraph : Control
{
    public static readonly StyledProperty<IBrush?> FramesBrushProperty = AvaloniaProperty.Register<StatusGraph, IBrush?>(nameof(FramesBrush));

    public static readonly StyledProperty<IBrush?> LatencyBrushProperty = AvaloniaProperty.Register<StatusGraph, IBrush?>(nameof(LatencyBrush));

    public static readonly StyledProperty<IBrush?> RateBrushProperty = AvaloniaProperty.Register<StatusGraph, IBrush?>(nameof(RateBrush));

    public static readonly StyledProperty<IBrush?> GridBrushProperty = AvaloniaProperty.Register<StatusGraph, IBrush?>(nameof(GridBrush));

    public static readonly StyledProperty<bool> IsSparkProperty = AvaloniaProperty.Register<StatusGraph, bool>(nameof(IsSpark));

    private const int Capacity = 60;

    private IReadOnlyList<StatusPoint> _points = [];

    static StatusGraph() => AffectsRender<StatusGraph>(FramesBrushProperty, LatencyBrushProperty, RateBrushProperty, GridBrushProperty, IsSparkProperty);

    public IBrush? FramesBrush
    {
        get => GetValue(FramesBrushProperty);
        set => SetValue(FramesBrushProperty, value);
    }

    public IBrush? LatencyBrush
    {
        get => GetValue(LatencyBrushProperty);
        set => SetValue(LatencyBrushProperty, value);
    }

    public IBrush? RateBrush
    {
        get => GetValue(RateBrushProperty);
        set => SetValue(RateBrushProperty, value);
    }

    public IBrush? GridBrush
    {
        get => GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public bool IsSpark
    {
        get => GetValue(IsSparkProperty);
        set => SetValue(IsSparkProperty, value);
    }

    /// <summary>One second: frames shown, latency in ms (null: not known), kilobits a second.</summary>
    public readonly record struct StatusPoint(double Frames, double? Latency, double Kilobits);

    /// <summary>Shows these seconds (oldest first, at most a minute; the newest on the right).</summary>
    public void Show(IReadOnlyList<StatusPoint> points)
    {
        _points = points;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width < 4 || bounds.Height < 4)
        {
            return;
        }

        if (!IsSpark && GridBrush is { } grid)
        {
            var pen = new Pen(grid, 1);
            for (var line = 0; line <= 2; line++)
            {
                var y = Math.Round((bounds.Height - 1) * line / 2) + 0.5;
                context.DrawLine(pen, new Point(0, y), new Point(bounds.Width, y));
            }
        }

        if (_points.Count < 2)
        {
            return;
        }

        var step = bounds.Width / (Capacity - 1);
        var start = Capacity - _points.Count;
        double X(int index) => (start + index) * step;
        double Y(double value, double top) => bounds.Height - 1 - (bounds.Height - 2) * Math.Min(1, value / top);

        if (!IsSpark && RateBrush is { } rate)
        {
            // The data rate as an area, faint.
            var top = Math.Max(1, _points.Max(point => point.Kilobits)) * 1.15;
            var geometry = new StreamGeometry();
            using (var draw = geometry.Open())
            {
                draw.BeginFigure(new Point(X(0), bounds.Height), true);
                for (var index = 0; index < _points.Count; index++)
                {
                    draw.LineTo(new Point(X(index), Y(_points[index].Kilobits, top)));
                }

                draw.LineTo(new Point(X(_points.Count - 1), bounds.Height));
                draw.EndFigure(true);
            }

            using (context.PushOpacity(0.28))
            {
                context.DrawGeometry(rate, null, geometry);
            }
        }

        if (!IsSpark && LatencyBrush is { } latency)
        {
            var top = Math.Max(100, _points.Max(point => point.Latency ?? 0) * 1.15);
            Line(context, latency, top, point => point.Latency, X, Y);
        }

        if (FramesBrush is { } frames)
        {
            var top = Math.Max(30, _points.Max(point => point.Frames) * 1.15);
            Line(context, frames, top, point => point.Frames, X, Y);
        }
    }

    private void Line(DrawingContext context, IBrush brush, double top, Func<StatusPoint, double?> value, Func<int, double> x, Func<double, double, double> y)
    {
        var pen = new Pen(brush, 1.5, lineJoin: PenLineJoin.Round, lineCap: PenLineCap.Round);
        Point? previous = null;
        for (var index = 0; index < _points.Count; index++)
        {
            if (value(_points[index]) is not { } known)
            {
                previous = null;
                continue;
            }

            var point = new Point(x(index), y(known, top));
            if (previous is { } from)
            {
                context.DrawLine(pen, from, point);
            }

            previous = point;
        }
    }
}
