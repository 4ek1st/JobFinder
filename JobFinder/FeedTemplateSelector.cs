using System.Windows;
using System.Windows.Controls;

namespace JobFinder;

public sealed class FeedTemplateSelector : DataTemplateSelector
{
    public override DataTemplate SelectTemplate(object item, DependencyObject container) => (DataTemplate)((FrameworkElement)container).FindResource(item is FeedStatusView ? "FeedStatusTemplate" : "JobCardTemplate");
}
