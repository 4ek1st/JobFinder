using System.Windows;
using System.Windows.Media;

namespace JobFinder;

public static class ThemeManager
{
    public static bool IsDark { get; private set; } = true;
    public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
    public static void Apply(string? theme)
    {
        IsDark = theme != "light";
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var palette = new ResourceDictionary { Source = new Uri($"Themes/{(IsDark ? "Dark" : "Light")}.xaml", UriKind.Relative) };
        if (dictionaries.Count == 0) dictionaries.Add(palette); else dictionaries[0] = palette;
    }
    public static T Color<T>(T control, DependencyProperty property, string key) where T : FrameworkElement
    { control.SetResourceReference(property, key); return control; }
}
