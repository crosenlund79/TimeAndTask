using System.Collections;
using System.Windows;
using System.Windows.Media;

namespace WorkTrace.App.Shell;

/// <summary>
/// Applies a visual-preview skin (Themes/Variants/{Variant}/Theme.xaml, compiled only into that preview's build).
/// Every resource is copied to the top level of the application resources, where it takes precedence over the
/// Fluent theme dictionaries and Styles.xaml, so views, popups and standard controls all pick it up unchanged.
/// </summary>
internal static class PreviewTheme
{
    /// <summary>The Fluent base the variant was designed on (its palette replaces all Fluent brushes).</summary>
    public static ThemeMode Mode => PreviewIdentity.Variant == "DarkProfessional" ? ThemeMode.Dark : ThemeMode.Light;

    public static void Apply(Application app)
    {
        if (PreviewIdentity.Variant is not { } variant)
        {
            return;
        }

        app.ThemeMode = Mode;
        var theme = (ResourceDictionary)Application.LoadComponent(new Uri($"/WorkTrace;component/Themes/Variants/{variant}/Theme.xaml", UriKind.Relative));
        var shared = (ResourceDictionary)Application.LoadComponent(new Uri("/WorkTrace;component/Themes/Variants/PreviewStyles.xaml", UriKind.Relative));
        CopyTo(theme, app.Resources);
        CopyTo(shared, app.Resources);

        // The variant's body font for every window (inherited by all text, including popups and dialogs).
        if (app.Resources["AppFontFamily"] is FontFamily font)
        {
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
            {
                if (sender is Window window && !Equals(window.FontFamily, font))
                {
                    window.FontFamily = font;
                }
            }));
        }
    }

    private static void CopyTo(ResourceDictionary source, ResourceDictionary target)
    {
        foreach (var merged in source.MergedDictionaries)
        {
            CopyTo(merged, target);
        }

        foreach (DictionaryEntry entry in source)
        {
            target[entry.Key] = entry.Value;
        }
    }
}
