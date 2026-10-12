using JobFinder.Core;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace JobFinder;

public partial class App
{
    private static async Task VerifySourceDirectoryAsync(string output, string isolated, Action<bool, string> check)
    {
        using var client = new SourceClient();
        var vm = new ViewModel(Path.Combine(isolated, "directory-profile"), testSources: SourceCatalog.Create(client));
        var window = TestWindow(vm); window.AutoLoadEnabled = false;
        var dialog = window.CreateSourcesDialog(); dialog.WindowStartupLocation = WindowStartupLocation.Manual; dialog.Left = -15000; dialog.Top = -15000; dialog.ShowActivated = false;
        dialog.Show(); await SettleAsync();
        var directory = (SourceDirectory)dialog.Content;
        check(directory.Model.Feeds.Count == SourceCatalog.FeedIds().Count && directory.Model.Portals.Count == SourceCatalog.Sources.Count(s => !s.InFeed), "Directory distinguishes all integrated feeds from external websites");
        check(directory.DirectoryScroll.ScrollableHeight > 0 && directory.SourceSearch.IsVisible && directory.RadiusChoice.IsVisible, "Large source catalog scrolls independently while search and filters stay visible");
        directory.Model.FeedOnly = true;
        await RenderAsync(dialog, Path.Combine(output, "JobFinder-04-sources-dark.png"), 760, 800);
        directory.Model.Search = "aZuBi"; await SettleAsync();
        check(directory.Model.Feeds.Any(s => s.Id == "azubiyo") && directory.Model.Feeds.Any(s => s.Id == "azubide") && directory.Model.Feeds.All(s => Text.Normalize(s.Name + " " + s.Categories).Contains("azubi")), "Source search is case-insensitive and returns matching real sources");
        directory.Model.Search = ""; directory.Model.Training = true; await SettleAsync();
        check(directory.Model.Feeds.Count > 3 && directory.Model.Feeds.All(s => s.Source.Supports(JobKind.Apprenticeship)), "Training filter shows only adapters supporting Ausbildung");
        var priorEnabled = vm.Sources.ToDictionary(s => s.Id, s => s.Enabled);
        var filteredIds = directory.Model.Feeds.Select(s => s.Id).ToHashSet();
        directory.Model.DisableVisible.Execute(null); await SettleAsync();
        check(directory.Model.Feeds.All(s => !s.Enabled) && vm.Sources.Where(s => !filteredIds.Contains(s.Id)).All(s => s.Enabled == priorEnabled[s.Id]), "Bulk disable applies to all filtered sources while preserving sources outside the filter");
        directory.Model.EnableVisible.Execute(null); await SettleAsync();
        check(directory.Model.Feeds.All(s => s.Enabled) && vm.Sources.Where(s => !filteredIds.Contains(s.Id)).All(s => s.Enabled == priorEnabled[s.Id]), "Bulk enable includes filtered sources beyond the visible viewport without changing unrelated selections");
        foreach (var source in vm.Sources) source.Enabled = priorEnabled[source.Id];
        var selected = directory.Model.Feeds[0]; bool enabled = selected.Enabled; selected.Enabled = !enabled; await SettleAsync();
        check(new ProfileStore(vm.Store.DirectoryPath).Load().EnabledSources.Contains(selected.Id) == selected.Enabled, "A source toggle is persisted without starting a new search");
        selected.Enabled = enabled; directory.Model.PortalOnly = true; await SettleAsync();
        check(directory.Model.Feeds.Count == 0 && directory.Model.Portals.Count > 0 && !directory.Model.EnableVisible.CanExecute(null), "External-only mode has no feed toggles or misleading enable action");
        directory.Model.Search = "zzzznoportal12345"; await SettleAsync();
        check(directory.Model.Empty, "An unmatched source search has an explicit empty state");
        directory.Model.Search = ""; directory.Model.AllKinds = true; directory.Model.Both = true; vm.SetTheme("light"); await SettleAsync();
        await RenderAsync(dialog, Path.Combine(output, "JobFinder-04-sources-light.png"), 760, 800);
        check(directory.SourceSearch.IsVisible && VisualChildren<CheckBox>(directory).All(box => box.Template is not null && VisualChildren<FluentIcon>(box).Any()), "New source controls and toggles retain application themes and glyphs");
        vm.LanguageCommand.Execute("de"); directory.Model.PortalOnly = true;
        await RenderAsync(dialog, Path.Combine(output, "JobFinder-04-sources-external-de.png"), 660, 720);
        check(dialog.Title == vm.Ui["Filters"] && directory.Model.Summary.Contains("ausgewählt") && directory.SourceSearch.IsVisible && directory.DirectoryScroll.ViewportHeight > 100, "German narrow catalog retains a usable viewport and localized title/selection summary");
        vm.LanguageCommand.Execute("ru"); vm.SetTheme("dark");
        dialog.Close();
        foreach (var source in vm.Sources.Take(7)) { source.State = "Network"; source.Refresh(); }
        vm.ApplyQuery(new("", "Berlin"));
        // No network request is needed for these directory checks; controller failures are tested separately.
        check(vm.Sources.Count > 10 && SourceCatalog.Sources.Count > vm.Sources.Count, "Expanded catalog contains many real adapters and additional external choices");
        window.Close();
    }
}
