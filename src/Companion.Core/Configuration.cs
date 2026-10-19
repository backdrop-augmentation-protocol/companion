using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bap.Companion.Core;

public interface IConfigStore
{
    Task<CompanionConfig> ReadAsync(CancellationToken cancellationToken = default);
    Task WriteGlobalAsync(Dictionary<string, object?> values, CancellationToken cancellationToken = default);
    Task WriteWallpaperAsync(string wallpaperId, Dictionary<string, object?> values, CancellationToken cancellationToken = default);
}

public sealed class JsonConfigStore : IConfigStore
{
    private readonly string path;
    private readonly SemaphoreSlim gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public JsonConfigStore(string path) => this.path = path;

    public async Task<CompanionConfig> ReadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(path)) return new([], []);
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<CompanionConfig>(stream, JsonOptions, cancellationToken) ?? new([], []);
        }
        finally { gate.Release(); }
    }

    public Task WriteGlobalAsync(Dictionary<string, object?> values, CancellationToken cancellationToken = default) =>
        UpdateAsync(config => config with { Global = values }, cancellationToken);

    public Task WriteWallpaperAsync(string wallpaperId, Dictionary<string, object?> values, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(wallpaperId)) throw new ArgumentException("wallpaperId is required", nameof(wallpaperId));
        return UpdateAsync(config =>
        {
            var wallpapers = new Dictionary<string, Dictionary<string, object?>>(config.Wallpapers) { [wallpaperId] = values };
            return config with { Wallpapers = wallpapers };
        }, cancellationToken);
    }

    private async Task UpdateAsync(Func<CompanionConfig, CompanionConfig> update, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = File.Exists(path) ? JsonSerializer.Deserialize<CompanionConfig>(await File.ReadAllTextAsync(path, cancellationToken), JsonOptions) ?? new([], []) : new([], []);
            var next = update(current);
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            var temporary = path + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(next, JsonOptions), cancellationToken);
            File.Move(temporary, path, true);
        }
        finally { gate.Release(); }
    }
}
