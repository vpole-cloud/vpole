using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Domain;
using Pole.ResultType;

namespace Pole.Features.ResourceDeployments.Application.Mutations;

public sealed record ResourceDeploymentMutationCommand(
    string TenantId,
    ResourceTypeId ResourceType,
    string ResourceId);

public interface IResourceDeploymentMutationWriter
{
    Task<Result<ResourceDeployment>> RequestApplyAsync(
        ResourceDeploymentMutationCommand command,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

    Task<Result<ResourceDeployment>> RequestDeletionAsync(
        ResourceDeploymentMutationCommand command,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);
}

public sealed class ResourceDeploymentMutationWriter(
    IResourceDeploymentDbContext dbContext,
    TimeProvider timeProvider)
    : IResourceDeploymentMutationWriter
{
    public async Task<Result<ResourceDeployment>> RequestApplyAsync(
        ResourceDeploymentMutationCommand command,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
        ArgumentNullException.ThrowIfNull(command);

        var loadResult = await LoadSingleAsync(command, session, cancellationToken);
        if (!loadResult.TryGetValue(out var current, out var loadFailure))
            return loadFailure;
        if (current.DesiredOperation == ResourceDeploymentOperation.Delete ||
            current.State is DeletedResourceDeploymentState)
        {
            return Failures.Conflict(
                "A resource deployment pending deletion cannot be reconciled.");
        }

        var runtimeCell = await dbContext.RuntimeCells
            .Find(session, cell =>
                cell.Id == current.RuntimeCellId &&
                cell.TenantId == command.TenantId)
            .FirstOrDefaultAsync(cancellationToken);
        if (runtimeCell is null ||
            !string.Equals(
                runtimeCell.DeploymentTargetId,
                current.ResolvedPlacement.DeploymentTargetId,
                StringComparison.Ordinal) ||
            !string.Equals(
                runtimeCell.KubernetesNamespace,
                current.ResolvedPlacement.KubernetesNamespace,
                StringComparison.Ordinal))
        {
            return Failures.Conflict(
                "The resource deployment runtime-cell placement is unavailable.");
        }

        var updated = current.RequestReconciliation(
            current.ResolvedPlacement,
            runtimeCell.State is ReadyRuntimeCellState,
            timeProvider.GetUtcNow().UtcDateTime);
        return await ReplaceAndEnqueueAsync(
            command,
            current,
            updated,
            session,
            cancellationToken);
    }

    public async Task<Result<ResourceDeployment>> RequestDeletionAsync(
        ResourceDeploymentMutationCommand command,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
        ArgumentNullException.ThrowIfNull(command);

        var loadResult = await LoadSingleAsync(command, session, cancellationToken);
        if (!loadResult.TryGetValue(out var current, out var loadFailure))
            return loadFailure;

        var updated = current.RequestDeletion(
            timeProvider.GetUtcNow().UtcDateTime);
        if (updated.Revision == current.Revision)
            return current;

        return await ReplaceAndEnqueueAsync(
            command,
            current,
            updated,
            session,
            cancellationToken);
    }

    private async Task<Result<ResourceDeployment>> LoadSingleAsync(
        ResourceDeploymentMutationCommand command,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.ResourceType.Value))
            throw new ArgumentException("Resource type is required.", nameof(command));

        var deployments = await dbContext.ResourceDeployments
            .Find(session, deployment =>
                deployment.TenantId == command.TenantId &&
                deployment.ResourceType == command.ResourceType.Value &&
                deployment.ResourceId == command.ResourceId)
            .SortBy(deployment => deployment.Id)
            .Limit(2)
            .ToListAsync(cancellationToken);
        return deployments.Count switch
        {
            0 => Failures.NotFound(
                "Resource deployment not found.",
                command.ResourceId),
            1 => deployments[0],
            _ => Failures.Conflict(
                "The logical resource has multiple deployments and cannot be mutated as a single-target resource.")
        };
    }

    private async Task<Result<ResourceDeployment>> ReplaceAndEnqueueAsync(
        ResourceDeploymentMutationCommand command,
        ResourceDeployment current,
        ResourceDeployment updated,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var replacement = await dbContext.ResourceDeployments.ReplaceOneAsync(
            session,
            Builders<ResourceDeployment>.Filter.And(
                Builders<ResourceDeployment>.Filter.Eq(
                    deployment => deployment.Id,
                    current.Id),
                Builders<ResourceDeployment>.Filter.Eq(
                    deployment => deployment.TenantId,
                    command.TenantId),
                Builders<ResourceDeployment>.Filter.Eq(
                    deployment => deployment.ResourceType,
                    command.ResourceType.Value),
                Builders<ResourceDeployment>.Filter.Eq(
                    deployment => deployment.ResourceId,
                    command.ResourceId),
                Builders<ResourceDeployment>.Filter.Eq(
                    deployment => deployment.Revision,
                    current.Revision)),
            updated,
            cancellationToken: cancellationToken);
        if (replacement.ModifiedCount != 1)
        {
            return Failures.Conflict(
                "The resource deployment changed while the operation was being requested.");
        }

        if (updated.State is PendingResourceDeploymentState)
        {
            await dbContext.StoreEventAsync(
                updated.ToCurrentRequestWrite(),
                session,
                cancellationToken);
        }

        return updated;
    }

    private static void EnsureTransaction(IClientSessionHandle session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.IsInTransaction)
        {
            throw new InvalidOperationException(
                "Resource deployment mutations must run inside the logical resource transaction.");
        }
    }
}
