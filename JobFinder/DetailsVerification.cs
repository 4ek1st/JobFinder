using JobFinder.Core;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace JobFinder;

public partial class App
{
    private static async Task VerifyDetailsAsync(string output, string fixtures, string isolated, Action<bool, string> check)
    {
        var page = AzubiyoSource.Parse(File.ReadAllText(Path.Combine(fixtures, "azubiyo-filtered.txt")), new("https://www.azubiyo.de/ausbildung/berlin/"));
        var source = new FixtureSource("azubiyo", "Azubiyo", JobKind.Apprenticeship, () => page);
        var loader = new CapturedDetailService(File.ReadAllText(Path.Combine(fixtures, "azubiyo-tu-detail.txt")));
        var logos = new CompanyLogoService(Path.Combine(isolated, "detail-logos"), false);
        logos.Seed(page.Jobs[0].LogoUrl, File.ReadAllBytes(Path.Combine(fixtures, "logo-azubiyo.png")));
        var directory = Path.Combine(isolated, "detail-profile");
        var vm = new ViewModel(directory, testSources: [source], detailService: loader, logos: logos);
        var window = TestWindow(vm); window.AutoLoadEnabled = false;
        vm.ApplyQuery(new("Fachinformatiker", "Berlin", 25, JobKind.Apprenticeship));
        await vm.SearchAsync(); await SettleAsync();
        check(loader.Calls == 0, "Details are fetched only when a posting is selected");
        var first = vm.VisibleJobs[0];
        var container = (ListBoxItem)window.JobList.ItemContainerGenerator.ContainerFromItem(first);
        var actionPanel = VisualChildren<StackPanel>(container).Single(x => x.Name == "ActionButtons");
        check(actionPanel.Visibility == Visibility.Hidden, "Card actions start hidden with their layout space reserved");
        double height = container.ActualHeight;
        vm.TouchActions = true; await SettleAsync();
        check(actionPanel.IsVisible && Math.Abs(container.ActualHeight - height) < 1, "Touch-accessible card actions appear without moving the card content");
        await RenderAsync(window, Path.Combine(output, "JobFinder-03-card-actions.png"), 1320, 930);
        vm.TouchActions = false; await SettleAsync();
        check(VisualChildren<CompanyLogo>(container).Single().HasImage, "A captured company logo is displayed from the real source image bytes");
        window.JobList.SelectedItem = first; await vm.DetailTask; await SettleAsync();
        check(vm.Detail?.Document?.HasDescription == true && vm.Detail.Job == first && loader.Calls == 1, "Selecting a real listing loads its full description into the detail panel");
        check(window.DetailPane.ActualWidth >= 430 && window.ListArea.IsVisible && window.DetailPane.IsVisible, "Wide window shows independent result and detail columns");
        await RenderAsync(window, Path.Combine(output, "JobFinder-03-details-dark.png"), 1536, 1024);
        double feedOffset = window.FeedScroll!.VerticalOffset;
        window.DetailPane.Scroll.ScrollToBottom(); await SettleAsync();
        check(window.DetailPane.Scroll.VerticalOffset > 0 && Math.Abs(window.FeedScroll.VerticalOffset - feedOffset) < 1, "Description scrolling leaves the result list at its previous position");
        await RenderAsync(window, Path.Combine(output, "JobFinder-03-details-contacts.png"), 1536, 1024);
        vm.ToggleFavorite(first);
        check(new ProfileStore(directory).Load().Favorites.Single().Details?.HasDescription == true, "Favorite persists the selected full description for offline viewing");
        vm.CloseDetails(); vm.SelectCommand.Execute(first); await vm.DetailTask;
        check(loader.Calls == 1 && vm.Detail?.Document?.HasDescription == true, "Reopening a selected posting uses its cached description");
        var offlineService = new CapturedDetailService("") { Fails = true };
        var offline = new ViewModel(directory, testSources: [source], detailService: offlineService, logos: new CompanyLogoService(Path.Combine(isolated, "offline-logos"), false));
        offline.ShowFavorites = true; offline.SelectCommand.Execute(offline.VisibleJobs[0]); await offline.DetailTask;
        check(offlineService.Calls == 0 && offline.Detail?.Document?.HasDescription == true, "Reloaded favorite details open without any network request");
        offline.Dispose();
        var savedDocument = vm.Detail!.Document;
        vm.SetTheme("light"); await SettleAsync(); window.DetailPane.Scroll.ScrollToTop();
        await RenderAsync(window, Path.Combine(output, "JobFinder-03-details-light.png"), 1536, 1024);
        check(vm.HasDetail && ReferenceEquals(vm.Detail!.Document, savedDocument) && ReferenceEquals(((Border)window.DetailPane.FindName("DetailSurface")).Background, ThemeManager.Brush("Surface")), "Theme switching retains the selected description and recolors the detail panel");
        vm.SetTheme("dark");
        await RenderAsync(window, Path.Combine(output, "JobFinder-03-details-narrow.png"), 820, 780);
        check(window.ListArea.Visibility == Visibility.Hidden && window.DetailPane.IsVisible && ((Button)window.DetailPane.FindName("BackButton")).IsVisible, "Narrow detail view replaces the list and provides a themed Back action");
        var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        window.RaiseEvent(key); await SettleAsync();
        check(!vm.HasDetail && window.ListArea.IsVisible && !vm.AutoPaused && ReferenceEquals(vm.SelectedJob, first), "Esc closes details without pausing the search or losing the selected card");
        window.FeedScroll.ScrollToVerticalOffset(180); await SettleAsync(); double returnOffset = window.FeedScroll.VerticalOffset;
        vm.SelectCommand.Execute(first); await vm.DetailTask; await SettleAsync();
        vm.CloseDetailsCommand.Execute(null); await SettleAsync();
        check(Math.Abs(window.FeedScroll.VerticalOffset - returnOffset) < 2, "Back from narrow details restores the exact list scroll position");
        vm.SelectedItem = vm.VisibleJobs[1]; await vm.DetailTask; await SettleAsync();
        check(vm.Detail is { HasError: true, IsBusy: false } && vm.ReloadDetailsCommand.CanExecute(null), "Unavailable posting description keeps Original and retry actions available");
        await RenderAsync(window, Path.Combine(output, "JobFinder-03-details-error.png"), 1320, 930);
        vm.SelectedItem = null; window.Close();
        var raceService = new RacingDetailService(page.Jobs[0].Key);
        var race = new ViewModel(Path.Combine(isolated, "detail-race"), testSources: [source], detailService: raceService, logos: new CompanyLogoService(Path.Combine(isolated, "race-logos"), false));
        await race.SearchAsync(); race.SelectedItem = race.VisibleJobs[0]; var old = race.DetailTask;
        await Task.Delay(15); race.SelectedItem = race.VisibleJobs[1]; var current = race.DetailTask;
        await Task.WhenAll(old, current);
        check(race.Detail?.Document?.Blocks.Single().Text == race.VisibleJobs[1].Title && race.VisibleJobs[0].Job.Details is null, "Late description response cannot overwrite a newer selection or enrich the wrong card");
        race.CloseDetails(); race.SelectedItem = null; race.SelectedItem = race.VisibleJobs[0]; var pending = race.DetailTask;
        race.CloseDetails(); await pending;
        check(!race.HasDetail, "Closing details prevents a late response from reopening the panel");
        race.Dispose();
    }
    private sealed class CapturedDetailService(string html) : IJobDetailService
    {
        public int Calls { get; private set; }
        public bool Fails { get; set; }
        public Task<JobDetails> LoadAsync(JobListing job, CancellationToken cancellation)
        {
            Calls++; cancellation.ThrowIfCancellationRequested();
            if (Fails) return Task.FromException<JobDetails>(new SourceFailure(FailureKind.Network, "Controlled offline condition"));
            try { return Task.FromResult(DetailParser.Parse(html, new(job.Url), job)); }
            catch (Exception e) { return Task.FromException<JobDetails>(e); }
        }
    }
    // Deliberately ignores cancellation to test response identity, never displayed in review screenshots.
    private sealed class RacingDetailService(string slowKey) : IJobDetailService
    {
        public async Task<JobDetails> LoadAsync(JobListing job, CancellationToken cancellation)
        { await Task.Delay(job.Key == slowKey ? 120 : 20); return new([new("paragraph", job.Title)], [], "", DateTimeOffset.UtcNow); }
    }
}
