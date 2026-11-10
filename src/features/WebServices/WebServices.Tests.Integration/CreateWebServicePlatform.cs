using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Domains.Contracts;
using Pole.Features.Quotas.Contracts;
using Pole.Features.ResourceConnections.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Testing;
using Pole.Features.Secrets.Contracts;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Testing;
using Pole.Workloads;

namespace Pole.Features.WebServices.Tests.Integration;

internal sealed class CreateWebServicePlatform(
    TestResourceGroup group
)
{
    internal string GroupId => group.Id;
    internal string IsolationScopeId => group.DefaultIsolationScopeId;
    internal TestDeploymentTargetLookup Targets { get; } = new();
    internal Dictionary<string, IReadOnlyList<PlacementSummaryDto>> Placements { get; } = [];
    internal List<ResourceDeploymentSetApplyRequest> ApplyRequests { get; } = [];
    internal List<CreateResourceDeploymentRequest> Deployments { get; } = [];
    internal List<CreateGeneratedHostnameBindingRequest> Bindings { get; } = [];
    internal List<GetOrCreateGeneratedZoneRequest> Zones { get; } = [];

    internal void RegisterContracts(IServiceCollection services, IMongoClient client)
    {
        services.AddSingleton<ListEligibleDeploymentTargetsForNewWorkload>(Targets.ListEligibleAsync);
        services.AddSingleton<ListEligiblePublicIngressDeploymentTargetsForNewWorkload>(
            Targets.ListEligiblePublicIngressForNewWorkloadAsync);
        services.AddSingleton<ResolveComputeProfiles>((request, _) => Task.FromResult(
            new ResolveComputeProfilesOutcome(
                request.Targets
                    .Select(target => new ResolvedComputeProfile(
                        "507f1f77bcf86cd799439097",
                        1,
                        target.DeploymentTargetId,
                        "General purpose",
                        new ComputeResourceValues(250, 256),
                        new ComputeResourceValues(1000, 1024),
                        ComputeArchitecture.Any,
                        [],
                        [],
                        [],
                        [],
                        [],
                        null))
                    .ToArray())));
        services.AddSingleton<IQuotaAdmissionExecutor>(new AdmittedQuota(new MongoRunTransaction(client)));
        services.AddSingleton<GetOrCreateGeneratedDomainZones>((
            requests, _,
            _) =>
        {
            Zones.AddRange(requests);
            return Task.FromResult(
                new GetOrCreateGeneratedDomainZonesOutcome(
                    requests.Select(zone => new TenantDomainZoneSnapshot(
                            ObjectId.GenerateNewId().ToString(),
                            zone.TenantId,
                            zone.DeploymentTargetId,
                            "tenant",
                            zone.PublicBaseDomain,
                            "Generated",
                            1,
                            DateTime.UnixEpoch,
                            DateTime.UnixEpoch,
                            null))
                        .ToArray()));
        });
        services.AddSingleton<CreateGeneratedHostnameBindings>((
            requests, _,
            _) =>
        {
            Bindings.AddRange(requests);
            return Task.FromResult(
                new CreateGeneratedHostnameBindingsOutcome(
                    requests.Select(binding => new HostnameBindingSnapshot(
                            ObjectId.GenerateNewId().ToString(),
                            binding.TenantId,
                            binding.TenantDomainZoneId,
                            binding.DeploymentTargetId,
                            binding.ResourceType.Value,
                            binding.ResourceId,
                            binding.ResourceDeploymentId,
                            binding.RelativeName,
                            binding.RelativeName + ".apps.test.local",
                            "Generated",
                            DateTime.UnixEpoch,
                            null))
                        .ToArray()));
        });
        services.AddSingleton<LoadResourceDeploymentSets>((request, _) =>
            Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<PlacementSummaryDto>>>(
                request.ResourceIds.Where(Placements.ContainsKey).ToDictionary(id => id, id => Placements[id])));
        services.AddSingleton<LoadResourceDeploymentSet>((request, _) => Task.FromResult(
            Placements.GetValueOrDefault(request.ResourceId) ?? (IReadOnlyList<PlacementSummaryDto>)[]));
        services.AddSingleton<AdmitDeploymentTargetPublicIngressForNewWorkload>(Targets.AdmitForNewWorkloadAsync);
        services.AddSingleton<LoadHostnameBindingsForDeploymentInTransaction>((
            _, _,
            _) => Task.FromResult<IReadOnlyList<HostnameBindingSnapshot>>([]));
        services.AddSingleton<ReconcileConnectionsForResource>((
            _, _,
            _) => Task.FromResult<ReconcileConnectionsForResourceOutcome>(new ResourceConnectionTopologyValid(0)));
        services.AddSingleton<AdmitResourceConnectionReferences>((
            _, _,
            _) => Task.FromResult<AdmitResourceConnectionReferencesOutcome>(
            new ResourceConnectionReferencesAdmitted()));
        services.AddSingleton<AdmitConsumerSecretReferences>((
            _, _,
            _) => Task.FromResult<AdmitConsumerSecretReferencesOutcome>(new ConsumerSecretReferencesAdmitted()));
        services.AddSingleton<LoadHostnameBindingForPlanning>((
            _, _,
            _) => throw new InvalidOperationException("Unexpected hostname planning lookup."));
        services.AddSingleton<LoadResourceDeploymentPlanningSnapshot>((
            _, _,
            _) => throw new InvalidOperationException("Unexpected placement planning lookup."));
        services.AddSingleton<BeginSourceBuildPromotion>((
            _, _,
            _) => throw new InvalidOperationException("Unexpected source build promotion."));
        services.AddSingleton<AdvanceResourceDeploymentRevision>((
            _, _,
            _) => throw new InvalidOperationException("Unexpected metadata-only revision advance."));
        services.AddSingleton<RequestResourceDeploymentSetApply>((
            request, _,
            _) =>
        {
            ApplyRequests.Add(request);
            var updated = Placements[request.ResourceId]
                .Select(placement => placement with
                {
                    DesiredGeneration = placement.DesiredGeneration + 1,
                    ResourceRevision = request.ResourceRevision,
                    DeploymentRevision = placement.DeploymentRevision + 1,
                    Status = ResourceDeploymentStatus.Pending,
                })
                .ToArray();
            Placements[request.ResourceId] = updated;
            return Task.FromResult<ResourceDeploymentSetMutationOutcome>(new MutatedResourceDeployments(updated));
        });
        services.AddSingleton<CreateResourceDeploymentSet>((
            request, _,
            _) =>
        {
            Deployments.AddRange(request.Deployments);
            var placements = request.Deployments.Select(Placement).ToArray();
            Placements.Add(request.Deployments[0].ResourceId, placements);
            return Task.FromResult<CreateResourceDeploymentSetOutcome>(new CreatedResourceDeployments(placements));
        });
    }

    private static PlacementSummaryDto Placement(CreateResourceDeploymentRequest request)
    {
        var compute = request.Compute;
        return new PlacementSummaryDto(
            request.DeploymentId,
            request.IsolationScopeId!,
            ObjectId.GenerateNewId().ToString(),
            request.Target.DeploymentTargetId,
            1,
            0,
            ResourceDeploymentStatus.Pending,
            null,
            new ComputeConfigurationSummaryDto(
                new ComputeProfileReference(compute.ComputeProfileId, compute.ComputeProfileRevision),
                compute.DisplayName,
                new ComputeResourceValuesDto(compute.Requests.CpuMillis, compute.Requests.MemoryMi),
                new ComputeResourceValuesDto(compute.Limits.CpuMillis, compute.Limits.MemoryMi),
                []),
            [],
            request.ResourceRevision,
            request.FixedReplicas,
            request.Target.Ingress,
            1,
            ResourceDeploymentDesiredOperation.Apply,
            new ResourceDeploymentIsolationDto("Supported", "Ready", "Test", null));
    }

    private sealed class AdmittedQuota(
        MongoRunTransaction transactions
    ) : IQuotaAdmissionExecutor
    {
        public Task<TOutcome> ExecuteOutcomeAsync<TSpec, TOutcome>(
            ExecuteQuotaAdmissionRequest<TSpec, TOutcome> request,
            CancellationToken cancellationToken)
            where TOutcome : notnull => transactions.InvokeOutcomeAsync<TOutcome>(
            async (transaction, ct) => await request.MutationCallback(
                    new QuotaAdmissionTransactionContext<TOutcome>(transaction.Session),
                    ct) switch
                {
                    QuotaAdmissionTransactionCommit<TOutcome> commit => transaction.Commit(commit.Outcome),
                    QuotaAdmissionTransactionAbort<TOutcome> abort => transaction.Abort(abort.Outcome),
                },
            cancellationToken);
    }
}
