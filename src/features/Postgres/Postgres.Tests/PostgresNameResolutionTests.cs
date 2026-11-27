// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.FSharp.Core;
using Pole.Features.Postgres.Domain;
using Pole.Features.ResourceConnections.Contracts;
using Xunit;

namespace Pole.Features.Postgres.Tests;

public sealed class PostgresNameResolutionTests
{
    [Fact]
    public void Resolves_one_active_tenant_resource_by_id()
    {
        var cluster = Create("postgres-1");

        var result = ResourceConnectionNameResolution.ResolveUniqueResourceId(
            [cluster],
            candidate => candidate.Lifecycle is not DeletingPostgresClusterLifecycle,
            candidate => candidate.Id);

        Assert.Equal("postgres-1", result);
    }

    [Fact]
    public void Duplicate_tenant_names_fail_closed_instead_of_selecting_the_first_cluster()
    {
        var matches = new[] { Create("postgres-1"), Create("postgres-2") };

        var result = ResourceConnectionNameResolution.ResolveUniqueResourceId(
            matches,
            candidate => candidate.Lifecycle is not DeletingPostgresClusterLifecycle,
            candidate => candidate.Id);

        Assert.Null(result);
    }

    [Fact]
    public void Deleting_cluster_name_is_not_resolved()
    {
        var cluster = Create("postgres-1").BeginDeletion(DateTime.UtcNow);

        var result = ResourceConnectionNameResolution.ResolveUniqueResourceId(
            [cluster],
            candidate => candidate.Lifecycle is not DeletingPostgresClusterLifecycle,
            candidate => candidate.Id);

        Assert.Null(result);
    }

    private static PostgresCluster Create(string id) => PostgresCluster.Create(
        "tenant-1",
        "orders",
        "group-1",
        17,
        "storage-profile-1",
        new CreatePostgresClusterOptions(FSharpOption<string>.Some(id), 1, 10));
}
