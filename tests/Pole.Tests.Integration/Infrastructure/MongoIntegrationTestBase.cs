// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Testing;
using Xunit;

namespace Pole.Tests.Integration.Infrastructure;

public abstract class MongoIntegrationTestBase : IAsyncLifetime, IIsolatedIntegrationTest
{
    protected MongoIntegrationTestBase(MongoReplicaSetFixture fixture)
    {
        Fixture = fixture.CreateDatabase();
        Timings = new TestTimings(GetType());
        TestScope = IntegrationTestScope.Create(Fixture);
        Data = new IntegrationTestData(TestScope);
    }

    protected TestDatabaseScope Fixture { get; }
    protected TestTimings Timings { get; }
    protected IntegrationTestScope TestScope { get; }
    protected IntegrationTestData Data { get; }
    protected IMongoDatabase Database => Fixture.Database;
    protected MongoTestStore DbContext => Fixture.DbContext;
    public virtual Task InitializeAsync() => Task.CompletedTask;

    public virtual async Task DisposeAsync()
    {
        using (Timings.Measure("database.dispose"))
        {
            await Fixture.DisposeAsync();
        }
    }
}
