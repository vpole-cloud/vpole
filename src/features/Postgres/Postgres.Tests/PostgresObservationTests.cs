using System.Net;
using System.Text;
using System.Text.Json;
using k8s;
using k8s.Models;
using Microsoft.FSharp.Core;
using MongoDB.Bson;
using Pole.Features.Core.Kubernetes;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Postgres.Application.Common;
using Pole.Features.Postgres.Application.Kubernetes;
using Pole.Features.Postgres.Domain;
using Pole.Features.ResourceDeployments.Contracts;
using Xunit;
using Xunit.Sdk;

namespace Pole.Features.Postgres.Tests;

public sealed class PostgresObservationTests
{
    [Theory]
    [InlineData(PostgresDeploymentMode.Operator, true)]
    [InlineData(PostgresDeploymentMode.Operator, false)]
    [InlineData(PostgresDeploymentMode.StatefulSet, true)]
    public async Task Provider_reads_exact_owned_target_and_requires_current_operator_condition(
        PostgresDeploymentMode mode,
        bool currentCondition)
    {
        var cluster = PostgresCluster.Create(
            TestData.TenantId,
            "health-database",
            ObjectId.GenerateNewId().ToString(),
            17,
            mode,
            TestData.DefaultStorageProfileId,
            new CreatePostgresClusterOptions(FSharpOption<string>.Some(ObjectId.GenerateNewId().ToString()), 1, 1));
        var targetId = TestData.DefaultDeploymentTargetId;
        var deploymentId = ObjectId.GenerateNewId().ToString();
        var identity = new WorkloadIdentityLabels(
            cluster.TenantId,
            cluster.GroupId,
            "scope",
            "cell",
            targetId,
            "Postgres",
            cluster.Id,
            deploymentId);
        var controllerMetadata = new V1ObjectMeta
        {
            Name = PostgresClusterKubernetesBuilder.buildResourceName(cluster.Id), NamespaceProperty = "owned",
            Uid = "controller", Generation = 3, Labels = identity.ToDictionary(),
        };
        controllerMetadata.Labels["pole.io/deployment-generation"] = "2";
        var pod = new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = "pg-owned-0",
                NamespaceProperty = "owned",
                Uid = "pod",
                Labels = new Dictionary<string, string>(controllerMetadata.Labels),
                OwnerReferences =
                [
                    new V1OwnerReference
                    {
                        ApiVersion = mode == PostgresDeploymentMode.Operator ? "postgresql.cnpg.io/v1" : "apps/v1",
                        Kind = mode == PostgresDeploymentMode.Operator ? "Cluster" : "StatefulSet",
                        Name = controllerMetadata.Name, Uid = "controller", Controller = true,
                    },
                ],
            },
            Status = new V1PodStatus
            {
                Phase = "Running",
                Conditions = [new V1PodCondition { Type = "Ready", Status = "True" }],
                ContainerStatuses =
                [
                    new V1ContainerStatus
                        { Name = "postgres", Image = "postgres", ImageID = "postgres", Ready = true, RestartCount = 0 },
                ],
            },
        };
        using var handler = new ReadHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/pods", StringComparison.Ordinal))
                return KubernetesJson.Serialize(new V1PodList { Metadata = new V1ListMeta(), Items = [pod] });
            if (request.RequestUri.AbsolutePath.EndsWith("/events", StringComparison.Ordinal))
                return "{\"kind\":\"EventList\",\"apiVersion\":\"v1\",\"metadata\":{},\"items\":[]}";
            if (mode == PostgresDeploymentMode.Operator)
            {
                Assert.Contains(
                    "/postgresql.cnpg.io/v1/namespaces/owned/clusters/pg-",
                    request.RequestUri.AbsolutePath);
                return KubernetesJson.Serialize(
                    new
                    {
                        apiVersion = "postgresql.cnpg.io/v1",
                        kind = "Cluster",
                        metadata = controllerMetadata,
                        spec = new { instances = 1 },
                        status = new
                        {
                            instances = 1, readyInstances = 1,
                            conditions = new[]
                            {
                                new
                                {
                                    type = "Ready", status = "True", observedGeneration = currentCondition ? 3 : 2,
                                    message = "secret-canary-native-message",
                                },
                            },
                        },
                    });
            }

            Assert.Contains("/apps/v1/namespaces/owned/statefulsets/pg-", request.RequestUri.AbsolutePath);
            return KubernetesJson.Serialize(
                new V1StatefulSet
                {
                    Metadata = controllerMetadata,
                    Spec = new V1StatefulSetSpec
                        { Replicas = 1, Template = new V1PodTemplateSpec { Metadata = controllerMetadata } },
                    Status = new V1StatefulSetStatus
                    {
                        ObservedGeneration = 3, Replicas = 1, UpdatedReplicas = 1, ReadyReplicas = 1,
                        AvailableReplicas = 1, CurrentRevision = "current", UpdateRevision = "current",
                    },
                });
        });
        using var client = new Kubernetes(
            new KubernetesClientConfiguration { Host = "http://postgres-observation.test" },
            handler);
        var clients = new ExactClient(client, targetId);
        var snapshot = new ResourceDeploymentReconciliationSnapshot(
            deploymentId,
            cluster.TenantId,
            PostgresResourceDeployment.ResourceType,
            cluster.Id,
            cluster.GroupId,
            "scope",
            "cell",
            1,
            1,
            2,
            DateTime.UtcNow,
            new ResourceDeploymentResolvedPlacementSnapshot(
                targetId,
                1,
                "owned",
                null,
                null,
                new ResourceDeploymentComputeConfiguration(
                    "profile",
                    1,
                    targetId,
                    "Small",
                    new ResourceDeploymentComputeResourceValues(100, 128),
                    new ResourceDeploymentComputeResourceValues(200, 256),
                    ComputeArchitecture.Any,
                    [],
                    [],
                    [],
                    [],
                    [],
                    null)));
        IResourceDeploymentObserver observer = new PostgresResourceDeploymentObserver(
            (
                tenant, id,
                _) =>
            {
                Assert.Equal(cluster.TenantId, tenant);
                Assert.Equal(cluster.Id, id);
                return Task.FromResult(FSharpOption<PostgresCluster>.Some(cluster));
            },
            clients);
        var result = Assert.IsType<ObservedWorkload>(await observer.ObserveAsync(snapshot));
        Assert.Equal(currentCondition, result.Ready);
        Assert.Equal(1, clients.Reads);
        Assert.DoesNotContain("secret-canary", JsonSerializer.Serialize(result));
    }

    private sealed class ExactClient(
        IKubernetes client,
        string expectedTarget
    ) : IKubernetesClientProvider
    {
        public int Reads { get; private set; }

        public Task<IKubernetes> GetAsync(string id, CancellationToken cancellationToken = default)
        {
            Assert.Equal(expectedTarget, id);
            Reads++;
            return Task.FromResult(client);
        }

        public Task<IKubernetes> GetForCleanupAsync(string id, CancellationToken cancellationToken = default) =>
            throw new XunitException("Health must not use cleanup credentials.");

        public Task<IKubernetes> GetDefaultAsync(CancellationToken cancellationToken = default) =>
            throw new XunitException("Health must not guess a default target.");
    }

    private sealed class ReadHandler(
        Func<HttpRequestMessage, string> response
    ) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(response(request), Encoding.UTF8, "application/json"),
            });
    }
}
