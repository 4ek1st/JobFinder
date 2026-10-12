using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JobFinder.Core;

public interface IJobSource
{
    string Id { get; }
    string Name { get; }
    bool Supports(JobKind kind);
    Task<JobPage> SearchAsync(SearchQuery query, string? next, CancellationToken cancellation);
}
public static class Html
{
    public static string Plain(string? html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var doc = new HtmlParser().ParseDocument(html);
        foreach (var node in doc.QuerySelectorAll("script,style,svg")) node.Remove();
        return Text.Clean(doc.Body?.TextContent);
    }
    public static string TextOf(IElement? element)
    {
        if (element is null) return "";
        var copy = (IElement)element.Clone(true);
        foreach (var node in copy.QuerySelectorAll("svg,script,style")) node.Remove();
        return Text.Clean(copy.TextContent);
    }
    public static string? SafeHref(IElement element, Uri root)
    {
        if (!Uri.TryCreate(root, element.GetAttribute("href"), out var uri) || uri.Host != root.Host || !Links.IsSafe(uri.AbsoluteUri)) return null;
        return uri.AbsoluteUri;
    }
}

public sealed class ArbeitnowSource(SourceClient client) : IJobSource
{
    public string Id => "arbeitnow";
    public string Name => "Arbeitnow";
    public bool Supports(JobKind kind) => true;
    public async Task<JobPage> SearchAsync(SearchQuery query, string? next, CancellationToken cancellation)
    {
        var response = await client.GetAsync(next ?? "https://www.arbeitnow.com/api/job-board-api", cancellation);
        cancellation.ThrowIfCancellationRequested();
        return await Task.Run(() => Parse(response.Body, query), cancellation);
    }
    public static JobPage Parse(string json, SearchQuery query)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) throw new JsonException();
            var jobs = new List<JobListing>();
            foreach (var item in data.EnumerateArray())
            {
                string S(string key) => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
                var url = S("url"); var title = Text.Clean(S("title"));
                if (!Links.IsSafe(url) || new Uri(url).Host != "www.arbeitnow.com" || title.Length == 0) continue;
                var types = item.TryGetProperty("job_types", out var jt) && jt.ValueKind == JsonValueKind.Array ? jt.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : [];
                DateTimeOffset? date = null;
                if (item.TryGetProperty("created_at", out var timestamp) && timestamp.TryGetInt64(out var seconds) && seconds > 0 && seconds < 253402300799) date = DateTimeOffset.FromUnixTimeSeconds(seconds);
                var summary = Html.Plain(S("description"));
                var job = new JobListing { Id = S("slug"), Source = "Arbeitnow", Title = title, Company = Text.Clean(S("company_name")), Location = Text.Clean(S("location")), Kind = Text.Classify(title, types), Url = url, FeedDate = date, Remote = item.TryGetProperty("remote", out var remote) && remote.ValueKind == JsonValueKind.True, Summary = summary[..Math.Min(500, summary.Length)], Details = DetailParser.FromDescription(S("description")) };
                if (Text.Matches(job, query)) jobs.Add(job);
            }
            string? next = root.TryGetProperty("links", out var links) && links.TryGetProperty("next", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            if (next is not null && (!SourceClient.IsSourceUrl(next) || new Uri(next).Host != "www.arbeitnow.com" || !new Uri(next).AbsolutePath.StartsWith("/api/job-board-api", StringComparison.Ordinal))) next = null;
            return new(jobs.DistinctBy(x => x.Key).ToList(), next, data.GetArrayLength(), true);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException) { throw new SourceFailure(FailureKind.Format, "Arbeitnow response format changed"); }
    }
}

