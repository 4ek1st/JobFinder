using System.Diagnostics;
using System.Buffers.Binary;
using System.ComponentModel;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JobFinder.Core;
using Microsoft.Win32.SafeHandles;

public static class UpdateInstallerVerification
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static async Task Run(string fixtures, Action<bool, string> check)
    {
        fixtures = Path.GetFullPath(fixtures); Directory.CreateDirectory(fixtures);
        string? executable = Environment.GetEnvironmentVariable("JOBFINDER_UPDATE_TEST_EXE");
        string? oldExecutable = Environment.GetEnvironmentVariable("JOBFINDER_UPDATE_TEST_OLD_EXE");
        var generatedFixtures = await BuildPeFixtures(fixtures);
        if (executable is null && oldExecutable is null) (oldExecutable, executable) = generatedFixtures;
        if (executable is null || oldExecutable is null) throw new InvalidOperationException("Provide both JOBFINDER_UPDATE_TEST_EXE and JOBFINDER_UPDATE_TEST_OLD_EXE, or leave both unset to compile the fixtures.");
        var targetVersion = PeVersion(executable);
        Directory.CreateDirectory(fixtures);
        using var rsa = RSA.Create(3072);
        var publicKey = rsa.ExportSubjectPublicKeyInfoPem();
        var caseRoot = Path.Combine(fixtures, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(caseRoot);
        var install = Path.Combine(caseRoot, "install"); Directory.CreateDirectory(install);
        File.Copy(oldExecutable, Path.Combine(install, "JobFinder.exe"));
        Directory.CreateDirectory(Path.Combine(install, "data"));
        File.WriteAllText(Path.Combine(install, "data", "profile.json"), "{\"favorites\":[\"preserve me\"]}");
        File.WriteAllText(Path.Combine(install, "README.md"), "old program documentation");
        File.WriteAllText(Path.Combine(install, "personal-note.txt"), "unknown local file");
        var host = new FixtureProcesses(Path.Combine(install, "JobFinder.exe"));
        var installer = new UpdateInstaller(publicKey, host, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200));
        var package = Path.Combine(caseRoot, "valid.zip");
        WritePackage(package, [("JobFinder.exe", File.ReadAllBytes(executable), 0), ("README.md", Encoding.UTF8.GetBytes("new program documentation"), 0), ("verification/new.txt", Encoding.UTF8.GetBytes("new release check"), 0)]);
        var update = Sign(package, targetVersion, File.ReadAllBytes(executable), rsa);
        async Task Reject(string name, params (string Name, byte[] Bytes, int Attributes)[] entries)
        {
            string file = Path.Combine(caseRoot, Guid.NewGuid().ToString("N") + ".zip");
            WritePackage(file, [("JobFinder.exe", new byte[64], 0), .. entries]);
            var rejected = false;
            try { await installer.PrepareAsync(Sign(file, targetVersion, File.ReadAllBytes(executable), rsa), file, install, host.CurrentProcessId, host.CurrentExecutablePath); }
            catch (UpdateFailure error) { rejected = error.Kind is UpdateFailureKind.Format or UpdateFailureKind.Verification or UpdateFailureKind.Install; }
            catch (NotImplementedException) { }
            check(rejected, name);
        }
        byte[] text = Encoding.UTF8.GetBytes("untrusted");
        await Reject("ZIP traversal cannot escape staging", ("../outside.txt", text, 0));
        await Reject("absolute ZIP paths cannot write outside staging", ("C:/outside.txt", text, 0));
        await Reject("Windows backslash traversal cannot escape staging", ("verification\\..\\outside.txt", text, 0));
        await Reject("case duplicate Windows executable names are rejected", ("jobfinder.EXE", text, 0));
        await Reject("trailing-dot Windows filename alias is rejected", ("README.md.", text, 0));
        await Reject("alternate data streams are rejected", ("README.md:payload", text, 0));
        await Reject("DOS short-name aliases are rejected", ("README~1.md", text, 0));
        await Reject("reserved Windows device names are rejected", ("verification/CON.txt", text, 0));
        await Reject("unexpected program roots are rejected", ("other.exe", text, 0));
        await Reject("release cannot replace portable data", ("data/profile.json", text, 0));
        await Reject("nested profiles are rejected under allowed documentation roots", ("verification/profile.json", text, 0));
        await Reject("nested helper plans are rejected", ("verification/plan.json", text, 0));
        await Reject("nested private keys are rejected", ("verification/private-key.pem", text, 0));
        await Reject("ZIP symlink entries are rejected", ("verification/link", text, unchecked((int)0xA1FF0000)));
        await Reject("ZIP Windows reparse attributes are rejected", ("README.md", text, (int)FileAttributes.ReparsePoint));
        await Reject("file-directory collisions are rejected", ("verification", text, 0), ("verification/report.txt", text, 0));
        UpdateApplyPlan? plan = null;
        try { plan = await installer.PrepareAsync(update, package, install, host.CurrentProcessId, host.CurrentExecutablePath); }
        catch (NotImplementedException) { check(false, "valid signed package prepares a private transaction"); }
        check(plan is not null && File.Exists(plan.PlanPath) && File.Exists(plan.HelperPath), "valid signed package prepares a private transaction");
        check(plan!.TransactionDirectory.StartsWith(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JobFinder", "Updates", "staging") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Guid.TryParseExact(plan.TransactionToken, "N", out _), "transaction lives under its own private GUID staging root");
        var abandoned = await installer.PrepareAsync(update, package, install, host.CurrentProcessId, host.CurrentExecutablePath);
        check(installer.AbortPrepared(abandoned) && !Directory.Exists(abandoned.TransactionDirectory) && File.Exists(package), "owner abort removes only the unhanded prepared transaction and preserves input package");
        var foreignOwner = await installer.PrepareAsync(update, package, install, host.CurrentProcessId, host.CurrentExecutablePath);
        bool foreignAbortRefused = false;
        try { new UpdateInstaller(publicKey, host).AbortPrepared(foreignOwner); }
        catch (UpdateFailure) { foreignAbortRefused = true; }
        check(foreignAbortRefused && Directory.Exists(foreignOwner.TransactionDirectory), "different installer cannot delete another prepared transaction");
        installer.AbortPrepared(foreignOwner);
        var marked = await installer.PrepareAsync(update, package, install, host.CurrentProcessId, host.CurrentExecutablePath);
        File.WriteAllText(Path.Combine(marked.TransactionDirectory, "helper-launched.json"), "{}");
        check(!installer.AbortPrepared(marked) && Directory.Exists(marked.TransactionDirectory), "abort cannot delete a transaction already handed to a helper");
        File.Delete(Path.Combine(marked.TransactionDirectory, "helper-launched.json")); installer.AbortPrepared(marked);
        var launchAbort = await installer.PrepareAsync(update, package, install, host.CurrentProcessId, host.CurrentExecutablePath);
        host.FailHelperLaunch = true; bool helperLaunchFailed = false;
        try { installer.LaunchHelper(launchAbort); }
        catch (IOException) { helperLaunchFailed = true; }
        check(helperLaunchFailed && installer.AbortPrepared(launchAbort) && !Directory.Exists(launchAbort.TransactionDirectory), "failed native helper launch leaves an owned transaction safe to abort"); host.FailHelperLaunch = false;
        check(Sha(plan.HelperPath) == Sha(Path.Combine(install, "JobFinder.exe")), "prepared helper is an exact copy of the running executable");
        check(File.ReadAllText(Path.Combine(install, "data", "profile.json")) == "{\"favorites\":[\"preserve me\"]}" && File.ReadAllText(Path.Combine(install, "personal-note.txt")) == "unknown local file", "preparation leaves personal data and unknown local files untouched");
        async Task<bool> RefusesPreparation(AvailableUpdate candidate, string file)
        {
            try { await installer.PrepareAsync(candidate, file, install, host.CurrentProcessId, host.CurrentExecutablePath); return false; }
            catch (UpdateFailure) { return true; }
        }
        string tampered = Path.Combine(caseRoot, "tampered.zip"); File.Copy(package, tampered);
        using (var altered = new FileStream(tampered, FileMode.Open, FileAccess.ReadWrite)) { altered.Position = altered.Length / 2; int value = altered.ReadByte(); altered.Position--; altered.WriteByte((byte)(value ^ 1)); }
        check(await RefusesPreparation(update, tampered), "tampered signed package fails before installation");
        string longer = Path.Combine(caseRoot, "longer.zip"); File.Copy(package, longer); using (var append = new FileStream(longer, FileMode.Append)) append.WriteByte(0);
        check(await RefusesPreparation(update, longer), "signed package length is checked independently of ZIP readability");
        var invalidSignature = update.SignatureBytes.ToArray(); invalidSignature[0] ^= 1;
        check(await RefusesPreparation(update with { SignatureBytes = invalidSignature }, package), "preparation independently verifies the manifest signature");
        var wrongVersion = new Version(targetVersion.Major, targetVersion.Minor, targetVersion.Build + 1, 0);
        check(await RefusesPreparation(Sign(package, wrongVersion, File.ReadAllBytes(executable), rsa), package), "signed target version must equal actual executable PE version");
        check(await RefusesPreparation(Sign(package, targetVersion, Encoding.UTF8.GetBytes("wrong executable digest"), rsa), package), "signed executable SHA is checked after package verification");
        check(await RefusesPreparation(update with { Version = wrongVersion }, package), "available update cannot substitute a version for signed metadata");
        var oldVersion = PeVersion(oldExecutable);
        check(await RefusesPreparation(Sign(package, oldVersion, File.ReadAllBytes(executable), rsa), package), "release cannot reinstall the currently installed version");
        var backwardsVersion = new Version(oldVersion.Major, oldVersion.Minor, Math.Max(0, oldVersion.Build - 1), 0);
        check(await RefusesPreparation(Sign(package, backwardsVersion, File.ReadAllBytes(executable), rsa), package), "release cannot downgrade the installed version");
        string tooMany = Path.Combine(caseRoot, "many-entries.zip");
        WritePackage(tooMany, [("JobFinder.exe", File.ReadAllBytes(executable), 0), .. Enumerable.Range(0, UpdateProduct.MaximumEntries).Select(index => ($"verification/check-{index}.txt", Array.Empty<byte>(), 0))]);
        check(await RefusesPreparation(Sign(tooMany, targetVersion, File.ReadAllBytes(executable), rsa), tooMany), "entry count limit rejects an otherwise signed release package");
        string oversizedExpansion = Path.Combine(caseRoot, "expanded-too-large.zip");
        WritePackage(oversizedExpansion, [("JobFinder.exe", File.ReadAllBytes(executable), 0)]);
        using (var archive = ZipFile.Open(oversizedExpansion, ZipArchiveMode.Update))
        using (var output = archive.CreateEntry("verification/large.dat", CompressionLevel.Fastest).Open())
        {
            byte[] block = new byte[65536];
            for (long written = 0; written <= UpdateProduct.MaximumExpandedBytes; written += block.Length) output.Write(block);
        }
        check(await RefusesPreparation(Sign(oversizedExpansion, targetVersion, File.ReadAllBytes(executable), rsa), oversizedExpansion), "expanded size limit rejects a signed decompression bomb before extraction");
        var canceled = new CancellationTokenSource(); canceled.Cancel(); bool cancelObserved = false;
        try { await installer.PrepareAsync(update, package, install, host.CurrentProcessId, host.CurrentExecutablePath, canceled.Token); }
        catch (OperationCanceledException) { cancelObserved = true; }
        check(cancelObserved, "preparation honors cancellation before filesystem mutation");

        var success = await Prepared("success", package, update, oldExecutable, publicKey, fixtures);
        UpdateApplyResult? result = null;
        try { result = await success.Installer.ApplyAsync(success.Plan.PlanPath); }
        catch (NotImplementedException) { check(false, "transaction applies and accepts exact child startup acknowledgement"); }
        check(result?.Success == true, "transaction applies and accepts exact child startup acknowledgement");
        check(Sha(Path.Combine(success.Install, "JobFinder.exe")) == Sha(executable) && File.ReadAllText(Path.Combine(success.Install, "README.md")) == "new program documentation", "successful transaction installs signed program and documentation");
        check(File.ReadAllText(Path.Combine(success.Install, "data", "profile.json")) == "original profile" && File.ReadAllText(Path.Combine(success.Install, "personal-note.txt")) == "private extra", "successful transaction preserves profile and unknown local files");
        check(result!.BackupDirectory.Length > 0 && File.ReadAllText(Path.Combine(result.BackupDirectory, "data", "profile.json")) == "original profile" && Sha(Path.Combine(result.BackupDirectory, "files", "JobFinder.exe")) == Sha(oldExecutable), "transaction retains program and data snapshots outside installation");
        check(File.Exists(result.ReceiptPath) && !File.ReadAllText(result.ReceiptPath).Contains("original profile"), "private outcome receipt contains no profile content");

        var absentParent = await Prepared("already-exited-parent", package, update, oldExecutable, publicKey, fixtures); absentParent.Host.Parent = null;
        var absentParentResult = await absentParent.Installer.ApplyAsync(absentParent.Plan.PlanPath);
        check(absentParentResult.Success && Sha(Path.Combine(absentParent.Install, "JobFinder.exe")) == Sha(executable), "already exited parent still uses helper and installed-binary identity checks");

        var parentTimeout = await Prepared("parent-timeout", package, update, oldExecutable, publicKey, fixtures); parentTimeout.Host.ParentExits = false;
        var timeoutResult = await parentTimeout.Installer.ApplyAsync(parentTimeout.Plan.PlanPath);
        check(!timeoutResult.Success && Sha(Path.Combine(parentTimeout.Install, "JobFinder.exe")) == Sha(oldExecutable) && parentTimeout.Host.Child is null, "parent timeout never replaces files or starts a child");
        var wrongParent = await Prepared("wrong-parent", package, update, oldExecutable, publicKey, fixtures); wrongParent.Host.Parent = wrongParent.Host.Parent! with { StartTimeUtcTicks = wrongParent.Plan.ParentStartTimeUtcTicks + 1 };
        check(await RefusesApply(wrongParent.Installer, wrongParent.Plan.PlanPath) && Sha(Path.Combine(wrongParent.Install, "JobFinder.exe")) == Sha(oldExecutable), "reused parent PID cannot authorize installation");
        var wrongHelper = await Prepared("wrong-helper", package, update, oldExecutable, publicKey, fixtures); wrongHelper.Host.CurrentExecutablePath = Path.Combine(wrongHelper.Install, "JobFinder.exe");
        check(await RefusesApply(wrongHelper.Installer, wrongHelper.Plan.PlanPath), "helper must be executing from its own private copy");

        var startupFailure = await Prepared("startup-failure", package, update, oldExecutable, publicKey, fixtures); startupFailure.Host.ChildExits = true;
        startupFailure.Host.OnStart = () => { File.WriteAllText(Path.Combine(startupFailure.Install, "data", "profile.json"), "failed-version profile"); File.WriteAllText(Path.Combine(startupFailure.Install, "data", "new.txt"), "failed-version new state"); Directory.Delete(Path.Combine(startupFailure.Install, "data", "empty")); };
        var failedResult = await startupFailure.Installer.ApplyAsync(startupFailure.Plan.PlanPath);
        check(!failedResult.Success && failedResult.RolledBack && Sha(Path.Combine(startupFailure.Install, "JobFinder.exe")) == Sha(oldExecutable) && File.ReadAllText(Path.Combine(startupFailure.Install, "README.md")) == "old docs", "failed child rolls back every changed program file");
        check(failedResult.RestoredProcessId is not null && startupFailure.Host.GetProcess(failedResult.RestoredProcessId.Value)?.ExecutablePath == Path.Combine(startupFailure.Install, "JobFinder.exe"), "rollback restarts the restored old executable for recovery");
        check(File.ReadAllText(Path.Combine(startupFailure.Install, "data", "profile.json")) == "original profile" && !File.Exists(Path.Combine(startupFailure.Install, "data", "new.txt")), "failed child restores the original portable data snapshot");
        check(Directory.Exists(Path.Combine(startupFailure.Install, "data", "empty")), "rollback restores original empty data directories");
        check(!File.Exists(Path.Combine(startupFailure.Install, "verification", "new.txt")), "rollback removes program files introduced only by the failed version");
        check(File.ReadAllText(Path.Combine(failedResult.BackupDirectory, "failed-data", "profile.json")) == "failed-version profile" && File.ReadAllText(Path.Combine(failedResult.BackupDirectory, "failed-data", "new.txt")) == "failed-version new state", "failed child data is preserved before restoring original data");
        check(File.ReadAllText(Path.Combine(startupFailure.Install, "personal-note.txt")) == "private extra", "rollback leaves unrelated local files untouched");

        var launchFailure = await Prepared("launch-failure", package, update, oldExecutable, publicKey, fixtures); launchFailure.Host.FailStart = true;
        var launchResult = await launchFailure.Installer.ApplyAsync(launchFailure.Plan.PlanPath);
        check(!launchResult.Success && launchResult.RolledBack && Sha(Path.Combine(launchFailure.Install, "JobFinder.exe")) == Sha(oldExecutable), "native child start failure rolls back the installed executable");
        var wrongAck = await Prepared("wrong-ack", package, update, oldExecutable, publicKey, fixtures); wrongAck.Host.WrongAcknowledgement = true;
        var wrongAckResult = await wrongAck.Installer.ApplyAsync(wrongAck.Plan.PlanPath);
        check(!wrongAckResult.Success && wrongAckResult.RolledBack && Sha(Path.Combine(wrongAck.Install, "JobFinder.exe")) == Sha(oldExecutable), "startup acknowledgement from a different PID cannot commit update");

        var partial = await Prepared("partial-copy", package, update, oldExecutable, publicKey, fixtures);
        using (var locked = new FileStream(Path.Combine(partial.Install, "README.md"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var partialResult = await partial.Installer.ApplyAsync(partial.Plan.PlanPath);
            check(!partialResult.Success && partialResult.RolledBack && Sha(Path.Combine(partial.Install, "JobFinder.exe")) == Sha(oldExecutable) && File.ReadAllText(Path.Combine(partial.Install, "README.md")) == "old docs", "locked later destination rolls back the earlier atomic replacement");
            check(partial.Host.Child is null, "partial installation never launches new application");
        }
        var installLocked = await Prepared("installation-lock", package, update, oldExecutable, publicKey, fixtures);
        using (var locked = new FileStream(Path.Combine(installLocked.Install, ".jobfinder-update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var busy = await installLocked.Installer.ApplyAsync(installLocked.Plan.PlanPath);
            check(!busy.Success && Sha(Path.Combine(installLocked.Install, "JobFinder.exe")) == Sha(oldExecutable), "exclusive installation lock prevents concurrent update replacement");
            check(busy.RequiresAttention && busy.RestoredProcessId is null && installLocked.Host.RestoredChild is null, "busy installation does not launch a competing old process");
        }
        var transactionLocked = await Prepared("transaction-lock", package, update, oldExecutable, publicKey, fixtures);
        using (var locked = new FileStream(Path.Combine(transactionLocked.Plan.TransactionDirectory, "apply.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            check(await RefusesApply(transactionLocked.Installer, transactionLocked.Plan.PlanPath) && !File.Exists(Path.Combine(transactionLocked.Plan.TransactionDirectory, "receipt.json")), "second helper cannot overwrite the active transaction receipt");
        var reusedChild = await Prepared("reused-child", package, update, oldExecutable, publicKey, fixtures); reusedChild.Host.ChildIdentityChanges = true;
        var reusedChildResult = await reusedChild.Installer.ApplyAsync(reusedChild.Plan.PlanPath);
        check(!reusedChildResult.Success && reusedChildResult.RequiresAttention && !reusedChildResult.RolledBack && !reusedChild.Host.ChildExits && reusedChild.Host.RestoredChild is null && Sha(Path.Combine(reusedChildResult.BackupDirectory, "files", "JobFinder.exe")) == Sha(oldExecutable), "reused child PID prevents termination and unsafe rollback while retaining original backup");

        var foreign = await Prepared("foreign-plan", package, update, oldExecutable, publicKey, fixtures); string foreignPlan = Path.Combine(fixtures, "foreign-plan.json"); File.Copy(foreign.Plan.PlanPath, foreignPlan, true);
        check(await RefusesApply(foreign.Installer, foreignPlan), "helper rejects plans outside private GUID staging");
        var hostile = await Prepared("hostile-plan", package, update, oldExecutable, publicKey, fixtures);
        File.WriteAllText(hostile.Plan.PlanPath, JsonSerializer.Serialize(hostile.Plan with { InstallDirectory = Path.GetPathRoot(hostile.Install)! }, Json));
        check(await RefusesApply(hostile.Installer, hostile.Plan.PlanPath) && Sha(Path.Combine(hostile.Install, "JobFinder.exe")) == Sha(oldExecutable), "forged plan cannot select an installation drive root");
        var changedPackage = await Prepared("tampered-after-prepare", package, update, oldExecutable, publicKey, fixtures);
        using (var altered = new FileStream(changedPackage.Plan.PackagePath, FileMode.Open, FileAccess.ReadWrite)) { altered.Position = 50; int value = altered.ReadByte(); altered.Position--; altered.WriteByte((byte)(value ^ 1)); }
        check(await RefusesApply(changedPackage.Installer, changedPackage.Plan.PlanPath) && Sha(Path.Combine(changedPackage.Install, "JobFinder.exe")) == Sha(oldExecutable), "helper independently checks staged package tampering after preparation");
        var changedHelper = await Prepared("helper-tampering", package, update, oldExecutable, publicKey, fixtures);
        using (var append = new FileStream(changedHelper.Plan.HelperPath, FileMode.Append)) append.WriteByte(0);
        check(await RefusesApply(changedHelper.Installer, changedHelper.Plan.PlanPath), "helper copy digest is rechecked before installation");
        var changedParent = await Prepared("parent-tampering", package, update, oldExecutable, publicKey, fixtures); changedParent.Host.Parent = null;
        using (var append = new FileStream(Path.Combine(changedParent.Install, "JobFinder.exe"), FileMode.Append)) append.WriteByte(0);
        check(await RefusesApply(changedParent.Installer, changedParent.Plan.PlanPath), "missing parent PID does not skip installed-binary integrity check");
        bool falseAckRejected = false;
        try { UpdateInstaller.AcknowledgeStartup(plan.TransactionToken, targetVersion, Path.Combine(install, "JobFinder.exe")); }
        catch (UpdateFailure) { falseAckRejected = true; }
        check(falseAckRejected && !File.Exists(Path.Combine(plan.TransactionDirectory, "startup.json")), "untrusted transaction cannot create a production startup acknowledgement");
        check(await RefusesApply(success.Installer, success.Plan.PlanPath), "completed transaction cannot be replayed against an installation");
        check(!success.Installer.AbortPrepared(success.Plan) && File.Exists(success.Plan.PlanPath), "abort cannot delete installation backups or a completed receipt");

        var reparse = await Prepared("reparse-data", package, update, oldExecutable, publicKey, fixtures);
        string linkedData = Path.Combine(reparse.Install, "data", "linked"); CreateJunction(linkedData, caseRoot);
        check((File.GetAttributes(linkedData) & FileAttributes.ReparsePoint) != 0 && await RefusesApply(reparse.Installer, reparse.Plan.PlanPath) && Sha(Path.Combine(reparse.Install, "JobFinder.exe")) == Sha(oldExecutable), "data snapshot rejects real filesystem reparse traversal");
        var reparseTarget = await Prepared("reparse-target", package, update, oldExecutable, publicKey, fixtures);
        CreateJunction(Path.Combine(reparseTarget.Install, "verification"), caseRoot);
        check(await RefusesApply(reparseTarget.Installer, reparseTarget.Plan.PlanPath) && Sha(Path.Combine(reparseTarget.Install, "JobFinder.exe")) == Sha(oldExecutable) && !File.Exists(Path.Combine(caseRoot, "new.txt")), "installation refuses reparse traversal through an existing package target directory");
        var reparsePlan = await Prepared("reparse-plan", package, update, oldExecutable, publicKey, fixtures);
        string linkedStage = UpdateInstaller.CreateDownloadDirectory(); Directory.Delete(linkedStage); CreateJunction(linkedStage, reparsePlan.Plan.TransactionDirectory);
        check(await RefusesApply(reparsePlan.Installer, Path.Combine(linkedStage, "plan.json")), "helper rejects a staged plan reached through a GUID reparse directory");
        Directory.Delete(linkedStage);
        await VerifyRealProcessIdentity(generatedFixtures.New, check);
    }

    private static async Task<(string Old, string New)> BuildPeFixtures(string fixtures)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The updater installation checks require Windows.");
        string sdk = Environment.GetEnvironmentVariable("JOBFINDER_UPDATE_TEST_DOTNET") ?? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        string root = Path.Combine(fixtures, "installer-generated-pe-fixtures");
        async Task<string> Build(string name, string version)
        {
            string folder = Path.Combine(root, name); Directory.CreateDirectory(folder); string project = Path.Combine(folder, "Fixture.csproj");
            File.WriteAllText(project, $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType><TargetFramework>net10.0</TargetFramework><RuntimeIdentifier>win-x64</RuntimeIdentifier><SelfContained>false</SelfContained><UseAppHost>true</UseAppHost><AssemblyName>JobFinder</AssemblyName><Version>{version}</Version><FileVersion>{version}.0</FileVersion></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(folder, "Fixture.cs"), "internal static class Fixture { private static void Main(string[] args) { if (args.Length == 1 && args[0] == \"--hold\") System.Threading.Thread.Sleep(15000); } }");
            var start = new ProcessStartInfo(sdk) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "build", project, "-c", "Release", "--nologo", "-v", "quiet" }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("The .NET 10 fixture compiler could not start.");
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidOperationException("PE fixture compilation failed. Set JOBFINDER_UPDATE_TEST_DOTNET to the .NET 10 SDK host. " + await output + await error);
            string exe = Path.Combine(folder, "bin", "Release", "net10.0", "win-x64", "JobFinder.exe");
            if (PeVersion(exe) != Version.Parse(version + ".0")) throw new InvalidOperationException("Compiled fixture PE has the wrong file version.");
            return exe;
        }
        string old = await Build("old", "0.4.1"); string current = await Build("new", "0.5.0"); return (old, current);
    }

    private static async Task VerifyRealProcessIdentity(string fixtureExecutable, Action<bool, string> check)
    {
        var host = (IUpdateProcessHost)Activator.CreateInstance(typeof(UpdateInstaller).Assembly.GetType("JobFinder.Core.UpdateProcessHost", throwOnError: true)!, nonPublic: true)!;
        var start = new ProcessStartInfo(fixtureExecutable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(fixtureExecutable)! }; start.ArgumentList.Add("--hold");
        string? sdk = Environment.GetEnvironmentVariable("JOBFINDER_UPDATE_TEST_DOTNET") ?? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (sdk is not null && Path.IsPathFullyQualified(sdk)) start.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(sdk)!;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Disposable process identity fixture did not start.");
        UpdateProcessIdentity? identity = null;
        try
        {
            identity = host.GetProcess(process.Id); check(identity is not null && Path.GetFullPath(identity.ExecutablePath) == Path.GetFullPath(fixtureExecutable), "production process host observes actual PID path and start time");
            bool wrongParent = false;
            try { await host.WaitForExitAsync(identity! with { StartTimeUtcTicks = identity.StartTimeUtcTicks + 1 }, TimeSpan.FromMilliseconds(50), default); }
            catch (UpdateFailure error) { wrongParent = error.Kind == UpdateFailureKind.Install; }
            check(wrongParent && !process.HasExited, "production parent wait rejects wrong start time without terminating process");
            check(!await host.WaitForExitAsync(identity!, TimeSpan.FromMilliseconds(50), default) && !process.HasExited, "production parent deadline leaves exact parent alive");
            check(!await host.StopAsync(identity! with { StartTimeUtcTicks = identity.StartTimeUtcTicks + 1 }, default) && !process.HasExited, "production child stop rejects a different identity on retained process handle");
            check(await host.StopAsync(identity!, default) && process.HasExited, "production child stop terminates only the exact disposable launched process");
        }
        finally
        {
            if (identity is not null && !process.HasExited) await host.StopAsync(identity, default);
        }
    }

    private static void CreateJunction(string path, string target)
    {
        path = Path.GetFullPath(path); target = Path.GetFullPath(target); Directory.CreateDirectory(path);
        byte[] substitute = Encoding.Unicode.GetBytes("\\??\\" + target); byte[] printed = Encoding.Unicode.GetBytes(target);
        byte[] buffer = new byte[16 + substitute.Length + 2 + printed.Length + 2];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), 0xA0000003);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4, 2), checked((ushort)(buffer.Length - 8)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10, 2), checked((ushort)substitute.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12, 2), checked((ushort)(substitute.Length + 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14, 2), checked((ushort)printed.Length));
        substitute.CopyTo(buffer, 16); printed.CopyTo(buffer, 16 + substitute.Length + 2);
        using var handle = Native.CreateFile(path, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid || !Native.DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create disposable junction fixture.");
    }
    private static class Native
    {
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DeviceIoControl(SafeFileHandle handle, uint control, byte[] input, int inputSize, IntPtr output, int outputSize, out int bytesReturned, IntPtr overlapped);
    }

    private sealed record PreparedFixture(UpdateInstaller Installer, FixtureProcesses Host, UpdateApplyPlan Plan, string Install);
    private static async Task<PreparedFixture> Prepared(string name, string package, AvailableUpdate update, string oldExecutable, string publicKey, string fixtures)
    {
        string install = Path.Combine(fixtures, name + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(install);
        File.Copy(oldExecutable, Path.Combine(install, "JobFinder.exe")); File.WriteAllText(Path.Combine(install, "README.md"), "old docs"); File.WriteAllText(Path.Combine(install, "personal-note.txt"), "private extra");
        Directory.CreateDirectory(Path.Combine(install, "data", "empty")); File.WriteAllText(Path.Combine(install, "data", "profile.json"), "original profile");
        var host = new FixtureProcesses(Path.Combine(install, "JobFinder.exe"));
        var installer = new UpdateInstaller(publicKey, host, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200));
        var plan = await installer.PrepareAsync(update, package, install, host.CurrentProcessId, host.CurrentExecutablePath);
        host.Plan = plan; host.CurrentExecutablePath = plan.HelperPath; host.CurrentProcessId = 200;
        return new(installer, host, plan, install);
    }
    private static async Task<bool> RefusesApply(UpdateInstaller installer, string path)
    {
        try { return !(await installer.ApplyAsync(path)).Success; }
        catch (UpdateFailure) { return true; }
    }
    private static string Sha(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }

    private static Version PeVersion(string path)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
    }

    private static AvailableUpdate Sign(string package, Version version, byte[] executable, RSA rsa)
    {
        string stable = version.Revision == 0 ? version.ToString(3) : version.ToString(4);
        var manifest = new UpdateManifest { Version = stable, Tag = "v" + stable, PackageFile = UpdateProduct.PackageName, PackageSize = new FileInfo(package).Length, PackageSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(package))), ExecutableSha256 = Convert.ToHexString(SHA256.HashData(executable)), MinimumUpdaterVersion = "0.3.0" };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        byte[] signature = rsa.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        return new AvailableUpdate(manifest, version, "https://github.com/4ek1st/JobFinder/releases/download/" + manifest.Tag + "/" + UpdateProduct.PackageName, bytes, signature, "https://github.com/4ek1st/JobFinder/releases/tag/" + manifest.Tag);
    }

    private static void WritePackage(string path, IEnumerable<(string Name, byte[] Bytes, int Attributes)> files)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, bytes, attributes) in files)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.NoCompression); entry.ExternalAttributes = attributes;
            using var stream = entry.Open(); stream.Write(bytes);
        }
    }

    private sealed class FixtureProcesses(string initialExecutable) : IUpdateProcessHost
    {
        public string CurrentExecutablePath { get; set; } = initialExecutable;
        public int CurrentProcessId { get; set; } = 100;
        public UpdateProcessIdentity? Parent { get; set; } = new(100, initialExecutable, 630000000000000000L);
        public bool ParentExits { get; set; } = true;
        public bool FailStart { get; set; }
        public bool FailHelperLaunch { get; set; }
        public bool ChildExits { get; set; }
        public bool WrongAcknowledgement { get; set; }
        public bool ChildIdentityChanges { get; set; }
        public Action? OnStart { get; set; }
        public UpdateApplyPlan? Plan { get; set; }
        public UpdateProcessIdentity? Child { get; private set; }
        public UpdateProcessIdentity? RestoredChild { get; private set; }
        public UpdateProcessIdentity? GetProcess(int pid) => Parent?.ProcessId == pid ? Parent : Child?.ProcessId == pid && !ChildExits ? ChildIdentityChanges ? Child with { StartTimeUtcTicks = Child.StartTimeUtcTicks + 1 } : Child : RestoredChild?.ProcessId == pid ? RestoredChild : pid == CurrentProcessId ? new(pid, CurrentExecutablePath, 630000000000000001L) : null;
        public Task<bool> WaitForExitAsync(UpdateProcessIdentity process, TimeSpan timeout, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested(); if (ParentExits) Parent = null; return Task.FromResult(ParentExits);
        }
        public Process LaunchHelper(string executablePath, string planPath) => FailHelperLaunch ? throw new IOException("Simulated native helper-start failure.") : throw new NotSupportedException("Fixture never starts a real process.");
        public UpdateProcessIdentity StartApplication(string executablePath, IReadOnlyList<string> arguments)
        {
            if (Plan is not null && arguments.Count == 2 && arguments[0] == "--update-failed" && arguments[1] == Plan.TransactionToken)
            { RestoredChild = new(301, executablePath, 630000000000000003L); return RestoredChild; }
            if (FailStart) throw new IOException("Simulated native process-start failure.");
            if (Plan is null || arguments.Count != 2 || arguments[0] != "--updated" || arguments[1] != Plan.TransactionToken) throw new InvalidOperationException("Unexpected updater launch.");
            Child = new(300, executablePath, 630000000000000002L); OnStart?.Invoke();
            if (!ChildExits) File.WriteAllText(Path.Combine(Plan.TransactionDirectory, "startup.json"), JsonSerializer.Serialize(new { transactionToken = Plan.TransactionToken, version = Plan.Version, processId = WrongAcknowledgement ? 999 : Child.ProcessId, executablePath, startTimeUtcTicks = Child.StartTimeUtcTicks }, Json));
            return Child;
        }
        public Task<bool> StopAsync(UpdateProcessIdentity child, CancellationToken cancellation)
        {
            if (Child != child) throw new InvalidOperationException("Cannot stop a process this fixture did not launch.");
            ChildExits = true; return Task.FromResult(true);
        }
    }
}
