using Pole.Features.ResourceConnections.Contracts;
using Xunit;
using Xunit.Sdk;

namespace Pole.Features.ResourceConnections.Tests;

public sealed class CascadeOutcomeMatchTests
{
    [Fact]
    public void Match_InvokesOnlyTheMatchingCallbackWithItsPayload()
    {
        CascadeConnectionsForResourceDeletionOutcome cascaded = new ResourceConnectionsCascaded(3);
        var count = cascaded.Match(
            success => success.Count,
            _ => throw new XunitException("Conflict callback ran for a cascaded outcome."));
        Assert.Equal(3, count);

        CascadeConnectionsForResourceDeletionOutcome conflicted = new ResourceConnectionCascadeConflict("connection-7");
        var connectionId = conflicted.Match(
            _ => throw new XunitException("Success callback ran for a conflict outcome."),
            conflict => conflict.ConnectionId);
        Assert.Equal("connection-7", connectionId);
    }
}
