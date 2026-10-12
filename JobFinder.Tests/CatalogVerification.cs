using JobFinder.Core;

public static class CatalogVerification
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        check(SourceCatalog.Sources.Select(s => s.Id).Distinct().Count() == SourceCatalog.Sources.Count, "Catalog has stable unique source IDs");
        check(SourceCatalog.Sources.All(s => Links.IsSafe(SourceCatalog.Portal(s.Id, new("Design & IT", "Berlin", 25, JobKind.Internship)))), "Catalog portal links safely encode search fields");
        using var client = new SourceClient(new NoNetwork(), TimeSpan.Zero);
        var feeds = SourceCatalog.Create(client);
        check(feeds.Count == SourceCatalog.FeedIds().Count && feeds.All(feed => SourceCatalog.Find(feed.Id) is { InFeed: true }), "Catalog factories and feed membership agree");
        check(feeds.All(feed => new[] { JobKind.Job, JobKind.Internship, JobKind.Apprenticeship }.All(kind => feed.Supports(kind) == SourceCatalog.Find(feed.Id)!.Supports(kind))), "Source category capabilities match actual adapters");
        check(!SourceCatalog.IsSourceUrl("https://localhost/test") && !SourceCatalog.IsSourceUrl("https://www.arbeitnow.com:444/test") && !SourceCatalog.SameSource("https://www.arbeitnow.com/api/job-board-api", "https://www.azubiyo.de/ausbildung/"), "Expanded source allowlist rejects private endpoints and cross-provider redirects");
        check(!SourceCatalog.IsReadOnlyPostUrl("https://www.arbeitnow.com/account/delete"), "Only registered read-only search POST paths are permitted");
        var posting = new JobListing { Id = "logo-empty", Source = "Arbeitnow", Title = "Developer", Url = "https://www.arbeitnow.com/jobs/logo-empty" };
        var details = DetailParser.Parse("<script type='application/ld+json'>{\"@type\":\"JobPosting\",\"title\":\"Developer\",\"description\":\"<p>Real job description</p>\",\"hiringOrganization\":{\"logo\":\"\"}}</script>", new(posting.Url), posting);
        check(details.HasDescription && details.LogoUrl.Length == 0, "An empty organization logo does not resolve to the posting HTML page");
        check(!Text.Matches(posting with { Title = "Mitarbeiter im Vertrieb", Company = "Betrieb", Summary = "Teamarbeit" }, new("IT")) && Text.Matches(posting with { Title = "IT-Administrator" }, new("IT")), "Short job keywords match words rather than letters inside unrelated German text");
        var query = new SearchQuery("Fachinformatiker", "Berlin", 25, JobKind.Apprenticeship);
        var profile = new UserProfile { EnabledSources = ["azubiyo"], Language = "de", Theme = "light", LastQuery = query, Searches = [new("IT", query)] };
        var oldSearches = profile.Searches;
        SourceCatalog.UpdateProfileSources(profile);
        check(!profile.EnabledSources.Contains("arbeitnow") && !profile.EnabledSources.Contains("praktikum") && profile.EnabledSources.Contains("azubiyo") && profile.EnabledSources.Intersect(SourceCatalog.FeedIds().Except(SourceCatalog.LegacyFeedIds)).Count() == SourceCatalog.FeedIds().Except(SourceCatalog.LegacyFeedIds).Count(), "Migration adds new feeds while preserving previously disabled sources");
        string? added = SourceCatalog.FeedIds().Except(SourceCatalog.LegacyFeedIds).FirstOrDefault();
        if (added is not null) { profile.EnabledSources.Remove(added); SourceCatalog.UpdateProfileSources(profile); check(!profile.EnabledSources.Contains(added), "An added feed disabled by the user stays disabled after another upgrade"); }
        check(ReferenceEquals(profile.LastQuery, query) && ReferenceEquals(profile.Searches, oldSearches) && profile.Language == "de" && profile.Theme == "light", "Source migration preserves queries, language and theme");
        var tracker = new Tracker();
        var sources = Enumerable.Range(0, 17).Select(i => (IJobSource)new ConcurrentSource(i, tracker)).ToArray();
        var results = new List<SourceResult>();
        await new SearchRunner(sources).RunAsync(new(), sources.ToDictionary(s => s.Id, _ => (string?)null), r => { lock (results) results.Add(r); }, default);
        check(tracker.Peak <= 6 && tracker.Peak > 1 && results.Count == sources.Length && results.Count(r => r.Failure is not null) == 1, "Many feeds load with bounded concurrency and an independent failed source");
    }
    private sealed class NoNetwork : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation) => throw new InvalidOperationException("Network not used by catalog test"); }
    private sealed class Tracker { public int Active; public int Peak; }
    private sealed class ConcurrentSource(int index, Tracker tracker) : IJobSource
    {
        public string Id => "test-" + index;
        public string Name => Id;
        public bool Supports(JobKind kind) => true;
        public async Task<JobPage> SearchAsync(SearchQuery query, string? next, CancellationToken cancellation)
        {
            lock (tracker) { tracker.Active++; tracker.Peak = Math.Max(tracker.Peak, tracker.Active); }
            try { await Task.Delay(30, cancellation); if (index == 5) throw new SourceFailure(FailureKind.Network, "Controlled failure"); return new([], null, 0); }
            finally { lock (tracker) tracker.Active--; }
        }
    }
}
