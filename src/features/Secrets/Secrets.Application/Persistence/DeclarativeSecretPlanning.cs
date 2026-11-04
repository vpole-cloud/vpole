using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Domain;

namespace Pole.Features.Secrets.Application.Persistence;

internal sealed partial class DeclarativeSecretProvider
{
    public async Task<ReadCurrentDeclarationOutcome> ReadCurrentAsync(
        ReadCurrentDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var current = request.Operation is { } operation
            ? await secrets.Find(session, SecretFilters.Identity(request.TenantId, operation.ResourceId))
                .FirstOrDefaultAsync(cancellationToken)
            : null;
        current ??= await secrets.Find(session, SecretFilters.TechnicalName(request.TenantId, request.Address.Name))
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null)
            return new CurrentDeclarationMissing();
        if (request.Operation is { } accepted && current.Id == accepted.ResourceId &&
            current.Name != request.Address.Name)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());


        return new CurrentDeclaration(current.Id, new(SecretPlanningFence.Versions(current)),
            Project(current.ResourceGroupId, current.IsolationScopeId, current.Purpose, current.KeyNames),
            DeclarationConfigurationState.Configured);
    }

    public async Task<ProjectDesiredDeclarationOutcome> ProjectDesiredAsync(
        ProjectDesiredDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        if (request.Operation is { } operation)
        {
            var acceptedGroup = await AcceptedGroupAsync(operation, session, cancellationToken);
            if (acceptedGroup is null)
                return new DeclarationReadBlocked(
                    new SecretDeclarationRejected(SecretDeclarationError.ResourceGroupUnavailable));

            var acceptedSpec = DeclarationJson.Read<SecretDeclaration>(request.Declaration.Spec);
            return Project(acceptedGroup.ResourceGroupId, acceptedGroup.DefaultIsolationScopeId,
                acceptedSpec.Purpose, acceptedSpec.Keys) with
            { Dependencies = new(GroupVersions(acceptedGroup)) };
        }

        // Advisory planning can resolve names; execution above uses accepted IDs and transactional owner reads.
        var spec = DeclarationJson.Read<SecretDeclaration>(NormalizeAndValidate(request.Declaration.Spec));
        var address = new ResourceAddress("ResourceGroup", spec.ResourceGroup);
        var groupId = await git.ResolveAsync(new(request.TenantId, address), session, cancellationToken);
        if (groupId is null)
            return new DeclarationReadBlocked(new DeclarationReferenceUnavailable(address));

        if (await groupOwnership(request.TenantId, groupId, cancellationToken) is not ResourceGroupOwnership group)
            return new DeclarationReadBlocked(new DeclarationReferenceUnavailable(address));

        return Project(groupId, group.DefaultIsolationScopeId, spec.Purpose, spec.Keys);
    }

    public async Task<FenceDeclarationOutcome> FenceAsync(FenceDeclarationRequest request,
        IClientSessionHandle session, CancellationToken cancellationToken)
    {
        var group = await AcceptedGroupAsync(request.Operation, session, cancellationToken);
        if (group is null || !GroupVersions(group).All(request.Expected.Documents.Contains))
            return new DeclarationPreconditionsChanged();

        var current = await secrets.Find(session,
                SecretFilters.Identity(request.Operation.Context.TenantId, request.Operation.ResourceId))
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null
                ? request.Expected.Documents.Any(item => item.DocumentId == $"Secret/{request.Operation.ResourceId}")
                : !SecretPlanningFence.Matches(current, request.Expected))
            return new DeclarationPreconditionsChanged();

        if (await fencePlanningGroup(group, session, cancellationToken) is ResourceGroupPlanningFenceRejected)
            return new DeclarationPreconditionsChanged();
        if (current is not null && !await planningFence.FenceAsync(current, session, cancellationToken))
            return new DeclarationPreconditionsChanged();

        return new DeclarationFenced();
    }

    private async Task<ResourceGroupPlanningSnapshot?> AcceptedGroupAsync(GitOperationInput operation,
        IClientSessionHandle session, CancellationToken ct)
    {
        var spec = DeclarationJson.Read<SecretDeclaration>(operation.Declaration.Spec);
        var id = await git.ResolveAcceptedAsync(new(operation.Context, operation.ResourceId,
            new("ResourceGroup", spec.ResourceGroup)), session, ct);
        return id is not null && await loadPlanningGroup(new(operation.Context.TenantId, id), session, ct)
            is ResourceGroupPlanningSnapshot group ? group : null;
    }

    private static DeclarationDocumentVersion[] GroupVersions(ResourceGroupPlanningSnapshot group) =>
    [
        new($"ResourceGroup/{group.ResourceGroupId}", group.Revision),
        new($"IsolationScope/{group.DefaultIsolationScopeId}", group.ScopeRevision)
    ];

    private static DeclarationProjection Project(
        string resourceGroupId,
        string isolationScopeId,
        string purpose,
        IEnumerable<string> keys) => new(
            DeclarationJson.Write(new { resourceGroupId, isolationScopeId }),
            DeclarationJson.Write(new
            {
                purpose = purpose.Trim(),
                keys = keys.Order(StringComparer.Ordinal).ToArray()
            }));
}
