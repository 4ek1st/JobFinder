using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JobFinder.Core;

// These adapters use only observed, anonymous search operations. A provider's
// category or recommendation is never allowed to turn a Werkstudent into a Praktikum.
internal static class InternshipFeed
{
    internal sealed record Location(string Name, double Latitude, double Longitude);
    internal static string S(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    internal static string[] Strings(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString() ?? "").ToArray() : [];
    internal static JobKind Kind(string title, IEnumerable<string>? types = null, JobKind fallback = JobKind.Job)
    {
        var kind = Text.Classify(title);
        if (kind != JobKind.Job) return kind;
        if (Regex.IsMatch(Text.Normalize(title), @"\b(jahres|berufsorientierungs)(praktikum|praktikant\w*)\b")) return JobKind.Internship;
        if (Regex.IsMatch(Text.Normalize(title), @"\b(werkstudent\w*|working student|trainee\w*)\b")) return JobKind.Job;
        var supplied = Text.Classify("", types);
        return supplied != JobKind.Job ? supplied : fallback;
    }
    internal static bool KindMatches(JobListing job, SearchQuery? query) => query is null || query.Kind == JobKind.All || query.Kind == job.Kind;
    internal static string E(string value) => Uri.EscapeDataString(value);
    internal static DateTimeOffset? Date(string value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;
    internal static bool SafeSourceUrl(string? value, string host) => Links.IsSafe(value) && new Uri(value!).Host.Equals(host, StringComparison.OrdinalIgnoreCase);
    internal static void Request(string url, string host, Func<Uri, bool> path)
    {
        if (!SafeSourceUrl(url, host) || !path(new Uri(url))) throw new SourceFailure(FailureKind.Format, "Unexpected source continuation");
    }
    internal static Dictionary<string, string[]> Parameters(Uri url) => url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(p => p.Split('=', 2)).GroupBy(p => Uri.UnescapeDataString(p[0].Replace('+', ' ')), StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.Select(p => p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : "").ToArray(), StringComparer.Ordinal);
    internal static int Page(Uri url, string key = "page", int initial = 1)
    {
        var parameters = Parameters(url);
        return parameters.TryGetValue(key, out var value) && value.Length == 1 && int.TryParse(value[0], NumberStyles.None, CultureInfo.InvariantCulture, out var page) && page >= initial && page <= 10000 ? page : initial;
    }
    internal static string? Next(IDocument doc, Uri url, string selector, int initialPage = 1)
    {
        var element = doc.QuerySelector(selector);
        if (element is null || element.HasAttribute("disabled")) return null;
        var value = Html.SafeHref(element, url);
        if (value is null) return null;
        var next = new Uri(value);
        if (next.AbsolutePath != url.AbsolutePath || next.Fragment.Length > 0 || value == url.AbsoluteUri) return null;
        // A next-page link must retain every search condition, including coordinates.
        var currentParameters = Parameters(url); var nextParameters = Parameters(next);
        foreach (var pair in currentParameters.Where(p => p.Key != "page"))
            if (!nextParameters.TryGetValue(pair.Key, out var values) || !pair.Value.SequenceEqual(values)) return null;
        return Page(next, initial: initialPage) == Page(url, initial: initialPage) + 1 ? value : null;
    }
    internal static void CheckCards(string provider, int inspected, int parsed, bool explicitlyEmpty)
    {
        if (inspected == 0 && !explicitlyEmpty || inspected > 0 && parsed == 0) throw new SourceFailure(FailureKind.Format, provider + " response layout changed");
    }
    internal static async Task<Location?> ResolveAsync(SourceClient client, string query, CancellationToken cancellation)
    {
        if (query.Length == 0) return null;
        var response = await client.GetAsync("https://rest.meinpraktikum.de/api/v1/search/read/location/autocomplete/city/" + E(query), cancellation);
        return ParseLocation(response.Body, query) ?? throw new SourceFailure(FailureKind.Format, "City or postal code could not be resolved");
    }
    internal static Location? ParseLocation(string json, string query)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException();
            var exact = document.RootElement.EnumerateArray().Where(item => S(item, "country") == "Deutschland" &&
                (Text.Normalize(S(item, "name")) == Text.Normalize(query) || Text.Normalize(S(item, "city")) == Text.Normalize(query) || Strings(item, "postal_code").Contains(query))).ToArray();
            if (exact.Length != 1) return null;
            var item = exact[0];
            if (!item.TryGetProperty("lat", out var latitude) || !latitude.TryGetDouble(out var lat) || !item.TryGetProperty("lon", out var longitude) || !longitude.TryGetDouble(out var lon) || lat is < -90 or > 90 || lon is < -180 or > 180) return null;
            return new(S(item, "name"), lat, lon);
        }
        catch (JsonException) { throw new SourceFailure(FailureKind.Format, "Location response format changed"); }
    }
    internal static double Distance(double lat1, double lon1, double lat2, double lon2)
    {
        static double R(double degrees) => degrees * Math.PI / 180;
        var a = Math.Pow(Math.Sin(R(lat2 - lat1) / 2), 2) + Math.Cos(R(lat1)) * Math.Cos(R(lat2)) * Math.Pow(Math.Sin(R(lon2 - lon1) / 2), 2);
        return 6371 * 2 * Math.Asin(Math.Sqrt(Math.Min(1, a)));
    }
    internal static IDocument StreamingDocument(string body)
    {
        var doc = new HtmlParser().ParseDocument(body);
        // React streams individual employer fields after their card. Resolve its
        // explicit placeholder mapping as DOM data, without running page JavaScript.
        foreach (var script in doc.QuerySelectorAll("script"))
            foreach (Match match in Regex.Matches(script.TextContent, @"\$RS\(""(S:\d+)"",""(P:\d+)""\)"))
            {
                var source = doc.GetElementById(match.Groups[1].Value); var target = doc.GetElementById(match.Groups[2].Value);
                if (source is null || target?.Parent is null) continue;
                foreach (var node in source.ChildNodes.ToArray()) target.Parent.InsertBefore(node, target);
                target.Remove(); source.Remove();
            }
        return doc;
    }
}

