using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace JobFinder.Core;

internal sealed class UpdateProcessHost : IUpdateProcessHost
{
    public string CurrentExecutablePath => Environment.ProcessPath ?? throw new UpdateFailure(UpdateFailureKind.Install, "The running executable cannot be identified.");
    public int CurrentProcessId => Environment.ProcessId;

    public UpdateProcessIdentity? GetProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return Identity(process);
        }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (System.ComponentModel.Win32Exception error) { throw new UpdateFailure(UpdateFailureKind.Install, "An update process identity cannot be verified.", error); }
    }

    public async Task<bool> WaitForExitAsync(UpdateProcessIdentity identity, TimeSpan timeout, CancellationToken cancellation)
    {
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            var actual = Identity(process);
            if (actual is null) return true;
            if (!Matches(identity, actual)) throw new UpdateFailure(UpdateFailureKind.Install, "The update parent process identity changed.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(timeout);
            try { await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); return true; }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return false; }
        }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
        catch (System.ComponentModel.Win32Exception error) { throw new UpdateFailure(UpdateFailureKind.Install, "The update parent identity cannot be verified.", error); }
    }

    public Process LaunchHelper(string executablePath, string planPath)
    {
        var start = new ProcessStartInfo(executablePath) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(executablePath)! };
        start.ArgumentList.Add("--apply-update"); start.ArgumentList.Add(planPath);
        return Process.Start(start) ?? throw new UpdateFailure(UpdateFailureKind.Install, "The update helper could not be started.");
    }

    public UpdateProcessIdentity StartApplication(string executablePath, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(executablePath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executablePath)! };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new UpdateFailure(UpdateFailureKind.Startup, "The updated application could not be started.");
        return new(process.Id, UpdatePaths.Absolute(executablePath), process.StartTime.ToUniversalTime().Ticks);
    }

    public async Task<bool> StopAsync(UpdateProcessIdentity child, CancellationToken cancellation)
    {
        try
        {
            using var process = Process.GetProcessById(child.ProcessId);
            var actual = Identity(process);
            if (actual is null) return true;
            if (!Matches(child, actual)) return false;
            process.Kill(entireProcessTree: false);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); return true;
        }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or OperationCanceledException) { return false; }
    }

    private static UpdateProcessIdentity? Identity(Process process)
    {
        // Keep the OS process object alive throughout inspection and the following wait/stop.
        // A cached PID alone could refer to a different process after the child exits.
        _ = process.SafeHandle;
        if (process.HasExited) return null;
        try
        {
            int capacity = 32768; var name = new StringBuilder(capacity);
            if (!QueryFullProcessImageName(process.SafeHandle, 0, name, ref capacity)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            string path = name.ToString();
            if (string.IsNullOrEmpty(path)) throw new UpdateFailure(UpdateFailureKind.Install, "An update process cannot be identified.");
            return new(process.Id, UpdatePaths.Absolute(path), process.StartTime.ToUniversalTime().Ticks);
        }
        catch (System.ComponentModel.Win32Exception) when (process.HasExited) { return null; }
        catch (InvalidOperationException) when (process.HasExited) { return null; }
    }

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref int size);

    internal static bool Matches(UpdateProcessIdentity expected, UpdateProcessIdentity actual) => expected.ProcessId == actual.ProcessId && expected.StartTimeUtcTicks == actual.StartTimeUtcTicks && UpdatePaths.Same(expected.ExecutablePath, actual.ExecutablePath);
}
