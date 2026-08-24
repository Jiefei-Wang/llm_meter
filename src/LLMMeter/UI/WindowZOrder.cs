using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LLMMeter.UI;

/// <summary>Applies a window's native topmost band without activating it.</summary>
internal static class WindowZOrder
{
    internal const uint NoMove = 0x0002;
    internal const uint NoSize = 0x0001;
    internal const uint NoActivate = 0x0010;
    internal const uint NoOwnerZOrder = 0x0200;
    internal const uint ApplyFlags = NoMove | NoSize | NoActivate | NoOwnerZOrder;

    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotTopmost = new(-2);

    public static void Apply(Window window, bool enabled)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        window.SetCurrentValue(Window.TopmostProperty, enabled);
        _ = SetWindowPos(hwnd, enabled ? HwndTopmost : HwndNotTopmost,
            0, 0, 0, 0, ApplyFlags);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