public sealed class MeinPraktikumSource(SourceClient client) : IJobSource
{
    public const string SearchEndpoint = "https://rest.meinpraktikum.de/api/v1/search/read/jobposting/search";
    public string Id => "meinpraktikum";
    public string Name => "MeinPraktikum.de";
    public bool Supports(JobKind kind) => kind is JobKind.All or JobKind.Job or JobKind.Internship;
    public static string SearchUrl(SearchQuery query)
    {
        var q = query.Clean();
        return $"https://www.meinpraktikum.de/suche?type=jobposting&t_what={InternshipFeed.E(q.Keyword)}&t_where={InternshipFeed.E(q.City)}&t_radius={q.Radius}" + (q.Kind == JobKind.Internship ? "&internship_types=internship" : "");
    }
    public async Task<JobPage> SearchAsync(SearchQuery query, string? next, CancellationToken cancellation)
    {
        var q = query.Clean(); int page = 1;
        if (next is not null)
        {
            InternshipFeed.Request(next, "rest.meinpraktikum.de", u => u.AbsolutePath == new Uri(SearchEndpoint).AbsolutePath && Regex.IsMatch(u.Query, @"^\?page=\d+$"));
            page = InternshipFeed.Page(new(next));
        }
        var location = await InternshipFeed.ResolveAsync(client, q.City, cancellation);
        var where = new { input = location?.Name ?? "", name = location?.Name ?? "", lat = location?.Latitude, lon = location?.Longitude };
        var payload = JsonSerializer.Serialize(new { portal_type = "mp", where.input, where.name, where.lat, where.lon, what = q.Keyword, where, radius = q.Radius, page, sectors = Array.Empty<string>(), branches = Array.Empty<string>(), internship_types = q.Kind == JobKind.Internship ? new[] { "internship" } : [], state = "", customer = "", type = "jobposting" });
        var response = await client.PostJsonAsync(SearchEndpoint, payload, cancellation);
        cancellation.ThrowIfCancellationRequested();
        return await Task.Run(() => Parse(response.Body, new Uri(SearchEndpoint + "?page=" + page), q, location), cancellation);
    }
    public static JobPage Parse(string body, Uri url, SearchQuery? query = null) => Parse(body, url, query, null);
    private static JobPage Parse(string body, Uri url, SearchQuery? query, InternshipFeed.Location? location)
    {
        try
        {
            bool html = body.TrimStart().StartsWith('<');
            string json = html ? new HtmlParser().ParseDocument(body).QuerySelector("script#__NEXT_DATA__")?.TextContent ?? throw new JsonException() : body;
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement; JsonElement items; int total;
            if (html)
            {
                var initial = root.GetProperty("props").GetProperty("pageProps").GetProperty("initialResults");
                items = initial.GetProperty("results"); total = initial.GetProperty("count").GetInt32();
            }
            else
            {
                var hits = root.GetProperty("body").GetProperty("hits"); items = hits.GetProperty("hits"); total = hits.GetProperty("total").GetProperty("value").GetInt32();
            }
            if (items.ValueKind != JsonValueKind.Array || total < 0) throw new JsonException();
            var jobs = new List<JobListing>(); int valid = 0;
            foreach (var item in items.EnumerateArray())
            {
                var posting = html ? item : item.GetProperty("_source");
                var title = Text.Clean(InternshipFeed.S(posting, "title"));
                if (title.Length == 0 || !posting.TryGetProperty("vacancies", out var vacancies) || vacancies.ValueKind != JsonValueKind.Array) continue;
                var kind = InternshipFeed.Kind(title, InternshipFeed.Strings(posting, "internship_type"));
                var company = Text.Clean(InternshipFeed.S(posting, "companyDisplayName"));
                if (company.Length == 0) company = Text.Clean(InternshipFeed.S(posting, "companyName"));
                var logo = InternshipFeed.S(posting, "company_logo_url");
                if (!DetailParser.IsLogoUrl(logo)) logo = "";
                var description = Description(InternshipFeed.S(posting, "description"), logo);
                foreach (var vacancy in vacancies.EnumerateArray())
                {
                    var slug = InternshipFeed.S(vacancy, "slug");
                    if (!Regex.IsMatch(slug, @"^[\p{L}\p{N}._-]+$")) continue;
                    valid++;
                    var city = Text.Clean(InternshipFeed.S(vacancy, "city"));
                    if (location is not null && vacancy.TryGetProperty("geo", out var geo) && geo.TryGetProperty("lat", out var latitude) && latitude.TryGetDouble(out var lat) && geo.TryGetProperty("lon", out var longitude) && longitude.TryGetDouble(out var lon) && InternshipFeed.Distance(location.Latitude, location.Longitude, lat, lon) > query!.Radius) continue;
                    if (location is null && query?.City.Length > 0 && !Text.Normalize(city).Contains(Text.Normalize(query.City))) continue;
                    var job = new JobListing { Id = InternshipFeed.S(vacancy, "id"), Source = "MeinPraktikum.de", Title = title, Company = company, Location = city, Kind = kind, Url = "https://www.meinpraktikum.de/stellen/" + InternshipFeed.E(slug), LogoUrl = logo, FeedDate = InternshipFeed.Date(InternshipFeed.S(posting, "createdAt")), Start = InternshipFeed.S(posting, "employmentStart"), Summary = string.Join(" ", description.Blocks.Select(b => b.Text)).TakeText(500), Details = description.HasDescription ? description : null };
                    if (InternshipFeed.KindMatches(job, query)) jobs.Add(job);
                }
            }
            InternshipFeed.CheckCards("MeinPraktikum.de", items.GetArrayLength(), valid, total == 0);
            int page = InternshipFeed.Page(url);
            string? next = !html && items.GetArrayLength() > 0 && page * 10 < total ? SearchEndpoint + "?page=" + (page + 1) : null;
            return new(jobs.DistinctBy(j => j.Key).ToArray(), next, items.GetArrayLength(), html);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException) { throw new SourceFailure(FailureKind.Format, "MeinPraktikum.de response format changed"); }
    }
    public static JobDetails Description(string markdown, string logo = "")
    {
        var html = new StringBuilder(); var paragraph = new List<string>();
        void Flush() { if (paragraph.Count == 0) return; html.Append("<p>").Append(WebUtility.HtmlEncode(string.Join(" ", paragraph))).Append("</p>"); paragraph.Clear(); }
        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            line = Regex.Replace(line, @"\[([^\]]+)\]\([^)]+\)", "$1").Replace("**", "").Replace("__", "");
            if (line.Length == 0) { Flush(); continue; }
            var heading = Regex.Match(line, @"^#{1,6}\s+(.*)$");
            var bullet = Regex.Match(line, @"^[-*]\s+(.*)$");
            if (heading.Success || bullet.Success) { Flush(); var tag = heading.Success ? "h2" : "li"; html.Append('<').Append(tag).Append('>').Append(WebUtility.HtmlEncode(heading.Success ? heading.Groups[1].Value : bullet.Groups[1].Value)).Append("</").Append(tag).Append('>'); }
            else paragraph.Add(WebUtility.HtmlDecode(line));
        }
        Flush(); return DetailParser.FromDescription(html.ToString(), logo);
    }
}

