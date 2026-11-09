using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Application.Endpoints.Sources;
using Pole.Features.GitOps.Application.Sources;
using Pole.Features.GitOps.Compiler;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Pole.Features.Quotas.Domain;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Application.Endpoints;
using Pole.Features.RedisServices.Application.Services;
using Pole.Features.RedisServices.Domain;
using Pole.Features.RedisServices.Migrations;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.StorageProfiles.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

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
        new("redis.yaml", $$"""
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
            """)
    ];
}
