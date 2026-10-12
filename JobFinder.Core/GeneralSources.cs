using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace JobFinder.Core;

// These adapters read the public pages used by each portal. Embedded SSR data is
// page content, not a claim that a private or community API is supported.
internal static class GeneralMarkup
{
    public static string S(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? Text.Clean(v.GetString()) : "";
    public static JsonElement P(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var v) ? v : default;
    public static int N(JsonElement item, string key) => P(item, key).TryNumber(out var n) ? n : 0;
    private static bool TryNumber(this JsonElement item, out int n) { n = 0; return item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out n); }
    public static bool B(JsonElement item, string key) => P(item, key).ValueKind == JsonValueKind.True;
    public static IEnumerable<JsonElement> A(JsonElement item) => item.ValueKind == JsonValueKind.Array ? item.EnumerateArray() : [];
    public static DateTimeOffset? Date(string value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;
    public static DateTimeOffset? GermanDate(string value) => DateTimeOffset.TryParseExact(Text.Clean(value), ["dd.MM.yyyy", "dd.MM.yy"], CultureInfo.GetCultureInfo("de-DE"), DateTimeStyles.AssumeUniversal, out var date) ? date : null;
    public static string Field(IElement? element) => Html.TextOf(element).Replace("\u00ad", "");
    public static string WithoutLabels(IElement? element)
    {
        if (element is null) return "";
        var copy = (IElement)element.Clone(true);
        foreach (var node in copy.QuerySelectorAll("em,.sr-only,.ia-sr-only,.nrmdy-visually-hidden")) node.Remove();
        return Field(copy);
    }
    public static Uri Require(string value, params string[] hosts)
    {
        if (!Links.IsSafe(value)) throw new SourceFailure(FailureKind.Format, "Unexpected source URL");
        var uri = new Uri(value);
        if (!hosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)) throw new SourceFailure(FailureKind.Format, "Unexpected source host");
        return uri;
    }
    public static string? Href(IElement? a, Uri root, string prefix)
    {
        if (a is null) return null;
        var href = Html.SafeHref(a, root);
        return href is not null && new Uri(href).AbsolutePath.StartsWith(prefix, StringComparison.Ordinal) ? href : null;
    }
    public static string QueryValue(Uri url, string key)
    {
        foreach (var part in url.Query.TrimStart('?').Split('&'))
        {
            var pair = part.Split('=', 2);
            if (Uri.UnescapeDataString(pair[0]) == key) return pair.Length == 2 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : "";
        }
        return "";
    }
    public static string SetQuery(Uri url, string key, string value)
    {
        var b = new UriBuilder(url) { Fragment = "" };
        var other = b.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Where(p => Uri.UnescapeDataString(p.Split('=', 2)[0]) != key);
        b.Query = string.Join("&", other.Append(Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(value)));
        return b.Uri.AbsoluteUri;
    }
    public static string Slug(string value) => Regex.Replace(Text.Normalize(value.ToLowerInvariant().Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue")), @"[^\p{L}\p{N}]+", "-").Trim('-');
    public static bool MatchesLocal(JobListing job, SearchQuery query)
    {
        if (query.Kind != JobKind.All && query.Kind != job.Kind) return false;
        string text = Text.Normalize(job.Title + " " + job.Company + " " + job.Summary);
        // Acronyms such as IT and HR must not match unrelated fragments inside
        // German words (Mitarbeiter, arbeiten, ...).
        foreach (var term in Text.Normalize(query.Keyword).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (term.Length <= 2 ? !Regex.IsMatch(text, @"(?<![\p{L}\p{N}])" + Regex.Escape(term) + @"(?![\p{L}\p{N}])") : !text.Contains(term, StringComparison.Ordinal)) return false;
        return query.City.Length == 0 || Text.Normalize(job.Location).Contains(Text.Normalize(query.City), StringComparison.Ordinal);
    }
    public static void EnsureCards(int cards, int jobs, string visibleText, string source)
    {
        if (cards > 0 && jobs == 0) throw new SourceFailure(FailureKind.Format, source + " cards could not be read");
        if (cards == 0 && !Regex.IsMatch(Text.Normalize(visibleText), @"(keine (passenden )?(jobs|stellen|angebote|treffer|ergebnisse)|0 (stellen|angebote|jobs|treffer)|keine jobs)", RegexOptions.IgnoreCase))
            throw new SourceFailure(FailureKind.Format, source + " response layout changed");
    }
}

public sealed class BaJobsSource(SourceClient client) : IJobSource
{
    private const string CursorPrefix = "ba-pages-v1:";
    public string Id => "ba";
    public string Name => "BA Jobsuche";
    public bool Supports(JobKind kind) => true;
    public static string SearchUrl(SearchQuery query)
    {
        var q = query.Clean();
        string E(string s) => Uri.EscapeDataString(s);
        var area = q.Kind == JobKind.Apprenticeship ? "suchbereich=ausbildung" : "suchbereich=jobs&angebotsart=" + (q.Kind == JobKind.Internship ? "34" : "1");
        return $"https://www.arbeitsagentur.de/jobsuche/suche?{area}&was={E(q.Keyword)}&wo={E(q.City)}&umkreis={q.Radius}";
    }
    public async Task<JobPage> SearchAsync(SearchQuery query, string? next, CancellationToken cancellation)
    {
        var clean = query.Clean();
        if (clean.Kind == JobKind.All)
        {
            var requests = next is null
                ? new[] { SearchUrl(clean with { Kind = JobKind.Job }), SearchUrl(clean with { Kind = JobKind.Apprenticeship }), SearchUrl(clean with { Kind = JobKind.Internship }) }
                : ReadCursor(next, clean);
            var pages = await Task.WhenAll(requests.Select(async branchUrl =>
            {
                var download = await client.GetAsync(branchUrl, cancellation);
                cancellation.ThrowIfCancellationRequested();
                return await Task.Run(() => Parse(download.Body, download.Url, clean), cancellation);
            }));
            var remaining = pages.Select(p => p.Next).Where(s => s is not null).Cast<string>().ToArray();
            string? cursor = remaining.Length == 0 ? null : CursorPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { v = 1, urls = remaining })));
            return new(pages.SelectMany(p => p.Jobs).DistinctBy(j => j.Key).ToList(), cursor, pages.Sum(p => p.Inspected));
        }
        var request = GeneralMarkup.Require(next ?? SearchUrl(query), "www.arbeitsagentur.de");
        if (request.AbsolutePath != "/jobsuche/suche") throw new SourceFailure(FailureKind.Format, "Unexpected BA search URL");
        var response = await client.GetAsync(request.AbsoluteUri, cancellation);
        cancellation.ThrowIfCancellationRequested();
        return await Task.Run(() => Parse(response.Body, response.Url, query), cancellation);
    }
    private static string[] ReadCursor(string value, SearchQuery query)
    {
        try
        {
            if (!value.StartsWith(CursorPrefix, StringComparison.Ordinal) || value.Length > 12000) throw new FormatException();
            using var json = JsonDocument.Parse(Convert.FromBase64String(value[CursorPrefix.Length..]));
            if (json.RootElement.ValueKind != JsonValueKind.Object || json.RootElement.EnumerateObject().Count() != 2 || GeneralMarkup.N(json.RootElement, "v") != 1) throw new FormatException();
            var items = GeneralMarkup.P(json.RootElement, "urls");
            if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() is < 1 or > 3) throw new FormatException();
            var urls = new List<string>(); var branches = new HashSet<JobKind>();
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) throw new FormatException();
                string url = item.GetString()!;
                var uri = GeneralMarkup.Require(url, "www.arbeitsagentur.de");
                if (!uri.IsDefaultPort || uri.AbsolutePath != "/jobsuche/suche" || uri.Fragment.Length > 0) throw new FormatException();
                var keys = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => Uri.UnescapeDataString(p.Split('=', 2)[0])).ToArray();
                string[] allowed = ["suchbereich", "angebotsart", "was", "wo", "umkreis", "page"];
                if (keys.Distinct().Count() != keys.Length || keys.Any(k => !allowed.Contains(k))) throw new FormatException();
                var area = GeneralMarkup.QueryValue(uri, "suchbereich"); var offer = GeneralMarkup.QueryValue(uri, "angebotsart");
                JobKind kind = area == "ausbildung" && offer is "" or "4" ? JobKind.Apprenticeship : area == "jobs" && offer == "1" ? JobKind.Job : area == "jobs" && offer == "34" ? JobKind.Internship : throw new FormatException();
                if (!branches.Add(kind) || GeneralMarkup.QueryValue(uri, "was") != query.Keyword || GeneralMarkup.QueryValue(uri, "wo") != query.City || GeneralMarkup.QueryValue(uri, "umkreis") != query.Radius.ToString(CultureInfo.InvariantCulture) || !int.TryParse(GeneralMarkup.QueryValue(uri, "page"), out var page) || page < 2) throw new FormatException();
                urls.Add(url);
            }
            return urls.ToArray();
        }
        catch (Exception e) when (e is FormatException or JsonException or InvalidOperationException or ArgumentException) { throw new SourceFailure(FailureKind.Format, "Invalid BA combined-search continuation"); }
    }
    public static JobPage Parse(string body, Uri url, SearchQuery? query = null)
    {
        GeneralMarkup.Require(url.AbsoluteUri, "www.arbeitsagentur.de");
        var doc = new HtmlParser().ParseDocument(body);
        var state = doc.QuerySelector("script#ng-state[type='application/json']")?.TextContent;
        try
        {
            using var json = JsonDocument.Parse(state ?? "");
            var result = GeneralMarkup.P(json.RootElement, "suchergebnis");
            var entries = GeneralMarkup.P(result, "ergebnisliste");
            var totalField = GeneralMarkup.P(result, "maxErgebnisse");
            if (totalField.ValueKind != JsonValueKind.Number || !totalField.TryGetInt32(out var total) || total < 0) throw new JsonException();
            int expectedPage = int.TryParse(GeneralMarkup.QueryValue(url, "page"), out var requestedPage) ? requestedPage : 1;
            if (expectedPage < 1 || GeneralMarkup.N(result, "page") != expectedPage) throw new SourceFailure(FailureKind.Format, "BA did not advance to the requested page");
            if (query is not null && query.City.Length > 0 && GeneralMarkup.S(GeneralMarkup.P(result, "woOutput"), "suchmodus") != "UMKREISSUCHE") throw new SourceFailure(FailureKind.Format, "BA could not resolve the requested geographic search");
            // The official zero-result SSR omits ergebnisliste entirely.
            if (total == 0 && GeneralMarkup.N(result, "page") > 0 && GeneralMarkup.N(result, "size") > 0) return new([], null, 0, query?.Kind == JobKind.All);
            if (entries.ValueKind != JsonValueKind.Array) throw new JsonException();
            var jobs = new List<JobListing>();
            foreach (var item in entries.EnumerateArray())
            {
                string id = GeneralMarkup.S(item, "referenznummer"), title = GeneralMarkup.S(item, "stellenangebotsTitel");
                if (!Regex.IsMatch(id, @"^[\w-]{4,100}$") || title.Length == 0) continue;
                string original = "https://www.arbeitsagentur.de/jobsuche/jobdetail/" + Uri.EscapeDataString(id);
                var kind = GeneralMarkup.S(item, "stellenangebotsart") == "AUSBILDUNG" ? JobKind.Apprenticeship : Text.Classify(title);
                // BA's combined Praktikum/Trainee category also contains Werkstudent jobs.
                if (query is not null && query.Kind != JobKind.All && query.Kind != kind) continue;
                string salary = "";
                var card = doc.QuerySelector($"article:has(a[href$='/{id}'])");
                var salaryElement = card?.QuerySelector("[id$='-gehaltsspanne'],[id$='-festgehalt']");
                if (salaryElement is not null) salary = GeneralMarkup.WithoutLabels(salaryElement);
                var locations = GeneralMarkup.A(GeneralMarkup.P(item, "stellenlokationen")).Select(l => GeneralMarkup.P(l, "adresse"));
                string location = string.Join(", ", locations.Select(a => string.Join(" ", new[] { GeneralMarkup.S(a, "plz"), GeneralMarkup.S(a, "ort") }.Where(x => x.Length > 0))).Where(x => x.Length > 0).Distinct());
                jobs.Add(new() { Id = id, Source = "BA Jobsuche", Title = title, Company = GeneralMarkup.S(item, "firma"), Location = location, Kind = kind, Url = original, Salary = salary, Start = GeneralMarkup.S(GeneralMarkup.P(item, "eintrittszeitraum"), "von"), FeedDate = GeneralMarkup.Date(GeneralMarkup.S(item, "datumErsteVeroeffentlichung")), Remote = GeneralMarkup.B(item, "homeofficemoeglich"), LogoUrl = DetailParser.Logo(card?.QuerySelector("img"), url) });
            }
            var page = GeneralMarkup.N(result, "page"); var size = GeneralMarkup.N(result, "size");
            if (page <= 0 || size <= 0 || total < 0) throw new JsonException();
            if (entries.GetArrayLength() > 0 && jobs.Count == 0 && query is null) throw new JsonException();
            string? next = entries.GetArrayLength() > 0 && (long)page * size < total ? GeneralMarkup.SetQuery(url, "page", (page + 1).ToString(CultureInfo.InvariantCulture)) : null;
            return new(jobs.DistinctBy(x => x.Key).ToList(), next, entries.GetArrayLength(), query?.Kind == JobKind.All);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException) { throw new SourceFailure(FailureKind.Format, "BA public search page format changed"); }
    }
}