internal static class InternshipStringExtensions
{
    internal static string TakeText(this string value, int limit) => value[..Math.Min(value.Length, limit)];
}

public sealed class AbsolventaSource(SourceClient client) : IJobSource
{
    public string Id => "absolventa";
    public string Name => "Absolventa.de";
    public bool Supports(JobKind kind) => kind is JobKind.All or JobKind.Job or JobKind.Internship;
    public static string SearchUrl(SearchQuery query)
    {
        var q = query.Clean();
        return $"https://www.absolventa.de/jobs?text={InternshipFeed.E(q.Keyword)}&location={InternshipFeed.E(q.City)}&radius={q.Radius}" + (q.Kind == JobKind.Internship ? "&positions%5B%5D=praktikum" : "");
    }
    public async Task<JobPage> SearchAsync(SearchQuery query, string? next, CancellationToken cancellation)
    {
        var target = next ?? SearchUrl(query);
        InternshipFeed.Request(target, "www.absolventa.de", u => u.AbsolutePath == "/jobs" || u.AbsolutePath.StartsWith("/jobs/", StringComparison.Ordinal));
        var response = await client.GetAsync(target, cancellation); cancellation.ThrowIfCancellationRequested();
        return await Task.Run(() => Parse(response.Body, response.Url, query), cancellation);
    }
    public static JobPage Parse(string body, Uri url, SearchQuery? query = null)
    {
        var doc = new HtmlParser().ParseDocument(body); var cards = doc.QuerySelectorAll("a[data-job-offer-target='jobOfferTeaser']"); var jobs = new List<JobListing>(); int valid = 0;
        foreach (var card in cards)
        {
            var original = Html.SafeHref(card, url); var title = Html.TextOf(card.QuerySelector("h2"));
            if (original is null || !new Uri(original).AbsolutePath.StartsWith("/stellenangebote/", StringComparison.Ordinal) || title.Length == 0) continue;
            valid++;
            var location = string.Join(", ", card.QuerySelectorAll("li").Where(li => li.QuerySelector("svg title")?.TextContent == "Standort").Select(Html.TextOf));
            var salaryNode = card.QuerySelectorAll("li").FirstOrDefault(li => li.QuerySelector("svg title")?.TextContent == "Gehaltsangabe");
            var salary = Html.TextOf(salaryNode?.QuerySelector("span[aria-hidden='true']") ?? salaryNode?.QuerySelector("span"));
            if (!salary.Contains('€')) salary = "";
            var job = new JobListing { Id = card.Id ?? original, Source = "Absolventa.de", Title = title, Company = Html.TextOf(card.QuerySelector("div.flex.flex-wrap > span.text-secondary")), Location = location, Kind = InternshipFeed.Kind(title), Url = original, Salary = salary, Remote = Html.TextOf(card).Contains("Homeoffice möglich", StringComparison.OrdinalIgnoreCase), LogoUrl = DetailParser.Logo(card.QuerySelector("img"), url) };
            // Sponsored nationwide cards occur even on a city search. Do not advertise them as local matches.
            if (!InternshipFeed.KindMatches(job, query) || query?.City.Length > 0 && !Text.Normalize(job.Location).Contains(Text.Normalize(query.City))) continue;
            jobs.Add(job);
        }
        InternshipFeed.CheckCards("Absolventa.de", cards.Length, valid, doc.QuerySelectorAll("p").Any(p => Html.TextOf(p).Contains("Leider konnten wir keine freien Stellen zu deiner Suche finden", StringComparison.OrdinalIgnoreCase)));
        var next = InternshipFeed.Next(doc, url, "link[rel='next'], a[rel='next']");
        return new(jobs.DistinctBy(j => j.Key).ToArray(), next, cards.Length, query?.City.Length > 0);
    }
}

