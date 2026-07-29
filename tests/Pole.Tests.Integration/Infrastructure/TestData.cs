using System.Collections.Immutable;
using Microsoft.FSharp.Core;
using Pole.ASP.Helpers;
using Pole.Features.DnsRecords.Domain;
using Pole.Features.Postgres.Application.Endpoints;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Application.Endpoints;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.ResourceGroups.Application.Endpoints;
using Pole.Features.ResourceGroups.Application.Services;
using Pole.Features.Shared.Models;
using Pole.Features.StorageProfiles.Contracts;
using Pole.Features.StorageProfiles.Domain;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Endpoints;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Events;

namespace Pole.Tests.Integration.Infrastructure;

public static class TestData
{
    public const string TenantId = "507f1f77bcf86cd799439011";
    public const string DefaultDeploymentTargetId = "507f1f77bcf86cd799439098";
    public const string DefaultStorageProfileId = "507f1f77bcf86cd799439099";
    public const string UserId = "test-user";
    public const string UserEmail = "test-user@example.test";

    private static readonly IKubernetesNamespaceGenerator NamespaceGenerator =
        new KubernetesNamespaceGenerator(new KubernetesOptions { NamespaceNamePrefix = "test" });

    public static CreateResourceGroupRequest CreateResourceGroupRequest(
        string name = "alpha",
        string description = "primary group")
        => new(new CreateResourceGroupRequestBody(
            Name: name,
            Description: description,
            Tags: ImmutableHashSet.Create("home", "lab")),
            TenantId);

    public static CreateWebServiceRequest CreateWebServiceRequest(
        string resourceGroupId,
        string name = "web-api",
        string domainName = "api.example.com",
        string tenantId = TenantId)
        => new(new CreateWebServiceRequestBody(
            Name: name,
            ResourceGroupId: resourceGroupId,
            Spec: ValidWebServiceSpec(),
            DomainName: domainName,
            Tags: ImmutableHashSet<string>.Empty),
            tenantId,
            new ClaimValue<string>(UserId),
            new ClaimValue<string>(UserEmail));