public sealed class WorkwiseSource(SourceClient client) : IJobSource
{
    public string Id => "workwise";
    public string Name => "Workwise";
    public bool Supports(JobKind kind) => kind is JobKind.All or JobKind.Job;
    public static string SearchUrl(SearchQuery query)
    {
        var city = query.Clean().City;
        // The public static job lists expose city pages, not an arbitrary keyword/radius API.
        return "https://www.workwise.io/jobs" + (city.Length > 0 && !Regex.IsMatch(city, @"^\d{5}$") ? "/" + Uri.EscapeDataString(GeneralMarkup.Slug(city)) : "");
    }
    public async Task<JobPage> SearchAsync(SearchQuery query, string? next, CancellationToken cancellation)
    {
        var uri = GeneralMarkup.Require(next ?? SearchUrl(query), "www.workwise.io");
        if (!uri.AbsolutePath.StartsWith("/jobs", StringComparison.Ordinal)) throw new SourceFailure(FailureKind.Format, "Unexpected Workwise search URL");
        var response = await client.GetAsync(uri.AbsoluteUri, cancellation);
        cancellation.ThrowIfCancellationRequested();
        return await Task.Run(() => Parse(response.Body, response.Url, query), cancellation);
    }
    public static JobPage Parse(string body, Uri url, SearchQuery? query = null)
    {
        GeneralMarkup.Require(url.AbsoluteUri, "www.workwise.io");
        var doc = new HtmlParser().ParseDocument(body);
        try
        {
            using var json = JsonDocument.Parse(doc.QuerySelector("script#__NEXT_DATA__")?.TextContent ?? "");
            var state = GeneralMarkup.P(GeneralMarkup.P(GeneralMarkup.P(json.RootElement, "props"), "pageProps"), "initialState");
            var data = GeneralMarkup.P(GeneralMarkup.P(GeneralMarkup.P(state, "enquiries"), "matchings"), "data");
            var entries = GeneralMarkup.P(GeneralMarkup.P(data, "data"), "matchings");
            var paging = GeneralMarkup.P(data, "paging");
            if (entries.ValueKind != JsonValueKind.Array || GeneralMarkup.N(paging, "page") <= 0) throw new JsonException();
            var jobs = new List<JobListing>();
            foreach (var match in entries.EnumerateArray())
            {
                var item = GeneralMarkup.P(match, "enquiries");
                int id = GeneralMarkup.N(item, "id"); string title = GeneralMarkup.S(item, "name"), slug = GeneralMarkup.S(item, "slug");
                if (id <= 0 || title.Length == 0 || !Regex.IsMatch(slug, @"^[a-z0-9-]+$")) continue;
                string original = $"https://www.workwise.io/job/{id}-{slug}";
                var locations = GeneralMarkup.A(GeneralMarkup.P(item, "locationLevels"));
                string location = string.Join(", ", locations.Select(l => string.Join(" ", new[] { GeneralMarkup.S(l, "zip"), GeneralMarkup.S(l, "city") }.Where(x => x.Length > 0))).Where(x => x.Length > 0).Distinct());
                var card = doc.QuerySelector($"article:has(a[href='/job/{id}-{slug}'])");
                string salary = card?.QuerySelectorAll("p").Select(GeneralMarkup.Field).FirstOrDefault(t => t.Contains('€')) ?? "";
                var job = new JobListing { Id = id.ToString(CultureInfo.InvariantCulture), Source = "Workwise", Title = title, Company = GeneralMarkup.S(GeneralMarkup.P(item, "company"), "name"), Url = original, Location = location, Salary = salary, Summary = GeneralMarkup.S(item, "shortDescription"), Kind = Text.Classify(title, [GeneralMarkup.S(GeneralMarkup.P(item, "type"), "name")]), Remote = GeneralMarkup.A(GeneralMarkup.P(item, "jobLocationTypes")).Any(t => t.ValueKind == JsonValueKind.String && t.GetString() is "homeOffice" or "remote"), LogoUrl = DetailParser.Logo(card?.QuerySelector("img[alt^='Unternehmenslogo']"), url) };
                if (query is null || GeneralMarkup.MatchesLocal(job, query)) jobs.Add(job);
            }
            if (entries.GetArrayLength() > 0 && jobs.Count == 0 && query is null) throw new JsonException();
            int page = GeneralMarkup.N(paging, "page");
            var nextAnchor = doc.QuerySelectorAll("a[aria-label]").FirstOrDefault(a => a.GetAttribute("aria-label") == "Seite " + (page + 1));
            string? next = GeneralMarkup.Href(nextAnchor, url, "/jobs");
            if (next == url.AbsoluteUri) next = null;
            return new(jobs.DistinctBy(x => x.Key).ToList(), next, entries.GetArrayLength(), true);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException) { throw new SourceFailure(FailureKind.Format, "Workwise public job list format changed"); }
    }
}

