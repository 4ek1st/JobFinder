using JobFinder.Core;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace JobFinder;

public partial class App
{
    private static IEnumerable<T> VisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) yield return typed;
            foreach (var nested in VisualChildren<T>(child)) yield return nested;
        }
    }
    private static async Task SettleAsync() { await Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); }
    private static async Task UntilAsync(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!ready()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException("UI verification condition"); await SettleAsync(); await Task.Delay(10); }
        await SettleAsync();
    }
    private static async Task VerifyAppearanceAsync(MainWindow window, string output, Action<bool, string> check)
    {
        var vm = window.Vm;
        window.FeedScroll!.ScrollToVerticalOffset(180); await SettleAsync();
        double before = window.FeedScroll.VerticalOffset; int count = vm.VisibleJobs.Count;
        vm.SetTheme("light"); await SettleAsync();
        check(!vm.IsDark && vm.VisibleJobs.Count == count && Math.Abs(window.FeedScroll.VerticalOffset - before) < 2, "Theme switch preserves results and scroll position");
        check(new ProfileStore(vm.Store.DirectoryPath).Load().Theme == "light", "Theme preference survives profile reload");
        window.FeedScroll.ScrollToVerticalOffset(0);
        await RenderAsync(window, Path.Combine(output, "JobFinder-Light.png"), 1320, 930);
        File.WriteAllText(Path.Combine(output, "icon-render-diagnostics.txt"), string.Join("\n", VisualChildren<FluentIcon>(window).Where(icon => icon.Kind == "search").Select(icon => $"{icon.Kind} size={icon.ActualWidth}x{icon.ActualHeight} pos={icon.TranslatePoint(new(0, 0), window)} foreground={icon.Foreground} drawing={VisualTreeHelper.GetDrawing(icon)?.Bounds}")));
        var dialog = window.CreateSourcesDialog();
        dialog.WindowStartupLocation = WindowStartupLocation.Manual; dialog.Left = -15000; dialog.Top = -15000; dialog.ShowActivated = false; dialog.Show();
        await SettleAsync();
        var combo = VisualChildren<ComboBox>(dialog).Single(); combo.ApplyTemplate();
        var popup = (Popup)combo.Template.FindName("PART_Popup", combo);
        // Render the actual popup child without activating a native popup on the user's desktop.
        var drop = (FrameworkElement)popup.Child;
        drop.Measure(new Size(combo.ActualWidth, 280)); drop.Arrange(new Rect(new(0, 0), drop.DesiredSize)); drop.UpdateLayout();
        check(VisualChildren<ComboBoxItem>(drop).Count() == vm.Radii.Length, "Custom dropdown creates all selectable radius entries");
        bool UsesTemplate(Control control, Type type)
        {
            var style = (Style)Current.FindResource(type);
            return ReferenceEquals(control.Template, style.Setters.OfType<Setter>().Single(setter => setter.Property == Control.TemplateProperty).Value);
        }
        check(VisualChildren<ComboBoxItem>(drop).All(item => UsesTemplate(item, typeof(ComboBoxItem))), "Dropdown items use the application template rather than a system template");
        var origin = combo.TranslatePoint(new(0, combo.ActualHeight), dialog);
        await RenderAsync(dialog, Path.Combine(output, "JobFinder-filters-Light.png"), 500, 730, overlay: drop, overlayOrigin: origin);
        vm.SetTheme("dark"); await SettleAsync();
        var minimize = (Button)window.Template.FindName("Minimize", window);
        check(minimize.IsVisible && minimize.Command!.CanExecute(null), "Main window exposes an enabled themed minimize button");
        ((System.Windows.Input.RoutedCommand)minimize.Command!).Execute(null, minimize); await SettleAsync();
        check(window.WindowState == WindowState.Minimized, "Themed minimize button changes the actual window state");
        SystemCommands.RestoreWindow(window); await SettleAsync();
        check(window.WindowState == WindowState.Normal && vm.VisibleJobs.Count == count, "Restoring the window keeps the feed intact");
        drop.UpdateLayout();
        check(ReferenceEquals(combo.Foreground, ThemeManager.Brush("TextPrimary")) && ReferenceEquals(dialog.Background, ThemeManager.Brush("AppBackground")), "Open dialog and dropdown follow theme changes");
        await RenderAsync(dialog, Path.Combine(output, "JobFinder-filters-Dark.png"), 500, 730, overlay: drop, overlayOrigin: origin);
        check(VisualChildren<CheckBox>(dialog).All(box => box.Template is not null && VisualChildren<FluentIcon>(box).Any()), "Source checkboxes have custom glyphs and templates");
        dialog.Close();
        await RenderAsync(window, Path.Combine(output, "JobFinder-Dark.png"), 1320, 930);
        check(VisualChildren<ScrollBar>(window).Where(bar => bar.IsVisible).All(bar => VisualChildren<Thumb>(bar).Any()), "Feed scrollbar retains a themed draggable thumb");
        var menu = window.CreateResultMenu();
        menu.Measure(new Size(330, double.PositiveInfinity)); menu.Arrange(new Rect(new(0, 0), menu.DesiredSize)); menu.UpdateLayout();
        check(menu.Items.OfType<MenuItem>().All(item => UsesTemplate(item, typeof(MenuItem))), "Sorting and context menus use the application item template");
        check(menu.Items.OfType<Separator>().All(item => UsesTemplate(item, typeof(Separator))), "Menu separators use the application template");
        await RenderAsync(window, Path.Combine(output, "JobFinder-menu-Dark.png"), 1320, 930, overlay: menu, overlayOrigin: new(970, 385));
        var sort = menu.Items.OfType<MenuItem>().Single(item => (string)item.Header == vm.Ui["ByTitle"]);
        sort.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        check(vm.Sort == 1, "Custom sorting menu retains its action");
        vm.Sort = 0;
        var add = window.CreateAddLinkDialog();
        add.WindowStartupLocation = WindowStartupLocation.Manual; add.Left = -15000; add.Top = -15000; add.ShowActivated = false; add.Show(); await SettleAsync();
        await RenderAsync(add, Path.Combine(output, "JobFinder-add-link-Dark.png"), 560, 600);
        check(VisualChildren<TextBox>(add).All(field => ReferenceEquals(field.Background, ThemeManager.Brush("FieldBackground"))), "Manual favorite fields share the dark theme");
        check(VisualChildren<TextBox>(add).All(field => field.ContextMenu?.Items.OfType<MenuItem>().Count() == 4), "Every text field receives the application editing menu");
        var choice = VisualChildren<ComboBox>(add).Single(); int oldIndex = choice.SelectedIndex; choice.SelectedIndex = 0;
        choice.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(choice), 0, System.Windows.Input.Key.Down) { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
        check(choice.SelectedIndex == 1, "Themed dropdown preserves WPF arrow-key selection handlers");
        choice.SelectedIndex = oldIndex;
        add.Close();
        foreach (var theme in new[] { "dark", "light" })
        {
            vm.SetTheme(theme);
            double Luminance(string key)
            {
                var color = ((SolidColorBrush)ThemeManager.Brush(key)).Color;
                double Channel(byte v) { double c = v / 255d; return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
                return .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
            }
            double Contrast(string a, string b) { double x = Luminance(a), y = Luminance(b); return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05); }
            check(new[] { "TextPrimary", "TextBody", "TextMuted" }.All(key => Contrast(key, "Surface") >= 4.5), $"{theme} theme text contrast at least 4.5:1");
            check(Contrast("OnAccent", "AccentFill") >= 4.5 && Contrast("Accent", "FieldBackground") >= 3, $"{theme} theme primary action and focus contrast");
        }
        vm.SetTheme("dark"); await SettleAsync();
    }
    private static MainWindow TestWindow(ViewModel vm)
    {
        var window = new MainWindow(vm) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -15000, Top = -15000, ShowActivated = false, ShowInTaskbar = false };
        window.Show(); return window;
    }
    private static async Task VerifyPaginationAsync(string output, string isolated, IJobSource captured, Action<bool, string> check)
    {
        var page = await captured.SearchAsync(new("Fachinformatiker", "Berlin", 25, JobKind.Apprenticeship), null, CancellationToken.None);
        var realJobs = page.Jobs.ToArray();
        var source = new PagingFixture(realJobs, "scroll");
        var vm = new ViewModel(Path.Combine(isolated, "auto-scroll"), testSources: [source]);
        var window = TestWindow(vm);
        await vm.SearchAsync(); await SettleAsync();
        check(source.Calls == 1 && vm.HasNext, "Initial page does not drain a feed already taller than the viewport");
        string previousKeyword = vm.Keyword; vm.Keyword = "temporary edited query";
        check(!vm.HasNext && vm.LoadingExplanation == vm.Ui["Changed"] && !vm.AutoPaused, "Edited query explains why old pages are not loaded without latching a pause");
        vm.Keyword = previousKeyword;
        check(vm.HasNext && !vm.AutoPaused && vm.LoadingExplanation.Length == 0, "Reverting the form to its active query automatically restores pagination");
        vm.Sources[0].Enabled = false; vm.Sources[0].Enabled = true;
        check(vm.HasNext && vm.LoadingExplanation.Length == 0, "Reverting source selection restores pagination without an unexplained pause");
        window.FeedScroll!.ScrollToBottom(); await SettleAsync();
        double position = window.FeedScroll.VerticalOffset;
        string Snapshot() => string.Join("; ", vm.VisibleJobs.Select(j => (job: j, element: window.JobList.ItemContainerGenerator.ContainerFromItem(j) as FrameworkElement)).Where(p => p.element is not null).Select(p => $"{p.job.Job.Id}: y={p.element!.TranslatePoint(new(0, 0), window.FeedScroll).Y:F2} h={p.element.ActualHeight:F2}"));
        string oldSnapshot = Snapshot();
        (string key, double y) Anchor()
        {
            foreach (var job in vm.VisibleJobs)
                if (window.JobList.ItemContainerGenerator.ContainerFromItem(job) is FrameworkElement element)
                { double y = element.TranslatePoint(new(0, 0), window.FeedScroll).Y; if (y + element.ActualHeight > 0) return (job.Job.Key, y); }
            return ("", 0);
        }
        var anchorBefore = Anchor();
        await UntilAsync(() => source.Calls >= 2 && !vm.IsBusy);
        check(source.Calls == 2 && vm.VisibleJobs.Count == 9 && source.Peak == 1, "Scrolling near the end automatically loads exactly one batch");
        File.WriteAllText(Path.Combine(output, "auto-scroll-diagnostics.txt"), $"Before offset={position:F2}; after={window.FeedScroll.VerticalOffset:F2}; before={oldSnapshot}; after={Snapshot()}");
        var anchorAfter = Anchor();
        check(anchorBefore.key == anchorAfter.key && Math.Abs(anchorAfter.y - anchorBefore.y) < 2, "Automatic append preserves the visible card and its pixel position");
        window.FeedScroll.ScrollToBottom(); await UntilAsync(() => source.Calls >= 4 && !vm.IsBusy);
        check(source.Calls == 4 && vm.VisibleJobs.Count == 12, "An empty page at the end automatically advances to the next page");
        window.FeedScroll.ScrollToBottom(); await UntilAsync(() => !vm.IsBusy && !vm.HasNext);
        check(source.Calls == 5 && vm.VisibleJobs.Select(j => j.Job.Key).Distinct().Count() == 15, "Automatic pagination reaches the end without duplicate cards");
        await RenderAsync(window, Path.Combine(output, "JobFinder-auto-end.png"), 1320, 930);
        window.Close();
        var gap = new PagingFixture(realJobs, "gap");
        var fill = new ViewModel(Path.Combine(isolated, "auto-fill"), testSources: [gap]);
        var fillWindow = TestWindow(fill); await fill.SearchAsync(); await UntilAsync(() => gap.Calls >= 3 && !fill.IsBusy);
        check(gap.Calls == 3 && fill.VisibleJobs.Count == 9, "Empty first page and short viewport fill automatically");
        fillWindow.Close();
        var noMatches = new PagingFixture(realJobs, "empty");
        var empty = new ViewModel(Path.Combine(isolated, "auto-empty"), testSources: [noMatches]);
        var emptyWindow = TestWindow(empty); await empty.SearchAsync(); await UntilAsync(() => empty.Sources[0].ScanLimited && !empty.IsBusy);
        check(noMatches.Calls == 12 && !empty.AutoPaused && !empty.CanResume && empty.Sources[0].Status == empty.Ui["SourceScanLimit"] && empty.RetryCommand.CanExecute(empty.Sources[0]), "Twelve empty pages stop only their source and explain how to continue it");
        empty.Keyword = "changed"; emptyWindow.QueueAutomaticLoad(); await SettleAsync();
        check(!empty.HasNext && !empty.CanResume && noMatches.Calls == 12, "Edited query prevents old automatic requests");
        await RenderAsync(emptyWindow, Path.Combine(output, "JobFinder-no-matches.png"), 1320, 930);
        emptyWindow.Close();
        var limitedSource = new PagingFixture(realJobs, "empty", "arbeitnow"); var peerSource = new PagingFixture(realJobs, "peer");
        var peers = new ViewModel(Path.Combine(isolated, "auto-independent"), testSources: [limitedSource, peerSource]);
        await peers.SearchAsync(); for (int i = 0; i < 12; i++) await peers.SearchAsync(more: true);
        check(limitedSource.Calls == 12 && peerSource.Calls == 13 && peers.Sources[0].ScanLimited && peers.HasNext && !peers.AutoPaused, "A source without matches cannot stop another source with new pages");
        peers.Dispose();
        var errorSource = new PagingFixture(realJobs, "error");
        var error = new ViewModel(Path.Combine(isolated, "auto-error"), testSources: [errorSource]);
        var errorWindow = TestWindow(error); await error.SearchAsync(); await UntilAsync(() => errorSource.Calls >= 2 && !error.IsBusy);
        errorWindow.QueueAutomaticLoad(); await Task.Delay(90); await SettleAsync();
        check(errorSource.Calls == 2 && error.Sources[0].HasError && error.VisibleJobs.Count == 1 && !error.HasNext, "Failure during automatic pagination does not retry or discard cards");
        await RenderAsync(errorWindow, Path.Combine(output, "JobFinder-auto-error.png"), 1320, 930);
        errorWindow.Close();
        var slow = new PagingFixture(realJobs, "cancel");
        var cancel = new ViewModel(Path.Combine(isolated, "auto-cancel"), testSources: [slow]);
        var cancelWindow = TestWindow(cancel); await cancel.SearchAsync(); await UntilAsync(() => slow.Calls == 2 && cancel.IsBusy);
        cancel.Cancel(); await UntilAsync(() => !cancel.IsBusy);
        cancelWindow.QueueAutomaticLoad(); await Task.Delay(90); await SettleAsync();
        check(slow.Calls == 2 && cancel.AutoPaused && cancel.CanResume && cancel.VisibleJobs.Count == 1, "Cancel stops future automatic requests while retaining cards");
        cancel.ResumeCommand.Execute(null); await UntilAsync(() => slow.Calls == 3 && !cancel.IsBusy);
        check(!cancel.AutoPaused && !cancel.HasNext && cancel.VisibleJobs.Count == 1, "Explicit resume after cancellation restarts automatic pagination once");
        cancelWindow.Close();
        var rateSource = new PagingFixture(realJobs, "rate");
        var rate = new ViewModel(Path.Combine(isolated, "auto-rate"), testSources: [rateSource]);
        var rateWindow = TestWindow(rate); await rate.SearchAsync(); await UntilAsync(() => rateSource.Calls >= 2 && !rate.IsBusy);
        rateWindow.QueueAutomaticLoad(); await Task.Delay(90); await SettleAsync();
        check(rateSource.Calls == 2 && rate.Sources[0].State == "RateLimit" && !rate.HasNext && !rate.RetryCommand.CanExecute(rate.Sources[0]), "Rate limit stops automatic loading and disables retry until the cooldown ends");
        rateWindow.Close();
        var favoriteSource = new PagingFixture(realJobs, "scroll");
        var favorites = new ViewModel(Path.Combine(isolated, "auto-favorites"), testSources: [favoriteSource]);
        var favoriteWindow = TestWindow(favorites); await favorites.SearchAsync(); await SettleAsync();
        favorites.ShowFavorites = true; favoriteWindow.FeedScroll!.ScrollToBottom(); favoriteWindow.QueueAutomaticLoad(); await Task.Delay(90); await SettleAsync();
        check(favoriteSource.Calls == 1, "Favorite view suspends automatic feed loading");
        favoriteWindow.Close();
        var cycleSource = new PagingFixture(realJobs, "cycle");
        var cycle = new ViewModel(Path.Combine(isolated, "auto-cycle"), testSources: [cycleSource]);
        await cycle.SearchAsync(); await cycle.SearchAsync(more: true); await cycle.SearchAsync(more: true);
        check(cycleSource.Calls == 3 && !cycle.HasNext, "Previously visited pagination cursor stops a source cycle");
        cycle.Dispose();
    }
    // Synthetic timing and cursors test the controller; all displayed cards are captured public source data.
    private sealed class PagingFixture(JobListing[] jobs, string mode, string id = "azubiyo") : IJobSource
    {
        public string Id => id; public string Name => id == "azubiyo" ? "Azubiyo" : "Arbeitnow"; public bool Supports(JobKind kind) => true;
        public int Calls { get; private set; } public int Peak { get; private set; } private int _active;
        public async Task<JobPage> SearchAsync(SearchQuery q, string? next, CancellationToken cancellation)
        {
            Calls++; Peak = Math.Max(Peak, ++_active);
            try
            {
                int n = next is null ? 1 : int.Parse(next);
                await Task.Delay(mode == "cancel" && n == 2 ? 3000 : 100, cancellation);
                if (mode == "error" && n == 2) throw new SourceFailure(FailureKind.Network, "Controlled source error");
                if (mode == "rate" && n == 2) throw new SourceFailure(FailureKind.RateLimit, "Controlled rate limit", DateTimeOffset.UtcNow.AddMinutes(1));
                if (mode == "empty") return new([], (n + 1).ToString(), 100);
                if (mode == "peer") return new(jobs.Skip(n - 1).Take(1).ToArray(), (n + 1).ToString(), 1);
                if (mode == "gap") return n switch { 1 => new([], "2", 100), 2 => new(jobs.Take(1).ToArray(), "3", 1), _ => new(jobs.Skip(1).Take(8).ToArray(), null, 8) };
                if (mode is "cancel" or "error" or "rate") return new(jobs.Take(1).ToArray(), n == 1 ? "2" : null, 1);
                if (mode == "cycle") return new(jobs.Skip((n - 1) * 3).Take(3).ToArray(), n == 3 ? "2" : (n + 1).ToString(), 3);
                return n switch { 1 => new(jobs.Take(6).ToArray(), "2", 6), 2 => new(jobs.Skip(6).Take(3).ToArray(), "3", 3), 3 => new([], "4", 100), 4 => new(jobs.Skip(9).Take(3).ToArray(), "5", 3), _ => new(jobs.Skip(12).Take(3).ToArray(), null, 3) };
            }
            finally { _active--; }
        }
    }
}
