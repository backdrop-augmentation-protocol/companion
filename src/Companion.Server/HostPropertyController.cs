using System.Text.Json;
using Bap.Companion.Core;

namespace Bap.Companion.Server;

internal sealed class HostPropertyController(
    InstalledCatalog catalog,
    JsonPresetStore presets,
    WallpaperEngineSwitcher wallpaper,
    ILogger<HostPropertyController> logger)
{
    public async Task<AuthorizationControlResponse> SetAsync(AuthorizationControlRequest request, CancellationToken cancellationToken)
    {
        if (request.MonitorIndex is not (>= 0 and <= 31) || string.IsNullOrWhiteSpace(request.PropertyKey) ||
            request.PropertyKey.Length > 256 || request.PropertyValue is null)
            return new(false, [], "invalid Host property request");

        try
        {
            var currentPath = await wallpaper.GetCurrentAsync(request.MonitorIndex.Value, cancellationToken);
            if (string.IsNullOrWhiteSpace(currentPath)) return new(false, [], "Wallpaper Engine's current wallpaper could not be determined.");
            var items = await catalog.DiscoverAsync(cancellationToken);
            var activeHost = FindHost(items, currentPath);
            if (activeHost is null) return new(false, [], "The active wallpaper is not an installed Web Host project.");
            if (request.HostKey is not null && !string.Equals(request.HostKey, activeHost.Key, StringComparison.Ordinal))
                return new(false, [], "The requested Host is no longer active on the selected display.");

            var definitions = activeHost.Properties ?? [];
            var property = definitions.FirstOrDefault(candidate => candidate.Key == request.PropertyKey);
            if (property is null) return new(false, [], $"Host property was not found: {request.PropertyKey}");
            if (!WallpaperPropertyReader.IsValidValue(property, request.PropertyValue.Value))
                return new(false, [], $"Value does not match the declared Host property '{request.PropertyKey}'.");

            var state = await presets.ReadAsync(cancellationToken);
            var existingConfig = state.HostConfigs?.GetValueOrDefault(activeHost.Key) ?? [];
            var effectiveValues = definitions.ToDictionary(definition => definition.Key,
                definition => existingConfig.TryGetValue(definition.Key, out var configured) && WallpaperPropertyReader.IsValidValue(definition, configured)
                    ? configured : definition.DefaultValue, StringComparer.Ordinal);
            if (property.ConditionSupported && !WallpaperPropertyReader.IsConditionMet(property, effectiveValues))
                return new(false, [], $"Host property is currently hidden by its display condition: {request.PropertyKey}");
            var hostConfig = new Dictionary<string, JsonElement>(existingConfig, StringComparer.Ordinal)
            {
                [request.PropertyKey] = request.PropertyValue.Value.Clone()
            };
            var allHostConfigs = new Dictionary<string, Dictionary<string, JsonElement>>(
                state.HostConfigs ?? new Dictionary<string, Dictionary<string, JsonElement>>(), StringComparer.Ordinal)
            {
                [activeHost.Key] = hostConfig
            };
            var updatedState = state with { HostConfigs = allHostConfigs };
            await presets.WriteAsync(updatedState, cancellationToken);
            await wallpaper.ApplyPropertiesAsync(hostConfig, request.MonitorIndex.Value, cancellationToken);
            var currentState = await presets.ReadAsync(cancellationToken);
            var receipt = new HostPropertyApplyReceipt(activeHost.Key, activeHost.Title, request.PropertyKey,
                request.MonitorIndex.Value, currentState);
            logger.LogInformation("Applied Host property {PropertyKey} for {HostKey} on display {MonitorIndex}",
                request.PropertyKey, activeHost.Key, request.MonitorIndex.Value);
            return new(true, [], HostProperty: receipt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            logger.LogWarning(error, "Host property update failed for {HostKey} property {PropertyKey}", request.HostKey, request.PropertyKey);
            return new(false, [], error.Message);
        }
    }

    private static InstalledItem? FindHost(IReadOnlyList<InstalledItem> items, string currentPath)
    {
        foreach (var item in items.Where(item => item.Role == "host"))
        {
            var projectFile = Path.Combine(item.ProjectDirectory, "project.json");
            if (PathsEqual(currentPath, projectFile)) return item;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(projectFile));
                if (document.RootElement.TryGetProperty("file", out var entry) && entry.ValueKind == JsonValueKind.String &&
                    PathsEqual(currentPath, Path.Combine(item.ProjectDirectory, entry.GetString()!))) return item;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException) { }
        }
        return null;
    }

    private static bool PathsEqual(string left, string right)
    {
        try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}
