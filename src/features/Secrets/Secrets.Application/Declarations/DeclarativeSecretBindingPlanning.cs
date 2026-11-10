using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Application.Bindings;
using Pole.Features.Secrets.Application.Mongo;
using Pole.Features.Secrets.Application.References;
using Pole.Features.Secrets.Domain;
using Pole.Features.Secrets.Primitives;
using Pole.Workloads;

namespace Pole.Features.Secrets.Application.Declarations;

internal sealed class SecretBindingPlanning(
    IMongoCollection<Secret> secrets,
    SecretBindingConsumerCatalog consumers,
    IGitOpsMutations git,
    LoadIsolationScopePlanningSnapshot loadPlanningScope,
    FenceIsolationScopePlanningSnapshot fencePlanningScope,
    FenceRegisteredResource fenceConsumer,
    LoadResourceDeploymentPlanningSnapshot loadPlanningPlacements,
    FenceResourceDeploymentPlanningSnapshot fencePlanningPlacements,
    SecretPlanningFence planningFence
)
{
    private ResourceTypeId ConsumerType(SecretBindingDeclaration spec) =>
        consumers.Get(spec.Consumer.Kind).ResourceType;

    public async Task<string?> ResolveAsync(
        ResolveDeclaredResourceRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var secret = await secrets.Find(session, SecretFilters.DeclarationBindingName(request.TenantId, request.Name))
            .FirstOrDefaultAsync(ct);

        return secret?.Bindings.SingleOrDefault(binding => binding.DeclarationName == request.Name)?.Id;
    }

    public async Task<ReadCurrentDeclarationOutcome> ReadCurrentAsync(
        ReadCurrentDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var secret = request.Operation is { } operation
            ? await secrets.Find(session, SecretFilters.DeclarationBindingId(request.TenantId, operation.ResourceId))
                .FirstOrDefaultAsync(cancellationToken)
            : null;
        secret ??= await secrets
            .Find(session, SecretFilters.DeclarationBindingName(request.TenantId, request.Address.Name))
            .FirstOrDefaultAsync(cancellationToken);
        if (secret is null)
            return new CurrentDeclarationMissing();

        var binding = request.Operation is { } input
            ? secret.Bindings.SingleOrDefault(item => item.Id == input.ResourceId) ??
            secret.Bindings.Single(item => item.DeclarationName == request.Address.Name)
            : secret.Bindings.Single(item => item.DeclarationName == request.Address.Name);
        if (request.Operation is { } accepted && binding.Id == accepted.ResourceId &&
            binding.DeclarationName != request.Address.Name)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());

        if (binding.Selection is not AllSecretDeployments || !consumers.All.Any(item =>
                item.ResourceType.Value == binding.Consumer.ResourceType && item.Usage == binding.Usage))
            return new DeclarationReadBlocked(
                new SecretBindingDeclarationRejected(SecretBindingDeclarationError.ImmutableBindingChange));

        return new CurrentDeclaration(
            binding.Id,
            new DeclarationVersionToken(SecretPlanningFence.Versions(secret)),
            Project(secret.Id, binding.Consumer, binding.Usage, binding.KeyMappings),
            DeclarationConfigurationState.Configured);
    }

    public async Task<ProjectDesiredDeclarationOutcome> ProjectDesiredAsync(
        ProjectDesiredDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        if (request.Operation is { } operation)
        {
            var read = await ReadDependenciesAsync(operation, session, cancellationToken);
            if (read is DeclarationReadBlocked blocked)
                return blocked;

            var dependencies = (BindingPlanningDependencies)read.Value;
            var accepted = DeclarationJson.Read<SecretBindingDeclaration>(request.Declaration.Spec);
            return Project(
                    dependencies.Secret.Id,
                    new SecretConsumerIdentity(ConsumerType(accepted).Value, dependencies.ConsumerId),
                    accepted.Usage,
                    accepted.KeyMappings) with
                {
                    Dependencies = new DeclarationVersionToken(Versions(dependencies)),
                };
        }

        var spec = DeclarationJson.Read<SecretBindingDeclaration>(
            consumers.NormalizeAndValidate(request.Declaration.Spec));
        var secretAddress = new ResourceAddress("Secret", spec.Secret);
        var secretId = await git.ResolveAsync(
            new ResolveGitResourceRequest(request.TenantId, secretAddress),
            session,
            cancellationToken);
        if (secretId is null)
            return new DeclarationReadBlocked(new DeclarationReferenceUnavailable(secretAddress));

        var consumerAddress = spec.Consumer;
        var consumerId = await git.ResolveAsync(
            new ResolveGitResourceRequest(request.TenantId, consumerAddress),
            session,
            cancellationToken);
        if (consumerId is null)
            return new DeclarationReadBlocked(new DeclarationReferenceUnavailable(consumerAddress));

        return Project(
            secretId,
            new SecretConsumerIdentity(ConsumerType(spec).Value, consumerId),
            spec.Usage,
            spec.KeyMappings);
    }

    public async Task<FenceDeclarationOutcome> FenceAsync(
        FenceDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var read = await ReadDependenciesAsync(request.Operation, session, cancellationToken);
        if (read is DeclarationReadBlocked blocked)
            return blocked;

        var dependencies = (BindingPlanningDependencies)read.Value;
        if (!Versions(dependencies).All(request.Expected.Documents.Contains))
            return new DeclarationPreconditionsChanged();
        var spec = DeclarationJson.Read<SecretBindingDeclaration>(request.Operation.Declaration.Spec);
        if (await fenceConsumer(
                new FenceRegisteredResourceRequest(
                    request.Operation.Context.TenantId,
                    ConsumerType(spec),
                    dependencies.ConsumerId),
                session,
                cancellationToken) is null)
            return new DeclarationPreconditionsChanged();

        if (await fencePlanningScope(dependencies.Scope, session, cancellationToken) is
            IsolationScopePlanningFenceRejected)
            return new DeclarationPreconditionsChanged();
        if (await fencePlanningPlacements(dependencies.Placements, session, cancellationToken) is
            ResourceDeploymentPlanningFenceRejected)
            return new DeclarationPreconditionsChanged();
        if (!await planningFence.FenceAsync(dependencies.Secret, session, cancellationToken))
            return new DeclarationPreconditionsChanged();

        return new DeclarationFenced();
    }

    internal async Task<ReadBindingPlanningOutcome> ReadDependenciesAsync(
        GitOperationInput operation,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var spec = DeclarationJson.Read<SecretBindingDeclaration>(operation.Declaration.Spec);
        var secretId = await git.ResolveAcceptedAsync(
            new ResolveAcceptedGitReferenceRequest(
                operation.Context,
                operation.ResourceId,
                new ResourceAddress("Secret", spec.Secret)),
            session,
            ct);
        var consumerId = await git.ResolveAcceptedAsync(
            new ResolveAcceptedGitReferenceRequest(operation.Context, operation.ResourceId, spec.Consumer),
            session,
            ct);
        if (secretId is null || consumerId is null)
            return BindingPlanningBlocked(SecretBindingDeclarationError.BindingPrerequisiteUnavailable);

        var secret = await secrets.Find(session, SecretFilters.Identity(operation.Context.TenantId, secretId))
            .FirstOrDefaultAsync(ct);
        if (secret is null)
            return BindingPlanningBlocked(SecretBindingDeclarationError.SecretUnavailable);
        if (spec.KeyMappings.Keys.Any(key => !secret.KeyNames.Contains(key)))
            return BindingPlanningBlocked(SecretBindingDeclarationError.SecretKeyUnavailable);

        var scope = await loadPlanningScope(
            new IsolationScopePlanningRequest(
                operation.Context.TenantId,
                secret.ResourceGroupId,
                secret.IsolationScopeId),
            session,
            ct);
        var placements = await loadPlanningPlacements(
            new ResourceDeploymentPlanningRequest(operation.Context.TenantId, ConsumerType(spec), consumerId),
            session,
            ct);
        if (scope is not IsolationScopePlanningSnapshot owned ||
            placements is not ResourceDeploymentPlanningSnapshot placed || placed.Items.Any(item =>
                item.ResourceGroupId != secret.ResourceGroupId || item.IsolationScopeId != secret.IsolationScopeId ||
                item.State == ResourceDeploymentPlanningState.Deleting))
            return BindingPlanningBlocked(SecretBindingDeclarationError.BindingScopeUnavailable);

        return new BindingPlanningDependencies(secret, consumerId, owned, placed);
    }

    internal static DeclarationReadBlocked BindingPlanningBlocked(SecretBindingDeclarationError reason) =>
        new(new SecretBindingDeclarationRejected(reason));

    internal static DeclarationDocumentVersion[] Versions(BindingPlanningDependencies dependencies) =>
    [
        .. SecretPlanningFence.Versions(dependencies.Secret),
        new(
            ResourceGroupVersionKeys.Revision(dependencies.Scope.ResourceGroupId),
            dependencies.Scope.ResourceGroupRevision),
        new(
            ResourceGroupVersionKeys.IsolationScopeRevision(dependencies.Scope.IsolationScopeId),
            dependencies.Scope.ScopeRevision),
        .. dependencies.Placements.Items.Select(item => new DeclarationDocumentVersion(
            ResourceDeploymentVersionKeys.Revision(item.DeploymentId),
            item.Revision)),
    ];

    internal static DeclarationProjection Project(
        string secretId,
        SecretConsumerIdentity consumer,
        SecretBindingUsage usage,
        IEnumerable<KeyValuePair<string, string>> keyMappings) => new(
        DeclarationJson.Write(
            new
            {
                secretId,
                consumerId = consumer.ResourceId,
                consumerType = consumer.ResourceType,
                usage = usage.ToString(),
            }),
        DeclarationJson.Write(
            new
            {
                keyMappings = keyMappings.OrderBy(item => item.Key, StringComparer.Ordinal)
                    .ToDictionary(item => item.Key, item => item.Value),
            }));
}

internal sealed record BindingPlanningDependencies(
    Secret Secret,
    string ConsumerId,
    IsolationScopePlanningSnapshot Scope,
    ResourceDeploymentPlanningSnapshot Placements
);

internal union ReadBindingPlanningOutcome(BindingPlanningDependencies, DeclarationReadBlocked);
