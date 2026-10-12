using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace JobFinder.Core;

public sealed record UpdateProcessIdentity(int ProcessId, string ExecutablePath, long StartTimeUtcTicks);

public interface IUpdateProcessHost
{
    string CurrentExecutablePath { get; }
    int CurrentProcessId { get; }
    UpdateProcessIdentity? GetProcess(int processId);
    Task<bool> WaitForExitAsync(UpdateProcessIdentity process, TimeSpan timeout, CancellationToken cancellation);
    Process LaunchHelper(string executablePath, string planPath);
    UpdateProcessIdentity StartApplication(string executablePath, IReadOnlyList<string> arguments);
    Task<bool> StopAsync(UpdateProcessIdentity child, CancellationToken cancellation);
}

public sealed record UpdateApplyPlan
{
    public int SchemaVersion { get; init; } = 1;
    public string PlanPath { get; init; } = "";
    public string TransactionDirectory { get; init; } = "";
    public string TransactionToken { get; init; } = "";
    public string InstallDirectory { get; init; } = "";
    public string Version { get; init; } = "";
    public string CurrentVersion { get; init; } = "";
    public string PackagePath { get; init; } = "";
    public string HelperPath { get; init; } = "";
    public string ParentExecutablePath { get; init; } = "";
    public string ParentExecutableSha256 { get; init; } = "";
    public int ParentProcessId { get; init; }
    public long ParentStartTimeUtcTicks { get; init; }
    public byte[] ManifestBytes { get; init; } = [];
    public byte[] SignatureBytes { get; init; } = [];
}

public sealed record UpdateApplyResult
{
    public bool Success { get; init; }
    public bool RolledBack { get; init; }
    public string FailureCode { get; init; } = "";
    public string TransactionDirectory { get; init; } = "";
    public string TransactionToken { get; init; } = "";
    public string InstallDirectory { get; init; } = "";
    public string Version { get; init; } = "";
    public string ReceiptPath { get; init; } = "";
    public string BackupDirectory { get; init; } = "";
    public int? StartedProcessId { get; init; }
    public int? RestoredProcessId { get; init; }
    public bool RequiresAttention { get; init; }
}

public sealed class UpdateInstaller
{
    private readonly string _publicKey;
    private readonly IUpdateProcessHost _processes;
    private readonly TimeSpan _parentExitTimeout, _startupTimeout;
    private readonly object _preparationGate = new();
    private readonly Dictionary<string, UpdateApplyPlan> _ownedPlans = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private sealed record PackageFile(string RelativePath, string Path, string Sha256, long Length);
    private sealed record StartupAcknowledgement(string TransactionToken, string Version, int ProcessId, string ExecutablePath, long StartTimeUtcTicks);
    private sealed record FileBackup(PackageFile File, string TargetPath, bool Existed, string BackupPath, string OriginalSha256);
    private sealed record DataFile(string RelativePath, string Sha256);
    private sealed record DataSnapshot(bool Existed, List<DataFile> Files, List<string> Directories);

    public UpdateInstaller(string? publicKeyPem = null, IUpdateProcessHost? processHost = null, TimeSpan? parentExitTimeout = null, TimeSpan? startupTimeout = null)
    {
        _publicKey = publicKeyPem ?? UpdateProduct.PublicKeyPem; _processes = processHost ?? new UpdateProcessHost();
        _parentExitTimeout = Timeout(parentExitTimeout, TimeSpan.FromSeconds(30)); _startupTimeout = Timeout(startupTimeout, TimeSpan.FromSeconds(45));
    }

    private static TimeSpan Timeout(TimeSpan? requested, TimeSpan maximum) => requested is null ? maximum : requested > TimeSpan.Zero && requested <= maximum ? requested.Value : throw new ArgumentOutOfRangeException(nameof(requested));

    public static string CreateDownloadDirectory()
    {
        string directory = UpdatePaths.Transaction(Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); UpdatePaths.NoReparse(directory); return directory;
    }

