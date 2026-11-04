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
        var current = request.Operation is { } operation
            ? await groups.Find(session, ResourceGroupFilters.ByTenantAndId(request.TenantId, operation.ResourceId))
                .FirstOrDefaultAsync(cancellationToken)
            : null;
        current ??= await groups.Find(session,
            ResourceGroupFilters.ByTechnicalName(request.TenantId, request.Address.Name))
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null)
            return new CurrentDeclarationMissing();
        if (request.Operation is { } accepted && current.Id == accepted.ResourceId &&
            current.Name != request.Address.Name)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());


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
    public async Task<FenceDeclarationOutcome> FenceAsync(FenceDeclarationRequest request,
        IClientSessionHandle session, CancellationToken cancellationToken)
    {
        var current = await groups.Find(session,
            ResourceGroupFilters.ByTenantAndId(request.Operation.Context.TenantId, request.Operation.ResourceId))
            .FirstOrDefaultAsync(cancellationToken);
        var expected = request.Expected.Documents.SingleOrDefault(item =>
            item.DocumentId == $"ResourceGroup/{request.Operation.ResourceId}");
        if (current is null)
            return expected is null ? new DeclarationFenced() : new DeclarationPreconditionsChanged();
        if (expected is null || expected.Revision != current.Revision)
            return new DeclarationPreconditionsChanged();

        var written = await groups.UpdateOneAsync(session,
            ResourceGroupFilters.AtRevisionAndRegistrationFence(current),
            Builders<ResourceGroup>.Update.Inc(item => item.RegistrationFence, 1),
            cancellationToken: cancellationToken);
        return written.ModifiedCount == 1 ? new DeclarationFenced() : new DeclarationPreconditionsChanged();
    }

}
