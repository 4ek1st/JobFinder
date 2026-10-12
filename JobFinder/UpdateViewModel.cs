using JobFinder.Core;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace JobFinder;

public enum UpdateState { Idle, Checking, Current, Available, Downloading, Preparing, Restarting, Cancelled, Error, Updated }

// Constructing a search model never performs a network request. Normal startup opts in through StartAsync.
public sealed class UpdateViewModel : Observable, IDisposable
{
    private readonly Ui _ui;
    private readonly IUpdateService _service;
    private readonly Dispatcher _dispatcher;
    private readonly Func<string> _createDownloadDirectory;
    private Func<bool>? _save;
    private Func<AvailableUpdate, string, CancellationToken, Task<Action>>? _prepare;
    private Action? _restart, _clearResume, _abortPrepared;
    private DispatcherTimer? _timer, _outcomeTimer;
    private CancellationTokenSource? _checkCancellation, _operationCancellation;
    private Task _checkTask = Task.CompletedTask, _operation = Task.CompletedTask;
    private AvailableUpdate? _available;
    private UpdateState _state;
    private string _statusKey = "UpdateIdle";
    private long _received, _total;
    private int _generation;
    private bool _disposed, _started, _isRestarting, _cancelling, _showOutcome, _checking;
    private string _outcomeKey = "UpdateUpdated";

    public UpdateViewModel(Ui ui, IUpdateService? service = null, Version? currentVersion = null, Func<string>? createDownloadDirectory = null)
    {
        _ui = ui; _service = service ?? new UpdateClient();
        CurrentVersion = currentVersion ?? typeof(App).Assembly.GetName().Version ?? new Version(0, 4, 1);
        _createDownloadDirectory = createDownloadDirectory ?? UpdateInstaller.CreateDownloadDirectory;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _ui.PropertyChanged += OnLanguageChanged;
        CaptionCommand = new(_ => { _ = CaptionActionAsync(); }, _ => CanCaptionAction);
        CheckCommand = new(_ => { _ = CheckAsync(manual: true); }, _ => CanCheck);
    }

    public Version CurrentVersion { get; }
    public AvailableUpdate? Available => _available;
    public UpdateState State => _state;
    public Task Operation => _operation;
    public bool IsRestarting => _isRestarting;
    public bool ShowOutcome => _showOutcome;
    public string OutcomeText => _outcomeKey == "UpdateUpdated" ? string.Format(_ui[_outcomeKey], FormatVersion(CurrentVersion)) : _ui[_outcomeKey];
    public bool IsDownloading => State == UpdateState.Downloading;
    public bool IsApplying => State is UpdateState.Preparing or UpdateState.Restarting;
    public bool IsChecking => _checking;
    public bool HasUpdate => _available is not null && _available.Version > CurrentVersion;
    public bool ShowCaption => HasUpdate && State is not UpdateState.Idle and not UpdateState.Current and not UpdateState.Updated;
    public bool CanCaptionAction => !_disposed && HasUpdate && !IsApplying && !IsChecking && !_cancelling;
    public bool CanCheck => !_disposed && !IsChecking && !IsDownloading && !IsApplying;
    public bool ShowProgress => IsDownloading || IsApplying;
    public double ProgressPercent => _total > 0 ? Math.Clamp(100d * _received / _total, 0, 100) : 0;
    public string ProgressLabel => ProgressPercent.ToString("0", CultureInfo.InvariantCulture) + "%";
    public string InstalledVersion => _ui["UpdateInstalled"] + " " + FormatVersion(CurrentVersion);
    public string CheckLabel => _ui[IsChecking ? "UpdateChecking" : "UpdateCheck"];
    public string Status => _statusKey is "UpdateAvailable" ? string.Format(_ui[_statusKey], FormatVersion(_available!.Version))
        : IsDownloading ? _cancelling ? _ui["UpdateCancelling"] : string.Format(_ui["UpdateDownloading"], ProgressLabel, TransferSize)
        : _statusKey == "UpdateUpdated" ? string.Format(_ui[_statusKey], FormatVersion(CurrentVersion)) : _ui[_statusKey];
    public string CaptionTooltip => IsDownloading ? Status + "\n" + _ui["UpdateCancel"] : IsApplying ? Status
        : State == UpdateState.Error && _available is not null ? Status + "\n" + string.Format(_ui["UpdateRetryInstall"], FormatVersion(_available.Version))
        : string.Format(_ui["UpdateInstall"], _available is null ? "" : FormatVersion(_available.Version));
    public string CaptionName => IsDownloading ? _ui["UpdateCancel"] + " · " + ProgressLabel : CaptionTooltip;
    public string TransferSize => _total <= 0 ? "" : $"{(_received / 1048576d).ToString("0.0", _ui.IsRu ? CultureInfo.GetCultureInfo("ru-RU") : CultureInfo.GetCultureInfo("de-DE"))} / {(_total / 1048576d).ToString("0.0", _ui.IsRu ? CultureInfo.GetCultureInfo("ru-RU") : CultureInfo.GetCultureInfo("de-DE"))} MB";
    public Command CaptionCommand { get; }
    public Command CheckCommand { get; }
    internal bool ScheduleRunning => _timer?.IsEnabled == true;
    public static string FormatVersion(Version version) => version.Revision > 0 ? version.ToString(4) : version.Build >= 0 ? version.ToString(3) : version.ToString(2);

