using Pole.Features.StorageProfiles.Primitives;
using Pole.Features.StorageProfiles.Contracts;
using Pole.Features.SeaweedFS.Domain;
using Pole.Workloads;
using System.Collections.Immutable;
using MongoDB.Bson;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.SeaweedFS.Application.Common;
using Pole.Features.SeaweedFS.Application.Endpoints;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.StorageProfiles.Domain;

namespace Pole.Features.SeaweedFS.Tests.Integration.Infrastructure;

public static class SeaweedFSTestData
{
    public const string TenantId = "507f1f77bcf86cd799439011";
    public const string DefaultDeploymentTargetId = "507f1f77bcf86cd799439098";
    public const string DefaultStorageProfileId = "507f1f77bcf86cd799439099";
    public const string DefaultComputeProfileId = "507f1f77bcf86cd799439097";

    public static CreateSeaweedFSServiceRequest CreateSeaweedFSServiceRequest(
        string resourceGroupId,
        string name = "seaweedfs-main",
        string? tenantId = null) => new(
        new CreateSeaweedFSServiceRequestBody(
            name,
            resourceGroupId,
            ValidSeaweedFSSpec(),
            ImmutableHashSet<string>.Empty),
        tenantId ?? TenantId);

    public static SeaweedFSSpecDto ValidSeaweedFSSpec() => new(
        SeaweedFSMode.SingleNode,
        new SeaweedFSStorageDto(5, DefaultStorageProfileId));

    public static StorageProfile SeedStorageProfile(
        string id = DefaultStorageProfileId,
        string storageClassName = "standard",
        bool isEnabled = true,
        bool isDefault = true) => StorageProfileFactory.Create(
        technicalName: "storage-" + ObjectId.GenerateNewId(),
        "Standard",
        "Default test storage",
        StorageMediaType.SSD,
        StoragePerformanceTier.Standard,
        isEnabled,
        isDefault,
        [StorageProfileTargetMapping.Create(DefaultDeploymentTargetId, storageClassName)],
        [ResourceTypeId.Create("pole/seaweedfs")],
        StorageProfileTenantAccess.Create(StorageProfileTenantAccessMode.AllTenants, []),
        StorageProfileSizeLimits.Create(1, 100),
        StorageProfileFactory.DefaultOptions with { Id = id });

    public static ComputeProfile SeedComputeProfile(
        string id = DefaultComputeProfileId,
        long requestedCpuMillis = 250,
        long requestedMemoryMi = 256,
        long limitCpuMillis = 1000,
        long limitMemoryMi = 1024,
        bool isDefault = true) => ComputeProfileFactory.Create(
        "profile-" + id,
        "Default",
        "Default test compute profile",
        ComputeProfileResourceRequirements.Create(
            ComputeProfileResourceQuantity.Create(requestedCpuMillis, requestedMemoryMi),
            ComputeProfileResourceQuantity.Create(limitCpuMillis, limitMemoryMi)),
        ComputeArchitecture.Any,
        [],
        [ResourceTypeId.Create("pole/seaweedfs")],
        AllTenantsComputeProfileAccess.Create(),
        [ComputeProfileTargetMapping.Create(DefaultDeploymentTargetId)],
        isEnabled: true,
        isDefault,
        new ComputeProfileFactory.CreateOptions(id, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

    public static ResourceGroup SeedResourceGroup(string name = "alpha", string tenantId = TenantId) =>
        ResourceGroupFactory.Create(
            tenantId,
            name,
            $"{name} description",
            ResourceGroupFactory.DefaultCreateOptions with
            {
                Tags = ImmutableHashSet.Create("seed"),
            });
}
