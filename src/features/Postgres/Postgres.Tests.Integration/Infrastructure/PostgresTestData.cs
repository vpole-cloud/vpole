using Microsoft.FSharp.Core;
using Pole.Features.ResourceGroups.Contracts;
using System.Collections.Immutable;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.Postgres.Application.Common;
using Pole.Features.Postgres.Application.Endpoints;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.StorageProfiles.Contracts;
using Pole.Features.StorageProfiles.Domain;

namespace Pole.Features.Postgres.Tests.Integration.Infrastructure;

public static class PostgresTestData
{
    public const string TenantId = "507f1f77bcf86cd799439011";
    public const string DefaultDeploymentTargetId = "507f1f77bcf86cd799439098";
    public const string DefaultStorageProfileId = "507f1f77bcf86cd799439099";
    public const string DefaultComputeProfileId = "507f1f77bcf86cd799439097";

    public static CreatePostgresClusterRequest CreatePostgresClusterRequest(
        string resourceGroupId,
        string name = "postgres-main",
        int version = 17,
        string username = "postgres",
        string password = "postgres-secret",
        string? tenantId = null)
        => new(new CreatePostgresClusterRequestBody(
            name,
            resourceGroupId,
            version,
            FSharpOption<int>.Some(1),
            FSharpOption<int>.Some(10),
            DefaultStorageProfileId,
            FSharpOption<string>.None,
            username,
            password,
            FSharpOption<PlacementRequest>.None),
            tenantId ?? TenantId);

    public static StorageProfile SeedStorageProfile(
        string id = DefaultStorageProfileId,
        string storageClassName = "standard",
        bool isEnabled = true,
        bool isDefault = true) =>
        StorageProfileFactory.Create(
            "Standard",
            "Default test storage",
            StorageMediaType.SSD,
            StoragePerformanceTier.Standard,
            isEnabled,
            isDefault,
            [StorageProfileTargetMapping.Create(DefaultDeploymentTargetId, storageClassName)],
            [ResourceTypeId.Create("pole/postgresql")],
            StorageProfileTenantAccess.Create(StorageProfileTenantAccessMode.AllTenants, []),
            StorageProfileSizeLimits.Create(1, 100),
            StorageProfileFactory.DefaultOptions with { Id = id });

    public static ComputeProfile SeedComputeProfile(
        string id = DefaultComputeProfileId,
        long requestedCpuMillis = 250,
        long requestedMemoryMi = 256,
        long limitCpuMillis = 1000,
        long limitMemoryMi = 1024,
        bool isDefault = true) =>
        ComputeProfileFactory.Create(
            "Default",
            "Default test compute profile",
            ComputeProfileResourceRequirements.Create(
                ComputeProfileResourceQuantity.Create(requestedCpuMillis, requestedMemoryMi),
                ComputeProfileResourceQuantity.Create(limitCpuMillis, limitMemoryMi)),
            ComputeArchitecture.Any,
            [],
            [ResourceTypeId.Create("pole/postgresql")],
            AllTenantsComputeProfileAccess.Create(),
            [ComputeProfileTargetMapping.Create(DefaultDeploymentTargetId)],
            isEnabled: true,
            isDefault,
            new ComputeProfileFactory.CreateOptions(
                id,
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

    public static ResourceGroup SeedResourceGroup(
        string name = "alpha",
        string tenantId = TenantId) =>
        ResourceGroupFactory.Create(
            tenantId,
            name,
            $"{name} description",
            ResourceGroupFactory.DefaultCreateOptions with
            {
                Tags = ImmutableHashSet.Create("seed")
            });
}
