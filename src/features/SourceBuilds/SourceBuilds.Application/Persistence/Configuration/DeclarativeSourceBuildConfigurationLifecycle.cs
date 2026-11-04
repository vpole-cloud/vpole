using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.GitProviders.Contracts;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Persistence.Configuration;

internal sealed partial class DeclarativeSourceBuildConfigurationProvider
{
    public string DependencyOwner => "SourceBuilds";

    public async Task<ReadLifecycleResourceOutcome> ReadLifecycleAsync(
        ReadLifecycleResourceRequest request, IClientSessionHandle session, CancellationToken cancellationToken)
    {
        var current = await configurations.Find(session,
            SourceBuildConfigurationFilters.ById(request.TenantId, request.Resource.ResourceId))
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

        var result = await deletion.ExecuteAsync(new(request.Authority.TenantId, request.Resource.ResourceId,
            request.Authority), session, cancellationToken);
        return result switch
        {
            SourceBuildConfigurationRemovalRecorded or SourceBuildConfigurationNotFound =>
                new GitLifecycleResourceRemoved(),
            SourceBuildConfigurationHasActiveJobs or SourceBuildConfigurationInUse =>
                new DeclarationReadBlocked(new GitLifecycleDependencyRetained()),
            SourceBuildConfigurationGitOwned => new StaleGitExecution()
        };
    }

    public async Task<IReadOnlyList<GitLifecycleDependency>> ReadDependenciesAsync(
        ReadLifecycleDependenciesRequest request, IClientSessionHandle session, CancellationToken cancellationToken)
    {
        var filter = request.Resource.Address.Kind switch
        {
            "SourceRepository" => SourceBuildConfigurationFilters.BySourceRepository(request.TenantId,
                request.Resource.ResourceId),
            "WebService" => SourceBuildConfigurationFilters.ByWebService(request.TenantId, request.Resource.ResourceId),
            _ => null
        };
        if (filter is null)
            return [];

        var rows = await configurations.Find(session, filter).SortBy(item => item.Id).ToListAsync(cancellationToken);
        return rows.Select(item => new GitLifecycleDependency(new(new(Kind, item.TechnicalName), item.Id),
            request.Resource, LifecycleVersions(item), request.Resource.Address.Kind == "WebService"
                ? GitLifecycleDependencyOrder.AfterRemovalRequested : GitLifecycleDependencyOrder.BeforeRemoval))
            .ToArray();
    }

    public async Task<FenceDeclarationOutcome> FenceDependenciesAsync(
        FenceLifecycleDependenciesRequest request, IClientSessionHandle session, CancellationToken cancellationToken)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Source delivery dependency fencing requires a transaction.");
        var current = await ReadDependenciesAsync(new(request.TenantId, request.Resource), session, cancellationToken);
        if (current.Count != request.Expected.Count || current.Any(item => !request.Expected.Any(expected =>
                expected.Dependent == item.Dependent && expected.Order == item.Order &&
                SameVersions(expected.Version, item.Version))))
            return new DeclarationPreconditionsChanged();
        if (request.Resource.Address.Kind == "SourceRepository")
        {
            var repository = await loadRepository(new(request.TenantId, request.Resource.ResourceId),
                session, cancellationToken);
            if (repository is SourceRepositoryBuildSnapshot source && await fenceRepository(new(request.TenantId,
                    source.Id, source.Revision), session, cancellationToken) is not SourceRepositoryFenced)
                return new DeclarationPreconditionsChanged();
        }
        // WebService deletion/admission writes its root before this contribution is fenced.
        foreach (var dependency in current)
        {
            var row = await configurations.Find(session,
                SourceBuildConfigurationFilters.ById(request.TenantId, dependency.Dependent.ResourceId))
                .SingleAsync(cancellationToken);
            var result = await configurations.UpdateOneAsync(session,
                SourceBuildConfigurationFilters.AtPlanningVersion(row),
                Builders<SourceBuildConfiguration>.Update.Inc(item => item.ReferenceFence, 1),
                cancellationToken: cancellationToken);
            if (result.ModifiedCount != 1)
                return new DeclarationPreconditionsChanged();
        }
        return new DeclarationFenced();
    }

    public async Task<PreviewLifecycleAdoptionOutcome> PreviewAdoptionAsync(
        PreviewLifecycleAdoptionRequest request, IClientSessionHandle session, CancellationToken cancellationToken)
    {
        var current = await ReadCurrentAsync(new(request.TenantId, request.Declaration.Address), session,
            cancellationToken);
        if (current is not CurrentDeclaration found || found.ResourceId != request.ResourceId)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());

        var spec = DeclarationJson.Read<SourceBuildConfigurationDeclaration>(request.Declaration.Spec);
        var repository = request.References.SingleOrDefault(item => item.Address ==
            new ResourceAddress("SourceRepository", spec.Repository));
        var target = request.References.SingleOrDefault(item => item.Address ==
            new ResourceAddress("WebService", spec.WebService));
        if (repository is null || target is null || await loadRepository(new(request.TenantId, repository.ResourceId),
                session, cancellationToken) is not SourceRepositoryBuildSnapshot source ||
            await loadTarget(new(request.TenantId, target.ResourceId), session, cancellationToken) is not
                SourceBuildTargetSnapshot web || web.ConfigurationId != request.ResourceId)
            return new DeclarationReadBlocked(new SourceBuildConfigurationAssociationMismatch());

        return new GitLifecycleAdoptionPreview(found, Project(source.Id, web.WebServiceId, spec.Recipe.Dockerfile) with
        {
            Dependencies = new([new($"SourceRepository/{source.Id}", source.Revision),
            new($"WebService/{web.WebServiceId}", web.Revision)])
        }, request.References);
    }

    private static DeclarationVersionToken LifecycleVersions(SourceBuildConfiguration current) => new(
        [new($"SourceBuildConfiguration/{current.Id}", current.Revision),
            new($"SourceBuildConfigurationFence/{current.Id}", current.ReferenceFence),
            new($"SourceBuildGeneration/{current.Id}", current.BuildGeneration)]);

    private static bool SameVersions(DeclarationVersionToken left, DeclarationVersionToken right) =>
        left.Documents.OrderBy(item => item.DocumentId, StringComparer.Ordinal)
            .SequenceEqual(right.Documents.OrderBy(item => item.DocumentId, StringComparer.Ordinal));
}
