using JobFinder.Core;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace JobFinder;

public sealed class PortalView(SourceDefinition source, Ui ui)
{
    public string Id => source.Id;
    public string Name => source.Name;
    public string Categories => string.Join(" · ", source.Kinds.Select(kind => ui[kind.ToString()]));
    public string Reason => source.Reason(ui.IsRu);
    public bool Supports(JobKind kind) => source.Supports(kind);
}

public sealed class SourceDirectoryViewModel : Observable, IDisposable
{
    public ViewModel App { get; }
    public Ui Ui => App.Ui;
    public bool IsStatusView { get; }
    public bool ShowCatalog => !IsStatusView;
    public ObservableCollection<SourceView> Feeds { get; } = [];
    public ObservableCollection<PortalView> Portals { get; } = [];
    private string _search = "";
    public string Search { get => _search; set { if (Set(ref _search, value)) Filter(); } }
    private JobKind _category;
    private int _mode;
    public bool AllKinds { get => _category == JobKind.All; set { if (value) Category(JobKind.All); } }
    public bool Work { get => _category == JobKind.Job; set { if (value) Category(JobKind.Job); } }
    public bool Internship { get => _category == JobKind.Internship; set { if (value) Category(JobKind.Internship); } }
    public bool Training { get => _category == JobKind.Apprenticeship; set { if (value) Category(JobKind.Apprenticeship); } }
    public bool Both { get => _mode == 0; set { if (value) Mode(0); } }
    public bool FeedOnly { get => _mode == 1; set { if (value) Mode(1); } }
    public bool PortalOnly { get => _mode == 2; set { if (value) Mode(2); } }
    public bool HasFeeds => Feeds.Count > 0;
    public bool HasPortals => Portals.Count > 0;
    public bool Empty => !HasFeeds && !HasPortals;
    public Command EnableVisible { get; }
    public Command DisableVisible { get; }
    public string Summary => Ui.IsRu
        ? $"Выбрано для поиска: {App.Sources.Count(s => s.Enabled)} из {App.Sources.Count} · На сайте: {SourceCatalog.Sources.Count(s => !s.InFeed)} · Показано: {Feeds.Count + Portals.Count}"
        : $"Für Suche ausgewählt: {App.Sources.Count(s => s.Enabled)} von {App.Sources.Count} · Auf Website: {SourceCatalog.Sources.Count(s => !s.InFeed)} · Angezeigt: {Feeds.Count + Portals.Count}";
    public SourceDirectoryViewModel(ViewModel app, bool statusView = false)
    {
        App = app; IsStatusView = statusView; App.PropertyChanged += AppChanged; Ui.PropertyChanged += LanguageChanged;
        foreach (var source in App.Sources) source.PropertyChanged += SourceChanged;
        EnableVisible = new(_ => { foreach (var source in Feeds.ToArray()) source.Enabled = true; }, _ => HasFeeds);
        DisableVisible = new(_ => { foreach (var source in Feeds.ToArray()) source.Enabled = false; }, _ => HasFeeds);
        Filter();
    }
    private void Category(JobKind kind) { _category = kind; Changed(nameof(AllKinds)); Changed(nameof(Work)); Changed(nameof(Internship)); Changed(nameof(Training)); Filter(); }
    private void Mode(int mode) { _mode = mode; Changed(nameof(Both)); Changed(nameof(FeedOnly)); Changed(nameof(PortalOnly)); Filter(); }
    private void Filter()
    {
        string term = Text.Normalize(Search);
        Feeds.Clear(); Portals.Clear();
        if (IsStatusView)
        {
            if (App.HasSourceStatus) foreach (var source in App.Sources.Where(s => s.Enabled && s.State is not "Off" and not "Skipped").OrderByDescending(s => s.NeedsAttention).ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)) Feeds.Add(source);
            Changed(nameof(HasFeeds)); Changed(nameof(HasPortals)); Changed(nameof(Empty)); Changed(nameof(Summary));
            return;
        }
        if (_mode != 2) foreach (var source in App.Sources.Where(s => s.Source.Supports(_category) && Text.Normalize(s.Name + " " + s.Categories).Contains(term)).OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)) Feeds.Add(source);
        if (_mode != 1) foreach (var source in SourceCatalog.Sources.Where(s => !s.InFeed && s.Supports(_category) && Text.Normalize(s.Name + " " + string.Join(" ", s.Kinds.Select(k => Ui[k.ToString()]))).Contains(term)).OrderBy(s => s.Name)) Portals.Add(new(source, Ui));
        Changed(nameof(HasFeeds)); Changed(nameof(HasPortals)); Changed(nameof(Empty)); Changed(nameof(Summary));
    }
    private void SourceChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(SourceView.Enabled)) Changed(nameof(Summary)); }
    private void AppChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName is nameof(ViewModel.SourceBrief) or "") { if (IsStatusView) Filter(); else Changed(nameof(Summary)); } }
    private void LanguageChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(Ui.IsRu)) Filter(); }
    public void Dispose() { App.PropertyChanged -= AppChanged; Ui.PropertyChanged -= LanguageChanged; foreach (var source in App.Sources) source.PropertyChanged -= SourceChanged; }
}