public sealed class BundRssSource(SourceClient client) : IJobSource
{
    public string Id => "bund";
    public string Name => "bund.de RSS";
    public bool Supports(JobKind kind) => true;
    public static string SearchUrl(SearchQuery query) => "https://www.service.bund.de/Content/Globals/Functions/RSSFeed/RSSGenerator_Stellen.xml?nn=4642046";
    public async Task<JobPage> SearchAsync(SearchQuery query, string? next, CancellationToken cancellation)
    {
        // The advertised RSS feed is allowed; the automated search path is disallowed.
        if (next is not null) throw new SourceFailure(FailureKind.Format, "bund.de RSS has no continuation");
        var response = await client.GetAsync(SearchUrl(query), cancellation);
        cancellation.ThrowIfCancellationRequested();
        return await Task.Run(() => Parse(response.Body, response.Url, query), cancellation);
    }
    public static JobPage Parse(string body, Uri url, SearchQuery? query = null)
    {
        GeneralMarkup.Require(url.AbsoluteUri, "www.service.bund.de");
        try
        {
            using var reader = XmlReader.Create(new StringReader(body), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 12 * 1024 * 1024 });
            var doc = XDocument.Load(reader);
            var channel = doc.Element("rss")?.Element("channel") ?? throw new XmlException();
            var entries = channel.Elements("item").ToArray();
            var jobs = new List<JobListing>();
            foreach (var item in entries)
            {
                string title = Html.Plain(item.Element("title")?.Value), original = item.Element("link")?.Value ?? "";
                if (!Links.IsSafe(original) || new Uri(original).Host != "www.service.bund.de" || !new Uri(original).AbsolutePath.StartsWith("/IMPORTE/Stellenangebote/", StringComparison.Ordinal) || title.Length == 0) continue;
                original = new UriBuilder(original) { Fragment = "" }.Uri.AbsoluteUri;
                string description = item.Element("description")?.Value ?? "";
                string Label(string label) => Html.Plain(Regex.Match(description, Regex.Escape(label) + @":\s*<strong>(.*?)</strong>", RegexOptions.Singleline | RegexOptions.IgnoreCase).Groups[1].Value);
                var job = new JobListing { Id = new Uri(original).AbsolutePath, Source = "bund.de RSS", Title = title, Url = original, Company = Label("Arbeitgeber"), Location = Label("Ort"), Kind = Text.Classify(title), FeedDate = GeneralMarkup.Date(item.Element("pubDate")?.Value ?? ""), Summary = Html.Plain(description) };
                if (query is null || GeneralMarkup.MatchesLocal(job, query)) jobs.Add(job);
            }
            if (entries.Length > 0 && jobs.Count == 0 && query is null) throw new XmlException();
            return new(jobs.DistinctBy(j => j.Key).ToList(), null, entries.Length, true);
        }
        catch (Exception e) when (e is XmlException or InvalidOperationException or ArgumentException) { throw new SourceFailure(FailureKind.Format, "bund.de RSS format changed"); }
    }
}

