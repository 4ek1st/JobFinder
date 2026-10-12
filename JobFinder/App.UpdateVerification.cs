using JobFinder.Core;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace JobFinder;

public partial class App
{
    // An opt-in real public-release proof, usable only in an empty bootstrap installation.
    // It never reads the user's existing profile or overrides the production update endpoint/key.
    private async Task VerifyGitHubUpdateAsync(string fixtures)
    {
        if (typeof(App).Assembly.GetName().Version != new Version(0, 4, 1, 0)) throw new InvalidOperationException("Use the real 0.4.1 bootstrap build.");
        string data = Path.Combine(AppContext.BaseDirectory, "data");
        if (Directory.Exists(data) && Directory.EnumerateFileSystemEntries(data).Any()) throw new InvalidOperationException("Use an empty isolated installation.");
        string evidence = Path.Combine(AppContext.BaseDirectory, "update-verification"); Directory.CreateDirectory(evidence);
        var page = AzubiyoSource.Parse(File.ReadAllText(Path.Combine(fixtures, "azubiyo-filtered.txt")), new("https://www.azubiyo.de/ausbildung/berlin/?subject=Fachinformatiker&location=Berlin&radius=25"));
        if (page.Jobs.Count < 2) throw new InvalidOperationException("Public captured source fixtures are required.");
        var profile = new UserProfile { KnownSourceIds = SourceCatalog.FeedIds(), EnabledSources = [], Language = "ru", Theme = "dark", LastQuery = new("Fachinformatiker", "Berlin", 25, JobKind.Apprenticeship), Favorites = page.Jobs.Take(2).ToList(), Searches = [new("Verification saved search", new("Design", "Hamburg", 50, JobKind.Internship))] };
        new ProfileStore(data).Save(profile);
        var vm = new ViewModel(data); var window = new MainWindow(vm) { Width = 1320, Height = 900, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        MainWindow = window; ShutdownMode = ShutdownMode.OnMainWindowClose; window.Show(); await vm.SearchAsync();
        vm.ShowFavorites = true; vm.Sort = 1; vm.Keyword = "Design draft retained"; vm.City = "Hamburg"; vm.SearchExpanded = true;
        await vm.Updates.StartAsync();
        if (!vm.Updates.ShowCaption || vm.Updates.Available?.Version <= vm.Updates.CurrentVersion) throw new InvalidOperationException("A real newer public release was not offered: " + vm.Updates.Status);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        var button = Descendants(window).OfType<Button>().Single(control => control.Name == "UpdateCaptionAction");
        if (!button.IsVisible || !button.IsEnabled || button.Command != vm.Updates.CaptionCommand) throw new InvalidOperationException("The actual caption action is not available.");
        File.WriteAllText(Path.Combine(evidence, "before.json"), JsonSerializer.Serialize(VerificationState(window), new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(data, "github-update-verification.marker"), "JobFinder isolated public update proof v1");
        await RenderAsync(window, Path.Combine(evidence, "public-update-available.png"), 1320, 900);
        button.Command.Execute(button.CommandParameter);
        await vm.Updates.Operation;
        if (!vm.Updates.IsRestarting) throw new InvalidOperationException("Update did not reach the helper/restart phase: " + vm.Updates.Status);
    }

    private static async Task CompleteGitHubUpdateVerificationAsync(MainWindow window, string token)
    {
        string marker = Path.Combine(AppContext.BaseDirectory, "data", "github-update-verification.marker");
        if (!File.Exists(marker) || File.ReadAllText(marker) != "JobFinder isolated public update proof v1") return;
        string evidence = Path.Combine(AppContext.BaseDirectory, "update-verification"); Directory.CreateDirectory(evidence);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        File.WriteAllText(Path.Combine(evidence, "after.json"), JsonSerializer.Serialize(new { transactionToken = token, state = VerificationState(window) }, new JsonSerializerOptions { WriteIndented = true }));
        await RenderAsync(window, Path.Combine(evidence, "public-update-complete.png"), window.Width, window.Height);
        File.Delete(marker);
    }

    private static object VerificationState(MainWindow window)
    {
        var vm = window.Vm;
        string Hash(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)))).ToLowerInvariant();
        return new { version = typeof(App).Assembly.GetName().Version!.ToString(), pid = Environment.ProcessId, loaded = window.IsLoaded, visible = window.IsVisible, executable = Environment.ProcessPath,
            favorites = vm.Profile.Favorites.Count, favoritesSha256 = Hash(vm.Profile.Favorites), searches = vm.Profile.Searches.Count, searchesSha256 = Hash(vm.Profile.Searches),
            enabledSources = vm.Profile.EnabledSources, knownSources = vm.Profile.KnownSourceIds, applied = vm.Profile.LastQuery, draft = new SearchQuery(vm.Keyword, vm.City, vm.Radius, vm.Kind),
            vm.ShowFavorites, vm.SearchExpanded, vm.Sort, vm.Profile.Language, vm.Profile.Theme, window.Width, window.Height, captionShown = vm.Updates.ShowCaption, updateState = vm.Updates.State.ToString(), vm.Updates.Status };
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }
}