public sealed class PraktikumSource(SourceClient client) : IJobSource
{
    public string Id => "praktikum";
    public string Name => "Praktikum.info";
    public bool Supports(JobKind kind) => kind is JobKind.All or JobKind.Job or JobKind.Internship;
    public async Task<JobPage> SearchAsync(SearchQuery query, string? next, CancellationToken cancellation)
    {
        var response = await client.GetAsync(next ?? Links.Portal(Id, query), cancellation);
        cancellation.ThrowIfCancellationRequested();
        var page = await Task.Run(() => Parse(response.Body, response.Url), cancellation);
        return page with { Jobs = page.Jobs.Where(j => query.Kind == JobKind.All || j.Kind == query.Kind).ToList() };
    }
    public static JobPage Parse(string body, Uri url)
    {
        var doc = new HtmlParser().ParseDocument(body);
        var cards = doc.QuerySelectorAll("a[data-job-offer-target='jobOfferTeaser']");
        var jobs = new List<JobListing>();
        foreach (var a in cards)
        {
            string? original = Html.SafeHref(a, url);
            string title = Html.TextOf(a.QuerySelector("h2"));
            if (original is null || title.Length == 0) continue;
            string location = string.Join(", ", a.QuerySelectorAll("li").Where(x => x.QuerySelector("svg title")?.TextContent == "Standort").Select(Html.TextOf));
            string company = Html.TextOf(a.QuerySelector("div.flex.flex-wrap span.text-secondary"));
            if (company.Length == 0) company = Text.Clean(a.QuerySelector("img[alt]")?.GetAttribute("alt")?.Replace(" Logo", ""));
            jobs.Add(new() { Id = a.Id ?? original, Source = "Praktikum.info", Title = title, Company = company, Location = location, Kind = Text.Classify(title), Url = original, Remote = a.TextContent.Contains("Homeoffice möglich", StringComparison.OrdinalIgnoreCase), LogoUrl = DetailParser.Logo(a.QuerySelector("img"), url) });
        }
        var nextElement = doc.QuerySelector("a[rel='next']") ?? doc.QuerySelectorAll("a[href]").FirstOrDefault(a => Regex.IsMatch(a.GetAttribute("aria-label") ?? Html.TextOf(a), @"^(Weiter|Nächste|Nächste Seite)$", RegexOptions.IgnoreCase));
        string? next = nextElement is null ? null : Html.SafeHref(nextElement, url);
        if (next == url.AbsoluteUri || (next is not null && !new Uri(next).AbsolutePath.StartsWith("/stellenangebote"))) next = null;
        if (cards.Length == 0)
        {
            var text = Html.TextOf(doc.Body).ToLowerInvariant();
            bool empty = Regex.IsMatch(text, @"(keine (passenden )?(stellenangebote|jobs|ergebnisse)|0 stellenangebote|leider (keine|nicht))");
            if (!empty) throw new SourceFailure(FailureKind.Format, "Praktikum.info layout changed or unavailable");
        }
        else if (jobs.Count == 0) throw new SourceFailure(FailureKind.Format, "Praktikum.info cards could not be read");
        return new(jobs.DistinctBy(x => x.Key).ToList(), next, cards.Length);
    }
}

