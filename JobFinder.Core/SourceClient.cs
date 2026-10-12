using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace JobFinder.Core;

public sealed record Download(string Body, Uri Url, bool Cached);
public sealed class SourceClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, Download Data)> _cache = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _cooldown = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _last = new();
    private readonly TimeSpan _spacing;
    public SourceClient(HttpMessageHandler? handler = null, TimeSpan? spacing = null)
    {
        _spacing = spacing ?? TimeSpan.FromSeconds(2);
        _http = handler is null ? new(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = true, MaxAutomaticRedirections = 4 }) : new(handler);
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("JobFinder/0.4 (personal user-initiated job search)");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("de-DE,de;q=0.9,en;q=0.6");
    }
    public static bool IsSourceUrl(string url) => SourceCatalog.IsSourceUrl(url);
    public Task<Download> GetAsync(string url, CancellationToken cancellation) => SendAsync(url, HttpMethod.Get, null, url, cancellation);
    public Task<Download> PostJsonAsync(string url, string json, CancellationToken cancellation)
    {
        if (!SourceCatalog.IsReadOnlyPostUrl(url) || json.Length > 65536) throw new SourceFailure(FailureKind.Format, "Unverified search endpoint");
        return SendAsync(url, HttpMethod.Post, () => new StringContent(json, Encoding.UTF8, "application/json"), PostKey(url, json), cancellation);
    }
    public Task<Download> PostFormAsync(string url, IReadOnlyDictionary<string, string> fields, CancellationToken cancellation)
    {
        if (!SourceCatalog.IsReadOnlyPostUrl(url) || fields.Count > 100 || fields.Sum(x => x.Key.Length + x.Value.Length) > 65536) throw new SourceFailure(FailureKind.Format, "Unverified search endpoint");
        var ordered = fields.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray();
        string fingerprint = System.Text.Json.JsonSerializer.Serialize(ordered);
        return SendAsync(url, HttpMethod.Post, () => new FormUrlEncodedContent(ordered), PostKey(url, fingerprint), cancellation);
    }
    private static string PostKey(string url, string body) => url + "#POST-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(body)));
    private async Task<Download> SendAsync(string url, HttpMethod method, Func<HttpContent>? content, string cacheKey, CancellationToken cancellation)
    {
        if (!IsSourceUrl(url)) throw new SourceFailure(FailureKind.Format, "Unexpected source URL");
        cancellation.ThrowIfCancellationRequested();
        if (_cache.TryGetValue(cacheKey, out var hit) && DateTimeOffset.UtcNow - hit.At < TimeSpan.FromMinutes(3)) return hit.Data with { Cached = true };
        string host = new Uri(url).Host;
        string provider = SourceCatalog.ProviderKey(host);
        var gate = _locks.GetOrAdd(provider, _ => new(1));
        await gate.WaitAsync(cancellation);
        try
        {
            if (_cache.TryGetValue(cacheKey, out hit) && DateTimeOffset.UtcNow - hit.At < TimeSpan.FromMinutes(3)) return hit.Data with { Cached = true };
            if (_cooldown.TryGetValue(provider, out var until) && until > DateTimeOffset.UtcNow) throw new SourceFailure(FailureKind.RateLimit, "Source cooldown", until);
            if (_last.TryGetValue(provider, out var last))
            {
                var delay = last + (_spacing > TimeSpan.Zero ? TimeSpan.FromTicks(Math.Max(_spacing.Ticks, SourceCatalog.Spacing(host).Ticks)) : _spacing) - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellation);
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(22));
            try
            {
                _last[provider] = DateTimeOffset.UtcNow;
                using var request = new HttpRequestMessage(method, url) { Content = content?.Invoke() };
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.StatusCode == (HttpStatusCode)429)
                {
                    var wait = response.Headers.RetryAfter?.Date ?? DateTimeOffset.UtcNow + (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(2));
                    _cooldown[provider] = wait;
                    throw new SourceFailure(FailureKind.RateLimit, "Source rate limit", wait);
                }
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) throw new SourceFailure(FailureKind.Access, "Source denied access");
                if (!response.IsSuccessStatusCode) throw new SourceFailure(FailureKind.Network, $"HTTP {(int)response.StatusCode}");
                var final = response.RequestMessage?.RequestUri ?? new Uri(url);
                if (!SourceCatalog.SameSource(url, final.AbsoluteUri)) throw new SourceFailure(FailureKind.Access, "Unexpected redirect");
                if (System.Text.RegularExpressions.Regex.IsMatch(final.AbsolutePath, @"^/(?:account/)?(?:login|anmelden|signin)(?:/|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) throw new SourceFailure(FailureKind.Access, "Original posting requires sign-in");
                const int limit = 12 * 1024 * 1024;
                if (response.Content.Headers.ContentLength > limit) throw new SourceFailure(FailureKind.Format, "Oversized source response");
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var memory = new MemoryStream();
                var buffer = new byte[32 * 1024];
                int bytes;
                while ((bytes = await stream.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    if (memory.Length + bytes > limit) throw new SourceFailure(FailureKind.Format, "Oversized source response");
                    memory.Write(buffer, 0, bytes);
                }
                var body = Encoding.UTF8.GetString(memory.GetBuffer(), 0, (int)memory.Length);
                if (body.Contains("cf-chl-", StringComparison.OrdinalIgnoreCase) || body.Contains("<title>Security Check", StringComparison.OrdinalIgnoreCase) || body.Contains("captcha-container", StringComparison.OrdinalIgnoreCase)) throw new SourceFailure(FailureKind.Access, "Source requires browser verification");
                var result = new Download(body, final, false);
                if (_cache.Count >= 30) _cache.Clear();
                _cache[cacheKey] = (DateTimeOffset.UtcNow, result);
                return result;
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new SourceFailure(FailureKind.Timeout, "Source timed out"); }
            catch (HttpRequestException) { throw new SourceFailure(FailureKind.Network, "Connection failed"); }
        }
        finally { gate.Release(); }
    }
    public void Dispose() { _http.Dispose(); foreach (var gate in _locks.Values) gate.Dispose(); }
}
