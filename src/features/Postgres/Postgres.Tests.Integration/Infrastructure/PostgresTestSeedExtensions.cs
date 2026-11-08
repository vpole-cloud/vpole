using Pole.Features.Postgres.Domain;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.ResourceGroups.Testing;

namespace Pole.Features.Postgres.Tests.Integration.Infrastructure;

public static class PostgresTestSeedExtensions
{
    public static async Task<ResourceGroup> InsertResourceGroupAsync(this PostgresTestStore store, ResourceGroup group)
    {
        await new ResourceGroupTestData(store.Database).SeedAsync(group);
        return group;
    }

    public static Task<long> CountOutboxMessagesAsync(this PostgresTestStore store, string eventType) =>
        store.OutboxMessages.CountDocumentsAsync(message => message.EventType == eventType);
}
