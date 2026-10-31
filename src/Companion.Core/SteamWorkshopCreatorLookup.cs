using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Bap.Companion.Core;

/// <summary>Reads a public Workshop item's creator name without requiring a Steam API key.</summary>
public sealed class SteamWorkshopCreatorLookup(HttpClient? httpClient = null)
{
    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly HttpClient client = httpClient ?? SharedClient;
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> pendingOrCached = new(StringComparer.Ordinal);

    public async Task<string?> GetCreatorNameAsync(string workshopId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workshopId) || workshopId.Any(character => !char.IsAsciiDigit(character))) return null;
        var lazyTask = pendingOrCached.GetOrAdd(workshopId,
            id => new Lazy<Task<string?>>(() => ResolveAsync(id, cancellationToken), LazyThreadSafetyMode.ExecutionAndPublication));
        try { return await lazyTask.Value.WaitAsync(cancellationToken); }
        catch
        {
            pendingOrCached.TryRemove(new KeyValuePair<string, Lazy<Task<string?>>>(workshopId, lazyTask));
            throw;
        }
    }

    private async Task<string?> ResolveAsync(string workshopId, CancellationToken cancellationToken)
    {
        using var detailsRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["itemcount"] = "1",
                ["publishedfileids[0]"] = workshopId
            })
        };
        using var detailsResponse = await client.SendAsync(detailsRequest, cancellationToken);
        detailsResponse.EnsureSuccessStatusCode();
        await using var detailsStream = await detailsResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var details = await JsonDocument.ParseAsync(detailsStream, cancellationToken: cancellationToken);
        if (!details.RootElement.TryGetProperty("response", out var response) ||
            !response.TryGetProperty("publishedfiledetails", out var files) || files.GetArrayLength() == 0 ||
            !files[0].TryGetProperty("creator", out var creatorValue)) return null;
        var creatorId = creatorValue.ValueKind == JsonValueKind.String ? creatorValue.GetString() : creatorValue.GetRawText();
        if (string.IsNullOrWhiteSpace(creatorId) || creatorId.Any(character => !char.IsAsciiDigit(character))) return null;

        var profileUrl = $"https://steamcommunity.com/profiles/{creatorId}?xml=1";
        using var profileResponse = await client.GetAsync(profileUrl, cancellationToken);
        profileResponse.EnsureSuccessStatusCode();
        await using var profileStream = await profileResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = XmlReader.Create(profileStream, new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var profile = await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken);
        var name = profile.Root?.Element("steamID")?.Value.Trim();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}
