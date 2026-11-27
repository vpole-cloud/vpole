// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Tests.Integration.Infrastructure;

public sealed class MongoReplicaSetFixture : Testing.MongoReplicaSetFixture
{
    public TestDatabaseScope SharedDatabase { get; private set; } = null!;
    public MongoTestStore DbContext => SharedDatabase.DbContext;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        SharedDatabase = new TestDatabaseScope(Database);
    }

    public TestDatabaseScope CreateDatabase(IntegrationSchemaProfile schemaProfile = IntegrationSchemaProfile.Full) =>
        new(Client.GetDatabase("pole-integration-" + Guid.NewGuid().ToString("N")), schemaProfile);

    public override Task ResetDatabaseAsync() => throw new NotSupportedException(
        "Use a tenant scope or a dedicated TestDatabaseScope instead of resetting shared storage.");

    public override async Task DisposeAsync()
    {
        try
        {
            if (SharedDatabase is not null)
                await SharedDatabase.DisposeAsync();
        }
        finally
        {
            await base.DisposeAsync();
        }
    }
}