public sealed class JobmensaSource(SourceClient client) : IJobSource
{
    public string Id => "jobmensa";
    public string Name => "Jobmensa.de";
    public bool Supports(JobKind kind) => true;
    public static string SearchUrl(SearchQuery query)
    {
        var q = query.Clean();
        return "https://www.jobmensa.de/jobs-suchen?job-title=" + InternshipFeed.E(q.Keyword) + (q.Kind switch { JobKind.Internship => "&categories=internship", JobKind.Apprenticeship => "&categories=apprenticeship&categories=dual_study", _ => "" });
    }
    public async Task<JobPage> SearchAsync(SearchQuery query, string? next, CancellationToken cancellation)
    {
        var q = query.Clean(); var target = next ?? SearchUrl(q);
        if (next is null && q.City.Length > 0)
        {
            var location = await InternshipFeed.ResolveAsync(client, q.City, cancellation);
            target += $"&location={InternshipFeed.E(location!.Name)}&latitude={location.Latitude.ToString("R", CultureInfo.InvariantCulture)}&longitude={location.Longitude.ToString("R", CultureInfo.InvariantCulture)}&radius={q.Radius}";
        }
        InternshipFeed.Request(target, "www.jobmensa.de", u => u.AbsolutePath == "/jobs-suchen" || u.AbsolutePath.StartsWith("/jobsuche/", StringComparison.Ordinal));
        var response = await client.GetAsync(target, cancellation); cancellation.ThrowIfCancellationRequested();
        return await Task.Run(() => Parse(response.Body, response.Url, q), cancellation);
    }
    public static JobPage Parse(string body, Uri url, SearchQuery? query = null)
    {
        var doc = InternshipFeed.StreamingDocument(body); var cards = doc.QuerySelectorAll("a[data-testid='job-offer-item']"); var jobs = new List<JobListing>(); int valid = 0;
        bool geographic = InternshipFeed.Parameters(url).ContainsKey("latitude") && InternshipFeed.Parameters(url).ContainsKey("longitude");
        foreach (var card in cards)
        {
            var original = Html.SafeHref(card, url); var title = Html.TextOf(card.QuerySelector("h2"));
            if (original is null || !Regex.IsMatch(new Uri(original).AbsolutePath, @"^/jobs/(in|ex)/[^/]+$") || title.Length == 0) continue;
            valid++;
            var companyNode = card.QuerySelectorAll("p").FirstOrDefault(p => p.QuerySelector("svg[data-icon='building']") is not null);
            var locationNode = card.QuerySelectorAll("p").FirstOrDefault(p => p.QuerySelector("svg[data-icon='location-dot']") is not null);
            var salary = Html.TextOf(card.QuerySelector("[data-testid='payment-per-hour']")); if (!salary.Contains('€')) salary = "";
            var job = new JobListing { Id = new Uri(original).AbsolutePath, Source = "Jobmensa.de", Title = title, Company = Html.TextOf(companyNode), Location = Html.TextOf(locationNode), Kind = InternshipFeed.Kind(title), Url = original, Salary = salary, LogoUrl = DetailParser.Logo(card.QuerySelector("img"), url), Remote = title.Contains("100% Remote", StringComparison.OrdinalIgnoreCase) || Html.TextOf(card).Contains("Remote", StringComparison.OrdinalIgnoreCase) };
            if (job.Company.Length == 0 && card.QuerySelector("svg[aria-label='jobvalley']") is not null) job = job with { Company = "jobvalley" };
            if (!InternshipFeed.KindMatches(job, query) || !geographic && query?.City.Length > 0 && !Text.Normalize(job.Location).Contains(Text.Normalize(query.City))) continue;
            jobs.Add(job);
        }
        InternshipFeed.CheckCards("Jobmensa.de", cards.Length, valid, doc.QuerySelectorAll("h3").Any(h => Html.TextOf(h) == "Kein passender Job dabei?"));
        var next = InternshipFeed.Next(doc, url, "nav[aria-label='Pagination'] a[rel='next'], a[aria-label='Nächste Seite'], a[rel='next']");
        if (next is null)
        {
            int nextPage = InternshipFeed.Page(url) + 1;
            var link = doc.QuerySelectorAll("nav a[href]").FirstOrDefault(a => Uri.TryCreate(url, a.GetAttribute("href"), out var candidate) && candidate.AbsolutePath == url.AbsolutePath && InternshipFeed.Page(candidate) == nextPage);
            if (link is not null) { link.SetAttribute("data-jobfinder-next", "true"); next = InternshipFeed.Next(doc, url, "a[data-jobfinder-next='true']"); }
        }
        return new(jobs.DistinctBy(j => j.Key).ToArray(), next, cards.Length, !geographic && query?.City.Length > 0);
    }
}

