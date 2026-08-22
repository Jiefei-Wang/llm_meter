using System.Windows;

namespace LLMMeter.UI;

/// <summary>Keeps restored window positions on a visible monitor.</summary>
public static class ScreenGuard
{
    public static Point EnsureVisible(double x, double y, double width, double height)
    {
        // Virtual screen bounds via Win32
        var left = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
        var top = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        var vWidth = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
        var vHeight = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);

        if (vWidth <= 0 || vHeight <= 0)
            return new Point(x, y);

        const double margin = 24;
        double maxX = left + vWidth - width - margin;
        double maxY = top + vHeight - height - margin;
        double minX = left + margin;
        double minY = top + margin;

        x = double.IsNaN(x) ? minX : Math.Clamp(x, minX, Math.Max(minX, maxX));
        y = double.IsNaN(y) ? minY : Math.Clamp(y, minY, Math.Max(minY, maxY));

        return new Point(x, y);
    }

    private static class Native
    {
        public const int SM_XVIRTUALSCREEN = 76;
        public const int SM_YVIRTUALSCREEN = 77;
        public const int SM_CXVIRTUALSCREEN = 78;
        public const int SM_CYVIRTUALSCREEN = 79;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int index);
    }
}
