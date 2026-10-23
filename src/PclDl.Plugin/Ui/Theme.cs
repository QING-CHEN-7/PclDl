using System.Windows;
using System.Windows.Media;

namespace PclDl.Plugin.Ui
{
    /// <summary>读取 PCL 的动态主题画刷（ColorBrush1..8），取不到时用后备色。</summary>
    internal static class Theme
    {
        public static void Apply(FrameworkElement element, DependencyProperty property, string resourceKey, Brush fallback)
        {
            try
            {
                if (element == null || property == null) return;
                element.SetValue(property, fallback);
                element.SetResourceReference(property, resourceKey);
            }
            catch { }
        }
    }
}