    public async Task<UpdateApplyPlan> PrepareAsync(AvailableUpdate update, string packagePath, string installDirectory, int parentPid, string parentExecutablePath, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var manifest = UpdateValidation.VerifyManifest(update.ManifestBytes, update.SignatureBytes, _publicKey);
        var version = UpdateValidation.ParseVersion(manifest.Version);
        if (version != Normalize(update.Version)) throw Verify("The available update does not match its signed version.");
        installDirectory = UpdatePaths.Absolute(installDirectory); UpdatePaths.Install(installDirectory);
        parentExecutablePath = UpdatePaths.Absolute(parentExecutablePath);
        if (!UpdatePaths.Same(parentExecutablePath, Path.Combine(installDirectory, "JobFinder.exe")) || _processes.CurrentProcessId != parentPid || !UpdatePaths.Same(_processes.CurrentExecutablePath, parentExecutablePath))
            throw Verify("The update parent must be the running installed executable.");
        UpdatePaths.NoReparse(parentExecutablePath);
        var parent = _processes.GetProcess(parentPid) ?? throw Verify("The update parent is no longer running.");
        if (!UpdatePaths.Same(parent.ExecutablePath, parentExecutablePath) || parent.StartTimeUtcTicks <= 0) throw Verify("The update parent identity cannot be verified.");
        var currentVersion = ExecutableVersion(parentExecutablePath);
        CheckVersions(manifest, version, currentVersion);
        string transaction = CreateDownloadDirectory();
        try
        {
            string package = Path.Combine(transaction, UpdateProduct.PackageName);
            UpdatePaths.NoReparse(packagePath);
            if (new FileInfo(packagePath).Length != manifest.PackageSize || manifest.PackageSize <= 0 || manifest.PackageSize > UpdateProduct.MaximumPackageBytes) throw Verify("The downloaded release package length is invalid.");
            await CopyFileAsync(packagePath, package, cancellation).ConfigureAwait(false);
            var files = await ExtractAsync(manifest, package, Path.Combine(transaction, "payload"), cancellation).ConfigureAwait(false);
            ValidatePayload(manifest, version, files);
            string helper = Path.Combine(transaction, "helper", "JobFinder.exe"); Directory.CreateDirectory(Path.GetDirectoryName(helper)!);
            string parentHash = await HashAsync(parentExecutablePath, cancellation).ConfigureAwait(false);
            await CopyFileAsync(parentExecutablePath, helper, cancellation).ConfigureAwait(false);
            if (!HashEquals(parentHash, await HashAsync(helper, cancellation).ConfigureAwait(false))) throw Verify("The updater helper copy did not match the running application.");
            var plan = new UpdateApplyPlan { PlanPath = Path.Combine(transaction, "plan.json"), TransactionDirectory = transaction, TransactionToken = Path.GetFileName(transaction), InstallDirectory = installDirectory, Version = version.ToString(4), CurrentVersion = currentVersion.ToString(4), PackagePath = package, HelperPath = helper, ParentExecutablePath = parentExecutablePath, ParentExecutableSha256 = parentHash, ParentProcessId = parentPid, ParentStartTimeUtcTicks = parent.StartTimeUtcTicks, ManifestBytes = update.ManifestBytes.ToArray(), SignatureBytes = update.SignatureBytes.ToArray() };
            WriteJson(plan.PlanPath, plan);
            lock (_preparationGate) _ownedPlans.Add(plan.TransactionToken, plan);
            return plan;
        }
        catch
        {
            // This new GUID is owned by preparation; no installation or personal state has changed.
            DeleteOwnedTransaction(transaction); throw;
        }
    }

    public Process LaunchHelper(UpdateApplyPlan plan)
    {
        lock (_preparationGate)
        {
            var saved = ReadPlan(plan.PlanPath);
            if (!Equivalent(saved, plan)) throw Verify("The update plan changed before helper launch.");
            ValidatePlan(saved, requireHelperProcess: false);
            if (_processes.CurrentProcessId != saved.ParentProcessId || !UpdatePaths.Same(_processes.CurrentExecutablePath, saved.ParentExecutablePath)) throw Verify("Only the prepared application can launch its helper.");
            var parent = _processes.GetProcess(saved.ParentProcessId);
            if (parent is null || parent.StartTimeUtcTicks != saved.ParentStartTimeUtcTicks || !UpdatePaths.Same(parent.ExecutablePath, saved.ParentExecutablePath)) throw Verify("The update parent identity changed before helper launch.");
            if (HasActivity(saved.TransactionDirectory, includeHandoff: true)) throw Verify("This update has already been handed to a helper.");
            string marker = Path.Combine(saved.TransactionDirectory, "helper-launched.json");
            WriteJson(marker, new { transactionToken = saved.TransactionToken, parentProcessId = saved.ParentProcessId });
            try { return _processes.LaunchHelper(saved.HelperPath, saved.PlanPath); }
            catch
            {
                // Process.Start failure occurred before handoff; retain any evidence of helper activity.
                if (!HasActivity(saved.TransactionDirectory, includeHandoff: false))
                {
                    try { UpdatePaths.NoReparse(marker); File.Delete(marker); }
                    catch (Exception cleanupError) when (cleanupError is UpdateFailure or IOException or UnauthorizedAccessException) { }
                }
                throw;
            }
        }
    }

