using System.Text.Json;
using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;

namespace Pole.Features.GitOps.Application;

// Read-only planning. The executor must revalidate versions and ownership transactionally before applying it.
public sealed class DeclarativeResourcePlanner(DeclarativeProviderRegistry providers, IGitOpsMutations ownership)
{
    public async Task<ResourceDeclarationPlan> PlanAsync(
        PlanDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var declaration = request.Declaration;
        if (providers.Find(declaration.Address.Kind) is not IDeclarativeResourcePlanningProvider provider)
            return new BlockedDeclarationPlan(new DeclarationPlanningUnsupported(declaration.Address.Kind));

        declaration = declaration with { Spec = provider.NormalizeAndValidate(declaration.Spec) };
        var current = await provider.ReadCurrentAsync(
            new(request.Scope.TenantId, declaration.Address), session, cancellationToken);
        var desired = await provider.ProjectDesiredAsync(
            new(request.Scope.TenantId, declaration), session, cancellationToken);

        return desired switch
        {
            DeclarationReadBlocked blocked => new BlockedDeclarationPlan(blocked.Failure),
            DeclarationProjection projection => current switch
            {
                DeclarationReadBlocked blocked => new BlockedDeclarationPlan(blocked.Failure),
                CurrentDeclarationMissing => request.Scope.AcceptedResourceId is null
                    ? new CreateDeclarationPlan(projection)
                    : new BlockedDeclarationPlan(new DeclarationIdentityChanged()),
                CurrentDeclaration found => await PlanExistingAsync(request, found, projection, cancellationToken)
            }
        };
    }

    private async Task<ResourceDeclarationPlan> PlanExistingAsync(
        PlanDeclarationRequest request,
        CurrentDeclaration current,
        DeclarationProjection desired,
        CancellationToken cancellationToken)
    {
        if (request.Scope.AcceptedResourceId is { } id && id != current.ResourceId)
            return new BlockedDeclarationPlan(new DeclarationIdentityChanged());

        var status = await ownership.StatusAsync(new(request.Scope.TenantId, request.Declaration.Address.Kind,
            current.ResourceId), cancellationToken);
        if (status is null)
            return new AdoptionDeclarationPlan(current, desired);

        if (status.SourceId != request.Scope.SourceId)
            return new BlockedDeclarationPlan(new GitDeclarationOwnershipConflict());

        if (!JsonElement.DeepEquals(current.Projection.Identity, desired.Identity))
            return new ReplacementDeclarationPlan(current, desired);

        return current.State == DeclarationConfigurationState.Configured &&
            JsonElement.DeepEquals(current.Projection.Configuration, desired.Configuration)
            ? new NoOpDeclarationPlan(current)
            : new UpdateDeclarationPlan(current, desired);
    }
}
