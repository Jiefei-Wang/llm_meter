using System.Drawing;
using System.Windows.Forms;
using LLMMeter.Core;

namespace LLMMeter.UI;

/// <summary>
/// System tray presence. The app lives here; widgets show/hide from this menu.
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly AppServices _services;

    public TrayService(AppServices services)
    {
        _services = services;

        var menu = new ContextMenuStrip();
        menu.Items.Add(Make("Show / Hide  (Ctrl+Alt+L)", (_, _) => services.Widgets.ToggleAllWindows()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Make("New Monitor Window", (_, _) => services.Widgets.CreateWindow(null)));
        menu.Items.Add(Make("Rescan Servers", (_, _) => services.Registry.Discovery.TriggerScan()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Make("Settings…", (_, _) => services.Widgets.ShowSettingsDialog()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Make("Exit", (_, _) => App.RequestShutdown()));

        _icon = new NotifyIcon
        {
            Icon = MakeIcon(),
            Text = "LLM Meter — no backend monitored",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                services.Widgets.ToggleAllWindows();
        };
    }

    private static ToolStripMenuItem Make(string text, EventHandler onClick)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += onClick;
        return item;
    }

    /// <summary>Draws the tray icon (no shipped .ico needed): rounded tile + meter bars.</summary>
    private static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var bg = new SolidBrush(Color.FromArgb(32, 32, 36));
            using var barOn = new SolidBrush(Color.FromArgb(108, 203, 95));
            using var barOff = new SolidBrush(Color.FromArgb(90, 90, 96));

            g.FillRectangle(bg, 0, 0, 32, 32);

            // three bars like a tiny throughput meter
            g.FillRectangle(barOn, 5, 18, 6, 9);
            g.FillRectangle(barOn, 13, 11, 6, 16);
            g.FillRectangle(barOff, 21, 5, 6, 22);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void UpdateTooltip(string text)
    {
        if (_icon.Visible) _icon.Text = text.Length > 60 ? text[..59] + "…" : text;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
