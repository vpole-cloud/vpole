using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.ResourceGroups.Contracts;

namespace Pole.Features.Secrets.Application.Persistence;

internal sealed partial class DeclarativeSecretProvider
{
    public async Task<ReadCurrentDeclarationOutcome> ReadCurrentAsync(
        ReadCurrentDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var current = await secrets.Find(session,
            SecretFilters.TechnicalName(request.TenantId, request.Address.Name)).FirstOrDefaultAsync(cancellationToken);
        if (current is null)
            return new CurrentDeclarationMissing();

        return new CurrentDeclaration(current.Id, new([new($"Secret/{current.Id}", current.Revision)]),
            Project(current.ResourceGroupId, current.IsolationScopeId, current.Purpose, current.KeyNames),
            DeclarationConfigurationState.Configured);
    }

    public async Task<ProjectDesiredDeclarationOutcome> ProjectDesiredAsync(
        ProjectDesiredDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var spec = DeclarationJson.Read<SecretDeclaration>(NormalizeAndValidate(request.Declaration.Spec));
        var address = new ResourceAddress("ResourceGroup", spec.ResourceGroup);
        var groupId = await git.ResolveAsync(new(request.TenantId, address), session, cancellationToken);
        if (groupId is null)
            return new DeclarationReadBlocked(new DeclarationReferenceUnavailable(address));

        if (await groupOwnership(request.TenantId, groupId, cancellationToken) is not ResourceGroupOwnership group)
            return new DeclarationReadBlocked(new DeclarationReferenceUnavailable(address));

        return Project(groupId, group.DefaultIsolationScopeId, spec.Purpose, spec.Keys);
    }

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
