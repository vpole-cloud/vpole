using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Events.Abstractions;
using Pole.Features.ResourceConnections.Application.Persistence;
using Pole.Features.ResourceConnections.Contracts;
using Pole.Features.ResourceConnections.Domain;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Endpoints;
using Pole.Features.WebServices.Application.Persistence.Mutations;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Events;
using Pole.Features.Shared.Models;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.ResourceConnections;

public sealed class ResourceConnectionMutationIntegrationTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    private IMongoCollection<ResourceConnection> Connections => Fixture.Database
        .GetCollection<ResourceConnection>("ResourceConnections");
    private IMongoCollection<WebService> WebServices => Fixture.Database
        .GetCollection<WebService>("WebServices");

    [Fact]
    public async Task Logical_connection_fields_resolve_exact_target_host_and_service_port_and_reject_revoked_or_foreign_source()
    {
        var connection = await CreateWebConnectionAsync();
        var source = await DbContext.ResourceDeployments.Find(item => item.ResourceId == connection.SourceResourceId).SingleAsync();
        var destination = await DbContext.ResourceDeployments.Find(item => item.ResourceId == connection.DestinationResourceId).SingleAsync();
        var secondTarget = ObjectId.GenerateNewId().ToString();
        var secondSource = OnSecondTarget(source, secondTarget, "caller");
        var secondDestination = OnSecondTarget(destination, secondTarget, "receiver");
        await DbContext.ResourceDeployments.InsertManyAsync([secondSource, secondDestination]);
        using var session = await Fixture.Client.StartSessionAsync(); session.StartTransaction();
        Assert.IsType<ResourceConnectionTopologyValid>((await Services.GetRequiredService<ReconcileConnectionsForResource>()(
            TestData.TenantId, WebServiceResourceDeployment.ResourceType, connection.SourceResourceId, session)).Value);
        ResourceConnectionValueReference[] references = [new(connection.Id, "Host"), new(connection.Id, "Port")];
        Assert.IsType<ResourceConnectionReferencesAdmitted>((await Services.GetRequiredService<AdmitResourceConnectionReferences>()(
            TestData.TenantId, WebServiceResourceDeployment.ResourceType, connection.SourceResourceId, references, session, default)).Value);
        await session.CommitTransactionAsync();
        var refreshed = await LoadConnectionAsync(connection.Id);
        var resolver = Services.GetRequiredService<ResolveResourceConnectionReferences>();
        var first = Assert.IsType<ResolvedResourceConnectionValues>((await resolver(TestData.TenantId, WebServiceResourceDeployment.ResourceType, connection.SourceResourceId, source.Id, references, default)).Value);
        var second = Assert.IsType<ResolvedResourceConnectionValues>((await resolver(TestData.TenantId, WebServiceResourceDeployment.ResourceType, connection.SourceResourceId, secondSource.Id, references, default)).Value);
        Assert.NotEqual(first.Values.Single(item => item.Reference.ValueKind == "Host").Value, second.Values.Single(item => item.Reference.ValueKind == "Host").Value);
        Assert.Equal("80", first.Values.Single(item => item.Reference.ValueKind == "Port").Value); // Target port is 8080.
        var destinationService = await WebServices.Find(item => item.Id == connection.DestinationResourceId).SingleAsync();
        var changedSpec = destinationService.Spec with { Config = destinationService.Spec.Config with { PortMappings = [new PortMapping(81, "8080", "TCP", "http")] } };
        await WebServices.ReplaceOneAsync(item => item.Id == destinationService.Id, destinationService with { Spec = changedSpec });
        Assert.Equal("EndpointChanged", Assert.IsType<ResourceConnectionReferencesUnavailable>((await resolver(TestData.TenantId, WebServiceResourceDeployment.ResourceType, connection.SourceResourceId, source.Id, references, default)).Value).ReasonCode);
        await WebServices.ReplaceOneAsync(item => item.Id == destinationService.Id, destinationService);
        Assert.IsType<ResourceConnectionReferencesUnavailable>((await resolver(TestData.TenantId, WebServiceResourceDeployment.ResourceType, connection.DestinationResourceId, destination.Id, references, default)).Value);
        Assert.IsType<ResourceConnectionReferencesUnavailable>((await resolver(ObjectId.GenerateNewId().ToString(), WebServiceResourceDeployment.ResourceType, connection.SourceResourceId, source.Id, references, default)).Value);
        await Connections.ReplaceOneAsync(item => item.Id == connection.Id, refreshed.BeginDeletion(DateTime.UtcNow));
        Assert.IsType<ResourceConnectionReferencesUnavailable>((await resolver(TestData.TenantId, WebServiceResourceDeployment.ResourceType, connection.SourceResourceId, source.Id, references, default)).Value);
    }

    [Fact]
    public async Task PlacementDeletion_RejectsBrokenConnection_AndCallerAbortRestoresAllWrites()
    {
        var connection = await CreateWebConnectionAsync();
        var destinationId = connection.DestinationResourceId;
        var deployment = await DbContext.ResourceDeployments
            .Find(item => item.ResourceId == destinationId).SingleAsync();
        var registrationBefore = await DbContext.ResourceRegistrations
            .Find(item => item.ResourceId == destinationId).SingleAsync();
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();

        var result = await Services.GetRequiredService<RequestExactResourceDeploymentDeletion>()(
            new ResourceDeploymentExactMutationRequest(
                TestData.TenantId,
                WebServiceResourceDeployment.ResourceType,
                destinationId,
                deployment.Id,
                deployment.Revision,
                new ComputeProfileReference(
                    deployment.ResolvedPlacement.Compute.ComputeProfileId,
                    deployment.ResolvedPlacement.Compute.ComputeProfileRevision)),
            session);

        var error = Assert.IsType<ResourceDeploymentMutationError>(result.Value);
        var conflict = Assert.IsType<ResourceDeploymentConnectionConflict>(error.Value);
        Assert.Equal(connection.Id, conflict.ConnectionId);
        await session.AbortTransactionAsync();
        var unchanged = await DbContext.ResourceDeployments.Find(item => item.Id == deployment.Id).SingleAsync();
        Assert.Equal(ResourceDeploymentOperation.Apply, unchanged.DesiredOperation);
        Assert.Equal(deployment.Revision, unchanged.Revision);
        Assert.Equal(connection.Generation, (await LoadConnectionAsync(connection.Id)).Generation);
        var registrationAfter = await DbContext.ResourceRegistrations
            .Find(item => item.ResourceId == destinationId).SingleAsync();
        Assert.Equal(registrationBefore.ReferenceFence, registrationAfter.ReferenceFence);
        Assert.Equal(0, await DbContext.CountOutboxMessagesAsync(
            "pole.resource-connections/v1/resource-connection-reconciliation-requested"));
    }

    [Fact]
    public async Task RemovingLastSourcePlacement_ReportsExactConflict_AndAbortRestoresPlacement()
    {
        var connection = await CreateWebConnectionAsync();
        var sourceId = connection.SourceResourceId;
        var deployment = await DbContext.ResourceDeployments
            .Find(item => item.ResourceId == sourceId).SingleAsync();
        var registrationBefore = await DbContext.ResourceRegistrations
            .Find(item => item.ResourceId == sourceId).SingleAsync();
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        Assert.NotNull(await Services.GetRequiredService<FenceRegisteredResource>()(
            TestData.TenantId, WebServiceResourceDeployment.ResourceType, sourceId, session));
        await DbContext.ResourceDeployments.DeleteOneAsync(session, item => item.Id == deployment.Id);

        var result = await Services.GetRequiredService<ReconcileConnectionsForResource>()(
            TestData.TenantId, WebServiceResourceDeployment.ResourceType, sourceId, session);

        Assert.Equal(connection.Id,
            Assert.IsType<ResourceConnectionSourceHasNoPlacements>(result.Value).ConnectionId);
        await session.AbortTransactionAsync();
        Assert.NotNull(await DbContext.ResourceDeployments
            .Find(item => item.Id == deployment.Id).SingleOrDefaultAsync());
        Assert.Equal(registrationBefore.ReferenceFence,
            (await DbContext.ResourceRegistrations
                .Find(item => item.ResourceId == sourceId).SingleAsync()).ReferenceFence);
        Assert.Equal(connection.Generation, (await LoadConnectionAsync(connection.Id)).Generation);
    }

    [Fact]
    public async Task RemovingEndpoint_ReportsExactConflict_AndAbortRestoresEndpoint()
    {
        var connection = await CreateWebConnectionAsync();
        var current = await WebServices.Find(item => item.Id == connection.DestinationResourceId)
            .SingleAsync();
        var replacement = current.UpdateDesiredState(
            current.Spec with { Config = current.Spec.Config with { PortMappings = [] } },
            current.Tags);
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        Assert.NotNull(await Services.GetRequiredService<FenceRegisteredResource>()(
            TestData.TenantId, WebServiceResourceDeployment.ResourceType, current.Id, session));
        await WebServices.ReplaceOneAsync(session, item => item.Id == current.Id, replacement);

        var result = await Services.GetRequiredService<ReconcileConnectionsForResource>()(
            TestData.TenantId, WebServiceResourceDeployment.ResourceType, current.Id, session);

        Assert.Equal(connection.Id,
            Assert.IsType<ResourceConnectionEndpointMissing>(result.Value).ConnectionId);
        await session.AbortTransactionAsync();
        var unchanged = await WebServices.Find(item => item.Id == current.Id).SingleAsync();
        Assert.Equal(current.Revision, unchanged.Revision);
        Assert.Equal("http", Assert.Single(unchanged.Spec.Config.PortMappings).Name);
        Assert.Equal(connection.Generation, (await LoadConnectionAsync(connection.Id)).Generation);
    }

    [Fact]
    public async Task DeletingDestination_ReportsExactConflict_AndAbortRestoresLifecycle()
    {
        var connection = await CreateWebConnectionAsync();
        var current = await WebServices.Find(item => item.Id == connection.DestinationResourceId)
            .SingleAsync();
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        Assert.NotNull(await Services.GetRequiredService<FenceRegisteredResource>()(
            TestData.TenantId, WebServiceResourceDeployment.ResourceType, current.Id, session));
        await WebServices.ReplaceOneAsync(
            session,
            item => item.Id == current.Id,
            current.BeginDeletion(DateTime.UtcNow));

        var result = await Services.GetRequiredService<ReconcileConnectionsForResource>()(
            TestData.TenantId, WebServiceResourceDeployment.ResourceType, current.Id, session);

        Assert.Equal(connection.Id,
            Assert.IsType<ResourceConnectionDestinationDeletingConflict>(result.Value).ConnectionId);
        await session.AbortTransactionAsync();
        var unchanged = await WebServices.Find(item => item.Id == current.Id).SingleAsync();
        Assert.IsType<ActiveWebServiceLifecycle>(unchanged.Lifecycle);
        Assert.Equal(current.Revision, unchanged.Revision);
        Assert.Equal(connection.Generation, (await LoadConnectionAsync(connection.Id)).Generation);
    }

    [Fact]
    public async Task SourceTargetRemoval_PreservesRemainingPairAndRetiresRemovedPolicyLocation()
    {
        var connection = await CreateWebConnectionAsync();
        var source = await DbContext.ResourceDeployments
            .Find(item => item.ResourceId == connection.SourceResourceId).SingleAsync();
        var destination = await DbContext.ResourceDeployments
            .Find(item => item.ResourceId == connection.DestinationResourceId).SingleAsync();
        var secondTargetId = ObjectId.GenerateNewId().ToString();
        var secondSource = OnSecondTarget(source, secondTargetId, "source-second-cell");
        var secondDestination = OnSecondTarget(destination, secondTargetId, "destination-second-cell");
        await DbContext.ResourceDeployments.InsertManyAsync([secondSource, secondDestination]);

        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var fence = Services.GetRequiredService<FenceRegisteredResource>();
        Assert.NotNull(await fence(
            TestData.TenantId,
            WebServiceResourceDeployment.ResourceType,
            source.ResourceId,
            session));
        var seeded = await Services.GetRequiredService<ReconcileConnectionsForResource>()(
            TestData.TenantId,
            WebServiceResourceDeployment.ResourceType,
            source.ResourceId,
            session);
        Assert.Equal(1, Assert.IsType<ResourceConnectionTopologyValid>(seeded.Value).RefreshedCount);
        await session.CommitTransactionAsync();

        var before = await LoadConnectionAsync(connection.Id);
        Assert.Equal(2, Assert.IsType<ActiveResourceConnection>(before.Lifecycle).Targets.Count);
        session.StartTransaction();
        var removal = await Services.GetRequiredService<RequestExactResourceDeploymentDeletion>()(
            new ResourceDeploymentExactMutationRequest(
                TestData.TenantId,
                WebServiceResourceDeployment.ResourceType,
                source.ResourceId,
                secondSource.Id,
                secondSource.Revision,
                new ComputeProfileReference(
                    secondSource.ResolvedPlacement.Compute.ComputeProfileId,
                    secondSource.ResolvedPlacement.Compute.ComputeProfileRevision)),
            session);
        Assert.IsType<PlacementSummaryDto>(removal.Value);
        await session.CommitTransactionAsync();

        var refreshed = await LoadConnectionAsync(connection.Id);
        Assert.Equal(before.Generation + 1, refreshed.Generation);
        var active = Assert.IsType<ActiveResourceConnection>(refreshed.Lifecycle);
        Assert.Single(active.Targets);
        Assert.Equal(source.ResolvedPlacement.DeploymentTargetId, active.Targets[0].Snapshot.TargetId);
        var retired = Assert.Single(active.RetiringTargets);
        Assert.Equal(secondTargetId, retired.Snapshot.TargetId);
        Assert.Equal(secondSource.Id, Assert.Single(retired.Snapshot.Sources).ResourceDeploymentId);
        Assert.Equal(secondDestination.Id, retired.Snapshot.DestinationResourceDeploymentId);
        Assert.IsType<PendingConnectionCleanup>(retired.Status);
        var removedDeployment = await DbContext.ResourceDeployments
            .Find(item => item.Id == secondSource.Id).SingleAsync();
        Assert.Equal(ResourceDeploymentOperation.Delete, removedDeployment.DesiredOperation);
        Assert.Equal(2, await DbContext.CountOutboxMessagesAsync(
            "pole.resource-connections/v1/resource-connection-reconciliation-requested"));
    }

    [Fact]
    public async Task EndpointRemoval_RejectsAndRollsBack_WhileSameEndpointPortChangeRefreshesPolicy()
    {
        var connection = await CreateWebConnectionAsync();
        var current = await WebServices.Find(item => item.Id == connection.DestinationResourceId).SingleAsync();
        var persist = Services.GetRequiredService<PersistUpdatedWebServiceOutcome>();
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var removed = current.UpdateDesiredState(
            current.Spec with { Config = current.Spec.Config with { PortMappings = [] } }, current.Tags);

        var rejected = await persist(session, current, removed, UpdatedEvent(removed));

        Assert.Equal(connection.Id,
            Assert.IsType<WebServiceConnectionConflict>(rejected.Value).ConnectionId);
        await session.AbortTransactionAsync();
        var unchanged = await WebServices.Find(item => item.Id == current.Id).SingleAsync();
        Assert.Equal(current.Revision, unchanged.Revision);
        Assert.Equal("http", Assert.Single(unchanged.Spec.Config.PortMappings).Name);
        Assert.Equal(0, await DbContext.CountOutboxMessagesAsync("pole.web-services/v1/web-service-updated"));

        session.StartTransaction();
        var changed = current.UpdateDesiredState(
            current.Spec with
            {
                Config = current.Spec.Config with
                {
                    PortMappings = [new PortMapping(81, "9090", "TCP", "http")]
                }
            }, current.Tags);
        var accepted = await persist(session, current, changed, UpdatedEvent(changed));
        Assert.IsType<WebServiceUpdateAccepted>(accepted.Value);
        await session.CommitTransactionAsync();

        var refreshed = await LoadConnectionAsync(connection.Id);
        Assert.Equal(connection.Generation + 1, refreshed.Generation);
        var target = Assert.Single(Assert.IsType<ActiveResourceConnection>(refreshed.Lifecycle).Targets);
        Assert.Equal(81, target.Snapshot.DestinationServicePort);
        Assert.Equal(9090, target.Snapshot.DestinationPort);
        Assert.IsType<PendingConnectionApply>(target.Status);
        Assert.Equal(1, await DbContext.CountOutboxMessagesAsync(
            "pole.resource-connections/v1/resource-connection-reconciliation-requested"));
        Assert.Equal(1, await DbContext.CountOutboxMessagesAsync("pole.web-services/v1/web-service-updated"));
    }

    [Fact]
    public async Task WholeResourceDeletion_CascadesConnection_WithRetainedCleanupSnapshot()
    {
        var connection = await CreateWebConnectionAsync();
        var original = Assert.Single(Assert.IsType<ActiveResourceConnection>(connection.Lifecycle).Targets).Snapshot;

        var result = await Mediator.Send(new DeleteWebServiceRequest(
            connection.DestinationResourceId, TestData.TenantId));

        _ = result.AssertSuccess();
        var deleting = await LoadConnectionAsync(connection.Id);
        Assert.Equal(connection.Generation + 1, deleting.Generation);
        var cleanup = Assert.Single(Assert.IsType<DeletingResourceConnection>(deleting.Lifecycle).Targets);
        Assert.True(original.HasSameResolvedValues(cleanup.Snapshot));
        Assert.Equal(1, await DbContext.CountOutboxMessagesAsync(
            "pole.resource-connections/v1/resource-connection-deletion-requested"));
        Assert.All(await DbContext.ResourceDeployments
                .Find(item => item.ResourceId == connection.DestinationResourceId).ToListAsync(),
            deployment => Assert.Equal(ResourceDeploymentOperation.Delete, deployment.DesiredOperation));
    }

    private async Task<ResourceConnection> CreateWebConnectionAsync()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var source = (await Mediator.Send(TestData.CreateWebServiceRequest(group.Id, "caller"))).AssertSuccess();
        var destination = (await Mediator.Send(TestData.CreateWebServiceRequest(group.Id, "receiver"))).AssertSuccess();
        var outcome = await Services.GetRequiredService<PersistResourceConnectionCreation>()(
            new CreateResourceConnectionCommand(
                TestData.TenantId,
                "caller-to-receiver",
                WebServiceResourceDeployment.ResourceType,
                source.WebServiceId,
                WebServiceResourceDeployment.ResourceType,
                destination.WebServiceId,
                "http"));
        return Assert.IsType<ResourceConnection>(outcome.Value);
    }

    private Task<ResourceConnection> LoadConnectionAsync(string id) =>
        Connections.Find(item => item.Id == id).SingleAsync();

    private static ResourceDeployment OnSecondTarget(
        ResourceDeployment original,
        string deploymentTargetId,
        string namespacePrefix)
    {
        var runtimeCellId = ObjectId.GenerateNewId().ToString();
        var placement = original.ResolvedPlacement with
        {
            RuntimeCellId = runtimeCellId,
            DeploymentTargetId = deploymentTargetId,
            KubernetesNamespace = $"{namespacePrefix}-{runtimeCellId[..8]}"
        };
        return ResourceDeploymentFactory.Create(
            original.TenantId,
            WebServiceResourceDeployment.ResourceType,
            original.ResourceId,
            original.ResourceGroupId,
            original.IsolationScopeId,
            placement,
            ResourcePlacementCapability.MultiTarget,
            runtimeCellReady: true,
            new ResourceDeploymentFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(),
                original.CreatedAtUtc,
                original.ResourceRevision,
                original.FixedReplicas));
    }

    private static WebServiceUpdatedEvent UpdatedEvent(WebService service)
    {
        var spec = service.Spec;
        static WebServiceProbeConfigSnapshot Probe(ProbeConfig probe) => new(
            probe.Path,
            probe.Port,
            probe.InitialDelaySeconds,
            probe.PeriodSeconds,
            probe.TimeoutSeconds,
            probe.FailureThreshold,
            probe.SuccessThreshold);
        return new WebServiceUpdatedEvent(
            service.Id,
            service.Name,
            service.ResourceGroupId,
            service.IsolationScopeId,
            service.Revision,
            new WebServiceSpecSnapshot(
                new WebServiceImageInfoSnapshot(
                    spec.Image.Server,
                    spec.Image.Name,
                    spec.Image.Tag,
                    spec.Image.SecretName),
                new WebServiceConfigSnapshot(
                    spec.Config.EnvironmentVariables,
                    spec.Config.PortMappings.Select(mapping => new PortMappingSnapshot(
                        mapping.ServicePort,
                        mapping.TargetPort,
                        mapping.Protocol,
                        mapping.Name)).ToImmutableList()),
                spec.Autoscaling is null ? null : new AutoscalingSpecSnapshot(
                    spec.Autoscaling.MinReplicas,
                    spec.Autoscaling.MaxReplicas,
                    new AutoscalingMetricSnapshot(
                        spec.Autoscaling.Metric.Type,
                        spec.Autoscaling.Metric.TargetAverageUtilization)),
                new WebServiceProbeSpecSnapshot(
                    Probe(spec.Probes.Startup),
                    Probe(spec.Probes.Liveness),
                    Probe(spec.Probes.Readiness))),
            new WebServiceExposureSnapshot(
                service.Exposure.Mode.ToString(),
                service.Exposure.GeneratedHostnameLabelPrefix),
            service.Tags,
            [])
        {
            EventCore = EventCore.Create(service.TenantId)
        };
    }
}
