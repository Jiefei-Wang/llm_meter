using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using LLMMeter.Core;

namespace LLMMeter.UI;

/// <summary>
/// The backend selector popup: discovered targets grouped by origin
/// (Windows / WSL · distro), manual endpoints with ●/○ status,
/// then "＋ Add backend…" and "↻ Rescan" actions.
/// </summary>
public sealed class PopupHost
{
    private readonly WidgetManager _manager;
    private readonly MonitorWindow _owner;
    private readonly Popup _popup = new()
    {
        StaysOpen = false,
        AllowsTransparency = true,
        Placement = PlacementMode.RelativePoint,
    };

    public event Action? Closed;

    public PopupHost(WidgetManager manager, MonitorWindow owner)
    {
        _manager = manager;
        _owner = owner;
    }

    public void ShowBelow(MonitorWindow owner)
    {
        var panel = BuildContent();
        var border = new Border
        {
            Background = (Brush)Application.Current.Resources["PopupBgBrush"],
            BorderBrush = (Brush)Application.Current.Resources["BorderBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(4),
            MaxHeight = 420,
            Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel },
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 12,
                ShadowDepth = 2,
                Opacity = 0.35,
            },
        };

        _popup.Child = border;
        _popup.PlacementTarget = owner;
        _popup.HorizontalOffset = 14;
        _popup.VerticalOffset = 36;
        _popup.Closed += (_, _) => Closed?.Invoke();
        _popup.IsOpen = true;
    }

    public void Dismiss() => _popup.IsOpen = false;

    private StackPanel BuildContent()
    {
        var panel = new StackPanel { MinWidth = 260 };
        var entries = _manager.Registry.GetTargetEntries();

        string? lastGroup = null;
        foreach (var e in entries)
        {
            if (lastGroup != null && e.GroupLabel != lastGroup)
                panel.Children.Add(GroupSeparator(e.GroupLabel));
            else if (lastGroup == null)
                panel.Children.Add(GroupSeparator(e.GroupLabel));
            lastGroup = e.GroupLabel;

            string status = e.Online ? "● " : "○ ";
            string model = e.ModelName is { Length: > 0 } m ? $"\n      {Trunc(m, 38)}" : "";
            string stateNote = !e.Online && e.State == ConnectionState.Connecting ? "  …" : "";

            var item = new Button
            {
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8, 5, 8, 5),
                Cursor = System.Windows.Input.Cursors.Hand,
                Content = new TextBlock
                {
                    Text = $"{status}{Trunc(e.Target.DisplayName, 44)}{stateNote}{model}",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 11.5,
                    Foreground = (Brush)Application.Current.Resources["FgBrush"],
                },
                ToolTip = $"{MonitorWindow.DescribeOrigin(e)}\n{e.Target.Kind.DisplayName()}",
            };
            item.Click += (_, _) =>
            {
                Dismiss();
                _owner.Bind(e);
            };
            StyleHover(item);
            panel.Children.Add(item);
        }

        if (entries.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "No backends found yet.\nWaiting for discovery…",
                FontSize = 11,
                Foreground = (Brush)Application.Current.Resources["DimBrush"],
                Margin = new Thickness(8, 6, 8, 6),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        panel.Children.Add(new Separator { Margin = new Thickness(4, 6, 4, 2) });

        var add = ActionRow("＋  Add backend…", () => { Dismiss(); _manager.ShowAddBackendDialog(); });
        panel.Children.Add(add);

        var rescan = ActionRow("↻  Rescan", () => { Dismiss(); _manager.Registry.Discovery.TriggerScan(); });
        panel.Children.Add(rescan);

        return panel;
    }

    private static TextBlock GroupHeader(string text) => new()
    {
        Text = text,
        FontSize = 10.5,
        Margin = new Thickness(8, 7, 4, 3),
        Foreground = (Brush)Application.Current.Resources["DimBrush"],
    };

    private static StackPanel GroupSeparator(string label)
    {
        var sp = new StackPanel();
        sp.Children.Add(new Separator { Margin = new Thickness(4, 2, 4, 1), Visibility = Visibility.Collapsed });
        sp.Children.Add(GroupHeader(label));
        return sp;
    }

    private static Button ActionRow(string label, Action onClick)
    {
        var b = new Button
        {
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 5, 8, 5),
            Cursor = System.Windows.Input.Cursors.Hand,
            Content = new TextBlock
            {
                Text = label,
                FontSize = 11.5,
                Foreground = (Brush)Application.Current.Resources["FgBrush"],
            },
        };
        b.Click += (_, _) => onClick();
        StyleHover(b);
        return b;
    }

    private static void StyleHover(Button b)
    {
        var template = new ControlTemplate(typeof(Button));
        var factory = new FrameworkElementFactory(typeof(Border));
        factory.Name = "bd";
        factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        factory.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        factory.AppendChild(presenter);
        template.VisualTree = factory;

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty,
            Application.Current.Resources["HoverBrush"], "bd"));
        template.Triggers.Add(hover);

        b.Template = template;
    }

    internal static string Trunc(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
