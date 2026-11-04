using System.Text.Json;
using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.GitProviders.Contracts;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Persistence.Configuration;

public sealed record SourceBuildConfigurationDeclaration(
    string Repository,
    string WebService,
    SourceBuildRecipeDeclaration Recipe);

public sealed record SourceBuildRecipeDeclaration(string Dockerfile);

public sealed record SourceBuildConfigurationAssociationMismatch : IDeclarationFailure;

internal sealed partial class DeclarativeSourceBuildConfigurationProvider(
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
    public string Kind => "SourceBuildConfiguration";

    public JsonElement NormalizeAndValidate(JsonElement spec)
    {
        var declaration = DeclarationJson.Read<SourceBuildConfigurationDeclaration>(spec);
        DeclarationJson.Require(DeclarationNames.Valid(declaration.Repository), "spec.repository");
        DeclarationJson.Require(DeclarationNames.Valid(declaration.WebService), "spec.webService");
        DeclarationJson.Require(declaration.Recipe is not null &&
            !string.IsNullOrWhiteSpace(declaration.Recipe.Dockerfile) &&
            declaration.Recipe.Dockerfile.Length <= 100_000, "spec.recipe.dockerfile");
        return DeclarationJson.Write(declaration);
    }

    public DeclarationFacts Facts(JsonElement spec)
    {
        var declaration = DeclarationJson.Read<SourceBuildConfigurationDeclaration>(spec);
        return new(null, [],
            [new(new("SourceRepository", declaration.Repository), "spec.repository"),
                new(new("WebService", declaration.WebService), "spec.webService")], [], [],
            ReferencesShareScope: false);
    }

    public IReadOnlyList<DeclarationStep> Steps(ResourceAddress address, JsonElement spec)
    {
        var declaration = DeclarationJson.Read<SourceBuildConfigurationDeclaration>(spec);
        return [new(DeclarationPhase.Configure,
            [new(new("SourceRepository", declaration.Repository), DeclarationPhase.Prepare),
                new(new("WebService", declaration.WebService), DeclarationPhase.Prepare)])];
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
        var targetId = await ResolveAsync(new("WebService", spec.WebService));
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
        [new($"SourceRepository/{references.Repository.Id}", references.Repository.Revision),
            new($"WebService/{references.Target.WebServiceId}", references.Target.Revision)];

    private static DeclarationProjection Project(string repositoryId, string webServiceId, string dockerfile) =>
        new(DeclarationJson.Write(new { repositoryId, webServiceId }), DeclarationJson.Write(new { dockerfile }));
}