public sealed class UnicumSource(SourceClient client) : IJobSource
{
    public string Id => "unicum";
    public string Name => "UNICUM Karrierezentrum";
    public bool Supports(JobKind kind) => true;
    public static string SearchUrl(SearchQuery query)
    {
        var q = query.Clean(); var path = q.Kind switch { JobKind.Internship => "/praktikum", JobKind.Apprenticeship => "/ausbildung", _ => "/jobs" };
        return $"https://karriere.unicum.de{path}?search={InternshipFeed.E(q.Keyword)}&job_geo_location={InternshipFeed.E(q.City)}&radius={q.Radius}";
    }
    public async Task<JobPage> SearchAsync(SearchQuery query, string? next, CancellationToken cancellation)
    {
        var target = next ?? SearchUrl(query);
        InternshipFeed.Request(target, "karriere.unicum.de", u => u.AbsolutePath is "/jobs" or "/praktikum" or "/ausbildung");
        var response = await client.GetAsync(target, cancellation); cancellation.ThrowIfCancellationRequested();
        return await Task.Run(() => Parse(response.Body, response.Url, query), cancellation);
    }
    public static JobPage Parse(string body, Uri url, SearchQuery? query = null)
    {
        var doc = new HtmlParser().ParseDocument(body); var cards = doc.QuerySelectorAll(".view-content article.node-job"); var jobs = new List<JobListing>(); int valid = 0;
        foreach (var card in cards)
        {
            var anchor = card.QuerySelector(".node__title a.recruiter-job-link"); if (anchor is null) continue;
            var original = Html.SafeHref(anchor, url); var title = Html.TextOf(anchor);
            if (original is null || !Regex.IsMatch(new Uri(original).AbsolutePath, @"^/(job/|praktikum/details/|stellenangebot/details/|ausbildung/details/)") || title.Length == 0) continue;
            valid++;
            var job = new JobListing { Id = card.Id ?? original, Source = "UNICUM Karrierezentrum", Title = title, Company = Html.TextOf(card.QuerySelector(".recruiter-company-profile-job-organization")), Location = Html.TextOf(card.QuerySelector(".location")), Kind = InternshipFeed.Kind(title), Url = original, LogoUrl = DetailParser.Logo(card.QuerySelector(".job__logo img"), url), FeedDate = DateTimeOffset.TryParseExact(Html.TextOf(card.QuerySelector(".description .date")).TrimEnd(','), "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null };
            if (InternshipFeed.KindMatches(job, query)) jobs.Add(job);
        }
        InternshipFeed.CheckCards("UNICUM", cards.Length, valid, doc.QuerySelector(".view-empty") is not null);
        return new(jobs.DistinctBy(j => j.Key).ToArray(), InternshipFeed.Next(doc, url, "li.pager-next a", 0), cards.Length);
    }
    public static JobDetails ParseDetails(string body, Uri url, JobListing job)
    {
        var doc = new HtmlParser().ParseDocument(body);
        if (url.Host != "karriere.unicum.de" || Text.Normalize(Html.TextOf(doc.QuerySelector("h1"))) != Text.Normalize(job.Title)) throw new SourceFailure(FailureKind.Format, "The original posting changed");
        var content = doc.QuerySelector(".field--name-body [property='schema:description']");
        if (content is null) throw new SourceFailure(FailureKind.Format, "Description unavailable");
        var contact = doc.QuerySelector(".field--name-field-job-contact-info .field__item");
        return DetailParser.FromDescription(content.InnerHtml + (contact is null ? "" : "<p>" + contact.InnerHtml + "</p>"), job.LogoUrl);
    }
}

public sealed class StellenwerkSource(SourceClient client) : IJobSource
{
    // Names and routes were read from the portal's own /standorte page, including
    // joint regional portals; neither a radius nor an arbitrary city slug is inferred.
    private static readonly (string Name, string Slug)[] Locations =
    [
        ("Aachen", "aachen"), ("Augsburg", "augsburg"), ("Bamberg", "bamberg"), ("Berlin", "berlin"), ("Bielefeld", "bielefeld"),
        ("Bochum", "bochum"), ("Bonn", "bonn-rhein-sieg"), ("Bonn-Rhein-Sieg", "bonn-rhein-sieg"), ("Braunschweig", "braunschweig"),
        ("Bremen", "bremen"), ("Bremerhaven", "bremerhaven"), ("Celle", "celle"), ("Chemnitz", "chemnitz"), ("Cuxhaven", "cuxhaven"),
        ("Darmstadt", "darmstadt"), ("Dortmund", "dortmund"), ("Dresden", "dresden"), ("Duisburg", "duisburg-essen"), ("Essen", "duisburg-essen"),
        ("Düsseldorf", "duesseldorf"), ("Erding", "erding"), ("Erlangen", "erlangen-nuernberg"), ("Nürnberg", "erlangen-nuernberg"),
        ("Flensburg", "flensburg"), ("Frankfurt am Main", "frankfurt-am-main"), ("Gelsenkirchen", "gelsenkirchen"), ("Gießen", "giessen"),
        ("Göttingen", "goettingen"), ("Göppingen", "goeppingen"), ("Halle", "halle"), ("Hamburg", "hamburg"), ("Hannover", "hannover"),
        ("Heidelberg", "heidelberg"), ("Hildesheim", "hildesheim"), ("Ingolstadt", "ingolstadt"), ("Jena", "jena"), ("Kaiserslautern", "kaiserslautern"),
        ("Kassel", "kassel"), ("Kiel", "kiel"), ("Koblenz", "koblenz"), ("Köln", "koeln"), ("Leipzig", "leipzig"), ("Lübeck", "luebeck"),
        ("Ludwigsburg", "ludwigsburg"), ("Lüneburg", "lueneburg"), ("Magdeburg", "magdeburg"), ("Mainz", "mainz"), ("Mannheim", "mannheim"),
        ("München", "muenchen"), ("Münster", "muenster"), ("Oldenburg", "oldenburg"), ("Osnabrück", "osnabrueck"), ("Paderborn", "paderborn"),
        ("Pforzheim", "pforzheim"), ("Potsdam", "potsdam"), ("Regensburg", "regensburg"), ("Reutlingen", "reutlingen"), ("Rostock", "rostock"),
        ("Saarbrücken", "saarbruecken"), ("Schleswig-Holstein", "schleswig-holstein"), ("Schweinfurt", "schweinfurt"), ("Stade", "stade"),
        ("Stuttgart", "stuttgart"), ("Ulm", "ulm"), ("Wiesbaden", "wiesbaden"), ("Wuppertal", "wuppertal"), ("Würzburg", "wuerzburg")
    ];
    private static string? Portal(string city) => Locations.FirstOrDefault(l => Text.Normalize(l.Name) == Text.Normalize(city) || Text.Normalize(l.Slug) == Text.Normalize(city)).Slug;
    public string Id => "stellenwerk";
    public string Name => "stellenwerk";
    public bool Supports(JobKind kind) => kind is JobKind.All or JobKind.Job or JobKind.Internship;
    public static string SearchUrl(SearchQuery query) => "https://www.stellenwerk.de/" + (Portal(query.Clean().City) ?? "");
    public async Task<JobPage> SearchAsync(SearchQuery query, string? next, CancellationToken cancellation)
    {
        var q = query.Clean();
        if (Regex.IsMatch(q.City, @"^\d{5}$")) q = q with { City = (await InternshipFeed.ResolveAsync(client, q.City, cancellation))!.Name };
        var target = next ?? SearchUrl(q);
        InternshipFeed.Request(target, "www.stellenwerk.de", u => u.AbsolutePath == "/" || Locations.Any(l => u.AbsolutePath == "/" + l.Slug));
        var response = await client.GetAsync(target, cancellation); cancellation.ThrowIfCancellationRequested();
        return await Task.Run(() => Parse(response.Body, response.Url, q), cancellation);
    }
    public static JobPage Parse(string body, Uri url, SearchQuery? query = null)
    {
        var doc = new HtmlParser().ParseDocument(body);
        var pagination = doc.QuerySelectorAll("script").Select(s => Regex.Match(s.TextContent, @"pagination:\{start:(\d+),limit:(\d+),total:(\d+)\}")).FirstOrDefault(m => m.Success);
        int start = pagination is null ? 0 : int.Parse(pagination.Groups[1].Value, CultureInfo.InvariantCulture);
        int limit = pagination is null ? 0 : int.Parse(pagination.Groups[2].Value, CultureInfo.InvariantCulture);
        int total = pagination is null ? -1 : int.Parse(pagination.Groups[3].Value, CultureInfo.InvariantCulture);
        if (total >= 0 && start >= total) return new([], null, 0, true);
        var cards = doc.QuerySelectorAll("a[href]").Where(a => Regex.IsMatch(a.GetAttribute("href") ?? "", @"^/[a-z-]+/[^/?#]+-\d{6}-\d+$") && a.QuerySelector("p, h2, h3") is not null).ToArray();
        var jobs = new List<JobListing>(); int valid = 0;
        foreach (var card in cards)
        {
            var original = Html.SafeHref(card, url); var title = Html.TextOf(card.QuerySelector("p, h2, h3"));
            if (original is null || title.Length == 0) continue;
            valid++;
            var companyAndLocation = Html.TextOf(card.QuerySelector("p.text-gray-700")).Split('·', 2, StringSplitOptions.TrimEntries);
            var company = companyAndLocation.Length == 2 ? companyAndLocation[0] : "";
            var location = companyAndLocation.Length == 2 ? companyAndLocation[1] : "";
            var salary = card.QuerySelectorAll("span").Select(Html.TextOf).Where(t => t.Contains('€')).OrderBy(t => t.Length).FirstOrDefault() ?? "";
            var job = new JobListing { Id = new Uri(original).AbsolutePath, Source = "stellenwerk", Title = title, Company = company, Location = location, Kind = InternshipFeed.Kind(title), Url = original, Salary = salary, Remote = card.QuerySelectorAll("span").Any(s => Html.TextOf(s) == "Homeoffice möglich"), LogoUrl = DetailParser.Logo(card.QuerySelector("img"), url) };
            if (!InternshipFeed.KindMatches(job, query)) continue;
            if (query is not null && !Text.Matches(job, query with { City = "" })) continue;
            if (query?.City.Length > 0 && (location.Length > 0 || url.AbsolutePath == "/") && !Text.Normalize(location).Contains(Text.Normalize(query.City))) continue;
            jobs.Add(job);
        }
        InternshipFeed.CheckCards("stellenwerk", cards.Length, valid, total == 0 || doc.QuerySelectorAll("p").Any(p => Html.TextOf(p) == "Nichts dabei?"));
        string? next = null;
        if (pagination is not null && limit > 0 && limit <= 100 && start + limit < total)
        {
            var builder = new UriBuilder(url); var parameters = InternshipFeed.Parameters(url);
            parameters["pagination[start]"] = [(start + limit).ToString(CultureInfo.InvariantCulture)];
            builder.Query = string.Join("&", parameters.SelectMany(p => p.Value.Select(v => InternshipFeed.E(p.Key) + "=" + InternshipFeed.E(v))));
            next = builder.Uri.AbsoluteUri;
        }
        return new(jobs.DistinctBy(j => j.Key).ToArray(), next, cards.Length, true);
    }
}

public sealed class BerufsstartSource(SourceClient client) : IJobSource
{
    public string Id => "berufsstart";
    public string Name => "Berufsstart";
    public bool Supports(JobKind kind) => kind is JobKind.All or JobKind.Job or JobKind.Internship;
    // The free-form search renders results via a separate script service. This
    // adapter reads the public current internship page and applies text locally.
    public static string SearchUrl(SearchQuery query) => "https://www.berufsstart.de/praktikum/" + (Text.Normalize(query.City) switch { "berlin" => "berlin/", "hamburg" => "hamburg/", "stuttgart" => "stuttgart/", "mainz" => "mainz/", "koln" or "koeln" => "koeln/", "frankfurt" or "frankfurt am main" => "frankfurt/", "erlangen" => "erlangen/", "ulm" => "ulm/", "munchen" or "muenchen" => "muenchen/", "bochum" => "bochum/", "nurnberg" or "nuernberg" => "nuernberg/", _ => "" });
    public async Task<JobPage> SearchAsync(SearchQuery query, string? next, CancellationToken cancellation)
    {
        if (next is not null) throw new SourceFailure(FailureKind.Format, "Unverified Berufsstart continuation");
        var q = query.Clean();
        if (Regex.IsMatch(q.City, @"^\d{5}$")) q = q with { City = (await InternshipFeed.ResolveAsync(client, q.City, cancellation))!.Name };
        var response = await client.GetAsync(SearchUrl(q), cancellation); cancellation.ThrowIfCancellationRequested();
        return await Task.Run(() => Parse(response.Body, response.Url, q), cancellation);
    }
    public static JobPage Parse(string body, Uri url, SearchQuery? query = null)
    {
        var doc = new HtmlParser().ParseDocument(body);
        // Further tables are explicitly "Weitere interessante Jobs", unrelated recommendations.
        var table = doc.QuerySelectorAll("table").FirstOrDefault(t => t.QuerySelector("a strong.h2") is not null);
        var cards = table?.QuerySelectorAll("a strong.h2").ToArray() ?? [];
        var jobs = new List<JobListing>(); int valid = 0;
        foreach (var heading in cards)
        {
            var anchor = heading.Closest("a"); var row = heading.Closest("tr"); if (anchor is null || row is null) continue;
            var original = Html.SafeHref(anchor, url); var title = Html.TextOf(heading);
            if (original is null || !Regex.IsMatch(new Uri(original).AbsolutePath, @"^/\d+-[^/]+$") || title.Length == 0) continue;
            valid++;
            if (title.StartsWith("Anzeigen Flatrate", StringComparison.OrdinalIgnoreCase)) continue;
            var info = row.QuerySelector(".hier");
            var fields = new List<StringBuilder> { new() };
            if (info is not null) foreach (var node in info.ChildNodes)
            {
                if (node is IElement element && element.LocalName == "br") fields.Add(new());
                else fields[^1].Append(node.TextContent);
            }
            var company = Text.Clean(fields[0].ToString());
            var location = fields.Count > 1 ? Text.Clean(fields[1].ToString()) : "";
            var entry = Html.TextOf(info?.QuerySelectorAll("b").LastOrDefault());
            var description = row.QuerySelector("td:nth-child(2) .row > div.abstanduntenp20");
            var logoElement = row.QuerySelector("td:last-child img");
            var logo = logoElement?.GetAttribute("src")?.EndsWith("/leer.png", StringComparison.OrdinalIgnoreCase) == true ? "" : DetailParser.Logo(logoElement, url);
            var dateText = Html.TextOf(row.QuerySelector("td:first-child span"));
            var job = new JobListing { Id = new Uri(original).AbsolutePath, Source = "Berufsstart", Title = title, Company = company, Location = location, Kind = InternshipFeed.Kind(title, [entry]), Url = original, LogoUrl = logo, Summary = Html.TextOf(description), FeedDate = DateTimeOffset.TryParseExact(dateText, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null, Remote = Html.TextOf(info).Contains("Home-Office möglich", StringComparison.OrdinalIgnoreCase) };
            if (query is null || Text.Matches(job, query)) jobs.Add(job);
        }
        InternshipFeed.CheckCards("Berufsstart", cards.Length, valid, Regex.IsMatch(Html.TextOf(doc.Body), @"(keine (passenden )?(Stellen|Jobangebote|Jobs)|0 Stellenangebote)", RegexOptions.IgnoreCase));
        return new(jobs.DistinctBy(j => j.Key).ToArray(), null, cards.Length, true);
    }
}
