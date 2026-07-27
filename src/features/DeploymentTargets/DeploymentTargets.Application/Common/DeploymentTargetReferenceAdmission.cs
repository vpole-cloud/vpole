using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Common;

public sealed class DeploymentTargetReferenceAdmission(
    IDeploymentTargetDbContext dbContext,
    TimeProvider timeProvider) : IDeploymentTargetReferenceAdmission
{
    public async Task<Result> RegisterAsync(
        string deploymentTargetId,
        DeploymentTargetReferenceOwner owner,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
        var referenceResult = CreateReference(deploymentTargetId, owner);
        if (!referenceResult.TryGetValue(out var reference, out var validationFailure))
            return validationFailure;

        var fence = await AdvanceFenceAsync(
            deploymentTargetId,
            session,
            requireAdmission: true,
            cancellationToken);
        if (fence is not ISuccess)
            return (Failure)fence;

        var referenceFilter = Builders<DeploymentTargetReference>.Filter.And(
            Builders<DeploymentTargetReference>.Filter.Eq(
                item => item.DeploymentTargetId,
                reference.DeploymentTargetId),
            Builders<DeploymentTargetReference>.Filter.Eq(item => item.OwnerType, reference.OwnerType),
            Builders<DeploymentTargetReference>.Filter.Eq(item => item.OwnerId, reference.OwnerId));
        await dbContext.DeploymentTargetReferences.UpdateOneAsync(
            session,
            referenceFilter,
            Builders<DeploymentTargetReference>.Update
                .SetOnInsert(item => item.Id, reference.Id)
                .SetOnInsert(item => item.DeploymentTargetId, reference.DeploymentTargetId)
                .SetOnInsert(item => item.OwnerType, reference.OwnerType)
                .SetOnInsert(item => item.OwnerId, reference.OwnerId)
                .SetOnInsert(item => item.CreatedAtUtc, reference.CreatedAtUtc),
            new UpdateOptions { IsUpsert = true },
            cancellationToken);
        return Result.Success();
    }

    public async Task<Result> UnregisterAsync(
        string deploymentTargetId,
        DeploymentTargetReferenceOwner owner,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
        var ownerValidation = ValidateOwner(owner);
        if (ownerValidation is not null)
            return ownerValidation;
        if (!ObjectId.TryParse(deploymentTargetId, out _))
            return InvalidTargetId();

        var normalizedOwnerType = owner.Type.Trim();
        var normalizedOwnerId = owner.Id.Trim();
        var referenceFilter = Builders<DeploymentTargetReference>.Filter.And(
            Builders<DeploymentTargetReference>.Filter.Eq(
                item => item.DeploymentTargetId,
                deploymentTargetId),
            Builders<DeploymentTargetReference>.Filter.Eq(item => item.OwnerType, normalizedOwnerType),
            Builders<DeploymentTargetReference>.Filter.Eq(item => item.OwnerId, normalizedOwnerId));
        var referenceExists = await dbContext.DeploymentTargetReferences
            .Find(session, referenceFilter)
            .Project(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (referenceExists is null)
            return Result.Success();

        var fence = await AdvanceFenceAsync(
            deploymentTargetId,
            session,
            requireAdmission: false,
            cancellationToken);
        if (fence is not ISuccess)
            return (Failure)fence;

        await dbContext.DeploymentTargetReferences.DeleteOneAsync(
            session,
            referenceFilter,
            cancellationToken: cancellationToken);
        return Result.Success();
    }

    private Result<DeploymentTargetReference> CreateReference(
        string deploymentTargetId,
        DeploymentTargetReferenceOwner owner)
    {
        var ownerValidation = ValidateOwner(owner);
        if (ownerValidation is not null)
            return ownerValidation;
        if (!ObjectId.TryParse(deploymentTargetId, out _))
            return InvalidTargetId();

        return DeploymentTargetReferenceFactory.Create(
            deploymentTargetId,
            owner.Type,
            owner.Id,
            new DeploymentTargetReferenceFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(),
                timeProvider.GetUtcNow().UtcDateTime));
    }

    private async Task<Result> AdvanceFenceAsync(
        string deploymentTargetId,
        IClientSessionHandle session,
        bool requireAdmission,
        CancellationToken cancellationToken)
    {
        var filter = Builders<DeploymentTarget>.Filter.And(
            Builders<DeploymentTarget>.Filter.Eq(target => target.Id, deploymentTargetId),
            Builders<DeploymentTarget>.Filter.Lt(target => target.ReferenceFence, long.MaxValue));
        if (requireAdmission)
        {
            filter &= Builders<DeploymentTarget>.Filter.And(
                Builders<DeploymentTarget>.Filter.Eq(target => target.Enabled, true),
                Builders<DeploymentTarget>.Filter.Eq(target => target.AcceptsWorkloads, true),
                Builders<DeploymentTarget>.Filter.Eq(
                    target => target.DeletionRequestedAtUtc,
                    null));
        }

        var update = await dbContext.DeploymentTargets.UpdateOneAsync(
            session,
            filter,
            Builders<DeploymentTarget>.Update.Inc(target => target.ReferenceFence, 1),
            cancellationToken: cancellationToken);
        if (update.ModifiedCount == 1)
            return Result.Success();

        return requireAdmission
            ? Failures.Conflict("The deployment target no longer accepts durable references.")
            : Failures.Conflict("The deployment target is unavailable for reference removal.");
    }

    private static ValidationFailure? ValidateOwner(DeploymentTargetReferenceOwner? owner)
    {
        if (owner is null ||
            string.IsNullOrWhiteSpace(owner.Type) ||
            owner.Type.Trim().Length > DeploymentTargetReferenceFactory.MaximumOwnerTypeLength ||
            string.IsNullOrWhiteSpace(owner.Id) ||
            owner.Id.Trim().Length > DeploymentTargetReferenceFactory.MaximumOwnerIdLength)
        {
            return Failures.ValidationFailure(new Dictionary<string, string[]>
            {
                ["Owner"] = ["A valid deployment-target reference owner is required."]
            });
        }

        return null;
    }

    private static ValidationFailure InvalidTargetId() =>
        Failures.ValidationFailure(new Dictionary<string, string[]>
        {
            ["DeploymentTargetId"] = ["Deployment target ID must be a MongoDB ObjectId."]
        });

    private static void EnsureTransaction(IClientSessionHandle session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.IsInTransaction)
        {
            throw new InvalidOperationException(
                "Deployment-target reference admission must run inside the reference owner's MongoDB transaction.");
        }
    }
}
