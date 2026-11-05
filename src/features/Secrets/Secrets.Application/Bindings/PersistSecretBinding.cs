using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
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
using Pole.ResultType;
using Pole.Workloads;

namespace Pole.Features.Secrets.Application.Bindings;

public delegate Task<PersistSecretBindingResult> PersistSecretBinding(
    string tenantId,
    string id,
    SecretBinding binding,
    CancellationToken ct);

public union PersistSecretBindingResult(
    Secret,
    SecretMissing,
    SecretBindingLimitReached,
    SecretConsumerAlreadyBound,
    SecretUnknownKeys,
    SecretReferenceRejected);

internal sealed class MongoPersistSecretBinding(
    IMongoCollection<Secret> secrets,
    MongoRunTransaction transactions,
    AdmitResourceScopeReference admitScope,
    FenceRegisteredResource fenceConsumer,
    AdmitResourceDeploymentReferences admitDeployments,
    StoreSecretChange publish,
    TimeProvider clock)
{
    public Task<PersistSecretBindingResult> InvokeAsync(
        string tenantId,
        string id,
        SecretBinding binding,
        CancellationToken ct)
    {
        binding = SecretIdentity.Binding(binding);
        return transactions.InvokeOutcomeAsync<PersistSecretBindingResult>(async (tx, token) =>
        {
            var current = await secrets
                .Find(tx.Session, SecretFilters.Identity(tenantId, id))
                .FirstOrDefaultAsync(token);
            if (current is null)
                return tx.Abort(new SecretMissing());
            if (current.Bindings.Length >= Secret.MaximumBindings)
                return tx.Abort(new SecretBindingLimitReached());
            if (current.Bindings.Any(x => x.Consumer.ResourceType == binding.Consumer.ResourceType
                && x.Consumer.ResourceId == binding.Consumer.ResourceId))
                return tx.Abort(new SecretConsumerAlreadyBound());
            if (binding.KeyMappings.Keys.Except(current.KeyNames).Any())
                return tx.Abort(new SecretUnknownKeys());

            var type = ResourceTypeId.Create(binding.Consumer.ResourceType);
            if (await fenceConsumer(new(tenantId, type, binding.Consumer.ResourceId), tx.Session, token) is null)
                return tx.Abort(new SecretReferenceRejected());

            if (await admitScope(
                new AdmitResourceScopeReferenceRequest(
                    TenantId: current.TenantId,
                    ResourceGroupId: current.ResourceGroupId,
                    IsolationScopeId: current.IsolationScopeId,
                    Consumer: new ResourceScopeConsumerReference(type, binding.Consumer.ResourceId)),
                session: tx.Session,
                cancellationToken: token) is ResourceScopeReferenceUnavailable ||
                await admitDeployments(
                    new AdmitResourceDeploymentReferencesRequest(
                        TenantId: current.TenantId,
                        ResourceGroupId: current.ResourceGroupId,
                        IsolationScopeId: current.IsolationScopeId,
                        ConsumerType: type,
                        ConsumerId: binding.Consumer.ResourceId,
                        AllDeployments: binding.Selection is AllSecretDeployments,
                        DeploymentIds: binding.Selection is SelectedSecretDeployments selected
                            ? selected.DeploymentIds.ToArray() : []),
                    session: tx.Session,
                    cancellationToken: token) is ResourceDeploymentReferenceUnavailable)
                return tx.Abort(new SecretReferenceRejected());

            var bound = current.Bind(binding, clock.GetUtcNow().UtcDateTime);
            await secrets.ReplaceOneAsync(
                tx.Session,
                SecretFilters.Identity(tenantId, id),
                bound,
                cancellationToken: token);
            await publish(tx.Session, bound, new SecretBindingAdded(binding.Id), token);
            return tx.Commit(bound);
        }, ct);
    }
}
