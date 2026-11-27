// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Secrets.Application.Bindings;
using Pole.Features.Secrets.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Secrets;

public sealed partial class ManagedSecretIntegrationTests
{
    [Fact]
    public async Task Asynchronous_consumer_cleanup_retains_owned_bindings_and_removes_only_unowned_bindings()
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var type = ResourceTypeId.Create("pole/web-service");
        var consumer = Id();
        var owned = await ConsumerSecretDeletionTestData.InsertAsync(Fixture.Database, group, type, consumer);
        var manual = await ConsumerSecretDeletionTestData.InsertAsync(Fixture.Database, group, type, Id());
        var secrets = Services.GetRequiredService<IMongoCollection<Secret>>();
        manual = manual with
        {
            Name = "manual-secret",
            NormalizedName = "manual-secret",
            Bindings = manual.Bindings
                .Select(item => item with
                {
                    Consumer = item.Consumer with { ResourceId = consumer },
                })
                .ToImmutableArray(),
        };
        await secrets.ReplaceOneAsync(item => item.Id == manual.Id, manual);
        await OwnBinding(group.TenantId, owned.Bindings.Single());
        var cleanup = Services.GetRequiredService<RemoveConsumerSecretBindings>();
        await cleanup(group.TenantId, group.Id, type.Value, consumer, default);
        await cleanup(group.TenantId, group.Id, type.Value, consumer, default);
        Assert.Equal(
            owned.ToBsonDocument(),
            (await secrets.Find(item => item.Id == owned.Id).SingleAsync()).ToBsonDocument());
        Assert.Empty((await secrets.Find(item => item.Id == manual.Id).SingleAsync()).Bindings);
        Assert.Equal(1, await DbContext.CountOutboxMessagesAsync(Data.TenantId, "pole.secrets/v1/secret-changed"));
    }
}
