using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Domains.Contracts;
using Pole.Features.Quotas.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Testing;
using Pole.Workloads;

namespace Pole.Features.WebServices.Tests.Integration;

internal sealed class CreateWebServicePlatform : IResourceRegistrar
{
    internal string GroupId { get; } = ObjectId.GenerateNewId().ToString();
    internal string IsolationScopeId { get; } = ObjectId.GenerateNewId().ToString();
    internal TestDeploymentTargetLookup Targets { get; } = new();
    internal List<RegisterResourceRequest> Registrations { get; } = [];
    internal List<CreateResourceDeploymentRequest> Deployments { get; } = [];
    internal List<CreateGeneratedHostnameBindingRequest> Bindings { get; } = [];
    internal List<GetOrCreateGeneratedZoneRequest> Zones { get; } = [];

    internal void RegisterContracts(IServiceCollection services, IMongoClient client)
    {
        services.AddSingleton<GetResourceGroupOwnership>((tenant, group, _) =>
            Task.FromResult<GetResourceGroupOwnershipOutcome>(
                tenant == TestPlatformDefaults.TenantId && group == GroupId
                    ? new ResourceGroupOwnership(group, tenant, IsolationScopeId, ResourceGroupOwnershipLifecycle.Active)
                    : new ResourceGroupOwnershipError(new ResourceGroupOwnershipNotFound(group))));
        services.AddSingleton<ListEligibleDeploymentTargetsForNewWorkload>(Targets.ListEligibleAsync);
        services.AddSingleton<ListEligiblePublicIngressDeploymentTargetsForNewWorkload>(Targets.ListEligiblePublicIngressForNewWorkloadAsync);
        services.AddSingleton<ResolveComputeProfiles>((request, _) =>
            Task.FromResult<ResolveComputeProfilesOutcome>(new ResolveComputeProfilesOutcome(
                request.Targets.Select(target => new ResolvedComputeProfile(
                    "507f1f77bcf86cd799439097", 1, target.DeploymentTargetId, "General purpose",
                    new(250, 256), new(1000, 1024), ComputeArchitecture.Any, [], [], [], [], [], null)).ToArray())));
        services.AddSingleton<IQuotaAdmissionExecutor>(new AdmittedQuota(new MongoRunTransaction(client)));
        services.AddSingleton<IResourceRegistrar>(this);
        services.AddSingleton<GetOrCreateGeneratedDomainZones>((requests, _, _) =>
        {
            Zones.AddRange(requests);
            return Task.FromResult(new GetOrCreateGeneratedDomainZonesOutcome(requests.Select(zone =>
                new TenantDomainZoneSnapshot(ObjectId.GenerateNewId().ToString(), zone.TenantId,
                    zone.DeploymentTargetId, "tenant", zone.PublicBaseDomain, "Generated", 1,
                    DateTime.UnixEpoch, DateTime.UnixEpoch, null)).ToArray()));
        });
        services.AddSingleton<CreateGeneratedHostnameBindings>((requests, _, _) =>
        {
            Bindings.AddRange(requests);
            return Task.FromResult(new CreateGeneratedHostnameBindingsOutcome(requests.Select(binding =>
                new HostnameBindingSnapshot(ObjectId.GenerateNewId().ToString(), binding.TenantId,
                    binding.TenantDomainZoneId, binding.DeploymentTargetId, binding.ResourceType.Value,
                    binding.ResourceId, binding.ResourceDeploymentId, binding.RelativeName,
                    binding.RelativeName + ".apps.test.local", "Generated", DateTime.UnixEpoch, null)).ToArray()));
        });
        services.AddSingleton<CreateResourceDeploymentSet>((request, _, _) =>
        {
            Deployments.AddRange(request.Deployments);
            return Task.FromResult<CreateResourceDeploymentSetOutcome>(
                new CreatedResourceDeployments(request.Deployments.Select(Placement).ToArray()));
        });
    }

    private static PlacementSummaryDto Placement(CreateResourceDeploymentRequest request)
    {
        var compute = request.Compute;
        return new(request.DeploymentId, request.IsolationScopeId!, ObjectId.GenerateNewId().ToString(),
            request.Target.DeploymentTargetId, 1, 0, ResourceDeploymentStatus.Pending, null,
            new(new(compute.ComputeProfileId, compute.ComputeProfileRevision), compute.DisplayName,
                new(compute.Requests.CpuMillis, compute.Requests.MemoryMi),
                new(compute.Limits.CpuMillis, compute.Limits.MemoryMi), []),
            [], request.ResourceRevision, request.FixedReplicas, request.Target.Ingress, 1,
            ResourceDeploymentDesiredOperation.Apply, new("Supported", "Ready", "Test", null));
    }

    public Task<ResourceRegistrationOutcome> RegisterAsync(
        RegisterResourceRequest request, IClientSessionHandle session, CancellationToken cancellationToken)
    {
        Registrations.Add(request);
        return Task.FromResult<ResourceRegistrationOutcome>(new RegisteredResource(
            ObjectId.GenerateNewId().ToString(), request.TenantId, request.ResourceGroupId,
            request.Resource.ResourceTypeId, request.Resource.ResourceId, request.Resource.DisplayName,
            null, DateTime.UnixEpoch, DateTime.UnixEpoch));
    }

    public Task<ResourceRegistrationOutcome> UpdateAsync(
        UpdateRegisteredResourceRequest request, IClientSessionHandle session, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Unexpected registration update during creation.");

    public Task<ResourceUnregistrationOutcome> UnregisterAsync(
        UnregisterResourceRequest request, IClientSessionHandle session, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Unexpected unregistration during creation.");

    private sealed class AdmittedQuota(MongoRunTransaction transactions) : IQuotaAdmissionExecutor
    {
        public Task<TOutcome> ExecuteOutcomeAsync<TSpec, TOutcome>(
            ExecuteQuotaAdmissionRequest<TSpec, TOutcome> request, CancellationToken cancellationToken)
            where TOutcome : notnull =>
            transactions.InvokeOutcomeAsync<TOutcome>(async (transaction, ct) =>
                await request.MutationCallback(new(transaction.Session), ct) switch
                {
                    QuotaAdmissionTransactionCommit<TOutcome> commit => transaction.Commit(commit.Outcome),
                    QuotaAdmissionTransactionAbort<TOutcome> abort => transaction.Abort(abort.Outcome)
                }, cancellationToken);
    }
}
