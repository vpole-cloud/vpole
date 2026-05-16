using System.Text.Json;
using MongoDB.Driver;
using Pole.Observability;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Observability;

public sealed class OutboxObservabilityTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    private const string TraceId = "0123456789abcdef0123456789abcdef";
    private const string CorrelationId = "correlation-from-test";

    [Fact]
    public async Task StoreEvent_PersistsTraceContext_OnOutboxMessageAndPayload()
    {
        using var scope = TraceContext.Push(new TraceContextSnapshot(TraceId, CorrelationId));

        var result = await Mediator.Send(TestData.CreateResourceGroupRequest());

        Assert.True(result.TryGetValue(out _, out var error));
        Assert.Null(error);

        var outboxMessage = await DbContext.OutboxMessages
            .Find(message => message.EventType == "create-resource-group")
            .SingleAsync();

        Assert.Equal(TraceId, outboxMessage.TraceId);
        Assert.Equal(CorrelationId, outboxMessage.CorrelationId);

        using var payload = JsonDocument.Parse(outboxMessage.PayloadJson);
        var eventCore = payload.RootElement.GetProperty("eventCore");
        Assert.Equal(TraceId, eventCore.GetProperty("traceId").GetString());
        Assert.Equal(CorrelationId, eventCore.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task StoreEvent_PersistsTraceContext_ForFSharpPostgresEvents()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        using var scope = TraceContext.Push(new TraceContextSnapshot(TraceId, CorrelationId));

        var result = await Mediator.Send(TestData.CreatePostgresClusterRequest(resourceGroup.Id));

        Assert.True(result.TryGetValue(out _, out var error));
        Assert.Null(error);

        var outboxMessage = await DbContext.OutboxMessages
            .Find(message => message.EventType == "create-postgres-cluster-requested")
            .SingleAsync();

        Assert.Equal(TraceId, outboxMessage.TraceId);
        Assert.Equal(CorrelationId, outboxMessage.CorrelationId);

        using var payload = JsonDocument.Parse(outboxMessage.PayloadJson);
        var eventCore = payload.RootElement.GetProperty("eventCore");
        Assert.Equal(TraceId, eventCore.GetProperty("traceId").GetString());
        Assert.Equal(CorrelationId, eventCore.GetProperty("correlationId").GetString());
    }
}
