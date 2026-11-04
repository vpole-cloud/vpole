using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.Features.Secrets.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceDeployments.Contracts;

namespace Pole.Features.Secrets.Application.Persistence;

internal sealed partial class DeclarativeSecretBindingProvider
{
    public async Task<ReadCurrentDeclarationOutcome> ReadCurrentAsync(
        ReadCurrentDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var secret = request.Operation is { } operation
            ? await secrets.Find(session, SecretFilters.DeclarationBindingId(request.TenantId, operation.ResourceId))
                .FirstOrDefaultAsync(cancellationToken)
            : null;
        secret ??= await secrets.Find(session,
            SecretFilters.DeclarationBindingName(request.TenantId, request.Address.Name))
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

        if (binding.Selection is not AllSecretDeployments || binding.Consumer.ResourceType != ConsumerType.Value)
            return new DeclarationReadBlocked(
                new SecretBindingDeclarationRejected(SecretBindingDeclarationError.ImmutableBindingChange));

        return new CurrentDeclaration(binding.Id, new(SecretPlanningFence.Versions(secret)),
            Project(secret.Id, binding.Consumer.ResourceId, binding.KeyMappings),
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
            return Project(dependencies.Secret.Id, dependencies.ConsumerId, accepted.KeyMappings) with
            {
                Dependencies = new(Versions(dependencies))
            };
        }

        var spec = DeclarationJson.Read<SecretBindingDeclaration>(NormalizeAndValidate(request.Declaration.Spec));
        var secretAddress = new ResourceAddress("Secret", spec.Secret);
        var secretId = await git.ResolveAsync(new(request.TenantId, secretAddress), session, cancellationToken);
        if (secretId is null)
            return new DeclarationReadBlocked(new DeclarationReferenceUnavailable(secretAddress));

        var consumerAddress = new ResourceAddress("WebService", spec.WebService);
        var consumerId = await git.ResolveAsync(new(request.TenantId, consumerAddress), session, cancellationToken);
        if (consumerId is null)
            return new DeclarationReadBlocked(new DeclarationReferenceUnavailable(consumerAddress));

        return Project(secretId, consumerId, spec.KeyMappings);
    }

    public async Task<FenceDeclarationOutcome> FenceAsync(FenceDeclarationRequest request,
        IClientSessionHandle session, CancellationToken cancellationToken)
    {
        var read = await ReadDependenciesAsync(request.Operation, session, cancellationToken);
        if (read is DeclarationReadBlocked blocked)
            return blocked;

        var dependencies = (BindingPlanningDependencies)read.Value;
        if (!Versions(dependencies).All(request.Expected.Documents.Contains))
            return new DeclarationPreconditionsChanged();
        if (await fencePlanningGroup(dependencies.Group, session, cancellationToken)
            is ResourceGroupPlanningFenceRejected)
            return new DeclarationPreconditionsChanged();
        if (await fencePlanningPlacements(dependencies.Placements, session, cancellationToken)
            is ResourceDeploymentPlanningFenceRejected)
            return new DeclarationPreconditionsChanged();
        if (!await planningFence.FenceAsync(dependencies.Secret, session, cancellationToken))
            return new DeclarationPreconditionsChanged();

        return new DeclarationFenced();
    }

    private async Task<ReadBindingPlanningOutcome> ReadDependenciesAsync(GitOperationInput operation,
        IClientSessionHandle session, CancellationToken ct)
    {
        var spec = DeclarationJson.Read<SecretBindingDeclaration>(operation.Declaration.Spec);
        var secretId = await git.ResolveAcceptedAsync(new(operation.Context, operation.ResourceId,
            new("Secret", spec.Secret)), session, ct);
        var consumerId = await git.ResolveAcceptedAsync(new(operation.Context, operation.ResourceId,
            new("WebService", spec.WebService)), session, ct);
        if (secretId is null || consumerId is null)
            return BindingPlanningBlocked(SecretBindingDeclarationError.BindingPrerequisiteUnavailable);

        var secret = await secrets.Find(session, SecretFilters.Identity(operation.Context.TenantId, secretId))
            .FirstOrDefaultAsync(ct);
        if (secret is null)
            return BindingPlanningBlocked(SecretBindingDeclarationError.SecretUnavailable);
        if (spec.KeyMappings.Keys.Any(key => !secret.KeyNames.Contains(key)))
            return BindingPlanningBlocked(SecretBindingDeclarationError.SecretKeyUnavailable);

        var group = await loadPlanningGroup(new(operation.Context.TenantId, secret.ResourceGroupId), session, ct);
        var placements = await loadPlanningPlacements(new(operation.Context.TenantId, ConsumerType, consumerId),
            session, ct);
        if (group is not ResourceGroupPlanningSnapshot owned ||
            placements is not ResourceDeploymentPlanningSnapshot placed ||
            owned.DefaultIsolationScopeId != secret.IsolationScopeId ||
            placed.Items.Any(item => item.ResourceGroupId != secret.ResourceGroupId ||
                                     item.IsolationScopeId != secret.IsolationScopeId ||
                                     item.State == ResourceDeploymentPlanningState.Deleting))
            return BindingPlanningBlocked(SecretBindingDeclarationError.BindingScopeUnavailable);

        return new BindingPlanningDependencies(secret, consumerId, owned, placed);
    }

    private static DeclarationReadBlocked BindingPlanningBlocked(SecretBindingDeclarationError reason) =>
        new(new SecretBindingDeclarationRejected(reason));

    private static DeclarationDocumentVersion[] Versions(BindingPlanningDependencies dependencies) =>
    [
        .. SecretPlanningFence.Versions(dependencies.Secret),
        new($"ResourceGroup/{dependencies.Group.ResourceGroupId}", dependencies.Group.Revision),
        new($"IsolationScope/{dependencies.Group.DefaultIsolationScopeId}", dependencies.Group.ScopeRevision),
        .. dependencies.Placements.Items.Select(item =>
            new DeclarationDocumentVersion($"ResourceDeployment/{item.DeploymentId}", item.Revision))
    ];

    private static DeclarationProjection Project(
        string secretId,
        string consumerId,
        IEnumerable<KeyValuePair<string, string>> keyMappings) => new(
            DeclarationJson.Write(new { secretId, consumerId }),
            DeclarationJson.Write(new
            {
                keyMappings = keyMappings.OrderBy(item => item.Key, StringComparer.Ordinal)
                    .ToDictionary(item => item.Key, item => item.Value)
            }));
}

internal sealed record BindingPlanningDependencies(
    Secret Secret,
    string ConsumerId,
    ResourceGroupPlanningSnapshot Group,
    ResourceDeploymentPlanningSnapshot Placements);

internal union ReadBindingPlanningOutcome(
    BindingPlanningDependencies,
    DeclarationReadBlocked);
