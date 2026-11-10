using System.Text.Json;
using Pole.Features.Postgres.Tests.Integration.Infrastructure;
using Pole.Observability;
using Xunit;

namespace Pole.Features.Postgres.Tests.Integration;

public sealed class PostgresOutboxTests(
    MongoReplicaSetFixture fixture
) : MediatorIntegrationTestBase(fixture)
{
    private const string TraceId = "0123456789abcdef0123456789abcdef";
    private const string CorrelationId = "postgres-test";

    [Fact]
    public async Task StoreEvent_PersistsTraceContext_ForFSharpPostgresEvents()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(PostgresTestData.SeedResourceGroup());
        using var scope = TraceContext.Push(new TraceContextSnapshot(TraceId, CorrelationId));

        var result = await Mediator.Send(PostgresTestData.CreatePostgresClusterRequest(resourceGroup.Id));

        _ = result.AssertCreated();

        var outboxMessage = await DbContext.OutboxMessages
            .Find(message => message.EventType == "pole.postgres/v1/postgres-cluster-created")
            .SingleAsync();

        Assert.Equal(TraceId, outboxMessage.TraceId);
        Assert.Equal(CorrelationId, outboxMessage.CorrelationId);

        using var payload = JsonDocument.Parse(outboxMessage.PayloadJson);
        var eventCore = payload.RootElement.GetProperty("eventCore");
        Assert.Equal(TraceId, eventCore.GetProperty("traceId").GetString());
        Assert.Equal(CorrelationId, eventCore.GetProperty("correlationId").GetString());
    }
}
