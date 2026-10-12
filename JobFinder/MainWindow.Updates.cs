using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace JobFinder;

public partial class MainWindow
{
    // An explicit marker keeps update actions out of every other themed window, even with the same model.
    public static readonly DependencyProperty HasUpdateCaptionProperty = DependencyProperty.RegisterAttached("HasUpdateCaption", typeof(bool), typeof(MainWindow), new PropertyMetadata(false));
    public static bool GetHasUpdateCaption(DependencyObject window) => (bool)window.GetValue(HasUpdateCaptionProperty);
    public static void SetHasUpdateCaption(DependencyObject window, bool value) => window.SetValue(HasUpdateCaptionProperty, value);
    private void InitializeUpdateUi()
    { SetHasUpdateCaption(this, true); Vm.ConfigureUpdateApply(SaveForUpdate); Vm.Ui.PropertyChanged += OnUiLanguageChanged; }
    private void OnUiLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Ui.IsRu)) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (_closed) return;
            // Auto-sized horizontal content may keep the previous language's layout clip after text grows.
            foreach (var root in new FrameworkElement[] { HeaderBand, CompactSearchBar, SearchSurface, ResultBand })
                foreach (var text in Descendants<TextBlock>(root))
                { text.InvalidateArrange(); if (VisualTreeHelper.GetParent(text) is FrameworkElement parent) parent.InvalidateArrange(); }
            UpdateLayout();
        }));
    }
    public bool SaveForUpdate()
    {
        Rect bounds = WindowState == WindowState.Normal ? new(Left, Top, Width, Height) : RestoreBounds;
        return Vm.SaveForUpdate(new(bounds.Width, bounds.Height, bounds.Left, bounds.Top, WindowState, FeedScroll?.VerticalOffset ?? 0, Vm.HasDetail ? DetailPane.Scroll.VerticalOffset : 0));
    }
    public async Task RestoreAfterUpdateAsync(bool restoreWindow = true, bool updateSucceeded = true)
    {
        var state = Vm.TakeUpdateResume();
        Vm.Updates.NotifyStartupOutcome(updateSucceeded);
        if (state is null) return;
        if (restoreWindow && state.Window is { } window && double.IsFinite(window.Width) && double.IsFinite(window.Height) && double.IsFinite(window.Left) && double.IsFinite(window.Top) && window.Width >= 300 && window.Height >= 250)
        {
            var area = SystemParameters.WorkArea;
            WindowState = WindowState.Normal;
            Width = Math.Clamp(window.Width, MinWidth, Math.Max(MinWidth, area.Width));
            Height = Math.Clamp(window.Height, MinHeight, Math.Max(MinHeight, area.Height));
            Left = Math.Clamp(window.Left, area.Left, Math.Max(area.Left, area.Right - Width));
            Top = Math.Clamp(window.Top, area.Top, Math.Max(area.Top, area.Bottom - Height));
            if (window.State == WindowState.Maximized) WindowState = WindowState.Maximized;
        }
        await Vm.RestoreUpdateStateAsync(state);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        if (state.Window is { } position)
        {
            if (double.IsFinite(position.FeedOffset)) FeedScroll?.ScrollToVerticalOffset(Math.Max(0, position.FeedOffset));
            if (Vm.HasDetail && double.IsFinite(position.DetailOffset)) DetailPane.Scroll.ScrollToVerticalOffset(Math.Max(0, position.DetailOffset));
        }
    }

    internal ThemedWindow CreateAboutDialog()
    {
        var dialog = NewDialog(Vm.Ui["About"], 660, 760);
        dialog.SetBinding(TitleProperty, new Binding("[About]") { Source = Vm.Ui });
        var dock = new DockPanel { Margin = new(23) };
        var close = new Button { HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 15, 0, 0), MinWidth = 110, IsCancel = true };
        close.SetBinding(ContentControl.ContentProperty, new Binding("[Close]") { Source = Vm.Ui });
        close.Click += (_, _) => dialog.Close(); DockPanel.SetDock(close, Dock.Bottom); dock.Children.Add(close);
        var panel = new StackPanel();
        var version = new TextBlock { FontSize = 19, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 12) };
        version.SetBinding(TextBlock.TextProperty, new Binding(nameof(UpdateViewModel.InstalledVersion)) { Source = Vm.Updates }); panel.Children.Add(version);

        var updatePanel = new StackPanel();
        var heading = new TextBlock { FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 7) };
        heading.SetBinding(TextBlock.TextProperty, new Binding("[UpdateHeading]") { Source = Vm.Ui }); updatePanel.Children.Add(heading);
        var status = new TextBlock { Name = "UpdateStatus", TextWrapping = TextWrapping.Wrap, LineHeight = 21 };
        status.SetBinding(TextBlock.TextProperty, new Binding(nameof(UpdateViewModel.Status)) { Source = Vm.Updates });
        AutomationProperties.SetLiveSetting(status, System.Windows.Automation.AutomationLiveSetting.Polite); updatePanel.Children.Add(status);
        var progress = new ProgressBar { Name = "AboutUpdateProgress", Maximum = 100, Height = 4, Margin = new(0, 12, 0, 0) };
        progress.SetBinding(ProgressBar.ValueProperty, new Binding(nameof(UpdateViewModel.ProgressPercent)) { Source = Vm.Updates, Mode = BindingMode.OneWay });
        progress.SetBinding(ProgressBar.IsIndeterminateProperty, new Binding(nameof(UpdateViewModel.IsApplying)) { Source = Vm.Updates });
        progress.SetBinding(VisibilityProperty, new Binding(nameof(UpdateViewModel.ShowProgress)) { Source = Vm.Updates, Converter = (IValueConverter)FindResource("Visible") }); updatePanel.Children.Add(progress);
        var actions = new WrapPanel { Margin = new(0, 12, 0, 0) };
        var check = new Button { Name = "CheckForUpdates", Command = Vm.Updates.CheckCommand, MinWidth = 160, Margin = new(0, 0, 8, 6) };
        check.SetBinding(ContentControl.ContentProperty, new Binding(nameof(UpdateViewModel.CheckLabel)) { Source = Vm.Updates }); actions.Children.Add(check);
        var repository = new Button { Name = "UpdateRepository", Style = (Style)FindResource("Quiet"), Margin = new(0, 0, 0, 6) };
        repository.SetBinding(ContentControl.ContentProperty, new Binding("[UpdateRepository]") { Source = Vm.Ui });
        repository.Click += (_, _) => Vm.Open($"https://github.com/{JobFinder.Core.UpdateProduct.Owner}/{JobFinder.Core.UpdateProduct.Repository}"); actions.Children.Add(repository); updatePanel.Children.Add(actions);
        var schedule = ThemeManager.Color(new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new(0, 2, 0, 0) }, TextBlock.ForegroundProperty, "TextMuted");
        schedule.SetBinding(TextBlock.TextProperty, new Binding("[UpdateSchedule]") { Source = Vm.Ui }); updatePanel.Children.Add(schedule);
        var surface = ThemeManager.Color(new Border { Padding = new(16), CornerRadius = new(9), Margin = new(0, 0, 0, 18), BorderThickness = new(1), Child = updatePanel }, Border.BackgroundProperty, "Surface");
        surface.SetResourceReference(Border.BorderBrushProperty, "Border"); panel.Children.Add(surface);
        var body = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 23 };
        body.SetBinding(TextBlock.TextProperty, new Binding("[AboutBody]") { Source = Vm.Ui }); panel.Children.Add(body);
        dock.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        dialog.Content = dock; return dialog;
    }
}
