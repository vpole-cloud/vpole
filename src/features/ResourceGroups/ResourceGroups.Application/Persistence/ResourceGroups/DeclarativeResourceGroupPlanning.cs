using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.ResourceGroups.Domain;

namespace Pole.Features.ResourceGroups.Application.Persistence.ResourceGroups;

internal sealed partial class DeclarativeResourceGroupProvider
{
    public async Task<ReadCurrentDeclarationOutcome> ReadCurrentAsync(
        ReadCurrentDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var current = await groups.Find(session,
            ResourceGroupFilters.ByTechnicalName(request.TenantId, request.Address.Name))
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null)
            return new CurrentDeclarationMissing();

        if (current.Lifecycle is DeletingResourceGroupLifecycleState)
            return new DeclarationReadBlocked(
                new ResourceGroupDeclarationRejected(ResourceGroupDeclarationError.ResourceDeleting));

        var projection = new DeclarationProjection(DeclarationJson.Write(new { }), NormalizeAndValidate(
            DeclarationJson.Write(new ResourceGroupDeclaration
            {
                Description = current.Description,
                Tags = current.Tags.ToArray()
            })));
        return new CurrentDeclaration(current.Id, new([new($"ResourceGroup/{current.Id}", current.Revision)]),
            projection, DeclarationConfigurationState.Configured);
    }

    public Task<ProjectDesiredDeclarationOutcome> ProjectDesiredAsync(
        ProjectDesiredDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken) => Task.FromResult<ProjectDesiredDeclarationOutcome>(
            new DeclarationProjection(DeclarationJson.Write(new { }), NormalizeAndValidate(request.Declaration.Spec)));
}
