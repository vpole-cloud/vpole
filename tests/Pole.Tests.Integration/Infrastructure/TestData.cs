using System.Collections.Immutable;
using Microsoft.FSharp.Core;
using Pole.Domain.Models;
using Pole.Features.Core.Models;
using Pole.Features.Postgres.Endpoints;
using Pole.Features.RedisServices.Common;
using Pole.Features.RedisServices.Endpoints;
using Pole.Features.ResourceGroups.Endpoints;
using Pole.Features.ResourceGroups.Services;
using Pole.Features.WebServices.Common;
using Pole.Features.WebServices.Endpoints;

namespace Pole.Tests.Integration.Infrastructure;

public static class TestData
{
    private static readonly IKubernetesNamespaceGenerator NamespaceGenerator =
        new KubernetesNamespaceGenerator(new KubernetesOptions { NamespaceNamePrefix = "test" });

    public static CreateResourceGroupRequest CreateResourceGroupRequest(
        string name = "alpha",
        string description = "primary group")
        => new(new CreateResourceGroupRequestBody(
            Name: name,
            Description: description,
            Tags: ImmutableHashSet.Create("home", "lab")));

    public static CreateWebServiceRequest CreateWebServiceRequest(
        string resourceGroupId,
        string name = "web-api",
        string domainName = "api.example.com")
        => new(new CreateWebServiceRequestBody(
            Name: name,
            ResourceGroupId: resourceGroupId,
            Spec: ValidWebServiceSpec(),
            DomainName: domainName,
            Tags: ImmutableHashSet<string>.Empty));

    public static CreateRedisServiceRequest CreateRedisServiceRequest(
        string resourceGroupId,
        string name = "redis-main")
        => new(new CreateRedisServiceRequestBody(
            Name: name,
            ResourceGroupId: resourceGroupId,
            Spec: ValidRedisSpec()));

    public static CreatePostgresClusterRequest CreatePostgresClusterRequest(
        string resourceGroupId,
        string name = "postgres-main",
        string username = "postgres",
        string password = "postgres-secret")
        => new(new CreatePostgresClusterRequestBody(
            name,
            resourceGroupId,
            FSharpOption<int>.Some(1),
            FSharpOption<int>.Some(10),
            username,
            password));

    public static WebServiceSpecDto ValidWebServiceSpec() => new(
        Image: new ImageInfoDto("docker.io", "nginx", "1.27"),
        Replicas: 2,
        Resources: new ResourcesDto(
            Requests: new ResourceDto(1, 256),
            Limits: new ResourceDto(2, 512)),
        Config: new WebServiceConfigDto(
            EnvironmentVariables: new Dictionary<string, string> { ["ASPNETCORE_ENVIRONMENT"] = "Development" },
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
        Storage: new StorageDto(5, "standard"),
        Resources: new ResourcesDto(
            Requests: new ResourceDto(1, 128),
            Limits: new ResourceDto(2, 256)));

    public static ProbeConfigDto ValidProbe() => new(
        Path: "/health",
        Port: "8080",
        InitialDelaySeconds: 5,
        PeriodSeconds: 10,
        TimeoutSeconds: 5,
        FailureThreshold: 3,
        SuccessThreshold: 1);

    public static ResourceGroup SeedResourceGroup(string name = "alpha", string? kubernetesNamespace = null)
    {
        var options = ResourceGroupFactory.DefaultCreateOptions with
        {
            Tags = ImmutableHashSet.Create("seed")
        };

        return ResourceGroupFactory.Create(
            name: name,
            description: $"{name} description",
            kubernetesNamespace: kubernetesNamespace ?? NamespaceGenerator.GenerateNamespaceName(options.Id),
            options: options);
    }

    public static DnsRecord SeedDnsRecord(string groupId, string resourceId, string name = "existing.example.com") =>
        DnsRecordFactory.Create(
            name: name,
            resourceType: ResourceType.WebService,
            resourceId: resourceId,
            resourceName: "existing-web",
            groupId: groupId,
            options: DnsRecordFactory.DefaultCreateOptions);
}
