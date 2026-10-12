namespace JobFinder.Core;

public sealed record SourceDefinition(string Id, string Name, JobKind[] Kinds, Func<SearchQuery, string> SearchUrl,
    Func<SourceClient, IJobSource>? Factory = null, string[]? Hosts = null, string[]? LogoHosts = null,
    string CoverageRu = "", string CoverageDe = "", string ReasonRu = "Поиск доступен на сайте; автоматическая загрузка не подключена.",
    string ReasonDe = "Suche auf der Website; kein automatischer Feed angebunden.", int SpacingSeconds = 2, string[]? ReadOnlyPostPaths = null)
{
    public bool InFeed => Factory is not null;
    public bool Supports(JobKind kind) => kind == JobKind.All || Kinds.Contains(kind);
    public string Coverage(bool russian) => russian ? CoverageRu : CoverageDe;
    public string Reason(bool russian) => russian ? ReasonRu : ReasonDe;
}

public static class SourceCatalog
{
    private static readonly JobKind[] All = [JobKind.Job, JobKind.Internship, JobKind.Apprenticeship];
    private static readonly JobKind[] Work = [JobKind.Job];
    private static readonly JobKind[] Training = [JobKind.Apprenticeship];
    private static readonly JobKind[] Entry = [JobKind.Job, JobKind.Internship];
    public static readonly string[] LegacyFeedIds = ["arbeitnow", "praktikum", "azubiyo"];
    private static string E(string value) => Uri.EscapeDataString(value);
    private static string Word(SearchQuery q) => string.Join(" ", new[] { q.Keyword, q.Kind == JobKind.Internship ? "Praktikum" : q.Kind == JobKind.Apprenticeship ? "Ausbildung" : "" }.Where(x => x.Length > 0));
    private static SourceDefinition External(string id, string name, string home, JobKind[] kinds, string reasonRu = "Поиск доступен на сайте; автоматическая загрузка не подключена.", string reasonDe = "Suche auf der Website; kein automatischer Feed angebunden.")
        => new(id, name, kinds, _ => home, ReasonRu: reasonRu, ReasonDe: reasonDe);
    public static IReadOnlyList<SourceDefinition> Sources { get; } =
    [
        new("arbeitnow", "Arbeitnow", All, _ => "https://www.arbeitnow.com/", c => new ArbeitnowSource(c), ["www.arbeitnow.com"], ["www.arbeitnow.com"], "Город и профессия по тексту загруженных страниц; без географического радиуса.", "Ort und Beruf als Text auf geladenen Seiten; kein geografischer Umkreis."),
        new("praktikum", "Praktikum.info", Entry, q => $"https://www.praktikum.info/stellenangebote?text={E(q.Keyword)}&location={E(q.City)}&radius={q.Radius}", c => new PraktikumSource(c), ["www.praktikum.info"], ["uno-production.imgix.net"], "Запрос, город и радиус передаются сайту; тип позиции проверяется по объявлению.", "Suchtext, Ort und Umkreis werden übergeben; Art der Stelle wird geprüft."),
        new("azubiyo", "Azubiyo", Training, q => $"https://www.azubiyo.de/ausbildung/?subject={E(q.Keyword)}&location={E(q.City)}&radius={q.Radius}", c => new AzubiyoSource(c), ["www.azubiyo.de"], ["www.azubiyo.de", "d1e0s3eaddm99m.cloudfront.net"], "Поиск Ausbildung по профессии, городу и радиусу; даты начала из объявления.", "Ausbildung nach Beruf, Ort und Umkreis; Beginn laut Anzeige."),
        new("stepstone", "StepStone", All, q => $"https://www.stepstone.de/jobs/{E(Word(q).Length > 0 ? Word(q) : "arbeit")}{(q.City.Length > 0 ? "/in-" + E(q.City) : "")}?radius={q.Radius}"),
        new("indeed", "Indeed", All, q => $"https://de.indeed.com/jobs?q={E(Word(q))}&l={E(q.City)}&radius={q.Radius}"),
        new("ba", "BA Jobsuche", All, BaJobsSource.SearchUrl, c => new BaJobsSource(c), ["www.arbeitsagentur.de"], ["rest.arbeitsagentur.de"], "Публичная выдача BA: профессия, город/индекс, радиус и тип объявления. Без стороннего API.", "Öffentliche BA-Suche: Beruf, Ort/PLZ, Umkreis und Angebotsart. Ohne fremde API."),
        new("ausbildung", "Ausbildung.de", Training, AusbildungSource.SearchUrl, c => new AusbildungSource(c), ["www.ausbildung.de"], ["www.ausbildung.de"], "Ausbildung и Duales Studium; поиск по профессии, городу и радиусу.", "Ausbildung und duales Studium; Suche nach Beruf, Ort und Umkreis."),
        new("aubiplus", "AUBI-plus", Training, AubiPlusSource.SearchUrl, c => new AubiPlusSource(c), ["www.aubi-plus.de"], ["media.aubi-plus.com"], "Поиск Ausbildungsplätze по профессии и местоположению; учитываются основные найденные объявления.", "Ausbildungsplätze nach Beruf und Standort; primäre Treffer werden berücksichtigt."),
        External("ausbildungsstellen", "Ausbildungsstellen.de", "https://www.ausbildungsstellen.de/", Training, "Сайт ограничил автоматический доступ при проверке. Поиск доступен в браузере.", "Automatischer Zugriff bei der Prüfung eingeschränkt. Suche im Browser."),
        new("ihk", "Meine Ausbildung (IHK)", Training, IhkTrainingSource.SearchUrl, c => new IhkTrainingSource(c), ["ihk-azubi-stellenmarkt.indexinternet.de"], [], "Ausbildung; город и радиус после разрешения места. Неизвестный город проверяется по тексту, без проверки километров. Полное описание — на оригинале.", "Ausbildung; Ort und Umkreis nach Standortauflösung. Unbekannte Orte als Text ohne Kilometerprüfung. Volltext auf der Originalseite.", ReadOnlyPostPaths: ["/ajax.php"]),
        new("handwerk", "Lehrstellenradar", [JobKind.Apprenticeship, JobKind.Internship], LehrstellenRadarSource.SearchUrl, c => new LehrstellenRadarSource(c), ["www.lehrstellen-radar.de"], [], "Ремесленные профессии: Ausbildung и практика. Нужен пятизначный почтовый индекс; профессия по загруженным объявлениям, радиус и тип передаются источнику. Описание включено в карточку.", "Handwerksberufe: Ausbildung und Praktikum. Fünfstellige PLZ erforderlich; Beruf auf geladenen Anzeigen, Umkreis und Art an die Quelle übergeben. Beschreibung in der Karte."),
        new("meinpraktikum", "MeinPraktikum.de", Entry, MeinPraktikumSource.SearchUrl, c => new MeinPraktikumSource(c), ["www.meinpraktikum.de", "rest.meinpraktikum.de"], ["cdn.tp.production.talentplatforms.net"], "Практика и работа для начинающих; запрос, город/индекс и радиус проверяются по выдаче.", "Praktika und Einstieg; Suchtext, Ort/PLZ und Umkreis werden geprüft.", ReadOnlyPostPaths: ["/api/v1/search/read/jobposting/search"]),
        new("absolventa", "Absolventa.de", Entry, AbsolventaSource.SearchUrl, c => new AbsolventaSource(c), ["www.absolventa.de"], ["uno-production.imgix.net"], "Практика, Werkstudent и начало карьеры; возможны общегерманские предложения в выдаче по городу.", "Praktikum, Werkstudent und Einstieg; bundesweite Angebote können in der Ortssuche erscheinen."),
        new("jobmensa", "Jobmensa.de", All, JobmensaSource.SearchUrl, c => new JobmensaSource(c), ["www.jobmensa.de"], ["www.jobmensa.de"], "Студенческая работа и практика; город/индекс и радиус используются при разрешённом местоположении.", "Studentenjobs und Praktika; Ort/PLZ und Umkreis bei aufgelöstem Standort."),
        new("unicum", "UNICUM Karrierezentrum", All, UnicumSource.SearchUrl, c => new UnicumSource(c), ["karriere.unicum.de"], ["karriere.unicum.de"], "Работа, практика и обучение; запросы к источнику не чаще одного раза в 10 секунд.", "Jobs, Praktika und Ausbildung; Quellenabfragen höchstens alle 10 Sekunden.", SpacingSeconds: 10),
        External("praktikumsstellen", "Praktikumsstellen.de", "https://www.praktikumsstellen.de/", [JobKind.Internship], "Сайт ограничил автоматический доступ при проверке. Поиск доступен в браузере.", "Automatischer Zugriff bei der Prüfung eingeschränkt. Suche im Browser."),
        new("workwise", "Workwise", Work, WorkwiseSource.SearchUrl, c => new WorkwiseSource(c), ["www.workwise.io"], ["img.workwise.io"], "Работа по городским страницам; профессия проверяется по загруженным объявлениям. Без проверки радиуса.", "Jobs auf Stadtseiten; Beruf wird in geladenen Anzeigen geprüft. Kein Umkreisfilter."),
        External("meinestadt", "meinestadt.de", "https://jobs.meinestadt.de/", All, "Сайт ограничил автоматический доступ при проверке. Поиск доступен в браузере.", "Automatischer Zugriff bei der Prüfung eingeschränkt. Suche im Browser."),
        new("interamt", "INTERAMT", All, InteramtSource.SearchUrl, c => new InteramtSource(c), ["interamt.de", "www.interamt.de"], [], "Государственная служба; полнотекстовый поиск в объявлениях, город/индекс и радиус. Слова могут совпадать в описании, а не в названии должности.", "Öffentlicher Dienst; Volltextsuche in Anzeigen, Ort/PLZ und Umkreis. Suchwörter können im Beschreibungstext statt im Stellentitel stehen.", ReadOnlyPostPaths: ["/koop/app/stellensuche"]),
        new("bund", "bund.de RSS", All, _ => "https://www.service.bund.de/", c => new BundRssSource(c), ["www.service.bund.de"], [], "Последние 500 объявлений официального RSS. Профессия и город по тексту; без географического радиуса и полного каталога.", "Letzte 500 Anzeigen aus dem offiziellen RSS. Beruf und Ort als Text; kein geografischer Umkreis oder vollständiger Katalog.", SpacingSeconds: 30),
        new("jobware", "Jobware", All, JobwareSource.SearchUrl, c => new JobwareSource(c), ["www.jobware.de"], ["www.jobware.de"], "Работа, практика и Ausbildung; поисковые поля и следующие страницы проверены на публичной выдаче.", "Jobs, Praktika und Ausbildung; Suchfelder und Folgeseiten in öffentlicher Suche geprüft."),
        new("stellenanzeigen", "stellenanzeigen.de", All, StellenanzeigenSource.SearchUrl, c => new StellenanzeigenSource(c), ["www.stellenanzeigen.de"], ["www.stellenanzeigen.de"], "Город и радиус из публичного справочника сайта. Индекс отдельно не поддерживается; проверяется доступный блок выдачи.", "Ort und Umkreis aus dem öffentlichen Standortverzeichnis. Keine reine PLZ-Suche; verfügbarer Ergebnisblock wird geprüft."),
        new("stellenwerk", "stellenwerk", Entry, StellenwerkSource.SearchUrl, c => new StellenwerkSource(c), ["www.stellenwerk.de"], ["www.stellenwerk.de", "stellenwerk-production-assets.s3.eu-central-1.amazonaws.com"], "Студенческие и стартовые позиции в университетских регионах. Профессия по загруженным страницам; без километрового радиуса.", "Studenten- und Einstiegsjobs in Hochschulregionen. Beruf auf geladenen Seiten; kein Kilometer-Umkreis."),
        new("berufsstart", "Berufsstart", Entry, BerufsstartSource.SearchUrl, c => new BerufsstartSource(c), ["www.berufsstart.de"], ["www.berufsstart.de"], "Практика и первые рабочие позиции из публичной страницы. Город и профессия по тексту; без проверки радиуса, охват ограничен.", "Praktika und Einstieg aus öffentlicher Seite. Ort und Beruf als Text; ohne Umkreis, begrenzte Abdeckung."),
        new("azubide", "Azubi.de", Training, AzubiDeSource.SearchUrl, c => new AzubiDeSource(c), ["www.azubi.de"], ["uno-production.imgix.net"], "Ausbildung; город дополнительно проверяется в загруженных объявлениях. Радиус источника независимо не подтверждён.", "Ausbildung; Ort zusätzlich in geladenen Anzeigen geprüft. Umkreis der Quelle nicht unabhängig bestätigt."),
        External("ausbildungsmarkt", "Ausbildungsmarkt.de", "https://www.ausbildungsmarkt.de/", Training, "Автоматический поиск ограничен правилами сайта. Откройте поиск в браузере.", "Automatische Suche durch Website-Regeln eingeschränkt. Suche im Browser öffnen."),
        new("berlin", "Karriereportal Berlin", All, BerlinCareerSource.SearchUrl, c => new BerlinCareerSource(c), ["www.karriereportal-stellen.berlin.de", "karriereportal-stellen.berlin.de"], [], "Публичная страница вакансий Land Berlin. Региональный источник; совпадения по загруженной странице, без проверки радиуса.", "Öffentliche Stellen des Landes Berlin. Regionale Quelle; Treffer auf der geladenen Seite, kein Umkreisfilter."),
        External("getinit", "get in IT", "https://www.get-in-it.de/", Work),
        new("linkedin", "LinkedIn Jobs", Entry, q => $"https://www.linkedin.com/jobs/search/?keywords={E(Word(q))}&location={E(q.City.Length > 0 ? q.City + ", Germany" : "Germany")}"),
        External("xing", "XING Jobs", "https://www.xing.com/jobs", Work),
        External("jobvector", "jobvector · IT & Technik", "https://www.jobvector.de/", Work),
        External("studentjob", "StudentJob", "https://www.studentjob.de/", Entry),
        External("heyjobs", "HeyJobs", "https://www.heyjobs.co/de-de/", Work),
    ];
    public static SourceDefinition? Find(string id) => Sources.FirstOrDefault(x => x.Id == id);
    public static string Portal(string id, SearchQuery query) => Find(id)?.SearchUrl(query.Clean()) ?? throw new ArgumentException("Unknown portal");
    public static IReadOnlyList<IJobSource> Create(SourceClient client) => Sources.Where(x => x.InFeed).Select(x => x.Factory!(client)).ToList();
    public static List<string> FeedIds() => Sources.Where(x => x.InFeed).Select(x => x.Id).ToList();
    public static bool IsSourceUrl(string url) => Links.IsSafe(url) && new Uri(url).IsDefaultPort && Sources.Any(s => s.InFeed && (s.Hosts ?? []).Contains(new Uri(url).Host, StringComparer.OrdinalIgnoreCase));
    public static bool SameSource(string original, string target) => IsSourceUrl(original) && IsSourceUrl(target) && Sources.Any(s => s.InFeed && (s.Hosts ?? []).Contains(new Uri(original).Host, StringComparer.OrdinalIgnoreCase) && (s.Hosts ?? []).Contains(new Uri(target).Host, StringComparer.OrdinalIgnoreCase));
    public static bool IsLogoUrl(string? url) => Links.IsSafe(url) && new Uri(url!).IsDefaultPort && Sources.Any(s => s.InFeed && (s.LogoHosts ?? []).Contains(new Uri(url!).Host, StringComparer.OrdinalIgnoreCase));
    public static TimeSpan Spacing(string host) => TimeSpan.FromSeconds(Sources.Where(s => s.InFeed && (s.Hosts ?? []).Contains(host, StringComparer.OrdinalIgnoreCase)).Select(s => s.SpacingSeconds).DefaultIfEmpty(2).Max());
    public static string ProviderKey(string host) => Sources.FirstOrDefault(s => s.InFeed && (s.Hosts ?? []).Contains(host, StringComparer.OrdinalIgnoreCase))?.Id ?? host;
    public static bool IsReadOnlyPostUrl(string url) => IsSourceUrl(url) && Sources.Any(s => s.InFeed && (s.Hosts ?? []).Contains(new Uri(url).Host, StringComparer.OrdinalIgnoreCase) && (s.ReadOnlyPostPaths ?? []).Any(path => new Uri(url).AbsolutePath.TrimEnd('/') == path.TrimEnd('/')));
    public static void UpdateProfileSources(UserProfile profile)
    {
        var known = profile.KnownSourceIds.Count > 0 ? profile.KnownSourceIds : LegacyFeedIds.ToList();
        foreach (var id in FeedIds().Except(known)) if (!profile.EnabledSources.Contains(id)) profile.EnabledSources.Add(id);
        profile.KnownSourceIds = known.Concat(FeedIds()).Distinct().ToList();
    }
}
