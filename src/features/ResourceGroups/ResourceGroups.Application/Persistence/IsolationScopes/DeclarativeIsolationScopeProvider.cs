using System.Text.Json;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.Workloads;

namespace Pole.Features.ResourceGroups.Application.Persistence.IsolationScopes;

public sealed record IsolationScopeDeclaration(string ResourceGroup);

public sealed record IsolationScopeDeclarationUnavailable : IDeclarationFailure;

internal sealed partial class DeclarativeIsolationScopeProvider(
    IMongoCollection<ResourceGroup> groups,
    IMongoCollection<IsolationScope> scopes,
    IGitOpsMutations git,
    LoadResourceGroupPlanningSnapshot loadGroup,
    FenceResourceGroupPlanningSnapshot fenceGroup,
    StoreOutboxWrites outbox,
    TimeProvider clock,
    MongoBeginIsolationScopeDeletion deletion) : ITransactionalDeclarativeResourceProvider,
        IDeclarativeResourceLifecycleProvider, IDeclarativeAdoptionPreviewProvider
{
    public string Kind => "IsolationScope";

    public string DescribeFailure(IDeclarationFailure failure) => failure switch
    {
        IsolationScopeDeclarationUnavailable => nameof(IsolationScopeDeclarationUnavailable),
        _ => throw new ArgumentException("The failure belongs to another declaration provider.", nameof(failure))
    };

    public DeclarationFacts Facts(JsonElement spec) =>
        new(new("ResourceGroup", DeclarationJson.Read<IsolationScopeDeclaration>(spec).ResourceGroup), [], [], [], []);

    public JsonElement NormalizeAndValidate(JsonElement spec)
    {
        var declaration = DeclarationJson.Read<IsolationScopeDeclaration>(spec);
        DeclarationJson.Require(DeclarationNames.Valid(declaration.ResourceGroup), "spec.resourceGroup");
        return DeclarationJson.Write(declaration);
    }

    public IReadOnlyList<DeclarationStep> Steps(ResourceAddress address, JsonElement spec)
    {
        DeclarationJson.Require(ResourceTechnicalName.IsValid(address.Name) &&
            address.Name != IsolationScopeFactory.DefaultScopeName, "metadata.name");
        return
        [
            new(DeclarationPhase.Prepare,
                [new(new("ResourceGroup", DeclarationJson.Read<IsolationScopeDeclaration>(spec).ResourceGroup),
                    DeclarationPhase.Prepare)])
        ];
    }

    public async Task<string?> ResolveAsync(
        ResolveDeclaredResourceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken) =>
        (await scopes.Find(session, IsolationScopeFilters.ByTechnicalName(request.TenantId, request.Name))
            .FirstOrDefaultAsync(cancellationToken))?.Id;

    public Task<GitOperationResult> ApplyAsync(GitOperation operation, CancellationToken cancellationToken) =>
        git.ExecuteAsync(operation, async (session, ct) =>
        {
            var parent = await AcceptedGroupAsync(operation.Input, session, ct);
            if (parent is null)
                return new(operation.ResourceId, new IsolationScopeDeclarationUnavailable());

            var current = await scopes.Find(session,
                IsolationScopeFilters.ByTenantAndId(operation.Context.TenantId, operation.ResourceId))
                .FirstOrDefaultAsync(ct);
            if (current is not null)
                return new(operation.ResourceId, new DeclarationPreconditionsChanged());

            var created = IsolationScopeFactory.CreateAdditional(operation.Context.TenantId,
                parent.ResourceGroupId, operation.Declaration.Address.Name,
                new(operation.ResourceId, clock.GetUtcNow().UtcDateTime));
            var written = await groups.UpdateOneAsync(session,
                ResourceGroupFilters.ActiveAtRevision(parent.TenantId, parent.ResourceGroupId, parent.Revision),
                Builders<ResourceGroup>.Update.Inc(group => group.Revision, 1)
                    .Set(group => group.UpdatedAtUtc, created.CreatedAtUtc), cancellationToken: ct);
            if (written.ModifiedCount != 1)
                return new(operation.ResourceId, new DeclarationPreconditionsChanged());

            try
            {
                await scopes.InsertOneAsync(session, created, cancellationToken: ct);
            }
            catch (MongoWriteException exception) when (MongoDuplicateKey.IsForIndex(exception,
                IsolationScopeIndexes.TenantName, scopes.CollectionNamespace.CollectionName))
            {
                return new(operation.ResourceId, new DeclarationPreconditionsChanged());
            }

            await outbox(session, [OutboxWrite.Create(created.ToCreatedEvent())], ct);
            return new(operation.ResourceId, Changed: true);
        }, cancellationToken);

    private async Task<ResourceGroupPlanningSnapshot?> AcceptedGroupAsync(
        GitOperationInput operation,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var spec = DeclarationJson.Read<IsolationScopeDeclaration>(operation.Declaration.Spec);
        var groupId = await git.ResolveAcceptedAsync(new(operation.Context, operation.ResourceId,
            new("ResourceGroup", spec.ResourceGroup)), session, cancellationToken);
        return groupId is not null && await loadGroup(new(operation.Context.TenantId, groupId),
            session, cancellationToken) is ResourceGroupPlanningSnapshot group ? group : null;
    }

    private static DeclarationDocumentVersion[] GroupVersions(ResourceGroupPlanningSnapshot group) =>
    [
        new($"ResourceGroup/{group.ResourceGroupId}", group.Revision),
        new($"IsolationScope/{group.DefaultIsolationScopeId}", group.ScopeRevision)
    ];

    private static DeclarationProjection Project(string groupId) =>
        new(DeclarationJson.Write(new { resourceGroupId = groupId }), DeclarationJson.Write(new { }));
}
