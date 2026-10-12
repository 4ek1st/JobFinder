using System.Windows;
using System.Windows.Controls;

namespace JobFinder;

public partial class SourceDirectory : UserControl
{
    public SourceDirectoryViewModel Model { get; }
    public SourceDirectory(ViewModel app, DataTemplate sourceTemplate, DataTemplate radiusTemplate, bool statusView = false)
    {
        InitializeComponent(); Model = new(app, statusView); DataContext = Model;
        FeedList.ItemTemplate = sourceTemplate; RadiusChoice.ItemTemplate = radiusTemplate;
        Loaded += (_, _) => { if (!Model.IsStatusView) SourceSearch.Focus(); else CloseButton.Focus(); }; Unloaded += (_, _) => Model.Dispose();
    }
    private void ClearSearch(object sender, RoutedEventArgs e) { Model.Search = ""; SourceSearch.Focus(); }
    private void CloseDialog(object sender, RoutedEventArgs e) => Window.GetWindow(this)?.Close();
}
