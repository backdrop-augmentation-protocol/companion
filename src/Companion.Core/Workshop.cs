using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Bap.Companion.Core;

// The source is deliberately an adapter: the service does not assume Steam's internal layout or files.
public interface IWorkshopComponentSource
{
    Task<IReadOnlyList<ComponentDescriptor>> DiscoverAsync(CancellationToken cancellationToken = default);
    Task<ComponentDescriptor?> GetAsync(string componentId, CancellationToken cancellationToken = default);
}

public sealed class EmptyWorkshopComponentSource : IWorkshopComponentSource
{
    public Task<IReadOnlyList<ComponentDescriptor>> DiscoverAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ComponentDescriptor>>([]);

    public Task<ComponentDescriptor?> GetAsync(string componentId, CancellationToken cancellationToken = default) =>
        Task.FromResult<ComponentDescriptor?>(null);
}

public sealed class WorkshopDirectorySource : IWorkshopComponentSource
{
    private static readonly string[] ManifestNames = ["manifest.json", "component.manifest.json"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };
    private readonly Func<IReadOnlyList<string>> rootsProvider;

    public WorkshopDirectorySource(IEnumerable<string> roots)
    {
        var fixedRoots = NormalizeRoots(roots);
        rootsProvider = () => fixedRoots;
    }

    public WorkshopDirectorySource(Func<IReadOnlyList<string>> rootsProvider) => this.rootsProvider = rootsProvider;

    private static IReadOnlyList<string> NormalizeRoots(IEnumerable<string> roots) => roots
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Select(Path.GetFullPath)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public async Task<IReadOnlyList<ComponentDescriptor>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var components = new Dictionary<string, ComponentDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in NormalizeRoots(rootsProvider()).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            foreach (var file in EnumerateManifestFiles(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var component = await ReadAsync(file, cancellationToken);
                if (component is not null && !components.ContainsKey(component.Manifest.Id)) components.Add(component.Manifest.Id, component);
            }
        }
        return components.Values.OrderBy(component => component.Manifest.Id, StringComparer.Ordinal).ToArray();
    }

    public async Task<ComponentDescriptor?> GetAsync(string componentId, CancellationToken cancellationToken = default) =>
        (await DiscoverAsync(cancellationToken)).FirstOrDefault(component => string.Equals(component.Manifest.Id, componentId, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> EnumerateManifestFiles(string root)
    {
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories); }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }
        foreach (var file in files)
            if (ManifestNames.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)) yield return file;
    }

    private static async Task<ComponentDescriptor?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            var manifest = await JsonSerializer.DeserializeAsync<ComponentManifest>(stream, JsonOptions, cancellationToken);
            if (ComponentManifestValidator.Validate(manifest).Count != 0 || manifest is null) return null;
            return new ComponentDescriptor(manifest with { Source = path }, Path.GetDirectoryName(path)!);
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}

