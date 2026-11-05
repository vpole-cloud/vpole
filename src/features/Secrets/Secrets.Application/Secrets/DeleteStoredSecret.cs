using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Application;
using Pole.Features.Secrets.Application.Bindings;
using Pole.Features.Secrets.Application.Bootstrap;
using Pole.Features.Secrets.Application.Declarations;
using Pole.Features.Secrets.Application.Lifecycle;
using Pole.Features.Secrets.Application.Managed;
using Pole.Features.Secrets.Application.Mongo;
using Pole.Features.Secrets.Application.Projections;
using Pole.Features.Secrets.Application.References;
using Pole.Features.Secrets.Application.Values;
using Pole.Features.Secrets.Domain;
using Pole.Features.Secrets.Events;
using Pole.ResultType;

namespace Pole.Features.Secrets.Application.Secrets;

public delegate Task<DeleteStoredSecretResult> DeleteStoredSecret(
    string tenantId,
    string id,
    CancellationToken ct);

public union DeleteStoredSecretResult(
    Unit,
    SecretMissing,
    SecretBindingsRemain,
    SecretReferenceRejected,
    SecretGitOwned);

public sealed record DeleteStoredSecretMutation(
    string TenantId,
    string SecretId,
    DeclarationVersionToken? Expected,
    GitLifecycleAuthority? Authority);

internal sealed class MongoDeleteStoredSecret(
    IMongoCollection<Secret> secrets,
    IMongoCollection<SecretVersion> versions,
    MongoRunTransaction transactions,
    IResourceRegistrar registrar,
    StoreSecretChange publish,
    IGitOpsMutations git)
{
    public Task<DeleteStoredSecretResult> InvokeAsync(
        string tenantId,
        string id,
        CancellationToken ct) =>
        transactions.InvokeOutcomeAsync<DeleteStoredSecretResult>(async (tx, token) =>
        {
            var outcome = await ExecuteAsync(new(tenantId, id, null, null), tx.Session, token);
            return outcome is Unit ? tx.Commit(outcome) : tx.Abort(outcome);
        }, ct);

    public async Task<DeleteStoredSecretResult> ExecuteAsync(
        DeleteStoredSecretMutation request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Secret deletion requires a transaction.");

        var current = await secrets.Find(session, SecretFilters.Identity(request.TenantId, request.SecretId))
            .FirstOrDefaultAsync(ct);
        if (current is null)
            return new SecretMissing();
        if (request.Expected is { } expected && !SecretPlanningFence.Matches(current, expected))
            return new SecretReferenceRejected();
        if (!await git.AuthorizeAsync(new(request.TenantId, "Secret", request.SecretId,
                Lifecycle: request.Authority,
                LifecycleEffect: request.Authority is null ? null : GitLifecycleMutationEffect.Remove), session, ct))
            return new SecretGitOwned();
        if (current.Bindings.Length != 0)
            return new SecretBindingsRemain();
        if (current.Management is ProviderManagedSecret)
            return new SecretReferenceRejected();

        var unregistered = await registrar.UnregisterAsync(new(current.TenantId, current.ResourceGroupId,
            SecretResource.Type, current.Id), session, ct);
        if (unregistered is ResourceRegistrationMutationError)
            return new SecretReferenceRejected();

        var deleted = await secrets.DeleteOneAsync(session, SecretFilters.AtPlanningVersion(current),
            cancellationToken: ct);
        if (deleted.DeletedCount != 1)
            return new SecretReferenceRejected();
        await versions.DeleteManyAsync(session, SecretFilters.Versions(current.TenantId, current.Id),
            cancellationToken: ct);
        await publish(session, current with { Revision = checked(current.Revision + 1) },
            new SecretDeletedChange(), ct);
        return Unit.Value;
    }
}
