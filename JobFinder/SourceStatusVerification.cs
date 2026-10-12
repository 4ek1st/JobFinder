using JobFinder.Core;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace JobFinder;

public partial class App
{
    private static async Task VerifySourceStatusAsync(string output, string fixtures, string isolated, Action<bool, string> check)
    {
        var captured = AzubiyoSource.Parse(File.ReadAllText(Path.Combine(fixtures, "azubiyo-filtered.txt")), new("https://www.azubiyo.de/ausbildung/berlin/"));
        var recovery = new StatusRecoverySource();
        var logos = new CompanyLogoService(Path.Combine(isolated, "status-logos"), false);
        logos.Seed(captured.Jobs[0].LogoUrl, File.ReadAllBytes(Path.Combine(fixtures, "logo-azubiyo.png")));
        var vm = new ViewModel(Path.Combine(isolated, "source-status-profile"), testSources: [new FixtureSource("azubiyo", "Azubiyo", JobKind.Apprenticeship, () => captured with { Limited = true }), recovery], detailService: new CapturedDetailService(File.ReadAllText(Path.Combine(fixtures, "azubiyo-tu-detail.txt"))), logos: logos);
        var window = TestWindow(vm); window.AutoLoadEnabled = false;
        check(!window.SettingsWarning.IsVisible && vm.SourceStatusTitle == vm.Ui["SourceStatusWaiting"], "Settings does not claim a failure or successful source check before a search");
        var waiting = window.CreateSourcesDialog(statusView: true); waiting.WindowStartupLocation = WindowStartupLocation.Manual; waiting.Left = -15000; waiting.Top = -15000; waiting.ShowActivated = false; waiting.ShowInTaskbar = false; waiting.Show(); await SettleAsync();
        check(((SourceDirectory)waiting.Content).StatusOverview.IsVisible && ((SourceDirectory)waiting.Content).Model.Feeds.Count == 0, "Source status settings has an honest pre-search state"); waiting.Close();
        vm.ApplyQuery(new("Fachinformatiker", "Berlin", 25, JobKind.Apprenticeship)); await vm.SearchAsync(); await SettleAsync();
        window.JobList.SelectedItem = vm.VisibleJobs[0]; await vm.DetailTask; await SettleAsync();
        check(window.SettingsWarning.IsVisible && vm.SourceAttentionCount == 1 && AutomationProperties.GetName(window.SettingsButton).Contains("1"), "A real failed source request lights the settings dot and exposes the issue count to accessibility");
        check(!vm.ShowNotice && !VisualChildren<TextBlock>(window.CoverageBand).Any(text => text.Text == vm.SourceBrief || text.Text == vm.SourceAttentionLabel), "Partial-source warnings and coverage no longer occupy the vacancy feed");
        check(window.SettingsButton.IsTabStop && !window.SettingsWarning.IsHitTestVisible && ReferenceEquals(window.SettingsWarning.Fill, ThemeManager.Brush("WarningDot")), "The yellow indicator preserves keyboard access and the settings click target");
        var menu = window.CreateAppMenu();
        check(menu.Items.OfType<MenuItem>().Any(item => Equals(item.Tag, "source-status")), "Settings provides a dedicated source-status action");
        await RenderAsync(window, Path.Combine(output, "JobFinder-052-settings-dot-dark.png"), 1536, 1024);
        vm.ShowFavorites = true; await SettleAsync();
        check(window.SettingsWarning.IsVisible, "An unresolved source issue remains visible while viewing favorites"); vm.ShowResults = true;
        var dialog = window.CreateSourcesDialog(statusView: true); dialog.WindowStartupLocation = WindowStartupLocation.Manual; dialog.Left = -15000; dialog.Top = -15000; dialog.ShowActivated = false; dialog.ShowInTaskbar = false; dialog.Show(); await SettleAsync();
        var directory = (SourceDirectory)dialog.Content;
        check(directory.Model.IsStatusView && directory.StatusOverview.IsVisible && !directory.SourceSearch.IsVisible && !directory.RadiusChoice.IsVisible && directory.Model.Feeds[0].Id == recovery.Id, "Status settings gives space to source details and puts issues first");
        check(VisualChildren<TextBlock>(directory).Any(text => text.IsVisible && text.Text == vm.SourceBrief) && VisualChildren<TextBlock>(directory).Any(text => text.Text == vm.Ui["Network"]), "Settings retains the original source response summary and a concrete failure reason");
        var problem = vm.Sources.Single(source => source.Id == recovery.Id);
        var retry = VisualChildren<Button>(directory).Single(button => ReferenceEquals(button.Command, vm.RetryCommand) && Equals(button.CommandParameter, problem));
        check(retry.IsVisible && retry.Command!.CanExecute(retry.CommandParameter), "A failed source can be retried directly from status settings");
        await RenderAsync(dialog, Path.Combine(output, "JobFinder-052-source-status-dark.png"), 760, 720);
        vm.SetTheme("light"); vm.LanguageCommand.Execute("de"); await SettleAsync();
        await RenderAsync(dialog, Path.Combine(output, "JobFinder-052-source-status-light-de.png"), 660, 620);
        check(dialog.Title == "Quellenstatus" && vm.SettingsLabel.Contains("Quellen") && directory.DirectoryScroll.ViewportHeight > 200 && directory.CloseButton.IsVisible, "German narrow status settings keeps localized text and usable scrolling in the light theme");
        vm.LanguageCommand.Execute("ru"); vm.SetTheme("dark"); await SettleAsync();
        var selected = vm.SelectedJob;
        recovery.Fail = false; recovery.Gate = new(); retry.Command!.Execute(retry.CommandParameter);
        await UntilAsync(() => recovery.Calls == 2 && vm.IsBusy);
        check(window.SettingsWarning.IsVisible && !vm.RetryCommand.CanExecute(problem), "The dot stays lit until a retry succeeds and duplicate retry is disabled during loading");
        recovery.Gate.SetResult(); await UntilAsync(() => !vm.IsBusy); recovery.Gate = null;
        check(!window.SettingsWarning.IsVisible && vm.SourceAttentionCount == 0 && vm.SourceStatusTitle == vm.Ui["SourceStatusHealthy"] && ReferenceEquals(vm.SelectedJob, selected), "Successful retry clears the settings dot while preserving loaded jobs and selection");
        check(vm.Sources[0].LimitedCoverage && !vm.ShowSourceAttention, "Normal limited source coverage does not create a false problem indicator");
        check(directory.Model.Feeds.All(source => !source.HasError) && VisualChildren<TextBlock>(directory.StatusOverview).Any(text => text.Text == vm.Ui["SourceStatusHealthy"]), "An open status page updates its source details and healthy summary after recovery");
        await RenderAsync(window, Path.Combine(output, "JobFinder-052-settings-healthy.png"), 1536, 1024);
        foreach (var failure in Enum.GetNames<FailureKind>())
        {
            problem.State = failure; problem.Refresh(); vm.Refresh(); await SettleAsync();
            check(window.SettingsWarning.IsVisible, $"Settings signals an actionable {failure} source issue");
        }
        problem.RetryAt = DateTimeOffset.UtcNow.AddHours(1); problem.State = "RateLimit"; problem.Refresh(); vm.Refresh(); await SettleAsync();
        check(!vm.RetryCommand.CanExecute(problem) && problem.Coverage.Contains(vm.Ui["Retry"]), "A source cooldown explains when to retry and disables premature requests"); problem.RetryAt = null;
        vm.Keyword = "changed draft"; await SettleAsync();
        check(window.SettingsWarning.IsVisible && !vm.RetryCommand.CanExecute(problem) && vm.HasSearchChanges, "Editing the search retains source issues and requires applying the changed query before retry"); vm.Keyword = "Fachinformatiker";
        problem.Enabled = false; await SettleAsync();
        check(!window.SettingsWarning.IsVisible && directory.Model.Feeds.All(source => source.Id != recovery.Id), "Disabling the failed source clears its attention indicator and updates the open status list"); problem.Enabled = true;
        problem.State = "Ok"; problem.ScanLimited = true; problem.Refresh(); vm.Refresh(); await SettleAsync();
        check(window.SettingsWarning.IsVisible && vm.RetryCommand.CanExecute(problem), "A source scan limit is discoverable and can be continued from settings"); problem.ScanLimited = false; problem.PaginationStopped = true; problem.Refresh(); vm.Refresh(); await SettleAsync();
        check(window.SettingsWarning.IsVisible && problem.Status == vm.Ui["RepeatedPage"], "A repeated source page has a visible reason in settings without a feed banner"); problem.PaginationStopped = false;
        problem.State = "Network"; problem.Refresh(); vm.Refresh(); recovery.Gate = new();
        var cancelledRetry = vm.SearchAsync(retry: recovery.Id); await UntilAsync(() => vm.IsBusy); vm.Cancel(); await cancelledRetry; recovery.Gate = null; await SettleAsync();
        check(window.SettingsWarning.IsVisible && problem.State == "Cancelled" && vm.RetryCommand.CanExecute(problem), "Cancelling recovery retains the unresolved issue and allows a later retry");
        await vm.SearchAsync(retry: recovery.Id); await SettleAsync();
        check(!window.SettingsWarning.IsVisible && !vm.AutoPaused, "A later successful retry recovers from cancellation and clears the dot");
        dialog.Close(); window.Close();
    }

    private sealed class StatusRecoverySource : IJobSource
    {
        public string Id => "arbeitnow";
        public string Name => "Arbeitnow";
        public bool Fail { get; set; } = true;
        public int Calls { get; private set; }
        public TaskCompletionSource? Gate { get; set; }
        public bool Supports(JobKind kind) => true;
        public async Task<JobPage> SearchAsync(SearchQuery query, string? next, CancellationToken cancellation)
        {
            Calls++;
            if (Gate is not null) await Gate.Task.WaitAsync(cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (Fail) throw new SourceFailure(FailureKind.Network, "Controlled source connection failure");
            return new([], null, 0);
        }
    }
}
