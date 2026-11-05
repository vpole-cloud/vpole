using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.Secrets.Application;
using Pole.Features.Secrets.Application.Bootstrap;
using Pole.Features.Secrets.Application.Declarations;
using Pole.Features.Secrets.Application.Lifecycle;
using Pole.Features.Secrets.Application.Managed;
using Pole.Features.Secrets.Application.Mongo;
using Pole.Features.Secrets.Application.Projections;
using Pole.Features.Secrets.Application.References;
using Pole.Features.Secrets.Application.Secrets;
using Pole.Features.Secrets.Application.Values;
using Pole.Features.Secrets.Domain;
using Pole.Features.Secrets.Events;

namespace Pole.Features.Secrets.Application.Bindings;

public delegate Task<RemoveSecretBindingResult> RemoveSecretBinding(
    string tenantId,
    string id,
    string bindingId,
    CancellationToken ct);

public union RemoveSecretBindingResult(
    Secret,
    SecretMissing,
    SecretManagedByProvider,
    SecretGitOwned);

public sealed record RemoveSecretBindingMutation(
    string TenantId,
    string SecretId,
    string BindingId,
    DeclarationVersionToken? Expected,
    GitLifecycleAuthority? Authority);

internal sealed class MongoRemoveSecretBinding(
    IMongoCollection<Secret> secrets,
    MongoRunTransaction transactions,
    StoreSecretChange publish,
    TimeProvider clock,
    IGitOpsMutations git)
{
    public Task<RemoveSecretBindingResult> InvokeAsync(
        string tenantId,
        string id,
        string bindingId,
        CancellationToken ct)
    {
        return transactions.InvokeOutcomeAsync<RemoveSecretBindingResult>(async (tx, token) =>
        {
            var result = await ExecuteAsync(new(tenantId, id, SecretIdentity.ObjectId(bindingId), null, null),
                tx.Session, token);
            return result is Secret ? tx.Commit(result) : tx.Abort(result);
        }, ct);
    }

    public async Task<RemoveSecretBindingResult> ExecuteAsync(
        RemoveSecretBindingMutation request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Secret binding removal requires a transaction.");

        var current = await secrets.Find(session, SecretFilters.Identity(request.TenantId, request.SecretId))
            .FirstOrDefaultAsync(ct);
        if (current is null)
            return new SecretMissing();
        if (request.Expected is { } expected && !SecretPlanningFence.Matches(current, expected))
            return new SecretGitOwned();
        if (!await git.AuthorizeAsync(new(request.TenantId, "SecretBinding", request.BindingId,
                Lifecycle: request.Authority,
                LifecycleEffect: request.Authority is null ? null : GitLifecycleMutationEffect.Remove), session, ct))
            return new SecretGitOwned();
        if (current.Management is ProviderManagedSecret owner && owner.BindingId == request.BindingId)
            return new SecretManagedByProvider();
        if (current.Bindings.All(item => item.Id != request.BindingId))
            return current;

        var detached = current.Detach(request.BindingId, clock.GetUtcNow().UtcDateTime);
        var replaced = await secrets.ReplaceOneAsync(session, SecretFilters.AtPlanningVersion(current), detached,
            cancellationToken: ct);
        if (replaced.ModifiedCount != 1)
            return new SecretGitOwned();
        await publish(session, detached, new SecretBindingRemoved(request.BindingId), ct);
        return detached;
    }
}
