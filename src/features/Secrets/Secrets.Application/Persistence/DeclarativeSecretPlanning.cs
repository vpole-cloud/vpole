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

        if (current.Management is ProviderManagedSecret)
            return new DeclarationReadBlocked(
                new SecretDeclarationRejected(SecretDeclarationError.ProviderManagedSecret));
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
            var acceptedGroup = await AcceptedScopeAsync(operation, session, cancellationToken);
            if (acceptedGroup is null)
                return new DeclarationReadBlocked(
                    new SecretDeclarationRejected(SecretDeclarationError.ResourceGroupUnavailable));

            var acceptedSpec = DeclarationJson.Read<SecretDeclaration>(request.Declaration.Spec);
            return Project(acceptedGroup.ResourceGroupId, acceptedGroup.IsolationScopeId,
                acceptedSpec.Purpose, acceptedSpec.Keys) with
            { Dependencies = new(ScopeVersions(acceptedGroup)) };
        }

        // Advisory planning can resolve names; execution above uses accepted IDs and transactional owner reads.
        var spec = DeclarationJson.Read<SecretDeclaration>(NormalizeAndValidate(request.Declaration.Spec));
        var address = new ResourceAddress("ResourceGroup", spec.ResourceGroup);
        var groupId = await git.ResolveAsync(new(request.TenantId, address), session, cancellationToken);
        if (groupId is null)
            return new DeclarationReadBlocked(new DeclarationReferenceUnavailable(address));

        string? scopeId = null;
        if (spec.IsolationScope is not null)
        {
            var scopeAddress = new ResourceAddress("IsolationScope", spec.IsolationScope);
            scopeId = await git.ResolveAsync(new(request.TenantId, scopeAddress), session, cancellationToken);
            if (scopeId is null)
                return new DeclarationReadBlocked(new DeclarationReferenceUnavailable(scopeAddress));
        }

        if (await loadPlanningScope(new(request.TenantId, groupId, scopeId), session, cancellationToken)
            is not IsolationScopePlanningSnapshot scope)
            return new DeclarationReadBlocked(new DeclarationReferenceUnavailable(address));

        return Project(groupId, scope.IsolationScopeId, spec.Purpose, spec.Keys) with
        { Dependencies = new(ScopeVersions(scope)) };
    }

    public async Task<FenceDeclarationOutcome> FenceAsync(FenceDeclarationRequest request,
        IClientSessionHandle session, CancellationToken cancellationToken)
    {
        var group = await AcceptedScopeAsync(request.Operation, session, cancellationToken);
        if (group is null || !ScopeVersions(group).All(request.Expected.Documents.Contains))
            return new DeclarationPreconditionsChanged();

        var current = await secrets.Find(session,
                SecretFilters.Identity(request.Operation.Context.TenantId, request.Operation.ResourceId))
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null
                ? request.Expected.Documents.Any(item => item.DocumentId == $"Secret/{request.Operation.ResourceId}")
                : !SecretPlanningFence.Matches(current, request.Expected))
            return new DeclarationPreconditionsChanged();

        if (await fencePlanningScope(group, session, cancellationToken) is IsolationScopePlanningFenceRejected)
            return new DeclarationPreconditionsChanged();
        if (current is not null && !await planningFence.FenceAsync(current, session, cancellationToken))
            return new DeclarationPreconditionsChanged();

        return new DeclarationFenced();
    }

    private async Task<IsolationScopePlanningSnapshot?> AcceptedScopeAsync(GitOperationInput operation,
        IClientSessionHandle session, CancellationToken ct)
    {
        var spec = DeclarationJson.Read<SecretDeclaration>(operation.Declaration.Spec);
        var groupId = await git.ResolveAcceptedAsync(new(operation.Context, operation.ResourceId,
            new("ResourceGroup", spec.ResourceGroup)), session, ct);
        if (groupId is null)
            return null;

        string? scopeId = null;
        if (spec.IsolationScope is not null)
        {
            scopeId = await git.ResolveAcceptedAsync(new(operation.Context, operation.ResourceId,
                new("IsolationScope", spec.IsolationScope)), session, ct);
            if (scopeId is null)
                return null;
        }

        return await loadPlanningScope(new(operation.Context.TenantId, groupId, scopeId), session, ct)
            is IsolationScopePlanningSnapshot scope ? scope : null;
    }

    private static DeclarationDocumentVersion[] ScopeVersions(IsolationScopePlanningSnapshot scope) =>
    [
        new($"ResourceGroup/{scope.ResourceGroupId}", scope.ResourceGroupRevision),
        new($"IsolationScope/{scope.IsolationScopeId}", scope.ScopeRevision)
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
