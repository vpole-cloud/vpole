using Pole.Features.DeploymentTargets.Contracts;
using Pole.ResultType;

namespace Pole.Tests.Integration.Infrastructure;

internal sealed class TestDeploymentTargetLookup : IDeploymentTargetLookup
{
    private int _rejectNewWorkloads;

    private static readonly DeploymentTargetLookupDto Target = new(
        "507f1f77bcf86cd799439099",
        "Integration test cluster",
        "test:kubernetes:integration",
        "InCluster",
        ConnectionVersion: 1,
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
}
