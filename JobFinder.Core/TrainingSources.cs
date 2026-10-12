using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JobFinder.Core;

internal static class TrainingHtml
{
    public static string Url(string host, string path, IEnumerable<KeyValuePair<string, string>> fields) =>
        "https://" + host + path + "?" + string.Join("&", fields.Select(x => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value)));
    public static IEnumerable<KeyValuePair<string, string>> Query(Uri url) => url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(part =>
    {
        var split = part.Split('=', 2);
        return new KeyValuePair<string, string>(Uri.UnescapeDataString(split[0].Replace('+', ' ')), split.Length == 2 ? Uri.UnescapeDataString(split[1].Replace('+', ' ')) : "");
    });
    public static string Param(Uri url, string key) => Query(url).FirstOrDefault(x => x.Key == key).Value ?? "";
    public static int Number(Uri url, string key, int fallback = 0) => int.TryParse(Param(url, key), out var n) && n >= 0 ? n : fallback;
    public static string PageUrl(Uri url, string key, int page)
    {
        var fields = Query(url).Where(x => x.Key != key).Append(new KeyValuePair<string, string>(key, page.ToString(CultureInfo.InvariantCulture)));
        return Url(url.Host, url.AbsolutePath, fields);
    }
    public static bool IsPage(string? value, string host, string path) => Links.IsSafe(value) && new Uri(value!).Host == host && new Uri(value!).AbsolutePath == path;
    public static string? Next(IElement? link, Uri root, string path)
    {
        var next = link is null ? null : Html.SafeHref(link, root);
        return next is not null && IsPage(next, root.Host, path) && next != root.AbsoluteUri ? next : null;
    }
    public static void ValidateNext(string? value, string host, string path)
    {
        if (value is not null && !IsPage(value, host, path)) throw new SourceFailure(FailureKind.Format, "Unexpected continuation URL");
    }
    public static string Rsc(IDocument doc)
    {
        var data = new StringBuilder();
        const string prefix = "self.__next_f.push(";
        foreach (var script in doc.QuerySelectorAll("script"))
        {
            var content = script.TextContent.Trim();
            if (!content.StartsWith(prefix, StringComparison.Ordinal) || content.Length > 2 * 1024 * 1024) continue;
            try
            {
                var literal = content[prefix.Length..].TrimEnd(';').TrimEnd(')');
                using var json = JsonDocument.Parse(literal);
                var root = json.RootElement;
                if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() >= 2 && root[0].TryGetInt32(out var type) && type == 1 && root[1].ValueKind == JsonValueKind.String)
                    data.Append(root[1].GetString());
            }
            catch (JsonException) { }
        }
        return data.ToString();
    }
    public static string? ObjectAfter(string text, string key)
    {
        var match = Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\\{");
        if (!match.Success) return null;
        int start = text.IndexOf('{', match.Index), depth = 0; bool quoted = false, escaped = false;
        for (int i = start; i < text.Length && i - start < 2 * 1024 * 1024; i++)
        {
            char c = text[i];
            if (escaped) { escaped = false; continue; }
            if (quoted && c == '\\') { escaped = true; continue; }
            if (c == '"') { quoted = !quoted; continue; }
            if (quoted) continue;
            if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return text[start..(i + 1)];
        }
        return null;
    }
    public static string String(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? Text.Clean(value.GetString()) : "";
    public static bool HasTerms(string text, string keyword) => Text.Normalize(keyword).Split(' ', StringSplitOptions.RemoveEmptyEntries).All(Text.Normalize(text).Contains);
}

public sealed class AusbildungSource(SourceClient client) : IJobSource
{
    public string Id => "ausbildung";
    public string Name => "Ausbildung.de";
    public bool Supports(JobKind kind) => kind is JobKind.All or JobKind.Apprenticeship;
    public static string SearchUrl(SearchQuery query)
    {
        var q = query.Clean();
        return TrainingHtml.Url("www.ausbildung.de", "/suche/", new Dictionary<string, string>
        {
            ["search"] = q.Keyword.Replace('|', ' ') + "|" + q.City.Replace('|', ' '), ["radius"] = q.Radius.ToString(CultureInfo.InvariantCulture)
        });
    }
    public async Task<JobPage> SearchAsync(SearchQuery query, string? next, CancellationToken cancellation)
    {
        if (!Supports(query.Kind)) return new([], null, 0);
        TrainingHtml.ValidateNext(next, "www.ausbildung.de", "/suche/");
        var response = await client.GetAsync(next ?? SearchUrl(query), cancellation);
        cancellation.ThrowIfCancellationRequested();
        return await Task.Run(() => Parse(response.Body, response.Url), cancellation);
    }
    public static JobPage Parse(string body, Uri url)
    {
        var doc = new HtmlParser().ParseDocument(body);
        if (doc.QuerySelector("[data-testid='search-result-empty-title']") is not null) return new([], null, 0);
        var results = TrainingHtml.ObjectAfter(TrainingHtml.Rsc(doc), "searchResults");
        if (results is null) throw new SourceFailure(FailureKind.Format, "Ausbildung.de response format changed");
        try
        {
            using var json = JsonDocument.Parse(results);
            var root = json.RootElement;
            if (!root.TryGetProperty("hits", out var hits) || !hits.TryGetProperty("primary", out var primary) || primary.ValueKind != JsonValueKind.Array || !root.TryGetProperty("meta", out var meta) || !meta.TryGetProperty("resultsCount", out var totalValue) || !totalValue.TryGetInt32(out var total) || total < 0) throw new JsonException();
            var jobs = new List<JobListing>();
            var cards = doc.QuerySelectorAll("article[data-testid='jp-card']");
            foreach (var hit in primary.EnumerateArray())
            {
                if (!hit.TryGetProperty("vacancyData", out var item)) continue;
                var id = TrainingHtml.String(item, "vacancyPublicId");
                var slug = TrainingHtml.String(item, "slug");
                var title = TrainingHtml.String(item, "title");
                if (!Guid.TryParse(id, out _) || title.Length == 0 || !Regex.IsMatch(slug, @"^[\p{L}\p{N}_-]+$")) continue;
                var original = new Uri(url, "/stellen/" + slug + "/").AbsoluteUri;
                var card = cards.FirstOrDefault(x => x.QuerySelector("a[href]")?.GetAttribute("href") == "/stellen/" + slug + "/");
                string company = TrainingHtml.String(item, "corporationName"), location = TrainingHtml.String(item, "location");
                if (card is not null)
                {
                    // The visible branch count belongs to this offer; keep it without making up the other cities.
                    var visibleLocation = Html.TextOf(card.QuerySelector("[data-testid='jp-branches']"));
                    if (visibleLocation.Length > 0) location = visibleLocation;
                }
                jobs.Add(new() { Id = id, Source = "Ausbildung.de", Title = title, Company = company, Location = location, Kind = JobKind.Apprenticeship, Url = original, Start = TrainingHtml.String(item, "startsNoEarlierThan"), LogoUrl = DetailParser.Logo(card?.QuerySelector("img[data-testid='logo']"), url) });
            }
            if (primary.GetArrayLength() > 0 && jobs.Count == 0) throw new JsonException();
            int offset = TrainingHtml.Number(url, "from");
            string? next = primary.GetArrayLength() > 0 && offset + primary.GetArrayLength() < total ? TrainingHtml.PageUrl(url, "from", offset + 20) : null;
            return new(jobs.DistinctBy(x => x.Key).ToList(), next, primary.GetArrayLength());
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException) { throw new SourceFailure(FailureKind.Format, "Ausbildung.de response format changed"); }
    }
}

public sealed class AubiPlusSource(SourceClient client) : IJobSource
{
    public string Id => "aubiplus";
    public string Name => "AUBI-plus";
    public bool Supports(JobKind kind) => kind is JobKind.All or JobKind.Apprenticeship;
    public static string SearchUrl(SearchQuery query)
    {
        var q = query.Clean();
        return TrainingHtml.Url("www.aubi-plus.de", "/suchmaschine/suche/", new Dictionary<string, string>
        {
            ["mSuggest"] = q.Keyword, ["aSuggest"] = q.City, ["fGeo"] = q.Radius.ToString(CultureInfo.InvariantCulture),
            ["fTyp[0]"] = "ausbildung", ["fTyp[1]"] = "duales-studium", ["fLand[0]"] = "deutschland"
        });
    }
    public async Task<JobPage> SearchAsync(SearchQuery query, string? next, CancellationToken cancellation)
    {
        if (!Supports(query.Kind)) return new([], null, 0);
        TrainingHtml.ValidateNext(next, "www.aubi-plus.de", "/suchmaschine/suche/");
        var response = await client.GetAsync(next ?? SearchUrl(query), cancellation);
        cancellation.ThrowIfCancellationRequested();
        return await Task.Run(() => Parse(response.Body, response.Url), cancellation);
    }
    public static JobPage Parse(string body, Uri url)
    {
        var doc = new HtmlParser().ParseDocument(body);
        var result = doc.QuerySelector(".result-container");
        if (result is null) throw new SourceFailure(FailureKind.Format, "AUBI-plus result layout changed");
        var cards = result.QuerySelectorAll("div.my-3.text-primary-dark.overflow-hidden.rounded-3");
        var jobs = new List<JobListing>();
        foreach (var card in cards)
        {
            // Collapsed 'weitere Stellen im Unternehmen' can violate the query. Read the matched primary card only.
            var main = card.Children.FirstOrDefault();
            var link = main?.QuerySelector("h2 a.stretched-link[title='Diese Stelle anzeigen']");
            var original = link is null ? null : Html.SafeHref(link, url);
            var title = Html.TextOf(link);
            if (original is null || title.Length == 0 || !(new Uri(original).AbsolutePath.StartsWith("/ausbildung/", StringComparison.Ordinal) || new Uri(original).AbsolutePath.StartsWith("/duales-studium/", StringComparison.Ordinal))) continue;
            var titleBox = link!.ParentElement?.ParentElement;
            var company = Html.TextOf(titleBox?.NextElementSibling);
            var location = Html.TextOf(main!.QuerySelector(".fa-location-dot")?.ParentElement?.QuerySelector("span"));
            var dates = main.QuerySelector(".fa-calendar-days")?.ParentElement?.QuerySelectorAll("span").Select(Html.TextOf).Where(x => x.Length > 0) ?? [];
            var id = main.QuerySelector("[data-type='Ausbildungsangebot'][data-id]")?.GetAttribute("data-id") ?? new Uri(original).AbsolutePath;
            jobs.Add(new() { Id = id, Source = "AUBI-plus", Title = title, Company = company, Location = location, Kind = JobKind.Apprenticeship, Url = original, Start = string.Join(" / ", dates), LogoUrl = DetailParser.Logo(main.QuerySelector("picture img"), url) });
        }
        if (cards.Length == 0)
        {
            if (!Html.TextOf(result.QuerySelector(".suchmaschine-title")).Contains("keine passenden Stellen gefunden", StringComparison.OrdinalIgnoreCase)) throw new SourceFailure(FailureKind.Format, "AUBI-plus result layout changed");
            return new([], null, 0);
        }
        if (jobs.Count == 0) throw new SourceFailure(FailureKind.Format, "AUBI-plus cards could not be read");
        string? next = TrainingHtml.Next(result.QuerySelector(".pagination .page-item:not(.disabled) a[aria-label^='Weiter zu Seite ']"), url, "/suchmaschine/suche/");
        if (next is not null)
        {
            var candidate = new Uri(next);
            if (TrainingHtml.Param(candidate, "mSuggest") != TrainingHtml.Param(url, "mSuggest") || TrainingHtml.Param(candidate, "aSuggest") != TrainingHtml.Param(url, "aSuggest") || TrainingHtml.Param(candidate, "fGeo") != TrainingHtml.Param(url, "fGeo")) next = null;
        }
        return new(jobs.DistinctBy(x => x.Key).ToList(), next, cards.Length);
    }
}

public sealed record IhkLocation(string Id, string Name);
public sealed class IhkTrainingSource(SourceClient client) : IJobSource
{
    private const string Host = "ihk-azubi-stellenmarkt.indexinternet.de";
    private static readonly ConcurrentDictionary<string, IhkLocation> Locations = new();
    public string Id => "ihk";
    public string Name => "Meine Ausbildung (IHK)";
    public bool Supports(JobKind kind) => kind is JobKind.All or JobKind.Apprenticeship;
    public static string SearchUrl(SearchQuery query)
    {
        var q = query.Clean();
        Locations.TryGetValue(Text.Normalize(q.City), out var location);
        return SearchUrl(q, location);
    }
    private static string SearchUrl(SearchQuery query, IhkLocation? location)
    {
        var fields = new Dictionary<string, string> { ["jobs"] = query.Keyword };
        if (location is not null)
        {
            fields["id_geopost_eo"] = location.Id; fields["id_geopost_name"] = location.Name; fields["umkreis_eo"] = query.Radius.ToString(CultureInfo.InvariantCulture);
        }
        return TrainingHtml.Url(Host, "/index.php", fields);
    }
    public async Task<JobPage> SearchAsync(SearchQuery query, string? next, CancellationToken cancellation)
    {
        query = query.Clean();
        if (!Supports(query.Kind)) return new([], null, 0);
        TrainingHtml.ValidateNext(next, Host, "/index.php");
        string request;
        if (next is not null) request = next;
        else
        {
            IhkLocation? location = null;
            if (query.City.Length > 0)
            {
                if (!Locations.TryGetValue(Text.Normalize(query.City), out location))
                {
                    var lookup = await client.PostFormAsync("https://" + Host + "/ajax.php", new Dictionary<string, string>
                    {
                        ["action"] = "geopost", ["stellenmarkt"] = "D", ["text"] = Uri.EscapeDataString(query.City.ToLowerInvariant())
                    }, cancellation);
                    location = ResolveLocation(lookup.Body, query.City);
                    if (location is not null)
                    {
                        if (Locations.Count > 100) Locations.Clear();
                        Locations[Text.Normalize(query.City)] = location;
                    }
                }
            }
            request = SearchUrl(query, location);
        }
        var response = await client.GetAsync(request, cancellation);
        cancellation.ThrowIfCancellationRequested();
        return await Task.Run(() => Parse(response.Body, response.Url, query), cancellation);
    }
    public static IhkLocation? ResolveLocation(string body, string city)
    {
        var doc = new HtmlParser().ParseDocument(body);
        var options = doc.QuerySelectorAll("option[value]");
        if (options.Length == 0 && !Regex.IsMatch(body, @"var\s+anzahl\s*=\s*0\b")) throw new SourceFailure(FailureKind.Format, "IHK place lookup changed");
        string requested = Text.Normalize(city);
        var matches = new List<IhkLocation>();
        foreach (var option in options)
        {
            var id = option.GetAttribute("value") ?? "";
            if (!Regex.IsMatch(id, @"^id_(ort|plz|ortsteil_2)=\d+$")) continue;
            var name = Html.TextOf(option);
            var plain = Regex.Replace(name, @"^(Stadt|Gemeinde|Ort|PLZ|Postleitzahl)\s+", "", RegexOptions.IgnoreCase);
            plain = Regex.Replace(plain, @"\s*\[[^]]+\]\s*$", "");
            bool exact = Text.Normalize(plain) == requested || Regex.IsMatch(city, @"^\d{5}$") && Regex.IsMatch(plain, @"^" + Regex.Escape(city) + @"(?:\s|$)");
            if (exact) matches.Add(new(id, name));
        }
        // Repeated city names across regions are ambiguous; never silently select a different town.
        return matches.DistinctBy(x => x.Id).Count() == 1 ? matches[0] : null;
    }
    public static JobPage Parse(string body, Uri url, SearchQuery? query = null)
    {
        var doc = new HtmlParser().ParseDocument(body);
        var cards = doc.QuerySelectorAll(".jobs > section.job");
        var count = Html.TextOf(doc.QuerySelector("span.ergebnisse_jobanzahl"));
        if (count == "0" && doc.QuerySelector(".jobs .keine_treffer") is not null) return new([], null, 0);
        var jobs = new List<JobListing>();
        foreach (var card in cards)
        {
            var link = card.QuerySelector("h3.job_title a[href]");
            var href = link?.GetAttribute("href");
            var title = Html.TextOf(link);
            if (!Links.IsSafe(href) || new Uri(href!).Host != "jobs.meine-ausbildung-in-deutschland.de" || new Uri(href!).AbsolutePath != "/klick_zaehler.cfm" || title.Length == 0) continue;
            string id = TrainingHtml.Param(new Uri(href!), "anzeige");
            if (!Regex.IsMatch(id, @"^\d+$")) continue;
            jobs.Add(new() { Id = id, Source = "Meine Ausbildung (IHK)", Title = title, Company = Html.TextOf(card.QuerySelector(".job-infos .firma")), Location = Html.TextOf(card.QuerySelector(".job-region")), Kind = JobKind.Apprenticeship, Url = href!, LogoUrl = DetailParser.Logo(card.QuerySelector(".firma-image img"), url) });
        }
        if (cards.Length == 0 || jobs.Count == 0) throw new SourceFailure(FailureKind.Format, "IHK training result layout changed");
        string? next = TrainingHtml.Next(doc.QuerySelector("li.paging-nav.forward a[href]"), url, "/index.php");
        if (next is not null)
        {
            var candidate = new Uri(next);
            foreach (var key in new[] { "jobs", "id_geopost_eo", "umkreis_eo" })
                if (TrainingHtml.Param(candidate, key) != TrainingHtml.Param(url, key)) next = null;
        }
        bool localCity = query is not null && query.City.Length > 0 && TrainingHtml.Param(url, "id_geopost_eo").Length == 0;
        var selected = localCity ? jobs.Where(j => Text.Normalize(j.Location).Contains(Text.Normalize(query!.City))) : jobs;
        return new(selected.DistinctBy(x => x.Key).ToList(), next, cards.Length, localCity);
    }
}

public sealed class LehrstellenRadarSource(SourceClient client) : IJobSource
{
    private const string Host = "www.lehrstellen-radar.de";
    private const string Path = "/5100,90,lsrlist.html";
    public string Id => "handwerk";
    public string Name => "Lehrstellenradar";
    public bool Supports(JobKind kind) => kind is JobKind.All or JobKind.Apprenticeship or JobKind.Internship;
    public static string SearchUrl(SearchQuery query)
    {
        var q = query.Clean();
        // The public API lookup followed by HTML search returned false empty results in runtime probes.
        // Use the independently verified postal-code form route; free-text keywords are checked locally below.
        return !Regex.IsMatch(q.City, @"^\d{5}$") ? TrainingHtml.Url(Host, "/5100,90,lsrsearch.html", new Dictionary<string, string> { ["search-plz"] = q.City, ["search-radius"] = q.Radius.ToString(CultureInfo.InvariantCulture) }) : SearchUrl(q, q.City, []);
    }
    private static string SearchUrl(SearchQuery query, string postcode, int[] professions)
    {
        var fields = new List<KeyValuePair<string, string>>
        {
            new("search-fromsearchform", "1"), new("search-plz", postcode), new("search-radius", query.Radius.ToString(CultureInfo.InvariantCulture))
        };
        if (query.Kind is JobKind.All or JobKind.Apprenticeship) fields.Add(new("search-ls", "1"));
        if (query.Kind is JobKind.All or JobKind.Internship) fields.Add(new("search-pr", "1"));
        fields.AddRange(professions.Select(x => new KeyValuePair<string, string>("search-jobs", x.ToString(CultureInfo.InvariantCulture))));
        return TrainingHtml.Url(Host, Path, fields);
    }
    public async Task<JobPage> SearchAsync(SearchQuery query, string? next, CancellationToken cancellation)
    {
        query = query.Clean();
        if (!Supports(query.Kind)) return new([], null, 0);
        TrainingHtml.ValidateNext(next, Host, Path);
        cancellation.ThrowIfCancellationRequested();
        if (!Regex.IsMatch(query.City, @"^\d{5}$")) throw new SourceFailure(FailureKind.Input, "Lehrstellenradar needs a five-digit postal code");
        string request = next ?? SearchUrl(query);
        var response = await client.GetAsync(request, cancellation);
        cancellation.ThrowIfCancellationRequested();
        return await Task.Run(() => Parse(response.Body, response.Url, query), cancellation);
    }
    public static string? ResolvePostalCode(string json, string city)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("postalCodes", out var codes) || codes.ValueKind != JsonValueKind.Array) throw new JsonException();
            foreach (var code in codes.EnumerateArray())
            {
                string postcode = TrainingHtml.String(code, "postalCode");
                if (TrainingHtml.String(code, "countryCode") == "DE" && Text.Normalize(TrainingHtml.String(code, "placeName")) == Text.Normalize(city) && Regex.IsMatch(postcode, @"^\d{5}$")) return postcode;
            }
            return null;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { throw new SourceFailure(FailureKind.Format, "Lehrstellenradar place lookup changed"); }
    }
    public static int[] ResolveProfessions(string json, string keyword)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException();
            return doc.RootElement.EnumerateArray().Where(x => TrainingHtml.HasTerms(TrainingHtml.String(x, "name"), keyword)).Select(x => x.TryGetProperty("berZdh", out var id) && id.TryGetInt32(out var value) ? value : 0).Where(x => x > 0).Distinct().Take(20).ToArray();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { throw new SourceFailure(FailureKind.Format, "Lehrstellenradar profession lookup changed"); }
    }
    public static JobPage Parse(string body, Uri url, SearchQuery? query = null)
    {
        var doc = new HtmlParser().ParseDocument(body);
        var root = doc.QuerySelector("#lsrresultlist");
        if (root is null) throw new SourceFailure(FailureKind.Format, "Lehrstellenradar result layout changed");
        var cards = root.QuerySelectorAll("a.list-group-item[data-resultid]");
        if (cards.Length == 0)
        {
            if (!Html.TextOf(root).Contains("Leider ergab die Suche keine Treffer", StringComparison.OrdinalIgnoreCase)) throw new SourceFailure(FailureKind.Format, "Lehrstellenradar result layout changed");
            return new([], null, 0);
        }
        var jobs = new List<JobListing>();
        int parsed = 0;
        foreach (var card in cards)
        {
            var id = card.GetAttribute("data-resultid") ?? "";
            if (!Regex.IsMatch(id, @"^\d+_\d+$")) continue;
            var detail = doc.GetElementById("detail_" + id);
            if (detail is null) continue;
            var original = detail.QuerySelector("a.btn[href*='jpguestdetail.html'], a.btn[href*='jobboardofferdetail.html']")?.GetAttribute("href");
            var title = Html.TextOf(card.QuerySelector(".list-group-item-heading strong"));
            bool internship = card.QuerySelector(".list-group-item-heading .icon-location-person-outlined") is not null;
            bool apprenticeship = card.QuerySelector(".list-group-item-heading .icon-location-person") is not null;
            if (!Links.IsSafe(original) || title.Length == 0 || !internship && !apprenticeship) continue;
            var info = card.QuerySelector("p.list-group-item-text");
            var parts = Regex.Split(info?.InnerHtml ?? "", @"<br\s*/?>", RegexOptions.IgnoreCase);
            var location = Html.Plain(parts.FirstOrDefault());
            var start = parts.Length > 1 ? Regex.Replace(Html.Plain(string.Join(" ", parts.Skip(1))), @"^\d+\s+(Lehrstellen?|Praktika|Praktikum)\s*", "", RegexOptions.IgnoreCase) : "";
            var companyLabel = detail.QuerySelectorAll("p strong").FirstOrDefault(x => Html.TextOf(x) == "Firma");
            var company = Html.TextOf(companyLabel?.ParentElement?.NextElementSibling);
            var copy = (IElement)detail.Clone(true);
            foreach (var node in copy.QuerySelectorAll("a.btn,script,style,iframe")) node.Remove();
            var details = DetailParser.FromDescription(copy.InnerHtml);
            var contactLabel = detail.QuerySelectorAll("p strong").FirstOrDefault(x => Html.TextOf(x) == "Kontakt");
            var contact = contactLabel?.ParentElement?.ParentElement;
            if (contact is not null)
            {
                var contacts = details.Contacts.ToList();
                string person = Html.TextOf(contactLabel!.ParentElement?.NextElementSibling);
                if (person.Length > 0 && !person.StartsWith("Telefon:", StringComparison.OrdinalIgnoreCase) && !person.StartsWith("E-Mail:", StringComparison.OrdinalIgnoreCase)) contacts.Add(new("person", "Kontakt", person));
                foreach (var span in contact.QuerySelectorAll("span"))
                {
                    var value = Html.TextOf(span);
                    if (!value.StartsWith("Telefon:", StringComparison.OrdinalIgnoreCase)) continue;
                    value = value[8..].Trim();
                    if (value.Length <= 40 && Regex.IsMatch(value, @"^\+?[0-9 ()/.-]+$") && value.Count(char.IsAsciiDigit) >= 5) contacts.Add(new("phone", "Telefon", value));
                }
                details = details with { Contacts = contacts.DistinctBy(x => (x.Kind, x.Value)).Take(16).ToArray() };
            }
            var job = new JobListing { Id = id, Source = "Lehrstellenradar", Title = title, Company = company, Location = location, Kind = internship ? JobKind.Internship : JobKind.Apprenticeship, Url = original!, Start = start, Details = details };
            parsed++;
            if (query is null || (query.Kind == JobKind.All || query.Kind == job.Kind) && TrainingHtml.HasTerms(job.Title + " " + job.Company, query.Keyword)) jobs.Add(job);
        }
        if (parsed == 0) throw new SourceFailure(FailureKind.Format, "Lehrstellenradar cards could not be read");
        int page = TrainingHtml.Number(url, "page", 1);
        int nextNumber = root.QuerySelectorAll("a.paging[data-page]").Select(x => int.TryParse(x.GetAttribute("data-page"), out var n) ? n : 0).Where(x => x > page).DefaultIfEmpty().Min();
        string? next = nextNumber > page ? TrainingHtml.PageUrl(url, "page", nextNumber) : null;
        bool localKeyword = query is not null && query.Keyword.Length > 0 && !TrainingHtml.Query(url).Any(x => x.Key == "search-jobs");
        return new(jobs.DistinctBy(x => x.Key).ToList(), next, cards.Length, localKeyword);
    }
}

