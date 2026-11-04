using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Domain;

namespace Pole.Features.GitProviders.Application.Persistence.Repositories;

internal sealed partial class DeclarativeSourceRepositoryProvider
{
    public async Task<ReadLifecycleResourceOutcome> ReadLifecycleAsync(
        ReadLifecycleResourceRequest request, IClientSessionHandle session, CancellationToken cancellationToken)
    {
        var current = await repositories.Find(session,
            GitProviderFilters.SourceRepositoryByTenantAndId(request.TenantId, request.Resource.ResourceId))
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null)
            return new GitLifecycleResourceAbsent();

        if (request.Resource.Address.Kind != Kind || current.TechnicalName != request.Resource.Address.Name)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());
        return new GitLifecycleResourceSnapshot(request.Resource, LifecycleVersions(current),
            GitLifecyclePresence.Active, GitLifecycleProtection.ConfigurationOnly,
            GitLifecycleVersionSemantics.ObservedDocuments);
    }

    public async Task<RemoveLifecycleResourceOutcome> RemoveAsync(
        RemoveLifecycleResourceRequest request, IClientSessionHandle session, CancellationToken cancellationToken)
    {
        var read = await ReadLifecycleAsync(new(request.Authority.TenantId, request.Resource),
            session, cancellationToken);
        if (read is GitLifecycleResourceAbsent)
            return new GitLifecycleResourceRemoved();

        if (read is DeclarationReadBlocked blocked)
            return blocked;
        if (!SameVersions(((GitLifecycleResourceSnapshot)read.Value).Version, request.Expected))
            return new DeclarationPreconditionsChanged();

        var outcome = await deletion.ExecuteAsync(new(request.Authority.TenantId, request.Resource.ResourceId,
            request.Authority), session, cancellationToken);
        return outcome switch
        {
            SourceRepositoryDeleted or SourceRepositoryNotFound => new GitLifecycleResourceRemoved(),
            SourceRepositoryHasBuildConfiguration => new DeclarationReadBlocked(new GitLifecycleDependencyRetained()),
            SourceRepositoryGitOwned => new StaleGitExecution()
        };
    }

    public async Task<ReadPreviewReferencesOutcome> ReadPreviewReferencesAsync(
        ReadPreviewReferencesRequest request, IClientSessionHandle session, CancellationToken cancellationToken)
    {
        var spec = DeclarationJson.Read<SourceRepositoryDeclaration>(request.Consumer.Spec);
        var address = ConnectionAddress(spec);
        var accepted = request.References.SingleOrDefault(item => item.Address == address);
        var filter = accepted is null
            ? GitProviderFilters.ConnectionByTenantAndName(request.TenantId, spec.Connection)
            : GitProviderFilters.ConnectionByTenantAndId(request.TenantId, accepted.ResourceId);
        var connection = await connections.Find(session, filter).FirstOrDefaultAsync(cancellationToken);
        if (connection is not { Provider: GitProviderKind.GitHub } || connection.TechnicalName != spec.Connection)
            return new DeclarationReadBlocked(new SourceRepositoryDeclarationConnectionUnavailable());
        return new GitPreviewReferencesSnapshot(request.References.Where(item => item.Address != address)
            .Append(new(address, connection.Id, new([ConnectionVersion(connection)]))).ToArray());
    }

    public async Task<PreviewLifecycleAdoptionOutcome> PreviewAdoptionAsync(
        PreviewLifecycleAdoptionRequest request, IClientSessionHandle session, CancellationToken cancellationToken)
    {
        var current = await ReadCurrentAsync(new(request.TenantId, request.Declaration.Address), session,
            cancellationToken);
        if (current is not CurrentDeclaration found || found.ResourceId != request.ResourceId)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());

        var read = await ReadPreviewReferencesAsync(new(request.TenantId, request.Declaration, request.References),
            session, cancellationToken);
        if (read is DeclarationReadBlocked blocked)
            return blocked;

        var references = ((GitPreviewReferencesSnapshot)read.Value).References;
        var spec = DeclarationJson.Read<SourceRepositoryDeclaration>(request.Declaration.Spec);
        var connection = references.Single(item => item.Address == ConnectionAddress(spec));
        return new GitLifecycleAdoptionPreview(found,
            Project(connection.ResourceId, spec.RepositoryId, spec.Branch) with
            { Dependencies = connection.Version }, references);
    }

    private static DeclarationVersionToken LifecycleVersions(SourceRepository repository) => new(
        [new($"SourceRepository/{repository.Id}", repository.Revision),
            new($"SourceRepositoryFence/{repository.Id}", repository.ReferenceFence)]);

    private static bool SameVersions(DeclarationVersionToken left, DeclarationVersionToken right) =>
        left.Documents.OrderBy(item => item.DocumentId, StringComparer.Ordinal)
            .SequenceEqual(right.Documents.OrderBy(item => item.DocumentId, StringComparer.Ordinal));
}
