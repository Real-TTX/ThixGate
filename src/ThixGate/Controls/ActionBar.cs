using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace ThixGate.Controls;

/// <summary>
/// The row of buttons below a list or form: left-aligned, in one line, from positive to negative.
/// A button with <c>controls:ActionBar.Separated="True"</c> (delete) gets a clear gap before it.
/// When the buttons do not fit in one line, every button after the first shows only its icon (its text becomes its tool tip and its name);
/// when even that does not fit, the buttons continue on the next line (order unchanged).
/// </summary>
public sealed class ActionBar : Panel
{
    public static readonly AttachedProperty<bool> SeparatedProperty =
        AvaloniaProperty.RegisterAttached<ActionBar, Control, bool>("Separated");

    public static readonly StyledProperty<double> SpacingProperty = AvaloniaProperty.Register<ActionBar, double>(nameof(Spacing), 8);

    public static readonly StyledProperty<double> SeparationProperty = AvaloniaProperty.Register<ActionBar, double>(nameof(Separation), 32);

    static ActionBar()
    {
        AffectsParentMeasure<ActionBar>(SeparatedProperty);
        AffectsMeasure<ActionBar>(SpacingProperty, SeparationProperty);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public double Separation
    {
        get => GetValue(SeparationProperty);
        set => SetValue(SeparationProperty, value);
    }

    public static bool GetSeparated(Control element) => element.GetValue(SeparatedProperty);

    public static void SetSeparated(Control element, bool value) => element.SetValue(SeparatedProperty, value);

    private readonly Dictionary<Control, double> _fullWidths = [];
    private bool _compact;

    /// <summary>The buttons show only their icons (they did not fit in one line with their texts).</summary>
    public bool IsCompact => _compact;

    protected override Size MeasureOverride(Size availableSize)
    {
        MeasureChildren();

        // The width of the line with every text (remembered from when the texts showed): compact only when that does not fit.
        var children = VisibleChildren().ToList();
        var full = 0d;
        for (var index = 0; index < children.Count; index++)
        {
            var child = children[index];
            var childWidth = _fullWidths.TryGetValue(child, out var known) ? known : child.DesiredSize.Width;
            full += childWidth + (index == 0 ? 0 : GetSeparated(child) ? Separation : Spacing);
        }

        var compact = !double.IsInfinity(availableSize.Width) && full > availableSize.Width;
        if (compact != _compact)
        {
            _compact = compact;
            foreach (var child in children.Skip(1))
            {
                SetCompact(child, compact);
            }

            MeasureChildren();
        }

        var lines = Layout(availableSize.Width);
        var width = lines.Count == 0 ? 0 : lines.Max(line => line.Width);
        return new Size(width, lines.Sum(line => line.Height) + Math.Max(0, lines.Count - 1) * Spacing);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double y = 0;
        foreach (var line in Layout(finalSize.Width))
        {
            foreach (var (child, x) in line.Items)
            {
                child.Arrange(new Rect(x, y, child.DesiredSize.Width, line.Height));
            }

            y += line.Height + Spacing;
        }

        return finalSize;
    }

    /// <summary>
    /// The buttons as a menu: every visible, enabled one with its icon and text, in their order; the gap before delete becomes a line.
    /// Null when no button can be used.
    /// </summary>
    public MenuFlyout? AsMenu()
    {
        var menu = new MenuFlyout();
        foreach (var child in VisibleChildren())
        {
            if (child is not Button { IsEffectivelyEnabled: true, Flyout: null, Content: IconLabel label } button)
            {
                continue;
            }

            var text = label.Text.Length > 0 ? label.Text : AutomationProperties.GetName(button);
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            if (GetSeparated(child) && menu.Items.Count > 0)
            {
                menu.Items.Add(new Separator());
            }

            var item = new MenuItem { Header = text, Icon = new IconLabel { Icon = label.Icon } };
            AutomationProperties.SetName(item, text);
            item.Click += (_, _) => button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            menu.Items.Add(item);
        }

        return menu.Items.Count == 0 ? null : menu;
    }

    /// <summary>
    /// The right mouse button on a row of <paramref name="list"/> chooses that row and offers these buttons as a menu (the same actions as
    /// below the list, for the row under the pointer).
    /// </summary>
    public void ServeAsMenuOf(ListBox list) =>
        list.ContextRequested += (_, e) =>
        {
            if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not { } row)
            {
                return;
            }

            list.SelectedItem = row.DataContext;
            if (AsMenu() is { } menu)
            {
                LastMenu = menu;
                menu.ShowAt(row, showAtPointer: true);
                e.Handled = true;
            }
        };

    /// <summary>The menu the right mouse button opened last (tests).</summary>
    internal MenuFlyout? LastMenu { get; private set; }

    private void MeasureChildren()
    {
        foreach (var child in VisibleChildren())
        {
            child.Measure(Size.Infinity);
            if (child is not Button { Content: IconLabel { IsTextVisible: false } })
            {
                _fullWidths[child] = child.DesiredSize.Width;
            }
        }
    }

    private static void SetCompact(Control child, bool compact)
    {
        if (child is not Button { Content: IconLabel { Text.Length: > 0 } label } button)
        {
            return;
        }

        label.IsTextVisible = !compact;
        if (compact && ToolTip.GetTip(button) is null)
        {
            ToolTip.SetTip(button, label.Text);
            button.SetValue(CompactTipProperty, true);
        }
        else if (!compact && button.GetValue(CompactTipProperty))
        {
            ToolTip.SetTip(button, null);
            button.SetValue(CompactTipProperty, false);
        }

        if (compact && string.IsNullOrEmpty(AutomationProperties.GetName(button)))
        {
            AutomationProperties.SetName(button, label.Text);
        }
    }

    private static readonly AttachedProperty<bool> CompactTipProperty =
        AvaloniaProperty.RegisterAttached<ActionBar, Control, bool>("CompactTip");

    private List<Line> Layout(double availableWidth)
    {
        var lines = new List<Line>();
        var current = new Line();
        foreach (var child in VisibleChildren())
        {
            var gap = current.Items.Count == 0 ? 0 : GetSeparated(child) ? Separation : Spacing;
            if (current.Items.Count > 0 && current.Width + gap + child.DesiredSize.Width > availableWidth)
            {
                lines.Add(current);
                current = new Line();
                gap = 0;
            }

            current.Items.Add((child, current.Width + gap));
            current.Width += gap + child.DesiredSize.Width;
            current.Height = Math.Max(current.Height, child.DesiredSize.Height);
        }

        if (current.Items.Count > 0)
        {
            lines.Add(current);
        }

        return lines;
    }

    private IEnumerable<Control> VisibleChildren() => Children.Where(child => child.IsVisible);

    private sealed class Line
    {
        public List<(Control Child, double X)> Items { get; } = [];

        public double Width { get; set; }

        public double Height { get; set; }
    }
}
