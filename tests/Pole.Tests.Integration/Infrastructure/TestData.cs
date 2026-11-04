using System.Collections.Immutable;
using Microsoft.FSharp.Core;
using Pole.ASP.Helpers;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.ResourceGroups.Application.Endpoints;
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
    public const string DefaultComputeProfileId = "507f1f77bcf86cd799439097";
    public const string UserId = "test-user";
    public const string UserEmail = "test-user@example.test";

    public static DeploymentTarget SeedDeploymentTarget(string id = DefaultDeploymentTargetId)
    {
        var now = DateTime.UtcNow;
        return DeploymentTargetFactory.Create(
            technicalName: "target-" + id,
            displayName: "Observation target",
            clusterIdentity: "cluster:" + id,
            connection: DeploymentTargetConnectionFactory.CreateInCluster(1),
            health: DeploymentTargetHealth.Healthy(1, now),
            capabilities: DeploymentTargetCapabilities.Observed(
                1,
                now,
                "v1.35.0",
                new DeploymentTargetNamespaceCapabilities(true, true, true, true, true),
                new DeploymentTargetNetworkPolicyCapabilities(true, true, true, true, true)),
            workloadConfiguration: DeploymentTargetWorkloadConfiguration.Create(null,
                DeploymentTargetNetworkSources.None),
            enabled: true,
            acceptsWorkloads: true,
            options: new DeploymentTargetFactory.CreateOptions(id, now));
    }

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
        string tenantId = TenantId)
        => new(new CreateWebServiceRequestBody(
            Name: name,
            ResourceGroupId: resourceGroupId,
            Spec: ValidWebServiceSpec(),
            Placement: new WebServicePlacementRequest(null,
                [new WebServiceDeploymentTargetRequest(DefaultDeploymentTargetId, 2)]),
            Tags: ImmutableHashSet<string>.Empty,
            Exposure: WebServiceExposureMode.Public),
            tenantId,
            new ClaimValue<string>(UserId),
            new ClaimValue<string>(UserEmail));

    public static WebServiceSpecDto ValidWebServiceSpec() => new(
        Image: new ImageInfoDto("docker.io", "nginx", "1.27"),
        Config: new WebServiceConfigDto()
        {
            EnvironmentVariables = ImmutableDictionary<string, string>.Empty.Add("ASPNETCORE_ENVIRONMENT",
                "Development"),
            PortMappings = [new PortMappingDto(80, "8080", "TCP", "http")],
            ConnectionEnvironmentReferences = ImmutableDictionary<string, ConnectionEnvironmentReferenceDto>.Empty,
            SecretEnvironmentReferences = ImmutableDictionary<string, SecretEnvironmentReferenceDto>.Empty
        },
        Autoscaling: null,
        Probes: new ProbeSpecDto(
            Startup: ValidProbe(),
            Liveness: ValidProbe(),
            Readiness: ValidProbe()));

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
            technicalName: "storage-" + id,
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
            allowedResourceTypes:
            [
                ResourceTypeId.Create("third-party/widget"),
                ResourceTypeId.Create("pole/postgresql")
            ],
            tenantAccess: tenantAccess ?? StorageProfileTenantAccess.Create(
                StorageProfileTenantAccessMode.AllTenants,
                []),
            sizeLimits: StorageProfileSizeLimits.Create(1, 100),
            options: StorageProfileFactory.DefaultOptions with
            {
                Id = id
            });
    }

    public static ComputeProfile SeedComputeProfile(
        string id = DefaultComputeProfileId,
        long requestedCpuMillis = 250,
        long requestedMemoryMi = 256,
        long limitCpuMillis = 1000,
        long limitMemoryMi = 1024,
        bool isDefault = true,
        IEnumerable<string>? deploymentTargetIds = null,
        string? technicalName = null) =>
        ComputeProfileFactory.Create(technicalName ?? (id == DefaultComputeProfileId ? "small" : "profile-" + id),
            "Default",
            "Default test compute profile",
            ComputeProfileResourceRequirements.Create(
                ComputeProfileResourceQuantity.Create(
                    requestedCpuMillis,
                    requestedMemoryMi),
                ComputeProfileResourceQuantity.Create(
                    limitCpuMillis,
                    limitMemoryMi)),
            ComputeArchitecture.Any,
            extendedResources: [],
            allowedResourceTypes:
            [
                ResourceTypeId.Create("pole/web-service"),
                ResourceTypeId.Create("third-party/widget"),
                ResourceTypeId.Create("pole/postgresql"),
                ResourceTypeId.Create("pole/function-app")
            ],
            AllTenantsComputeProfileAccess.Create(),
            (deploymentTargetIds ?? [DefaultDeploymentTargetId])
                .Select(targetId => ComputeProfileTargetMapping.Create(targetId)),
            isEnabled: true,
            isDefault,
            new ComputeProfileFactory.CreateOptions(
                id,
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

    public static ProbeConfigDto ValidProbe() => new(
        Path: "/health",
        Port: "8080",
        InitialDelaySeconds: 5,
        PeriodSeconds: 10,
        TimeoutSeconds: 5,
        FailureThreshold: 3,
        SuccessThreshold: 1);

    public static ResourceGroup SeedResourceGroup(string name = "alpha", string tenantId = TenantId)
    {
        var options = ResourceGroupFactory.DefaultCreateOptions with
        {
            Tags = ImmutableHashSet.Create("seed")
        };

        return ResourceGroupFactory.Create(
            name: name,
            tenantId: tenantId,
            description: $"{name} description",
            options: options);
    }

    public static WebService SeedWebService(
        string resourceGroupId,
        string tenantId = TenantId,
        string name = "web-api",
        string? id = null,
        long activeRevision = 1)
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
            ResourceGroupFactory.DefaultCreateOptions.DefaultIsolationScopeId,
            ValidWebServiceSpec().ToDomain(),
            WebServiceExposure.Create(),
            options);
    }

    public static WebServiceSpecSnapshot ValidWebServiceSpecSnapshot()
    {
        var spec = ValidWebServiceSpec();
        return new WebServiceSpecSnapshot(
            new WebServiceImageInfoSnapshot(spec.Image!.Server, spec.Image.Name, spec.Image.Tag),
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
            new WebServiceProbeSpecSnapshot(
                ToProbeSnapshot(spec.Probes.Startup),
                ToProbeSnapshot(spec.Probes.Liveness),
                ToProbeSnapshot(spec.Probes.Readiness)));

        static WebServiceProbeConfigSnapshot ToProbeSnapshot(ProbeConfigDto probe) =>
            new(
                probe.Path,
                probe.Port,
                probe.InitialDelaySeconds,
                probe.PeriodSeconds,
                probe.TimeoutSeconds,
                probe.FailureThreshold,
                probe.SuccessThreshold);
    }

}
