using System.Windows;

namespace SazViewer.App.Views;

/// <summary>
/// A merged dictionary that loads each <see cref="Source"/> once per thread and shares it, so every native view can
/// merge <c>ViewResources.xaml</c> (needed for StaticResource lookups during InitializeComponent, before the view is
/// parented) without re-parsing the styles for each recycled list item.
/// </summary>
internal sealed class SharedResourceDictionary : ResourceDictionary
{
    [ThreadStatic]
    private static Dictionary<Uri, ResourceDictionary>? cache;

    public new Uri Source
    {
        get => base.Source;
        set
        {
            cache ??= [];
            if (!cache.TryGetValue(value, out var shared))
            {
                shared = new ResourceDictionary { Source = value };
                cache[value] = shared;
            }
            MergedDictionaries.Add(shared);
        }
    }
}
