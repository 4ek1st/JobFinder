using JobFinder.Core;
using System.Text.Json;

public static class TrainingSourceVerification
{
    public static void Run(string fixtures, Action<bool, string> check)
    {
        (string Body, Uri Url) Fixture(string name)
        {
            using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtures, name + ".json")));
            return (File.ReadAllText(Path.Combine(fixtures, name + ".html")), new(metadata.RootElement.GetProperty("url").GetString()!));
        }
        bool Format(Action parse)
        {
            try { parse(); return false; } catch (SourceFailure e) { return e.Kind == FailureKind.Format; }
        }
        var q = new SearchQuery("Fachinformatiker", "Berlin", 25, JobKind.Apprenticeship);
        var f = Fixture("ausbildung-filtered");
        var ausbildung = AusbildungSource.Parse(f.Body, f.Url);
        check(ausbildung.Jobs.Count == 20 && ausbildung.Jobs.Any(j => j.Title.Contains("Systemintegration") && j.Company.Contains("Grone") && j.Location.Contains("Berlin")), "Ausbildung.de real primary title/company/location and stable offer IDs");
        check(ausbildung.Next == f.Url.AbsoluteUri + "&from=20" && ausbildung.Jobs.All(j => Guid.TryParse(j.Id, out _) && j.Kind == JobKind.Apprenticeship), "Ausbildung.de verified offset pagination preserves search and category");
        f = Fixture("ausbildung-page2");
        var ausbildung2 = AusbildungSource.Parse(f.Body, f.Url);
        check(ausbildung2.Jobs.Count == 3 && ausbildung2.Next is null && !ausbildung2.Jobs.Any(j => ausbildung.Jobs.Any(first => first.Key == j.Key)), "Ausbildung.de last page uses 23 offer groups rather than 85 advertised places");
        f = Fixture("ausbildung-last-empty");
        var beyond = AusbildungSource.Parse(f.Body, f.Url);
        check(beyond.Jobs.Count == 0 && beyond.Next is null, "Ausbildung.de exhausted continuation is an empty page");
        f = Fixture("ausbildung-empty");
        check(AusbildungSource.Parse(f.Body, f.Url).Jobs.Count == 0, "Ausbildung.de genuine empty search excludes recommendations");
        check(Format(() => AusbildungSource.Parse("<html><body>Unexpected layout</body></html>", new(AusbildungSource.SearchUrl(q)))), "Ausbildung.de layout failure is distinct from zero matches");
        check(AusbildungSource.SearchUrl(new("C++ & Design", "München", 50)).Contains("C%2B%2B%20%26%20Design%7CM%C3%BCnchen") && AusbildungSource.SearchUrl(new("Test|Other", "Berlin")).Contains("Test%20Other%7CBerlin"), "Ausbildung.de compound search encoding does not inject location separators");
        f = Fixture("ausbildung-detail");
        check(DetailParser.Parse(f.Body, f.Url, ausbildung.Jobs[0]).Blocks.Sum(x => x.Text.Length) > 1200, "Ausbildung.de exact posting structured description parses with generic detail service");

        f = Fixture("aubi-training");
        var aubi = AubiPlusSource.Parse(f.Body, f.Url);
        check(aubi.Jobs.Count == 8 && aubi.Jobs.Any(j => j.Id == "463235" && j.Company == "Berliner Wasserbetriebe AöR" && j.Location == "10864 Berlin" && j.Start == "01.09.2027"), "AUBI-plus real primary cards preserve employer, postcode, title and training start");
        check(aubi.Jobs.All(j => j.Kind == JobKind.Apprenticeship && j.Title.Contains("Fachinformatik")) && aubi.Jobs.Any(j => j.Title.StartsWith("Umschulung")), "AUBI-plus advertised training classification preserves the source's real vocational retraining offer");
        f = Fixture("aubi-berlin");
        var aubiBerlin = AubiPlusSource.Parse(f.Body, f.Url);
        check(aubiBerlin.Jobs.Count == 10 && aubiBerlin.Next is not null && aubiBerlin.Next.Contains("seite=2") && aubiBerlin.Next.Contains("aSuggest=Berlin") && aubiBerlin.Next.Contains("fGeo=25"), "AUBI-plus primary offer groups exclude collapsed company recommendations and retain pagination filters");
        f = Fixture("aubi-page2");
        var aubi2 = AubiPlusSource.Parse(f.Body, f.Url);
        check(aubi2.Jobs.Count == 10 && !aubi2.Jobs.Any(j => aubiBerlin.Jobs.Any(first => first.Key == j.Key)), "AUBI-plus actual second page advances primary offer groups");
        f = Fixture("aubi-hamburg");
        var hamburg = AubiPlusSource.Parse(f.Body, f.Url);
        check(hamburg.Jobs.Count == 1 && hamburg.Jobs[0].Company == "Beiersdorf AG" && hamburg.Jobs[0].Location == "22529 Hamburg" && hamburg.Next is null, "AUBI-plus alternate city/radius query produces its real local vacancy");
        f = Fixture("aubi-empty");
        check(AubiPlusSource.Parse(f.Body, f.Url).Jobs.Count == 0 && Format(() => AubiPlusSource.Parse("<html><body>Unexpected layout</body></html>", f.Url)), "AUBI-plus genuine empty results remain distinct from an unreadable response");
        f = Fixture("aubi-detail");
        check(DetailParser.Parse(f.Body, f.Url, aubi.Jobs.First(j => j.Id == "463235")).Blocks.Sum(x => x.Text.Length) > 1000, "AUBI-plus multiline entity-encoded JobPosting description parses");

        check(IhkTrainingSource.ResolveLocation(Fixture("ihk-location-berlin").Body, "Berlin")?.Id == "id_ort=28286" && IhkTrainingSource.ResolveLocation(Fixture("ihk-location-munich").Body, "München")?.Id == "id_ort=27219", "IHK public place suggestions select exact Berlin/Munich rather than similar towns/counties");
        check(IhkTrainingSource.ResolveLocation(Fixture("ihk-location-empty").Body, "zzzznonexistent123456") is null && IhkTrainingSource.ResolveLocation(Fixture("ihk-location-berlin").Body, "Berlinchen")?.Id == "id_ort=28334", "IHK unresolved locations are not replaced by the first suggestion");
        f = Fixture("ihk-berlin-filtered");
        var ihk = IhkTrainingSource.Parse(f.Body, f.Url, q);
        check(ihk.Jobs.Count == 20 && ihk.Jobs.Any(j => j.Location.Contains("Berlin") && j.Title.Contains("Fachinformatiker")) && !ihk.Limited, "IHK actual profession/place/radius query parses advertised company and location");
        check(ihk.Next is not null && ihk.Next.Contains("id_geopost_eo=id_ort%3D28286") && ihk.Next.Contains("umkreis_eo=25") && ihk.Jobs.All(j => new Uri(j.Url).Host == "jobs.meine-ausbildung-in-deutschland.de" && long.TryParse(j.Id, out _)), "IHK pagination retains the resolved geographic identity and original posting IDs");
        f = Fixture("ihk-page2");
        var ihk2 = IhkTrainingSource.Parse(f.Body, f.Url, q);
        check(ihk2.Jobs.Count == 20 && !ihk2.Jobs.Any(j => ihk.Jobs.Any(first => first.Key == j.Key)), "IHK verified continuation advances twenty real results");
        f = Fixture("ihk-munich-filtered");
        check(IhkTrainingSource.Parse(f.Body, f.Url, new("Fachinformatiker", "München", 10, JobKind.Apprenticeship)).Jobs.Any(j => j.Location.Contains("München")), "IHK alternate normalized city lookup produces Munich vacancies");
        f = Fixture("ihk-berliner");
        var localIhk = IhkTrainingSource.Parse(f.Body, f.Url, new("Berliner", "Berlin", 25, JobKind.Apprenticeship));
        check(localIhk.Limited && localIhk.Jobs.Count > 0 && localIhk.Jobs.All(j => j.Location.Contains("Berlin")), "IHK geographic fallback is explicitly limited and locally filtered");
        f = Fixture("ihk-filtered");
        var localIhkMore = IhkTrainingSource.Parse(f.Body, f.Url, q);
        check(localIhkMore.Limited && localIhkMore.Next is not null && localIhkMore.Jobs.All(j => j.Location.Contains("Berlin")), "IHK local city filtering keeps available continuation even when this page has no city match");
        f = Fixture("ihk-empty");
        check(IhkTrainingSource.Parse(f.Body, f.Url).Jobs.Count == 0 && Format(() => IhkTrainingSource.Parse("<html><body>Unexpected layout</body></html>", f.Url)), "IHK documented zero result state is distinct from format failure");

        var codes = Fixture("radar-city").Body;
        var professions = Fixture("radar-jobs").Body;
        check(LehrstellenRadarSource.ResolvePostalCode(codes, "Berlin") == "10117" && LehrstellenRadarSource.ResolvePostalCode(codes, "Unknown place") is null, "Lehrstellenradar public place lookup preserves exact city and source postcode");
        check(LehrstellenRadarSource.ResolveProfessions(professions, "Tischler").SequenceEqual([13270]) && LehrstellenRadarSource.ResolveProfessions(professions, "Fachinformatiker").SequenceEqual([32191]) && LehrstellenRadarSource.ResolveProfessions(professions, "zzzznonexistent123456").Length == 0, "Lehrstellenradar uses actual profession IDs and preserves unknown keyword fallback");
        f = Fixture("radar-zip");
        var radar = LehrstellenRadarSource.Parse(f.Body, f.Url);
        check(radar.Jobs.Count == 15 && radar.Jobs.Any(j => j.Id == "91_2789" && j.Company == "Delta T Kälte-, Klima- & Lüftungstechnik GmbH" && j.Location == "10117 Berlin" && j.Start == "01.09.2027"), "Lehrstellenradar joins actual offer cards with their scoped employer details");
        var delta = radar.Jobs.First(j => j.Id == "91_2789");
        check(delta.Details is not null && delta.Details.Blocks.Sum(x => x.Text.Length) > 1600 && delta.Details.Contacts.Any(c => c.Kind == "phone" && c.Value == "+493099194820") && delta.Details.Contacts.Any(c => c.Kind == "email" && c.Value == "info@delta-t-berlin.de") && delta.Url == "https://www.hwk-berlin.de/91,0,jpguestdetail.html?id=2789", "Lehrstellenradar full description/contact and real chamber original are retained without extra detail requests");
        check(radar.Next is not null && radar.Next.Contains("page=2") && radar.Next.Contains("search-plz=10115") && radar.Next.Contains("search-ls=1"), "Lehrstellenradar hidden result form pagination preserves place and advertised type");
        f = Fixture("radar-page2");
        var radar2 = LehrstellenRadarSource.Parse(f.Body, f.Url);
        check(radar2.Jobs.Count == 30 && radar2.Inspected == 30 && radar2.Next is not null && radar2.Next.Contains("page=3"), "Lehrstellenradar actual continuation preserves its variable page size and next page");
        f = Fixture("radar-tischler");
        var tischler = LehrstellenRadarSource.Parse(f.Body, f.Url, new("Tischler", "Berlin", 25, JobKind.Apprenticeship));
        check(tischler.Jobs.Count == 2 && tischler.Jobs.All(j => j.Title == "Tischler (m/w/d)") && tischler.Jobs.Any(j => j.Location.Contains("Ahrensfelde")) && tischler.Next is null && !tischler.Limited, "Lehrstellenradar mapped profession search keeps actual neighbouring towns within the requested radius");
        f = Fixture("radar-internship");
        var internships = LehrstellenRadarSource.Parse(f.Body, f.Url, new("", "Berlin", 25, JobKind.Internship));
        check(internships.Jobs.Count == 15 && internships.Jobs.All(j => j.Kind == JobKind.Internship && j.Details is not null) && internships.Jobs.Any(j => new Uri(j.Url).AbsolutePath.Contains("jobboardofferdetail")), "Lehrstellenradar real internship icons and both observed chamber posting routes are supported");
        f = Fixture("radar-text-ignored");
        var localRadar = LehrstellenRadarSource.Parse(f.Body, f.Url, new("zzzznonexistent123456", "Berlin", 25, JobKind.Apprenticeship));
        check(localRadar.Jobs.Count == 0 && localRadar.Limited && localRadar.Next is not null, "Lehrstellenradar ignored free-text field never becomes unrelated matches or a false exhausted feed");
        f = Fixture("radar-empty");
        check(LehrstellenRadarSource.Parse(f.Body, f.Url).Jobs.Count == 0 && Format(() => LehrstellenRadarSource.Parse("<html><body>Unexpected layout</body></html>", f.Url)), "Lehrstellenradar genuine empty response differs from parser failure");
        check(Format(() => LehrstellenRadarSource.ResolvePostalCode("not JSON", "Berlin")) && Format(() => LehrstellenRadarSource.ResolveProfessions("not JSON", "Tischler")), "Lehrstellenradar malformed place/profession responses are explicit failures");
        using (var client = new SourceClient(new TrainingNoNetworkHandler(), TimeSpan.Zero))
        {
            var source = new LehrstellenRadarSource(client);
            bool Input(string city)
            {
                try { source.SearchAsync(new("Tischler", city, 25, JobKind.Apprenticeship), null, default).GetAwaiter().GetResult(); return false; }
                catch (SourceFailure ex) { return ex.Kind == FailureKind.Input; }
            }
            check(Input("") && Input("Berlin"), "Lehrstellenradar required postal input is explicit before any network request");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            bool cancellationWon = false;
            try { source.SearchAsync(new("Tischler", "Berlin"), null, cancelled.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { cancellationWon = true; }
            check(cancellationWon, "Lehrstellenradar cancelled search remains cancelled rather than showing a new input error");
        }
        check(LehrstellenRadarSource.SearchUrl(new("", "10117", 25, JobKind.Internship)).Contains("search-pr=1") && !LehrstellenRadarSource.SearchUrl(new("", "10117", 25, JobKind.Internship)).Contains("search-ls=1") && LehrstellenRadarSource.SearchUrl(new("", "Berlin", 50)).Contains("search-plz=Berlin"), "Lehrstellenradar validated postal URLs retain real type/radius while city-only external action opens the source form");

        f = Fixture("azubide-search");
        var azubiRaw = AzubiDeSource.Parse(f.Body, f.Url);
        var azubiBerlin = AzubiDeSource.Parse(f.Body, f.Url, q);
        check(azubiRaw.Jobs.Count == 20 && azubiRaw.Jobs.Any(j => j.Id == "teaser_job_offer_13306773" && j.Company == "HARIBO IT Dienstleistungs GmbH & Co. KG" && j.Location == "53501 Grafschaft (bei Bonn)"), "Azubi.de actual job IDs/company/location remain source values");
        check(azubiBerlin.Jobs.Count == 0 && azubiBerlin.Limited && azubiBerlin.Next is not null && azubiBerlin.Inspected == 20, "Azubi.de nationwide offers on a Berlin query never become false city matches or an exhausted feed");
        check(azubiRaw.Next is not null && azubiRaw.Next.Contains("page=2") && azubiRaw.Next.Contains("radius=25") && new Uri(azubiRaw.Next).Host == "www.azubi.de", "Azubi.de canonical SEO continuation retains its actual path and radius");
        f = Fixture("azubide-page2");
        var azubi2 = AzubiDeSource.Parse(f.Body, f.Url);
        check(azubi2.Jobs.Count == 20 && !azubi2.Jobs.Any(j => azubiRaw.Jobs.Any(first => first.Key == j.Key)), "Azubi.de verified second page advances actual offer IDs");
        f = Fixture("azubide-hamburg");
        var azubiHamburg = AzubiDeSource.Parse(f.Body, f.Url, new("Fachinformatiker", "Hamburg", 10, JobKind.Apprenticeship));
        check(azubiHamburg.Jobs.Count == 20 && azubiHamburg.Jobs.All(j => j.Kind == JobKind.Apprenticeship && j.Location.Contains("Hamburg")) && azubiHamburg.Jobs[0].Location == "22297 Hamburg" && azubiHamburg.Limited, "Azubi.de observed Hamburg results retain honest local city filtering and training category");
        f = Fixture("azubide-detail");
        check(DetailParser.Parse(f.Body, f.Url, azubiHamburg.Jobs[0]).Blocks.Sum(x => x.Text.Length) > 1000, "Azubi.de original posting full description parses through generic detail service");
        f = Fixture("azubide-empty");
        check(AzubiDeSource.Parse(f.Body, f.Url).Jobs.Count == 0 && Format(() => AzubiDeSource.Parse("<html><body>Unexpected layout</body></html>", f.Url)), "Azubi.de genuine empty response differs from parser failure");
    }
}

sealed class TrainingNoNetworkHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation) => throw new Exception("Unexpected network request");
}
