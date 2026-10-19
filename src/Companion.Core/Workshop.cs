using System.Text.RegularExpressions;

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
