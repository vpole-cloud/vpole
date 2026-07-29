using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Common;

public sealed class DeploymentTargetLookup(IDeploymentTargetDbContext dbContext)
    : IDeploymentTargetLookup
{
    public async Task<Result<DeploymentTargetLookupDto>> GetAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken = default)
    {
        var idValidation = ValidateId(deploymentTargetId);
        if (idValidation is not null)
            return idValidation;

        var target = await dbContext.DeploymentTargets
            .Find(item => item.Id == deploymentTargetId)
            .FirstOrDefaultAsync(cancellationToken);

        return target is null
            ? Failures.NotFound("Deployment target not found.", deploymentTargetId)
            : target.ToLookupDto();
    }

    public async Task<Result<DeploymentTargetLookupDto>> GetDefaultAsync(
        CancellationToken cancellationToken = default)
    {
        var defaultTargetId = await dbContext.DeploymentTargetSettings
            .Find(settings => settings.Id == DeploymentTargetSettings.SingletonId)
            .Project(settings => settings.DefaultDeploymentTargetId)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(defaultTargetId))
            return Failures.Conflict("The default deployment target is not configured.");

        return await GetAsync(defaultTargetId, cancellationToken);
    }

    public async Task<Result<DeploymentTargetLookupDto>> ResolveForNewWorkloadAsync(
        string? deploymentTargetId,
        CancellationToken cancellationToken = default)
    {
        var lookup = string.IsNullOrWhiteSpace(deploymentTargetId)
            ? await GetDefaultAsync(cancellationToken)
            : await GetAsync(deploymentTargetId, cancellationToken);
        if (!lookup.TryGetValue(out var target, out var failure))
            return failure;

        if (!target.Enabled)
            return Failures.Conflict("The deployment target is disabled.");
        if (target.DeletionRequested)
            return Failures.Conflict("The deployment target is pending deletion.");
        if (!target.AcceptsWorkloads)
            return Failures.Conflict("The deployment target does not accept new workloads.");

        // Health is observational and can recover. Admission is controlled only by the durable
        // Enabled and AcceptsWorkloads flags; a stale degraded observation must not become a latch.
        return target;
    }

    public async Task<Result<IReadOnlyList<DeploymentTargetLookupDto>>>
        ListEligibleForNewWorkloadAsync(
            CancellationToken cancellationToken = default)
    {
        var targets = await dbContext.DeploymentTargets
            .Find(target =>
                target.Enabled &&
                target.AcceptsWorkloads &&
                target.DeletionRequestedAtUtc == null)
            .SortBy(target => target.Id)
            .ToListAsync(cancellationToken);
        return targets
            .Select(target => target.ToLookupDto())
            .ToArray();
    }

    private static ValidationFailure? ValidateId(string? deploymentTargetId)
    {
        if (!string.IsNullOrWhiteSpace(deploymentTargetId) &&
            ObjectId.TryParse(deploymentTargetId, out _))
        {
            return null;
        }

        return Failures.ValidationFailure(new Dictionary<string, string[]>
        {
            ["DeploymentTargetId"] = ["Deployment target ID must be a MongoDB ObjectId."]
        });
    }
}