public sealed class JobwareSource(SourceClient client) : IJobSource
{
    public string Id => "jobware";
    public string Name => "Jobware";
    public bool Supports(JobKind kind) => true;
    public static string SearchUrl(SearchQuery query)
    {
        var q = query.Clean(); string E(string s) => Uri.EscapeDataString(s);
        string filter = q.Kind switch { JobKind.Internship => "&f=t%3A1", JobKind.Apprenticeship => "&f=t%3A2", _ => "" };
        return $"https://www.jobware.de/jobs?q={E(q.Keyword)}&l={E(q.City)}&r={q.Radius}{filter}";
    }
    public async Task<JobPage> SearchAsync(SearchQuery query, string? next, CancellationToken cancellation)
    {
        var uri = GeneralMarkup.Require(next ?? SearchUrl(query), "www.jobware.de");
        if (!uri.AbsolutePath.StartsWith("/jobs", StringComparison.Ordinal)) throw new SourceFailure(FailureKind.Format, "Unexpected Jobware search URL");
        var response = await client.GetAsync(uri.AbsoluteUri, cancellation);
        cancellation.ThrowIfCancellationRequested();
        return await Task.Run(() => Parse(response.Body, response.Url, query), cancellation);
    }
    public static JobPage Parse(string body, Uri url, SearchQuery? query = null)
    {
        GeneralMarkup.Require(url.AbsoluteUri, "www.jobware.de");
        var doc = new HtmlParser().ParseDocument(body);
        var cards = doc.QuerySelectorAll(".resultlist .jobcardResultlist");
        var jobs = new List<JobListing>();
        foreach (var card in cards)
        {
            string title = GeneralMarkup.Field(card.QuerySelector(".job-card__title-wrapper h2"));
            string? original = GeneralMarkup.Href(card.QuerySelector("a[data-job-detail-link]"), url, "/job/");
            // Some public cards advertise their original URL on a button instead
            // of an anchor. Use that exact observed URL; do not construct slugs.
            var advertised = card.QuerySelector("button[data-job-detail-url]")?.GetAttribute("data-job-detail-url");
            if (original is null && advertised is not null && Uri.TryCreate(url, advertised, out var buttonUrl) && Links.IsSafe(buttonUrl.AbsoluteUri) && buttonUrl.Host == url.Host && buttonUrl.AbsolutePath.StartsWith("/job/", StringComparison.Ordinal)) original = buttonUrl.AbsoluteUri;
            if (title.Length == 0 || original is null) continue;
            var kind = Text.Classify(title);
            var place = card.QuerySelector(".job-card__location") ?? card.QuerySelectorAll(".job-card__details .tag__text-content").FirstOrDefault(t => t.QuerySelectorAll(".nrmdy-visually-hidden").Any(label => GeneralMarkup.Field(label) == "in"));
            var job = new JobListing { Id = new Uri(original).AbsolutePath, Source = "Jobware", Title = title, Company = GeneralMarkup.Field(card.QuerySelector(".job-card__advertiser-name")), Location = GeneralMarkup.WithoutLabels(place), Url = original, Kind = kind, FeedDate = GeneralMarkup.Date(card.QuerySelector("time[datetime]")?.GetAttribute("datetime") ?? ""), Remote = card.QuerySelectorAll(".job-card__labels .tag__text-content").Any(t => GeneralMarkup.Field(t).Equals("Home-Office", StringComparison.OrdinalIgnoreCase)), Summary = GeneralMarkup.Field(card.QuerySelector(".job-card__task")), LogoUrl = DetailParser.Logo(card.QuerySelector("img.job-card__logo"), url) };
            if (query is null || query.Kind == JobKind.All || query.Kind == kind) jobs.Add(job);
        }
        GeneralMarkup.EnsureCards(cards.Length, query is null ? jobs.Count : cards.Length, GeneralMarkup.Field(doc.QuerySelector("h1")), "Jobware");
        int page = int.TryParse(GeneralMarkup.QueryValue(url, "page").Split(':')[0], out var p) && p > 0 ? p : 1;
        var totalMatch = Regex.Match(GeneralMarkup.Field(doc.QuerySelector("h1")?.ParentElement?.QuerySelector(".subheadline")), @"([\d.]+)\s+Stellenangebote gefunden");
        int total = totalMatch.Success && int.TryParse(totalMatch.Groups[1].Value.Replace(".", ""), out var count) ? count : 0;
        // The observed public result list contains 20 cards per page, including
        // page=2. A partial final page must not create an endless continuation.
        string? next = cards.Length >= 20 && total > (long)page * 20 ? GeneralMarkup.SetQuery(url, "page", (page + 1).ToString(CultureInfo.InvariantCulture)) : null;
        return new(jobs.DistinctBy(j => j.Key).ToList(), next, cards.Length);
    }
}