    public void ConfigureApply(Func<bool> saveState, Func<AvailableUpdate, string, CancellationToken, Task<Action>> prepare, Action restart, Action? clearResume = null, Action? abortPrepared = null)
    { _save = saveState; _prepare = prepare; _restart = restart; _clearResume = clearResume; _abortPrepared = abortPrepared; }

    public Task StartAsync()
    {
        if (_disposed || _started) return _checkTask;
        _started = true;
        _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMinutes(15) };
        _timer.Tick += OnTimer; _timer.Start();
        return CheckAsync();
    }
    private void OnTimer(object? sender, EventArgs e) => _ = CheckAsync();
    public Task CheckAsync(bool manual = false)
    {
        if (_disposed) return Task.CompletedTask;
        if (IsChecking) return _checkTask;
        if (!CanCheck) return Task.CompletedTask;
        if (manual && _service is UpdateClient client) client.InvalidateCache();
        _checkTask = RunCheckAsync(); return _checkTask;
    }
    private async Task RunCheckAsync()
    {
        using var cancellation = new CancellationTokenSource(); _checkCancellation = cancellation;
        _checking = true;
        SetState(UpdateState.Checking, "UpdateChecking");
        try
        {
            var available = await _service.CheckAsync(CurrentVersion, cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested) return;
            _available = available is not null && available.Version > CurrentVersion ? available : null;
            SetState(_available is null ? UpdateState.Current : UpdateState.Available, _available is null ? "UpdateCurrent" : "UpdateAvailable");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        { if (!_disposed) SetState(UpdateState.Error, FailureKey(error, checking: true)); }
        finally { if (ReferenceEquals(_checkCancellation, cancellation)) { _checkCancellation = null; _checking = false; if (!_disposed) Notify(); } }
    }

    public Task CaptionActionAsync()
    {
        if (_disposed) return Task.CompletedTask;
        if (IsDownloading)
        {
            if (!_cancelling) { _cancelling = true; Notify(); _operationCancellation?.Cancel(); }
            return _operation;
        }
        if (!CanCaptionAction) return Task.CompletedTask;
        _operation = RunUpdateAsync(_available!); return _operation;
    }
    private async Task RunUpdateAsync(AvailableUpdate update)
    {
        using var cancellation = new CancellationTokenSource(); _operationCancellation = cancellation;
        int generation = ++_generation;
        string? directory = null, package = null, stagingPackage = null;
        bool savedResume = false, helperLaunched = false, prepared = false;
        _received = 0; _total = update.Manifest.PackageSize; _cancelling = false;
        SetState(UpdateState.Downloading, "UpdateDownloading");
        try
        {
            directory = _createDownloadDirectory(); Directory.CreateDirectory(directory);
            stagingPackage = package = Path.Combine(directory, UpdateProduct.PackageName);
            var progress = new Progress<UpdateTransferProgress>(value =>
            {
                if (_disposed || generation != _generation || !IsDownloading || cancellation.IsCancellationRequested) return;
                _received = Math.Clamp(value.BytesReceived, 0, value.TotalBytes > 0 ? value.TotalBytes : update.Manifest.PackageSize);
                _total = value.TotalBytes > 0 ? value.TotalBytes : update.Manifest.PackageSize; Notify();
            });
            package = await _service.DownloadAsync(update, package, progress, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            SetState(UpdateState.Preparing, "UpdatePreparing");
            if (_prepare is null || _save is null || _restart is null) throw new UpdateFailure(UpdateFailureKind.Install, "Update actions are not configured");
            // Validation/preparation can take time. Capture personal state only immediately before starting the helper.
            var launch = await _prepare(update, package, cancellation.Token);
            prepared = true;
            // The prepared transaction owns its validated copy. The download staging file is no longer needed.
            CleanupDownload(directory, stagingPackage);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!_save()) throw new UpdateFailure(UpdateFailureKind.Save, "Personal state was not saved");
            savedResume = true; _isRestarting = true;
            SetState(UpdateState.Restarting, "UpdateRestarting");
            launch(); helperLaunched = true;
            _restart();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { if (!_disposed) SetState(UpdateState.Cancelled, "UpdateCancelled"); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _isRestarting = false;
            if (!_disposed) SetState(UpdateState.Error, FailureKey(error, checking: false, applying: IsApplying));
        }
        finally
        {
            _cancelling = false;
            if (!helperLaunched)
            {
                if (prepared) try { _abortPrepared?.Invoke(); } catch (IOException) { } catch (UnauthorizedAccessException) { } catch (UpdateFailure) { }
                if (savedResume) try { _clearResume?.Invoke(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            CleanupDownload(directory, stagingPackage);
            if (ReferenceEquals(_operationCancellation, cancellation)) _operationCancellation = null;
            if (!_disposed) Notify();
        }
    }
    private static void CleanupDownload(string? directory, string? package)
    {
        try { if (package is not null && File.Exists(package)) File.Delete(package); if (directory is not null && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    private static string FailureKey(Exception error, bool checking, bool applying = false) => error switch
    {
        UpdateFailure { Kind: UpdateFailureKind.Save } => "UpdateSaveError",
        UpdateFailure { Kind: UpdateFailureKind.Verification or UpdateFailureKind.Format } => "UpdateVerificationError",
        UpdateFailure { Kind: UpdateFailureKind.RateLimit } => "UpdateWait",
        UpdateFailure { Kind: UpdateFailureKind.Access } => "UpdateAccessError",
        UpdateFailure { Kind: UpdateFailureKind.Install or UpdateFailureKind.Startup } => "UpdateInstallError",
        _ => checking ? "UpdateCheckError" : applying ? "UpdateInstallError" : "UpdateDownloadError"
    };
    public void ReportUpdated() => NotifyStartupOutcome(true);
    public void NotifyStartupOutcome(bool success)
    {
        if (_disposed) return;
        _outcomeKey = success ? "UpdateUpdated" : "UpdateRolledBack"; _showOutcome = true;
        if (IsChecking) Notify();
        else SetState(success ? HasUpdate ? UpdateState.Available : UpdateState.Updated : UpdateState.Error, success && HasUpdate ? "UpdateAvailable" : _outcomeKey);
        if (_outcomeTimer is null)
        {
            _outcomeTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromSeconds(14) };
            _outcomeTimer.Tick += OnOutcomeTimer;
        }
        _outcomeTimer.Stop(); _outcomeTimer.Start();
    }
    private void OnOutcomeTimer(object? sender, EventArgs e) { _outcomeTimer?.Stop(); _showOutcome = false; Notify(); }
    private void SetState(UpdateState state, string statusKey) { _state = state; _statusKey = statusKey; Notify(); }
    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e) => Notify();
    private void Notify() { Refresh(); CommandManager.InvalidateRequerySuggested(); }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _generation++;
        _timer?.Stop(); if (_timer is not null) _timer.Tick -= OnTimer;
        _outcomeTimer?.Stop(); if (_outcomeTimer is not null) _outcomeTimer.Tick -= OnOutcomeTimer;
        _checkCancellation?.Cancel(); _operationCancellation?.Cancel();
        _ui.PropertyChanged -= OnLanguageChanged;
        if (_service is IDisposable disposable) disposable.Dispose();
    }
}
