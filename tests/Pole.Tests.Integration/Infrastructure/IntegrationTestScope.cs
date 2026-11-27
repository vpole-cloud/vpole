// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson;
using Pole.Features.Tenants.Testing;
using Pole.Features.Users.Domain;

namespace Pole.Tests.Integration.Infrastructure;

public sealed record IntegrationTestScope(
    string TenantId,
    string UserId,
    string UserEmail,
    string UniqueSuffix,
    TestDatabaseScope Database
)
{
    public static IntegrationTestScope Create(TestDatabaseScope database)
    {
        var id = ObjectId.GenerateNewId().ToString();
        return new IntegrationTestScope(id, "test-user-" + id, "test-" + id + "@example.test", id, database);
    }

    public string Hostname(string label = "app") => label + "-" + UniqueSuffix + ".example.test";

    public async Task SeedAsync()
    {
        await new TenantTestData(Database.Database).CreateOwnedAsync(UserId, UserEmail, id: TenantId);
        await Database.DbContext.Users.InsertOneAsync(
            PoleUserFactory.Create(UserId, UserEmail, null, null, TenantId, DateTime.UtcNow));
    }
}
