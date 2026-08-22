using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace LLMMeter.UI;

/// <summary>Applies a light/dark palette following the Windows app theme.</summary>
public static class ThemeManager
{
    public static bool IsDarkByDefault()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int v)
                return v == 0;
        }
        catch { }
        return true; // default to dark for the benchmarking crowd
    }

    public static void Apply(bool dark)
    {
        var uri = new Uri(dark ? "UI/Theme.Dark.xaml" : "UI/Theme.Light.xaml", UriKind.Relative);
        var dict = new ResourceDictionary { Source = uri };

        var app = Application.Current;
        var merged = app.Resources.MergedDictionaries;
        for (int i = 0; i < merged.Count; i++)
        {
            if (merged[i].Source?.OriginalString.EndsWith("Theme.Light.xaml", StringComparison.OrdinalIgnoreCase) == true ||
                merged[i].Source?.OriginalString.EndsWith("Theme.Dark.xaml", StringComparison.OrdinalIgnoreCase) == true)
            {
                merged[i] = dict;
                return;
            }
        }
        merged.Insert(0, dict);
    }
}
