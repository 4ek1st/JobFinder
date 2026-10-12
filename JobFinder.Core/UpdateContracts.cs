using System.Text.Json.Serialization;

namespace JobFinder.Core;

public static class UpdateProduct
{
    public const string Owner = "4ek1st", Repository = "JobFinder", PackageName = "JobFinder-portable-win-x64.zip", ManifestName = "update-manifest.json", SignatureName = "update-manifest.sig";
    public const long MaximumPackageBytes = 200L * 1024 * 1024, MaximumExpandedBytes = 400L * 1024 * 1024;
    public const int MaximumEntries = 5000;
    public const string PublicKeyPem = """
-----BEGIN PUBLIC KEY-----
MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAwL5RQeniRF5sK9xewv1J
krMl4AXr2FEJtmDVsEdQpO/Lbm7af2dnUstxb9K6wG1lqtOtMZnk7itZKZCyFotc
b3cnrl9PaolBsQbvniQDgxn6B8nq80lpf/hWCdeB5QBSbuGjs5Y/aUi9/EaSt16o
tGUPZHpLpNBpTGqjnBzpqDCdhDBCN9uao5yUTVb8H2NgO7zlPZyY6wV32ZkJuGWC
9ZjvqWhr0shHApyc6tjApPpgjiNhf+KrJbJYIXj0N9cDIiHj9fEjbYSikSWuzCFy
anwNYFB9LmXFdL8e381kpJbWtqoAbRJXaKlMmDQQkkkDAbAH1CNyFCaHMs/NxR6M
PutzlftzQvFP4537tuIaxkaNDX1QngIiYy0Vgmz2z4foAmnyr5r9tRASOHNMrQJ5
/huU+nYXxAZCReExgXxTGAjBPfem9LTICyItJwcYn5zMldyyDxpiZHK8LUKJTRD7
4R/m1zEeJ91ZHNYnL/oyhnlOvE0wZb/KFPtuSABgbvCdAgMBAAE=
-----END PUBLIC KEY-----
""";
}

public sealed record UpdateManifest
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; } = 1;
    [JsonPropertyName("version")] public string Version { get; init; } = "";
    [JsonPropertyName("tag")] public string Tag { get; init; } = "";
    [JsonPropertyName("packageFile")] public string PackageFile { get; init; } = "";
    [JsonPropertyName("packageSize")] public long PackageSize { get; init; }
    [JsonPropertyName("packageSha256")] public string PackageSha256 { get; init; } = "";
    [JsonPropertyName("executableSha256")] public string ExecutableSha256 { get; init; } = "";
    [JsonPropertyName("minimumUpdaterVersion")] public string MinimumUpdaterVersion { get; init; } = "";
}

public sealed record AvailableUpdate(UpdateManifest Manifest, Version Version, string PackageUrl, byte[] ManifestBytes, byte[] SignatureBytes, string ReleaseUrl);
public sealed record UpdateTransferProgress(long BytesReceived, long TotalBytes)
{
    public double Percent => TotalBytes > 0 ? Math.Clamp(100d * BytesReceived / TotalBytes, 0, 100) : 0;
}
public enum UpdateFailureKind { Network, Timeout, Access, RateLimit, Format, Verification, Unavailable, Install, Startup, Save }
public sealed class UpdateFailure(UpdateFailureKind kind, string message, Exception? inner = null, DateTimeOffset? retryAt = null) : Exception(message, inner)
{
    public UpdateFailureKind Kind { get; } = kind;
    public DateTimeOffset? RetryAt { get; } = retryAt;
}
public interface IUpdateService
{
    Task<AvailableUpdate?> CheckAsync(Version currentVersion, CancellationToken cancellation = default);
    Task<string> DownloadAsync(AvailableUpdate update, string packagePath, IProgress<UpdateTransferProgress>? progress = null, CancellationToken cancellation = default);
}
