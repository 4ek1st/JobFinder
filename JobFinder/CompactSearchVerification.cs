using JobFinder.Core;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace JobFinder;

public partial class App
{
    private static async Task VerifyCompactSearchAsync(string output, string fixtures, string isolated, Action<bool, string> check)
    {
        var page = AzubiyoSource.Parse(File.ReadAllText(Path.Combine(fixtures, "azubiyo-filtered.txt")), new("https://www.azubiyo.de/ausbildung/berlin/"));
        var loader = new CapturedDetailService(File.ReadAllText(Path.Combine(fixtures, "azubiyo-tu-detail.txt")));
        var logos = new CompanyLogoService(Path.Combine(isolated, "compact-logos"), false);
        logos.Seed(page.Jobs[0].LogoUrl, File.ReadAllBytes(Path.Combine(fixtures, "logo-azubiyo.png")));
        var vm = new ViewModel(Path.Combine(isolated, "compact-profile"), testSources: [new FixtureSource("azubiyo", "Azubiyo", JobKind.Apprenticeship, () => page)], detailService: loader, logos: logos);
        var window = TestWindow(vm); window.AutoLoadEnabled = false;
        check(vm.Ui.IsDe && vm.Profile.Language == "de", "A fresh application opens in German by default");
        check(window.SearchSurface.IsVisible && !window.CompactSearchBar.IsVisible, "Before a first search, the full search form is available");
        vm.ApplyQuery(new("Fachinformatiker", "Berlin", 25, JobKind.Apprenticeship)); await vm.SearchAsync(); await SettleAsync();
        check(!window.SearchSurface.IsVisible && window.EditSearchButton.IsVisible && window.ResultSourcesButton.IsVisible, "A new search collapses the form while editing and filters remain accessible");
        check(!VisualChildren<TextBlock>(window.CoverageBand).Any(text => text.Text == vm.SourceBrief), "Source coverage belongs in settings instead of occupying the vacancy feed after a search");
        window.JobList.SelectedItem = vm.VisibleJobs[0]; await vm.DetailTask; await SettleAsync();
        await RenderAsync(window, Path.Combine(output, "JobFinder-031-compact-dark.png"), 1536, 1024);
        window.FeedScroll!.ScrollToVerticalOffset(180); window.DetailPane.Scroll.ScrollToVerticalOffset(160); await SettleAsync();
        double listOffset = window.FeedScroll.VerticalOffset, detailOffset = window.DetailPane.Scroll.VerticalOffset;
        var selected = vm.SelectedJob; var document = vm.Detail!.Document;
        double compactHeight = window.JobList.ActualHeight;
        ApplicationCommands.Find.Execute(null, window); await SettleAsync();
        check(window.InputBindings.OfType<KeyBinding>().Any(binding => binding.Key == Key.F && binding.Modifiers == ModifierKeys.Control && binding.Command == ApplicationCommands.Find) && window.SearchSurface.IsVisible && !window.CompactSearchBar.IsVisible && window.KeywordBox.SelectionLength == vm.Keyword.Length, "The routed Ctrl+F search action expands the form and selects the profession field");
        double expandedHeight = window.JobList.ActualHeight;
        check(ReferenceEquals(vm.SelectedJob, selected) && ReferenceEquals(vm.Detail!.Document, document) && loader.Calls == 1 && Math.Abs(window.FeedScroll.VerticalOffset - listOffset) < 2 && Math.Abs(window.DetailPane.Scroll.VerticalOffset - detailOffset) < 2, "Opening the form preserves the selected posting and both scroll positions without refetching details");
        await RenderAsync(window, Path.Combine(output, "JobFinder-031-expanded-dark.png"), 1536, 1024);
        string activeLabel = vm.CompactSearchLabel;
        vm.Keyword = "Design"; vm.CollapseSearchCommand.Execute(null); await SettleAsync();
        check(vm.HasSearchChanges && vm.CompactSearchLabel == activeLabel && vm.Keyword == "Design" && ReferenceEquals(vm.SelectedJob, selected) && loader.Calls == 1 && Math.Abs(window.FeedScroll.VerticalOffset - listOffset) < 2, "Collapsing an edited draft retains its fields, marks pending changes and keeps the applied query and selection");
        check(ReferenceEquals(FocusManager.GetFocusedElement(window), window.EditSearchButton), "Collapsing the focused form returns logical focus to search editing");
        check(window.JobList.ActualHeight - expandedHeight >= 100 && Math.Abs(window.JobList.ActualHeight - compactHeight) < 2, "The compact search gives at least 100 additional logical pixels to the content");
        await RenderAsync(window, Path.Combine(output, "JobFinder-031-pending-changes.png"), 1536, 1024);
        vm.Keyword = "Fachinformatiker"; await SettleAsync();
        check(!vm.HasSearchChanges && !vm.AutoPaused, "Restoring applied fields clears the draft marker without pausing the search");
        vm.SetTheme("light"); window.DetailPane.Scroll.ScrollToTop();
        await RenderAsync(window, Path.Combine(output, "JobFinder-031-compact-light.png"), 1536, 1024);
        check(window.EditSearchButton.IsVisible && ReferenceEquals(vm.Detail!.Document, document), "The compact search remains available in the light theme with the same description");
        vm.SetTheme("dark"); vm.LanguageCommand.Execute("de");
        await RenderAsync(window, Path.Combine(output, "JobFinder-031-compact-de.png"), 820, 780);
        check(vm.CompactSearchLabel.Contains("Ausbildung") && window.EditSearchButton.IsVisible && window.DetailPane.IsVisible, "German compact search stays usable with a narrow detail overlay");
        vm.CloseDetails(); vm.LanguageCommand.Execute("ru");
        vm.ApplyQuery(new(new string('W', 120), new string('M', 80), 100, JobKind.Apprenticeship)); await vm.SearchAsync();
        await RenderAsync(window, Path.Combine(output, "JobFinder-031-long-query-small-scale200.png"), 768, 512, 2);
        var buttonBounds = window.EditSearchButton.TransformToAncestor(window.CompactSearchBar).TransformBounds(new Rect(window.EditSearchButton.RenderSize));
        check(window.CompactSearchSummary.TextTrimming == TextTrimming.CharacterEllipsis && buttonBounds.Right <= window.CompactSearchBar.ActualWidth && window.EditSearchButton.IsVisible && window.JobList.ActualHeight >= ((FrameworkElement)window.JobList.ItemContainerGenerator.ContainerFromIndex(0)).ActualHeight, "Long searches truncate while keeping editing and a complete card visible in a short 200 percent viewport");
        foreach (var source in vm.Sources) source.Enabled = false;
        await vm.SearchAsync(); await SettleAsync();
        check(vm.SearchExpanded && window.SearchAction.IsVisible && vm.Notice == vm.Ui["NoSources"], "A search without enabled sources keeps the form open and explains the correction");
        File.WriteAllText(Path.Combine(output, "compact-search-metrics.json"), JsonSerializer.Serialize(new { window = "1536x1024", expandedListHeight = expandedHeight, compactListHeight = compactHeight, contentHeightGained = compactHeight - expandedHeight, scrollTolerance = 2, rendering = "Actual WPF controls with captured public postings and company logo bytes", verifiedAt = DateTimeOffset.UtcNow }, new JsonSerializerOptions { WriteIndented = true }));
        window.Close();

        var paging = new PagingFixture(page.Jobs.ToArray(), "pages");
        var pagingVm = new ViewModel(Path.Combine(isolated, "compact-paging"), testSources: [paging]);
        var pagingWindow = TestWindow(pagingVm); pagingWindow.AutoLoadEnabled = false;
        await pagingVm.SearchAsync(); pagingVm.EditSearchCommand.Execute(null); await SettleAsync();
        await pagingVm.SearchAsync(more: true); await SettleAsync();
        check(pagingVm.SearchExpanded && pagingVm.VisibleJobs.Count == 9 && pagingWindow.SearchAction.IsVisible, "Appending a background page leaves a manually opened search form open");
        pagingVm.Sources[0].State = "Network"; pagingVm.Sources[0].Refresh();
        await pagingVm.SearchAsync(retry: "azubiyo"); await SettleAsync();
        check(pagingVm.SearchExpanded && pagingWindow.SearchAction.IsVisible, "Retrying a source leaves a manually opened search form open");
        pagingWindow.Close();

        var busy = new ViewModel(Path.Combine(isolated, "compact-cancel"), testSources: [new CancellableSource()]);
        var busyWindow = TestWindow(busy); busyWindow.AutoLoadEnabled = false;
        var request = busy.SearchAsync(); await SettleAsync();
        check(busyWindow.CompactStopButton.IsVisible && busyWindow.CompactStopButton.Command!.CanExecute(null) && busyWindow.EditSearchButton.IsVisible, "A running search can be stopped from the compact bar without reopening the form");
        busyWindow.CompactStopButton.Command.Execute(null); await request; await SettleAsync();
        check(!busy.IsBusy && busy.AutoPaused && busyWindow.EditSearchButton.IsVisible && !busyWindow.CompactStopButton.IsVisible, "Stopping from the compact bar leaves loaded content and search editing available");
        busyWindow.Close();
    }
}
