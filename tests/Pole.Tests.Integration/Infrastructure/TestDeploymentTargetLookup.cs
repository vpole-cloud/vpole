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
                "letsencrypt-prod")),
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

    public Task<Result<DeploymentTargetLookupDto>> GetAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<Result<DeploymentTargetLookupDto>>(Target);

    public Task<Result<DeploymentTargetLookupDto>> GetDefaultAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<Result<DeploymentTargetLookupDto>>(Target);

    public Task<Result<DeploymentTargetLookupDto>> ResolveForNewWorkloadAsync(
        string? deploymentTargetId,
        CancellationToken cancellationToken = default) =>
        RejectNewWorkloads
            ? Task.FromResult<Result<DeploymentTargetLookupDto>>(
                Failures.Conflict("The deployment target is not accepting new workloads."))
            : Task.FromResult<Result<DeploymentTargetLookupDto>>(Target);

    public Task<Result<IReadOnlyList<DeploymentTargetLookupDto>>>
        ListEligibleForNewWorkloadAsync(
            CancellationToken cancellationToken = default) =>
        Task.FromResult<Result<IReadOnlyList<DeploymentTargetLookupDto>>>(
            RejectNewWorkloads
                ? Array.Empty<DeploymentTargetLookupDto>()
                : [Target]);

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
        Task.FromResult<Result>(Result.Success());

    public Task<Result> FenceAsync(
        string deploymentTargetId,
        long expectedWorkloadConfigurationRevision,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<Result>(
            string.Equals(
                deploymentTargetId,
                Target.Id,
                StringComparison.Ordinal) &&
            expectedWorkloadConfigurationRevision ==
            Target.WorkloadConfiguration.Revision
                ? Result.Success()
                : Failures.Conflict(
                    "The test deployment target workload configuration changed."));
}
