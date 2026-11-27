// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.GitOps.Compiler;
using Pole.Features.RedisServices.Domain;

namespace Pole.Tests.Integration.GitOps;

public abstract partial class DeclarativePipelineTestBase
{
    private protected IMongoCollection<Redis> RedisResources => Fixture.Database.GetCollection<Redis>("RedisServices");

    private protected Task SeedRedisPlanningCatalog() => Fixture.PrepareNamedCatalogAsync();

    private protected static ManifestFile[] RedisSnapshot(
        int size = 4,
        string? scope = "private-cache",
        long storageRevision = 1) =>
    [
        new(
            "redis.yaml",
            $$"""
              apiVersion: pole.io/v1alpha1
              kind: RedisService
              metadata:
                name: cache
              spec:
                resourceGroup: redis-group
                placement:
                  deploymentTarget: lab
                  computeProfile: { name: small, revision: 1 }
                  {{(scope is null ? "" : $"isolationScope: {scope}")}}
                storage:
                  sizeGb: {{size}}
                  profile: { name: redis-storage, revision: {{storageRevision}} }
                tags: [production, cache]
              ---
              apiVersion: pole.io/v1alpha1
              kind: IsolationScope
              metadata:
                name: private-cache
              spec:
                resourceGroup: redis-group
              ---
              apiVersion: pole.io/v1alpha1
              kind: ResourceGroup
              metadata:
                name: redis-group
              spec:
                description: Redis group
                tags: []
              """),
    ];
}
