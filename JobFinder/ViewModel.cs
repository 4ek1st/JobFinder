using JobFinder.Core;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace JobFinder;

public class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Changed(name); return true; }
    public void Refresh() => PropertyChanged?.Invoke(this, new(""));
}
public sealed class Command(Action<object?> execute, Predicate<object?>? can = null) : ICommand
{
    public bool CanExecute(object? parameter) => can?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => execute(parameter);
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
}
public sealed class SourceView(IJobSource source, Ui ui) : Observable
{
    public IJobSource Source { get; } = source;
    public string Id => Source.Id;
    public string Name => Source.Name;
    public Ui Ui => ui;
    public Action? SelectionChanged { get; set; }
    private bool _enabled = true;
    public bool Enabled { get => _enabled; set { if (Set(ref _enabled, value)) SelectionChanged?.Invoke(); } }
    public string State { get; set; } = "Idle";
    public int Found { get; set; }
    public int Inspected { get; set; }
    public int Pages { get; set; }
    public string? Next { get; set; }
    public HashSet<string> CompletedRequests { get; } = [];
    public bool PaginationStopped { get; set; }
    public int EmptyPages { get; set; }
    public bool ScanLimited { get; set; }
    public bool LimitedCoverage { get; set; }
    public bool RetryingProblem { get; set; }
    public DateTimeOffset? RetryAt { get; set; }
    public bool HasError => State is "Network" or "Timeout" or "Access" or "RateLimit" or "Format" or "Input";
    public string Status => ui[ScanLimited ? "SourceScanLimit" : PaginationStopped ? "RepeatedPage" : State == "Input" && Id == "stellenanzeigen" ? "InputCity" : State == "Input" && Id == "handwerk" ? "InputPostcode" : State];
    public bool NeedsAttention => HasError || ScanLimited || PaginationStopped || RetryingProblem;
    public bool CanRetry => HasError && State != "Input" || ScanLimited || RetryingProblem && State == "Cancelled";
    public string RetryLabel => ui[ScanLimited ? "ContinueSource" : "Retry"];
    public string Coverage => (Pages > 0 ? $"{Found} {ui["Found"]} · {Pages} {ui["Pages"]}" + (LimitedCoverage || Id == "arbeitnow" ? $"\n{ui["Inspected"]}: {Inspected}" : "") : "") + (LimitedCoverage ? $"\n{ui["LimitedSourceCoverage"]}" : "") + (RetryAt.HasValue ? $"\n{ui["Retry"]}: {RetryAt.Value.ToLocalTime():HH:mm}" : "");
    public Brush Tone => ThemeManager.Brush(HasError ? "WarningText" : State == "Loading" ? "Accent" : "TextMuted");
    public bool CoverageVisible => Coverage.Length > 0;
    public string Categories => string.Join(" · ", new[] { JobKind.Job, JobKind.Internship, JobKind.Apprenticeship }.Where(Source.Supports).Select(k => ui[k.ToString()]));
    public string SearchScope => SourceCatalog.Find(Id)?.Coverage(ui.IsRu) ?? "";
}
public sealed class JobView(JobListing job, ViewModel owner) : Observable
{
    public JobListing Job { get; set; } = job;
    public Ui Ui => owner.Ui;
    public string Title => Job.Title;
    public string Company => Job.Company;
    public string CompanyLocation => string.Join("  ·  ", new[] { Company, Location }.Where(x => x.Length > 0));
    public string Location => Job.Location.Length > 0 ? Job.Location : Ui["NoCity"];
    public string Source => Job.Source;
    public Brush SourceColor => ThemeManager.Brush(Job.Source switch { "Azubiyo" => "AzubiyoBackground", "Praktikum.info" => "PraktikumBackground", "Arbeitnow" => "ArbeitnowBackground", _ => "Hover" });
    public Brush SourceTextColor => ThemeManager.Brush(Job.Source switch { "Azubiyo" => "AzubiyoText", "Praktikum.info" => "PraktikumText", "Arbeitnow" => "ArbeitnowText", _ => "TextBody" });
    public bool IsCompact => owner.IsCompact;
    public CompanyLogoService LogoLoader => owner.Logos;
    public string LogoUrl => Job.LogoUrl;
    public string CompanyInitials => CompanyLogo.Initials(Company.Length > 0 ? Company : Source);
    public GridLength ActionWidth => owner.IsCompact ? new(0) : new(360);
    public string Kind => Ui[Job.Kind.ToString()];
    public Brush KindColor => ThemeManager.Brush("KindBackground");
    public string Details => string.Join("  ·  ", new[] { Job.Start.Length > 0 ? Ui["Start"] + ": " + Job.Start : "", Job.Salary, Job.Remote ? Ui["Remote"] : "" }.Where(x => x.Length > 0));
    public bool HasDetails => Details.Length > 0;
    public string Date => Job.FeedDate.HasValue ? $"{Ui[Job.Source == "Arbeitnow" ? "SourceDate" : "SourcePostedDate"]} {Job.FeedDate.Value.ToLocalTime():dd.MM.yyyy}" : "";
    public bool HasDate => Job.FeedDate.HasValue;
    public string OtherSources => Job.OtherLinks.Count > 0 ? Ui["OtherLinks"] + " " + string.Join(", ", Job.OtherLinks.Select(x => x.Source).Distinct()) : "";
    public bool HasOtherSources => Job.OtherLinks.Count > 0;
    public string Summary => Job.Summary;
    public bool HasSummary => Summary.Length > 0;
    public bool IsFavorite => owner.Profile.Favorites.Any(x => x.Key == Job.Key);
    public string BookmarkText => Ui[IsFavorite ? "Bookmarked" : "Bookmark"];
}
public sealed class FeedStatusView(ViewModel owner) : Observable
{
    public bool IsBusy => owner.IsBusy;
    public string Label => owner.IsBusy ? owner.Ui["LoadingMore"] : owner.LoadingExplanation.Length > 0 ? owner.LoadingExplanation : owner.Ui[owner.HasPendingPages ? "ScrollForMore" : "AvailableLoaded"];
}
public sealed partial class ViewModel : Observable, IDisposable
{
    public Ui Ui { get; } = new();
    public UserProfile Profile { get; }
    public ProfileStore Store { get; }
    private readonly SourceClient _client;
    private readonly SearchRunner _runner;
    private CancellationTokenSource? _search;
    private int _generation;
    private readonly List<JobView> _results = [];
    public ObservableCollection<JobView> VisibleJobs { get; } = [];
    public ObservableCollection<object> FeedItems { get; } = [];
    private readonly FeedStatusView _feedStatus;
    public event Action<bool>? JobsUpdating;
    public event Action<bool>? JobsUpdated;
    public ObservableCollection<SourceView> Sources { get; } = [];
    public ObservableCollection<SavedSearch> SavedSearches { get; } = [];
    public int[] Radii { get; } = [5, 10, 25, 50, 100];
    private string _keyword = "", _city = "", _notice = "", _storageNotice = "";
    private int _radius = 25, _sort;
    private JobKind _kind;
    private bool _busy, _favorites, _hasSearched;
    private bool _compact;
    private bool _shortViewport;
    public bool ShortViewport { get => _shortViewport; set => Set(ref _shortViewport, value); }
    private bool _searchExpanded = true;
    public bool SearchExpanded { get => _searchExpanded; set { if (Set(ref _searchExpanded, value)) { Changed(nameof(SearchCollapsed)); Changed(nameof(ShowNotice)); } } }
    public bool SearchCollapsed => !SearchExpanded;
    public bool HasSearchChanges => _active is not null && (!QueryUnchanged || _settingsChanged);
    public string CompactSearchLabel
    {
        get
        {
            var query = _active ?? new SearchQuery(Keyword, City, Radius, Kind).Clean();
            return string.Join(" · ", new[] { query.Keyword.Length > 0 ? query.Keyword : Ui["All"], query.City.Length > 0 ? query.City : Ui["AnyLocation"], Ui[query.Kind.ToString()], query.Radius + " km" });
        }
    }
    private bool _touchActions;
    public bool TouchActions { get => _touchActions; set => Set(ref _touchActions, value); }
    private bool _autoPaused;
    private bool _settingsChanged;
    private HashSet<string> _activeSources = [];
    public bool AutoPaused => _autoPaused;
    public string LoadingExplanation => _active is null ? "" : _settingsChanged ? Ui["SourcesChanged"] : !QueryUnchanged ? Ui["Changed"] : _autoPaused ? Ui["Stopped"] : "";
    public bool IsDark => ThemeManager.IsDark;
    public string ThemeIcon => IsDark ? "weather_sunny" : "weather_moon";
    public string ThemeLabel => Ui[IsDark ? "LightTheme" : "DarkTheme"];
    public string SearchLabel => Ui[IsBusy ? "Cancel" : "Search"];
    public string SearchIcon => IsBusy ? "dismiss" : "search";
    public bool IsCompact { get => _compact; set { if (Set(ref _compact, value)) foreach (var job in _results.Concat(VisibleJobs).Distinct()) job.Refresh(); } }
    private SearchQuery? _active;
    private SavedSearch? _selectedSearch;
    public string Keyword { get => _keyword; set { if (Set(ref _keyword, value)) QueryChanged(); } }
    public string City { get => _city; set { if (Set(ref _city, value)) QueryChanged(); } }
    public int Radius { get => _radius; set { if (Set(ref _radius, value)) { Changed(nameof(FilterLabel)); QueryChanged(); } } }
    public JobKind Kind { get => _kind; set { if (Set(ref _kind, value)) { Changed(nameof(IsAll)); Changed(nameof(IsJob)); Changed(nameof(IsInternship)); Changed(nameof(IsApprenticeship)); QueryChanged(); } } }
    public bool IsAll { get => Kind == JobKind.All; set { if (value) Kind = JobKind.All; } }
    public bool IsJob { get => Kind == JobKind.Job; set { if (value) Kind = JobKind.Job; } }
    public bool IsInternship { get => Kind == JobKind.Internship; set { if (value) Kind = JobKind.Internship; } }
    public bool IsApprenticeship { get => Kind == JobKind.Apprenticeship; set { if (value) Kind = JobKind.Apprenticeship; } }
    public bool IsBusy { get => _busy; private set { Set(ref _busy, value); RefreshState(); CommandManager.InvalidateRequerySuggested(); } }
    public bool ShowFavorites { get => _favorites; set { if (Set(ref _favorites, value)) { Changed(nameof(ShowResults)); RefreshJobs(false); } } }
    public bool ShowResults { get => !ShowFavorites; set { if (value) ShowFavorites = false; } }
    public int Sort { get => _sort; set { if (Set(ref _sort, value)) RefreshJobs(false); } }
    public string Notice { get => _notice; private set { Set(ref _notice, value); Changed(nameof(ShowNotice)); } }
    public bool ShowNotice => Notice.Length > 0 && Notice != Ui["Complete"] && Notice != Ui["Ready"] && Notice != Ui["Partial"] && (!SearchCollapsed || Notice != Ui["Changed"] && Notice != Ui["SourcesChanged"]);
    public string StorageNotice { get => _storageNotice; private set { Set(ref _storageNotice, value); Changed(nameof(HasStorageNotice)); } }
    public bool HasStorageNotice => StorageNotice.Length > 0;
    public int FavoriteCount => Profile.Favorites.Count;
    public string CountLabel => ShowFavorites ? $"{VisibleJobs.Count} {Ui["SavedCount"].ToLowerInvariant()}" : Ui.ListingCount(VisibleJobs.Count);
    public string FilterLabel => Ui["FiltersShort"] + " · " + Radius + " km";
    public string SourceBrief
    {
        get
        {
            var relevant = Sources.Where(x => x.Enabled && x.State is not "Off" and not "Skipped" && x.Source.Supports(_active?.Kind ?? Kind)).ToList();
            int ok = relevant.Count(x => x.State is "Ok" or "Empty");
            return Ui.IsRu ? $"{ok} из {relevant.Count} источников ответили · Поиск охватывает часть вакансий" : $"{ok} von {relevant.Count} Quellen geladen · Auswahl verfügbarer Stellen";
        }
    }
    public bool HasSourceStatus => _hasSearched;
    public int SourceAttentionCount => Sources.Count(s => s.Enabled && s.State is not "Off" and not "Skipped" && s.NeedsAttention);
    public bool ShowSourceAttention => SourceAttentionCount > 0;
    public string SourceAttentionLabel => (Ui.IsRu ? $"Источники требуют внимания: {SourceAttentionCount}" : $"Quellen mit Hinweisen: {SourceAttentionCount}") + " · " + Ui["SourceStatusDetails"];
    public string SettingsLabel => Ui["Settings"] + (ShowSourceAttention ? " · " + string.Format(Ui["SourceAttentionSummary"], SourceAttentionCount) : "");
    public string SourceStatusMenuLabel => Ui["SourceStatus"] + (ShowSourceAttention ? $" · {SourceAttentionCount}" : "");
    public string SourceStatusTitle => !_hasSearched ? Ui["SourceStatusWaiting"] : ShowSourceAttention ? string.Format(Ui["SourceAttentionSummary"], SourceAttentionCount) : IsBusy ? Ui["Searching"] : !Sources.Any(s => s.Enabled && s.State is not "Off" and not "Skipped") ? Ui["NoSources"] : Sources.Any(s => s.Enabled && s.State == "Cancelled") ? Ui["Stopped"] : Ui["SourceStatusHealthy"];
    public Brush SourceTone => ThemeManager.Brush(ShowSourceAttention ? "WarningText" : "TextMuted");
    private bool QueryUnchanged => _active is not null && new SearchQuery(Keyword, City, Radius, Kind).Clean() == _active;
    public bool HasPendingPages => Sources.Any(x => x.Next is not null && x.Enabled && x.Source.Supports(_active?.Kind ?? Kind) && !x.HasError && !x.ScanLimited && x.State != "Off" && x.State != "Skipped");
    public bool HasNext => !ShowFavorites && !IsBusy && !_autoPaused && !_settingsChanged && QueryUnchanged && Sources.Any(x => x.Next is not null && x.Enabled && !x.ScanLimited && x.State is "Ok" or "Empty");
    public bool CanResume => !IsBusy && !ShowFavorites && _autoPaused && !_settingsChanged && QueryUnchanged && HasPendingPages;
    public bool Empty => VisibleJobs.Count == 0;
    public bool HasJobs => !Empty;
    public bool ShowExamples => !ShowFavorites && !_hasSearched;
    public bool ShowLimit => !ShowFavorites && _hasSearched;
    public string EmptyTitle => Ui[ShowFavorites ? "FavoriteEmptyTitle" : IsBusy ? "LoadingTitle" : !_hasSearched ? "WelcomeTitle" : _autoPaused ? "PausedTitle" : Sources.Any(x => x.HasError) ? "ErrorTitle" : "EmptyTitle"];
    public string EmptyBody => LoadingExplanation.Length > 0 && !ShowFavorites ? LoadingExplanation : Ui[ShowFavorites ? "FavoriteEmptyBody" : IsBusy ? "LoadingBody" : !_hasSearched ? "WelcomeBody" : Sources.Any(x => x.HasError) ? "ErrorBody" : Sources.Any(x => x.ScanLimited) ? "SourceScanLimit" : "EmptyBody"];
    public string ActiveLabel => ShowFavorites ? Ui["LocalOnly"] : _active is null ? "" : string.Join(" · ", new[] { _active.Keyword.Length > 0 ? _active.Keyword : Ui["All"], _active.City.Length > 0 ? _active.City : Ui["AnyLocation"], Ui[_active.Kind.ToString()], _active.Radius + " km" });
    public SavedSearch? SelectedSearch { get => _selectedSearch; set { if (Set(ref _selectedSearch, value) && value is not null) { ApplyQuery(value.Query); _ = SearchAsync(); } } }
    private object? _selectedItem;
    public object? SelectedItem { get => _selectedItem; set { if (value is FeedStatusView) return; if (Set(ref _selectedItem, value)) { Changed(nameof(SelectedJob)); if (value is JobView job) SelectDetails(job); else CloseDetails(); } } }
    public JobView? SelectedJob { get => SelectedItem as JobView; set => SelectedItem = value; }
    public Command SearchCommand { get; }
    public Command EditSearchCommand { get; }
    public Command CollapseSearchCommand { get; }
    public Command CancelCommand { get; }
    public Command ResumeCommand { get; }
    public Command ThemeCommand { get; }
    public Command RetryCommand { get; }
    public Command BookmarkCommand { get; }
    public Command OpenCommand { get; }
    public Command CopyCommand { get; }
    public Command PortalCommand { get; }
    public Command LanguageCommand { get; }
    public Command ExampleCommand { get; }
    public Command RemoveSearchCommand { get; }
    public ViewModel(string dataDirectory, SourceClient? client = null, IEnumerable<IJobSource>? testSources = null, IJobDetailService? detailService = null, CompanyLogoService? logos = null, IUpdateService? updateService = null, Func<string>? updateDownloadDirectory = null)
    {
        _feedStatus = new(this);
        Store = new(dataDirectory); Profile = Store.Load(); Ui.Language = Profile.Language;
        ThemeManager.Apply(Profile.Theme);
        InitializeUpdates(updateService, updateDownloadDirectory);
        _client = client ?? new();
        InitializeDetails(dataDirectory, detailService ?? new JobDetailService(_client), logos);
        if (testSources is null) SourceCatalog.UpdateProfileSources(Profile);
        _runner = new(testSources ?? SourceCatalog.Create(_client));
        foreach (var source in _runner.Sources)
        {
            var view = new SourceView(source, Ui) { Enabled = Profile.EnabledSources.Contains(source.Id) };
            view.SelectionChanged = () => { Profile.EnabledSources = Sources.Where(x => x.Enabled).Select(x => x.Id).ToList(); Save(); if (_hasSearched) { _settingsChanged = !_activeSources.SetEquals(Profile.EnabledSources); Notice = Ui[_settingsChanged ? "SourcesChanged" : "Complete"]; } RefreshState(); };
            Sources.Add(view);
        }
        foreach (var s in Profile.Searches) SavedSearches.Add(s);
        ApplyQuery(Profile.LastQuery); Notice = Ui["Ready"];
        if (testSources is null) Save();
        if (Store.RecoveryMessage is not null) StorageNotice = Ui[Store.RecoveryMessage == "backup" ? "StorageBackup" : "StorageDamaged"];
        SearchCommand = new(_ => { if (IsBusy) Cancel(); else _ = SearchAsync(); });
        EditSearchCommand = new(_ => SearchExpanded = true);
        CollapseSearchCommand = new(_ => SearchExpanded = false);
        CancelCommand = new(_ => Cancel(), _ => IsBusy);
        ResumeCommand = new(_ => Resume(), _ => CanResume);
        ThemeCommand = new(_ => SetTheme(IsDark ? "light" : "dark"));
        RetryCommand = new(p => { if (p is SourceView s) _ = SearchAsync(retry: s.Id); }, p => !IsBusy && !_settingsChanged && QueryUnchanged && p is SourceView s && s.Enabled && s.CanRetry && (!s.RetryAt.HasValue || s.RetryAt <= DateTimeOffset.UtcNow));
        BookmarkCommand = new(p => { if (p is JobView j) ToggleFavorite(j); });
        OpenCommand = new(p => { if (p is JobView j) Open(j.Job.Url); });
        CopyCommand = new(p => { if (p is JobView j) { try { Clipboard.SetText(j.Job.Url); Notice = Ui["Copied"]; } catch (System.Runtime.InteropServices.ExternalException) { Notice = Ui["OpenError"]; } } });
        PortalCommand = new(p => { if (p is string id) Open(Links.Portal(id, new(Keyword, City, Radius, Kind))); });
        LanguageCommand = new(p => { Ui.Language = p?.ToString() ?? "de"; Profile.Language = Ui.Language; Notice = Ui[IsBusy ? "Searching" : "Ready"]; Save(); Refresh(); foreach (var s in Sources) s.Refresh(); Detail?.Refresh(); RefreshJobs(); });
        ExampleCommand = new(p => { ApplyQuery(p?.ToString() switch { "internship" => new("Design", "", 25, JobKind.Internship), "apprenticeship" => new("Fachinformatiker", "", 25, JobKind.Apprenticeship), _ => new("Developer", "", 25, JobKind.Job) }); _ = SearchAsync(); });
        RemoveSearchCommand = new(_ => { if (SelectedSearch is not null) { SavedSearches.Remove(SelectedSearch); Profile.Searches = SavedSearches.ToList(); _selectedSearch = null; Changed(nameof(SelectedSearch)); Save(); } }, _ => SelectedSearch is not null);
    }
    private void QueryChanged() { if (_active is not null) { Notice = Ui[!QueryUnchanged ? "Changed" : _autoPaused ? "Stopped" : "Complete"]; RefreshState(); } else Changed(nameof(CompactSearchLabel)); }
    public void SetTheme(string theme)
    {
        ThemeManager.Apply(theme); Profile.Theme = IsDark ? "dark" : "light"; Save();
        Changed(nameof(IsDark)); Changed(nameof(ThemeIcon)); Changed(nameof(ThemeLabel)); Changed(nameof(SourceTone));
        foreach (var source in Sources) source.Refresh();
        RefreshJobs();
    }
    public void ApplyQuery(SearchQuery query) { Keyword = query.Keyword; City = query.City; Radius = query.Radius; Kind = query.Kind; }
    public async Task SearchAsync(bool more = false, string? retry = null)
    {
        if ((more || retry is not null) && IsBusy || more && !HasNext) return;
        var query = (more || retry is not null) && _active is not null ? _active : new SearchQuery(Keyword, City, Radius, Kind).Clean();
        var selected = Sources.Where(s => s.Enabled && s.Source.Supports(query.Kind) && (!more || s.Next is not null && !s.ScanLimited && s.State is "Ok" or "Empty") && (retry is null || retry == s.Id)).ToList();
        if (selected.Count == 0) { if (!more && retry is null) SearchExpanded = true; Notice = Ui["NoSources"]; return; }
        _search?.Cancel();
        using var tokenSource = new CancellationTokenSource(); _search = tokenSource;
        int generation = ++_generation;
        if (!more && retry is null)
        {
            CloseDetails(); SelectedItem = null;
            _results.Clear(); _active = query; _hasSearched = true; _autoPaused = false; _settingsChanged = false; _activeSources = Sources.Where(s => s.Enabled).Select(s => s.Id).ToHashSet();
            foreach (var s in Sources) { s.Found = 0; s.Inspected = 0; s.Pages = 0; s.EmptyPages = 0; s.ScanLimited = false; s.LimitedCoverage = false; s.RetryingProblem = false; s.Next = null; s.CompletedRequests.Clear(); s.PaginationStopped = false; s.RetryAt = null; s.State = !s.Enabled ? "Off" : !s.Source.Supports(query.Kind) ? "Skipped" : "Loading"; s.Refresh(); }
            SearchExpanded = false;
        }
        if (!more) { ShowFavorites = false; _autoPaused = false; }
        if (retry is not null) { var source = selected[0]; source.RetryingProblem = source.NeedsAttention; source.ScanLimited = false; source.EmptyPages = 0; }
        foreach (var s in selected) { s.State = "Loading"; s.RetryAt = null; s.Refresh(); }
        Profile.LastQuery = query; Save(); IsBusy = true; Notice = Ui["Searching"]; RefreshJobs(more || retry is not null); Changed(nameof(ActiveLabel));
        var requests = selected.ToDictionary(s => s.Id, s => (more || retry is not null) ? s.Next : null);
        try
        {
            await _runner.RunAsync(query, requests, result => Application.Current.Dispatcher.Invoke(() =>
            {
                if (generation != _generation) return;
                var s = Sources.First(x => x.Id == result.Id);
                if (!result.Cancelled && !tokenSource.IsCancellationRequested) s.RetryingProblem = false;
                if (result.Cancelled || tokenSource.IsCancellationRequested) s.State = "Cancelled";
                else if (result.Failure is not null) { s.State = result.Failure.Kind.ToString(); s.RetryAt = result.Failure.RetryAt; }
                else if (result.Page is not null)
                {
                    s.State = result.Page.Jobs.Count > 0 ? "Ok" : "Empty"; s.LimitedCoverage = result.Page.Limited; s.Pages++; s.Inspected += result.Page.Inspected;
                    var previous = requests[s.Id]; s.CompletedRequests.Add(previous ?? "");
                    s.PaginationStopped = result.Page.Next is not null && (result.Page.Next == previous || s.CompletedRequests.Contains(result.Page.Next));
                    s.Next = s.PaginationStopped ? null : result.Page.Next;
                    int before = _results.Count;
                    foreach (var job in result.Page.Jobs)
                    {
                        var match = _results.FirstOrDefault(x => x.Job.CanMerge(job));
                        if (match is null) _results.Add(new(job, this)); else { match.Job = match.Job.Merge(job); match.Refresh(); }
                    }
                    s.EmptyPages = _results.Count == before ? s.EmptyPages + 1 : 0;
                    s.ScanLimited = s.EmptyPages >= 12 && s.Next is not null;
                    s.Found = _results.Count(j => j.Source == s.Name || j.Job.OtherLinks.Any(link => link.Source == s.Name));
                }
                s.Refresh(); RefreshJobs();
            }), tokenSource.Token);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { if (generation == _generation) foreach (var s in selected.Where(s => s.State == "Loading")) { s.RetryingProblem = tokenSource.IsCancellationRequested && s.RetryingProblem; s.State = tokenSource.IsCancellationRequested ? "Cancelled" : "Network"; s.Refresh(); } }
        finally
        {
            if (generation == _generation)
            {
                _search = null; IsBusy = false;
                if (tokenSource.IsCancellationRequested) _autoPaused = true;
                Notice = Ui[tokenSource.IsCancellationRequested ? "Stopped" : Sources.Any(s => s.HasError) ? "Partial" : "Complete"];
                RefreshState();
            }
        }
    }
    public void Cancel() { _autoPaused = true; _search?.Cancel(); RefreshState(); }
    public void Resume()
    {
        if (!CanResume) return;
        _autoPaused = false;
        foreach (var s in Sources.Where(s => s.State == "Cancelled" && s.Next is not null)) { s.State = "Ok"; s.Refresh(); }
        Notice = Ui["Complete"]; RefreshState(); JobsUpdated?.Invoke(true);
    }
    public void ToggleFavorite(JobView view)
    {
        var old = Profile.Favorites.FirstOrDefault(x => x.Key == view.Job.Key);
        if (old is not null) Profile.Favorites.Remove(old); else Profile.Favorites.Insert(0, view.Job);
        Save(); foreach (var j in _results) j.Refresh(); RefreshJobs(); Changed(nameof(FavoriteCount));
    }
    public void AddFavorite(JobListing job)
    {
        if (!Profile.Favorites.Any(x => x.Key == job.Key)) Profile.Favorites.Insert(0, job);
        Save(); ShowFavorites = true; RefreshJobs(); Changed(nameof(FavoriteCount));
    }
    public void SaveSearch(string name)
    {
        var query = new SearchQuery(Keyword, City, Radius, Kind).Clean();
        if (string.IsNullOrWhiteSpace(name)) name = query.Label.Length > 0 ? query.Label : "Deutschland";
        var existing = SavedSearches.FirstOrDefault(s => s.Query == query);
        if (existing is not null) SavedSearches.Remove(existing);
        SavedSearches.Insert(0, new(Text.Clean(name), query)); Profile.Searches = SavedSearches.ToList(); Save(); Notice = Ui["Saved"];
    }
    public bool Save()
    {
        try { Store.Save(Profile); if (Store.RecoveryMessage is null && StorageNotice == Ui["StorageError"]) StorageNotice = ""; return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { StorageNotice = Ui["StorageError"]; return false; }
    }
    public void RefreshJobs(bool preservePosition = true)
    {
        JobsUpdating?.Invoke(preservePosition);
        IEnumerable<JobView> jobs = ShowFavorites ? Profile.Favorites.Select(j => VisibleJobs.FirstOrDefault(v => v.Job.Key == j.Key) ?? new JobView(j, this)) : _results;
        if (!ShowFavorites && Sort == 0) jobs = jobs.OrderBy(j => Sources.Select(s => s.Name).ToList().IndexOf(j.Source));
        if (Sort == 1) jobs = jobs.OrderBy(j => j.Title, StringComparer.CurrentCultureIgnoreCase);
        else if (Sort == 2) jobs = jobs.OrderByDescending(j => j.Job.FeedDate ?? DateTimeOffset.MinValue);
        var desired = jobs.ToList();
        Synchronize(VisibleJobs, desired);
        foreach (var job in desired) job.Refresh();
        RefreshFeed();
        RefreshState();
        JobsUpdated?.Invoke(preservePosition);
    }
    private static void Synchronize<T>(ObservableCollection<T> collection, IReadOnlyList<T> desired)
    {
        // Keep existing containers and selection. A Reset would jump to the top.
        for (int i = 0; i < desired.Count; i++)
        {
            if (i < collection.Count && EqualityComparer<T>.Default.Equals(collection[i], desired[i])) continue;
            int old = collection.IndexOf(desired[i]);
            if (old >= 0) collection.Move(old, i); else collection.Insert(i, desired[i]);
        }
        while (collection.Count > desired.Count) collection.RemoveAt(collection.Count - 1);
    }
    private void RefreshFeed()
    {
        var desired = VisibleJobs.Cast<object>().ToList();
        if (!ShowFavorites && _hasSearched && VisibleJobs.Count > 0) desired.Add(_feedStatus);
        Synchronize(FeedItems, desired); _feedStatus.Refresh();
    }
    private void RefreshState() { Changed(nameof(CountLabel)); Changed(nameof(Empty)); Changed(nameof(HasJobs)); Changed(nameof(ShowExamples)); Changed(nameof(ShowLimit)); Changed(nameof(EmptyTitle)); Changed(nameof(EmptyBody)); Changed(nameof(HasNext)); Changed(nameof(HasPendingPages)); Changed(nameof(CanResume)); Changed(nameof(AutoPaused)); Changed(nameof(LoadingExplanation)); Changed(nameof(ActiveLabel)); Changed(nameof(CompactSearchLabel)); Changed(nameof(HasSearchChanges)); Changed(nameof(SourceBrief)); Changed(nameof(SourceAttentionCount)); Changed(nameof(ShowSourceAttention)); Changed(nameof(SourceAttentionLabel)); Changed(nameof(SourceTone)); Changed(nameof(HasSourceStatus)); Changed(nameof(SettingsLabel)); Changed(nameof(SourceStatusMenuLabel)); Changed(nameof(SourceStatusTitle)); Changed(nameof(FilterLabel)); Changed(nameof(SearchLabel)); Changed(nameof(SearchIcon)); Changed(nameof(ThemeLabel)); RefreshFeed(); }
    public void Open(string url)
    {
        try { if (!Links.IsSafe(url)) throw new ArgumentException(); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException) { Notice = Ui["OpenError"]; }
    }
    public void Dispose() { Updates.Dispose(); _search?.Cancel(); _detailCancellation?.Cancel(); Logos.Dispose(); _client.Dispose(); }
}
