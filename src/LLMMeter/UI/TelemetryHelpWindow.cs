using System.Windows;
using System.Windows.Controls;
using LLMMeter.Adapters;

namespace LLMMeter.UI;

/// <summary>
/// Compact non-modal explanation of limited telemetry (spec §31):
/// what's available, what isn't, and how to enable full metrics.
/// </summary>
public sealed class TelemetryHelpWindow : Window
{
    public TelemetryHelpWindow(TelemetryHelp help)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = "LLM Meter — telemetry info";
        Deactivated += (_, _) => Close();

        var stack = new StackPanel { Margin = new Thickness(12) };

        stack.Children.Add(new TextBlock
        {
            Text = help.Summary,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6),
        });

        if (help.Available.Count > 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "Available:",
                Foreground = (System.Windows.Media.Brush)Application.Current.Resources["DimBrush"],
                FontSize = 11,
            });
            foreach (var a in help.Available)
                stack.Children.Add(new TextBlock { Text = $"✓ {a}", FontSize = 11.5 });
        }

        if (help.Unavailable.Count > 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "Unavailable:",
                Foreground = (System.Windows.Media.Brush)Application.Current.Resources["DimBrush"],
                FontSize = 11,
                Margin = new Thickness(0, 8, 0, 0),
            });
            foreach (var u in help.Unavailable)
                stack.Children.Add(new TextBlock { Text = $"✕ {u}", FontSize = 11.5 });
        }

        if (!string.IsNullOrWhiteSpace(help.SuggestedCommand))
        {
            stack.Children.Add(new TextBlock
            {
                Text = "Enable full monitoring:",
                Foreground = (System.Windows.Media.Brush)Application.Current.Resources["DimBrush"],
                FontSize = 11,
                Margin = new Thickness(0, 10, 0, 2),
            });
            var cmdBorder = new Border
            {
                Background = (System.Windows.Media.Brush)Application.Current.Resources["PanelBrush"],
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 5, 8, 5),
                Child = new TextBlock
                {
                    Text = help.SuggestedCommand,
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                },
            };
            stack.Children.Add(cmdBorder);

            var copyRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            var copyBtn = new Button
            {
                Content = "Copy command",
                Padding = new Thickness(10, 3, 10, 3),
            };
            copyBtn.Click += (_, _) =>
            {
                try { Clipboard.SetText(help.SuggestedCommand); copyBtn.Content = "Copied ✓"; }
                catch { }
            };
            copyRow.Children.Add(copyBtn);
            stack.Children.Add(copyRow);
        }

        if (!string.IsNullOrWhiteSpace(help.CurrentCommand))
        {
            stack.Children.Add(new TextBlock
            {
                Text = "Current process:",
                Foreground = (System.Windows.Media.Brush)Application.Current.Resources["DimBrush"],
                FontSize = 11,
                Margin = new Thickness(0, 8, 0, 2),
            });
            stack.Children.Add(new TextBlock
            {
                Text = help.CurrentCommand,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        var note = new TextBlock
        {
            Text = "LLM Meter never restarts or modifies your server.",
            Foreground = (System.Windows.Media.Brush)Application.Current.Resources["DimBrush"],
            FontSize = 10,
            Margin = new Thickness(0, 10, 0, 0),
        };
        stack.Children.Add(note);

        Content = new Border
        {
            Background = (System.Windows.Media.Brush)Application.Current.Resources["BgBrush"],
            BorderBrush = (System.Windows.Media.Brush)Application.Current.Resources["BorderBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            MaxWidth = 420,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 14,
                ShadowDepth = 2,
                Opacity = 0.4,
            },
            Child = stack,
        };
    }

    public void ShowNear(Window anchor)
    {
        // Position near the anchor's top-right, on-screen.
        Left = Math.Max(SystemParameters.VirtualScreenLeft, anchor.Left + anchor.ActualWidth - 60);
        Top = Math.Max(SystemParameters.VirtualScreenTop, anchor.Top + 40);
        Show();
        Activate();
    }
}