    public bool AbortPrepared(UpdateApplyPlan plan)
    {
        lock (_preparationGate)
        {
            if (!_ownedPlans.TryGetValue(plan.TransactionToken, out var owned) || !Equivalent(owned, plan)) throw Verify("Only the installer that prepared this transaction can abort it.");
            string transaction = UpdatePaths.Transaction(owned.TransactionToken);
            if (!UpdatePaths.Same(transaction, owned.TransactionDirectory)) throw Verify("The prepared transaction path is invalid.");
            if (!Directory.Exists(transaction)) return true;
            if (HasActivity(transaction, includeHandoff: true)) return false;
            if (!Equivalent(ReadPlan(owned.PlanPath), owned)) throw Verify("The prepared transaction changed before abort.");
            DeleteOwnedTransaction(transaction); return !Directory.Exists(transaction);
        }
    }

    private static bool Equivalent(UpdateApplyPlan left, UpdateApplyPlan right) => JsonSerializer.Serialize(left, Json) == JsonSerializer.Serialize(right, Json);
    private static bool HasActivity(string transaction, bool includeHandoff)
    {
        foreach (string marker in new[] { "apply.lock", "journal.json", "launch.json", "receipt.json", "backup", "helper-launched.json" })
        {
            if (!includeHandoff && marker == "helper-launched.json") continue;
            string path = Path.Combine(transaction, marker); UpdatePaths.NoReparse(path);
            if (File.Exists(path) || Directory.Exists(path)) return true;
        }
        return false;
    }
    public async Task<UpdateApplyResult> ApplyAsync(string planPath, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var plan = ReadPlan(planPath);
        string receipt = Path.Combine(plan.TransactionDirectory, "receipt.json");
        string backupDirectory = Path.Combine(plan.TransactionDirectory, "backup");
        var changes = new List<FileBackup>(); DataSnapshot? dataSnapshot = null; UpdateProcessIdentity? child = null;
        bool parentExited = false; bool dataRestored = true; bool rollbackSucceeded = true;
        FileStream? installLock = null, transactionLock = null;
        UpdateApplyResult Outcome(bool success, string code, bool rolledBack = false, bool attention = false) => new()
        {
            Success = success, FailureCode = code, RolledBack = rolledBack, TransactionDirectory = plan.TransactionDirectory, TransactionToken = plan.TransactionToken, InstallDirectory = plan.InstallDirectory, Version = plan.Version,
            ReceiptPath = receipt, BackupDirectory = Directory.Exists(backupDirectory) ? backupDirectory : "", StartedProcessId = child?.ProcessId, RequiresAttention = attention
        };
        try
        {
            UpdatePaths.NoReparse(receipt);
            if (File.Exists(receipt)) return Outcome(false, "AlreadyApplied");
            string transactionLockPath = Path.Combine(plan.TransactionDirectory, "apply.lock"); UpdatePaths.NoReparse(transactionLockPath);
            transactionLock = new FileStream(transactionLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (File.Exists(receipt)) return Outcome(false, "AlreadyApplied");
            var manifest = ValidatePlan(plan, requireHelperProcess: true);
            var expectedParent = new UpdateProcessIdentity(plan.ParentProcessId, plan.ParentExecutablePath, plan.ParentStartTimeUtcTicks);
            var actualParent = _processes.GetProcess(plan.ParentProcessId);
            if (actualParent is not null && !UpdateProcessHost.Matches(expectedParent, actualParent)) throw Verify("The update parent PID now belongs to a different process.");
            if (!await _processes.WaitForExitAsync(expectedParent, _parentExitTimeout, cancellation).ConfigureAwait(false)) throw new UpdateFailure(UpdateFailureKind.Timeout, "The existing application did not close in time.");
            parentExited = true;
            string lockPath = Path.Combine(plan.InstallDirectory, ".jobfinder-update.lock"); UpdatePaths.NoReparse(lockPath);
            installLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            // The parent may have closed before the helper began; disk identity is mandatory in either case.
            manifest = ValidatePlan(plan, requireHelperProcess: true);
            var version = UpdateValidation.ParseVersion(manifest.Version);
            var files = await ExtractAsync(manifest, plan.PackagePath, Path.Combine(plan.TransactionDirectory, "checked-" + Guid.NewGuid().ToString("N")), cancellation).ConfigureAwait(false);
            ValidatePayload(manifest, version, files);
            UpdatePaths.NoReparse(backupDirectory);
            if (Directory.Exists(backupDirectory)) throw Verify("This transaction already contains a previous installation snapshot.");
            Directory.CreateDirectory(Path.Combine(backupDirectory, "files"));
            var backups = new List<FileBackup>();
            foreach (var file in files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                string target = Path.Combine(plan.InstallDirectory, file.RelativePath); UpdatePaths.NoReparse(target);
                if (!UpdatePaths.Within(target, plan.InstallDirectory) || Directory.Exists(target)) throw Verify("A release file collides with the existing installation layout.");
                bool existed = File.Exists(target); string saved = Path.Combine(backupDirectory, "files", file.RelativePath); string originalHash = "";
                if (existed)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(saved)!); await CopyFileAsync(target, saved, cancellation).ConfigureAwait(false);
                    originalHash = await HashAsync(target, cancellation).ConfigureAwait(false);
                    if (!HashEquals(originalHash, await HashAsync(saved, cancellation).ConfigureAwait(false))) throw Verify("A file changed while its installation backup was created.");
                }
                backups.Add(new(file, target, existed, saved, originalHash));
            }
            dataSnapshot = await SnapshotDataAsync(Path.Combine(plan.InstallDirectory, "data"), Path.Combine(backupDirectory, "data"), cancellation).ConfigureAwait(false);
            WriteJson(Path.Combine(plan.TransactionDirectory, "journal.json"), new { phase = "prepared", transactionToken = plan.TransactionToken, files = backups.Select(backup => new { path = backup.File.RelativePath, existed = backup.Existed, originalSha256 = backup.OriginalSha256, newSha256 = backup.File.Sha256 }), dataExisted = dataSnapshot.Existed });
            foreach (var backup in backups)
            {
                await AtomicReplaceAsync(backup.File.Path, backup.TargetPath, backup.File.Sha256, plan.TransactionToken, cancellation).ConfigureAwait(false);
                changes.Add(backup);
                WriteJson(Path.Combine(plan.TransactionDirectory, "journal.json"), new { phase = "installing", transactionToken = plan.TransactionToken, changedFiles = changes.Select(change => change.File.RelativePath).ToArray() });
            }
            cancellation.ThrowIfCancellationRequested();
            child = _processes.StartApplication(plan.ParentExecutablePath, ["--updated", plan.TransactionToken]);
            if (!UpdatePaths.Same(child.ExecutablePath, plan.ParentExecutablePath) || child.StartTimeUtcTicks <= 0 || child.ProcessId <= 0 || child.ProcessId == plan.ParentProcessId || child.ProcessId == _processes.CurrentProcessId) throw new UpdateFailure(UpdateFailureKind.Startup, "The updated child process identity is invalid.");
            WriteJson(Path.Combine(plan.TransactionDirectory, "launch.json"), child);
            if (!await WaitForAcknowledgementAsync(plan, child, cancellation).ConfigureAwait(false)) throw new UpdateFailure(UpdateFailureKind.Startup, "The updated application did not confirm a usable startup.");
            var committed = Outcome(true, ""); WriteJson(receipt, committed);
            WriteJson(Path.Combine(plan.TransactionDirectory, "journal.json"), new { phase = "committed", transactionToken = plan.TransactionToken });
            return committed;
        }
        catch (Exception error) when (error is UpdateFailure or IOException or UnauthorizedAccessException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            // A second helper must never publish an outcome for the helper holding this lock.
            if (transactionLock is null) return Outcome(false, error is UpdateFailure earlyFailure ? earlyFailure.Kind.ToString() : "Busy");
            bool canRestore = true;
            if (child is not null)
            {
                try
                {
                    var observed = _processes.GetProcess(child.ProcessId);
                    canRestore = observed is null || UpdateProcessHost.Matches(child, observed) && await _processes.StopAsync(child, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception stopError) when (stopError is UpdateFailure or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { canRestore = false; }
            }
            if (canRestore)
            {
                foreach (var change in changes.AsEnumerable().Reverse())
                {
                    try
                    {
                        UpdatePaths.NoReparse(change.TargetPath);
                        if (change.Existed) await AtomicReplaceAsync(change.BackupPath, change.TargetPath, change.OriginalSha256, plan.TransactionToken, CancellationToken.None).ConfigureAwait(false);
                        else if (File.Exists(change.TargetPath)) File.Delete(change.TargetPath);
                    }
                    catch (Exception restoreError) when (restoreError is UpdateFailure or IOException or UnauthorizedAccessException) { rollbackSucceeded = false; }
                }
                if (child is not null && dataSnapshot is not null)
                {
                    try { await RestoreDataAsync(plan, backupDirectory, dataSnapshot).ConfigureAwait(false); }
                    catch (Exception restoreError) when (restoreError is UpdateFailure or IOException or UnauthorizedAccessException) { dataRestored = false; }
                }
            }
            else rollbackSucceeded = false;
            rollbackSucceeded &= dataRestored;
            string code = !rollbackSucceeded ? "RollbackFailed" : error is OperationCanceledException ? "Cancelled" : error is UpdateFailure failure ? failure.Kind.ToString() : child is not null ? "Startup" : "Install";
            var failed = Outcome(false, code, changes.Count > 0 && rollbackSucceeded, !rollbackSucceeded || parentExited && installLock is null);
            TryWriteReceipt(receipt, failed);
            if (parentExited && rollbackSucceeded && installLock is not null)
            {
                try
                {
                    if (!HashEquals(plan.ParentExecutableSha256, Hash(plan.ParentExecutablePath)) || ExecutableVersion(plan.ParentExecutablePath).ToString(4) != plan.CurrentVersion) throw Verify("The restored executable does not match the original application.");
                    var restored = _processes.StartApplication(plan.ParentExecutablePath, ["--update-failed", plan.TransactionToken]);
                    failed = failed with { RestoredProcessId = restored.ProcessId }; TryWriteReceipt(receipt, failed);
                }
                catch (Exception restartError) when (restartError is UpdateFailure or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                { failed = failed with { RequiresAttention = true }; TryWriteReceipt(receipt, failed); }
            }
            return failed;
        }
        finally { installLock?.Dispose(); transactionLock?.Dispose(); }
    }

    public static void AcknowledgeStartup(string transactionToken, Version actualVersion, string actualExePath)
    {
        var installer = new UpdateInstaller();
        string transaction = UpdatePaths.Transaction(transactionToken);
        var plan = installer.ReadPlan(Path.Combine(transaction, "plan.json"));
        var manifest = UpdateValidation.VerifyManifest(plan.ManifestBytes, plan.SignatureBytes, UpdateProduct.PublicKeyPem);
        var version = UpdateValidation.ParseVersion(manifest.Version);
        if (plan.Version != version.ToString(4) || Normalize(actualVersion) != version || !UpdatePaths.Same(actualExePath, Path.Combine(plan.InstallDirectory, "JobFinder.exe")) || !UpdatePaths.Same(Environment.ProcessPath ?? "", actualExePath) || ExecutableVersion(actualExePath) != version || !HashEquals(Hash(actualExePath), manifest.ExecutableSha256)) throw Verify("The restarted application identity does not match its signed update.");
        using var current = Process.GetCurrentProcess();
        WriteJson(Path.Combine(transaction, "startup.json"), new StartupAcknowledgement(transactionToken, version.ToString(4), current.Id, UpdatePaths.Absolute(actualExePath), current.StartTime.ToUniversalTime().Ticks));
    }

    private async Task<bool> WaitForAcknowledgementAsync(UpdateApplyPlan plan, UpdateProcessIdentity child, CancellationToken cancellation)
    {
        var started = Stopwatch.StartNew(); string path = Path.Combine(plan.TransactionDirectory, "startup.json");
        while (started.Elapsed < _startupTimeout)
        {
            cancellation.ThrowIfCancellationRequested();
            var actual = _processes.GetProcess(child.ProcessId);
            if (actual is null || !UpdateProcessHost.Matches(child, actual)) return false;
            UpdatePaths.NoReparse(path);
            if (File.Exists(path) && new FileInfo(path).Length <= 8192)
            {
                try
                {
                    var ack = JsonSerializer.Deserialize<StartupAcknowledgement>(File.ReadAllBytes(path), Json);
                    if (ack is not null && ack.TransactionToken == plan.TransactionToken && ack.Version == plan.Version && ack.ProcessId == child.ProcessId && ack.StartTimeUtcTicks == child.StartTimeUtcTicks && UpdatePaths.Same(ack.ExecutablePath, child.ExecutablePath) && ExecutableVersion(child.ExecutablePath).ToString(4) == plan.Version)
                    {
                        var manifest = UpdateValidation.VerifyManifest(plan.ManifestBytes, plan.SignatureBytes, _publicKey);
                        if (HashEquals(Hash(child.ExecutablePath), manifest.ExecutableSha256)) return true;
                    }
                }
                catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException) { }
            }
            var remaining = _startupTimeout - started.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            await Task.Delay(remaining < TimeSpan.FromMilliseconds(100) ? remaining : TimeSpan.FromMilliseconds(100), cancellation).ConfigureAwait(false);
        }
        return false;
    }

    private static async Task<DataSnapshot> SnapshotDataAsync(string dataDirectory, string destination, CancellationToken cancellation)
    {
        UpdatePaths.NoReparse(dataDirectory); UpdatePaths.NoReparse(destination);
        bool existed = Directory.Exists(dataDirectory);
        if (File.Exists(dataDirectory)) throw Verify("Portable data is not a directory.");
        var snapshot = new List<DataFile>();
        var directories = UpdatePaths.Directories(dataDirectory).Select(directory => Path.GetRelativePath(dataDirectory, directory)).ToList();
        Directory.CreateDirectory(destination);
        foreach (string directory in directories) Directory.CreateDirectory(Path.Combine(destination, directory));
        foreach (string source in UpdatePaths.Files(dataDirectory))
        {
            string relative = Path.GetRelativePath(dataDirectory, source); string target = Path.Combine(destination, relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await CopyFileAsync(source, target, cancellation).ConfigureAwait(false);
            string hash = await HashAsync(source, cancellation).ConfigureAwait(false);
            if (!HashEquals(hash, await HashAsync(target, cancellation).ConfigureAwait(false))) throw Verify("Portable data changed while its snapshot was created.");
            snapshot.Add(new(relative, hash));
        }
        return new(existed, snapshot, directories);
    }

    private static async Task RestoreDataAsync(UpdateApplyPlan plan, string backupDirectory, DataSnapshot snapshot)
    {
        string data = Path.Combine(plan.InstallDirectory, "data");
        await SnapshotDataAsync(data, Path.Combine(backupDirectory, "failed-data"), CancellationToken.None).ConfigureAwait(false);
        var previous = snapshot.Files.Select(file => file.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string current in UpdatePaths.Files(data))
        {
            if (previous.Contains(Path.GetRelativePath(data, current))) continue;
            UpdatePaths.NoReparse(current); if (!UpdatePaths.Within(current, data)) throw Verify("Data rollback escaped its root."); File.Delete(current);
        }
        foreach (var file in snapshot.Files)
            await AtomicReplaceAsync(Path.Combine(backupDirectory, "data", file.RelativePath), Path.Combine(data, file.RelativePath), file.Sha256, plan.TransactionToken, CancellationToken.None).ConfigureAwait(false);
        var previousDirectories = snapshot.Directories.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in UpdatePaths.Directories(data).OrderByDescending(directory => directory.Length))
        {
            if (!previousDirectories.Contains(Path.GetRelativePath(data, directory)) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
        foreach (string directory in snapshot.Directories) { string restored = Path.Combine(data, directory); UpdatePaths.NoReparse(restored); Directory.CreateDirectory(restored); }
        if (snapshot.Existed) Directory.CreateDirectory(data);
        else if (Directory.Exists(data) && !Directory.EnumerateFileSystemEntries(data).Any()) Directory.Delete(data);
    }

    private static async Task AtomicReplaceAsync(string source, string destination, string expectedSha256, string token, CancellationToken cancellation)
    {
        UpdatePaths.NoReparse(source); UpdatePaths.NoReparse(destination);
        string parent = Path.GetDirectoryName(destination)!; Directory.CreateDirectory(parent); UpdatePaths.NoReparse(parent);
        string temporary = Path.Combine(parent, ".jobfinder-new-" + token + "-" + Guid.NewGuid().ToString("N"));
        try
        {
            await CopyFileAsync(source, temporary, cancellation).ConfigureAwait(false);
            if (!HashEquals(expectedSha256, await HashAsync(temporary, cancellation).ConfigureAwait(false))) throw Verify("A staged file changed before installation.");
            cancellation.ThrowIfCancellationRequested(); UpdatePaths.NoReparse(destination);
            if (File.Exists(destination)) File.Replace(temporary, destination, null, ignoreMetadataErrors: true); else File.Move(temporary, destination);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void TryWriteReceipt(string path, UpdateApplyResult result)
    {
        try { WriteJson(path, result); }
        catch (Exception error) when (error is UpdateFailure or IOException or UnauthorizedAccessException) { }
    }

    private UpdateApplyPlan ReadPlan(string path)
    {
        path = UpdatePaths.Absolute(path); UpdatePaths.NoReparse(path);
        string directory = Path.GetDirectoryName(path)!;
        string token = Path.GetFileName(directory);
        if (!UpdatePaths.Same(directory, UpdatePaths.Transaction(token)) || !UpdatePaths.Same(path, Path.Combine(directory, "plan.json"))) throw Verify("The helper only accepts its own private staged plan.");
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw Verify("The update plan exceeds its limit.");
        try
        {
            var plan = JsonSerializer.Deserialize<UpdateApplyPlan>(File.ReadAllBytes(path), Json) ?? throw Verify("The update plan is empty.");
            if (plan.SchemaVersion != 1 || !UpdatePaths.Same(plan.PlanPath, path) || !UpdatePaths.Same(plan.TransactionDirectory, directory) || plan.TransactionToken != token) throw Verify("The update plan identity is invalid.");
            return plan;
        }
        catch (JsonException error) { throw new UpdateFailure(UpdateFailureKind.Format, "The update plan is invalid.", error); }
    }

    private UpdateManifest ValidatePlan(UpdateApplyPlan plan, bool requireHelperProcess)
    {
        var manifest = UpdateValidation.VerifyManifest(plan.ManifestBytes, plan.SignatureBytes, _publicKey);
        var version = UpdateValidation.ParseVersion(manifest.Version);
        if (plan.Version != version.ToString(4) || plan.ParentProcessId <= 0 || plan.ParentStartTimeUtcTicks <= 0 || plan.ParentProcessId == _processes.CurrentProcessId && requireHelperProcess) throw Verify("The update plan process or version is invalid.");
        UpdatePaths.Install(plan.InstallDirectory);
        string transaction = UpdatePaths.Transaction(plan.TransactionToken);
        if (!UpdatePaths.Same(plan.TransactionDirectory, transaction) || !UpdatePaths.Same(plan.PackagePath, Path.Combine(transaction, UpdateProduct.PackageName)) || !UpdatePaths.Same(plan.HelperPath, Path.Combine(transaction, "helper", "JobFinder.exe")) || !UpdatePaths.Same(plan.ParentExecutablePath, Path.Combine(plan.InstallDirectory, "JobFinder.exe"))) throw Verify("The update plan contains an unexpected path.");
        UpdatePaths.NoReparse(plan.HelperPath); UpdatePaths.NoReparse(plan.ParentExecutablePath); UpdatePaths.NoReparse(plan.PackagePath);
        if (requireHelperProcess && !UpdatePaths.Same(_processes.CurrentExecutablePath, plan.HelperPath)) throw Verify("The helper must run from this transaction's private executable copy.");
        if (!HashEquals(plan.ParentExecutableSha256, Hash(plan.HelperPath)) || !HashEquals(plan.ParentExecutableSha256, Hash(plan.ParentExecutablePath))) throw Verify("The current application or helper has changed since preparation.");
        var current = ExecutableVersion(plan.ParentExecutablePath);
        if (plan.CurrentVersion != current.ToString(4) || ExecutableVersion(plan.HelperPath) != current) throw Verify("The current application version changed since preparation.");
        CheckVersions(manifest, version, current); return manifest;
    }

    private static void CheckVersions(UpdateManifest manifest, Version target, Version current)
    {
        if (target <= current) throw Verify("Updates cannot reinstall or downgrade the running version.");
        if (UpdateValidation.ParseVersion(manifest.MinimumUpdaterVersion) > current) throw Verify("This release requires a newer updater.");
    }

    private static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));

    private static Version ExecutableVersion(string path)
    {
        UpdatePaths.NoReparse(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[64];
        if (stream.Read(header) != 64 || header[0] != 'M' || header[1] != 'Z') throw Verify("The update executable is not a valid Windows PE file.");
        int offset = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header[60..]);
        if (offset < 64 || offset > stream.Length - 6) throw Verify("The update executable PE header is invalid.");
        stream.Position = offset; Span<byte> signature = stackalloc byte[6]; stream.ReadExactly(signature);
        if (signature[0] != 'P' || signature[1] != 'E' || signature[2] != 0 || signature[3] != 0 || System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(signature[4..]) != 0x8664) throw Verify("The update executable must be a Windows x64 PE file.");
        var info = FileVersionInfo.GetVersionInfo(path);
        if (info.FileMajorPart < 0 || info.FileMinorPart < 0 || info.FileBuildPart < 0 || info.FilePrivatePart < 0 || string.IsNullOrEmpty(info.FileVersion)) throw Verify("The update executable has no valid version.");
        return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
    }

    private static void ValidatePayload(UpdateManifest manifest, Version version, List<PackageFile> files)
    {
        var executable = files.SingleOrDefault(file => file.RelativePath.Equals("JobFinder.exe", StringComparison.OrdinalIgnoreCase)) ?? throw Verify("The release package has no executable.");
        if (!HashEquals(executable.Sha256, manifest.ExecutableSha256) || ExecutableVersion(executable.Path) != version) throw Verify("The release executable hash or PE version does not match its signed manifest.");
    }

    private static async Task<List<PackageFile>> ExtractAsync(UpdateManifest manifest, string packagePath, string destination, CancellationToken cancellation)
    {
        UpdatePaths.NoReparse(packagePath); UpdatePaths.NoReparse(destination);
        if (Directory.Exists(destination)) throw Verify("The update extraction directory already exists.");
        using var stream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != manifest.PackageSize || stream.Length <= 0 || stream.Length > UpdateProduct.MaximumPackageBytes) throw Verify("The release package length does not match its signed manifest.");
        byte[] digest = await SHA256.HashDataAsync(stream, cancellation).ConfigureAwait(false);
        if (!HashEquals(Convert.ToHexString(digest), manifest.PackageSha256)) throw Verify("The release package digest does not match its signed manifest.");
        stream.Position = 0;
        try
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count > UpdateProduct.MaximumEntries) throw Verify("The release package has too many entries.");
            var explicitNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var layout = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var entries = new List<(ZipArchiveEntry Entry, string Name, bool Directory)>();
            long expanded = 0;
            foreach (var entry in archive.Entries)
            {
                cancellation.ThrowIfCancellationRequested();
                string name = UpdatePaths.ZipName(entry.FullName); UpdatePaths.AllowedPackagePath(name);
                bool directory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
                int type = (entry.ExternalAttributes >> 16) & 0xF000;
                if (type != 0 && type != 0x8000 && type != 0x4000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 || directory && entry.Length != 0 || type == 0x4000 && !directory) throw Verify("The release package contains a link or invalid file type.");
                if (!explicitNames.Add(name) || layout.TryGetValue(name, out bool existingDirectory) && (!existingDirectory || !directory)) throw Verify("The release package has duplicate or colliding Windows filenames.");
                layout[name] = directory;
                var parts = name.Split('/');
                for (int count = 1; count < parts.Length; count++)
                {
                    string ancestor = string.Join('/', parts.Take(count));
                    if (layout.TryGetValue(ancestor, out bool ancestorDirectory) && !ancestorDirectory) throw Verify("The release package has a file-directory collision.");
                    layout[ancestor] = true;
                }
                expanded = checked(expanded + entry.Length);
                if (entry.Length < 0 || expanded > UpdateProduct.MaximumExpandedBytes) throw Verify("The expanded release package exceeds its limit.");
                entries.Add((entry, name, directory));
            }
            Directory.CreateDirectory(destination); var result = new List<PackageFile>();
            byte[] buffer = new byte[65536];
            foreach (var (entry, name, directory) in entries)
            {
                string target = Path.Combine(destination, name.Replace('/', Path.DirectorySeparatorChar));
                if (!UpdatePaths.Within(target, destination)) throw Verify("A release package path escaped its extraction directory.");
                UpdatePaths.NoReparse(target);
                if (directory) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var source = entry.Open();
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    long written = 0; int read;
                    while ((read = await source.ReadAsync(buffer, cancellation).ConfigureAwait(false)) != 0)
                    {
                        written += read; if (written > entry.Length || written > UpdateProduct.MaximumExpandedBytes) throw Verify("A release package entry expanded beyond its declared limit.");
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
                    }
                    if (written != entry.Length) throw Verify("A release package entry has an invalid expanded length.");
                    output.Flush(true);
                }
                result.Add(new(name.Replace('/', Path.DirectorySeparatorChar), target, await HashAsync(target, cancellation).ConfigureAwait(false), entry.Length));
            }
            return result;
        }
        catch (InvalidDataException error) { throw new UpdateFailure(UpdateFailureKind.Format, "The release package is not a valid ZIP archive.", error); }
        catch (OverflowException error) { throw new UpdateFailure(UpdateFailureKind.Verification, "The expanded release package exceeds its limit.", error); }
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellation)
    {
        source = UpdatePaths.Absolute(source); destination = UpdatePaths.Absolute(destination); UpdatePaths.NoReparse(source); UpdatePaths.NoReparse(destination);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await input.CopyToAsync(output, cancellation).ConfigureAwait(false); output.Flush(true);
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellation)
    {
        UpdatePaths.NoReparse(path); using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation).ConfigureAwait(false));
    }

    private static string Hash(string path)
    {
        UpdatePaths.NoReparse(path); using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static bool HashEquals(string left, string right) => left.Length == 64 && right.Length == 64 && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static UpdateFailure Verify(string message) => new(UpdateFailureKind.Verification, message);

    private static void WriteJson<T>(string path, T value)
    {
        UpdatePaths.NoReparse(path);
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { JsonSerializer.Serialize(stream, value, Json); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void DeleteOwnedTransaction(string transaction)
    {
        try
        {
            string expected = UpdatePaths.Transaction(Path.GetFileName(transaction));
            if (!UpdatePaths.Same(transaction, expected)) return;
            UpdatePaths.Files(transaction); Directory.Delete(transaction, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or UpdateFailure) { }
    }
}
