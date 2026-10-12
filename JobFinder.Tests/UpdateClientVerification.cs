using JobFinder.Core;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

public static class UpdateClientVerification
{
    public static async Task Run(string fixtures, Action<bool, string> check)
    {
        using var key = RSA.Create(3072);
        var publicKey = key.ExportSubjectPublicKeyInfoPem();
        var package = Encoding.UTF8.GetBytes("JobFinder verified release package fixture");
        var signed = Signed(key, "0.5.0", package);

        check(UpdateValidation.ParseVersion("0.5.0") == new Version(0, 5, 0, 0), "Updater normalizes stable three-part version before comparison");
        check(UpdateValidation.ParseVersion("0.5.0.1") > UpdateValidation.ParseVersion("0.5.0"), "Updater compares numeric fourth component without string ordering");
        foreach (var version in new[] { "0.5", "v0.5.0", "0.5.0-beta", "-1.5.0", "0.5.0.0.1", " 0.5.0", "0.5.0 ", "0.5.+1", "0.5.2147483648", "０.5.0" })
            check(Rejects(() => UpdateValidation.ParseVersion(version), UpdateFailureKind.Format), "Updater rejects unstable or malformed version " + version);

        var verified = UpdateValidation.VerifyManifest(signed.Bytes, signed.Signature, publicKey);
        check(verified.Version == "0.5.0" && verified.PackageSize == package.Length && verified.MinimumUpdaterVersion == "0.4.1", "Updater verifies raw RSA-PSS signature before returning manifest fields");
        var tampered = signed.Bytes.ToArray(); tampered[Array.IndexOf(tampered, (byte)'5')] = (byte)'6';
        check(Rejects(() => UpdateValidation.VerifyManifest(tampered, signed.Signature, publicKey), UpdateFailureKind.Verification), "Updater rejects changed raw signed manifest bytes");
        check(Rejects(() => UpdateValidation.VerifyManifest(signed.Bytes, new byte[384], publicKey), UpdateFailureKind.Verification), "Updater rejects invalid signature");
        using (var otherKey = RSA.Create(3072))
            check(Rejects(() => UpdateValidation.VerifyManifest(signed.Bytes, signed.Signature, otherKey.ExportSubjectPublicKeyInfoPem()), UpdateFailureKind.Verification), "Updater rejects manifest signed by another author");
        using (var weakKey = RSA.Create(2048))
        {
            var weak = Signed(weakKey, "0.5.0", package);
            check(Rejects(() => UpdateValidation.VerifyManifest(weak.Bytes, weak.Signature, weakKey.ExportSubjectPublicKeyInfoPem()), UpdateFailureKind.Verification), "Updater requires RSA-3072 author key");
        }
        var pkcsSignature = key.SignData(signed.Bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        check(Rejects(() => UpdateValidation.VerifyManifest(signed.Bytes, pkcsSignature, publicKey), UpdateFailureKind.Verification), "Updater requires PSS rather than PKCS1 signature padding");
        check(Rejects(() => UpdateValidation.VerifyManifest(new byte[1024 * 1024 + 1], signed.Signature, publicKey), UpdateFailureKind.Format), "Updater bounds manifest allocation to one MiB");
        check(Rejects(() => UpdateValidation.VerifyManifest(signed.Bytes, new byte[4097], publicKey), UpdateFailureKind.Format), "Updater bounds signature allocation to four KiB");

        foreach (var mutation in new Action<JsonObject>[]
        {
            n => n["schemaVersion"] = 2,
            n => n.Remove("schemaVersion"),
            n => n["version"] = "0.5.0-beta",
            n => n["tag"] = "v0.6.0",
            n => n["packageFile"] = "../JobFinder.exe",
            n => n["packageSize"] = 0,
            n => n["packageSize"] = UpdateProduct.MaximumPackageBytes + 1,
            n => n["packageSha256"] = "bad",
            n => n["executableSha256"] = new string('x', 64),
            n => n["minimumUpdaterVersion"] = "0.4.1-beta",
            n => n["minimumUpdaterVersion"] = "0.6.0",
            n => n["unexpectedField"] = "not schema 1"
        })
        {
            var invalid = Signed(key, "0.5.0", package, mutation);
            check(Rejects(() => UpdateValidation.VerifyManifest(invalid.Bytes, invalid.Signature, publicKey), UpdateFailureKind.Format), "Updater rejects invalid signed schema or manifest field");
        }
        var duplicateBytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(signed.Bytes).Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal));
        check(Rejects(() => UpdateValidation.VerifyManifest(duplicateBytes, key.SignData(duplicateBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss), publicKey), UpdateFailureKind.Format), "Updater rejects duplicate signed JSON fields");
        await CheckFeed(key, publicKey, signed, check);
        await CheckTransport(publicKey, signed, check);
        await CheckDownload(fixtures, publicKey, signed, check);
    }

    private sealed record SignedFixture(byte[] Bytes, byte[] Signature, byte[] Package);
    private static SignedFixture Signed(RSA key, string version, byte[] package, Action<JsonObject>? change = null)
    {
        var node = new JsonObject
        {
            ["schemaVersion"] = 1, ["version"] = version, ["tag"] = "v" + version,
            ["packageFile"] = UpdateProduct.PackageName, ["packageSize"] = package.Length,
            ["packageSha256"] = Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant(),
            ["executableSha256"] = new string('a', 64), ["minimumUpdaterVersion"] = "0.4.1"
        };
        change?.Invoke(node);
        var bytes = Encoding.UTF8.GetBytes(node.ToJsonString());
        return new(bytes, key.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss), package);
    }
    private static bool Rejects(Action action, UpdateFailureKind expected)
    {
        try { action(); return false; } catch (UpdateFailure failure) { return failure.Kind == expected; }
    }

    private const string Latest = "https://api.github.com/repos/4ek1st/JobFinder/releases/latest";
    private static string AssetUrl(string tag, string name) => "https://github.com/4ek1st/JobFinder/releases/download/" + tag + "/" + name;
    private static JsonObject Feed(SignedFixture fixture, string version = "0.5.0")
    {
        JsonObject Asset(string name, byte[] bytes, int id) => new()
        {
            ["id"] = id, ["url"] = "https://api.github.com/repos/4ek1st/JobFinder/releases/assets/" + id,
            ["name"] = name, ["state"] = "uploaded", ["size"] = bytes.LongLength,
            ["digest"] = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            ["browser_download_url"] = AssetUrl("v" + version, name)
        };
        return new JsonObject
        {
            ["id"] = 7, ["url"] = "https://api.github.com/repos/4ek1st/JobFinder/releases/7",
            ["tag_name"] = "v" + version, ["draft"] = false, ["prerelease"] = false,
            ["html_url"] = "https://github.com/4ek1st/JobFinder/releases/tag/v" + version,
            ["assets"] = new JsonArray(Asset(UpdateProduct.ManifestName, fixture.Bytes, 11), Asset(UpdateProduct.SignatureName, fixture.Signature, 12), Asset(UpdateProduct.PackageName, fixture.Package, 13))
        };
    }
    private static HttpResponseMessage Response(byte[] bytes, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new ByteArrayContent(bytes) };
    private static HttpResponseMessage Response(string text, HttpStatusCode status = HttpStatusCode.OK) => Response(Encoding.UTF8.GetBytes(text), status);
    private static HttpResponseMessage Reply(HttpRequestMessage request, SignedFixture fixture, JsonObject? feed = null)
    {
        string url = request.RequestUri!.AbsoluteUri;
        if (url == Latest) return Response((feed ?? Feed(fixture)).ToJsonString());
        if (url == AssetUrl("v0.5.0", UpdateProduct.ManifestName)) return Response(fixture.Bytes);
        if (url == AssetUrl("v0.5.0", UpdateProduct.SignatureName)) return Response(fixture.Signature);
        if (url == AssetUrl("v0.5.0", UpdateProduct.PackageName)) return Response(fixture.Package);
        return Response("Unknown fixture endpoint", HttpStatusCode.NotFound);
    }
    private static async Task<UpdateFailure?> FailureAsync(Func<Task> action)
    {
        try { await action(); return null; } catch (UpdateFailure failure) { return failure; }
    }
    private static async Task<bool> CanceledAsync(Func<Task> action)
    {
        try { await action(); return false; } catch (OperationCanceledException) { return true; }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        private readonly List<string> _requests = [];
        public string[] Requests { get { lock (_requests) return _requests.ToArray(); } }
        public bool AnonymousHeaders = true, CorrectApiHeaders = true;
        public Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : this((r, _) => Task.FromResult(send(r))) { }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            lock (_requests) _requests.Add(request.RequestUri!.AbsoluteUri);
            AnonymousHeaders &= !request.Headers.Contains("Authorization") && !request.Headers.Contains("Cookie") && !request.Headers.Contains("Proxy-Authorization");
            if (request.RequestUri!.AbsoluteUri == Latest)
                CorrectApiHeaders &= request.Method == HttpMethod.Get && request.Headers.UserAgent.ToString().Contains("JobFinder", StringComparison.Ordinal) &&
                    request.Headers.Accept.ToString().Contains("application/vnd.github+json", StringComparison.Ordinal) &&
                    request.Headers.TryGetValues("X-GitHub-Api-Version", out var versions) && versions.Single() == "2026-03-10";
            return send(request, cancellation);
        }
    }
    private static async Task CheckFeed(RSA key, string publicKey, SignedFixture signed, Action<bool, string> check)
    {
        using (var handler = new Handler(r => Reply(r, signed)))
        using (var http = new HttpClient(handler))
        using (var client = new UpdateClient(http, publicKey))
        {
            var available = await client.CheckAsync(new Version(0, 4, 1));
            check(available?.Version == new Version(0, 5, 0, 0) && available.Manifest.Version == "0.5.0" && available.PackageUrl == AssetUrl("v0.5.0", UpdateProduct.PackageName), "Updater discovers only a verified newer stable fixed-tag package");
            check(handler.AnonymousHeaders && handler.CorrectApiHeaders && handler.Requests.Length == 3, "Updater uses public latest-release API headers without credentials");
            var cached = await client.CheckAsync(new Version(0, 4, 1));
            check(cached?.Version == new Version(0, 5, 0, 0) && handler.Requests.Length == 3, "Updater reuses bounded verified-release cache");
            if (available is not null) available.ManifestBytes[0] ^= 1;
            var untouched = await client.CheckAsync(new Version(0, 4, 1));
            check(untouched is not null && untouched.ManifestBytes.SequenceEqual(signed.Bytes), "Updater cached signed evidence cannot be changed through returned arrays");
        }
        foreach (var current in new[] { new Version(0, 5, 0), new Version(0, 5, 0, 0), new Version(0, 6, 0), new Version(0, 5, 0, 1) })
        {
            using var handler = new Handler(r => Reply(r, signed)); using var http = new HttpClient(handler); using var client = new UpdateClient(http, publicKey);
            check(await client.CheckAsync(current) is null, "Updater never offers an equal or older release to " + current);
            check(await client.CheckAsync(current) is null && handler.Requests.Length == 1, "Updater caches current-version outcome without downloading assets");
        }
        foreach (var field in new[] { "draft", "prerelease" })
        {
            var feed = Feed(signed); feed[field] = true;
            using var handler = new Handler(r => Reply(r, signed, feed)); using var http = new HttpClient(handler); using var client = new UpdateClient(http, publicKey);
            check(await client.CheckAsync(new Version(0, 4, 1)) is null && handler.Requests.Length == 1, "Updater hides " + field + " releases without reading signed assets");
        }
        using (var handler = new Handler(_ => Response("No public stable release", HttpStatusCode.NotFound)))
        using (var http = new HttpClient(handler))
        using (var client = new UpdateClient(http, publicKey))
            check(await client.CheckAsync(new Version(0, 4, 1)) is null && await client.CheckAsync(new Version(0, 4, 1)) is null && handler.Requests.Length == 1, "Updater caches a repository with no published stable release");

        foreach (var url in new[]
        {
            "http://github.com/4ek1st/JobFinder/releases/download/v0.5.0/" + UpdateProduct.PackageName,
            "https://name:secret@github.com/4ek1st/JobFinder/releases/download/v0.5.0/" + UpdateProduct.PackageName,
            "https://github.com:444/4ek1st/JobFinder/releases/download/v0.5.0/" + UpdateProduct.PackageName,
            "https://github.com/other/JobFinder/releases/download/v0.5.0/" + UpdateProduct.PackageName,
            AssetUrl("v0.6.0", UpdateProduct.PackageName),
            "https://release-assets.githubusercontent.com/raw.zip",
            AssetUrl("v0.5.0", UpdateProduct.PackageName) + "?token=untrusted",
            AssetUrl("v0.5.0", UpdateProduct.PackageName) + "#fragment",
            "https://github.com/4ek1st/JobFinder/releases/download/v0.5.0/extra/../" + UpdateProduct.PackageName
        })
        {
            var feed = Feed(signed); feed["assets"]![2]!["browser_download_url"] = url;
            using var handler = new Handler(r => Reply(r, signed, feed)); using var http = new HttpClient(handler); using var client = new UpdateClient(http, publicKey);
            check((await FailureAsync(() => client.CheckAsync(new Version(0, 4, 1))))?.Kind == UpdateFailureKind.Verification && handler.Requests.Length == 1, "Updater rejects unbound asset URL before asset HTTP");
        }
        var feedCases = new (string Name, Action<JsonObject> Change, UpdateFailureKind Kind)[]
        {
            ("foreign release page", n => n["html_url"] = "https://github.com/other/JobFinder/releases/tag/v0.5.0", UpdateFailureKind.Verification),
            ("unstable release tag", n => n["tag_name"] = "v0.5.0-rc1", UpdateFailureKind.Format),
            ("missing release status", n => n.Remove("prerelease"), UpdateFailureKind.Format),
            ("duplicate assets", n => ((JsonArray)n["assets"]!).Add(n["assets"]![0]!.DeepClone()), UpdateFailureKind.Format),
            ("missing signature asset", n => ((JsonArray)n["assets"]!).RemoveAt(1), UpdateFailureKind.Format),
            ("oversized manifest", n => n["assets"]![0]!["size"] = 1024 * 1024 + 1, UpdateFailureKind.Format),
            ("oversized signature", n => n["assets"]![1]!["size"] = 4097, UpdateFailureKind.Format),
            ("oversized package", n => n["assets"]![2]!["size"] = UpdateProduct.MaximumPackageBytes + 1, UpdateFailureKind.Format),
            ("wrong package size", n => n["assets"]![2]!["size"] = signed.Package.Length + 1, UpdateFailureKind.Verification),
            ("wrong package digest", n => n["assets"]![2]!["digest"] = "sha256:" + new string('b', 64), UpdateFailureKind.Verification),
            ("wrong manifest digest", n => n["assets"]![0]!["digest"] = "sha256:" + new string('b', 64), UpdateFailureKind.Verification),
            ("wrong signature digest", n => n["assets"]![1]!["digest"] = "sha256:" + new string('b', 64), UpdateFailureKind.Verification),
            ("missing asset digest", n => n["assets"]![2]!.AsObject().Remove("digest"), UpdateFailureKind.Format)
        };
        foreach (var test in feedCases)
        {
            var feed = Feed(signed); test.Change(feed);
            using var handler = new Handler(r => Reply(r, signed, feed)); using var http = new HttpClient(handler); using var client = new UpdateClient(http, publicKey);
            check((await FailureAsync(() => client.CheckAsync(new Version(0, 4, 1))))?.Kind == test.Kind, "Updater rejects " + test.Name);
        }
        var wrongSignature = signed with { Signature = new byte[384] };
        using (var handler = new Handler(r => Reply(r, wrongSignature)))
        using (var http = new HttpClient(handler))
        using (var client = new UpdateClient(http, publicKey))
            check((await FailureAsync(() => client.CheckAsync(new Version(0, 4, 1))))?.Kind == UpdateFailureKind.Verification, "Updater does not trust a matching API digest with invalid author signature");
        var wrongTag = Signed(key, "0.5.1", signed.Package);
        using (var handler = new Handler(r => Reply(r, wrongTag, Feed(wrongTag))))
        using (var http = new HttpClient(handler))
        using (var client = new UpdateClient(http, publicKey))
            check((await FailureAsync(() => client.CheckAsync(new Version(0, 4, 1))))?.Kind == UpdateFailureKind.Verification, "Updater binds signed manifest version to latest release tag");
        using (var handler = new Handler(r => Reply(r, signed)))
        using (var http = new HttpClient(handler))
        using (var client = new UpdateClient(http, publicKey))
            check((await FailureAsync(() => client.CheckAsync(new Version(0, 4, 0))))?.Kind == UpdateFailureKind.Unavailable, "Updater refuses a release requiring a newer updater than the running version");

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var handler = new Handler(async (r, token) => { if (r.RequestUri!.AbsoluteUri == Latest) { started.TrySetResult(); await finish.Task.WaitAsync(token); } return Reply(r, signed); }))
        using (var http = new HttpClient(handler))
        using (var client = new UpdateClient(http, publicKey))
        {
            var first = client.CheckAsync(new Version(0, 4, 1));
            if (!first.IsCompleted) await started.Task;
            var second = client.CheckAsync(new Version(0, 4, 1));
            using var cancellation = new CancellationTokenSource();
            var canceledWait = client.CheckAsync(new Version(0, 4, 1), cancellation.Token); cancellation.Cancel();
            check(await CanceledAsync(() => canceledWait), "Updater honors cancellation while another check is in flight");
            finish.TrySetResult(); var both = await Task.WhenAll(first, second);
            check(both.All(x => x?.Version == new Version(0, 5, 0, 0)) && handler.Requests.Length == 3, "Updater serializes concurrent requests into one verified release check");
        }
        using (var handler = new Handler(async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return Response(""); }))
        using (var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(80) })
        using (var client = new UpdateClient(http, publicKey))
            check((await FailureAsync(() => client.CheckAsync(new Version(0, 4, 1))))?.Kind == UpdateFailureKind.Timeout, "Updater classifies elapsed metadata timeout separately from caller cancellation");
        using (var cancellation = new CancellationTokenSource())
        using (var handler = new Handler(async (_, token) => { cancellation.Cancel(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return Response(""); }))
        using (var http = new HttpClient(handler))
        using (var client = new UpdateClient(http, publicKey))
            check(await CanceledAsync(() => client.CheckAsync(new Version(0, 4, 1), cancellation.Token)), "Updater propagates active metadata cancellation");
    }

    private static HttpResponseMessage Redirect(string target)
    {
        var response = Response("", HttpStatusCode.Found); response.Headers.Location = new Uri(target); return response;
    }
    private static async Task CheckTransport(string publicKey, SignedFixture signed, Action<bool, string> check)
    {
        using (var handler = new Handler(r => Reply(r, signed)))
        using (var http = new HttpClient(handler))
        using (var client = new UpdateClient(http, publicKey))
        {
            await client.CheckAsync(new Version(0, 4, 1)); client.InvalidateCache();
            check((await client.CheckAsync(new Version(0, 4, 1)))?.Version == new Version(0, 5, 0, 0) && handler.Requests.Length == 6, "Manual update check invalidates successful result cache");
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            check(await CanceledAsync(() => client.CheckAsync(new Version(0, 4, 1), canceled.Token)), "Updater honors cancellation before a cached response");
        }
        foreach (var scenario in new[] { "429", "primary403", "secondary403" })
        {
            var reset = DateTimeOffset.UtcNow.AddMinutes(4).ToUnixTimeSeconds();
            using var handler = new Handler(_ =>
            {
                var response = Response(scenario == "secondary403" ? "{\"message\":\"You have exceeded a secondary rate limit\"}" : "{}", scenario == "429" ? HttpStatusCode.TooManyRequests : HttpStatusCode.Forbidden);
                if (scenario == "429") response.Headers.RetryAfter = new(TimeSpan.FromMinutes(2));
                if (scenario == "primary403") { response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0"); response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", reset.ToString()); }
                return response;
            });
            using var http = new HttpClient(handler); using var client = new UpdateClient(http, publicKey);
            var first = await FailureAsync(() => client.CheckAsync(new Version(0, 4, 1))); client.InvalidateCache();
            var second = await FailureAsync(() => client.CheckAsync(new Version(0, 4, 1)));
            check(first?.Kind == UpdateFailureKind.RateLimit && first.RetryAt > DateTimeOffset.UtcNow.AddSeconds(45), "Updater classifies " + scenario + " with useful retry time");
            check(second?.Kind == UpdateFailureKind.RateLimit && second.RetryAt == first?.RetryAt && handler.Requests.Length == 1, "Manual retry preserves " + scenario + " cooldown without another request");
            if (scenario == "primary403") check(first?.RetryAt >= DateTimeOffset.FromUnixTimeSeconds(reset), "Updater honors GitHub primary rate-limit reset timestamp");
        }
        using (var handler = new Handler(_ => Response("Access denied", HttpStatusCode.Forbidden)))
        using (var http = new HttpClient(handler))
        using (var client = new UpdateClient(http, publicKey))
            check((await FailureAsync(() => client.CheckAsync(new Version(0, 4, 1))))?.Kind == UpdateFailureKind.Access && handler.Requests.Length == 1, "Updater distinguishes access denial and never retries or bypasses it");
        using (var handler = new Handler((_, _) => throw new HttpRequestException("Controlled offline network")))
        using (var http = new HttpClient(handler))
        using (var client = new UpdateClient(http, publicKey))
        {
            var first = await FailureAsync(() => client.CheckAsync(new Version(0, 4, 1)));
            var second = await FailureAsync(() => client.CheckAsync(new Version(0, 4, 1)));
            check(first?.Kind == UpdateFailureKind.Network && first.RetryAt is not null && second?.RetryAt == first.RetryAt && handler.Requests.Length == 1, "Updater retains bounded retry metadata after offline failure");
        }
        foreach (var destination in new[]
        {
            "http://release-assets.githubusercontent.com/github-production-release-asset-2e65be/1/file",
            "https://release-assets.githubusercontent.com.evil.example/github-production-release-asset-2e65be/1/file",
            "https://user:secret@release-assets.githubusercontent.com/github-production-release-asset-2e65be/1/file",
            "https://release-assets.githubusercontent.com:444/github-production-release-asset-2e65be/1/file",
            "https://github.com/foreign/JobFinder/releases/download/v0.5.0/" + UpdateProduct.ManifestName,
            "https://localhost/private",
            "https://objects.githubusercontent.com/not-release-content/file"
        })
        {
            using var handler = new Handler(r => r.RequestUri!.AbsoluteUri == AssetUrl("v0.5.0", UpdateProduct.ManifestName) ? Redirect(destination) : Reply(r, signed));
            using var http = new HttpClient(handler); using var client = new UpdateClient(http, publicKey);
            check((await FailureAsync(() => client.CheckAsync(new Version(0, 4, 1))))?.Kind == UpdateFailureKind.Verification && handler.Requests.Length == 2, "Updater rejects unsafe HTTPS/CDN redirect before following it");
        }
        using (var handler = new Handler(_ => Redirect("https://api.github.com/repos/foreign/JobFinder/releases/latest")))
        using (var http = new HttpClient(handler))
        using (var client = new UpdateClient(http, publicKey))
            check((await FailureAsync(() => client.CheckAsync(new Version(0, 4, 1))))?.Kind == UpdateFailureKind.Verification && handler.Requests.Length == 1, "Updater rejects metadata redirect to a foreign repository");
        foreach (var host in new[] { "release-assets.githubusercontent.com", "objects.githubusercontent.com" })
        foreach (var prefix in new[] { "/github-production-release-asset-2e65be/", "/github-production-release-asset/" })
        {
            var cdn = "https://" + host + prefix + "1416029797/file?response-content-type=application%2Foctet-stream";
            using var handler = new Handler(r => r.RequestUri!.AbsoluteUri == AssetUrl("v0.5.0", UpdateProduct.ManifestName) ? Redirect(cdn) : r.RequestUri!.AbsoluteUri == cdn ? Response(signed.Bytes) : Reply(r, signed));
            using var http = new HttpClient(handler); using var client = new UpdateClient(http, publicKey);
            check((await client.CheckAsync(new Version(0, 4, 1)))?.Version == new Version(0, 5, 0, 0) && handler.Requests.Length == 4 && handler.AnonymousHeaders, "Updater follows authenticated-by-signature release redirect to " + host);
        }
        using (var handler = new Handler(r => r.RequestUri!.AbsoluteUri == AssetUrl("v0.5.0", UpdateProduct.ManifestName) ? Redirect(r.RequestUri.AbsoluteUri) : Reply(r, signed)))
        using (var http = new HttpClient(handler))
        using (var client = new UpdateClient(http, publicKey))
            check((await FailureAsync(() => client.CheckAsync(new Version(0, 4, 1))))?.Kind == UpdateFailureKind.Verification && handler.Requests.Length <= 6, "Updater bounds repeated safe-host redirect loops");
        using (var handler = new Handler(_ => Response(new string('x', 1024 * 1024 + 1))))
        using (var http = new HttpClient(handler))
        using (var client = new UpdateClient(http, publicKey))
            check((await FailureAsync(() => client.CheckAsync(new Version(0, 4, 1))))?.Kind == UpdateFailureKind.Format, "Updater bounds release metadata body before JSON parsing");
        using (var handler = new Handler(_ => Transfer(Encoding.UTF8.GetBytes(Feed(signed).ToJsonString()), async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); })))
        using (var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(80) })
        using (var abort = new CancellationTokenSource())
        using (var client = new UpdateClient(http, publicKey))
        {
            var pending = FailureAsync(() => client.CheckAsync(new Version(0, 4, 1), abort.Token));
            var completed = await Task.WhenAny(pending, Task.Delay(500));
            if (completed != pending) { abort.Cancel(); await CanceledAsync(() => pending); }
            check(completed == pending && (await pending)?.Kind == UpdateFailureKind.Timeout, "Updater also bounds metadata response-body stalls to configured HTTP timeout");
        }
        using (var handler = new Handler(r => Reply(r, signed)))
        using (var http = new HttpClient(handler))
        using (var client = new UpdateClient(http, publicKey))
        {
            http.DefaultRequestHeaders.Authorization = new("Bearer", "controlled-test-value");
            check((await FailureAsync(() => client.CheckAsync(new Version(0, 4, 1))))?.Kind == UpdateFailureKind.Verification && handler.Requests.Length == 0, "Updater refuses credential headers added to an injected client after construction");
        }
        using (var handler = new Handler(r =>
        {
            var response = Reply(r, signed);
            if (r.RequestUri!.AbsoluteUri == AssetUrl("v0.5.0", UpdateProduct.ManifestName)) response.Content.Headers.ContentLength = signed.Bytes.Length + 1;
            return response;
        }))
        using (var http = new HttpClient(handler))
        using (var client = new UpdateClient(http, publicKey))
            check((await FailureAsync(() => client.CheckAsync(new Version(0, 4, 1))))?.Kind == UpdateFailureKind.Verification, "Updater binds signed-evidence HTTP content length to the selected asset");
    }

    private sealed class Progress(Action<UpdateTransferProgress>? onReport = null) : IProgress<UpdateTransferProgress>
    {
        public List<UpdateTransferProgress> Values { get; } = [];
        public void Report(UpdateTransferProgress value) { Values.Add(value); onReport?.Invoke(value); }
    }
    private sealed class TransferStream(byte[] bytes, int chunk = 8, Func<int, CancellationToken, ValueTask>? beforeRead = null) : Stream
    {
        private int _position, _reads;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (beforeRead is not null) await beforeRead(_reads++, cancellationToken);
            int count = Math.Min(Math.Min(chunk, buffer.Length), bytes.Length - _position);
            bytes.AsMemory(_position, count).CopyTo(buffer); _position += count; return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private static HttpResponseMessage Transfer(byte[] bytes, Func<int, CancellationToken, ValueTask>? beforeRead = null) => new(HttpStatusCode.OK) { Content = new StreamContent(new TransferStream(bytes, 8, beforeRead)) };
    private static async Task CheckDownload(string fixtures, string publicKey, SignedFixture signed, Action<bool, string> check)
    {
        var available = new AvailableUpdate(UpdateValidation.VerifyManifest(signed.Bytes, signed.Signature, publicKey), new Version(0, 5, 0, 0), AssetUrl("v0.5.0", UpdateProduct.PackageName), signed.Bytes, signed.Signature, "https://github.com/4ek1st/JobFinder/releases/tag/v0.5.0");
        string testBase = Path.Combine(Path.GetFullPath(fixtures), "update-client-tests");
        string testId = Guid.NewGuid().ToString("N");
        string root = Path.Combine(testBase, testId);
        Directory.CreateDirectory(root);
        try
        {
            string PathFor(string scenario) { var path = Path.Combine(root, scenario, UpdateProduct.PackageName); Directory.CreateDirectory(Path.GetDirectoryName(path)!); return path; }
            bool Clean(string path) => !File.Exists(path) && Directory.GetFiles(Path.GetDirectoryName(path)!, "*.partial").Length == 0;
            using (var handler = new Handler(_ => Transfer(signed.Package)))
            using (var http = new HttpClient(handler))
            using (var client = new UpdateClient(http, publicKey))
            {
                var path = PathFor("verified"); bool incompleteHidden = true;
                var progress = new Progress(p => { if (p.BytesReceived > 0) incompleteHidden &= !File.Exists(path) && Directory.GetFiles(Path.GetDirectoryName(path)!, "*.partial").Length == 1; });
                var result = await client.DownloadAsync(available, path, progress);
                check(result == path && File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(signed.Package), "Updater publishes downloaded file only after exact size and SHA-256 verification");
                check(progress.Values.Count >= 3 && progress.Values.First().BytesReceived == 0 && progress.Values.Last().BytesReceived == signed.Package.Length && progress.Values.Last().Percent == 100 && progress.Values.All(x => x.TotalBytes == signed.Package.Length && x.BytesReceived <= x.TotalBytes) && progress.Values.Select(x => x.BytesReceived).SequenceEqual(progress.Values.Select(x => x.BytesReceived).Order()), "Updater reports monotonic transfer progress against signed package size");
                check(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.partial").Length == 0, "Updater successful download leaves no partial file");
                check(incompleteHidden, "Updater keeps unfinished bytes in a uniquely owned partial file until verified");
            }
            var modifications = new (string Name, AvailableUpdate Update)[]
            {
                ("foreign-url", available with { PackageUrl = "https://github.com/foreign/JobFinder/releases/download/v0.5.0/" + UpdateProduct.PackageName }),
                ("latest-url", available with { PackageUrl = "https://github.com/4ek1st/JobFinder/releases/latest/download/" + UpdateProduct.PackageName }),
                ("changed-version", available with { Version = new Version(0, 6, 0, 0) }),
                ("changed-manifest", available with { Manifest = available.Manifest with { PackageSha256 = new string('b', 64) } }),
                ("invalid-signature", available with { SignatureBytes = new byte[384] }),
                ("changed-release-url", available with { ReleaseUrl = "https://github.com/foreign/JobFinder/releases/tag/v0.5.0" })
            };
            foreach (var test in modifications)
            {
                using var handler = new Handler(_ => Transfer(signed.Package)); using var http = new HttpClient(handler); using var client = new UpdateClient(http, publicKey);
                var path = PathFor(test.Name);
                check((await FailureAsync(() => client.DownloadAsync(test.Update, path)))?.Kind == UpdateFailureKind.Verification && handler.Requests.Length == 0 && Clean(path), "Updater revalidates downloaded update identity before any package HTTP: " + test.Name);
            }
            var corrupted = signed.Package.ToArray(); corrupted[0] ^= 1;
            foreach (var scenario in new[] { "digest", "truncated", "oversized", "wrong-header" })
            {
                using var handler = new Handler(_ => scenario switch
                {
                    "digest" => Transfer(corrupted),
                    "truncated" => Transfer(signed.Package[..^1]),
                    "oversized" => Transfer(signed.Package.Concat(new byte[] { 9 }).ToArray()),
                    _ => Response(signed.Package.Concat(new byte[] { 9 }).ToArray())
                });
                using var http = new HttpClient(handler); using var client = new UpdateClient(http, publicKey);
                var path = PathFor(scenario);
                check((await FailureAsync(() => client.DownloadAsync(available, path)))?.Kind == UpdateFailureKind.Verification && Clean(path), "Updater rejects " + scenario + " package and cleans its own partial");
            }
            using (var cancellation = new CancellationTokenSource())
            using (var handler = new Handler(_ => Transfer(signed.Package)))
            using (var http = new HttpClient(handler))
            using (var client = new UpdateClient(http, publicKey))
            {
                var path = PathFor("canceled-transfer"); var neighbor = Path.Combine(Path.GetDirectoryName(path)!, "another-download.partial");
                File.WriteAllText(neighbor, "keep unrelated partial");
                var progress = new Progress(p => { if (p.BytesReceived > 0) cancellation.Cancel(); });
                check(await CanceledAsync(() => client.DownloadAsync(available, path, progress, cancellation.Token)) && !File.Exists(path) && Directory.GetFiles(Path.GetDirectoryName(path)!, "*.partial").SequenceEqual(new[] { neighbor }) && File.ReadAllText(neighbor) == "keep unrelated partial", "Updater cancellation cleans only its own partial and preserves neighboring staging files");
            }
            using (var cancellation = new CancellationTokenSource())
            using (var handler = new Handler(_ => Transfer(signed.Package)))
            using (var http = new HttpClient(handler))
            using (var client = new UpdateClient(http, publicKey))
            {
                cancellation.Cancel(); var path = PathFor("already-canceled");
                check(await CanceledAsync(() => client.DownloadAsync(available, path, cancellation: cancellation.Token)) && handler.Requests.Length == 0 && Clean(path), "Updater honors cancellation before package request or file creation");
            }
            using (var handler = new Handler(_ => Transfer(signed.Package, (read, _) => read > 0 ? ValueTask.FromException(new IOException("Controlled interrupted response stream")) : ValueTask.CompletedTask)))
            using (var http = new HttpClient(handler))
            using (var client = new UpdateClient(http, publicKey))
            {
                var path = PathFor("interrupted-stream");
                check((await FailureAsync(() => client.DownloadAsync(available, path)))?.Kind == UpdateFailureKind.Network && Clean(path), "Updater classifies interrupted stream and removes partial bytes");
            }
            using (var handler = new Handler(_ => Transfer(signed.Package, async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); })))
            using (var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(80) })
            using (var client = new UpdateClient(http, publicKey))
            {
                var path = PathFor("stalled-stream");
                check((await FailureAsync(() => client.DownloadAsync(available, path)))?.Kind == UpdateFailureKind.Timeout && Clean(path), "Updater bounds stalled package reads and cleans timed-out partial");
            }
            using (var handler = new Handler(_ => Transfer(signed.Package)))
            using (var http = new HttpClient(handler))
            using (var client = new UpdateClient(http, publicKey))
            {
                var path = PathFor("existing-file"); File.WriteAllText(path, "do not replace existing package");
                check((await FailureAsync(() => client.DownloadAsync(available, path)))?.Kind == UpdateFailureKind.Install && handler.Requests.Length == 0 && File.ReadAllText(path) == "do not replace existing package", "Updater refuses to overwrite an existing destination file");
            }
            using (var handler = new Handler(_ => Response("Gone release asset", HttpStatusCode.NotFound)))
            using (var http = new HttpClient(handler))
            using (var client = new UpdateClient(http, publicKey))
            {
                var path = PathFor("missing-package");
                check((await FailureAsync(() => client.DownloadAsync(available, path)))?.Kind == UpdateFailureKind.Unavailable && Clean(path), "Updater retains usable failure classification when the fixed-tag package was removed");
            }
            using (var handler = new Handler(_ => Response("Rate limited", HttpStatusCode.TooManyRequests)))
            using (var http = new HttpClient(handler))
            using (var client = new UpdateClient(http, publicKey))
            {
                var first = await FailureAsync(() => client.CheckAsync(new Version(0, 4, 1))); var path = PathFor("download-cooldown");
                var failure = await FailureAsync(() => client.DownloadAsync(available, path));
                check(failure?.Kind == UpdateFailureKind.RateLimit && failure.RetryAt == first?.RetryAt && handler.Requests.Length == 1 && Clean(path), "Updater package retry shares feed rate-limit cooldown");
            }
            foreach (var prefix in new[] { "/github-production-release-asset-2e65be/", "/github-production-release-asset/" })
            {
            var cdn = "https://release-assets.githubusercontent.com" + prefix + "1416029797/package";
            using (var handler = new Handler(r => r.RequestUri!.AbsoluteUri == available.PackageUrl ? Redirect(cdn) : Response(signed.Package)))
            using (var http = new HttpClient(handler))
            using (var client = new UpdateClient(http, publicKey))
            {
                var path = PathFor("cdn-package-" + (prefix.Contains("2e65be") ? "legacy" : "current"));
                check(await client.DownloadAsync(available, path) == path && File.Exists(path) && handler.Requests.SequenceEqual(new[] { available.PackageUrl, cdn }), "Updater downloads verified package through inspected HTTPS GitHub release CDN redirect");
            }
            }
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.GetFullPath(testBase) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(resolved) != testId)
                throw new InvalidOperationException("Test cleanup escaped its owned staging directory");
            Directory.Delete(resolved, recursive: true);
        }
    }
}
