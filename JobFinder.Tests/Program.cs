using JobFinder.Core;
using System.Net;
using System.Text.Json;

var fixtures = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--")) ?? "../../");
var report = new List<object>();
void Check(bool condition, string name) { if (!condition) throw new Exception(name); report.Add(new { test = name, passed = true }); Console.WriteLine("PASS " + name); }
async Task<SourceFailure> Fails(Func<Task> action, FailureKind kind)
{
    try { await action(); throw new Exception("Expected failure: " + kind); }
    catch (SourceFailure e) { Check(e.Kind == kind, "Source failure classified: " + kind); return e; }
}
try
{
    Check(new ProfileStore(Path.Combine(Path.GetTempPath(), "jobfinder-locale-" + Guid.NewGuid().ToString("N"))).Load().Language == "de", "German is the default language for a fresh installation");
    var languageStore = new ProfileStore(Path.Combine(Path.GetTempPath(), "jobfinder-language-choice-" + Guid.NewGuid().ToString("N")));
    languageStore.Save(new UserProfile { Language = "ru" });
    Check(languageStore.Load().Language == "ru", "An existing explicit Russian language choice survives the new German default");
    languageStore.Save(new UserProfile { Language = "unsupported" });
    Check(languageStore.Load().Language == "de", "An unsupported saved language falls back to German");
    var azubi = AzubiyoSource.Parse(File.ReadAllText(Path.Combine(fixtures, "azubiyo-filtered.txt")), new("https://www.azubiyo.de/ausbildung/berlin/?subject=Fachinformatiker&location=Berlin&radius=25"));
    Check(azubi.Jobs.Count >= 10 && azubi.Jobs.Any(j => j.Title.Contains("Systemintegration") && j.Company.Contains("Universität Berlin") && j.Location.Contains("Berlin")), "Actual Azubiyo title, company, city parsed");
    Check(azubi.Jobs.Any(j => j.Start == "01.09.2027" && j.Salary.Contains('€')), "Training start and salary preserved separately");
    Check(azubi.Next == "https://www.azubiyo.de/ausbildung/berlin/2/?subject=Fachinformatiker&location=Berlin&radius=25", "Verified Azubiyo next-page URL retains filters");
    if (File.Exists(Path.Combine(fixtures, "azubi-empty.txt")))
    {
        var zero = AzubiyoSource.Parse(File.ReadAllText(Path.Combine(fixtures, "azubi-empty.txt")), new("https://www.azubiyo.de/ausbildung/?subject=zzzznonexistent123456&location=Berlin&radius=25"));
        Check(zero.Jobs.Count == 0 && zero.Next is null, "Azubiyo recommendations at zero results are not advertised as matches");
    }
    var praktik = PraktikumSource.Parse(File.ReadAllText(Path.Combine(fixtures, "praktikum-search.txt")), new("https://www.praktikum.info/stellenangebote?text=Design&location=Berlin"));
    Check(praktik.Jobs.Count >= 3 && praktik.Jobs.Any(j => j.Company == "Finanzfluss" && j.Title.Contains("Praktikum") && j.Location.Contains("Berlin")), "Actual internship title, company, city parsed");
    Check(praktik.Jobs.All(j => !j.Location.Contains("Standort") && !j.Title.Contains("&amp;")), "SVG labels and HTML entities are not visible text");
    var arbeit = ArbeitnowSource.Parse(File.ReadAllText(Path.Combine(fixtures, "arbeitnow.txt")), new());
    Check(arbeit.Inspected >= 100 && arbeit.Jobs.Count > 0 && arbeit.Next is not null, "Actual Arbeitnow paginated feed");
    var dev = ArbeitnowSource.Parse(File.ReadAllText(Path.Combine(fixtures, "arbeitnow.txt")), new("Developer", "", 25, JobKind.Job));
    Check(dev.Jobs.Count > 0 && dev.Jobs.All(j => j.Kind == JobKind.Job), "Job keyword and kind filter");
    var missing = ArbeitnowSource.Parse(File.ReadAllText(Path.Combine(fixtures, "arbeitnow.txt")), new("zzzz-no-such-position-123456"));
    Check(missing.Jobs.Count == 0 && missing.Next is not null && missing.Inspected > 0, "No local match does not hide further pages");
    Check(Text.Classify("Softwareentwickler", ["full-time"]) == JobKind.Job && Text.Classify("Werkstudent Softwareentwicklung") == JobKind.Job && Text.Classify("Trainee Marketing") == JobKind.Job, "Jobs and Werkstudent are not mislabeled internships");
    Check(Text.Classify("Praktikum Grafik Design") == JobKind.Internship && Text.Classify("Ausbildung Fachinformatiker") == JobKind.Apprenticeship, "Title-based category classification");
    Check(Text.Normalize("München") == Text.Normalize("Munchen"), "City diacritics matching");
    var example = azubi.Jobs[0];
    var fullAzubi = DetailParser.Parse(File.ReadAllText(Path.Combine(fixtures, "azubiyo-tu-detail.txt")), new(example.Url), example);
    Check(fullAzubi.HasDescription && fullAzubi.Blocks.Count > 8 && fullAzubi.Blocks.Any(x => x.Kind == "heading"), "Actual Azubiyo description retains headings, paragraphs and requirements");
    Check(DetailParser.IsLogoUrl(example.LogoUrl) && DetailParser.IsLogoUrl(fullAzubi.LogoUrl) && praktik.Jobs.Any(j => DetailParser.IsLogoUrl(j.LogoUrl)), "Actual source cards expose company logo URLs");
    var fullPraktikum = DetailParser.Parse(File.ReadAllText(Path.Combine(fixtures, "praktikum-detail.txt")), new(praktik.Jobs[0].Url), praktik.Jobs[0]);
    Check(fullPraktikum.HasDescription && fullPraktikum.Blocks.Sum(x => x.Text.Length) > 1500 && fullPraktikum.Blocks.Any(x => x.Kind == "bullet") && fullPraktikum.Blocks.All(x => !x.Text.Contains("&lt;")), "Actual Praktikum entity-encoded and multiline structured description is decoded");
    Check(arbeit.Jobs.All(j => j.Details is not null) && arbeit.Jobs.Any(j => j.Details!.Blocks.Sum(x => x.Text.Length) > 500), "Arbeitnow API retains descriptions beyond the search summary");
    var syntheticPosting = JsonSerializer.Serialize(new Dictionary<string, object> { ["@type"] = "JobPosting", ["title"] = example.Title, ["description"] = "<h2>Aufgaben</h2><p>Beschreibung</p><ul><li>Erste Aufgabe</li></ul><p>jobs@example.org</p><footer>unrelated@example.org</footer><script>not displayed</script>", ["hiringOrganization"] = new { name = example.Company, email = "site-wide@example.org" }, ["applicationContact"] = new { name = "Job contact", email = "recruiting@example.org", telephone = "+49 30 123456" } });
    var syntheticDetails = DetailParser.Parse("<script type='application/ld+json'>" + syntheticPosting + "</script><footer><a href='mailto:footer@example.org'>Support</a></footer>", new(example.Url), example);
    Check(syntheticDetails.Contacts.Count == 4 && syntheticDetails.Contacts.Any(x => x.Value == "recruiting@example.org") && !syntheticDetails.Contacts.Any(x => x.Value.Contains("unrelated") || x.Value.Contains("site-wide") || x.Value.Contains("footer")) && syntheticDetails.Blocks.All(x => !x.Text.Contains("not displayed")), "Only posting description and application contacts are extracted, excluding site footer, organization support and scripts");
    await Fails(() => Task.Run(() => DetailParser.Parse("<script type='application/ld+json'>" + syntheticPosting.Replace(JsonSerializer.Serialize(example.Title), JsonSerializer.Serialize("Another unrelated job")) + "</script>", new(example.Url), example)), FailureKind.Format);
    Check(!DetailParser.IsLogoUrl("https://localhost/a.png") && !DetailParser.IsLogoUrl("https://example.org/logo.png") && !DetailParser.IsLogoUrl("file:///C:/logo.png"), "Logo loading accepts only observed source image hosts");
    var detailHandler = new StubHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(File.ReadAllText(Path.Combine(fixtures, "azubiyo-tu-detail.txt"))) });
    using (var detailClient = new SourceClient(detailHandler, TimeSpan.Zero))
    {
        var service = new JobDetailService(detailClient); await service.LoadAsync(example, default); var cachedDetails = await service.LoadAsync(example, default);
        Check(detailHandler.Count == 1 && cachedDetails.HasDescription, "Repeated detail request shares the source response cache");
    }
    example = example with { Details = fullAzubi };
    Check(!example.CanMerge(example with { Url = example.Url + "other/", Start = "01.08.2028" }), "Distinct training starts are not merged");
    Check(example.CanMerge(example with { Url = example.Url + "?utm_source=test" }) && example.Key == (example with { Url = example.Url + "?utm_source=test" }).Key, "Tracking parameters do not duplicate an original");
    Check(!Links.IsSafe("javascript:alert(1)") && !Links.IsSafe("file:///C:/test") && !Links.IsSafe("https://name:secret@example.com/") && !Links.IsSafe("https://localhost/"), "Original links reject unsafe schemes and embedded credentials");
    Check(Links.Portal("indeed", new("C++ & Design", "Berlin")).Contains("C%2B%2B%20%26%20Design"), "Search terms encoded independently from URL syntax");
    await Fails(() => Task.Run(() => PraktikumSource.Parse("<html><body>Unexpected design</body></html>", new("https://www.praktikum.info/stellenangebote"))), FailureKind.Format);
    var empty = PraktikumSource.Parse("<html><body>Keine passenden Stellenangebote</body></html>", new("https://www.praktikum.info/stellenangebote"));
    Check(empty.Jobs.Count == 0, "Explicit empty state remains distinct from parser failure");
    var unsafeFeed = ArbeitnowSource.Parse("{\"data\":[{\"title\":\"Test\",\"url\":\"javascript:alert(1)\"}],\"links\":{\"next\":\"https://other.example/\"}}", new());
    Check(unsafeFeed.Jobs.Count == 0 && unsafeFeed.Next is null, "Untrusted feed links and next-page host rejected");
    var accessHandler = new StubHandler(_ => new(HttpStatusCode.Forbidden));
    using (var client = new SourceClient(accessHandler, TimeSpan.Zero)) { await Fails(() => client.GetAsync("https://www.azubiyo.de/ausbildung/", default), FailureKind.Access); Check(accessHandler.Count == 1, "403 has no automatic retry or bypass"); }
    var limitHandler = new StubHandler(_ => { var r = new HttpResponseMessage((HttpStatusCode)429); r.Headers.RetryAfter = new(TimeSpan.FromMinutes(1)); return r; });
    using (var client = new SourceClient(limitHandler, TimeSpan.Zero))
    {
        await Fails(() => client.GetAsync("https://www.azubiyo.de/ausbildung/", default), FailureKind.RateLimit);
        await Fails(() => client.GetAsync("https://www.azubiyo.de/ausbildung/2/", default), FailureKind.RateLimit);
        Check(limitHandler.Count == 1, "429 cooldown shared across provider pages");
    }
    var cacheHandler = new StubHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent("real response") });
    using (var client = new SourceClient(cacheHandler, TimeSpan.Zero))
    {
        var first = await client.GetAsync("https://www.arbeitnow.com/api/job-board-api", default);
        var cached = await client.GetAsync("https://www.arbeitnow.com/api/job-board-api", default);
        Check(first.Body == cached.Body && cached.Cached && cacheHandler.Count == 1, "Repeated same query uses short memory cache");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await client.GetAsync("https://www.arbeitnow.com/api/job-board-api", cancelled.Token); throw new Exception("Canceled request was not canceled"); } catch (OperationCanceledException) { Check(true, "Cancellation applies before cached response"); }
    }
    var folder = Path.Combine(fixtures, "test-state", Guid.NewGuid().ToString("N"));
    var store = new ProfileStore(folder); var profile = new UserProfile { Language = "de", Favorites = [example], Searches = [new("IT Berlin", new("Fachinformatiker", "Berlin", 25, JobKind.Apprenticeship))] };
    store.Save(profile); store.Save(profile);
    var reloaded = store.Load(); Check(reloaded.Favorites.Count == 1 && reloaded.Favorites[0].Key == example.Key && reloaded.Searches[0].Query.Kind == JobKind.Apprenticeship && reloaded.Language == "de", "Atomic favorite, search and language persistence");
    Check(reloaded.Favorites[0].Details?.Blocks.Count == fullAzubi.Blocks.Count && reloaded.Favorites[0].LogoUrl == example.LogoUrl, "Full descriptions and logos survive favorite profile reload");
    File.WriteAllText(store.FilePath, "broken profile"); var recovered = store.Load();
    Check(recovered.Favorites.Count == 1 && store.RecoveryMessage == "backup" && Directory.GetFiles(folder, "profile.damaged-*.json").Length > 0, "Corrupt profile preserved and backup recovered");
    store.Save(recovered); Check(store.Load().Favorites.Count == 1, "Recovered profile persists without losing backup");
    var mixed = new SearchRunner([new TestSource("ok", false), new TestSource("bad", true)]); var received = new List<SourceResult>();
    await mixed.RunAsync(new(), new Dictionary<string, string?> { ["ok"] = null, ["bad"] = null }, r => received.Add(r), default);
    Check(received.Count == 2 && received.Any(r => r.Page?.Jobs.Count == 1) && received.Any(r => r.Failure?.Kind == FailureKind.Access), "Failed provider does not discard successful source");
    await CatalogVerification.RunAsync(Check);
    if (Directory.Exists(Path.Combine(fixtures, "source-expansion", "training"))) TrainingSourceVerification.Run(Path.Combine(fixtures, "source-expansion", "training"), Check);
    if (Directory.Exists(Path.Combine(fixtures, "source-expansion", "internship"))) InternshipSourceVerification.Run(Path.Combine(fixtures, "source-expansion", "internship"), Check);
    if (Directory.Exists(Path.Combine(fixtures, "source-expansion", "general"))) GeneralSourceVerification.Run(fixtures, Check);
    await UpdateClientVerification.Run(fixtures, Check);
    await UpdateInstallerVerification.Run(fixtures, Check);
    if (args.Contains("--live"))
    {
        using var client = new SourceClient();
        var queries = new (IJobSource Source, SearchQuery Query)[] { (new ArbeitnowSource(client), new("Developer", "", 25, JobKind.Job)), (new PraktikumSource(client), new("Design", "Berlin", 25, JobKind.Internship)), (new AzubiyoSource(client), new("Fachinformatiker", "Berlin", 25, JobKind.Apprenticeship)) };
        foreach (var (source, query) in queries)
        {
            var page = await source.SearchAsync(query, null, default);
            Check(page.Jobs.Count > 0 && page.Jobs.All(j => Links.IsSafe(j.Url) && j.Title.Length > 0), "LIVE " + source.Name + " " + query.Kind);
            var liveDetails = await new JobDetailService(client).LoadAsync(page.Jobs[0], default);
            Check(liveDetails.HasDescription && liveDetails.Blocks.Sum(x => x.Text.Length) > 300, "LIVE " + source.Name + " full posting description");
            report.Add(new { source = source.Name + " details", original = page.Jobs[0].Url, blocks = liveDetails.Blocks.Count, contacts = liveDetails.Contacts.Count, logoProvided = liveDetails.LogoUrl.Length > 0, verifiedAt = DateTimeOffset.UtcNow });
            report.Add(new { source = source.Name, query, found = page.Jobs.Count, inspected = page.Inspected, next = page.Next, sample = page.Jobs.Take(3).Select(j => new { j.Title, j.Company, j.Location, j.Start, j.Url }), verifiedAt = DateTimeOffset.UtcNow });
            if (source.Id == "azubiyo" && page.Next is not null)
            {
                var next = await source.SearchAsync(query, page.Next, default);
                Check(next.Jobs.Count > 0 && next.Jobs.Any(j => !page.Jobs.Any(old => old.Key == j.Key)), "LIVE Azubiyo pagination produces different listings");
                report.Add(new { source = "Azubiyo page 2", found = next.Jobs.Count, sample = next.Jobs.Take(2).Select(j => new { j.Title, j.Start, j.Url }) });
            }
            if (source.Id == "praktikum" && page.Next is not null)
            {
                var next = await source.SearchAsync(query, page.Next, default);
                Check(next.Jobs.Count > 0 && next.Jobs.Any(j => !page.Jobs.Any(old => old.Key == j.Key)) && page.Next.Contains("radius=25"), "LIVE Praktikum pagination retains radius and adds listings");
            }
        }
        if (args.Contains("--extended"))
        {
            foreach (var (source, query) in new (IJobSource, SearchQuery)[] { (new PraktikumSource(client), new("Marketing", "Hamburg", 25, JobKind.Internship)), (new AzubiyoSource(client), new("Fachinformatiker", "München", 25, JobKind.Apprenticeship)), (new PraktikumSource(client), new("zzzznonexistent123456", "Berlin", 25, JobKind.Internship)), (new AzubiyoSource(client), new("zzzznonexistent123456", "Berlin", 25, JobKind.Apprenticeship)) })
            {
                var page = await source.SearchAsync(query, null, default);
                bool expectedEmpty = query.Keyword.StartsWith("zzzz");
                Check(expectedEmpty ? page.Jobs.Count == 0 : page.Jobs.Count > 0, "LIVE " + source.Name + " " + (expectedEmpty ? "empty query" : query.City));
                report.Add(new { source = source.Name, query, found = page.Jobs.Count, empty = expectedEmpty, verifiedAt = DateTimeOffset.UtcNow });
            }
        }
    }
    var path = Path.Combine(fixtures, args.Contains("--live") ? "verification-live.json" : "verification-core.json");
    File.WriteAllText(path, JsonSerializer.Serialize(new { passed = report.Count(x => x.GetType().GetProperty("test") is not null), report }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("Report: " + path);
}
catch (Exception e) { Console.Error.WriteLine(e); Environment.ExitCode = 1; }

sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> create) : HttpMessageHandler
{
    public int Count { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation) { cancellation.ThrowIfCancellationRequested(); Count++; var result = create(request); result.RequestMessage = request; return Task.FromResult(result); }
}
sealed class TestSource(string id, bool fail) : IJobSource
{
    public string Id => id; public string Name => id; public bool Supports(JobKind kind) => true;
    public Task<JobPage> SearchAsync(SearchQuery q, string? next, CancellationToken cancellation) => fail ? Task.FromException<JobPage>(new SourceFailure(FailureKind.Access, "Access blocked")) : Task.FromResult(new JobPage([new() { Id = "1", Source = id, Title = "real isolated example", Url = "https://example.com/job/1" }], null, 1));
}
