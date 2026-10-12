using JobFinder.Core;
using System.IO;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;

namespace JobFinder;

public partial class App
{
    internal static async Task VerifyUpdateUiAsync(string output, string isolated, Action<bool, string> check)
    {
        var names = new List<string>();
        void Check(bool condition, string name) { check(condition, name); names.Add(name); }
        var capturedJobs = Current.Windows.OfType<MainWindow>().First().Vm.VisibleJobs.Select(view => view.Job).ToArray();
        var source = new FixtureSource("azubiyo", "Azubiyo", JobKind.Apprenticeship, () => new(capturedJobs, null, capturedJobs.Length));
        var service = new ControlledUpdateService();
        string profile = Path.Combine(isolated, "updates-profile");
        var vm = new ViewModel(profile, testSources: [source], detailService: new UpdateFixtureDetails(), logos: new CompanyLogoService(Path.Combine(isolated, "updates-logos"), false), updateService: service, updateDownloadDirectory: () => Path.Combine(isolated, "updates-stage", Guid.NewGuid().ToString("N")));
        var window = TestWindow(vm); window.AutoLoadEnabled = false;
        await SettleAsync();
        Check(window.Template.FindName("UpdateCaptionAction", window) is Button, "The main window provides the update action immediately before Minimize");
        var caption = (Button)window.Template.FindName("UpdateCaptionAction", window);
        var minimize = (Button)window.Template.FindName("Minimize", window);
        Check(service.CheckCalls == 0 && vm.Updates.State == UpdateState.Idle && caption.Visibility == Visibility.Collapsed, "Constructing a model and showing a verifier window performs no update request and hides the action");
        await vm.Updates.CheckAsync(); await SettleAsync();
        Check(vm.Updates.State == UpdateState.Current && caption.Visibility == Visibility.Collapsed, "A current version hides the download action");
        service.Next = CreateUiUpdate() with { Version = vm.Updates.CurrentVersion };
        await vm.Updates.CheckAsync(); await SettleAsync();
        Check(caption.Visibility == Visibility.Collapsed && !vm.Updates.HasUpdate, "An equal-version offer cannot expose the update action");
        service.CheckFailure = new UpdateFailure(UpdateFailureKind.Verification, "RSA secret C:\\private\\key.pem");
        await vm.Updates.CheckAsync(); await SettleAsync();
        Check(vm.Updates.State == UpdateState.Error && !vm.Updates.ShowCaption && !vm.Updates.Status.Contains("RSA") && !vm.Updates.Status.Contains("private"), "A failed check without a known update stays hidden and does not expose diagnostic details");
        service.CheckFailure = null; service.Next = CreateUiUpdate(); service.CheckGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var checking = vm.Updates.CheckAsync(); var overlapping = vm.Updates.CheckAsync(); await SettleAsync();
        Check(vm.Updates.IsChecking && !caption.IsVisible && ReferenceEquals(checking, overlapping), "Concurrent checks share one request and keep an unknown update hidden");
        service.CheckGate.SetResult(); await checking; service.CheckGate = null; await SettleAsync();
        Check(vm.Updates.ShowCaption && caption.IsVisible && caption.Command!.CanExecute(null), "A newer release shows an enabled download action");
        Check(WindowChrome.GetIsHitTestVisibleInChrome(caption) && caption.Focusable && caption.IsTabStop && caption.FocusVisualStyle is not null, "The caption action supports WindowChrome hit testing and keyboard focus");
        var stack = (StackPanel)VisualTreeHelper.GetParent(caption);
        Check(stack.Children.IndexOf(minimize) == stack.Children.IndexOf(caption) + 1, "The download action occupies the caption slot immediately before Minimize");
        vm.ApplyQuery(new("Fachinformatiker", "Berlin", 25, JobKind.Apprenticeship)); await vm.SearchAsync(); await SettleAsync();
        Check(vm.VisibleJobs.Count > 0 && window.JobList.IsVisible, "Update captions are verified alongside real captured vacancies in the active result list");
        await RenderAsync(window, Path.Combine(output, "JobFinder-update-baseline.png"), 1536, 1024);
        double listHeight = window.JobList.ActualHeight;
        var metrics = new List<object>();
        foreach (var (theme, language, width, height, name) in new[] { ("dark", "ru", 1536d, 1024d, "JobFinder-update-available-dark.png"), ("light", "ru", 1536d, 1024d, "JobFinder-update-available-light.png"), ("dark", "de", 820d, 780d, "JobFinder-update-available-narrow-de.png"), ("light", "de", 768d, 512d, "JobFinder-update-available-short-de.png") })
        {
            vm.SetTheme(theme); vm.LanguageCommand.Execute(language);
            await RenderAsync(window, Path.Combine(output, name), width, height);
            var bounds = caption.TransformToAncestor(window).TransformBounds(new Rect(caption.RenderSize));
            var minimizeBounds = minimize.TransformToAncestor(window).TransformBounds(new Rect(minimize.RenderSize));
            Check(caption.IsVisible && bounds.Right <= minimizeBounds.Left + 1 && bounds.Top >= 0 && bounds.Bottom <= 39 && caption.ActualWidth == 44, $"{theme}/{language} update action stays in its own top-right caption slot at {width}x{height}");
            Check(ReferenceEquals(caption.Foreground, ThemeManager.Brush("Accent")) && AutomationProperties.GetName(caption).Contains("9.1.0"), $"{theme}/{language} update action follows the palette and has a localized version label");
            metrics.Add(new { name, theme, language, width, height, update = bounds, minimize = minimizeBounds });
        }
        vm.SetTheme("dark"); vm.LanguageCommand.Execute("ru");
        await RenderAsync(window, Path.Combine(output, "JobFinder-update-available-dark.png"), 1536, 1024);
        await SettleAsync();
        await RenderAsync(window, Path.Combine(output, "JobFinder-update-language-settled.png"), 1536, 1024);
        var labelDiagnostics = VisualChildren<TextBlock>(window).Where(text => new[] { vm.Ui["EditSearch"], vm.Ui["SourcesShort"], vm.Ui["Favorites"] }.Contains(text.Text)).Select(text =>
        {
            var formatted = new FormattedText(text.Text, CultureInfo.CurrentCulture, text.FlowDirection, new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, text.Foreground, VisualTreeHelper.GetDpi(text).PixelsPerDip);
            var ancestors = new List<object>();
            for (DependencyObject? ancestor = text; ancestor is Visual; ancestor = VisualTreeHelper.GetParent(ancestor)) if (ancestor is FrameworkElement element) ancestors.Add(new { type = element.GetType().Name, element.Name, element.ActualWidth, element.DesiredSize, origin = element.TranslatePoint(new(0, 0), window), clip = VisualTreeHelper.GetClip(element)?.Bounds, drawing = VisualTreeHelper.GetDrawing(element)?.Bounds });
            return new { text.Text, text.FontSize, text.ActualWidth, text.DesiredSize, formattedWidth = formatted.WidthIncludingTrailingWhitespace, clipWidth = VisualTreeHelper.GetClip(text)?.Bounds.Width, parent = VisualTreeHelper.GetParent(text).GetType().Name, parentWidth = (VisualTreeHelper.GetParent(text) as FrameworkElement)?.ActualWidth, ancestors };
        }).ToArray();
        File.WriteAllText(Path.Combine(output, "update-label-diagnostics.json"), JsonSerializer.Serialize(labelDiagnostics, new JsonSerializerOptions { WriteIndented = true }));
        Check(labelDiagnostics.All(label => label.ActualWidth >= label.formattedWidth - 1), "Switching DE to RU and restoring a wide viewport keeps ordinary control labels fully measured");
        Check(labelDiagnostics.All(label => !label.clipWidth.HasValue || label.clipWidth.Value >= label.formattedWidth - 1), "Switching languages clears the previous narrower visual clips on ordinary control labels");
        Check(Math.Abs(window.JobList.ActualHeight - listHeight) < 2 && !vm.HasSearchChanges, "An available update leaves the vacancy viewport and applied search unchanged");
        ApplicationCommands.Find.Execute(null, window); await SettleAsync();
        Check(window.SearchSurface.IsVisible && window.KeywordBox.SelectionLength == vm.Keyword.Length, "Ctrl+F still opens and focuses search editing while an update is available");
        vm.CollapseSearchCommand.Execute(null); await SettleAsync();
        Check(ReferenceEquals(FocusManager.GetFocusedElement(window), window.EditSearchButton), "Collapsing search still returns logical focus while the caption action is present");
        var about = window.CreateAboutDialog(); about.WindowStartupLocation = WindowStartupLocation.Manual; about.Left = -15000; about.Top = -15000; about.ShowActivated = false; about.ShowInTaskbar = false; about.Show(); await SettleAsync();
        Check(((Button)about.Template.FindName("UpdateCaptionAction", about)).Visibility == Visibility.Collapsed && VisualChildren<Button>(about).Single(b => b.Name == "CheckForUpdates").Command!.CanExecute(null), "About exposes a manual update check while its dialog caption has no update action");
        int checkCalls = service.CheckCalls; VisualChildren<Button>(about).Single(b => b.Name == "CheckForUpdates").Command!.Execute(null); await SettleAsync();
        Check(service.CheckCalls == checkCalls + 1 && vm.Updates.State == UpdateState.Available, "The actual About check action refreshes release status without affecting the search");
        Check(VisualChildren<TextBlock>(about).Any(text => text.Text == vm.Updates.InstalledVersion) && vm.Ui["AboutBody"].Contains("JobFinder " + Ui.AppVersion), "About displays the actual running assembly version");
        await RenderAsync(about, Path.Combine(output, "JobFinder-update-about-dark.png"), 660, 760); about.Close();

        service.ResetDownload(hold: true);
        caption.Command!.Execute(null); await service.DownloadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); await SettleAsync();
        Check(vm.Updates.IsDownloading && vm.Updates.ProgressPercent == 25 && caption.IsEnabled && caption.ActualWidth == 76 && !vm.Updates.CheckCommand.CanExecute(null), "Clicking the real caption action shows actual transfer percentage and blocks overlapping checks");
        Check(vm.Updates.CaptionTooltip.Contains(vm.Ui["UpdateCancel"]) && VisualChildren<ProgressBar>(caption).Single().Value == 25, "The transfer tooltip explains cancellation and the rendered progress bar uses transfer bytes");
        await RenderAsync(window, Path.Combine(output, "JobFinder-update-downloading-dark.png"), 1536, 1024);
        caption.Command.Execute(null); await vm.Updates.Operation; await SettleAsync();
        Check(vm.Updates.State == UpdateState.Cancelled && vm.Updates.ShowCaption && caption.IsEnabled && !vm.Updates.IsRestarting, "A second caption click cancels downloading and leaves the existing app with an available retry");
        Check(service.LastPackagePath is not null && !Directory.Exists(Path.GetDirectoryName(service.LastPackagePath)), "Cancellation removes only its owned empty download staging directory");
        service.ResetDownload(hold: false, failure: new IOException("Private downloader path C:\\secret"));
        await vm.Updates.CaptionActionAsync(); await SettleAsync();
        Check(vm.Updates.State == UpdateState.Error && caption.IsEnabled && !vm.Updates.Status.Contains("secret") && !vm.Updates.Status.Contains("C:"), "A failed transfer keeps retry available and hides private exception messages");
        await RenderAsync(window, Path.Combine(output, "JobFinder-update-retry-dark.png"), 1536, 1024);

