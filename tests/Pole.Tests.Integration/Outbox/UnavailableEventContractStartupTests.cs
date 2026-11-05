using MongoDB.Driver;
using Pole.Features.Outbox.Application;
using Pole.Features.Outbox.Domain;
using Pole.Features.Outbox.Application.Delivery;
using MassTransit;
using Pole.Features.Events.Abstractions;
using System.Reflection;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Outbox;

public sealed class UnavailableEventContractStartupTests(MongoReplicaSetFixture fixture) : ApiIntegrationTestBase(fixture)
{
    [Fact]
    public async Task MissingEventContract_FailsStartupWithoutChangingPendingMessages_AndRestoringItsVersionedContractUnblocksStartup()
    {
        var messages = Fixture.Database.GetCollection<OutboxMessage>("OutboxMessages");
        var now = DateTime.UtcNow;
        var pending = OutboxMessage.Create("acme.test/v1/created", "tenant-1", "{}", null, null, now, now, null);
        await messages.InsertOneAsync(pending);
        var missing = new EnsurePendingOutboxEventsRegistered(messages, new OutboxEventAssemblyCatalog([]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => missing.InvokeAsync());
        var retained = await messages.Find(message => message.Id == pending.Id).SingleAsync();
        Assert.IsType<PendingOutboxMessageState>(retained.State);

        var restored = new EnsurePendingOutboxEventsRegistered(messages,
            new OutboxEventAssemblyCatalog([new FixtureAssembly()]));
        await restored.InvokeAsync();

        await messages.ReplaceOneAsync(message => message.Id == pending.Id, pending.MarkPublished(now));
        await missing.InvokeAsync();
        Assert.Equal(1, await messages.CountDocumentsAsync(message => message.Id == pending.Id));
    }
    [EntityName("acme.test/v1/created")]
    private sealed record FixtureEvent : IPoleEvent
    {
        public EventCore EventCore { get; init; } = EventCore.Create();
    }
    private sealed class FixtureAssembly : Assembly
    {
        public override string FullName => "OutboxEventContractFixture";
        public override Type[] GetTypes() => [typeof(FixtureEvent)];
    }
}
