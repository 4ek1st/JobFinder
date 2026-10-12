using JobFinder.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace JobFinder;

public partial class MainWindow : ThemedWindow
{
    public ViewModel Vm { get; }
    public MainWindow(ViewModel vm)
    {
        Vm = vm; InitializeComponent(); DataContext = vm; InitializeUpdateUi();
        Vm.JobsUpdating += CapturePosition; Vm.JobsUpdated += RestorePosition;
        Vm.PropertyChanged += OnModelChanged;
        JobList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnFeedScrollChanged));
        Width = Math.Min(1320, SystemParameters.WorkArea.Width * 0.95); Height = Math.Min(930, SystemParameters.WorkArea.Height * 0.95);
        MinWidth = Math.Min(660, SystemParameters.WorkArea.Width * 0.9); MinHeight = Math.Min(500, SystemParameters.WorkArea.Height * 0.9);
        Closed += (_, _) => { Vm.JobsUpdating -= CapturePosition; Vm.JobsUpdated -= RestorePosition; Vm.PropertyChanged -= OnModelChanged; Vm.Ui.PropertyChanged -= OnUiLanguageChanged; _closed = true; if (!Vm.Updates.IsRestarting) { Vm.Profile.LastQuery = new(Vm.Keyword, Vm.City, Vm.Radius, Vm.Kind); Vm.Save(); } Vm.Dispose(); };
    }
    internal bool AutoLoadEnabled { get; set; } = true;
    private bool _paginationQueued, _closed;
    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.SearchExpanded))
        {
            bool returnFocus = IsInside(Keyboard.FocusedElement as DependencyObject, SearchSurface);
            CapturePosition(true); Adjust(); RestorePosition(true);
            bool expanded = Vm.SearchExpanded;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
            {
                if (_closed || Vm.SearchExpanded != expanded) return;
                if (expanded) { KeywordBox.Focus(); KeywordBox.SelectAll(); }
                else if (returnFocus) EditSearchButton.Focus();
            }));
            QueueAutomaticLoad();
        }
        if (e.PropertyName == nameof(ViewModel.Detail))
        {
            if (Vm.HasDetail && !_detailsOpen) { _detailReturnOffset = FeedScroll?.VerticalOffset ?? 0; _detailReturnFocus = Keyboard.FocusedElement; }
            bool returnFromOverlay = !Vm.HasDetail && _detailsOpen && !_sideBySide;
            CapturePosition(true); _detailsOpen = Vm.HasDetail; Adjust(); RestorePosition(true);
            if (returnFromOverlay) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => { FeedScroll?.ScrollToVerticalOffset(_detailReturnOffset); if (_detailReturnFocus is UIElement element && element.IsVisible) element.Focus(); else if (Vm.SelectedJob is JobView job && JobList.ItemContainerGenerator.ContainerFromItem(job) is ListBoxItem item) item.Focus(); QueueAutomaticLoad(); }));
        }
        if (e.PropertyName is nameof(ViewModel.HasNext) or nameof(ViewModel.IsBusy) or nameof(ViewModel.ShowFavorites)) QueueAutomaticLoad();
    }
    private bool _detailsOpen, _sideBySide;
    private double _detailReturnOffset;
    private IInputElement? _detailReturnFocus;
    private void OnFeedScrollChanged(object sender, ScrollChangedEventArgs e) => QueueAutomaticLoad();
    internal void QueueAutomaticLoad()
    {
        if (_paginationQueued || !AutoLoadEnabled || _closed) return;
        _paginationQueued = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(async () =>
        {
            _paginationQueued = false;
            if (_closed || !IsLoaded || !AutoLoadEnabled || !Vm.HasNext || Vm.HasDetail && !_sideBySide) return;
            var viewer = FeedScroll;
            if (!Vm.Empty && (viewer is null || viewer.ViewportHeight < 1 || viewer.ScrollableHeight - viewer.VerticalOffset > Math.Min(480, viewer.ViewportHeight * 0.65))) return;
            // A single search owns all source requests. Further demand is evaluated after layout/anchor restoration.
            await Vm.SearchAsync(more: true);
            QueueAutomaticLoad();
        }));
    }
    private string? _anchorKey, _focusKey;
    private double _anchorY;
    private ICommand? _focusCommand;
    private int _layoutGeneration;
    private bool _resetPosition;
    internal ScrollViewer? FeedScroll => Descendants<ScrollViewer>(JobList).FirstOrDefault();
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match; foreach (var nested in Descendants<T>(child)) yield return nested; }
    }
    private void CapturePosition(bool preserve)
    {
        _layoutGeneration++; _anchorKey = null; _focusKey = null; _focusCommand = null;
        if (!preserve) { _resetPosition = true; return; }
        var viewer = Descendants<ScrollViewer>(JobList).FirstOrDefault();
        if (viewer is null || _resetPosition) return;
        foreach (var job in Vm.VisibleJobs)
        {
            if (JobList.ItemContainerGenerator.ContainerFromItem(job) is not FrameworkElement container) continue;
            double y = container.TranslatePoint(new(0, 0), viewer).Y;
            if (y + container.ActualHeight > 0 && y < viewer.ActualHeight) { _anchorKey = job.Job.Key; _anchorY = y; break; }
        }
        if (Keyboard.FocusedElement is Button focused && IsInside(focused, JobList) && focused.DataContext is JobView card) { _focusKey = card.Job.Key; _focusCommand = focused.Command; }
    }
    private void RestorePosition(bool preserve)
    {
        int generation = _layoutGeneration; string? key = _anchorKey, focusKey = _focusKey; double y = _anchorY; var command = _focusCommand;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            if (generation != _layoutGeneration || !IsLoaded) return;
            var viewer = Descendants<ScrollViewer>(JobList).FirstOrDefault(); if (viewer is null) return;
            if (_resetPosition) { viewer.ScrollToVerticalOffset(0); _resetPosition = false; }
            else if (key is not null && Vm.VisibleJobs.FirstOrDefault(j => j.Job.Key == key) is JobView anchor)
            {
                JobList.ScrollIntoView(anchor); JobList.UpdateLayout();
                if (JobList.ItemContainerGenerator.ContainerFromItem(anchor) is FrameworkElement container) viewer.ScrollToVerticalOffset(viewer.VerticalOffset + container.TranslatePoint(new(0, 0), viewer).Y - y);
            }
            if (focusKey is not null && Vm.VisibleJobs.FirstOrDefault(j => j.Job.Key == focusKey) is JobView focus && JobList.ItemContainerGenerator.ContainerFromItem(focus) is DependencyObject element)
                Descendants<Button>(element).FirstOrDefault(b => ReferenceEquals(b.Command, command))?.Focus();
        }));
    }
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var icon = Application.GetResourceStream(new Uri("Assets/jobfinder.ico", UriKind.Relative));
        if (icon is not null) { using var stream = icon.Stream; var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(stream, System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad); BrandLogo.Source = decoder.Frames.OrderByDescending(f => f.PixelWidth).First(); }
        Adjust(); if (Vm.SearchExpanded) KeywordBox.Focus(); else EditSearchButton.Focus(); QueueAutomaticLoad();
    }
    private void OnSizeChanged(object sender, SizeChangedEventArgs e) { if (KeywordPanel is not null) { Adjust(); QueueAutomaticLoad(); } }
    private void Adjust()
    {
        Vm.IsCompact = ActualWidth < 1080;
        bool shortWindow = ActualHeight < 680;
        Vm.ShortViewport = shortWindow;
        LayoutRoot.Margin = shortWindow ? new(24, 8, 24, 8) : Vm.IsCompact ? new(24, 20, 24, 20) : new(36, 28, 36, 24);
        HeaderBand.Margin = new(0, 0, 0, shortWindow ? 6 : Vm.IsCompact ? 16 : 26);
        BrandLogo.Width = BrandLogo.Height = shortWindow ? 34 : 43; BrandTitle.FontSize = shortWindow ? 22 : 25; BrandTagline.Visibility = shortWindow ? Visibility.Collapsed : Visibility.Visible;
        ThemeButton.Padding = SettingsButton.Padding = new(shortWindow ? 7 : 9);
        SearchSurface.Padding = shortWindow ? new(16, 10, 16, 8) : Vm.IsCompact ? new(18, 16, 18, 13) : new(22, 20, 22, 17);
        ResultBand.Margin = shortWindow ? new(0, 8, 0, 6) : Vm.SearchCollapsed ? new(0, 14, 0, 14) : Vm.IsCompact ? new(0, 16, 0, 12) : new(0, 22, 0, 17);
        CoverageBand.Margin = new(4, shortWindow ? 5 : 11, 0, 0); FooterBand.Margin = new(0, shortWindow ? 4 : 12, 0, 0);
        KeywordPanel.Margin = CityPanel.Margin = new(0, 0, 14, shortWindow ? 10 : 14);
        KeywordBox.MinHeight = CityBox.MinHeight = SearchAction.MinHeight = shortWindow ? 43 : 49;
        KeywordBox.Padding = CityBox.Padding = shortWindow ? new(40, 9, 34, 9) : new(40, 12, 34, 12);
        SearchAction.Margin = new(0, shortWindow ? 24 : 26, 0, shortWindow ? 10 : 14);
        foreach (var radio in Descendants<RadioButton>(TypeBand)) radio.Padding = shortWindow ? new(12, 8, 12, 8) : new(15, 10, 15, 10);
        foreach (var radio in Descendants<RadioButton>(PageBand)) radio.Padding = shortWindow ? new(12, 8, 12, 8) : new(15, 11, 15, 11);
        AdjustDetailLayout();
        var available = Math.Min(LayoutRoot.MaxWidth, ActualWidth - LayoutRoot.Margin.Left - LayoutRoot.Margin.Right - 2) - SearchSurface.Padding.Left - SearchSurface.Padding.Right - 2;
        SearchAction.Width = Vm.IsCompact ? 130 : 158;
        var fields = Math.Max(350, available - SearchAction.Width - 28);
        KeywordPanel.Width = Math.Floor(fields * 0.6);
        CityPanel.Width = Math.Floor(fields * 0.4);
        AdjustDetailLayout();
    }
    private void AdjustDetailLayout()
    {
        if (DetailPane is null) return;
        _sideBySide = ActualWidth >= 1200;
        LayoutRoot.MaxWidth = Vm.HasDetail && _sideBySide ? 1800 : 1400;
        double available = Math.Min(LayoutRoot.MaxWidth, Math.Max(0, ActualWidth - LayoutRoot.Margin.Left - LayoutRoot.Margin.Right));
        DetailsGap.Width = new(Vm.HasDetail && _sideBySide ? 22 : 0);
        DetailsColumn.Width = new(Vm.HasDetail && _sideBySide ? Math.Clamp(available * 0.37, 430, 530) : 0);
        Grid.SetColumn(DetailPane, Vm.HasDetail && _sideBySide ? 2 : 0);
        Grid.SetColumnSpan(DetailPane, _sideBySide ? 1 : 3);
        ListArea.Visibility = Vm.HasDetail && !_sideBySide ? Visibility.Hidden : Visibility.Visible;
        DetailPane.SetOverlay(!_sideBySide);
    }
    private void OnCardMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not JobView job) return;
        for (var parent = e.OriginalSource as DependencyObject; parent is not null && parent != element; parent = parent is Visual ? VisualTreeHelper.GetParent(parent) : LogicalTreeHelper.GetParent(parent)) if (parent is Button) return;
        Vm.SelectCommand.Execute(job);
    }
    protected override void OnPreviewTouchDown(TouchEventArgs e) { Vm.TouchActions = true; base.OnPreviewTouchDown(e); }
    protected override void OnPreviewMouseMove(MouseEventArgs e) { if (e.StylusDevice is null && Vm.TouchActions) Vm.TouchActions = false; base.OnPreviewMouseMove(e); }
    private void OnClearProfession(object sender, RoutedEventArgs e) { Vm.Keyword = ""; KeywordBox.Focus(); }
    private void OnClearCity(object sender, RoutedEventArgs e) { Vm.City = ""; CityBox.Focus(); }
    private static bool IsInside(DependencyObject? child, DependencyObject parent)
    {
        while (child is not null) { if (child == parent) return true; child = child is Visual || child is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(child) : LogicalTreeHelper.GetParent(child); }
        return false;
    }
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.D1 && Keyboard.Modifiers == ModifierKeys.Control) { Vm.ShowFavorites = false; e.Handled = true; }
        else if (e.Key == Key.D2 && Keyboard.Modifiers == ModifierKeys.Control) { Vm.ShowFavorites = true; e.Handled = true; }
        else if (e.Key == Key.Escape && Vm.HasDetail) { Vm.CloseDetails(); e.Handled = true; }
        else if (e.Key == Key.Escape && Vm.IsBusy) { Vm.Cancel(); e.Handled = true; }
        else if (e.Key == Key.D && Keyboard.Modifiers == ModifierKeys.Control && Keyboard.FocusedElement is not TextBox) { var job = (Keyboard.FocusedElement as FrameworkElement)?.DataContext as JobView ?? Vm.SelectedJob; if (job is not null) { Vm.ToggleFavorite(job); e.Handled = true; } }
        else if (e.Key == Key.Enter && Keyboard.FocusedElement is TextBox && IsInside(Keyboard.FocusedElement as DependencyObject, SearchForm)) { _ = Vm.SearchAsync(); e.Handled = true; }
        else if (e.Key == Key.Enter && (Keyboard.FocusedElement is ListBoxItem || Keyboard.FocusedElement == JobList) && Vm.SelectedJob is not null) { Vm.SelectCommand.Execute(Vm.SelectedJob); DetailPane.Scroll.Focus(); e.Handled = true; }
    }
    private void OnEditSearchExecuted(object sender, ExecutedRoutedEventArgs e) { OpenSearchEditor(); e.Handled = true; }
    private void OpenSearchEditor()
    {
        Vm.EditSearchCommand.Execute(null);
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() => { if (!_closed && Vm.SearchExpanded) { KeywordBox.Focus(); KeywordBox.SelectAll(); } }));
    }
    private void OnAbout(object sender, RoutedEventArgs e) => CreateAboutDialog().ShowDialog();
    private ThemedWindow NewDialog(string title, double width, double height)
    {
        var dialog = new ThemedWindow { IsDialog = true, Title = title, Owner = this, DataContext = Vm, Width = width, Height = height, MaxHeight = SystemParameters.WorkArea.Height - 30, MaxWidth = SystemParameters.WorkArea.Width - 30, WindowStartupLocation = WindowStartupLocation.CenterOwner, FontFamily = FontFamily, FontSize = 14, ResizeMode = ResizeMode.CanResize, MinWidth = 360, MinHeight = 250, ShowInTaskbar = false };
        dialog.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { dialog.Close(); e.Handled = true; } }; return dialog;
    }
    private void OnSaveSearch(object sender, RoutedEventArgs e)
    {
        var dialog = NewDialog(Vm.Ui["SaveQuery"], 480, 260);
        var panel = new StackPanel { Margin = new(24) };
        panel.Children.Add(new TextBlock { Text = Vm.Ui["SaveName"], Margin = new(0, 0, 0, 7) });
        var name = new TextBox { Text = string.Join(" · ", new[] { Vm.Keyword, Vm.City, Vm.Ui[Vm.Kind.ToString()] }.Where(s => s.Length > 0)), MaxLength = 100 };
        panel.Children.Add(name);
        var save = new Button { Content = Vm.Ui["Save"], Style = (Style)FindResource("Primary"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 18, 0, 0), MinWidth = 110, IsDefault = true };
        save.Click += (_, _) => { Vm.SaveSearch(name.Text); dialog.Close(); }; panel.Children.Add(save);
        dialog.Content = panel; dialog.Loaded += (_, _) => { name.Focus(); name.SelectAll(); }; dialog.ShowDialog();
    }
    private void OnAddLink(object sender, RoutedEventArgs e) => CreateAddLinkDialog().ShowDialog();
    internal ThemedWindow CreateAddLinkDialog()
    {
        var dialog = NewDialog(Vm.Ui["AddLink"], 560, 600);
        var panel = new StackPanel { Margin = new(24) };
        panel.Children.Add(ThemeManager.Color(new TextBlock { Text = Vm.Ui["ManualHint"], TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12) }, TextBlock.ForegroundProperty, "TextMuted"));
        TextBox Field(string key) { panel.Children.Add(new TextBlock { Text = Vm.Ui[key], Margin = new(0, 10, 0, 5) }); var field = new TextBox { MaxLength = key == "Url" ? 2048 : 160 }; panel.Children.Add(field); return field; }
        var title = Field("Title"); var company = Field("Company"); var location = Field("Location"); var url = Field("Url");
        var choices = new ComboBox { ItemsSource = new[] { Vm.Ui["Job"], Vm.Ui["Internship"], Vm.Ui["Apprenticeship"] }, SelectedIndex = Math.Max(0, (int)Vm.Kind - 1), Margin = new(0, 12, 0, 0) }; panel.Children.Add(choices);
        var error = ThemeManager.Color(new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0) }, TextBlock.ForegroundProperty, "Error"); panel.Children.Add(error);
        var save = new Button { Content = Vm.Ui["Bookmark"], Style = (Style)FindResource("Primary"), Margin = new(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true };
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(title.Text) || !Links.IsSafe(url.Text.Trim())) { error.Text = Vm.Ui["InvalidLink"]; return; }
            var original = Links.Canonical(url.Text.Trim());
            Vm.AddFavorite(new() { Id = Guid.NewGuid().ToString("N"), Title = Text.Clean(title.Text), Company = Text.Clean(company.Text), Location = Text.Clean(location.Text), Url = original, Source = new Uri(original).Host, Kind = (JobKind)(choices.SelectedIndex + 1) });
            dialog.Close();
        };
        panel.Children.Add(save); dialog.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; dialog.Loaded += (_, _) => title.Focus(); return dialog;
    }
    private void OnSources(object sender, RoutedEventArgs e) => CreateSourcesDialog().ShowDialog();
    internal ThemedWindow CreateSourcesDialog()
    {
        var dialog = NewDialog(Vm.Ui["Filters"], 760, 800);
        dialog.SetBinding(Window.TitleProperty, new System.Windows.Data.Binding("[Filters]") { Source = Vm.Ui });
        dialog.Content = new SourceDirectory(Vm, (DataTemplate)FindResource("SourceTemplate"), (DataTemplate)FindResource("RadiusItem"));
        return dialog;
    }
    private void OnAppMenu(object sender, RoutedEventArgs e)
    {
        OpenMenu((Button)sender, CreateAppMenu());
    }
    internal ContextMenu CreateAppMenu()
    {
        var menu = new ContextMenu(); var filters = new MenuItem { Header = Vm.Ui["Filters"] }; filters.Click += OnSources; menu.Items.Add(filters);
        var searches = new MenuItem { Header = Vm.Ui["SavedQueries"] }; searches.Click += OnSavedQueries; menu.Items.Add(searches);
        var about = new MenuItem { Header = Vm.Ui["About"] }; about.Click += OnAbout; menu.Items.Add(about);
        var add = new MenuItem { Header = Vm.Ui["AddLink"] }; add.Click += OnAddLink; menu.Items.Add(add);
        return menu;
    }
    private void OnResultMenu(object sender, RoutedEventArgs e)
    {
        OpenMenu((Button)sender, CreateResultMenu());
    }
    internal ContextMenu CreateResultMenu()
    {
        var menu = new ContextMenu(); var save = new MenuItem { Header = Vm.Ui["SaveQuery"] }; save.Click += OnSaveSearch; menu.Items.Add(save);
        var searches = new MenuItem { Header = Vm.Ui["SavedQueries"] }; searches.Click += OnSavedQueries; menu.Items.Add(searches); menu.Items.Add(new Separator());
        for (int i = 0; i < 3; i++) { int index = i; var sort = new MenuItem { Header = Vm.Ui[new[] { "Relevant", "ByTitle", "ByDate" }[i]], IsCheckable = true, IsChecked = Vm.Sort == i }; sort.Click += (_, _) => Vm.Sort = index; menu.Items.Add(sort); }
        return menu;
    }
    private void OnCardMenu(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).DataContext is not JobView job) return;
        var menu = new ContextMenu();
        var copy = new MenuItem { Header = Vm.Ui["CopyLink"], Command = Vm.CopyCommand, CommandParameter = job }; menu.Items.Add(copy);
        var details = new MenuItem { Header = Vm.Ui["Details"], Command = Vm.SelectCommand, CommandParameter = job }; menu.Items.Add(details);
        foreach (var link in job.Job.OtherLinks) { var item = new MenuItem { Header = link.Source + " ↗" }; item.Click += (_, _) => Vm.Open(link.Url); menu.Items.Add(item); }
        OpenMenu((Button)sender, menu);
    }
    private static void OpenMenu(Button button, ContextMenu menu) { menu.PlacementTarget = button; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; menu.IsOpen = true; }
    private void OnSavedQueries(object sender, RoutedEventArgs e)
    {
        var dialog = NewDialog(Vm.Ui["SavedQueries"], 550, 510); var panel = new DockPanel { Margin = new(24) };
        var save = new Button { Content = Vm.Ui["SaveQuery"], Margin = new(0, 15, 0, 0), Style = (Style)FindResource("Primary") }; DockPanel.SetDock(save, Dock.Bottom); panel.Children.Add(save); save.Click += (_, _) => OnSaveSearch(sender, e);
        var list = new StackPanel();
        if (Vm.SavedSearches.Count == 0) list.Children.Add(new TextBlock { Text = Vm.Ui["NoSavedQueries"], TextWrapping = TextWrapping.Wrap });
        void Fill()
        {
            list.Children.Clear();
            if (Vm.SavedSearches.Count == 0) list.Children.Add(new TextBlock { Text = Vm.Ui["NoSavedQueries"], TextWrapping = TextWrapping.Wrap });
            foreach (var search in Vm.SavedSearches.ToArray())
            {
                var row = new DockPanel { Margin = new(0, 0, 0, 8) }; var remove = new Button { Content = new FluentIcon { Kind = "dismiss", Width = 17, Height = 17 }, ToolTip = Vm.Ui["DeleteQuery"], Style = (Style)FindResource("Quiet") }; System.Windows.Automation.AutomationProperties.SetName(remove, Vm.Ui["DeleteQuery"]); DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove);
                remove.Click += (_, _) => { Vm.SavedSearches.Remove(search); Vm.Profile.Searches = Vm.SavedSearches.ToList(); Vm.Save(); Fill(); };
                var choose = new Button { Content = new TextBlock { Text = search.Name, TextWrapping = TextWrapping.Wrap }, ToolTip = search.Query.Label, HorizontalContentAlignment = HorizontalAlignment.Left }; choose.Click += (_, _) => { Vm.ApplyQuery(search.Query); _ = Vm.SearchAsync(); dialog.Close(); }; row.Children.Add(choose); list.Children.Add(row);
            }
        }
        Fill(); save.Click += (_, _) => Fill(); panel.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); dialog.Content = panel; dialog.ShowDialog();
    }
}
