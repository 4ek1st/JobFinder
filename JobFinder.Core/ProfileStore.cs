using System.Text.Json;

namespace JobFinder.Core;

public sealed class ProfileStore(string directory)
{
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public string FilePath => Path.Combine(DirectoryPath, "profile.json");
    public string? RecoveryMessage { get; private set; }
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IgnoreReadOnlyProperties = true };
    public UserProfile Load()
    {
        RecoveryMessage = null;
        if (!File.Exists(FilePath)) return new();
        try { return Read(FilePath); }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            // Preserve damaged bytes before attempting recovery; never silently erase favorites.
            var damaged = Path.Combine(DirectoryPath, $"profile.damaged-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.json");
            try { File.Copy(FilePath, damaged, false); } catch (Exception copy) when (copy is IOException or UnauthorizedAccessException) { }
            if (File.Exists(FilePath + ".bak"))
            {
                try { var restored = Read(FilePath + ".bak"); RecoveryMessage = "backup"; return restored; }
                catch (Exception backup) when (backup is JsonException or IOException or UnauthorizedAccessException) { }
            }
            RecoveryMessage = "damaged";
            return new();
        }
    }
    private static UserProfile Read(string file)
    {
        var profile = JsonSerializer.Deserialize<UserProfile>(File.ReadAllText(file), Json) ?? throw new JsonException();
        if (profile.Schema != 1 || profile.Favorites is null || profile.Searches is null || profile.EnabledSources is null || profile.LastQuery is null) throw new JsonException();
        profile.Favorites = profile.Favorites.Where(j => Links.IsSafe(j.Url) && !string.IsNullOrWhiteSpace(j.Title)).DistinctBy(j => j.Key).ToList();
        profile.Language = profile.Language == "de" ? "de" : "ru";
        profile.Theme = profile.Theme == "light" ? "light" : "dark";
        profile.KnownSourceIds ??= [];
        profile.LastQuery = profile.LastQuery.Clean();
        profile.Searches = profile.Searches.Where(s => s is not null && s.Query is not null && s.Name is not null).Select(s => s with { Query = s.Query.Clean() }).ToList();
        return profile;
    }
    public void Save(UserProfile profile)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temporary = FilePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(stream, profile, Json); stream.Flush(true); }
            if (File.Exists(FilePath))
            {
                // A failed load left the original damaged file intact; keep a valid recovered backup.
                if (RecoveryMessage is not null) File.Move(temporary, FilePath, true);
                else File.Replace(temporary, FilePath, FilePath + ".bak", true);
            }
            else File.Move(temporary, FilePath);
            RecoveryMessage = null;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
