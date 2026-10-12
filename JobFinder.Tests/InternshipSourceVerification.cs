using JobFinder.Core;

public static class InternshipSourceVerification
{
    public static void Run(string fixtures, Action<bool, string> check)
    {
        string Read(string name) => File.ReadAllText(Path.Combine(fixtures, name));
        var query = new SearchQuery("Marketing", "Berlin", 25, JobKind.Internship);
        var mp = MeinPraktikumSource.Parse(Read("meinpraktikum-post-page1.json"), new("https://rest.meinpraktikum.de/api/v1/search/read/jobposting/search?page=1"), query);
        check(mp.Jobs.Count > 5 && mp.Jobs.Any(j => j.Company == "Workwise GmbH" && j.Location == "Berlin" && j.Title.Contains("Online-Marketing")), "MeinPraktikum actual title, company, city from public search");
        check(mp.Next?.EndsWith("?page=2") == true && !mp.Limited, "MeinPraktikum verified POST pagination");
        check(mp.Jobs.All(j => !j.Title.StartsWith("Werkstudent") && j.Kind == JobKind.Internship), "MeinPraktikum mislabeled Werkstudent is excluded from internships");
        check(mp.Jobs.Any(j => j.Details?.HasDescription == true && j.Details.Blocks.Any(b => b.Kind == "bullet")), "MeinPraktikum posting Markdown retains structured full description");
        var mp2 = MeinPraktikumSource.Parse(Read("meinpraktikum-post-page2.json"), new("https://rest.meinpraktikum.de/api/v1/search/read/jobposting/search?page=2"), query);
        check(mp2.Jobs.Count > 0 && mp2.Jobs.All(j => mp.Jobs.All(old => old.Key != j.Key)), "MeinPraktikum second real page adds new original postings");
        check(MeinPraktikumSource.Parse(Read("meinpraktikum-post-zero.json"), new("https://rest.meinpraktikum.de/api/v1/search/read/jobposting/search?page=1"), query with { Keyword = "zzzznonexistent123456" }).Jobs.Count == 0, "MeinPraktikum public POST actually applies impossible keyword");
        var jm = JobmensaSource.Parse(Read("jobmensa-geographic.html"), new("https://www.jobmensa.de/jobs-suchen?categories=internship&job-title=Marketing&location=Berlin&latitude=52.5069348&longitude=13.39748268150147&radius=25"), query);
        check(jm.Jobs.Count == 2 && jm.Jobs.All(j => j.Location == "Berlin"), "Jobmensa actual geographic internship results");
        check(jm.Jobs.Any(j => j.Company == "Com2uS Europe GmbH"), "Jobmensa streamed employer fields are read without executing scripts");
        check(JobmensaSource.Parse(Read("jobmensa-empty.html"), new("https://www.jobmensa.de/jobs-suchen?job-title=zzzznonexistent123456&categories=internship")).Jobs.Count == 0, "Jobmensa explicit empty remains distinct from format failure");
        var av = AbsolventaSource.Parse(Read("absolventa-home.html"), new("https://www.absolventa.de/jobs"));
        check(av.Jobs.Count == 20 && av.Jobs.Any(j => j.Company.Length > 0 && j.Location.Length > 0), "Absolventa actual SSR cards and metadata");
        check(av.Next == "https://www.absolventa.de/jobs?page=2", "Absolventa observed first next-page URL");
        check(av.Jobs.Any(j => j.Salary.Contains('€')) && av.Jobs.All(j => j.Start.Length == 0 && j.FeedDate is null), "Absolventa advertised salary only; unknown start and date preserved");
        check(AbsolventaSource.Parse(Read("absolventa-empty.html"), new("https://www.absolventa.de/jobs?text=zzzznonexistent123456&location=Berlin&radius=25")).Jobs.Count == 0, "Absolventa actual explicit empty");
        var uc = UnicumSource.Parse(Read("unicum-filtered.html"), new("https://karriere.unicum.de/praktikum?search=Marketing&job_geo_location=Berlin&radius=25"), query);
        check(uc.Jobs.Count == 2 && uc.Jobs.All(j => j.Location.Contains("Berlin") && j.Kind == JobKind.Internship), "UNICUM actual category, city and company cards");
        var selected = uc.Jobs.Single(j => j.Url.EndsWith("/job/online-marketing-praktikum-m-w"));
        var detail = UnicumSource.ParseDetails(Read("unicum-detail.html"), new(selected.Url), selected);
        check(detail.HasDescription && detail.Blocks.Sum(b => b.Text.Length) > 1500 && detail.Contacts.Any(c => c.Value == "ch@cremeguides.com"), "UNICUM scoped description and posting contact");
        check(UnicumSource.Parse(Read("unicum-empty.html"), new("https://karriere.unicum.de/jobs?search=zzzznonexistent123456&job_geo_location=Berlin&radius=25")).Jobs.Count == 0, "UNICUM actual explicit empty");
        var sw = StellenwerkSource.Parse(Read("stellenwerk-home.html"), new("https://www.stellenwerk.de/berlin"), new("", "Berlin"));
        check(sw.Jobs.Count == 10 && sw.Jobs.Any(j => j.Title.StartsWith("Studentische Hilfskraft") && j.Company == "Olin gemeinnützige GmbH" && j.Salary.Contains("16")), "Stellenwerk real regional cards, company and advertised salary");
        check(sw.Next == "https://www.stellenwerk.de/berlin?pagination%5Bstart%5D=10" && sw.Limited, "Stellenwerk actual start pagination with disclosed local keyword and region limits");
        var sw2 = StellenwerkSource.Parse(Read("stellenwerk-page2.html"), new(sw.Next!));
        check(sw2.Jobs.Count == 10 && sw2.Jobs.All(j => sw.Jobs.All(old => old.Key != j.Key)) && sw2.Jobs.Any(j => j.Kind == JobKind.Internship), "Stellenwerk real second page, including Jahrespraktikantin");
        var swNoMatch = StellenwerkSource.Parse(Read("stellenwerk-home.html"), new("https://www.stellenwerk.de/berlin"), new("zzzznonexistent123456", "Berlin"));
        check(swNoMatch.Jobs.Count == 0 && swNoMatch.Next is not null, "Stellenwerk no local match retains later pages");
        check(StellenwerkSource.Parse(Read("stellenwerk-empty-page.html"), new("https://www.stellenwerk.de/berlin?pagination%5Bstart%5D=99999")).Jobs.Count == 0, "Stellenwerk actual exhausted page is empty");
        var bs = BerufsstartSource.Parse(Read("berufsstart-internships.html"), new("https://www.berufsstart.de/praktikum/"));
        check(bs.Jobs.Count >= 20 && bs.Jobs.All(j => j.Company.Length > 0 && j.Title.Length > 0) && bs.Limited, "Berufsstart real bounded internship and entry listings");
        check(bs.Jobs.Any(j => j.Title.StartsWith("Working Student") && j.Kind == JobKind.Job), "Berufsstart Working Student isn't reclassified by portal Praktikum label");
        var bsBerlin = BerufsstartSource.Parse(Read("berufsstart-berlin.html"), new("https://www.berufsstart.de/praktikum/berlin/"), new("Marketing", "Berlin", 25, JobKind.Internship));
        check(bsBerlin.Jobs.Count == 1 && bsBerlin.Jobs.All(j => j.Location == "Berlin") && bsBerlin.Jobs[0].Company == "Planet2050", "Berufsstart recommendations excluded and local conditions applied");
        check(BerufsstartSource.Parse(Read("berufsstart-internships.html"), new("https://www.berufsstart.de/praktikum/"), new("zzzznonexistent123456")).Jobs.Count == 0, "Berufsstart explicit bounded local zero");
        foreach (var parse in new Func<JobPage>[] {
            () => MeinPraktikumSource.Parse("{}", new("https://rest.meinpraktikum.de/api/v1/search/read/jobposting/search")),
            () => JobmensaSource.Parse("<html><body>Unexpected layout</body></html>", new("https://www.jobmensa.de/jobs-suchen")),
            () => AbsolventaSource.Parse("<html><body>Unexpected layout</body></html>", new("https://www.absolventa.de/jobs")),
            () => UnicumSource.Parse("<html><body>Unexpected layout</body></html>", new("https://karriere.unicum.de/jobs")) })
        {
            try { parse(); check(false, "Changed source layout must fail explicitly"); }
            catch (SourceFailure failure) { check(failure.Kind == FailureKind.Format, "Changed internship layout classified as format failure"); }
        }
    }
}
