using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bap.Companion.Core;

public sealed record InstalledItem(
    string Key, string SourceKind, string Role, string Title, string ProjectDirectory,
    string? WorkshopId, string? BapId, string? Version, string? PreviewPath,
    ComponentManifest? ComponentManifest, string Status = "valid",
    IReadOnlyList<WallpaperPropertyDefinition>? Properties = null,
    IReadOnlyList<string>? PropertyDiagnostics = null);

public sealed class InstalledCatalog(IEnumerable<string> workshopRoots, IEnumerable<string> localRoots)
{
    private readonly string[] workshopRoots = workshopRoots.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private readonly string[] localRoots = localRoots.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private readonly SemaphoreSlim scanGate = new(1, 1);
    private IReadOnlyList<InstalledItem>? cachedItems;
    private DateTimeOffset lastScan;

    public static InstalledCatalog FromEnvironment() => new(
        WorkshopRoots.FromEnvironment(discoveryMode: Environment.GetEnvironmentVariable("BAP_DISCOVER_STEAM_WORKSHOP") ?? "1"),
        (Environment.GetEnvironmentVariable("BAP_LOCAL_ROOTS") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat(SteamWorkshopLocator.FindLocalProjectRoots(Environment.GetEnvironmentVariable("BAP_STEAM_ROOT"))));

    public async Task<IReadOnlyList<InstalledItem>> DiscoverAsync(CancellationToken cancellationToken = default, bool force = false)
    {
        if (!force && cachedItems is not null && DateTimeOffset.UtcNow - lastScan < TimeSpan.FromSeconds(10)) return cachedItems;
        await scanGate.WaitAsync(cancellationToken);
        try
        {
        if (!force && cachedItems is not null && DateTimeOffset.UtcNow - lastScan < TimeSpan.FromSeconds(10)) return cachedItems;
        var items = new List<InstalledItem>();
        foreach (var (kind, roots) in new[] { ("workshop", workshopRoots), ("local", localRoots) })
            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                var directories = File.Exists(Path.Combine(root, "project.json")) || File.Exists(Path.Combine(root, "manifest.json"))
                    ? new[] { root } : Directory.EnumerateDirectories(root);
                foreach (var directory in directories)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var item = await ReadItemAsync(directory, kind, cancellationToken);
                    if (item is not null) items.Add(item);
                }
            }
        cachedItems = items.DistinctBy(item => item.Key).OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase).ToArray();
        lastScan = DateTimeOffset.UtcNow;
        return cachedItems;
        }
        finally { scanGate.Release(); }
    }

    public async Task<InstalledItem?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        (await DiscoverAsync(cancellationToken)).FirstOrDefault(item => item.Key == key);

    public async Task<string?> MatchHostKeyAsync(string? projectUrl, string bapId, CancellationToken cancellationToken = default)
    {
        return MatchHostKey(projectUrl, bapId, await DiscoverAsync(cancellationToken));
    }

    public static string? MatchHostKey(string? projectUrl, string bapId, IReadOnlyList<InstalledItem> items)
    {
        if (!Uri.TryCreate(projectUrl, UriKind.Absolute, out var uri) || !uri.IsFile) return null;
        string projectPath;
        try { projectPath = Path.GetFullPath(uri.LocalPath); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        return items.FirstOrDefault(item => item.Role == "host" && item.BapId == bapId &&
            IsWithinDirectory(projectPath, item.ProjectDirectory))?.Key;
    }

    private static bool IsWithinDirectory(string path, string directory)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<InstalledItem?> ReadItemAsync(string directory, string kind, CancellationToken ct)
    {
        try
        {
            var projectFile = Path.Combine(directory, "project.json");
            var manifestFile = Path.Combine(directory, "manifest.json");
            JsonElement? project = null;
            if (File.Exists(projectFile))
            {
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(projectFile, ct));
                project = document.RootElement.Clone();
                if (project.Value.ValueKind != JsonValueKind.Object) return null;
                if (!project.Value.TryGetProperty("type", out var type) || !string.Equals(type.GetString(), "web", StringComparison.OrdinalIgnoreCase)) return null;
            }
            ComponentManifest? manifest = null;
            string? bapId = null;
            string? version = null;
            var roleName = "host";
            var status = "unverified";
            if (File.Exists(manifestFile))
            {
                try
                {
                    using var document = JsonDocument.Parse(await File.ReadAllTextAsync(manifestFile, ct));
                    var role = document.RootElement.TryGetProperty("role", out var roleValue) && roleValue.ValueKind == JsonValueKind.String ? roleValue.GetString() : null;
                    if (role == "component")
                    {
                        roleName = "component";
                        bapId = document.RootElement.TryGetProperty("id", out var idValue) && idValue.ValueKind == JsonValueKind.String ? idValue.GetString() : null;
                        version = document.RootElement.TryGetProperty("version", out var versionValue) && versionValue.ValueKind == JsonValueKind.String ? versionValue.GetString() : null;
                        try
                        {
                            manifest = JsonSerializer.Deserialize<ComponentManifest>(document.RootElement.GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
                            status = ComponentManifestValidator.Validate(manifest).Count == 0 ? "valid" : "invalid";
                            if (status == "invalid") manifest = null;
                        }
                        catch (JsonException) { status = "invalid"; }
                    }
                    else if (role == "host")
                    {
                        bapId = document.RootElement.TryGetProperty("id", out var idValue) && idValue.ValueKind == JsonValueKind.String ? idValue.GetString() : null;
                        version = document.RootElement.TryGetProperty("version", out var versionValue) && versionValue.ValueKind == JsonValueKind.String ? versionValue.GetString() : null;
                        status = string.IsNullOrWhiteSpace(bapId) ? "invalid" : "valid";
                    }
                    else status = "invalid";
                }
                catch (JsonException) { status = "invalid"; }
            }
            if (project is null && !File.Exists(manifestFile)) return null;
            if (project is null && roleName == "host") return null;
            var title = project is not null && project.Value.TryGetProperty("title", out var titleValue)
                ? titleValue.GetString() : manifest?.Name;
            var preview = project is not null && project.Value.TryGetProperty("preview", out var previewValue)
                ? previewValue.GetString() : null;
            var propertyResult = roleName == "component" && project is not null
                ? WallpaperPropertyReader.Read(project.Value)
                : WallpaperPropertyReadResult.Empty;
            // Existing published demo hosts predate packaged Host Manifests.
            if (roleName == "host" && bapId is null && title == "BAP Host Wallpaper Demo")
            { bapId = "org.bap.examples.host-wallpaper"; status = "legacy"; }
            var workshopId = kind == "workshop" ? Path.GetFileName(directory) : null;
            var key = kind == "workshop" ? $"workshop:{workshopId}" : $"local:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(directory).ToUpperInvariant())))[..16].ToLowerInvariant()}";
            return new(key, kind, roleName, title ?? Path.GetFileName(directory), Path.GetFullPath(directory), workshopId, bapId, version,
                ResolvePreviewPath(directory, preview), manifest, status, propertyResult.Properties, propertyResult.Diagnostics);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return null; }
    }

    public static string? ResolvePreviewPath(string directory, string? preview)
    {
        try
        {
            if (!PackagePathResolver.TryResolveFile(directory, preview, out var path)) return null;
            if (!new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) return null;
            return path;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
