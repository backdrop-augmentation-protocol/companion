using Bap.Companion.Core;
using Xunit;

namespace Bap.Companion.Tests;

public sealed class HostAuthorizationTests
{
    [Theory]
    [InlineData(null, 300_000)]
    [InlineData("3000", 3_000)]
    [InlineData("999", 300_000)]
    [InlineData("300001", 300_000)]
    [InlineData("invalid", 300_000)]
    public void ApprovalTimeoutIsCompanionOwnedAndBounded(string? configured, int expectedMilliseconds)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), CompanionOptions.ParseAuthorizationTimeout(configured));
    }

    [Fact]
    public async Task NewRequestRemainsPendingUntilAnExplicitDecision()
    {
        var queue = NewQueue();
        Assert.True(queue.TryEnqueue(Request("session-a"), out var decision));

        Assert.False(decision.IsCompleted);
        Assert.Equal("session-a", Assert.Single(queue.Snapshot()).SessionId);

        Assert.True(queue.Deny("session-a"));
        var result = await decision;
        Assert.False(result.Approved);
        Assert.Empty(result.GrantedPermissions);
        Assert.Empty(queue.Snapshot());
    }

    [Fact]
    public async Task ApprovalGrantsOnlySupportedPermissionsThatWereRequested()
    {
        var queue = NewQueue();
        queue.TryEnqueue(Request("session-a", ["component.discovery", "config.read"]), out var decision);

        Assert.True(queue.Approve("session-a", ["component.discovery", "config.write", "unknown.permission"]));

        var result = await decision;
        Assert.True(result.Approved);
        Assert.Equal(["component.discovery"], result.GrantedPermissions);
    }

    [Fact]
    public async Task RequestsAreIsolatedAndDuplicateSessionDoesNotReplacePendingRequest()
    {
        var queue = NewQueue();
        queue.TryEnqueue(Request("session-a", ["config.read"]), out var first);
        Assert.False(queue.TryEnqueue(Request("session-a", ["config.write"]), out var duplicate));
        queue.TryEnqueue(Request("session-b", ["component.discovery"]), out var second);

        Assert.Same(first, duplicate);
        Assert.True(queue.Approve("session-b", ["component.discovery"]));
        Assert.False(first.IsCompleted);
        Assert.Equal(["component.discovery"], (await second).GrantedPermissions);
        Assert.True(queue.Deny("session-a"));
        Assert.False((await first).Approved);
    }

    [Fact]
    public async Task CancelRemovesPendingRequestWithoutGrantingPermissions()
    {
        var queue = NewQueue();
        queue.TryEnqueue(Request("session-a"), out var decision);

        Assert.True(queue.Cancel("session-a"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await decision);
        Assert.Empty(queue.Snapshot());
        Assert.False(queue.Approve("session-a", ["component.discovery"]));
    }

    [Fact]
    public async Task RememberedApprovalDoesNotBypassApprovalForAnotherSession()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "trusted.json");
        var first = new HostAuthorizationQueue(new TrustedHostStore(path));
        first.TryEnqueue(Request("session-a", ["component.discovery"]), out var decision);
        Assert.True(first.Approve("session-a", ["component.discovery"], remember: true));
        Assert.True((await decision).Approved);

        var restarted = new HostAuthorizationQueue(new TrustedHostStore(path));
        var spoofed = Request("session-b", ["component.discovery"]) with
        {
            ClaimedIdentity = new Identity("org.example.host", "Impostor", "9.9.9")
        };
        Assert.True(restarted.TryEnqueue(spoofed, out var remembered));
        Assert.False(remembered.IsCompleted);
        Assert.True(restarted.Approve("session-b", ["component.discovery"]));
        Assert.Equal(["component.discovery"], (await remembered).GrantedPermissions);

        Assert.True(restarted.TryEnqueue(Request("session-c", ["component.discovery", "config.write"]), out var expanded));
        Assert.False(expanded.IsCompleted);
        Assert.True(restarted.Deny("session-c"));
        Assert.False((await expanded).Approved);
    }

    [Fact]
    public void RememberedHostCanBeRevoked()
    {
        var store = new TrustedHostStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "trusted.json"));
        store.Remember(new("org.example.host", "Example", "1.0"), ["component.discovery"]);
        Assert.True(store.Remove("org.example.host"));
        Assert.Empty(store.List());
    }

    [Fact]
    public void RevokingHostInvalidatesItsActiveSessions()
    {
        var sessions = new InMemorySessionStore();
        var revoked = sessions.Create("host", TimeSpan.FromMinutes(5));
        var retained = sessions.Create("host", TimeSpan.FromMinutes(5));
        Assert.True(sessions.BindHost(revoked.Token, "org.example.host"));
        Assert.True(sessions.BindHost(retained.Token, "org.other.host"));
        Assert.True(sessions.SetPermissions(revoked.Token, ["component.discovery"]));
        Assert.True(sessions.SetPermissions(retained.Token, ["component.discovery"]));

        Assert.Equal(1, sessions.RevokeHost("org.example.host"));
        Assert.True(revoked.RevocationToken.IsCancellationRequested);
        Assert.False(retained.RevocationToken.IsCancellationRequested);
        Assert.False(sessions.TryGet(revoked.Token, out _));
        Assert.True(sessions.TryGet(retained.Token, out var remaining));
        Assert.Equal(["component.discovery"], remaining.GrantedPermissions);
    }

    [Fact]
    public void AuthorizationFailureCodesAreAllowedByCoreErrorSchema()
    {
        // Keep this Core 0.1.0 list synchronized with spec/versions/0.1.0/schemas/error.schema.json.
        var coreErrorCodes = new HashSet<string>(StringComparer.Ordinal)
        {
            "INVALID_MESSAGE", "UNSUPPORTED_VERSION", "UNSUPPORTED_PROFILE", "UNAUTHORIZED",
            "CAPABILITY_DENIED", "INVALID_STATE", "NOT_FOUND", "CONFLICT", "TIMEOUT", "INTERNAL_ERROR"
        };

        Assert.Contains(AuthorizationErrorCodes.TimedOut, coreErrorCodes);
        Assert.Contains(AuthorizationErrorCodes.Denied, coreErrorCodes);
    }

    private static HostAuthorizationQueue NewQueue() => new(new TrustedHostStore(
        Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "trusted.json")));

    private static HostAuthorizationRequest Request(string sessionId, IReadOnlyCollection<string>? permissions = null) => new(
        sessionId,
        new Identity("org.example.host", "Example Host", "1.0.0"),
        ["component-discovery", "config"],
        permissions ?? ["component.discovery"],
        DateTimeOffset.UtcNow);
}