public sealed class InteramtSource(SourceClient client) : IJobSource
{
    public string Id => "interamt";
    public string Name => "INTERAMT";
    public bool Supports(JobKind kind) => true;
    public static string SearchUrl(SearchQuery query) => "https://interamt.de/koop/app/stellensuche";
    public async Task<JobPage> SearchAsync(SearchQuery query, string? next, CancellationToken cancellation)
    {
        Download response;
        if (next is not null)
        {
            var continuation = GeneralMarkup.Require(next, "interamt.de");
            if (continuation.AbsolutePath != "/koop/app/trefferliste" || !continuation.Query.Contains("loadMoreLink", StringComparison.Ordinal)) throw new SourceFailure(FailureKind.Format, "Unexpected INTERAMT continuation");
            response = await client.GetAsync(next, cancellation);
        }
        else
        {
            // Wicket links are session-specific. Read a fresh public form for each
            // query; never reuse a cached token from another search.
            string fresh = SearchUrl(query) + "?antiCache=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var initial = await client.GetAsync(fresh, cancellation);
            var doc = new HtmlParser().ParseDocument(initial.Body);
            var form = doc.QuerySelector("form[aria-label='Stellendetailsuche'][method='post']") ?? throw new SourceFailure(FailureKind.Format, "INTERAMT public search form changed");
            if (!Uri.TryCreate(initial.Url, form.GetAttribute("action"), out var post)) throw new SourceFailure(FailureKind.Format, "INTERAMT search action changed");
            GeneralMarkup.Require(post.AbsoluteUri, "interamt.de");
            if (post.AbsolutePath != "/koop/app/stellensuche" || !post.Query.Contains("tabOneContentContainer-oneContent-1-item-form", StringComparison.Ordinal)) throw new SourceFailure(FailureKind.Format, "Unexpected INTERAMT form action");
            var fields = new Dictionary<string, string>();
            foreach (var input in form.QuerySelectorAll("input[name]"))
            {
                var type = input.GetAttribute("type") ?? "text";
                if (type is "password" or "file" || input.HasAttribute("disabled") || (type is "checkbox" or "radio" && !input.HasAttribute("checked"))) continue;
                fields[input.GetAttribute("name")!] = input.GetAttribute("value") ?? "";
            }
            var q = query.Clean();
            void Assign(string suffix, string value)
            {
                var input = form.QuerySelectorAll("input[name]").SingleOrDefault(i => i.GetAttribute("name")!.EndsWith(suffix, StringComparison.Ordinal)) ?? throw new SourceFailure(FailureKind.Format, "INTERAMT search field changed");
                fields[input.GetAttribute("name")!] = value;
            }
            Assign("plzDistance:PLZ", q.City);
            Assign("plzDistance:stellensucheFilterAttributes.maxEntfernung", q.Radius.ToString(CultureInfo.InvariantCulture));
            Assign("suchtextContainer:stellensucheFilterAttributes.suchText", q.Keyword);
            var submit = form.QuerySelector("button[name]")?.GetAttribute("name") ?? throw new SourceFailure(FailureKind.Format, "INTERAMT search submit changed");
            fields[submit] = "";
            if (q.Kind is JobKind.Apprenticeship or JobKind.Internship)
            {
                var group = form.QuerySelectorAll("input[type='checkbox']").FirstOrDefault(i => GeneralMarkup.Field(form.QuerySelector($"label[for='{i.Id}']")).Contains("Ausbildung/Praktikum/Duales Studium", StringComparison.Ordinal));
                if (group is not null) fields[group.GetAttribute("name")!] = group.GetAttribute("value") ?? "";
            }
            response = await client.PostFormAsync(post.AbsoluteUri, fields, cancellation);
        }
        cancellation.ThrowIfCancellationRequested();
        return await Task.Run(() => Parse(response.Body, response.Url, query), cancellation);
    }
    public static JobPage Parse(string body, Uri url, SearchQuery? query = null)
    {
        GeneralMarkup.Require(url.AbsoluteUri, "interamt.de");
        var doc = new HtmlParser().ParseDocument(body);
        var cards = doc.QuerySelectorAll("table tbody tr:has(td[data-field='StellenangebotId'])");
        var jobs = new List<JobListing>();
        foreach (var row in cards)
        {
            string F(string key) => GeneralMarkup.WithoutLabels(row.QuerySelector($"td[data-field='{key}']"));
            string id = F("StellenangebotId"), title = F("Stellenbezeichnung");
            if (!Regex.IsMatch(id, @"^\d{5,10}$") || title.Length == 0) continue;
            var kind = Text.Classify(title);
            var job = new JobListing { Id = id, Source = "INTERAMT", Title = title, Company = F("Behoerde"), Location = F("PLZOrte"), Kind = kind, Url = "https://interamt.de/koop/app/stelle?id=" + id, Salary = string.Join(" · ", new[] { F("BesoldungGruppeDisplayString"), F("TarifEbeneDisplayString") }.Where(s => s.Length > 0)), FeedDate = GeneralMarkup.GermanDate(F("Von")), Remote = F("Dienstort") is "Hybrid" or "Remote" or "Homeoffice" };
            if (query is null || query.Kind == JobKind.All || query.Kind == kind) jobs.Add(job);
        }
        var heading = GeneralMarkup.Field(doc.QuerySelector("h1.ia-e-head-1"));
        GeneralMarkup.EnsureCards(cards.Length, query is null ? jobs.Count : cards.Length, heading, "INTERAMT");
        string? next = null;
        var more = doc.QuerySelector("button.ia-m-searchresults__btn-load:not([disabled])");
        if (more is not null)
        {
            foreach (Match match in Regex.Matches(body, "Wicket\\.Ajax\\.ajax\\((\\{\"u\":\"[^\"\\r\\n]*loadMoreLink\"[^\\r\\n]*?\\})\\)"))
            {
                try
                {
                    using var callback = JsonDocument.Parse(match.Groups[1].Value);
                    if (GeneralMarkup.S(callback.RootElement, "c") != more.Id) continue;
                    if (!Uri.TryCreate(url, GeneralMarkup.S(callback.RootElement, "u"), out var uri) || !Links.IsSafe(uri.AbsoluteUri) || uri.Host != url.Host || uri.AbsolutePath != "/koop/app/trefferliste") continue;
                    next = uri.AbsoluteUri;
                    break;
                }
                catch (JsonException) { }
            }
            // A changed continuation is visible as partial coverage, not a fake next page.
        }
        return new(jobs.DistinctBy(j => j.Key).ToList(), next, cards.Length, more is not null && next is null);
    }
}

