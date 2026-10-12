using JobFinder.Core;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

public static class GeneralSourceVerification
{
    public static void Run(string fixtures, Action<bool, string> check)
    {
        if (Directory.Exists(Path.Combine(fixtures, "source-expansion", "general"))) fixtures = Path.Combine(fixtures, "source-expansion", "general");
        string Body(string name) => File.ReadAllText(Path.Combine(fixtures, name + ".html"));
        Uri Url(string name)
        {
            using var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtures, name + ".meta.json")));
            return new Uri(meta.RootElement.GetProperty("finalUrl").GetString()!);
        }
        bool Format(Func<JobPage> action) { try { action(); return false; } catch (SourceFailure e) { return e.Kind == FailureKind.Format; } }
        void Stable(JobPage page, string name) => check(page.Jobs.Count > 0 && page.Jobs.All(j => j.Id.Length > 0 && Links.IsSafe(j.Url) && j.Title.Length > 0) && page.Jobs.Select(j => j.Key).Distinct().Count() == page.Jobs.Count, name + " stable IDs and original links");
        var ba = BaJobsSource.Parse(Body("ba"), Url("ba"));
        Stable(ba, "BA");
        check(ba.Jobs.Count == 25 && ba.Inspected == 25 && ba.Jobs.Any(j => j.Title == "IT-Mitarbeiter (m/w/d)" && j.Company.Contains("MCF") && j.Location.Contains("Berlin")), "BA actual SSR employer/title/place metadata");
        check(ba.Next?.Contains("page=2") == true && ba.Next.Contains("was=IT") && ba.Next.Contains("wo=Berlin") && ba.Next.Contains("umkreis=25"), "BA continuation preserves text, city and radius");
        var ba2 = BaJobsSource.Parse(Body("ba-page2"), Url("ba-page2"));
        check(ba2.Jobs.Count == 25 && !ba.Jobs.Select(j => j.Key).Intersect(ba2.Jobs.Select(j => j.Key)).Any(), "BA observed second page has different IDs");
        check(ba.Jobs.Any(j => j.Salary.Contains('€')) && ba.Jobs.Any(j => j.Salary.Length == 0) && ba.Jobs.All(j => j.FeedDate is not null), "BA visible salary and real publication dates; unknown salary preserved");
        var baTraining = BaJobsSource.Parse(Body("ba-ausbildung"), Url("ba-ausbildung"), new("Fachinformatiker", "Berlin", 25, JobKind.Apprenticeship));
        check(baTraining.Jobs.Count == 25 && baTraining.Jobs.All(j => j.Kind == JobKind.Apprenticeship), "BA explicit Ausbildung category recognizes titles without Ausbildung word");
        var baIntern = BaJobsSource.Parse(Body("ba-internship"), Url("ba-internship"), new("IT", "Berlin", 25, JobKind.Internship));
        check(baIntern.Jobs.Count > 0 && baIntern.Jobs.All(j => j.Kind == JobKind.Internship && !j.Title.StartsWith("Werkstudent", StringComparison.OrdinalIgnoreCase) && !j.Title.StartsWith("Working Student", StringComparison.OrdinalIgnoreCase)) && baIntern.Next is not null, "BA combined Praktikum/Trainee feed excludes pure Werkstudent jobs and retains later pages");
        var baEmpty = BaJobsSource.Parse(Body("ba-empty"), Url("ba-empty"));
        check(baEmpty.Jobs.Count == 0 && baEmpty.Next is null && baEmpty.Inspected == 0, "BA real zero response is empty");
        check(Format(() => BaJobsSource.Parse("<html>Unavailable</html>", Url("ba"))), "BA broken SSR raises Format, not empty success");
        using (var handler = new BaAllFixtureHandler(Body("ba"), Body("ba-ausbildung"), Body("ba-empty"), Body("ba-page2")))
        using (var fixtureClient = new SourceClient(handler, TimeSpan.Zero))
        {
            var combinedSource = new BaJobsSource(fixtureClient);
            var allQuery = new SearchQuery("C++", "Berlin", 25, JobKind.All);
            JobPage Search(string? cursor) => Task.Run(() => combinedSource.SearchAsync(allQuery, cursor, CancellationToken.None)).GetAwaiter().GetResult();
            var first = Search(null);
            check(first.Jobs.Count == 50 && first.Jobs.Any(j => j.Kind == JobKind.Job) && first.Jobs.Any(j => j.Kind == JobKind.Apprenticeship) && handler.Requests.Count == 3 && !first.Limited, "BA All requests all three categories and combines real page fixtures");
            check(handler.Requests.All(u => u.Contains("was=C%2B%2B") && u.Contains("wo=Berlin") && u.Contains("umkreis=25")) && first.Next?.StartsWith("ba-pages-v1:") == true, "BA combined cursor is internal and preserves encoded keyword/city/radius");
            var second = Search(first.Next);
            check(second.Jobs.Count == 1 && second.Next is null && handler.Requests.Count == 4 && !second.Jobs.Select(j => j.Key).Intersect(first.Jobs.Select(j => j.Key)).Any(), "BA All continues only unfinished job branch and exhausts correctly");
            bool CursorFails(string cursor, SearchQuery? changed = null)
            {
                try { Task.Run(() => combinedSource.SearchAsync(changed ?? allQuery, cursor, CancellationToken.None)).GetAwaiter().GetResult(); return false; }
                catch (SourceFailure e) { return e.Kind == FailureKind.Format; }
            }
            string Cursor(string[] urls) => "ba-pages-v1:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { v = 1, urls })));
            string validNext = "https://www.arbeitsagentur.de/jobsuche/suche?suchbereich=jobs&angebotsart=1&was=C%2B%2B&wo=Berlin&umkreis=25&page=2";
            check(CursorFails(Cursor([validNext.Replace("www.arbeitsagentur.de", "example.org")])) && handler.Requests.Count == 4, "BA combined cursor rejects foreign hosts before HTTP");
            check(CursorFails(Cursor([validNext.Replace("page=2", "page=1")])) && CursorFails(Cursor([validNext, validNext])) && CursorFails(first.Next!, allQuery with { City = "Hamburg" }), "BA combined cursor rejects repeated initial pages, duplicate branches and changed query");
            handler.RepeatRequestedPage = true;
            check(CursorFails(Cursor([validNext.Replace("page=2", "page=3")])) && handler.Requests.Count == 5, "BA detects a server response that fails to advance requested page");
        }
        using (var handler = new BaAllFixtureHandler(Body("ba"), Body("ba-ausbildung"), Body("ba-empty"), Body("ba-page2")) { DuplicateTrainingId = true })
        using (var fixtureClient = new SourceClient(handler, TimeSpan.Zero))
        {
            var first = Task.Run(() => new BaJobsSource(fixtureClient).SearchAsync(new("IT", "Berlin", 25, JobKind.All), null, CancellationToken.None)).GetAwaiter().GetResult();
            check(first.Jobs.Count == 49 && first.Jobs.Select(j => j.Key).Distinct().Count() == 49, "BA All deduplicates identical canonical posting IDs across categories");
        }

        var ww = WorkwiseSource.Parse(Body("workwise"), Url("workwise"));
        Stable(ww, "Workwise");
        check(ww.Jobs.Count == 14 && ww.Jobs.Any(j => j.Title.Contains("Cisco") && j.Company == "DATAGROUP Operations GmbH" && j.Location.Contains("10178 Berlin") && j.Location.Contains("Nürnberg")), "Workwise complete locations come from observed card SSR");
        check(ww.Jobs.Any(j => j.Remote) && ww.Jobs.Any(j => j.Salary.Contains('€')) && ww.Limited, "Workwise public remote/salary fields and limited search coverage");
        var ww2 = WorkwiseSource.Parse(Body("workwise-page2"), Url("workwise-page2"));
        check(ww.Next == "https://www.workwise.io/jobs/berlin/2" && !ww.Jobs.Select(j => j.Key).Intersect(ww2.Jobs.Select(j => j.Key)).Any(), "Workwise real next-page link and different IDs");
        var wwFiltered = WorkwiseSource.Parse(Body("workwise"), Url("workwise"), new("Cisco", "Berlin", 25, JobKind.Job));
        check(wwFiltered.Jobs.Count == 1 && wwFiltered.Next is not null && wwFiltered.Limited, "Workwise local text match retains continuation");
        check(!new WorkwiseSource(null!).Supports(JobKind.Internship) && !new WorkwiseSource(null!).Supports(JobKind.Apprenticeship), "Workwise advertises only verified job category");
        check(Format(() => WorkwiseSource.Parse("<html>Unavailable</html>", Url("workwise"))), "Workwise changed page is a source error");

        var bund = BundRssSource.Parse(Body("bund-rss"), Url("bund-rss"));
        Stable(bund, "bund.de RSS");
        check(bund.Inspected == 500 && bund.Jobs.Count == 500 && bund.Next is null && bund.Limited, "bund.de advertised RSS supplies bounded 500 real jobs");
        check(bund.Jobs.Any(j => j.Title.Contains("Forsteinrichtung") && j.Company == "Landesbetrieb Forst Brandenburg" && j.Location == "16225 Eberswalde" && j.FeedDate is not null), "bund.de RSS actual employer, postcode and date");
        check(bund.Jobs.All(j => new Uri(j.Url).Fragment.Length == 0 && j.Salary.Length == 0 && j.Start.Length == 0), "bund.de fragment tracking removed and unknown metadata preserved");
        var bundPostal = BundRssSource.Parse(Body("bund-rss"), Url("bund-rss"), new("", "16225"));
        check(bundPostal.Jobs.Count > 0 && bundPostal.Jobs.All(j => j.Location.Contains("16225")) && bundPostal.Limited, "bund.de local postcode match does not claim radius search");
        var bundIt = BundRssSource.Parse(Body("bund-rss"), Url("bund-rss"), new("IT", "Berlin"));
        check(bundIt.Jobs.Count > 0 && bundIt.Jobs.All(j => System.Text.RegularExpressions.Regex.IsMatch(Text.Normalize(j.Title + " " + j.Company + " " + j.Summary), @"\bit\b")), "Local IT search does not match letters buried in Mitarbeiter or arbeiten");
        check(Format(() => BundRssSource.Parse("<!DOCTYPE rss [<!ENTITY e SYSTEM 'file:///C:/Windows/win.ini'>]><rss><channel>&e;</channel></rss>", Url("bund-rss"))), "bund.de RSS rejects external XML entities");

        var jw = JobwareSource.Parse(Body("jobware-query"), Url("jobware-query"));
        Stable(jw, "Jobware");
        check(jw.Jobs.Count == 20 && jw.Jobs.All(j => j.Company.Length > 0 && j.Location.Length > 0 && j.FeedDate is not null), "Jobware actual employer, place and dates");
        var jw2 = JobwareSource.Parse(Body("jobware-page2"), Url("jobware-page2"));
        check(jw.Next?.Contains("page=2") == true && !jw.Jobs.Select(j => j.Key).Intersect(jw2.Jobs.Select(j => j.Key)).Any(), "Jobware public page=2 actually advances");
        check(jw.Jobs.All(j => j.Salary.Length == 0) && jw.Jobs.Any(j => j.Remote), "Jobware salary-button text is not invented salary");
        check(JobwareSource.SearchUrl(new("IT", "10178", 50)).Contains("l=10178&r=50"), "Jobware verified postcode/radius parameter names");
        var jwTraining = JobwareSource.Parse(Body("jobware-apprenticeship"), Url("jobware-apprenticeship"), new("Fachinformatiker", "Berlin", 25, JobKind.Apprenticeship));
        check(jwTraining.Jobs.Count > 0 && jwTraining.Jobs.All(j => j.Kind == JobKind.Apprenticeship), "Jobware official Ausbildung filter returns apprenticeships");
        var jwIntern = JobwareSource.Parse(Body("jobware-internship"), Url("jobware-internship"), new("IT", "Berlin", 25, JobKind.Internship));
        check(jwIntern.Jobs.Count > 0 && jwIntern.Jobs.All(j => j.Kind == JobKind.Internship), "Jobware student jobs stay Job when selecting Praktikum");
        var jwEmpty = JobwareSource.Parse(Body("jobware-empty"), Url("jobware-empty"));
        check(jwEmpty.Jobs.Count == 0 && jwEmpty.Next is null, "Jobware real empty result");

        var ia = InteramtSource.Parse(Body("interamt-query"), Url("interamt-query"));
        Stable(ia, "INTERAMT");
        check(ia.Jobs.Count == 10 && ia.Jobs.Any(j => j.Location.Contains("Berlin") && j.Salary.Contains("TVöD") && j.Remote), "INTERAMT filtered public table preserves location/pay grade/hybrid");
        check(ia.Next?.Contains("/koop/app/trefferliste?") == true && ia.Next.Contains("loadMoreLink"), "INTERAMT only observed same-session load-more callback is followed");
        var iaMore = InteramtSource.Parse(Body("interamt-paging-more"), Url("interamt-paging-more"));
        check(iaMore.Jobs.Count == 20 && iaMore.Jobs.Select(j => j.Key).Except(ia.Jobs.Select(j => j.Key)).Count() == 10, "INTERAMT public continuation grows ten to twenty actual jobs");
        check(ia.Jobs.All(j => new Uri(j.Url).AbsolutePath == "/koop/app/stelle" && j.Url.EndsWith("id=" + j.Id)), "INTERAMT original links use stable public posting IDs, not Wicket edit actions");
        check(Format(() => InteramtSource.Parse("<html>Session expired</html>", Url("interamt-query"))), "INTERAMT expired layout reports failure");

        var berlin = BerlinCareerSource.Parse(Body("berlin-list"), Url("berlin-list"));
        Stable(berlin, "Karriereportal Berlin");
        var berlin2 = BerlinCareerSource.Parse(Body("berlin-page2"), Url("berlin-page2"));
        check(berlin.Jobs.Count == 20 && berlin2.Jobs.Count == 20 && !berlin.Jobs.Select(j => j.Key).Intersect(berlin2.Jobs.Select(j => j.Key)).Any(), "Berlin public start=20 is a distinct second page");
        var berlinIt = BerlinCareerSource.Parse(Body("berlin-query"), Url("berlin-query"), new("IT", "Berlin"));
        check(berlinIt.Jobs.Count == 3 && berlinIt.Jobs.All(j => j.Title.Contains("IT")), "Berlin observed GET form filter narrows 570 to three jobs");
        check(berlin.Jobs.All(j => j.Location.Length == 0) && berlin.Limited, "Berlin regional source never invents posting locations");
        check(BerlinCareerSource.Parse(Body("berlin-list"), Url("berlin-list"), new("", "Hamburg")).Jobs.Count == 0, "Berlin regional source does not produce false Hamburg matches");
        check(berlin.Next?.Contains("start=20") == true, "Berlin follows actual public pager");

        check(StellenanzeigenSource.ResolveCity(Body("stellenanzeigen-city"), "Berlin") == "M-DE-5165", "stellenanzeigen resolves observed Berlin municipal ID");
        check(StellenanzeigenSource.ResolveCity(Body("stellenanzeigen-hamburg"), "Hamburg").Length > 0 && StellenanzeigenSource.ResolveCity(Body("stellenanzeigen-city"), "Hamburg").Length == 0, "stellenanzeigen city resolver validates exact requested city");
        var sde = StellenanzeigenSource.Parse(Body("stellenanzeigen-plural-city"), Url("stellenanzeigen-plural-city"), new("IT", "Berlin"));
        Stable(sde, "stellenanzeigen.de");
        check(sde.Jobs.Count > 0 && sde.Jobs.All(j => j.Company.Length > 0 && j.Location.Length > 0) && sde.Next is null && sde.Limited, "stellenanzeigen real first block has honest bounded coverage");
        check(Format(() => StellenanzeigenSource.Parse(Body("stellenanzeigen-query"), Url("stellenanzeigen-query"), new("IT", "Berlin"))), "Ignored singular locationId query is rejected as unapplied geography");
        var sdeEmpty = StellenanzeigenSource.Parse(Body("stellenanzeigen-empty"), Url("stellenanzeigen-empty"));
        check(sdeEmpty.Jobs.Count == 0 && sdeEmpty.Inspected == 0 && sdeEmpty.Next is null, "stellenanzeigen actual zero result excludes recommendations");
        check(Format(() => StellenanzeigenSource.Parse("<html>Unavailable</html>", Url("stellenanzeigen-query"))), "stellenanzeigen unknown layout does not become empty success");

        foreach (var (name, page, titleFragment) in new[] { ("ba-detail", ba, "IT-Mitarbeiter"), ("workwise-detail", ww, "Cisco"), ("jobware-detail", jw, "Sachgebietsleitung"), ("berlin-detail", berlin, "IT – Koordinator") })
        {
            var job = page.Jobs.First(j => j.Title.Contains(titleFragment));
            var details = DetailParser.Parse(Body(name), Url(name), job);
            check(details.HasDescription && details.Blocks.Sum(b => b.Text.Length) > 800, name + " full original JobPosting description verified");
        }
        var bundDetails = DetailParser.Parse(Body("bund-detail"), Url("bund-detail"), bund.Jobs.First(j => j.Title.Contains("Forsteinrichtung")));
        check(bundDetails.HasDescription && bundDetails.Contacts.Any(c => c.Kind == "email") && bundDetails.Contacts.All(c => !c.Value.Contains("redaktion-support", StringComparison.OrdinalIgnoreCase)), "bund.de scoped detail contains employer contact and excludes site footer");
        var iaOriginal = InteramtSource.Parse(Body("interamt-results"), Url("interamt-results"));
        var iaDetails = DetailParser.Parse(Body("interamt-detail"), Url("interamt-detail"), iaOriginal.Jobs.First(j => j.Id == "1501408"));
        check(iaDetails.HasDescription && iaDetails.Blocks.Sum(b => b.Text.Length) > 1000 && iaDetails.Contacts.Any(c => c.Kind == "email"), "INTERAMT original richtext scope provides real full description and contact");
    }

    private sealed class BaAllFixtureHandler(string jobs, string training, string empty, string secondJobs) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public bool RepeatRequestedPage { get; set; }
        public bool DuplicateTrainingId { get; set; }
        private static JsonNode State(string html) => JsonNode.Parse(new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html).QuerySelector("script#ng-state")!.TextContent)!;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.AbsoluteUri;
            Requests.Add(url);
            bool apprentice = url.Contains("suchbereich=ausbildung"), intern = url.Contains("angebotsart=34");
            int page = System.Text.RegularExpressions.Regex.Match(url, @"(?:[?&])page=(\d+)") is { Success: true } match ? int.Parse(match.Groups[1].Value) : 1;
            var root = State(intern ? empty : apprentice ? training : page == 1 ? jobs : secondJobs);
            var result = root["suchergebnis"]!;
            result["page"] = RepeatRequestedPage && page > 1 ? 1 : page;
            result["maxErgebnisse"] = intern ? 0 : apprentice ? 25 : 26;
            if (!apprentice && !intern && page > 1) result["ergebnisliste"] = new JsonArray(result["ergebnisliste"]![0]!.DeepClone());
            if (apprentice && DuplicateTrainingId) result["ergebnisliste"]![0]!["referenznummer"] = State(jobs)["suchergebnis"]!["ergebnisliste"]![0]!["referenznummer"]!.DeepClone();
            string html = "<html><script id='ng-state' type='application/json'>" + root.ToJsonString() + "</script></html>";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(html, Encoding.UTF8, "text/html") });
        }
    }
}
