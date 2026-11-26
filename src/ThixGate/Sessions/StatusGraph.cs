using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ThixGate.Sessions;

/// <summary>
/// The last minute of a remote desktop as two lines: the latency (<see cref="LineBrush"/>) and the data rate (<see cref="SecondBrush"/>),
/// each scaled to its own highest value. Colours come from the theme (set with <c>Use</c>); a gap where a value was not known.
/// </summary>
public sealed class StatusGraph : Control
{
    public static readonly StyledProperty<IBrush?> LineBrushProperty = AvaloniaProperty.Register<StatusGraph, IBrush?>(nameof(LineBrush));

    public static readonly StyledProperty<IBrush?> SecondBrushProperty = AvaloniaProperty.Register<StatusGraph, IBrush?>(nameof(SecondBrush));

    public static readonly StyledProperty<IBrush?> GridBrushProperty = AvaloniaProperty.Register<StatusGraph, IBrush?>(nameof(GridBrush));

    private IReadOnlyList<double?> _first = [];
    private IReadOnlyList<double?> _second = [];
    private int _capacity = 60;

    static StatusGraph() => AffectsRender<StatusGraph>(LineBrushProperty, SecondBrushProperty, GridBrushProperty);

    public IBrush? LineBrush
    {
        get => GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    public IBrush? SecondBrush
    {
        get => GetValue(SecondBrushProperty);
        set => SetValue(SecondBrushProperty, value);
    }

    public IBrush? GridBrush
    {
        get => GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    /// <summary>Shows these values (oldest first; at most <paramref name="capacity"/>, the newest on the right).</summary>
    public void Show(IReadOnlyList<double?> first, IReadOnlyList<double?> second, int capacity)
    {
        _first = first;
        _second = second;
        _capacity = Math.Max(2, capacity);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width < 4 || bounds.Height < 4)
        {
            return;
        }

        if (GridBrush is { } grid)
        {
            var pen = new Pen(grid, 1);
            for (var line = 0; line <= 2; line++)
            {
                var y = Math.Round(bounds.Height * line / 2) + 0.5;
                context.DrawLine(pen, new Point(0, y), new Point(bounds.Width, y));
            }
        }

        Draw(context, bounds, _second, SecondBrush, 1.5);
        Draw(context, bounds, _first, LineBrush, 2);
    }

    private void Draw(DrawingContext context, Rect bounds, IReadOnlyList<double?> values, IBrush? brush, double thickness)
    {
        if (brush is null || values.Count < 2)
        {
            return;
        }

        var highest = values.Where(value => value is not null).Select(value => value!.Value).DefaultIfEmpty(0).Max();
        highest = highest <= 0 ? 1 : highest * 1.15;
        var step = bounds.Width / (_capacity - 1);
        var start = _capacity - values.Count;
        var pen = new Pen(brush, thickness, lineJoin: PenLineJoin.Round, lineCap: PenLineCap.Round);
        Point? previous = null;
        for (var index = 0; index < values.Count; index++)
        {
            if (values[index] is not { } value)
            {
                previous = null;
                continue;
            }

            var point = new Point((start + index) * step, bounds.Height - 2 - (bounds.Height - 4) * Math.Min(1, value / highest));
            if (previous is { } from)
            {
                context.DrawLine(pen, from, point);
            }

            previous = point;
        }
    }
}