public static class WorkshopRoots
{
    public static IReadOnlyList<string> FromEnvironment(string? value = null, string? discoveryMode = null, string? steamRoot = null)
    {
        var roots = (value ?? Environment.GetEnvironmentVariable("BAP_WORKSHOP_ROOTS") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        var mode = discoveryMode ?? Environment.GetEnvironmentVariable("BAP_DISCOVER_STEAM_WORKSHOP");
        if (mode == "1" || (mode != "0" && roots.Count == 0))
            roots.AddRange(SteamWorkshopLocator.FindRoots(steamRoot ?? Environment.GetEnvironmentVariable("BAP_STEAM_ROOT")));
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}

public static class SteamWorkshopLocator
{
    public static IReadOnlyList<string> FindRoots(string? steamRoot, string appId = "431960")
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in CandidateSteamRoots(steamRoot))
        {
            AddWorkshopRoot(candidates, root, appId);
            var libraryFile = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraryFile)) continue;
            foreach (var library in ReadLibraryPaths(libraryFile)) AddWorkshopRoot(candidates, library, appId);
        }
        return candidates.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IReadOnlyList<string> FindLocalProjectRoots(string? steamRoot)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var steam in CandidateSteamRoots(steamRoot))
        {
            AddLocalProjectRoot(roots, steam);
            var libraryFile = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraryFile)) continue;
            foreach (var library in ReadLibraryPaths(libraryFile)) AddLocalProjectRoot(roots, library);
        }
        return roots.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void AddLocalProjectRoot(HashSet<string> roots, string library)
    {
        var path = Path.GetFullPath(Path.Combine(library, "steamapps", "common", "wallpaper_engine", "projects", "myprojects"));
        if (Directory.Exists(path)) roots.Add(path);
    }

    private static IEnumerable<string> CandidateSteamRoots(string? configuredRoot)
    {
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            yield return configuredRoot;
            yield break;
        }
        if (OperatingSystem.IsWindows())
        {
            foreach (var root in WindowsRegistrySteamRoots()) yield return root;
        }
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFiles)) yield return Path.Combine(programFiles, "Steam");
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData)) yield return Path.Combine(localAppData, "Steam");
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<string> WindowsRegistrySteamRoots()
    {
        var roots = new List<string>();
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var steamKey = baseKey.OpenSubKey(@"Software\Valve\Steam");
                    foreach (var name in new[] { "SteamPath", "InstallPath" })
                    {
                        if (steamKey?.GetValue(name) is string path && !string.IsNullOrWhiteSpace(path))
                            roots.Add(path);
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (System.Security.SecurityException) { }
            }
        }
        return roots;
    }

    private static IEnumerable<string> ReadLibraryPaths(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(text, "\\\"path\\\"\\s+\\\"([^\\\"]+)\\\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            yield return match.Groups[1].Value.Replace("\\\\", "\\");
    }

    private static void AddWorkshopRoot(HashSet<string> roots, string root, string appId)
    {
        var workshop = Path.GetFullPath(Path.Combine(root, "steamapps", "workshop", "content", appId));
        if (Directory.Exists(workshop)) roots.Add(workshop);
    }
}

public static class WorkshopResourceResolver
{
    public static bool TryResolve(ComponentDescriptor component, string? resourcePath, out string fullPath)
    {
        fullPath = string.Empty;
        return component is not null && PackagePathResolver.TryResolveFile(component.Location, resourcePath, out fullPath);
    }
}

public static partial class ComponentManifestValidator
{
    public static IReadOnlyList<string> Validate(ComponentManifest? manifest)
    {
        var errors = new List<string>();
        if (manifest is null) return ["manifest is required"];
        if (!ComponentIdRegex().IsMatch(manifest.Id)) errors.Add("id must match ^[a-z0-9][a-z0-9._-]{1,127}$");
        if (!SemverRegex().IsMatch(manifest.Version)) errors.Add("version must be SemVer");
        if (string.IsNullOrWhiteSpace(manifest.Name)) errors.Add("name is required");
        if (manifest.Protocol is null || manifest.Protocol.Name != "bap" || !SemverRegex().IsMatch(manifest.Protocol.Version) || string.IsNullOrWhiteSpace(manifest.Protocol.Profile)) errors.Add("protocol must be bap with a valid version and profile");
        if (manifest.Role != "component") errors.Add("role must be component");
        if (manifest.Capabilities is null) errors.Add("capabilities is required");
        if (manifest.Entrypoints is null || !manifest.Entrypoints.ContainsKey("default")) errors.Add("entrypoints.default is required");
        if (manifest.Modes is null || manifest.Modes.Count == 0 || !manifest.Modes.Any(m => m is "standalone" or "embedded"))
            errors.Add("modes must contain standalone or embedded");
        if (manifest.Modes?.Contains("standalone") == true && manifest.Entrypoints is not null && !manifest.Entrypoints.ContainsKey("standalone")) errors.Add("entrypoints.standalone is required");
        if (manifest.Modes?.Contains("embedded") == true && manifest.Entrypoints is not null && !manifest.Entrypoints.ContainsKey("embedded")) errors.Add("entrypoints.embedded is required");
        return errors;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{1,127}$")]
    private static partial Regex ComponentIdRegex();

    [GeneratedRegex("^(0|[1-9]\\d*)\\.(0|[1-9]\\d*)\\.(0|[1-9]\\d*)(?:-[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?$")]
    private static partial Regex SemverRegex();
}
