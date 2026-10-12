using JobFinder.Core;
using System.Windows;
using System.Windows.Input;

namespace JobFinder;

public sealed class JobDetailView(JobView job, ViewModel owner) : Observable
{
    public JobView Job { get; } = job;
    public Ui Ui => owner.Ui;
    public JobDetails? Document { get; set; }
    public bool IsBusy { get; set; }
    public string Error { get; set; } = "";
    public DateTimeOffset? RetryAt { get; set; }
    public bool HasError => Error.Length > 0;
    public string ErrorText => Error + (RetryAt.HasValue ? "\n" + Ui["RetryAvailableAt"] + " " + RetryAt.Value.ToLocalTime().ToString("HH:mm") : "");
    public bool HasDescription => Document?.HasDescription == true;
    public bool ShowEmptyDescription => !IsBusy && !HasError && !HasDescription;
    public bool ShowNoContacts => !IsBusy && Document?.Contacts.Count is not > 0;
    public string NoContactsText => Ui[HasError ? "ContactsUnavailable" : "NoContacts"];
    public IReadOnlyList<DetailBlock> Blocks => Document?.Blocks ?? [];
    public IReadOnlyList<JobContact> Contacts => Document?.Contacts ?? [];
    public string Snapshot => Document is null ? "" : Ui["DetailsLoadedAt"] + " " + Document.RetrievedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
    public string RetryLabel => Ui["Retry"];
}

public sealed partial class ViewModel
{
    private IJobDetailService _detailService = null!;
    private CancellationTokenSource? _detailCancellation;
    private int _detailGeneration;
    private readonly Dictionary<string, JobDetails> _detailCache = [];
    private JobDetailView? _detail;
    public JobDetailView? Detail { get => _detail; private set { Set(ref _detail, value); Changed(nameof(HasDetail)); } }
    public bool HasDetail => Detail is not null;
    public CompanyLogoService Logos { get; private set; } = null!;
    public Command SelectCommand { get; private set; } = null!;
    public Command CloseDetailsCommand { get; private set; } = null!;
    public Command ReloadDetailsCommand { get; private set; } = null!;
    public Command CopyContactCommand { get; private set; } = null!;
    public Task DetailTask { get; private set; } = Task.CompletedTask;
    private void InitializeDetails(string directory, IJobDetailService service, CompanyLogoService? logos)
    {
        _detailService = service; Logos = logos ?? new CompanyLogoService(System.IO.Path.Combine(directory, "logo-cache"));
        SelectCommand = new(p => { if (p is JobView job) { if (ReferenceEquals(SelectedItem, job)) SelectDetails(job); else SelectedItem = job; } });
        CloseDetailsCommand = new(_ => CloseDetails());
        ReloadDetailsCommand = new(_ => { if (Detail is not null) DetailTask = LoadDetailsAsync(Detail, true); }, _ => Detail is { IsBusy: false } detail && (!detail.RetryAt.HasValue || detail.RetryAt <= DateTimeOffset.UtcNow));
        CopyContactCommand = new(p => { if (p is JobContact contact) { try { Clipboard.SetText(contact.Value); Notice = Ui["Copied"]; } catch (System.Runtime.InteropServices.ExternalException) { Notice = Ui["OpenError"]; } } });
    }
    private void SelectDetails(JobView job)
    {
        if (Detail is not null && Detail.Job.Job.Key == job.Job.Key) return;
        _detailCancellation?.Cancel(); ++_detailGeneration;
        Detail = new(job, this);
        DetailTask = LoadDetailsAsync(Detail, false);
    }
    public void CloseDetails()
    {
        _detailCancellation?.Cancel(); ++_detailGeneration;
        Detail = null;
    }
    private async Task LoadDetailsAsync(JobDetailView detail, bool refresh)
    {
        _detailCancellation?.Cancel(); using var token = new CancellationTokenSource(); _detailCancellation = token;
        int generation = ++_detailGeneration;
        detail.Error = ""; detail.RetryAt = null;
        JobDetails? saved = detail.Job.Job.Details;
        if (saved is null) _detailCache.TryGetValue(detail.Job.Job.Key, out saved);
        if (!refresh && saved is not null)
        { detail.Document = saved; detail.IsBusy = false; detail.Refresh(); _detailCancellation = null; return; }
        detail.IsBusy = true; detail.Refresh();
        try
        {
            var doc = await _detailService.LoadAsync(detail.Job.Job, token.Token);
            if (generation != _detailGeneration || !ReferenceEquals(Detail, detail) || token.IsCancellationRequested) return;
            if (_detailCache.Count >= 60) _detailCache.Clear(); _detailCache[detail.Job.Job.Key] = doc;
            detail.Document = doc;
            detail.Job.Job = detail.Job.Job with { Details = doc, LogoUrl = doc.LogoUrl.Length > 0 ? doc.LogoUrl : detail.Job.Job.LogoUrl };
            detail.Job.Refresh();
            int index = Profile.Favorites.FindIndex(x => x.Key == detail.Job.Job.Key);
            if (index >= 0) { Profile.Favorites[index] = detail.Job.Job; Save(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            if (generation == _detailGeneration && ReferenceEquals(Detail, detail))
            {
                detail.Error = Ui["DetailsUnavailable"] + (e is SourceFailure failure ? " · " + Ui[failure.Kind.ToString()] : "");
                detail.RetryAt = (e as SourceFailure)?.RetryAt;
            }
        }
        finally
        {
            if (generation == _detailGeneration && ReferenceEquals(Detail, detail)) { detail.IsBusy = false; detail.Refresh(); CommandManager.InvalidateRequerySuggested(); }
            if (ReferenceEquals(_detailCancellation, token)) _detailCancellation = null;
        }
    }
}
