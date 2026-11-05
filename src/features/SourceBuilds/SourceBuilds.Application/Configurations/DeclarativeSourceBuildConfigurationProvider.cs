using System.Text.Json;
using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.GitProviders.Contracts;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Application.Mongo;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Configurations;

public sealed record SourceBuildConfigurationDeclaration(
    string Repository,
    string WebService,
    SourceBuildRecipeDeclaration Recipe);

public sealed record SourceBuildRecipeDeclaration(string Dockerfile);

public sealed record SourceBuildConfigurationAssociationMismatch : IDeclarationFailure;

internal sealed class DeclarativeSourceBuildConfigurationProvider(
    IMongoCollection<SourceBuildConfiguration> configurations,
    IGitOpsMutations git,
    LoadSourceRepositoryReference loadRepository,
    FenceSourceRepository fenceRepository,
    LoadSourceBuildTarget loadTarget,
    AdmitSourceBuildWebService admitTarget,
    MongoCreateSourceBuildConfiguration create,
    MongoUpdateSourceBuildConfiguration update,
    TimeProvider timeProvider,
    MongoDeleteSourceBuildConfiguration deletion) : ITransactionalDeclarativeResourceProvider,
        IDeclarativeResourceLifecycleProvider, IDeclarativeLifecycleDependencyProvider,
        IDeclarativeAdoptionPreviewProvider
{
    private const string RepositoryField = "spec.repository";

    private const string WebServiceKind = "WebService";

    private const string WebServiceField = "spec.webService";

    public string Kind => "SourceBuildConfiguration";

    public JsonElement NormalizeAndValidate(JsonElement spec)
    {
        var declaration = DeclarationJson.Read<SourceBuildConfigurationDeclaration>(spec);
        DeclarationJson.Require(DeclarationNames.Valid(declaration.Repository), RepositoryField);
        DeclarationJson.Require(DeclarationNames.Valid(declaration.WebService), WebServiceField);
        DeclarationJson.Require(declaration.Recipe is not null &&
            !string.IsNullOrWhiteSpace(declaration.Recipe.Dockerfile) &&
            declaration.Recipe.Dockerfile.Length <= 100_000, "spec.recipe.dockerfile");
        return DeclarationJson.Write(declaration);
    }

    public DeclarationFacts Facts(JsonElement spec)
    {
        var declaration = DeclarationJson.Read<SourceBuildConfigurationDeclaration>(spec);
        return new(null, [],
            [new(new("SourceRepository", declaration.Repository), RepositoryField),
                new(new(WebServiceKind, declaration.WebService), WebServiceField)], [], [],
            ReferencesShareScope: false);
    }

    public IReadOnlyList<DeclarationStep> Steps(ResourceAddress address, JsonElement spec)
    {
        var declaration = DeclarationJson.Read<SourceBuildConfigurationDeclaration>(spec);
        return [new(DeclarationPhase.Configure,
            [new(new("SourceRepository", declaration.Repository), DeclarationPhase.Prepare),
                new(new(WebServiceKind, declaration.WebService), DeclarationPhase.Prepare)])];
    }

    public string DescribeFailure(IDeclarationFailure failure) => failure switch
    {
        SourceBuildConfigurationAssociationMismatch => nameof(SourceBuildConfigurationAssociationMismatch),
        _ => throw new ArgumentException("The failure belongs to another declaration provider.", nameof(failure))
    };

    public async Task<string?> ResolveAsync(
        ResolveDeclaredResourceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken) =>
        (await configurations.Find(session, SourceBuildConfigurationFilters.ByName(request.TenantId, request.Name))
            .FirstOrDefaultAsync(cancellationToken))?.Id;

    public Task<GitOperationResult> ApplyAsync(GitOperation operation, CancellationToken cancellationToken) =>
        git.ExecuteAsync(operation, async (session, ct) =>
        {
            var declaration = DeclarationJson.Read<SourceBuildConfigurationDeclaration>(operation.Declaration.Spec);
            var references = await ReferencesAsync(operation.Context.TenantId, operation.Declaration, operation.Input,
                session, ct);
            if (references is null)
                return new(operation.ResourceId, new SourceBuildConfigurationAssociationMismatch());

            var current = await configurations.Find(session,
                SourceBuildConfigurationFilters.ById(operation.Context.TenantId, operation.ResourceId))
                .FirstOrDefaultAsync(ct);
            var recipe = SourceBuildRecipe.Create(declaration.Recipe.Dockerfile);
            if (current is null)
            {
                var created = SourceBuildConfigurationFactory.Create(operation.Context.TenantId,
                    operation.Declaration.Address.Name, references.Repository.Id, references.Target.WebServiceId,
                    recipe, new GitSourceBuildConfigurationOrigin(operation.Context.SourceId, operation.Context.RunId),
                    new(operation.ResourceId, timeProvider.GetUtcNow().UtcDateTime));
                return await create.InsertAsync(new(created, references.Repository.Revision), session, ct)
                    is SourceBuildConfiguration
                    ? new(operation.ResourceId, Changed: true)
                    : new(operation.ResourceId, new DeclarationPreconditionsChanged());
            }

            return await update.UpdateAsync(new(current, recipe, timeProvider.GetUtcNow().UtcDateTime), session, ct)
                is SourceBuildConfiguration
                ? new(operation.ResourceId, Changed: current.Recipe != recipe)
                : new(operation.ResourceId, new DeclarationPreconditionsChanged());
        }, cancellationToken);

    private sealed record References(
        SourceRepositoryBuildSnapshot Repository,
        SourceBuildTargetSnapshot Target);

    private async Task<References?> ReferencesAsync(
        string tenantId,
        CompiledDeclaration declaration,
        GitOperationInput? operation,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var spec = DeclarationJson.Read<SourceBuildConfigurationDeclaration>(declaration.Spec);
        var repositoryId = await ResolveAsync(new("SourceRepository", spec.Repository));
        var targetId = await ResolveAsync(new(WebServiceKind, spec.WebService));
        if (repositoryId is null || targetId is null)
            return null;

        var repository = await loadRepository(new(tenantId, repositoryId), session, cancellationToken);
        var target = await loadTarget(new(tenantId, targetId), session, cancellationToken);
        if (repository is not SourceRepositoryBuildSnapshot found || target is not SourceBuildTargetSnapshot web ||
            operation is not null && web.ConfigurationId != operation.ResourceId)
            return null;

        return new(found, web);

        Task<string?> ResolveAsync(ResourceAddress address) => operation is null
            ? git.ResolveAsync(new(tenantId, address), session, cancellationToken)
            : git.ResolveAcceptedAsync(
                new(operation.Context, operation.ResourceId, address), session, cancellationToken);
    }

    private static DeclarationDocumentVersion[] Versions(References references) =>
        [new(SourceRepositoryVersionKeys.Revision(references.Repository.Id), references.Repository.Revision),
            new(DeclarationVersionKeys.Resource(WebServiceKind, references.Target.WebServiceId),
                references.Target.Revision)];

    private static DeclarationProjection Project(string repositoryId, string webServiceId, string dockerfile) =>
        new(DeclarationJson.Write(new { repositoryId, webServiceId }), DeclarationJson.Write(new { dockerfile }));

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
            WebServiceKind =>
                SourceBuildConfigurationFilters.ByWebService(request.TenantId, request.Resource.ResourceId),
            _ => null
        };
        if (filter is null)
            return [];

        var rows = await configurations.Find(session, filter).SortBy(item => item.Id).ToListAsync(cancellationToken);
        return rows.Select(item => new GitLifecycleDependency(new(new(Kind, item.TechnicalName), item.Id),
            request.Resource, LifecycleVersions(item), request.Resource.Address.Kind == WebServiceKind
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
            new ResourceAddress(WebServiceKind, spec.WebService));
        if (repository is null || target is null || await loadRepository(new(request.TenantId, repository.ResourceId),
                session, cancellationToken) is not SourceRepositoryBuildSnapshot source ||
            await loadTarget(new(request.TenantId, target.ResourceId), session, cancellationToken) is not
                SourceBuildTargetSnapshot web || web.ConfigurationId != request.ResourceId)
            return new DeclarationReadBlocked(new SourceBuildConfigurationAssociationMismatch());

        return new GitLifecycleAdoptionPreview(found, Project(source.Id, web.WebServiceId, spec.Recipe.Dockerfile) with
        {
            Dependencies = new([new(SourceRepositoryVersionKeys.Revision(source.Id), source.Revision),
            new(DeclarationVersionKeys.Resource(WebServiceKind, web.WebServiceId), web.Revision)])
        }, request.References);
    }

    private static DeclarationVersionToken LifecycleVersions(SourceBuildConfiguration current) => new(
        [new(SourceBuildConfigurationVersionKeys.Revision(current.Id), current.Revision),
            new(ConfigurationFenceVersionKey(current.Id), current.ReferenceFence),
            new(BuildGenerationVersionKey(current.Id), current.BuildGeneration)]);

    private static bool SameVersions(DeclarationVersionToken left, DeclarationVersionToken right) =>
        left.Documents.OrderBy(item => item.DocumentId, StringComparer.Ordinal)
            .SequenceEqual(right.Documents.OrderBy(item => item.DocumentId, StringComparer.Ordinal));

    public async Task<ReadCurrentDeclarationOutcome> ReadCurrentAsync(
        ReadCurrentDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var current = request.Operation is { } operation
            ? await configurations.Find(session,
                SourceBuildConfigurationFilters.ById(request.TenantId, operation.ResourceId))
                .FirstOrDefaultAsync(cancellationToken) : null;
        current ??= await configurations.Find(session,
            SourceBuildConfigurationFilters.ByName(request.TenantId, request.Address.Name))
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null)
            return new CurrentDeclarationMissing();

        if (current.TechnicalName != request.Address.Name)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());

        return new CurrentDeclaration(current.Id,
            new([new(SourceBuildConfigurationVersionKeys.Revision(current.Id), current.Revision)]),
            Project(current.SourceRepositoryId, current.WebServiceId, current.Recipe.Dockerfile),
            DeclarationConfigurationState.Configured);
    }

    public async Task<ProjectDesiredDeclarationOutcome> ProjectDesiredAsync(
        ProjectDesiredDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var references = await ReferencesAsync(request.TenantId, request.Declaration, request.Operation,
            session, cancellationToken);
        if (references is null)
            return new DeclarationReadBlocked(new SourceBuildConfigurationAssociationMismatch());

        var declaration = DeclarationJson.Read<SourceBuildConfigurationDeclaration>(request.Declaration.Spec);
        return Project(references.Repository.Id, references.Target.WebServiceId, declaration.Recipe.Dockerfile)
            with
        {
            Dependencies = new(Versions(references))
        };
    }

    public async Task<FenceDeclarationOutcome> FenceAsync(
        FenceDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var operation = request.Operation;
        var references = await ReferencesAsync(operation.Context.TenantId, operation.Declaration, operation,
            session, cancellationToken);
        if (references is null || !Versions(references).All(request.Expected.Documents.Contains))
            return new DeclarationPreconditionsChanged();

        var current = await configurations.Find(session,
            SourceBuildConfigurationFilters.ById(operation.Context.TenantId, operation.ResourceId))
            .FirstOrDefaultAsync(cancellationToken);
        var expected = request.Expected.Documents.SingleOrDefault(item =>
            item.DocumentId == SourceBuildConfigurationVersionKeys.Revision(operation.ResourceId));
        if (current is null ? expected is not null : expected?.Revision != current.Revision)
            return new DeclarationPreconditionsChanged();

        if (await fenceRepository(new(operation.Context.TenantId, references.Repository.Id,
            references.Repository.Revision), session, cancellationToken) is not SourceRepositoryFenced)
            return new DeclarationPreconditionsChanged();

        if (await admitTarget(new(operation.Context.TenantId, references.Target.WebServiceId, operation.ResourceId,
            SourceBuildAdmissionPurpose.Configure), session, cancellationToken) is not SourceBuildWebServiceAdmitted)
            return new DeclarationPreconditionsChanged();

        if (current is not null)
        {
            var fenced = await configurations.UpdateOneAsync(session,
                SourceBuildConfigurationFilters.AtRevision(current),
                Builders<SourceBuildConfiguration>.Update.Inc(item => item.ReferenceFence, 1),
                cancellationToken: cancellationToken);
            if (fenced.ModifiedCount != 1)
                return new DeclarationPreconditionsChanged();
        }

        return new DeclarationFenced();
    }

    private static string ConfigurationFenceVersionKey(string id) => $"SourceBuildConfigurationFence/{id}";

    private static string BuildGenerationVersionKey(string id) => $"SourceBuildGeneration/{id}";
}
