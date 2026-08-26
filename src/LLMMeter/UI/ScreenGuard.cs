using System.Windows;

namespace LLMMeter.UI;

/// <summary>Keeps restored window positions on a visible monitor using WPF DIP coordinates.</summary>
public static class ScreenGuard
{
    public static Point EnsureVisible(double x, double y, double width, double height)
    {
        var vRect = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);

        return EnsureVisible(x, y, width, height, vRect);
    }

    public static Point EnsureVisible(double x, double y, double width, double height, Rect virtualBounds)
    {
        if (virtualBounds.Width <= 0 || virtualBounds.Height <= 0)
            return new Point(x, y);

        const double margin = 24;
        double maxX = virtualBounds.Left + virtualBounds.Width - width - margin;
        double maxY = virtualBounds.Top + virtualBounds.Height - height - margin;
        double minX = virtualBounds.Left + margin;
        double minY = virtualBounds.Top + margin;

        x = double.IsNaN(x) ? minX : Math.Clamp(x, minX, Math.Max(minX, maxX));
        y = double.IsNaN(y) ? minY : Math.Clamp(y, minY, Math.Max(minY, maxY));

        return new Point(x, y);
    }
}