    public static CreateRedisServiceRequest CreateRedisServiceRequest(
        string resourceGroupId,
        string name = "redis-main",
        string? tenantId = null)
        => new(new CreateRedisServiceRequestBody(
            Name: name,
            ResourceGroupId: resourceGroupId,
            Spec: ValidRedisSpec(),
            Tags: ImmutableHashSet<string>.Empty),
            tenantId ?? TenantId);

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
            new Resources(
                Requests: new Resource(1, 256),
                Limits: new Resource(2, 512)),
            DefaultStorageProfileId,
            username,
            password,
            FSharpOption<PlacementRequest>.None),
            tenantId ?? TenantId);

    public static WebServiceSpecDto ValidWebServiceSpec() => new(
        Image: new ImageInfoDto("docker.io", "nginx", "1.27"),
        Replicas: 2,
        Resources: new Resources(
            Requests: new Resource(1, 256),
            Limits: new Resource(2, 512)),
        Config: new WebServiceConfigDto(
            EnvironmentVariables: ImmutableDictionary<string, string>.Empty
                .Add("ASPNETCORE_ENVIRONMENT", "Development"),
            PortMappings:
            [
                new PortMappingDto(80, "8080", "TCP", "http")
            ]),
        Autoscaling: null,
        Probes: new ProbeSpecDto(
            Startup: ValidProbe(),
            Liveness: ValidProbe(),
            Readiness: ValidProbe()));

    public static RedisSpecDto ValidRedisSpec() => new(
        Storage: new StorageDto(5, DefaultStorageProfileId),
        Resources: new Resources(
            Requests: new Resource(1, 128),
            Limits: new Resource(2, 256)));

    public static ResolvedStorageProfile DefaultResolvedStorageProfile(string storageClassName = "standard") =>
        new(
            DefaultStorageProfileId,
            ProfileRevision: 1,
            DefaultDeploymentTargetId,
            "Standard",
            storageClassName,
            StorageMediaType.SSD,
            StoragePerformanceTier.Standard);

    public static StorageProfile SeedStorageProfile(
        string id = DefaultStorageProfileId,
        string storageClassName = "standard",
        bool isEnabled = true,
        bool isDefault = true,
        StorageProfileTenantAccess? tenantAccess = null)
    {
        return StorageProfileFactory.Create(
            displayName: "Standard",
            description: "Default test storage",
            mediaType: StorageMediaType.SSD,
            performanceTier: StoragePerformanceTier.Standard,
            isEnabled: isEnabled,
            isDefault: isDefault,
            targetMappings:
            [
                StorageProfileTargetMapping.Create(
                    DefaultDeploymentTargetId,
                    storageClassName)
            ],
            allowedResourceTypes: ImmutableHashSet.Create(ResourceType.Redis, ResourceType.Postgres),
            tenantAccess: tenantAccess ?? StorageProfileTenantAccess.Create(
                StorageProfileTenantAccessMode.AllTenants,
                []),
            sizeLimits: StorageProfileSizeLimits.Create(1, 100),
            options: StorageProfileFactory.DefaultOptions with
            {
                Id = id
            });
    }

    public static ProbeConfigDto ValidProbe() => new(
        Path: "/health",
        Port: "8080",
        InitialDelaySeconds: 5,
        PeriodSeconds: 10,
        TimeoutSeconds: 5,
        FailureThreshold: 3,
        SuccessThreshold: 1);

    public static ResourceGroup SeedResourceGroup(string name = "alpha", string? kubernetesNamespace = null, string tenantId = TenantId)
    {
        var options = ResourceGroupFactory.DefaultCreateOptions with
        {
            Tags = ImmutableHashSet.Create("seed")
        };

        return ResourceGroupFactory.Create(
            name: name,
            tenantId: tenantId,
            description: $"{name} description",
            kubernetesNamespace: kubernetesNamespace ?? NamespaceGenerator.GenerateNamespaceName(tenantId, options.Id),
            options: options);
    }

    public static WebService SeedWebService(
        string resourceGroupId,
        string tenantId = TenantId,
        string name = "web-api",
        string domain = "api.example.com",
        string? id = null,
        int activeRevision = 1)
    {
        var defaultOptions = WebServiceFactory.DefaultOptions;
        var options = defaultOptions with
        {
            Id = id ?? defaultOptions.Id,
            ActiveRevision = activeRevision
        };

        return WebServiceFactory.Create(
            name,
            tenantId,
            resourceGroupId,
            ValidWebServiceSpec().ToDomain(),
            new DnsRecordShortInfo("dns-1", domain),
            options);
    }

    public static WebServiceSpecSnapshot ValidWebServiceSpecSnapshot()
    {
        var spec = ValidWebServiceSpec();
        return new WebServiceSpecSnapshot(
            new WebServiceImageInfoSnapshot(spec.Image.Server, spec.Image.Name, spec.Image.Tag),
            spec.Replicas,
            spec.Resources,
            new WebServiceConfigSnapshot(
                spec.Config.EnvironmentVariables,
                spec.Config.PortMappings
                    .Select(mapping => new PortMappingSnapshot(
                        mapping.ServicePort,
                        mapping.TargetPort,
                        mapping.Protocol,
                        mapping.Name))
                    .ToImmutableList()),
            spec.Autoscaling is null
                ? null
                : new AutoscalingSpecSnapshot(
                    spec.Autoscaling.MinReplicas,
                    spec.Autoscaling.MaxReplicas,
                    new AutoscalingMetricSnapshot(
                        spec.Autoscaling.Metric.Type,
                        spec.Autoscaling.Metric.TargetAverageUtilization)),
            spec.Probes.ToDomain());
    }

    public static DnsRecord SeedDnsRecord(string groupId, string resourceId, string name = "existing.example.com") =>
        DnsRecordFactory.Create(
            name: name,
            tenantId: TenantId,
            resourceType: ResourceType.WebService,
            resourceId: resourceId,
            resourceName: "existing-web",
            groupId: groupId,
            options: DnsRecordFactory.DefaultCreateOptions);
}
