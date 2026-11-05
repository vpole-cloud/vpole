using System.Collections.Immutable;
using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Application;
using Pole.Features.Secrets.Application.Bindings;
using Pole.Features.Secrets.Application.Bootstrap;
using Pole.Features.Secrets.Application.Lifecycle;
using Pole.Features.Secrets.Application.Managed;
using Pole.Features.Secrets.Application.Mongo;
using Pole.Features.Secrets.Application.Projections;
using Pole.Features.Secrets.Application.References;
using Pole.Features.Secrets.Application.Secrets;
using Pole.Features.Secrets.Application.Values;
using Pole.Features.Secrets.Domain;
using Pole.Features.Secrets.Events;
using Pole.Workloads;

namespace Pole.Features.Secrets.Application.Declarations;

internal sealed class SecretBindingAdoption(
    IMongoCollection<Secret> secrets,
    SecretBindingConsumerCatalog consumers,
    IGitOpsMutations git,
    LoadIsolationScopePlanningSnapshot loadPlanningScope,
    LoadResourceDeploymentPlanningSnapshot loadPlanningPlacements,
    SecretBindingPlanning planning,
    StoreSecretChange publish,
    TimeProvider clock)
{
    private const string Kind = "SecretBinding";

    private ResourceTypeId ConsumerType(SecretBindingDeclaration spec) =>
        consumers.Get(spec.Consumer.Kind).ResourceType;
    public async Task<PreviewLifecycleAdoptionOutcome> PreviewAdoptionAsync(
        PreviewLifecycleAdoptionRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var spec = DeclarationJson.Read<SecretBindingDeclaration>(request.Declaration.Spec);
        var secret = await secrets.Find(session,
                SecretFilters.DeclarationBindingId(request.TenantId, request.ResourceId))
            .FirstOrDefaultAsync(ct);
        var binding = secret?.Bindings.SingleOrDefault(item => item.Id == request.ResourceId);
        var secretId = request.References.SingleOrDefault(item => item.Address == new ResourceAddress("Secret",
            spec.Secret))?.ResourceId;
        var consumerId = request.References.SingleOrDefault(item => item.Address == spec.Consumer)?.ResourceId;
        if (secret is null || binding is null || secretId is null || consumerId is null)
            return SecretBindingPlanning.BindingPlanningBlocked(
                SecretBindingDeclarationError.BindingPrerequisiteUnavailable);

        if (secret.Management is ProviderManagedSecret owner && owner.BindingId == binding.Id ||
            binding.Selection is not AllSecretDeployments || secret.Id != secretId ||
            binding.Consumer.ResourceId != consumerId || binding.Consumer.ResourceType != ConsumerType(spec).Value ||
            binding.Usage != spec.Usage ||
            binding.DeclarationName is { } name && name != request.Declaration.Address.Name)
            return SecretBindingPlanning.BindingPlanningBlocked(SecretBindingDeclarationError.ImmutableBindingChange);

        var named = await secrets.Find(session,
            SecretFilters.DeclarationBindingName(request.TenantId, request.Declaration.Address.Name))
            .FirstOrDefaultAsync(ct);
        if (named?.Bindings.Any(item => item.DeclarationName == request.Declaration.Address.Name &&
                item.Id != binding.Id) == true)
            return SecretBindingPlanning.BindingPlanningBlocked(SecretBindingDeclarationError.ImmutableBindingChange);

        var scope = await loadPlanningScope(new(request.TenantId, secret.ResourceGroupId, secret.IsolationScopeId),
            session, ct);
        var placements = await loadPlanningPlacements(new(request.TenantId, ConsumerType(spec), consumerId),
            session, ct);
        if (scope is not IsolationScopePlanningSnapshot accepted ||
            placements is not ResourceDeploymentPlanningSnapshot placed || placed.Items.Any(item =>
                item.ResourceGroupId != secret.ResourceGroupId || item.IsolationScopeId != secret.IsolationScopeId ||
                item.State == ResourceDeploymentPlanningState.Deleting))
            return SecretBindingPlanning.BindingPlanningBlocked(SecretBindingDeclarationError.BindingScopeUnavailable);

        var desired = SecretBinding.Create(binding.Id, binding.Consumer.ResourceType, consumerId,
            new AllSecretDeployments(), spec.KeyMappings, spec.Usage) with
        { DeclarationName = request.Declaration.Address.Name };
        if (secret.DeclareBinding(desired, clock.GetUtcNow().UtcDateTime) is not Secret)
            return SecretBindingPlanning.BindingPlanningBlocked(SecretBindingDeclarationError.SecretKeysInUse);

        var versions = new DeclarationVersionToken(
            SecretBindingPlanning.Versions(new(secret, consumerId, accepted, placed)));
        return new GitLifecycleAdoptionPreview(new(binding.Id, versions,
                SecretBindingPlanning.Project(secret.Id, binding.Consumer, binding.Usage, binding.KeyMappings),
                DeclarationConfigurationState.Configured),
            SecretBindingPlanning.Project(secret.Id, binding.Consumer, spec.Usage, spec.KeyMappings) with
            { Dependencies = versions },
            [new(new("Secret", spec.Secret), secret.Id, new(SecretPlanningFence.Versions(secret))),
                new(spec.Consumer, consumerId, versions)]);
    }

    public async Task<AdoptLifecycleResourceOutcome> AdoptAsync(
        AdoptLifecycleResourceRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var operation = request.Operation;
        if (!await git.AuthorizeAsync(new(operation.Context.TenantId, Kind, operation.ResourceId,
                Lifecycle: request.Authority, LifecycleEffect: GitLifecycleMutationEffect.Adopt), session, ct))
            return new StaleGitExecution();

        var dependencies = await planning.ReadDependenciesAsync(operation, session, ct);
        if (dependencies is DeclarationReadBlocked blocked)
            return blocked;

        var read = (BindingPlanningDependencies)dependencies.Value;
        var binding = read.Secret.Bindings.SingleOrDefault(item => item.Id == operation.ResourceId);
        if (binding is null || read.Secret.Management is ProviderManagedSecret owner && owner.BindingId == binding.Id ||
            binding.DeclarationName is { } name && name != operation.Declaration.Address.Name)
            return SecretBindingPlanning.BindingPlanningBlocked(SecretBindingDeclarationError.ImmutableBindingChange);

        var fenced = await planning.FenceAsync(new(operation, request.Expected), session, ct);
        if (fenced is DeclarationReadBlocked failure)
            return failure;
        if (fenced is DeclarationPreconditionsChanged changed)
            return changed;

        if (binding.DeclarationName == operation.Declaration.Address.Name)
            return new GitLifecycleResourceAdopted();

        var current = await secrets.Find(session, SecretFilters.Identity(operation.Context.TenantId, read.Secret.Id))
            .SingleAsync(ct);
        var updated = current with
        {
            Bindings = current.Bindings.Select(item => item.Id == binding.Id
                ? item with { DeclarationName = operation.Declaration.Address.Name } : item).ToImmutableArray(),
            Revision = checked(current.Revision + 1),
            UpdatedAtUtc = clock.GetUtcNow().UtcDateTime
        };
        var replaced = await secrets.ReplaceOneAsync(session, SecretFilters.AtPlanningVersion(current), updated,
            cancellationToken: ct);
        if (replaced.ModifiedCount != 1)
            return new DeclarationPreconditionsChanged();

        await publish(session, updated, new SecretBindingAdded(binding.Id), ct);
        return new GitLifecycleResourceAdopted();
    }
}