        int launched = 0, restarted = 0, aborted = 0;
        var prepareGate = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        string preparedDirectory = Path.Combine(isolated, "prepared-save-failure"), preparedFile = Path.Combine(preparedDirectory, "payload.test");
        vm.Updates.ConfigureApply(window.SaveForUpdate, (_, _, _) => { Directory.CreateDirectory(preparedDirectory); File.WriteAllText(preparedFile, "owned preparation fixture"); return prepareGate.Task; }, () => restarted++, vm.ClearUpdateResume, () => { File.Delete(preparedFile); Directory.Delete(preparedDirectory); aborted++; });
        service.ResetDownload(hold: false); var preparing = vm.Updates.CaptionActionAsync(); await SettleAsync();
        Check(vm.Updates.State == UpdateState.Preparing && caption.IsVisible && !caption.IsEnabled && !vm.Updates.CheckCommand.CanExecute(null), "Preparing installation retains a disabled caption indicator and prevents duplicate actions");
        await RenderAsync(window, Path.Combine(output, "JobFinder-update-preparing-dark.png"), 1536, 1024);
        using (var locked = new FileStream(vm.Store.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            prepareGate.SetResult(() => launched++); await preparing; await SettleAsync();
            Check(vm.Updates.State == UpdateState.Error && launched == 0 && restarted == 0 && !vm.Updates.IsRestarting && vm.HasStorageNotice, "An actual profile save failure prevents helper launch and restart");
        }
        Check(caption.IsEnabled && vm.Updates.Status == vm.Ui["UpdateSaveError"], "A blocked save leaves a plain localized recovery status and an enabled retry");
        Check(!File.Exists(Path.Combine(profile, "update-resume.json")), "A failed profile save does not publish a restart snapshot");
        Check(aborted == 1 && !Directory.Exists(preparedDirectory) && !Directory.Exists(Path.GetDirectoryName(service.LastPackagePath)), "A save failure aborts the prepared transaction and removes the copied download staging directory");
        vm.Updates.ConfigureApply(() => false, (_, _, _) => Task.FromResult<Action>(() => launched++), () => restarted++);
        service.ResetDownload(hold: false); await vm.Updates.CaptionActionAsync();
        Check(launched == 0 && restarted == 0, "An injected save rejection also prevents installation and restart");
        string failedHelperDirectory = Path.Combine(isolated, "prepared-helper-failure"), failedHelperFile = Path.Combine(failedHelperDirectory, "payload.test");
        int cleared = 0;
        vm.Updates.ConfigureApply(() => true, (_, _, _) => { Directory.CreateDirectory(failedHelperDirectory); File.WriteAllText(failedHelperFile, "owned helper fixture"); return Task.FromResult<Action>(() => throw new System.ComponentModel.Win32Exception("Controlled helper start failure")); }, () => restarted++, () => cleared++, () => { File.Delete(failedHelperFile); Directory.Delete(failedHelperDirectory); aborted++; });
        service.ResetDownload(hold: false); await vm.Updates.CaptionActionAsync();
        Check(vm.Updates.State == UpdateState.Error && !vm.Updates.IsRestarting && restarted == 0 && cleared == 1 && aborted == 2 && !Directory.Exists(failedHelperDirectory), "A failed helper launch clears the resume snapshot, aborts preparation and leaves the app running");

        var selected = vm.VisibleJobs[0]; vm.SelectedJob = selected; await vm.DetailTask; vm.CloseDetails();
        vm.ToggleFavorite(selected); vm.SaveSearch("Update preserved search");
        vm.Sort = 1; vm.SearchExpanded = true; vm.Keyword = "Draft Design";
        var geometry = new UpdateWindowState(1234, 876, 120, 90, WindowState.Maximized, 120, 40);
        string handedDirectory = Path.Combine(isolated, "prepared-handoff"), handedFile = Path.Combine(handedDirectory, "helper-owned.test");
        vm.Updates.ConfigureApply(() => vm.SaveForUpdate(geometry), (_, _, _) => { Directory.CreateDirectory(handedDirectory); File.WriteAllText(handedFile, "owned handoff fixture"); return Task.FromResult<Action>(() => launched++); }, () => { restarted++; window.Close(); }, vm.ClearUpdateResume, () => { File.Delete(handedFile); Directory.Delete(handedDirectory); aborted++; });
        service.ResetDownload(hold: false); await vm.Updates.CaptionActionAsync();
        var saved = new ProfileStore(profile).Load();
        Check(launched == 1 && restarted == 1 && saved.LastQuery.Keyword == "Fachinformatiker", "Successful apply launch closes only the isolated window and preserves the applied query through MainWindow.Closed");
        Check(aborted == 2 && File.Exists(handedFile) && !Directory.Exists(Path.GetDirectoryName(service.LastPackagePath)), "A successful helper handoff retains its transaction while removing download staging");
        var resume = JsonSerializer.Deserialize<UpdateResumeState>(File.ReadAllText(Path.Combine(profile, "update-resume.json")))!;
        Check(resume.Draft.Keyword == "Draft Design" && resume.SearchExpanded && resume.Sort == 1 && resume.Window == geometry && resume.SelectedKey == selected.Job.Key, "The one-time local snapshot preserves unsent draft, expansion, sort, selection and window geometry");
        Check(saved.Favorites.Any(job => job.Key == selected.Job.Key) && saved.Searches.Any(search => search.Name == "Update preserved search") && saved.EnabledSources.SequenceEqual(vm.Profile.EnabledSources), "Updating preserves existing favorites, saved searches and source choices");

        var restoredService = new ControlledUpdateService();
        var restored = new ViewModel(profile, testSources: [source], detailService: new UpdateFixtureDetails(), logos: new CompanyLogoService(Path.Combine(isolated, "updates-restore-logos"), false), updateService: restoredService);
        var restoredWindow = TestWindow(restored); restoredWindow.AutoLoadEnabled = false;
        await restored.SearchAsync(); await restoredWindow.RestoreAfterUpdateAsync(restoreWindow: false); await SettleAsync();
        Check(restored.Keyword == "Draft Design" && restored.HasSearchChanges && restored.CompactSearchLabel.Contains("Fachinformatiker") && restored.SearchExpanded && restored.Sort == 1, "After the applied search runs, updater restart restores the unsent draft with an explicit pending-change state");
        Check(restored.SelectedJob?.Job.Key == selected.Job.Key && !restored.HasDetail && !File.Exists(Path.Combine(profile, "update-resume.json")), "Restored selection survives the update and the resume file is consumed once");
        Check(restored.Updates.State == UpdateState.Updated && !restored.Updates.ShowCaption, "Updater restart reports the installed version and clears the caption offer");
        Check(((TextBlock)restoredWindow.Template.FindName("UpdateOutcomeMessage", restoredWindow)).IsVisible, "Successful restart gives a brief themed caption status without a dialog");
        restored.Updates.NotifyStartupOutcome(false); await SettleAsync();
        Check(restored.Updates.State == UpdateState.Error && !restored.Updates.ShowCaption && restored.Updates.Status == restored.Ui["UpdateRolledBack"], "A rolled-back update reports recovery instead of success and keeps the action hidden until another offer");
        restored.LanguageCommand.Execute("de"); await SettleAsync();
        Check(((TextBlock)restoredWindow.Template.FindName("UpdateOutcomeMessage", restoredWindow)).Text == restored.Ui["UpdateRolledBack"] && restored.Updates.OutcomeText.StartsWith("Das Update"), "The visible rollback message switches to German with the rest of the window");
        restoredService.Next = CreateUiUpdate(); await restored.Updates.CheckAsync(); restored.Updates.NotifyStartupOutcome(false); await SettleAsync();
        Check(restored.Updates.ShowCaption && ((Button)restoredWindow.Template.FindName("UpdateCaptionAction", restoredWindow)).IsEnabled && restored.Updates.Status == restored.Ui["UpdateRolledBack"], "A rollback retains an already known newer release as an enabled caption retry");
        restoredWindow.Close();

        var scheduling = new ControlledUpdateService();
        using (var controller = new UpdateViewModel(new Ui(), scheduling, new Version(0, 4, 1)))
        {
            await controller.StartAsync(); await controller.StartAsync();
            Check(scheduling.CheckCalls == 1 && controller.ScheduleRunning, "Normal startup checks once and creates a single periodic update schedule");
            controller.Dispose(); Check(!controller.ScheduleRunning, "Disposal stops periodic checking");
        }
        foreach (bool success in new[] { true, false })
        {
            var startup = new ControlledUpdateService { Next = CreateUiUpdate(), CheckGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            using var controller = new UpdateViewModel(new Ui(), startup, new Version(0, 4, 1));
            var firstCheck = controller.CheckAsync(); controller.NotifyStartupOutcome(success); var secondCheck = controller.CheckAsync();
            Check(controller.IsChecking && !controller.CheckCommand.CanExecute(null) && ReferenceEquals(firstCheck, secondCheck) && startup.CheckCalls == 1, $"Startup outcome ({success}) cannot overlap a pending release check");
            startup.CheckGate.SetResult(); await firstCheck;
        }
        var late = new ControlledUpdateService { Next = CreateUiUpdate(), CheckGate = new(TaskCreationOptions.RunContinuationsAsynchronously), IgnoreCheckCancellation = true };
        using (var controller = new UpdateViewModel(new Ui(), late, new Version(0, 4, 1)))
        {
            var pending = controller.CheckAsync(); controller.Dispose(); late.CheckGate.SetResult(); await pending;
            Check(controller.Available is null && !controller.ShowCaption, "A late check completion after disposal cannot restore an update offer");
        }
        File.WriteAllText(Path.Combine(output, "update-ui-verification.json"), JsonSerializer.Serialize(new { passed = names.Count, checks = names, rendering = "Actual WPF tree and captured public vacancy records; update transport, preparation and restart are controlled delegates. No physical mouse/keyboard interaction or real installation is claimed by these UI checks.", verifiedAt = DateTimeOffset.UtcNow }, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(output, "update-caption-context.json"), JsonSerializer.Serialize(new { source = "JobFinder/Themes/Controls.xaml", template = "AppWindowStyle", control = "UpdateCaptionAction", icon = "Assets/Icons/download.svg", placement = "immediately before Minimize", sizes = metrics }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static AvailableUpdate CreateUiUpdate() => new(new UpdateManifest { Version = "9.1.0", PackageFile = UpdateProduct.PackageName, PackageSize = 100 * 1024 * 1024 }, new Version(9, 1, 0), "https://github.com/4ek1st/JobFinder/releases/download/v9.1.0/JobFinder-portable-win-x64.zip", [], [], "https://github.com/4ek1st/JobFinder/releases/tag/v9.1.0");
    private sealed class ControlledUpdateService : IUpdateService
    {
        public AvailableUpdate? Next { get; set; }
        public Exception? CheckFailure { get; set; }
        public int CheckCalls { get; private set; }
        public TaskCompletionSource? CheckGate { get; set; }
        public bool IgnoreCheckCancellation { get; set; }
        public TaskCompletionSource DownloadEntered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? LastPackagePath { get; private set; }
        private TaskCompletionSource _downloadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _holdDownload;
        private Exception? _downloadFailure;
        public async Task<AvailableUpdate?> CheckAsync(Version currentVersion, CancellationToken cancellation = default)
        {
            CheckCalls++;
            if (CheckGate is not null) { if (IgnoreCheckCancellation) await CheckGate.Task; else await CheckGate.Task.WaitAsync(cancellation); }
            if (CheckFailure is not null) throw CheckFailure;
            return Next;
        }
        public void ResetDownload(bool hold, Exception? failure = null)
        { _holdDownload = hold; _downloadFailure = failure; DownloadEntered = new(TaskCreationOptions.RunContinuationsAsynchronously); _downloadGate = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        public async Task<string> DownloadAsync(AvailableUpdate update, string packagePath, IProgress<UpdateTransferProgress>? progress = null, CancellationToken cancellation = default)
        {
            LastPackagePath = packagePath;
            progress?.Report(new(update.Manifest.PackageSize / 4, update.Manifest.PackageSize)); DownloadEntered.SetResult();
            if (_holdDownload) await _downloadGate.Task.WaitAsync(cancellation);
            cancellation.ThrowIfCancellationRequested(); if (_downloadFailure is not null) throw _downloadFailure;
            File.WriteAllBytes(packagePath, [1, 2, 3]);
            return packagePath;
        }
    }
    private sealed class UpdateFixtureDetails : IJobDetailService
    {
        public Task<JobDetails> LoadAsync(JobListing job, CancellationToken cancellation) => Task.FromResult(job.Details ?? new JobDetails([new("paragraph", job.Summary)], [], "", DateTimeOffset.UtcNow));
    }
}