public sealed class AzubiyoSource(SourceClient client) : IJobSource
{
    public string Id => "azubiyo";
    public string Name => "Azubiyo";
    public bool Supports(JobKind kind) => kind is JobKind.All or JobKind.Apprenticeship;
    public async Task<JobPage> SearchAsync(SearchQuery query, string? next, CancellationToken cancellation)
    {
        var response = await client.GetAsync(next ?? Links.Portal(Id, query), cancellation);
        cancellation.ThrowIfCancellationRequested();
        return await Task.Run(() => Parse(response.Body, response.Url), cancellation);
    }
    public static JobPage Parse(string body, Uri url)
    {
        var doc = new HtmlParser().ParseDocument(body);
        // Azubiyo renders "Ähnliche Stellen" cards even when the actual query has zero results.
        // They are recommendations, not matches for the user's search.
        var count = Html.TextOf(doc.QuerySelector("#divJobCount"));
        if (Regex.IsMatch(count, @"^0(?:\s|$)")) return new([], null, 0);
        var root = doc.QuerySelector("#jobSearchResults") ?? doc.QuerySelector("#job-search-job-list") ?? doc.DocumentElement;
        var cards = root.QuerySelectorAll("a[href^='/stellenanzeigen/']");
        var jobs = new List<JobListing>();
        foreach (var card in cards)
        {
            var original = Html.SafeHref(card, url);
            var title = Html.TextOf(card.QuerySelector("span.h3"));
            if (title.Length == 0 || original is null) continue;
            var details = card.QuerySelectorAll("ul li").Select(Html.TextOf).Where(x => x.Length > 0).ToArray();
            var start = details.FirstOrDefault(x => Regex.IsMatch(x, @"^\d{2}\.\d{2}\.\d{4}$") || Regex.IsMatch(x, @"^ab sofort$", RegexOptions.IgnoreCase)) ?? "";
            var salary = details.FirstOrDefault(x => x.Contains('€')) ?? "";
            var location = details.FirstOrDefault(x => x != start && x != salary) ?? "";
            jobs.Add(new() { Id = new Uri(original).AbsolutePath, Source = "Azubiyo", Title = title, Company = Html.TextOf(card.QuerySelector("span.text-secondary")), Location = location, Kind = JobKind.Apprenticeship, Url = original, Start = start, Salary = salary, LogoUrl = DetailParser.Logo(card.QuerySelector("img"), url) });
        }
        string? next = null;
        var nextElement = doc.QuerySelector("nav[aria-label='Seitennavigation'] a[aria-label='zur nächsten Seite navigieren']");
        if (nextElement is not null)
        {
            next = Html.SafeHref(nextElement, url);
            if (next is null)
            {
                // Verified browser navigation: /ausbildung/berlin/2/?subject=...&location=...&radius=25.
                var b = new UriBuilder(url);
                var path = b.Path.TrimEnd('/');
                var match = Regex.Match(path, @"/(\d+)$");
                int page = match.Success ? int.Parse(match.Groups[1].Value) : 1;
                if (match.Success) path = path[..match.Index];
                b.Path = path + "/" + (page + 1) + "/";
                next = b.Uri.AbsoluteUri;
            }
            if (next == url.AbsoluteUri || !new Uri(next).AbsolutePath.StartsWith("/ausbildung/")) next = null;
        }
        if (cards.Length == 0)
        {
            var text = Html.TextOf(doc.Body).ToLowerInvariant();
            if (!Regex.IsMatch(text, @"(keine (passenden )?(stellen|ergebnisse)|0 ausbildungspl|leider.*keine|keine treffer)")) throw new SourceFailure(FailureKind.Format, "Azubiyo layout changed or unavailable");
        }
        else if (jobs.Count == 0) throw new SourceFailure(FailureKind.Format, "Azubiyo cards could not be read");
        return new(jobs.DistinctBy(x => x.Key).ToList(), next, cards.Length);
    }
}

public sealed class SearchRunner(IEnumerable<IJobSource> sources)
{
    public IReadOnlyList<IJobSource> Sources { get; } = sources.ToList();
    public async Task RunAsync(SearchQuery query, IReadOnlyDictionary<string, string?> requests, Action<SourceResult> complete, CancellationToken cancellation)
    {
        using var parallel = new SemaphoreSlim(6);
        await Task.WhenAll(Sources.Where(s => requests.ContainsKey(s.Id) && s.Supports(query.Kind)).Select(async s =>
        {
            SourceResult result;
            bool admitted = false;
            try { await parallel.WaitAsync(cancellation); admitted = true; result = new(s.Id, await s.SearchAsync(query.Clean(), requests[s.Id], cancellation), null); }
            catch (OperationCanceledException) { result = new(s.Id, null, null, true); }
            catch (SourceFailure e) { result = new(s.Id, null, e); }
            catch (Exception e) when (e is not OutOfMemoryException) { result = new(s.Id, null, new(FailureKind.Format, "Source response unavailable")); }
            finally { if (admitted) parallel.Release(); }
            complete(result);
        }));
    }
}
