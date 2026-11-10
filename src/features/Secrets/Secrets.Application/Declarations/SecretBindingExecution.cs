using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Application.Bindings;
using Pole.Features.Secrets.Application.Mongo;
using Pole.Features.Secrets.Application.Values;
using Pole.Features.Secrets.Domain;
using Pole.Features.Secrets.Events;
using Pole.Workloads;

namespace Pole.Features.Secrets.Application.Declarations;

internal sealed class SecretBindingExecution(
    IMongoCollection<Secret> secrets,
    SecretBindingConsumerCatalog consumers,
    IGitOpsMutations git,
    AdmitResourceScopeReference admitScope,
    AdmitResourceDeploymentReferences admitDeployments,
    StoreSecretChange publish,
    TimeProvider clock
)
{
    private ResourceTypeId ConsumerType(SecretBindingDeclaration spec) =>
        consumers.Get(spec.Consumer.Kind).ResourceType;

    public Task<GitOperationResult> ApplyAsync(GitOperation operation, CancellationToken ct) => git.ExecuteAsync(
        operation,
        async (session, token) =>
        {
            var context = operation.Context;
            var spec = DeclarationJson.Read<SecretBindingDeclaration>(operation.Declaration.Spec);
            var id = await git.ResolveAcceptedAsync(
                new ResolveAcceptedGitReferenceRequest(
                    Context: context,
                    ConsumerResourceId: operation.ResourceId,
                    Address: new ResourceAddress("Secret", spec.Secret)),
                session: session,
                ct: token);
            var consumerId = await git.ResolveAcceptedAsync(
                new ResolveAcceptedGitReferenceRequest(
                    Context: context,
                    ConsumerResourceId: operation.ResourceId,
                    Address: spec.Consumer),
                session: session,
                ct: token);

            if (id is null || consumerId is null)
                return new GitOperationResult(
                    operation.ResourceId,
                    new SecretBindingDeclarationRejected(SecretBindingDeclarationError.BindingPrerequisiteUnavailable));

            var secret = await secrets.Find(session, SecretFilters.Identity(context.TenantId, id))
                .FirstOrDefaultAsync(token);

            if (secret is null)
                return new GitOperationResult(
                    operation.ResourceId,
                    new SecretBindingDeclarationRejected(SecretBindingDeclarationError.SecretUnavailable));

            var namedSecret = await secrets.Find(
                    session,
                    SecretFilters.DeclarationBindingName(context.TenantId, operation.Declaration.Address.Name))
                .FirstOrDefaultAsync(token);

            if (namedSecret is not null && namedSecret.Id != id)
                return new GitOperationResult(
                    operation.ResourceId,
                    new SecretBindingDeclarationRejected(SecretBindingDeclarationError.ImmutableBindingChange));

            var binding = SecretBinding.Create(
                    operation.ResourceId,
                    ConsumerType(spec).Value,
                    consumerId,
                    new AllSecretDeployments(),
                    spec.KeyMappings,
                    spec.Usage) with
                {
                    DeclarationName = operation.Declaration.Address.Name,
                };
            var transition = secret.DeclareBinding(binding, clock.GetUtcNow().UtcDateTime);

            return transition switch
            {
                Secret updated => await PersistAsync(updated),
                SecretBindingUsageChanged => Rejected(SecretBindingDeclarationError.ImmutableBindingChange),
                SecretBindingConsumerChanged => Rejected(SecretBindingDeclarationError.ImmutableBindingChange),
                SecretBindingConsumerConflict => Rejected(SecretBindingDeclarationError.ExplicitAdoptionRequired),
                UnknownSecretBindingKeys => Rejected(SecretBindingDeclarationError.SecretKeyUnavailable),
                SecretBindingCapacityExceeded => Rejected(SecretBindingDeclarationError.SecretBindingLimitReached),
                SecretBindingKeysInUse => Rejected(SecretBindingDeclarationError.SecretKeysInUse),
            };

            GitOperationResult Rejected(SecretBindingDeclarationError reason) => new(
                operation.ResourceId,
                new SecretBindingDeclarationRejected(reason));

            async Task<GitOperationResult> PersistAsync(Secret updated)
            {
                if (await admitScope(
                        new AdmitResourceScopeReferenceRequest(
                            TenantId: context.TenantId,
                            ResourceGroupId: secret.ResourceGroupId,
                            IsolationScopeId: secret.IsolationScopeId,
                            Consumer: new ResourceScopeConsumerReference(ConsumerType(spec), consumerId)),
                        session: session,
                        cancellationToken: token) is ResourceScopeReferenceUnavailable || await admitDeployments(
                        new AdmitResourceDeploymentReferencesRequest(
                            TenantId: context.TenantId,
                            ResourceGroupId: secret.ResourceGroupId,
                            IsolationScopeId: secret.IsolationScopeId,
                            ConsumerType: ConsumerType(spec),
                            ConsumerId: consumerId,
                            AllDeployments: true,
                            DeploymentIds: []),
                        session: session,
                        cancellationToken: token) is ResourceDeploymentReferenceUnavailable)
                    return new GitOperationResult(
                        operation.ResourceId,
                        new SecretBindingDeclarationRejected(SecretBindingDeclarationError.BindingScopeUnavailable));

                if (updated == secret)
                    return new GitOperationResult(operation.ResourceId);

                await secrets.ReplaceOneAsync(
                    session,
                    SecretFilters.Identity(context.TenantId, id),
                    updated,
                    cancellationToken: token);
                await publish(session, updated, new SecretBindingAdded(binding.Id), token);

                return new GitOperationResult(operation.ResourceId, Changed: true);
            }
        },
        ct);
}
