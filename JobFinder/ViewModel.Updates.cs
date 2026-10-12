using JobFinder.Core;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace JobFinder;

public sealed record UpdateWindowState(double Width, double Height, double Left, double Top, WindowState State, double FeedOffset = 0, double DetailOffset = 0);
internal sealed record UpdateResumeState(int Schema, DateTimeOffset SavedAt, SearchQuery AppliedQuery, SearchQuery Draft, bool SearchExpanded, bool ShowFavorites, int Sort, string? SelectedKey, bool DetailsOpen, UpdateWindowState? Window);

public sealed partial class ViewModel
{
    public UpdateViewModel Updates { get; private set; } = null!;
    private string UpdateResumePath => Path.Combine(Store.DirectoryPath, "update-resume.json");
    private static readonly JsonSerializerOptions ResumeJson = new() { WriteIndented = true };

    private void InitializeUpdates(IUpdateService? service, Func<string>? downloadDirectory)
    {
        Updates = new(Ui, service, createDownloadDirectory: downloadDirectory);
        ConfigureUpdateApply(() => SaveForUpdate());
    }
    internal void ConfigureUpdateApply(Func<bool> saveSnapshot)
    {
        var installer = new UpdateInstaller();
        UpdateApplyPlan? pending = null;
        Updates.ConfigureApply(saveSnapshot, async (update, package, cancellation) =>
        {
            string executable = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "JobFinder.exe");
            var plan = await installer.PrepareAsync(update, package, Path.GetDirectoryName(executable)!, Environment.ProcessId, executable, cancellation);
            pending = plan;
            return () => { using var helper = installer.LaunchHelper(plan); };
        }, () => Application.Current.Shutdown(), ClearUpdateResume, () => { if (pending is not null) { installer.AbortPrepared(pending); pending = null; } });
    }

    public bool SaveForUpdate(UpdateWindowState? window = null)
    {
        // LastQuery belongs to the applied results. A draft is restored only after that query runs again.
        var applied = _active ?? Profile.LastQuery;
        var resume = new UpdateResumeState(1, DateTimeOffset.UtcNow, applied, new(Keyword, City, Radius, Kind), SearchExpanded, ShowFavorites, Sort, SelectedJob?.Job.Key, HasDetail, window);
        Profile.LastQuery = applied;
        if (!Save()) return false;
        string temporary = UpdateResumePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(stream, resume, ResumeJson); stream.Flush(true); }
            File.Move(temporary, UpdateResumePath, true); return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { StorageNotice = Ui["StorageError"]; return false; }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    internal void ClearUpdateResume()
    { try { if (File.Exists(UpdateResumePath)) File.Delete(UpdateResumePath); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    internal UpdateResumeState? TakeUpdateResume()
    {
        try
        {
            if (!File.Exists(UpdateResumePath) || new FileInfo(UpdateResumePath).Length > 256 * 1024) return null;
            var state = JsonSerializer.Deserialize<UpdateResumeState>(File.ReadAllText(UpdateResumePath), ResumeJson);
            if (state is null || state.Schema != 1 || state.Draft is null || state.AppliedQuery is null || state.SavedAt < DateTimeOffset.UtcNow.AddDays(-7) || state.SavedAt > DateTimeOffset.UtcNow.AddMinutes(5) || state.Draft.Keyword is null || state.Draft.City is null || !Enum.IsDefined(state.Draft.Kind)) return null;
            File.Delete(UpdateResumePath); return state;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    internal async Task RestoreUpdateStateAsync(UpdateResumeState state)
    {
        Sort = Math.Clamp(state.Sort, 0, 2); ShowFavorites = state.ShowFavorites;
        ApplyQuery(state.Draft); SearchExpanded = state.SearchExpanded;
        if (state.SelectedKey is not null && VisibleJobs.FirstOrDefault(job => job.Job.Key == state.SelectedKey) is JobView selected)
        {
            SelectedJob = selected;
            if (state.DetailsOpen) await DetailTask; else CloseDetails();
        }
        RefreshState();
    }
}