public sealed class BerlinCareerSource(SourceClient client) : IJobSource
{
    public string Id => "berlin";
    public string Name => "Karriereportal Berlin";
    public bool Supports(JobKind kind) => true;
    public static string SearchUrl(SearchQuery query) => "https://www.karriereportal-stellen.berlin.de/stellenangebote.html?search_mode=job_filter_advanced&filter%5Bvolltext%5D=" + Uri.EscapeDataString(query.Clean().Keyword);
    public async Task<JobPage> SearchAsync(SearchQuery query, string? next, CancellationToken cancellation)
    {
        var request = GeneralMarkup.Require(next ?? SearchUrl(query), "www.karriereportal-stellen.berlin.de");
        if (request.AbsolutePath != "/stellenangebote.html") throw new SourceFailure(FailureKind.Format, "Unexpected Berlin list URL");
        var response = await client.GetAsync(request.AbsoluteUri, cancellation);
        cancellation.ThrowIfCancellationRequested();
        return await Task.Run(() => Parse(response.Body, response.Url, query), cancellation);
    }
    public static JobPage Parse(string body, Uri url, SearchQuery? query = null)
    {
        GeneralMarkup.Require(url.AbsoluteUri, "www.karriereportal-stellen.berlin.de");
        var doc = new HtmlParser().ParseDocument(body);
        var cards = doc.QuerySelectorAll("tbody tr:has(td.real_table_col1 a[href])");
        var jobs = new List<JobListing>();
        foreach (var row in cards)
        {
            var link = row.QuerySelector("td.real_table_col1 a[href]");
            string? original = GeneralMarkup.Href(link, url, "/");
            var id = Regex.Match(original ?? "", @"-de-j(\d+)\.html$");
            string title = GeneralMarkup.Field(link);
            if (!id.Success || title.Length == 0 || original is null) continue;
            var kind = Text.Classify(title);
            var job = new JobListing { Id = id.Groups[1].Value, Source = "Karriereportal Berlin", Title = title, Url = original, Company = GeneralMarkup.Field(row.QuerySelector("td.real_table_col2")), Salary = GeneralMarkup.Field(row.QuerySelector("td.real_table_col3")), FeedDate = GeneralMarkup.GermanDate(GeneralMarkup.Field(row.QuerySelector("td.real_table_col4"))), Kind = kind };
            // The list is regional, but it does not publish the job's address.
            // Preserve unknown Location and never invent a Berlin postal code.
            bool region = query is null || query.City.Length == 0 || Text.Normalize(query.City) == "berlin";
            if (region && (query is null || query.Kind == JobKind.All || query.Kind == kind)) jobs.Add(job);
        }
        GeneralMarkup.EnsureCards(cards.Length, query is null ? jobs.Count : cards.Length, GeneralMarkup.Field(doc.QuerySelector(".number_jobs")), "Karriereportal Berlin");
        string? next = GeneralMarkup.Href(doc.QuerySelector(".nav_next a[title='Nächste Seite']"), url, "/stellenangebote.html");
        // The portal's pager drops the query from hrefs and carries its form state
        // in cookies. Keep the observed GET keyword filter explicit in every page.
        if (next is not null && query is not null)
        {
            next = GeneralMarkup.SetQuery(new Uri(next), "search_mode", "job_filter_advanced");
            next = GeneralMarkup.SetQuery(new Uri(next), "filter[volltext]", query.Keyword);
        }
        if (next == url.AbsoluteUri) next = null;
        return new(jobs.DistinctBy(j => j.Key).ToList(), next, cards.Length, true);
    }
}

