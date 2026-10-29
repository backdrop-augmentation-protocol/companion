using Bap.Companion.Core;
using Xunit;

namespace Bap.Companion.Tests;

public sealed class HostAuthorizationTests
{
    [Fact]
    public async Task NewRequestRemainsPendingUntilAnExplicitDecision()
    {
        var queue = new HostAuthorizationQueue();
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
        var queue = new HostAuthorizationQueue();
        queue.TryEnqueue(Request("session-a", ["component.discovery", "config.read"]), out var decision);

        Assert.True(queue.Approve("session-a", ["component.discovery", "config.write", "unknown.permission"]));

        var result = await decision;
        Assert.True(result.Approved);
        Assert.Equal(["component.discovery"], result.GrantedPermissions);
    }

    [Fact]
    public async Task RequestsAreIsolatedAndDuplicateSessionDoesNotReplacePendingRequest()
    {
        var queue = new HostAuthorizationQueue();
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
        var queue = new HostAuthorizationQueue();
        queue.TryEnqueue(Request("session-a"), out var decision);

        Assert.True(queue.Cancel("session-a"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await decision);
        Assert.Empty(queue.Snapshot());
        Assert.False(queue.Approve("session-a", ["component.discovery"]));
    }

    private static HostAuthorizationRequest Request(string sessionId, IReadOnlyCollection<string>? permissions = null) => new(
        sessionId,
        new Identity("org.example.host", "Example Host", "1.0.0"),
        ["component-discovery", "config"],
        permissions ?? ["component.discovery"],
        DateTimeOffset.UtcNow);
}
