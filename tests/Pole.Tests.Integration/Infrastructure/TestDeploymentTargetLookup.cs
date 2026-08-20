using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.ResultType;

namespace Pole.Tests.Integration.Infrastructure;

internal sealed class TestDeploymentTargetLookup :
    IDeploymentTargetLookup,
    IDeploymentTargetReferenceAdmission,
    IDeploymentTargetWorkloadConfigurationFence
{
    private int _rejectNewWorkloads;
    private int _rejectReferenceRemoval;
    private readonly Dictionary<string, DeploymentTargetLookupDto> _targets =
        new(StringComparer.Ordinal)
        {
            [Target.Id] = Target
        };

    private static readonly DeploymentTargetLookupDto Target = new(
        TestData.DefaultDeploymentTargetId,
        "Integration test cluster",
        "test:kubernetes:integration",
        "InCluster",
        ConnectionVersion: 1,
        new DeploymentTargetWorkloadConfigurationLookupDto(
            Revision: 1,
            new DeploymentTargetIngressConfigurationLookupDto(
                "traefik",
                "letsencrypt-prod",
                "apps.test.local")),
        Revision: 1,
        Enabled: true,
        AcceptsWorkloads: true,
        DeletionRequested: false,
        HealthStatus: "Healthy");

    public bool RejectNewWorkloads
    {
        get => Volatile.Read(ref _rejectNewWorkloads) == 1;
        set => Volatile.Write(ref _rejectNewWorkloads, value ? 1 : 0);
    }

    public bool RejectReferenceRemoval
    {
        get => Volatile.Read(ref _rejectReferenceRemoval) == 1;
        set => Volatile.Write(ref _rejectReferenceRemoval, value ? 1 : 0);
    }

    public void AddEligibleTarget(string id, string publicBaseDomain)
    {
        _targets[id] = Target with
        {
            Id = id,
            DisplayName = $"Integration target {id}",
            ClusterIdentity = $"test:kubernetes:{id}",
            WorkloadConfiguration = Target.WorkloadConfiguration with
            {
                Ingress = Target.WorkloadConfiguration.Ingress! with
                {
                    PublicBaseDomain = publicBaseDomain
                }
            }
        };
    }

    public Task<Result<DeploymentTargetLookupDto>> GetAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_targets.TryGetValue(deploymentTargetId, out var target)
            ? Result.Success(target)
            : (Result<DeploymentTargetLookupDto>)Failures.NotFound(
                "Deployment target not found.", deploymentTargetId));

    public Task<Result<DeploymentTargetLookupDto>> GetDefaultAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<Result<DeploymentTargetLookupDto>>(Target);

    public Task<Result<DeploymentTargetLookupDto>> ResolveForNewWorkloadAsync(
        string? deploymentTargetId,
        CancellationToken cancellationToken = default) =>
        RejectNewWorkloads
            ? Task.FromResult<Result<DeploymentTargetLookupDto>>(
                Failures.Conflict("The deployment target is not accepting new workloads."))
            : deploymentTargetId is null
                ? _targets.Count == 1
                    ? Task.FromResult<Result<DeploymentTargetLookupDto>>(_targets.Values.Single())
                    : Task.FromResult<Result<DeploymentTargetLookupDto>>(
                        Failures.Conflict("A deployment target must be selected."))
                : GetAsync(deploymentTargetId, cancellationToken);

    public Task<Result<IReadOnlyList<DeploymentTargetLookupDto>>>
        ListEligibleForNewWorkloadAsync(
            CancellationToken cancellationToken = default) =>
        Task.FromResult<Result<IReadOnlyList<DeploymentTargetLookupDto>>>(
            RejectNewWorkloads
                ? Array.Empty<DeploymentTargetLookupDto>()
                : _targets.Values.OrderBy(target => target.Id, StringComparer.Ordinal).ToArray());

    public Task<DeploymentTargetPublicIngressAdmissionResult>
        AdmitForNewWorkloadAsync(
            DeploymentTargetAdmissionRequest request,
            CancellationToken cancellationToken = default)
    {
        DeploymentTargetPublicIngressAdmissionResult result;
        if (RejectNewWorkloads)
        {
            result = new DeploymentTargetNewWorkloadsDisabled(
                request.ExplicitDeploymentTargetId ?? string.Empty);
        }
        else if (!_targets.TryGetValue(
                     request.ExplicitDeploymentTargetId ?? string.Empty,
                     out var target))
        {
            result = new DeploymentTargetNotFound(
                request.ExplicitDeploymentTargetId ?? string.Empty);
        }
        else
        {
            result = new DeploymentTargetPublicIngressDto(
                target.Id,
                target.WorkloadConfiguration.Revision,
                target.WorkloadConfiguration.Ingress!.IngressClassName,
                target.WorkloadConfiguration.Ingress.ClusterIssuerName,
                target.WorkloadConfiguration.Ingress.PublicBaseDomain!);
        }

        return Task.FromResult(result);
    }

    public Task<Result<
        DeploymentTargetPublicIngressDto,
        DeploymentTargetDomainReservationAdmissionError>> AdmitForDomainReservationAsync(
        string tenantId,
        string deploymentTargetId,
        CancellationToken cancellationToken = default)
    {
        Result<
            DeploymentTargetPublicIngressDto,
            DeploymentTargetDomainReservationAdmissionError> result;
        if (RejectNewWorkloads)
        {
            result = new Err<DeploymentTargetDomainReservationAdmissionError>(
                new DeploymentTargetNewWorkloadsDisabled(deploymentTargetId));
        }
        else if (!_targets.TryGetValue(deploymentTargetId, out var target))
        {
            result = new Err<DeploymentTargetDomainReservationAdmissionError>(
                new DeploymentTargetNotFound(deploymentTargetId));
        }
        else if (target.WorkloadConfiguration.Ingress?.PublicBaseDomain is null)
        {
            result = new Err<DeploymentTargetDomainReservationAdmissionError>(
                new DeploymentTargetPublicIngressUnavailable(deploymentTargetId));
        }
        else
        {
            result = new Ok<DeploymentTargetPublicIngressDto>(
                new DeploymentTargetPublicIngressDto(
                    target.Id,
                    target.WorkloadConfiguration.Revision,
                    target.WorkloadConfiguration.Ingress!.IngressClassName,
                    target.WorkloadConfiguration.Ingress.ClusterIssuerName,
                    target.WorkloadConfiguration.Ingress.PublicBaseDomain));
        }

        return Task.FromResult(result);
    }

    public Task<Result> RegisterAsync(
        string deploymentTargetId,
        DeploymentTargetReferenceOwner owner,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<Result>(Result.Success());

    public Task<Result> UnregisterAsync(
        string deploymentTargetId,
        DeploymentTargetReferenceOwner owner,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<Result>(RejectReferenceRemoval
            ? Failures.Conflict(
                "The test deployment target rejected reference removal.")
            : Result.Success());

    public Task<Result> ApplyChangesAsync(
        DeploymentTargetReferenceOwner owner,
        IReadOnlyCollection<string> addedDeploymentTargetIds,
        IReadOnlyCollection<string> removedDeploymentTargetIds,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<Result>(Result.Success());

    public Task<Result> FenceAsync(
        string deploymentTargetId,
        long expectedWorkloadConfigurationRevision,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        var accepted = _targets.TryGetValue(deploymentTargetId, out var target) &&
                       expectedWorkloadConfigurationRevision ==
                       target.WorkloadConfiguration.Revision;
        return Task.FromResult<Result>(accepted
            ? Result.Success()
            : Failures.Conflict(
                "The test deployment target workload configuration changed."));
    }

    public async Task<Result> FenceManyAsync(
        IReadOnlyList<DeploymentTargetWorkloadConfigurationFenceRequest> requests,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        foreach (var request in requests)
        {
            var result = await FenceAsync(
                request.DeploymentTargetId,
                request.ExpectedWorkloadConfigurationRevision,
                session,
                cancellationToken);
            if (result is not ISuccess)
                return result;
        }

        return Result.Success();
    }
}
