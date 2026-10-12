using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace JobFinder.Core;

public static class UpdateValidation
{
    internal const int MaximumManifestBytes = 1024 * 1024, MaximumSignatureBytes = 4096;
    private static readonly HashSet<string> ManifestFields = new(StringComparer.Ordinal)
    {
        "schemaVersion", "version", "tag", "packageFile", "packageSize", "packageSha256", "executableSha256", "minimumUpdaterVersion"
    };

    public static Version ParseVersion(string version)
    {
        if (string.IsNullOrEmpty(version)) throw new UpdateFailure(UpdateFailureKind.Format, "Missing stable update version");
        var parts = version.Split('.');
        if (parts.Length is not (3 or 4)) throw new UpdateFailure(UpdateFailureKind.Format, "Expected a stable three- or four-part version");
        var values = new int[4];
        for (int i = 0; i < parts.Length; i++)
            if (parts[i].Length == 0 || parts[i].Any(c => c is < '0' or > '9') || !int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out values[i]))
                throw new UpdateFailure(UpdateFailureKind.Format, "Invalid stable update version");
        return new(values[0], values[1], values[2], values[3]);
    }

    public static UpdateManifest VerifyManifest(byte[] manifestBytes, byte[] signatureBytes, string? publicKeyPem = null)
    {
        if (manifestBytes is null || manifestBytes.Length is 0 or > MaximumManifestBytes || signatureBytes is null || signatureBytes.Length is 0 or > MaximumSignatureBytes)
            throw new UpdateFailure(UpdateFailureKind.Format, "Update manifest or signature exceeds its permitted size");
        try
        {
            using var author = RSA.Create();
            author.ImportFromPem(publicKeyPem ?? UpdateProduct.PublicKeyPem);
            if (author.KeySize != 3072 || signatureBytes.Length != 384 || !author.VerifyData(manifestBytes, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                throw new UpdateFailure(UpdateFailureKind.Verification, "Update manifest signature does not match the author");
        }
        catch (Exception error) when (error is CryptographicException or ArgumentException)
        {
            throw new UpdateFailure(UpdateFailureKind.Verification, "Could not verify update manifest signature", error);
        }
        try
        {
            using var document = JsonDocument.Parse(manifestBytes, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Expected a manifest object");
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in document.RootElement.EnumerateObject())
                if (!ManifestFields.Contains(field.Name) || !fields.Add(field.Name)) throw new JsonException("Unexpected or repeated manifest field");
            if (fields.Count != ManifestFields.Count) throw new JsonException("Incomplete update manifest");
            var manifest = document.RootElement.Deserialize<UpdateManifest>() ?? throw new JsonException("Missing manifest");
            var version = ParseVersion(manifest.Version);
            var minimum = ParseVersion(manifest.MinimumUpdaterVersion);
            if (manifest.SchemaVersion != 1 || manifest.Tag != "v" + manifest.Version || manifest.PackageFile != UpdateProduct.PackageName ||
                manifest.PackageSize <= 0 || manifest.PackageSize > UpdateProduct.MaximumPackageBytes ||
                !IsHash(manifest.PackageSha256) || !IsHash(manifest.ExecutableSha256) || minimum > version)
                throw new JsonException("Invalid update manifest field");
            return manifest;
        }
        catch (JsonException error) { throw new UpdateFailure(UpdateFailureKind.Format, "Invalid signed update manifest", error); }
    }

    internal static bool IsHash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
}

public sealed class UpdateClient : IUpdateService, IDisposable
{
    private const string LatestUrl = "https://api.github.com/repos/4ek1st/JobFinder/releases/latest";
    private const int MaximumRedirects = 4;
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(30);
    private readonly HttpClient _http;
    private readonly string _publicKey;
    private readonly bool _ownsHttp;
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private readonly object _state = new();
    private Version? _cachedFor;
    private AvailableUpdate? _cached;
    private UpdateFailure? _checkFailure, _rateFailure;
    private DateTimeOffset _cacheUntil;
    private int _cacheGeneration, _rateFailures;
    private bool _disposed;

    /// <summary>Injected clients must expose redirects (AllowAutoRedirect=false) and carry no credential headers.</summary>
    public UpdateClient(HttpClient? httpClient = null, string? publicKeyPem = null)
    {
        if (httpClient is not null && HasCredentialHeaders(httpClient))
            throw new ArgumentException("The public update client must not carry credentials", nameof(httpClient));
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, Credentials = null, DefaultProxyCredentials = null,
            ConnectTimeout = TimeSpan.FromSeconds(15), PooledConnectionLifetime = TimeSpan.FromMinutes(15)
        }) { Timeout = Timeout.InfiniteTimeSpan };
        _publicKey = publicKeyPem ?? UpdateProduct.PublicKeyPem;
    }

    public async Task<AvailableUpdate?> CheckAsync(Version currentVersion, CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(currentVersion);
        var current = Normalize(currentVersion);
        cancellation.ThrowIfCancellationRequested();
        await _checkGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            int generation;
            lock (_state)
            {
                ThrowCooldown();
                if (_cachedFor == current && _cacheUntil > DateTimeOffset.UtcNow)
                {
                    if (_checkFailure is not null) throw _checkFailure;
                    return Clone(_cached);
                }
                generation = _cacheGeneration;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(BoundedTimeout(MetadataTimeout));
            try
            {
                var update = await ReadLatestAsync(current, timeout.Token).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                lock (_state)
                    if (generation == _cacheGeneration)
                    {
                        _cachedFor = current; _cached = Clone(update); _checkFailure = null;
                        _cacheUntil = DateTimeOffset.UtcNow.AddMinutes(15);
                    }
                return Clone(update);
            }
            catch (OperationCanceledException error) when (!cancellation.IsCancellationRequested)
            {
                throw RememberFailure(new(UpdateFailureKind.Timeout, "Update check timed out", error), current, generation);
            }
            catch (Exception error) when (error is HttpRequestException or IOException)
            {
                throw RememberFailure(new(UpdateFailureKind.Network, "Could not reach the public update service", error), current, generation);
            }
            catch (UpdateFailure failure) { throw RememberFailure(failure, current, generation); }
        }
        finally { _checkGate.Release(); }
    }

    private async Task<AvailableUpdate?> ReadLatestAsync(Version current, CancellationToken cancellation)
    {
        using var response = await SendAsync(new(LatestUrl), true, cancellation).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, cancellation).ConfigureAwait(false);
        var bytes = await ReadBoundedAsync(response.Content, UpdateValidation.MaximumManifestBytes, cancellation).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            var release = document.RootElement;
            EnsureUniqueProperties(release);
            if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) return null;
            var tag = RequiredString(release, "tag_name");
            if (!tag.StartsWith('v')) throw new UpdateFailure(UpdateFailureKind.Format, "Missing stable release tag");
            var version = UpdateValidation.ParseVersion(tag[1..]);
            var releaseUrl = RequiredString(release, "html_url");
            RequireExactUrl(releaseUrl, ReleaseUrl(tag));
            if (version <= current) return null;
            var assets = release.GetProperty("assets");
            if (assets.ValueKind != JsonValueKind.Array || assets.GetArrayLength() > 64) throw new JsonException("Invalid release assets");
            var found = new Dictionary<string, Asset>(StringComparer.OrdinalIgnoreCase);
            foreach (var asset in assets.EnumerateArray())
            {
                EnsureUniqueProperties(asset);
                var name = RequiredString(asset, "name");
                if (!found.TryAdd(name, new(name, "", 0, ""))) throw new JsonException("Ambiguous release asset names");
                if (name is not (UpdateProduct.ManifestName or UpdateProduct.SignatureName or UpdateProduct.PackageName)) continue;
                if (RequiredString(asset, "state") != "uploaded") throw new JsonException("Unfinished release asset");
                var url = RequiredString(asset, "browser_download_url");
                RequireExactUrl(url, AssetUrl(tag, name));
                long size = asset.GetProperty("size").GetInt64();
                long maximum = name == UpdateProduct.ManifestName ? UpdateValidation.MaximumManifestBytes : name == UpdateProduct.SignatureName ? UpdateValidation.MaximumSignatureBytes : UpdateProduct.MaximumPackageBytes;
                if (size <= 0 || size > maximum) throw new JsonException("Release asset exceeds its permitted size");
                var digest = RequiredString(asset, "digest");
                if (!digest.StartsWith("sha256:", StringComparison.Ordinal) || !UpdateValidation.IsHash(digest[7..])) throw new JsonException("Missing SHA-256 release asset digest");
                found[name] = new(name, url, size, digest[7..]);
            }
            Asset Need(string name) => found.TryGetValue(name, out var value) && value.Name == name && value.Size > 0 ? value : throw new JsonException("Missing expected release asset");
            var manifestAsset = Need(UpdateProduct.ManifestName);
            var signatureAsset = Need(UpdateProduct.SignatureName);
            var packageAsset = Need(UpdateProduct.PackageName);
            var manifestBytes = await ReadAssetAsync(manifestAsset, UpdateValidation.MaximumManifestBytes, cancellation).ConfigureAwait(false);
            var signatureBytes = await ReadAssetAsync(signatureAsset, UpdateValidation.MaximumSignatureBytes, cancellation).ConfigureAwait(false);
            var manifest = UpdateValidation.VerifyManifest(manifestBytes, signatureBytes, _publicKey);
            if (manifest.Tag != tag || UpdateValidation.ParseVersion(manifest.Version) != version || manifest.PackageSize != packageAsset.Size || !SameHash(manifest.PackageSha256, packageAsset.Hash))
                throw new UpdateFailure(UpdateFailureKind.Verification, "Signed package does not match the public release assets");
            if (UpdateValidation.ParseVersion(manifest.MinimumUpdaterVersion) > current)
                throw new UpdateFailure(UpdateFailureKind.Unavailable, "This release requires a newer updater");
            return new(manifest, version, packageAsset.Url, manifestBytes, signatureBytes, releaseUrl);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { throw new UpdateFailure(UpdateFailureKind.Format, "Invalid public release metadata", error); }
    }

    private sealed record Asset(string Name, string Url, long Size, string Hash);
    private async Task<byte[]> ReadAssetAsync(Asset asset, int maximum, CancellationToken cancellation)
    {
        using var response = await SendAsync(new(asset.Url), false, cancellation).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellation).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is { } declared && declared != asset.Size)
            throw new UpdateFailure(UpdateFailureKind.Verification, "Signed-evidence response length differs from its release asset");
        var bytes = await ReadBoundedAsync(response.Content, maximum, cancellation).ConfigureAwait(false);
        if (bytes.LongLength != asset.Size || !SameHash(Convert.ToHexString(SHA256.HashData(bytes)), asset.Hash))
            throw new UpdateFailure(UpdateFailureKind.Verification, "Release metadata does not match downloaded signed evidence");
        return bytes;
    }

    private async Task<HttpResponseMessage> SendAsync(Uri original, bool metadata, CancellationToken cancellation)
    {
        var current = original;
        for (int redirects = 0; ; redirects++)
        {
            lock (_state) ThrowCooldown();
            if (HasCredentialHeaders(_http)) throw new UpdateFailure(UpdateFailureKind.Verification, "The public update client must not carry credentials");
            RequireRedirect(current, original, metadata);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.ParseAdd("JobFinder-Updater/0.4.1");
            request.Headers.Accept.ParseAdd(metadata ? "application/vnd.github+json" : "application/octet-stream");
            if (metadata) request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2026-03-10");
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is { } effective && effective.AbsoluteUri != current.AbsoluteUri)
            {
                response.Dispose();
                throw new UpdateFailure(UpdateFailureKind.Verification, "Update HTTP client followed an uninspected redirect");
            }
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)) return response;
            try
            {
                if (redirects >= MaximumRedirects || response.Headers.Location is not { } location)
                    throw new UpdateFailure(UpdateFailureKind.Verification, "Update redirect limit exceeded or missing destination");
                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                RequireRedirect(next, original, metadata);
                current = next;
            }
            finally { response.Dispose(); }
        }
    }

    private static void RequireRedirect(Uri target, Uri original, bool metadata)
    {
        if (!target.IsAbsoluteUri || target.Scheme != Uri.UriSchemeHttps || !target.IsDefaultPort || target.UserInfo.Length != 0 || target.Fragment.Length != 0)
            throw new UpdateFailure(UpdateFailureKind.Verification, "Unsafe update redirect");
        if (target.AbsoluteUri == original.AbsoluteUri) return;
        if (!metadata && target.Host is ("release-assets.githubusercontent.com" or "objects.githubusercontent.com") &&
            (target.AbsolutePath.StartsWith("/github-production-release-asset-2e65be/", StringComparison.Ordinal) || target.AbsolutePath.StartsWith("/github-production-release-asset/", StringComparison.Ordinal))) return;
        throw new UpdateFailure(UpdateFailureKind.Verification, "Update redirect is outside the selected GitHub release");
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellation)
    {
        if (response.IsSuccessStatusCode) return;
        bool forbidden = response.StatusCode == HttpStatusCode.Forbidden;
        bool rate = response.StatusCode == HttpStatusCode.TooManyRequests;
        if (forbidden)
        {
            rate = response.Headers.RetryAfter is not null || Header(response, "X-RateLimit-Remaining") == "0";
            if (!rate)
            {
                var body = await ReadBoundedAsync(response.Content, 64 * 1024, cancellation).ConfigureAwait(false);
                rate = System.Text.Encoding.UTF8.GetString(body).Contains("rate limit", StringComparison.OrdinalIgnoreCase);
            }
        }
        if (rate)
        {
            lock (_state)
            {
                var now = DateTimeOffset.UtcNow;
                _rateFailures = Math.Min(_rateFailures + 1, 6);
                var retry = now.AddSeconds(60 * (1 << (_rateFailures - 1)));
                if (response.Headers.RetryAfter?.Date is { } date && date > retry) retry = date;
                if (response.Headers.RetryAfter?.Delta is { } delta)
                {
                    var candidate = delta > DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now + delta;
                    if (candidate > retry) retry = candidate;
                }
                if (long.TryParse(Header(response, "X-RateLimit-Reset"), NumberStyles.None, CultureInfo.InvariantCulture, out var unix))
                {
                    try { var reset = DateTimeOffset.FromUnixTimeSeconds(unix); if (reset > retry) retry = reset; } catch (ArgumentOutOfRangeException) { }
                }
                _rateFailure = new(UpdateFailureKind.RateLimit, "Public update service requests a cooldown", retryAt: retry);
                throw _rateFailure;
            }
        }
        if (forbidden || response.StatusCode == HttpStatusCode.Unauthorized) throw new UpdateFailure(UpdateFailureKind.Access, "Access to the public update service was denied");
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) throw new UpdateFailure(UpdateFailureKind.Unavailable, "The selected release asset is unavailable");
        throw new UpdateFailure(UpdateFailureKind.Network, "Public update service returned an unsuccessful response");
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximum, CancellationToken cancellation)
    {
        if (content.Headers.ContentLength > maximum) throw new UpdateFailure(UpdateFailureKind.Format, "Update metadata exceeds its permitted size");
        await using var stream = await content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
        {
            if (output.Length + count > maximum) throw new UpdateFailure(UpdateFailureKind.Format, "Update metadata exceeds its permitted size");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private UpdateFailure RememberFailure(UpdateFailure failure, Version current, int generation)
    {
        var retry = failure.RetryAt ?? DateTimeOffset.UtcNow.AddSeconds(failure.Kind is UpdateFailureKind.Network or UpdateFailureKind.Timeout ? 30 : 60);
        var remembered = failure.RetryAt is null ? new UpdateFailure(failure.Kind, failure.Message, failure.InnerException, retry) : failure;
        lock (_state)
            if (generation == _cacheGeneration)
            {
                _cachedFor = current; _cached = null; _checkFailure = remembered; _cacheUntil = retry;
            }
        return remembered;
    }
    private void ThrowCooldown()
    {
        if (_rateFailure?.RetryAt > DateTimeOffset.UtcNow) throw _rateFailure;
    }
    private static string? Header(HttpResponseMessage response, string name) => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
    private static bool HasCredentialHeaders(HttpClient client) => client.DefaultRequestHeaders.Contains("Authorization") || client.DefaultRequestHeaders.Contains("Cookie") || client.DefaultRequestHeaders.Contains("Proxy-Authorization");
    private static string RequiredString(JsonElement element, string name) => element.GetProperty(name).GetString() is { Length: > 0 } value ? value : throw new JsonException("Missing release metadata field");
    private static void EnsureUniqueProperties(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new JsonException("Expected a metadata object");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in element.EnumerateObject()) if (!names.Add(field.Name)) throw new JsonException("Repeated release metadata field");
    }
    private static void RequireExactUrl(string actual, string expected)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal)) throw new UpdateFailure(UpdateFailureKind.Verification, "Update URL is outside the selected GitHub release");
    }
    private static string AssetUrl(string tag, string name) => "https://github.com/4ek1st/JobFinder/releases/download/" + tag + "/" + name;
    private static string ReleaseUrl(string tag) => "https://github.com/4ek1st/JobFinder/releases/tag/" + tag;
    private static bool SameHash(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    private static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
    private static AvailableUpdate? Clone(AvailableUpdate? update) => update is null ? null : update with { ManifestBytes = update.ManifestBytes.ToArray(), SignatureBytes = update.SignatureBytes.ToArray() };

    public async Task<string> DownloadAsync(AvailableUpdate update, string packagePath, IProgress<UpdateTransferProgress>? progress = null, CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(update);
        cancellation.ThrowIfCancellationRequested();
        if (update.ManifestBytes is null || update.ManifestBytes.Length > UpdateValidation.MaximumManifestBytes || update.SignatureBytes is null || update.SignatureBytes.Length > UpdateValidation.MaximumSignatureBytes)
            throw new UpdateFailure(UpdateFailureKind.Format, "Update evidence exceeds its permitted size");
        var manifestBytes = update.ManifestBytes.ToArray();
        var signatureBytes = update.SignatureBytes.ToArray();
        var manifest = UpdateValidation.VerifyManifest(manifestBytes, signatureBytes, _publicKey);
        if (manifest != update.Manifest || update.Version is null || Normalize(update.Version) != UpdateValidation.ParseVersion(manifest.Version))
            throw new UpdateFailure(UpdateFailureKind.Verification, "Requested package identity differs from its signed manifest");
        RequireExactUrl(update.PackageUrl, AssetUrl(manifest.Tag, manifest.PackageFile));
        RequireExactUrl(update.ReleaseUrl, ReleaseUrl(manifest.Tag));
        lock (_state) ThrowCooldown();

        string destination;
        try
        {
            destination = Path.GetFullPath(packagePath);
            if (File.Exists(destination) || Directory.Exists(destination)) throw new UpdateFailure(UpdateFailureKind.Install, "Update destination already exists");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw new UpdateFailure(UpdateFailureKind.Install, "Could not create the update staging directory", error); }

        string partial = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        bool ownsPartial = false;
        using var transferTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        transferTimeout.CancelAfter(BoundedTimeout(TimeSpan.FromMinutes(10)));
        try
        {
            FileStream output;
            try
            {
                output = new FileStream(partial, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                    BufferSize = 64 * 1024, Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                });
                ownsPartial = true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { throw new UpdateFailure(UpdateFailureKind.Install, "Could not create the temporary update package", error); }
            await using (output.ConfigureAwait(false))
            {
                progress?.Report(new(0, manifest.PackageSize));
                using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(transferTimeout.Token);
                headerTimeout.CancelAfter(BoundedTimeout(MetadataTimeout));
                using var response = await SendAsync(new(update.PackageUrl), false, headerTimeout.Token).ConfigureAwait(false);
                headerTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
                await EnsureSuccessAsync(response, transferTimeout.Token).ConfigureAwait(false);
                if (response.Content.Headers.ContentLength is { } declared && declared != manifest.PackageSize)
                    throw new UpdateFailure(UpdateFailureKind.Verification, "Downloaded package length differs from its signed manifest");
                await using var input = await response.Content.ReadAsStreamAsync(transferTimeout.Token).ConfigureAwait(false);
                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[64 * 1024];
                long received = 0;
                while (true)
                {
                    using var idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(transferTimeout.Token);
                    idleTimeout.CancelAfter(BoundedTimeout(MetadataTimeout));
                    int count;
                    try { count = await input.ReadAsync(buffer, idleTimeout.Token).ConfigureAwait(false); }
                    catch (IOException error) { throw new UpdateFailure(UpdateFailureKind.Network, "Update package stream was interrupted", error, DateTimeOffset.UtcNow.AddSeconds(30)); }
                    if (count == 0) break;
                    if (received + count > manifest.PackageSize || received + count > UpdateProduct.MaximumPackageBytes)
                        throw new UpdateFailure(UpdateFailureKind.Verification, "Downloaded package exceeds its signed size");
                    digest.AppendData(buffer, 0, count);
                    try { await output.WriteAsync(buffer.AsMemory(0, count), transferTimeout.Token).ConfigureAwait(false); }
                    catch (IOException error) { throw new UpdateFailure(UpdateFailureKind.Install, "Could not write the staged update package", error); }
                    received += count;
                    progress?.Report(new(received, manifest.PackageSize));
                }
                if (received != manifest.PackageSize || !SameHash(Convert.ToHexString(digest.GetHashAndReset()), manifest.PackageSha256))
                    throw new UpdateFailure(UpdateFailureKind.Verification, "Downloaded package size or SHA-256 does not match its signed manifest");
                transferTimeout.Token.ThrowIfCancellationRequested();
                try { await output.FlushAsync(transferTimeout.Token).ConfigureAwait(false); }
                catch (IOException error) { throw new UpdateFailure(UpdateFailureKind.Install, "Could not flush the verified update package", error); }
            }
            transferTimeout.Token.ThrowIfCancellationRequested();
            try { File.Move(partial, destination, overwrite: false); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { throw new UpdateFailure(UpdateFailureKind.Install, "Could not publish the verified staged package", error); }
            ownsPartial = false;
            return destination;
        }
        catch (OperationCanceledException error) when (!cancellation.IsCancellationRequested)
        { throw new UpdateFailure(UpdateFailureKind.Timeout, "Update package download timed out", error, DateTimeOffset.UtcNow.AddSeconds(30)); }
        catch (HttpRequestException error)
        { throw new UpdateFailure(UpdateFailureKind.Network, "Could not download the public update package", error, DateTimeOffset.UtcNow.AddSeconds(30)); }
        finally
        {
            if (ownsPartial)
                try { File.Delete(partial); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { throw new UpdateFailure(UpdateFailureKind.Install, "Could not remove this download's interrupted temporary package", error); }
        }
    }
    private TimeSpan BoundedTimeout(TimeSpan requested) => _http.Timeout != Timeout.InfiniteTimeSpan && _http.Timeout < requested ? _http.Timeout : requested;
    public void InvalidateCache()
    {
        lock (_state) { _cacheGeneration++; _cachedFor = null; _cached = null; _checkFailure = null; _cacheUntil = default; }
    }
    public void Dispose() { if (_disposed) return; _disposed = true; if (_ownsHttp) _http.Dispose(); }
}
