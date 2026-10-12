using JobFinder.Core;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace JobFinder;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 2 && e.Args[0] == "--apply-update")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { var result = await new UpdateInstaller().ApplyAsync(e.Args[1]); Shutdown(result.Success ? 0 : 2); }
            catch { Shutdown(3); }
            return;
        }
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.TextBox), FrameworkElement.LoadedEvent, new RoutedEventHandler(InstallTextMenu));
        if (e.Args.Length == 2 && e.Args[0] == "--source-catalog")
        {
            var destination = Path.GetFullPath(e.Args[1]); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllText(destination, JsonSerializer.Serialize(new { version = typeof(App).Assembly.GetName().Version?.ToString(), feedCount = SourceCatalog.FeedIds().Count, websiteCount = SourceCatalog.Sources.Count(s => !s.InFeed), sources = SourceCatalog.Sources.Select(s => new { s.Id, s.Name, inFeed = s.InFeed, kinds = s.Kinds.Select(k => k.ToString()).ToArray(), url = s.SearchUrl(new()), coverageRu = s.CoverageRu, coverageDe = s.CoverageDe, externalReasonRu = s.InFeed ? "" : s.ReasonRu, spacingSeconds = s.SpacingSeconds }), generatedAt = DateTimeOffset.UtcNow }, new JsonSerializerOptions { WriteIndented = true }));
            Shutdown(0); return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--make-icon") { IconBuilder.Create(e.Args[1]); Shutdown(0); return; }
        if (e.Args.Length >= 3 && e.Args[0] == "--verify")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { await VerifyAsync(Path.GetFullPath(e.Args[1]), Path.GetFullPath(e.Args[2])); Shutdown(0); }
            catch (Exception ex) { Directory.CreateDirectory(e.Args[1]); File.WriteAllText(Path.Combine(e.Args[1], "ui-error.txt"), ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--verify-github-update")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { await VerifyGitHubUpdateAsync(Path.GetFullPath(e.Args[1])); }
            catch (Exception error)
            {
                var evidence = Path.Combine(AppContext.BaseDirectory, "update-verification"); Directory.CreateDirectory(evidence);
                File.WriteAllText(Path.Combine(evidence, "error.txt"), error.ToString()); Shutdown(1);
            }
            return;
        }
        string data = Path.Combine(AppContext.BaseDirectory, "data");
        var vm = new ViewModel(data); var window = new MainWindow(vm); MainWindow = window; window.Show();
        bool updated = e.Args.Length == 2 && e.Args[0] == "--updated";
        bool restored = e.Args.Length == 2 && e.Args[0] == "--update-failed";
        if (updated)
        {
            try
            {
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (!window.IsLoaded || !window.IsVisible) throw new InvalidOperationException("The new application window did not load.");
                UpdateInstaller.AcknowledgeStartup(e.Args[1], typeof(App).Assembly.GetName().Version!, Environment.ProcessPath!);
            }
            catch { Shutdown(2); return; }
        }
        _ = vm.Updates.StartAsync();
        await vm.SearchAsync();
        if (updated || restored) await window.RestoreAfterUpdateAsync(updateSucceeded: updated);
        if (updated) await CompleteGitHubUpdateVerificationAsync(window, e.Args[1]);
    }
    private static void InstallTextMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox field) return;
        var ui = field.DataContext is SourceDirectoryViewModel directory ? directory.Ui : (field.DataContext as ViewModel)?.Ui ?? new Ui();
        var menu = new System.Windows.Controls.ContextMenu();
        void Add(string key, System.Windows.Input.RoutedUICommand command, string shortcut)
        {
            var item = new System.Windows.Controls.MenuItem { Command = command, CommandTarget = field, InputGestureText = shortcut };
            item.SetBinding(System.Windows.Controls.HeaderedItemsControl.HeaderProperty, new System.Windows.Data.Binding($"[{key}]") { Source = ui });
            menu.Items.Add(item);
        }
        Add("Cut", System.Windows.Input.ApplicationCommands.Cut, "Ctrl+X");
        Add("Copy", System.Windows.Input.ApplicationCommands.Copy, "Ctrl+C");
        Add("Paste", System.Windows.Input.ApplicationCommands.Paste, "Ctrl+V");
        menu.Items.Add(new System.Windows.Controls.Separator()); Add("SelectAll", System.Windows.Input.ApplicationCommands.SelectAll, "Ctrl+A");
        field.ContextMenu = menu;
    }
    private static async Task RenderAsync(ThemedWindow window, string file, double width, double height, double scale = 1, FrameworkElement? overlay = null, Point? overlayOrigin = null)
    {
        window.Width = width; window.Height = height;
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        var visual = (FrameworkElement)window;
        if (window is MainWindow main)
        {
            var jobs = (System.Windows.Controls.ListBox)main.FindName("JobList");
            File.AppendAllText(Path.Combine(Path.GetDirectoryName(file)!, "layout-metrics.txt"), $"{Path.GetFileName(file)} window {window.ActualWidth}x{window.ActualHeight}; root {visual.ActualWidth}x{visual.ActualHeight}; list {jobs.ActualWidth}x{jobs.ActualHeight}; first {((FrameworkElement?)jobs.ItemContainerGenerator.ContainerFromIndex(0))?.ActualHeight}\n");
        }
        var scene = new DrawingVisual();
        using (var drawing = scene.RenderOpen())
        {
            drawing.DrawRectangle(window.Background, null, new(0, 0, width, height));
            var offset = visual.TranslatePoint(new(0, 0), window);
            drawing.DrawRectangle(new VisualBrush(visual) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new(offset.X, offset.Y, visual.ActualWidth, visual.ActualHeight));
            if (overlay is not null && overlayOrigin is Point origin) drawing.DrawRectangle(new VisualBrush(overlay) { Stretch = Stretch.None }, null, new(origin, new Size(overlay.ActualWidth, overlay.ActualHeight)));
        }
        var bitmap = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(scene); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(file); encoder.Save(stream);
    }
    private static async Task VerifyAsync(string output, string fixtures)
    {
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
        var fixedSources = new IJobSource[]
        {
            new FixtureSource("azubiyo", "Azubiyo", JobKind.Apprenticeship, () => AzubiyoSource.Parse(File.ReadAllText(Path.Combine(fixtures, "azubiyo-filtered.txt")), new("https://www.azubiyo.de/ausbildung/berlin/?subject=Fachinformatiker&location=Berlin&radius=25"))),
            new FixtureSource("praktikum", "Praktikum.info", JobKind.Internship, () => PraktikumSource.Parse(File.ReadAllText(Path.Combine(fixtures, "praktikum-search.txt")), new("https://www.praktikum.info/stellenangebote?text=Design&location=Berlin"))),
            new FixtureSource("arbeitnow", "Arbeitnow", JobKind.Job, () => ArbeitnowSource.Parse(File.ReadAllText(Path.Combine(fixtures, "arbeitnow.txt")), new("Developer")))
        };
        var isolated = Path.Combine(output, "test-state", Guid.NewGuid().ToString("N"));
        var vm = new ViewModel(Path.Combine(isolated, "profile"), testSources: fixedSources);
        var window = new MainWindow(vm) { AutoLoadEnabled = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -15000, Top = -15000, ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        // Render actual WPF controls with data from captured public live responses. No mock cards.
        await RenderAsync(window, Path.Combine(output, "JobFinder-start.png"), 1320, 930);
        vm.ApplyQuery(new("Fachinformatiker", "Berlin", 25, JobKind.Apprenticeship)); await vm.SearchAsync();
        Check(vm.VisibleJobs.Count > 0 && vm.VisibleJobs.All(j => j.Job.Kind == JobKind.Apprenticeship), "Ausbildung cards from actual source response");
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        await RenderAsync(window, Path.Combine(output, "JobFinder-Ausbildung.png"), 1536, 1024);
        Check(!vm.IsCompact, "Wide cards use side actions");
        await RenderAsync(window, Path.Combine(output, "JobFinder-default.png"), 1320, 930);
        await RenderAsync(window, Path.Combine(output, "JobFinder-scale125.png"), 1228, 819, 1.25);
        await RenderAsync(window, Path.Combine(output, "JobFinder-scale150.png"), 1024, 768, 1.5);
        await RenderAsync(window, Path.Combine(output, "JobFinder-scale200.png"), 820, 780, 2);
        await RenderAsync(window, Path.Combine(output, "JobFinder-small-scale200.png"), 768, 512, 2);
        Check(((FrameworkElement)window.FindName("JobList")).ActualHeight >= ((FrameworkElement)window.JobList.ItemContainerGenerator.ContainerFromIndex(0)).ActualHeight && window.EditSearchButton.IsVisible, "Short 200 percent viewport retains search editing and a complete first card");
        await RenderAsync(window, Path.Combine(output, "JobFinder-compact.png"), 820, 780);
        await RenderAsync(window, Path.Combine(output, "JobFinder-compact-tall.png"), 820, 880);
        Check(vm.IsCompact, "Narrow cards move actions below their text");
        Check(window.ResultSourcesButton.IsVisible, "Compact source dialog remains accessible");
        vm.LanguageCommand.Execute("de");
        await RenderAsync(window, Path.Combine(output, "JobFinder-Deutsch.png"), 1320, 930);
        vm.LanguageCommand.Execute("ru");
        var first = vm.VisibleJobs[0]; vm.BookmarkCommand.Execute(first);
        Check(vm.Profile.Favorites.Any(j => j.Key == first.Job.Key), "Favorite command saves original listing");
        vm.ShowFavorites = true;
        await RenderAsync(window, Path.Combine(output, "JobFinder-favorites.png"), 1320, 930);
        Check(vm.VisibleJobs.Count == vm.Profile.Favorites.Count, "Favorite tab offline contents");
        vm.SaveSearch("IT Berlin");
        var reload = new ProfileStore(vm.Store.DirectoryPath).Load();
        Check(reload.Favorites.Any(j => j.Key == first.Job.Key) && reload.Searches.Any(s => s.Name == "IT Berlin"), "Favorite and saved search survive profile reload");
        vm.ApplyQuery(new("Design", "Berlin", 25, JobKind.Internship)); await vm.SearchAsync();
        Check(vm.VisibleJobs.Any() && vm.VisibleJobs.All(j => j.Job.Kind == JobKind.Internship), "Praktikum cards preserve category from actual source response");
        await RenderAsync(window, Path.Combine(output, "JobFinder-Praktikum.png"), 1320, 930);
        vm.ApplyQuery(new("Developer", "", 25, JobKind.Job)); await vm.SearchAsync();
        Check(vm.VisibleJobs.Any(), "Job cards from actual source response");
        await RenderAsync(window, Path.Combine(output, "JobFinder-work.png"), 1320, 930);
        // Preserve position and containers while changing a card or fetching another page.
        vm.ApplyQuery(new("Fachinformatiker", "Berlin", 25, JobKind.Apprenticeship)); await vm.SearchAsync();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.FeedScroll!.ScrollToVerticalOffset(185);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        double before = window.FeedScroll.VerticalOffset; int resets = 0;
        System.Collections.Specialized.NotifyCollectionChangedEventHandler monitor = (_, change) => { if (change.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) resets++; };
        vm.FeedItems.CollectionChanged += monitor;
        vm.BookmarkCommand.Execute(vm.VisibleJobs[1]);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(resets == 0 && Math.Abs(window.FeedScroll.VerticalOffset - before) < 2, "Bookmark keeps scroll position and existing containers");
        await vm.SearchAsync(more: true);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(resets == 0 && Math.Abs(window.FeedScroll.VerticalOffset - before) < 2, "Load-more keeps scroll position without a list Reset");
        Check(!vm.HasNext && vm.VisibleJobs.Count == 15, "Repeated page stops pagination without duplicate cards");
        vm.FeedItems.CollectionChanged -= monitor;
        vm.Sources[0].State = "Access"; vm.Sources[0].Refresh(); vm.Refresh();
        Check(vm.VisibleJobs.Count > 0 && vm.Sources[0].HasError, "Unavailable source status stays alongside loaded cards");
        window.FeedScroll.ScrollToVerticalOffset(0);
        await RenderAsync(window, Path.Combine(output, "JobFinder-partial-error.png"), 1320, 930);
        vm.Sources[0].State = "Ok"; vm.Sources[0].Refresh(); vm.Refresh();
        await VerifyAppearanceAsync(window, output, Check);
        await VerifyDetailsAsync(output, fixtures, isolated, Check);
        await VerifyCompactSearchAsync(output, fixtures, isolated, Check);
        await VerifySourceDirectoryAsync(output, isolated, Check);
        await VerifyUpdateUiAsync(output, isolated, Check);
        await VerifyPaginationAsync(output, isolated, fixedSources[0], Check);
        // Delayed sources deliberately ignore cancellation to prove generation protection.
        var race = new ViewModel(Path.Combine(isolated, "race-profile"), testSources: [new DelayedSource()]);
        race.ApplyQuery(new("old")); var old = race.SearchAsync(); await Task.Delay(20); race.ApplyQuery(new("new")); var current = race.SearchAsync(); await Task.WhenAll(old, current);
        Check(race.VisibleJobs.Count == 1 && race.VisibleJobs[0].Title == "new", "Old response cannot replace a new search");
        var cancel = new ViewModel(Path.Combine(isolated, "cancel-profile"), testSources: [new CancellableSource()]);
        var pending = cancel.SearchAsync(); await Task.Delay(20); cancel.Cancel(); await pending;
        Check(!cancel.IsBusy && cancel.Sources[0].State == "Cancelled", "Cancel leaves no busy source");
        File.WriteAllText(Path.Combine(output, "ui-verification.json"), JsonSerializer.Serialize(new { passed = checks.Count, checks, rendering = "Actual WPF control tree, public captured responses; no physical mouse/keyboard interaction claimed. 125/150/200 percent are logical viewport and raster-density simulations, not a physical monitor change.", verifiedAt = DateTimeOffset.UtcNow }, new JsonSerializerOptions { WriteIndented = true }));
        window.Close(); race.Dispose(); cancel.Dispose();
    }
    private sealed class FixtureSource(string id, string name, JobKind kind, Func<JobPage> read) : IJobSource
    {
        public string Id => id; public string Name => name;
        public bool Supports(JobKind k) => k == JobKind.All || k == kind;
        public Task<JobPage> SearchAsync(SearchQuery q, string? next, CancellationToken c) { c.ThrowIfCancellationRequested(); var page = read(); return Task.FromResult(page with { Jobs = page.Jobs.Where(j => q.Kind == JobKind.All || q.Kind == j.Kind).ToList() }); }
    }
    private sealed class DelayedSource : IJobSource
    {
        public string Id => "arbeitnow"; public string Name => "Delayed test source"; public bool Supports(JobKind k) => true;
        public async Task<JobPage> SearchAsync(SearchQuery q, string? next, CancellationToken c) { await Task.Delay(q.Keyword == "old" ? 150 : 30); return new([new() { Id = q.Keyword, Source = Name, Title = q.Keyword, Url = "https://www.arbeitnow.com/jobs/" + q.Keyword }], null, 1); }
    }
    private sealed class CancellableSource : IJobSource
    {
        public string Id => "arbeitnow"; public string Name => "Cancel test source"; public bool Supports(JobKind k) => true;
        public async Task<JobPage> SearchAsync(SearchQuery q, string? next, CancellationToken c) { await Task.Delay(3000, c); return new([], null, 0); }
    }
}
