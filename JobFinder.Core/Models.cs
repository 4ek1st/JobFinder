using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace JobFinder.Core;

public enum JobKind { All, Job, Internship, Apprenticeship }
public enum FailureKind { Network, Timeout, Access, RateLimit, Format, Input }
public sealed record SearchQuery(string Keyword = "", string City = "", int Radius = 25, JobKind Kind = JobKind.All)
{
    public SearchQuery Clean() => this with { Keyword = Keyword.Trim()[..Math.Min(Keyword.Trim().Length, 120)], City = City.Trim()[..Math.Min(City.Trim().Length, 80)], Radius = new[] { 5, 10, 25, 50, 100 }.Contains(Radius) ? Radius : 25 };
    public string Label => string.Join(" · ", new[] { Keyword, City, Kind == JobKind.All ? "" : Kind.ToString() }.Where(x => x.Length > 0));
}

public sealed record SourceLink(string Source, string Url);
public sealed record JobListing
{
    public required string Id { get; init; }
    public required string Source { get; init; }
    public required string Title { get; init; }
    public string Company { get; init; } = "";
    public string Location { get; init; } = "";
    public JobKind Kind { get; init; } = JobKind.Job;
    public required string Url { get; init; }
    public string Salary { get; init; } = "";
    public string Start { get; init; } = "";
    public bool Remote { get; init; }
    public DateTimeOffset? FeedDate { get; init; }
    public DateTimeOffset RetrievedAt { get; init; } = DateTimeOffset.UtcNow;
    public string Summary { get; init; } = "";
    public string LogoUrl { get; init; } = "";
    public JobDetails? Details { get; init; }
    public List<SourceLink> OtherLinks { get; init; } = [];
    public string Key => Links.Canonical(Url);
    public string Fingerprint => string.Join("|", new[] { Title, Company, Location, Start }.Select(Text.Normalize));
    public bool CanMerge(JobListing other) => Key == other.Key || (Company.Length > 0 && other.Company.Length > 0 && Fingerprint == other.Fingerprint && Kind == other.Kind);
    public JobListing Merge(JobListing other) => this with { LogoUrl = LogoUrl.Length > 0 ? LogoUrl : other.LogoUrl, Details = Details ?? other.Details, OtherLinks = OtherLinks.Concat(new[] { new SourceLink(other.Source, other.Url) }).Concat(other.OtherLinks).Where(x => Links.Canonical(x.Url) != Key).DistinctBy(x => Links.Canonical(x.Url)).ToList() };
}

public sealed record DetailBlock(string Kind, string Text);
public sealed record JobContact(string Kind, string Label, string Value);
public sealed record JobDetails(IReadOnlyList<DetailBlock> Blocks, IReadOnlyList<JobContact> Contacts, string LogoUrl, DateTimeOffset RetrievedAt)
{
    public bool HasDescription => Blocks.Any(x => x.Text.Length > 0);
}

public sealed record JobPage(IReadOnlyList<JobListing> Jobs, string? Next, int Inspected, bool Limited = false);
public sealed record SourceResult(string Id, JobPage? Page, SourceFailure? Failure, bool Cancelled = false);
public sealed class SourceFailure(FailureKind kind, string message, DateTimeOffset? retryAt = null) : Exception(message)
{
    public FailureKind Kind { get; } = kind;
    public DateTimeOffset? RetryAt { get; } = retryAt;
}
public sealed record SavedSearch(string Name, SearchQuery Query);
public sealed record UserProfile
{
    public int Schema { get; init; } = 1;
    public string Language { get; set; } = "de";
    public string Theme { get; set; } = "dark";
    public SearchQuery LastQuery { get; set; } = new();
    public List<string> EnabledSources { get; set; } = SourceCatalog.FeedIds();
    public List<string> KnownSourceIds { get; set; } = [];
    public List<JobListing> Favorites { get; set; } = [];
    public List<SavedSearch> Searches { get; set; } = [];
}

public static class Text
{
    public static string Clean(string? value) => Regex.Replace(value ?? "", @"\s+", " ").Trim();
    public static string Normalize(string value)
    {
        var sb = new StringBuilder();
        foreach (var c in value.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(c));
        return Clean(sb.ToString().Replace("ß", "ss"));
    }
    // Classify the advertised position, never requirements buried in its description.
    public static JobKind Classify(string title, IEnumerable<string>? types = null)
    {
        string s = Normalize(title + " " + string.Join(" ", types ?? []));
        if (Regex.IsMatch(s, @"\b(ausbildung|auszubildende[nr]?|azubi|apprenticeship|duales studium)\b")) return JobKind.Apprenticeship;
        if (Regex.IsMatch(s, @"\b((pflicht|schuler|schueler|schul|vor)?praktikum|praktikant\w*|internship|intern)\b")) return JobKind.Internship;
        return JobKind.Job;
    }
    public static bool Matches(JobListing job, SearchQuery q)
    {
        if (q.Kind != JobKind.All && q.Kind != job.Kind) return false;
        // Arbeitnow has no documented query or coordinates: bounded local matching only.
        string terms = Normalize(job.Title + " " + job.Company + " " + job.Summary);
        if (q.Keyword.Length > 0 && !Normalize(q.Keyword).Split(' ', StringSplitOptions.RemoveEmptyEntries).All(term => term.Length <= 3 ? Regex.IsMatch(terms, @"(?<![\p{L}\p{N}])" + Regex.Escape(term) + @"(?![\p{L}\p{N}])") : terms.Contains(term))) return false;
        return q.City.Length == 0 || Normalize(job.Location).Contains(Normalize(q.City));
    }
}

public static class Links
{
    public static bool IsSafe(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == "https" && u.UserInfo.Length == 0 && !u.IsLoopback && u.Host.Length > 0;
    public static string Canonical(string url)
    {
        if (!IsSafe(url)) return "";
        var b = new UriBuilder(url) { Fragment = "" };
        b.Query = string.Join("&", b.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Where(x => !x.StartsWith("utm_", StringComparison.OrdinalIgnoreCase) && !x.StartsWith("gclid=", StringComparison.OrdinalIgnoreCase)));
        return b.Uri.AbsoluteUri;
    }
    public static string Portal(string id, SearchQuery query) => SourceCatalog.Portal(id, query);
}
