using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JobFinder.Core;

public interface IJobDetailService
{
    Task<JobDetails> LoadAsync(JobListing job, CancellationToken cancellation);
}

public sealed class JobDetailService(SourceClient client) : IJobDetailService
{
    public async Task<JobDetails> LoadAsync(JobListing job, CancellationToken cancellation)
    {
        if (!SourceClient.IsSourceUrl(job.Url)) throw new SourceFailure(FailureKind.Access, "External-only listing");
        var page = await client.GetAsync(job.Url, cancellation);
        cancellation.ThrowIfCancellationRequested();
        return await Task.Run(() => DetailParser.Parse(page.Body, page.Url, job), cancellation);
    }
}

public static class DetailParser
{
    // Images come only from the observed source card or that posting's organization.
    public static bool IsLogoUrl(string? value) => SourceCatalog.IsLogoUrl(value);
    public static string Logo(IElement? image, Uri root)
    {
        if (image is null) return "";
        foreach (var value in new[] { image.GetAttribute("src"), image.GetAttribute("data-src"), image.GetAttribute("srcset")?.Split(',')[0].Trim().Split(' ')[0] })
            if (!string.IsNullOrWhiteSpace(value) && Uri.TryCreate(root, value, out var uri) && IsLogoUrl(uri.AbsoluteUri)) return uri.AbsoluteUri;
        return "";
    }
    private static string Logo(JsonElement org, Uri root)
    {
        foreach (var key in new[] { "logo", "image" })
        {
            if (!org.TryGetProperty(key, out var item)) continue;
            var value = item.ValueKind == JsonValueKind.String ? item.GetString() : String(item, "url");
            if (!string.IsNullOrWhiteSpace(value) && Uri.TryCreate(root, value, out var uri) && IsLogoUrl(uri.AbsoluteUri)) return uri.AbsoluteUri;
        }
        return "";
    }
    public static JobDetails Parse(string body, Uri url, JobListing job)
    {
        if (url.Host == "karriere.unicum.de") return UnicumSource.ParseDetails(body, url, job);
        var doc = new HtmlParser().ParseDocument(body);
        if (url.Host is "interamt.de" or "www.interamt.de")
        {
            var interamtContent = doc.QuerySelector("#ia-tab-primary .ia-e-richtext");
            if (interamtContent is null || Text.Normalize(Html.TextOf(interamtContent.QuerySelector("h1"))) != Text.Normalize(job.Title)) throw new SourceFailure(FailureKind.Format, "The original posting changed");
            return FromDescription(interamtContent.InnerHtml, job.LogoUrl);
        }
        if (url.Host == "www.service.bund.de")
        {
            var article = doc.QuerySelector("article#main");
            string title = Html.TextOf(article?.QuerySelector(".content-box > section h1")).Replace("\u00AD", "");
            if (Text.Normalize(title) != Text.Normalize(job.Title.Replace("\u00AD", ""))) throw new SourceFailure(FailureKind.Format, "The original posting changed");
            var bundContent = article?.QuerySelector(".content-box > .text");
            if (bundContent is null) throw new SourceFailure(FailureKind.Format, "Description unavailable");
            foreach (var extra in bundContent.QuerySelectorAll(".hint, .map, script, style")) extra.Remove();
            return FromDescription(bundContent.InnerHtml, job.LogoUrl);
        }
        foreach (var script in doc.QuerySelectorAll("script[type='application/ld+json']"))
        {
            if (script.TextContent.Length > 2 * 1024 * 1024) continue;
            try
            {
                using var json = JsonDocument.Parse(RepairControlCharacters(script.TextContent), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                foreach (var posting in Postings(json.RootElement))
                {
                    // Never use a recommended posting or a site-wide organization as this job.
                    if (Text.Normalize(Html.Plain(String(posting, "title"))) != Text.Normalize(job.Title)) continue;
                    var description = String(posting, "description");
                    if (description.Length == 0) continue;
                    var result = url.Host == "www.meinpraktikum.de" ? MeinPraktikumSource.Description(description, job.LogoUrl) : FromDescription(description, job.LogoUrl);
                    var contacts = result.Contacts.ToList();
                    if (posting.TryGetProperty("applicationContact", out var contact)) ReadContact(contact, contacts);
                    string logo = posting.TryGetProperty("hiringOrganization", out var org) && org.ValueKind == JsonValueKind.Object ? Logo(org, url) : "";
                    return result with { Contacts = contacts.DistinctBy(x => (x.Kind, x.Value)).Take(16).ToArray(), LogoUrl = result.LogoUrl.Length > 0 ? result.LogoUrl : logo };
                }
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException) { }
        }
        var heading = Html.TextOf(doc.QuerySelector("h1"));
        if (Text.Normalize(heading) != Text.Normalize(job.Title)) throw new SourceFailure(FailureKind.Format, "The original posting changed");
        // Verified description containers, scoped to this original page; never the whole body/footer.
        var content = doc.QuerySelector("[itemprop='description']");
        if (content is not null) return FromDescription(content.InnerHtml, job.LogoUrl);
        if (url.Host == "www.praktikum.info")
        {
            var descriptions = doc.QuerySelectorAll("div.prose-turtles");
            if (descriptions.Length > 0) return FromDescription(string.Join("\n", descriptions.Select(x => x.InnerHtml)), job.LogoUrl);
        }
        throw new SourceFailure(FailureKind.Format, "Description unavailable");
    }
    private static string String(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static IEnumerable<JsonElement> Postings(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array) { foreach (var item in element.EnumerateArray()) foreach (var posting in Postings(item)) yield return posting; }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("@type", out var type) && (type.ValueKind == JsonValueKind.String && type.GetString() == "JobPosting" || type.ValueKind == JsonValueKind.Array && type.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == "JobPosting"))) yield return element;
            if (element.TryGetProperty("@graph", out var graph)) foreach (var posting in Postings(graph)) yield return posting;
        }
    }
    // Two current sources emit literal line breaks inside JSON strings. Repair only those controls.
    private static string RepairControlCharacters(string value)
    {
        var result = new StringBuilder(value.Length); bool quoted = false, escaped = false;
        foreach (char c in value)
        {
            if (quoted && c < ' ') { result.Append(c switch { '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", _ => " " }); continue; }
            result.Append(c);
            if (escaped) { escaped = false; continue; }
            if (c == '\\' && quoted) escaped = true; else if (c == '"') quoted = !quoted;
        }
        return result.ToString();
    }
    public static JobDetails FromDescription(string html, string logo = "")
    {
        // Entity-encoded markup is decoded once, then entities inside it are decoded by the HTML parser.
        if (!html.Contains('<') && html.Contains("&lt;", StringComparison.OrdinalIgnoreCase)) html = WebUtility.HtmlDecode(html);
        var doc = new HtmlParser().ParseDocument(html);
        foreach (var node in doc.QuerySelectorAll("script,style,svg,iframe,form,nav,footer,aside,noscript")) node.Remove();
        var blocks = new List<DetailBlock>(); var buffer = new StringBuilder(); int length = 0;
        void Add(string kind, string value)
        {
            value = Text.Clean(value); if (value.Length == 0 || length >= 60000 || blocks.Count >= 240) return;
            value = value[..Math.Min(value.Length, 60000 - length)]; length += value.Length;
            if (blocks.Count == 0 || blocks[^1] != new DetailBlock(kind, value)) blocks.Add(new(kind, value));
        }
        void Flush() { Add("paragraph", buffer.ToString()); buffer.Clear(); }
        void Visit(INode node)
        {
            if (node is IText text) { buffer.Append(text.Data); return; }
            if (node is not IElement element) return;
            string tag = element.LocalName;
            if (tag is "h1" or "h2" or "h3" or "h4" or "h5" or "h6") { Flush(); Add("heading", Html.TextOf(element)); return; }
            if (tag == "li") { Flush(); Add("bullet", Html.TextOf(element)); return; }
            if (tag == "br") { Flush(); return; }
            bool block = tag is "p" or "div" or "section" or "article" or "ul" or "ol" or "tr";
            if (block) Flush();
            foreach (var child in element.ChildNodes) Visit(child);
            if (block) Flush();
        }
        if (doc.Body is not null) foreach (var child in doc.Body.ChildNodes) Visit(child); Flush();
        var contacts = new List<JobContact>();
        if (doc.Body is not null)
        {
            foreach (var a in doc.Body.QuerySelectorAll("a[href]"))
            {
                var href = a.GetAttribute("href") ?? "";
                if (href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) AddEmail(href[7..].Split('?')[0], contacts);
                else if (href.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)) AddPhone(href[4..], contacts);
            }
            foreach (Match match in Regex.Matches(Html.TextOf(doc.Body), @"[\w.!#$%&'*+/=?^`{|}~-]+@[\w.-]+\.[a-zA-Z]{2,}")) AddEmail(match.Value, contacts);
        }
        return new(blocks, contacts.DistinctBy(x => (x.Kind, x.Value)).Take(16).ToArray(), IsLogoUrl(logo) ? logo : "", DateTimeOffset.UtcNow);
    }
    private static void ReadContact(JsonElement contact, List<JobContact> result)
    {
        if (contact.ValueKind == JsonValueKind.Array) { foreach (var item in contact.EnumerateArray()) ReadContact(item, result); return; }
        if (contact.ValueKind != JsonValueKind.Object) return;
        var name = String(contact, "name"); if (name.Length > 0) result.Add(new("person", "", Text.Clean(name)));
        AddEmail(String(contact, "email"), result); AddPhone(String(contact, "telephone"), result);
    }
    private static void AddEmail(string value, List<JobContact> result)
    {
        value = Uri.UnescapeDataString(value).Trim();
        if (value.Length <= 160 && !value.Any(char.IsControl) && MailAddress.TryCreate(value, out var mail) && mail.Address == value && mail.Host.Contains('.')) result.Add(new("email", "E-mail", value));
    }
    private static void AddPhone(string value, List<JobContact> result)
    {
        value = Uri.UnescapeDataString(value).Trim();
        if (value.Length is >= 5 and <= 40 && Regex.IsMatch(value, @"^\+?[0-9 ()/.-]+$") && value.Count(char.IsAsciiDigit) >= 5) result.Add(new("phone", "Telefon", value));
    }
}
