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
        if (!ComponentIdRegex().IsMatch(manifest.Id)) errors.Add("id must match ^[a-z0-9][a-z0-9._-]{1,63}$");
        if (string.IsNullOrWhiteSpace(manifest.Version)) errors.Add("version is required");
        if (string.IsNullOrWhiteSpace(manifest.Name)) errors.Add("name is required");
        if (string.IsNullOrWhiteSpace(manifest.EntryPoint)) errors.Add("entryPoint is required");
        if (manifest.Modes is null || manifest.Modes.Count == 0 || !manifest.Modes.Any(m => m is "standalone" or "embedded"))
            errors.Add("modes must contain standalone or embedded");
        return errors;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{1,63}$")]
    private static partial Regex ComponentIdRegex();
}
