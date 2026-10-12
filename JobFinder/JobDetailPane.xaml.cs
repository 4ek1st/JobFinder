using System.Windows;
using System.Windows.Controls;

namespace JobFinder;

public partial class JobDetailPane : UserControl
{
    public JobDetailPane() { InitializeComponent(); DataContextChanged += (_, _) => DescriptionScroll.ScrollToTop(); }
    internal ScrollViewer Scroll => DescriptionScroll;
    internal void SetOverlay(bool value) => BackButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
}