public sealed class StellenanzeigenSource(SourceClient client) : IJobSource
{
    private static readonly ConcurrentDictionary<string, string> CityIds = new(StringComparer.Ordinal);
    public string Id => "stellenanzeigen";
    public string Name => "stellenanzeigen.de";
    public bool Supports(JobKind kind) => true;
    private static string Word(SearchQuery q) => string.Join(" ", new[] { q.Keyword, q.Kind switch { JobKind.Internship => "Praktikum", JobKind.Apprenticeship => "Ausbildung", _ => "" } }.Where(s => s.Length > 0));
    public static string SearchUrl(SearchQuery query)
    {
        var q = query.Clean();
        string id = "";
        if (q.City.Length > 0 && !CityIds.TryGetValue(Text.Normalize(q.City), out id!)) return CityUrl(q.City);
        return SearchWithCity(q, id);
    }
    private static string SearchWithCity(SearchQuery q, string id) => "https://www.stellenanzeigen.de/suche/?fulltext=" + Uri.EscapeDataString(Word(q)) + (id.Length > 0 ? "&locationIds=" + Uri.EscapeDataString(id) : "") + "&radius=" + q.Radius;
    private static string CityUrl(string city) => "https://www.stellenanzeigen.de/jobs/" + Uri.EscapeDataString(GeneralMarkup.Slug(city)) + "/";
    public async Task<JobPage> SearchAsync(SearchQuery query, string? next, CancellationToken cancellation)
    {
        if (next is not null) throw new SourceFailure(FailureKind.Format, "stellenanzeigen.de public feed has no verified continuation");
        var q = query.Clean();
        if (Regex.IsMatch(q.City, @"^\d{5}$")) throw new SourceFailure(FailureKind.Input, "Postal-code lookup is unavailable on this public feed");
        string cityId = q.City.Length > 0 ? CityIds.GetValueOrDefault(Text.Normalize(q.City)) ?? "" : "";
        if (q.City.Length > 0 && cityId.Length == 0)
        {
            var cityPage = await client.GetAsync(CityUrl(q.City), cancellation);
            cityId = ResolveCity(cityPage.Body, q.City);
            if (cityId.Length == 0) throw new SourceFailure(FailureKind.Format, "stellenanzeigen.de city could not be resolved");
            if (CityIds.Count >= 64) CityIds.Clear();
            CityIds[Text.Normalize(q.City)] = cityId;
        }
        var response = await client.GetAsync(SearchWithCity(q, cityId), cancellation);
        cancellation.ThrowIfCancellationRequested();
        return await Task.Run(() => Parse(response.Body, response.Url, q), cancellation);
    }
    public static string ResolveCity(string body, string requestedCity)
    {
        var doc = new HtmlParser().ParseDocument(body);
        foreach (var text in RscChunks(doc))
        {
            foreach (Match match in Regex.Matches(text, "\\{\"locationId\":\"([A-Z0-9-]+)\",\"locationName\":(\"(?:\\\\.|[^\"\\\\])*\")"))
            {
                try
                {
                    string name = JsonSerializer.Deserialize<string>(match.Groups[2].Value) ?? "";
                    if (Text.Normalize(name) == Text.Normalize(requestedCity) && Regex.IsMatch(match.Groups[1].Value, @"^M-[A-Z]{2}-\d+$")) return match.Groups[1].Value;
                }
                catch (JsonException) { }
            }
        }
        return "";
    }
    private static IEnumerable<string> RscChunks(IDocument doc)
    {
        const string prefix = "self.__next_f.push(";
        foreach (var script in doc.QuerySelectorAll("script"))
        {
            string code = script.TextContent;
            int begin = code.IndexOf(prefix, StringComparison.Ordinal), end = code.LastIndexOf(')');
            if (begin < 0 || end <= begin + prefix.Length || code.Length > 4 * 1024 * 1024) continue;
            string? result = null;
            try
            {
                using var json = JsonDocument.Parse(code[(begin + prefix.Length)..end]);
                if (json.RootElement.ValueKind == JsonValueKind.Array && json.RootElement.GetArrayLength() > 1 && json.RootElement[1].ValueKind == JsonValueKind.String) result = json.RootElement[1].GetString();
            }
            catch (JsonException) { }
            if (result is not null) yield return result;
        }
    }
    public static JobPage Parse(string body, Uri url, SearchQuery? query = null)
    {
        GeneralMarkup.Require(url.AbsoluteUri, "www.stellenanzeigen.de");
        var doc = new HtmlParser().ParseDocument(body);
        var count = Regex.Match(GeneralMarkup.Field(doc.QuerySelector("[data-testid='job-list-count']")), @"^0\s+Jobs", RegexOptions.IgnoreCase);
        if (count.Success) return new([], null, 0, true);
        if (query is not null && query.City.Length > 0 && !Text.Normalize(doc.QuerySelector("input[name='locationId']")?.GetAttribute("value") ?? "").Equals(Text.Normalize(query.City), StringComparison.Ordinal))
            throw new SourceFailure(FailureKind.Format, "stellenanzeigen.de did not apply the requested city");
        var cards = doc.QuerySelectorAll("div[data-jobid]:has(a[data-testid='qa-hitzone'])");
        var jobs = new List<JobListing>();
        foreach (var card in cards)
        {
            var link = card.QuerySelector("a[data-testid='qa-hitzone']");
            string? original = GeneralMarkup.Href(link, url, "/job/");
            string title = Text.Clean(link?.GetAttribute("title")), id = Text.Clean(card.GetAttribute("data-jobid"));
            if (original is null || title.Length == 0 || !Regex.IsMatch(id, @"^[\w-]{1,100}$")) continue;
            var icon = card.QuerySelector("i.fa-map-marker-alt");
            string location = icon is null ? "" : GeneralMarkup.Field(icon.ParentElement);
            var kind = Text.Classify(title);
            var job = new JobListing { Id = id, Source = "stellenanzeigen.de", Title = title, Url = original, Company = GeneralMarkup.Field(card.QuerySelector("[data-testid='company-name-text']")), Location = location, Kind = kind, Remote = card.QuerySelectorAll("[role='listitem']").Any(t => Regex.IsMatch(GeneralMarkup.Field(t), @"^(100% )?Home[- ]Office( möglich)?$", RegexOptions.IgnoreCase)), LogoUrl = DetailParser.Logo(card.QuerySelector("img[data-testid='company-logo']"), url) };
            if (query is null || query.Kind == JobKind.All || kind == query.Kind) jobs.Add(job);
        }
        GeneralMarkup.EnsureCards(cards.Length, query is null ? jobs.Count : cards.Length, GeneralMarkup.Field(doc.Body), "stellenanzeigen.de");
        // This public HTML contains the first result block. Unverified/ignored
        // page parameters must not masquerade as working pagination.
        return new(jobs.DistinctBy(j => j.Key).ToList(), null, cards.Length, true);
    }
}