public sealed class AzubiDeSource(SourceClient client) : IJobSource
{
    public string Id => "azubide";
    public string Name => "Azubi.de";
    public bool Supports(JobKind kind) => kind is JobKind.All or JobKind.Apprenticeship;
    public static string SearchUrl(SearchQuery query)
    {
        var q = query.Clean();
        return TrainingHtml.Url("www.azubi.de", "/suche", new Dictionary<string, string> { ["text"] = q.Keyword, ["location"] = q.City, ["radius"] = q.Radius.ToString(CultureInfo.InvariantCulture) });
    }
    private static bool IsPage(string? value)
    {
        if (!Links.IsSafe(value) || new Uri(value!).Host != "www.azubi.de") return false;
        string path = new Uri(value!).AbsolutePath;
        return path == "/suche" || path == "/ausbildungsplatz" || path.StartsWith("/beruf/", StringComparison.Ordinal) || path.StartsWith("/stadt/", StringComparison.Ordinal) || path.StartsWith("/duales-studium/", StringComparison.Ordinal);
    }
    public async Task<JobPage> SearchAsync(SearchQuery query, string? next, CancellationToken cancellation)
    {
        if (!Supports(query.Kind)) return new([], null, 0);
        if (next is not null && !IsPage(next)) throw new SourceFailure(FailureKind.Format, "Unexpected Azubi.de continuation");
        var response = await client.GetAsync(next ?? SearchUrl(query), cancellation);
        cancellation.ThrowIfCancellationRequested();
        return await Task.Run(() => Parse(response.Body, response.Url, query), cancellation);
    }
    public static JobPage Parse(string body, Uri url, SearchQuery? query = null)
    {
        var doc = new HtmlParser().ParseDocument(body);
        var cards = doc.QuerySelectorAll("a[data-job-offer-target='jobOfferTeaser']");
        if (cards.Length == 0)
        {
            if (Html.TextOf(doc.QuerySelector("main")).Contains("Leider konnten wir keine freien Stellen zu deiner Suche finden", StringComparison.OrdinalIgnoreCase)) return new([], null, 0);
            throw new SourceFailure(FailureKind.Format, "Azubi.de result layout changed");
        }
        var jobs = new List<JobListing>();
        foreach (var card in cards)
        {
            var original = Html.SafeHref(card, url);
            var title = Html.TextOf(card.QuerySelector("h2"));
            if (original is null || !new Uri(original).AbsolutePath.StartsWith("/ausbildungsplatz/", StringComparison.Ordinal) || title.Length == 0) continue;
            var company = Html.TextOf(card.QuerySelector("div.flex.flex-wrap span.text-secondary"));
            var location = string.Join(", ", card.QuerySelectorAll("li").Where(x => x.QuerySelector("svg title")?.TextContent == "Standort").Select(Html.TextOf));
            var job = new JobListing { Id = card.Id ?? new Uri(original).AbsolutePath, Source = "Azubi.de", Title = title, Company = company, Location = location, Kind = JobKind.Apprenticeship, Url = original, LogoUrl = DetailParser.Logo(card.QuerySelector("img"), url) };
            jobs.Add(job);
        }
        if (jobs.Count == 0) throw new SourceFailure(FailureKind.Format, "Azubi.de cards could not be read");
        var nextElement = doc.QuerySelector("link[rel='next']") ?? doc.QuerySelector("a[rel='next']");
        var next = nextElement is null ? null : Html.SafeHref(nextElement, url);
        if (!IsPage(next) || next == url.AbsoluteUri) next = null;
        // Verified Berlin/25 query advertises nationwide offers. Never represent an off-city card as a geographic match.
        // Exact text matching is bounded to each returned page; unknown distance and nearby towns remain unclaimed.
        bool localCity = query is not null && query.City.Length > 0;
        var selected = localCity ? jobs.Where(j => Text.Normalize(j.Location).Contains(Text.Normalize(query!.City))) : jobs;
        return new(selected.DistinctBy(x => x.Key).ToList(), next, cards.Length, localCity);
    }
}
