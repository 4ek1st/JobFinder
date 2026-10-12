using System.Text;
using System.Text.RegularExpressions;

namespace JobFinder.Core;

internal static class UpdatePaths
{
    internal static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JobFinder", "Updates");
    internal static string StagingRoot => Path.Combine(Root, "staging");
    private static readonly Regex Device = new(@"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³]|CLOCK\$|CONIN\$|CONOUT\$)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static string Absolute(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value) || value.StartsWith("\\\\", StringComparison.Ordinal) || value.StartsWith("//", StringComparison.Ordinal))
            throw Invalid("The update path must name a local absolute path.");
        var root = Path.GetPathRoot(value) ?? throw Invalid("Invalid update path.");
        foreach (string part in value[root.Length..].Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)) Component(part);
        string canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        if (canonical.Length > 1800) throw Invalid("Update path exceeds its limit.");
        return canonical;
    }

    internal static void Component(string part)
    {
        if (part.Length == 0 || part.Length > 240 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.Contains('~') || !part.IsNormalized(NormalizationForm.FormC) || part.Any(c => c < 32 || "<>:\"|?*\\/".Contains(c)) || Device.IsMatch(part.Split('.')[0].TrimEnd(' ', '.')))
            throw Invalid("Unsafe Windows filename in update.");
    }

    internal static bool Same(string left, string right) => string.Equals(Absolute(left), Absolute(right), StringComparison.OrdinalIgnoreCase);
    internal static bool Within(string path, string directory) => Absolute(path).StartsWith(Absolute(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal static void NoReparse(string path)
    {
        path = Absolute(path);
        string current = Path.GetPathRoot(path)!;
        foreach (string part in path[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Prepend(""))
        {
            if (part.Length != 0) current = Path.Combine(current, part);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw Invalid("Reparse points cannot participate in an update.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal static string Transaction(string token)
    {
        if (!Guid.TryParseExact(token, "N", out var guid) || guid == Guid.Empty || guid.ToString("N") != token)
            throw Invalid("Invalid update transaction token.");
        string path = Absolute(Path.Combine(StagingRoot, token)); NoReparse(path); return path;
    }

    internal static void Install(string path)
    {
        path = Absolute(path); NoReparse(path);
        if (Same(path, Path.GetPathRoot(path)!) || Within(path, Root) || Same(path, Root) || Within(Root, path) || !Directory.Exists(path))
            throw Invalid("The update does not name a valid program installation.");
    }

    internal static string ZipName(string value)
    {
        if (value.Length > 700 || value.StartsWith('/') || value.StartsWith('\\')) throw Invalid("Unsafe ZIP entry path.");
        var name = value.Replace('\\', '/');
        bool directory = name.EndsWith('/');
        if (directory) name = name[..^1];
        foreach (string part in name.Split('/')) Component(part);
        return name;
    }

    internal static void AllowedPackagePath(string name)
    {
        var parts = name.Split('/'); var root = parts[0];
        bool allowed = root.Equals("JobFinder.exe", StringComparison.OrdinalIgnoreCase) && parts.Length == 1 ||
            (root.Equals("README", StringComparison.OrdinalIgnoreCase) || root.StartsWith("README.", StringComparison.OrdinalIgnoreCase)) && parts.Length == 1 ||
            root.Equals("CHANGELOG.md", StringComparison.OrdinalIgnoreCase) && parts.Length == 1 ||
            root.Equals("THIRD-PARTY-NOTICES", StringComparison.OrdinalIgnoreCase) || root.Equals("verification", StringComparison.OrdinalIgnoreCase);
        if (!allowed) throw Invalid("The package contains an unexpected installation root.");
        foreach (string part in parts)
        {
            var stem = part.Split('.')[0]; var extension = Path.GetExtension(part);
            if (stem.Equals("data", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("profile", StringComparison.OrdinalIgnoreCase) || stem.Equals("plan", StringComparison.OrdinalIgnoreCase) || stem.Equals("update-plan", StringComparison.OrdinalIgnoreCase) || stem.Equals("update-manifest", StringComparison.OrdinalIgnoreCase) || stem.Contains("private-key", StringComparison.OrdinalIgnoreCase) || stem.Contains("signing-key", StringComparison.OrdinalIgnoreCase) || extension.ToLowerInvariant() is ".pem" or ".key" or ".pfx" or ".p12")
                throw Invalid("Release packages cannot contain personal state, update plans, or signing keys.");
        }
    }

    internal static List<string> Files(string directory)
    {
        NoReparse(directory);
        if (!Directory.Exists(directory)) return [];
        var files = new List<string>(); var pending = new Stack<string>(); pending.Push(directory);
        while (pending.TryPop(out string? current))
        {
            NoReparse(current);
            foreach (string entry in Directory.EnumerateFileSystemEntries(current))
            {
                NoReparse(entry);
                if (!Within(entry, directory)) throw Invalid("A snapshot path escaped its root.");
                if (Directory.Exists(entry)) pending.Push(entry); else files.Add(entry);
            }
        }
        return files;
    }

    internal static List<string> Directories(string directory)
    {
        NoReparse(directory); var result = new List<string>();
        if (!Directory.Exists(directory)) return result;
        var pending = new Stack<string>(); pending.Push(directory);
        while (pending.TryPop(out string? current))
        {
            NoReparse(current);
            foreach (string child in Directory.EnumerateDirectories(current))
            {
                NoReparse(child);
                if (!Within(child, directory)) throw Invalid("A snapshot directory escaped its root.");
                result.Add(child); pending.Push(child);
            }
        }
        return result;
    }

    internal static UpdateFailure Invalid(string message) => new(UpdateFailureKind.Format, message);
}
