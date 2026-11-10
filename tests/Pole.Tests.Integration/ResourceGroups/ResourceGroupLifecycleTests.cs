using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceGroups.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.ResourceGroups;

[Collection(IntegrationTestCollection.Tenant3)]
public sealed class ResourceGroupLifecycleTests(
    MongoReplicaSetFixture fixture
) : MediatorIntegrationTestBase(fixture)
{
    private ReviewedLifecycleAuthority? _authorization;

    private ReviewedLifecycleAuthority authorization =>
        _authorization ??= new ReviewedLifecycleAuthority(Fixture.Client, Data.TenantId);

    protected override void ConfigureServices(IServiceCollection services) =>
        services.AddSingleton<IGitOpsMutations>(authorization);

    private IDeclarativeResourceLifecycleProvider Provider(string kind) => Services
        .GetServices<IDeclarativeResourceLifecycleProvider>()
        .Single(item => item.Kind == kind);

    [Fact]
    public async Task Group_removal_and_generated_scope_cleanup_join_the_callers_transaction()
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup("reviewed"));
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var snapshot = Assert.IsType<GitLifecycleResourceSnapshot>(
            (await Provider("ResourceGroup").ReadLifecycleAsync(Read(group), session, default)).Value);
        var removal = new RemoveLifecycleResourceRequest(authorization.Authority, snapshot.Resource, snapshot.Version);
        Assert.IsType<GitLifecycleResourceRemoved>(
            (await Provider("ResourceGroup").RemoveAsync(removal, session, default)).Value);
        await session.AbortTransactionAsync();
        Assert.Equal(1, await DbContext.ResourceGroups.CountDocumentsAsync(item => item.TenantId == Data.TenantId));
        Assert.Equal(1, await DbContext.IsolationScopes.CountDocumentsAsync(item => item.TenantId == Data.TenantId));
        Assert.Equal(
            0,
            await DbContext.CountOutboxMessagesAsync(Data.TenantId, "pole.resource-groups/v1/resource-group-deleted"));

        session.StartTransaction();
        Assert.IsType<GitLifecycleResourceRemoved>(
            (await Provider("ResourceGroup").RemoveAsync(removal, session, default)).Value);
        await session.CommitTransactionAsync();
        Assert.Equal(authorization.Authority, authorization.Observed);
        Assert.Equal(0, await DbContext.ResourceGroups.CountDocumentsAsync(item => item.TenantId == Data.TenantId));
        Assert.Equal(0, await DbContext.IsolationScopes.CountDocumentsAsync(item => item.TenantId == Data.TenantId));
        Assert.Equal(
            1,
            await DbContext.CountOutboxMessagesAsync(Data.TenantId, "pole.resource-groups/v1/resource-group-deleted"));
    }

    [Fact]
    public async Task Group_removal_never_cascades_an_additional_manual_scope()
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup("reviewed"));
        var scope = IsolationScopeFactory.CreateAdditional(
            group.TenantId,
            group.Id,
            "retained",
            IsolationScopeFactory.DefaultOptions);
        await DbContext.IsolationScopes.InsertOneAsync(scope);
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var snapshot = Assert.IsType<GitLifecycleResourceSnapshot>(
            (await Provider("ResourceGroup").ReadLifecycleAsync(Read(group), session, default)).Value);
        var blocked = Assert.IsType<DeclarationReadBlocked>(
            (await Provider("ResourceGroup")
                .RemoveAsync(
                    new RemoveLifecycleResourceRequest(authorization.Authority, snapshot.Resource, snapshot.Version),
                    session,
                    default)).Value);
        Assert.IsType<GitLifecycleDependencyRetained>(blocked.Failure);
        await session.AbortTransactionAsync();
        Assert.Equal(2, await DbContext.IsolationScopes.CountDocumentsAsync(item => item.TenantId == Data.TenantId));
    }

    [Fact]
    public async Task Scope_removal_preserves_default_scope_and_rejects_stale_parent_versions()
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup("reviewed"));
        var scope = IsolationScopeFactory.CreateAdditional(
            group.TenantId,
            group.Id,
            "isolated",
            IsolationScopeFactory.DefaultOptions);
        await DbContext.IsolationScopes.InsertOneAsync(scope);
        using var session = await Fixture.Client.StartSessionAsync();
        var resource = new GitLifecycleResourceReference(new ResourceAddress("IsolationScope", scope.Name), scope.Id);
        var provider = Provider("IsolationScope");
        var snapshot = Assert.IsType<GitLifecycleResourceSnapshot>(
            (await provider.ReadLifecycleAsync(
                new ReadLifecycleResourceRequest(group.TenantId, resource),
                session,
                default)).Value);
        await DbContext.ResourceGroups.UpdateOneAsync(
            item => item.Id == group.Id,
            Builders<ResourceGroup>.Update.Inc(item => item.RegistrationFence, 1));
        session.StartTransaction();
        Assert.IsType<DeclarationPreconditionsChanged>(
            (await provider.RemoveAsync(
                new RemoveLifecycleResourceRequest(authorization.Authority, resource, snapshot.Version),
                session,
                default)).Value);
        await session.AbortTransactionAsync();
        session.StartTransaction();
        snapshot = Assert.IsType<GitLifecycleResourceSnapshot>(
            (await provider.ReadLifecycleAsync(
                new ReadLifecycleResourceRequest(group.TenantId, resource),
                session,
                default)).Value);
        Assert.IsType<GitLifecycleResourceRemoved>(
            (await provider.RemoveAsync(
                new RemoveLifecycleResourceRequest(authorization.Authority, resource, snapshot.Version),
                session,
                default)).Value);
        await session.CommitTransactionAsync();
        Assert.Equal(
            group.DefaultIsolationScopeId,
            (await DbContext.IsolationScopes.Find(item => item.TenantId == Data.TenantId).SingleAsync()).Id);
        Assert.IsType<DeclarationReadBlocked>(
            (await provider.ReadLifecycleAsync(
                new ReadLifecycleResourceRequest(
                    group.TenantId,
                    new GitLifecycleResourceReference(
                        new ResourceAddress("IsolationScope", "default"),
                        group.DefaultIsolationScopeId)),
                session,
                default)).Value);
    }

    [Fact]
    public async Task Reverse_reference_admission_invalidates_an_empty_reviewed_set()
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup("reviewed"));
        var provider = Services.GetServices<IDeclarativeLifecycleDependencyProvider>()
            .Single(item => item.DependencyOwner == "ResourceGroups");
        using var session = await Fixture.Client.StartSessionAsync();
        Assert.Empty(
            await provider.ReadDependenciesAsync(
                new ReadLifecycleDependenciesRequest(group.TenantId, Read(group).Resource),
                session,
                default));
        await DbContext.IsolationScopes.InsertOneAsync(
            IsolationScopeFactory.CreateAdditional(
                group.TenantId,
                group.Id,
                "late",
                IsolationScopeFactory.DefaultOptions));
        session.StartTransaction();
        Assert.IsType<DeclarationPreconditionsChanged>(
            (await provider.FenceDependenciesAsync(
                new FenceLifecycleDependenciesRequest(group.TenantId, Read(group).Resource, []),
                session,
                default)).Value);
        await session.AbortTransactionAsync();
    }

    [Fact]
    public async Task Unknown_registered_resource_type_blocks_lifecycle_instead_of_being_skipped()
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup("reviewed"));
        await DbContext.ResourceRegistrations.InsertOneAsync(
            ResourceRegistrationFactory.Create(
                group.TenantId,
                group.Id,
                ResourceTypeId.Create("unknown/resource"),
                "resource",
                "Unmapped",
                "unknown",
                new ResourceRegistrationFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), DateTime.UtcNow)));
        using var session = await Fixture.Client.StartSessionAsync();
        var blocked = Assert.IsType<DeclarationReadBlocked>(
            (await Provider("ResourceGroup").ReadLifecycleAsync(Read(group), session, default)).Value);
        Assert.IsType<GitLifecycleUnsupported>(blocked.Failure);
    }

    [Fact]
    public async Task Scope_adoption_uses_pinned_parent_and_does_not_change_manual_resources()
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup("reviewed"));
        var scope = IsolationScopeFactory.CreateAdditional(
            group.TenantId,
            group.Id,
            "isolated",
            IsolationScopeFactory.DefaultOptions);
        await DbContext.IsolationScopes.InsertOneAsync(scope);
        var declaration = new CompiledDeclaration(
            new ResourceAddress("IsolationScope", scope.Name),
            DeclarationJson.Write(new { resourceGroup = group.Name }),
            "hash",
            [],
            "scope.yaml",
            1);
        var provider = Assert.IsAssignableFrom<IDeclarativeAdoptionPreviewProvider>(Provider("IsolationScope"));
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var preview = Assert.IsType<GitLifecycleAdoptionPreview>(
            (await provider.PreviewAdoptionAsync(
                new PreviewLifecycleAdoptionRequest(
                    group.TenantId,
                    declaration,
                    scope.Id,
                    [new GitPreviewResolvedReference(new ResourceAddress("ResourceGroup", group.Name), group.Id)]),
                session,
                default)).Value);
        Assert.Equal(scope.Id, preview.Current.ResourceId);
        Assert.True(JsonElement.DeepEquals(preview.Current.Projection.Identity, preview.Desired.Identity));
        Assert.Equal(
            group.Revision,
            (await DbContext.ResourceGroups.Find(item => item.TenantId == Data.TenantId).SingleAsync()).Revision);
        Assert.Equal(
            scope.Revision,
            (await DbContext.IsolationScopes.Find(item => item.Id == scope.Id).SingleAsync()).Revision);

        var blocked = await provider.PreviewAdoptionAsync(
            new PreviewLifecycleAdoptionRequest(
                group.TenantId,
                declaration,
                scope.Id,
                [
                    new GitPreviewResolvedReference(
                        new ResourceAddress("ResourceGroup", group.Name),
                        ObjectId.GenerateNewId().ToString()),
                ]),
            session,
            default);
        Assert.IsType<DeclarationReadBlocked>(blocked.Value);
        Assert.IsType<GitLifecycleResourceAbsent>(
            (await Provider("IsolationScope")
                .ReadLifecycleAsync(
                    new ReadLifecycleResourceRequest(
                        ObjectId.GenerateNewId().ToString(),
                        new GitLifecycleResourceReference(new ResourceAddress("IsolationScope", scope.Name), scope.Id)),
                    session,
                    default)).Value);
        await session.CommitTransactionAsync();
    }

    [Fact]
    public async Task Derived_scope_reservation_uses_real_existing_identity_and_new_planned_identity()
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup("reviewed"));
        var provider = Assert.IsAssignableFrom<IDeclarativeDerivedIdentityProvider>(Provider("ResourceGroup"));
        using var session = await Fixture.Client.StartSessionAsync();
        var existing = Assert.Single(await provider.ReserveDerivedIdentitiesAsync(Read(group), session, default));
        Assert.Equal(group.DefaultIsolationScopeId, existing.ResourceId);
        Assert.Equal(GitPreviewReferenceState.Existing, existing.State);
        Assert.NotNull(existing.Version);
        var created = Assert.Single(
            await provider.ReserveDerivedIdentitiesAsync(
                new ReadLifecycleResourceRequest(
                    group.TenantId,
                    new GitLifecycleResourceReference(
                        new ResourceAddress("ResourceGroup", "replacement"),
                        ObjectId.GenerateNewId().ToString())),
                session,
                default));
        Assert.Equal(GitPreviewReferenceState.Planned, created.State);
        Assert.NotEqual(existing.ResourceId, created.ResourceId);
        Assert.Equal(1, await DbContext.IsolationScopes.CountDocumentsAsync(item => item.TenantId == Data.TenantId));
        Assert.Equal(1, await DbContext.ResourceGroups.CountDocumentsAsync(item => item.TenantId == Data.TenantId));
    }

    [Fact]
    public async Task Prepared_reference_is_the_default_scope_created_by_group_apply()
    {
        var provider = Assert.IsAssignableFrom<IDeclarativeResourceProvider>(Provider("ResourceGroup"));
        var id = ObjectId.GenerateNewId().ToString();
        var declaration = new CompiledDeclaration(
            new ResourceAddress("ResourceGroup", "prepared"),
            DeclarationJson.Write(new { }),
            "hash",
            [new DeclarationStep(DeclarationPhase.Prepare, [])],
            "group.yaml",
            1);
        var authority = authorization.Authority;
        var input = new GitOperationInput(
            new GitMutationContext(
                Data.TenantId,
                authority.SourceId,
                authority.RunId,
                1,
                "https://example.test/repository",
                new GitExecutionAuthority(1, 1, authority.WorkerId, 1, new string('a', 40))),
            declaration,
            DeclarationPhase.Prepare,
            id);
        var reserved = ObjectId.GenerateNewId().ToString();
        await authorization.PinReferenceAsync(
            new PinGitReferenceRequest(
                input.Context,
                id,
                new GitReferenceTarget(new ResourceAddress("DefaultIsolationScope", "prepared"), reserved)),
            default);
        Assert.IsType<DeclarationReferencesPrepared>(
            (await ((IDeclarativeReferencePreparation)provider).PrepareReferencesAsync(input, default)).Value);
        var created = await provider.ApplyAsync(
            new GitOperation(
                input,
                new DeclarationExecutionPlan(
                    1,
                    new CreateDeclarationPlan(
                        new DeclarationProjection(DeclarationJson.Write(new { }), declaration.Spec)))),
            default);
        Assert.True(created.Applied);
        var group = await DbContext.ResourceGroups.Find(item => item.Id == id).SingleAsync();
        Assert.Equal(reserved, group.DefaultIsolationScopeId);
        Assert.Equal(
            reserved,
            (await DbContext.IsolationScopes.Find(item => item.TenantId == Data.TenantId).SingleAsync()).Id);
    }

    private static ReadLifecycleResourceRequest Read(ResourceGroup group) => new(
        group.TenantId,
        new GitLifecycleResourceReference(new ResourceAddress("ResourceGroup", group.Name), group.Id));
}
