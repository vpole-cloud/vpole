using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Secrets;

[Collection(IntegrationTestCollection.Tenant4)]
public sealed class SecretLifecycleTests(
    MongoReplicaSetFixture fixture
) : MediatorIntegrationTestBase(fixture)
{
    private ReviewedAuthority? _authorization;
    private ReviewedAuthority authorization => _authorization ??= new ReviewedAuthority(Data.TenantId);
    private IMongoCollection<Secret> Secrets => Services.GetRequiredService<IMongoCollection<Secret>>();

    private IDeclarativeResourceLifecycleProvider Provider(string kind) => Services
        .GetServices<IDeclarativeResourceLifecycleProvider>()
        .Single(item => item.Kind == kind);

    protected override void ConfigureServices(IServiceCollection services) =>
        services.AddSingleton<IGitOpsMutations>(authorization);

    [Fact]
    public async Task Read_is_exact_by_id_and_tenant_and_marks_Secret_as_protected()
    {
        var secret = await Seed();
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var request = new ReadLifecycleResourceRequest(secret.TenantId, Reference(secret));
        var snapshot = Assert.IsType<GitLifecycleResourceSnapshot>(
            (await Provider("Secret").ReadLifecycleAsync(request, session, default)).Value);
        Assert.Equal(GitLifecycleProtection.ProtectedData, snapshot.Protection);
        Assert.IsType<GitLifecycleResourceAbsent>(
            (await Provider("Secret")
                .ReadLifecycleAsync(
                    request with
                    {
                        TenantId = ObjectId.GenerateNewId().ToString(),
                    },
                    session,
                    default)).Value);
        Assert.IsType<GitLifecycleResourceAbsent>(
            (await Provider("Secret")
                .ReadLifecycleAsync(
                    request with
                    {
                        Resource = Reference(secret) with { ResourceId = ObjectId.GenerateNewId().ToString() },
                    },
                    session,
                    default)).Value);
        await session.AbortTransactionAsync();
    }

    [Fact]
    public async Task Joined_Secret_removal_rolls_back_registration_and_event_then_commits_once()
    {
        var secret = await Seed();
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var snapshot = Assert.IsType<GitLifecycleResourceSnapshot>(
            (await Provider("Secret")
                .ReadLifecycleAsync(
                    new ReadLifecycleResourceRequest(secret.TenantId, Reference(secret)),
                    session,
                    default)).Value);
        var request = new RemoveLifecycleResourceRequest(authorization.Authority, Reference(secret), snapshot.Version);
        Assert.IsType<GitLifecycleResourceRemoved>(
            (await Provider("Secret").RemoveAsync(request, session, default)).Value);
        await session.AbortTransactionAsync();
        Assert.Equal(1, await Secrets.CountDocumentsAsync(item => item.Id == secret.Id));
        Assert.Equal(
            1,
            await DbContext.ResourceRegistrations.CountDocumentsAsync(item => item.ResourceId == secret.Id));
        Assert.Equal(0, await DbContext.CountOutboxMessagesAsync(Data.TenantId, "pole.secrets/v1/secret-changed"));
        session.StartTransaction();
        Assert.IsType<GitLifecycleResourceRemoved>(
            (await Provider("Secret").RemoveAsync(request, session, default)).Value);
        await session.CommitTransactionAsync();
        Assert.Equal(0, await Secrets.CountDocumentsAsync(item => item.Id == secret.Id));
        Assert.Equal(
            0,
            await DbContext.ResourceRegistrations.CountDocumentsAsync(item => item.ResourceId == secret.Id));
        Assert.Equal(1, await DbContext.CountOutboxMessagesAsync(Data.TenantId, "pole.secrets/v1/secret-changed"));
    }

    [Fact]
    public async Task Secret_with_binding_is_blocked_without_cascade()
    {
        var secret = await Seed();
        var binding = SecretBinding.Create(Id(), "pole/web-service", Id(), new AllSecretDeployments(), []);
        secret = secret.Bind(binding, DateTime.UtcNow);
        await Secrets.ReplaceOneAsync(item => item.Id == secret.Id, secret);
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var snapshot = Assert.IsType<GitLifecycleResourceSnapshot>(
            (await Provider("Secret")
                .ReadLifecycleAsync(
                    new ReadLifecycleResourceRequest(secret.TenantId, Reference(secret)),
                    session,
                    default)).Value);
        var result = await Provider("Secret")
            .RemoveAsync(
                new RemoveLifecycleResourceRequest(authorization.Authority, Reference(secret), snapshot.Version),
                session,
                default);
        Assert.IsType<GitLifecycleDependencyRetained>(Assert.IsType<DeclarationReadBlocked>(result.Value).Failure);
        await session.AbortTransactionAsync();
        Assert.Single((await Secrets.Find(item => item.Id == secret.Id).SingleAsync()).Bindings);
    }

    [Fact]
    public async Task Binding_removal_preserves_Secret_identity_and_other_bindings_and_rolls_back()
    {
        var secret = await Seed();
        var binding = SecretBinding.Create(Id(), "pole/web-service", Id(), new AllSecretDeployments(), []) with
        {
            DeclarationName = "access",
        };
        var other = SecretBinding.Create(Id(), "pole/web-service", Id(), new AllSecretDeployments(), []);
        secret = secret.Bind(binding, DateTime.UtcNow).Bind(other, DateTime.UtcNow);
        await Secrets.ReplaceOneAsync(item => item.Id == secret.Id, secret);
        var reference = new GitLifecycleResourceReference(new ResourceAddress("SecretBinding", "access"), binding.Id);
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var snapshot = Assert.IsType<GitLifecycleResourceSnapshot>(
            (await Provider("SecretBinding")
                .ReadLifecycleAsync(new ReadLifecycleResourceRequest(secret.TenantId, reference), session, default))
            .Value);
        var request = new RemoveLifecycleResourceRequest(authorization.Authority, reference, snapshot.Version);
        Assert.IsType<GitLifecycleResourceRemoved>(
            (await Provider("SecretBinding").RemoveAsync(request, session, default)).Value);
        await session.AbortTransactionAsync();
        Assert.Equal(2, (await Secrets.Find(item => item.Id == secret.Id).SingleAsync()).Bindings.Length);
        session.StartTransaction();
        Assert.IsType<GitLifecycleResourceRemoved>(
            (await Provider("SecretBinding").RemoveAsync(request, session, default)).Value);
        await session.CommitTransactionAsync();
        var remaining = await Secrets.Find(item => item.Id == secret.Id).SingleAsync();
        Assert.Equal(other.Id, Assert.Single(remaining.Bindings).Id);
        Assert.Equal(secret.CurrentVersion, remaining.CurrentVersion);
    }

    [Fact]
    public async Task Unnamed_binding_read_allows_adoption_but_does_not_authorize_named_deletion()
    {
        var secret = await Seed();
        var binding = SecretBinding.Create(Id(), "pole/web-service", Id(), new AllSecretDeployments(), []);
        secret = secret.Bind(binding, DateTime.UtcNow);
        await Secrets.ReplaceOneAsync(item => item.Id == secret.Id, secret);
        var reference = new GitLifecycleResourceReference(new ResourceAddress("SecretBinding", "new-name"), binding.Id);
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var current = Assert.IsType<GitLifecycleResourceSnapshot>(
            (await Provider("SecretBinding")
                .ReadLifecycleAsync(new ReadLifecycleResourceRequest(secret.TenantId, reference), session, default))
            .Value);
        var rejected = await Provider("SecretBinding")
            .RemoveAsync(
                new RemoveLifecycleResourceRequest(authorization.Authority, reference, current.Version),
                session,
                default);
        Assert.IsType<DeclarationIdentityChanged>(Assert.IsType<DeclarationReadBlocked>(rejected.Value).Failure);
        await session.AbortTransactionAsync();
        Assert.Equal(
            binding.Id,
            Assert.Single((await Secrets.Find(item => item.Id == secret.Id).SingleAsync()).Bindings).Id);
    }

    [Fact]
    public async Task Provider_managed_Secret_cannot_enter_independent_lifecycle()
    {
        var secret = await Seed();
        secret = secret with
        {
            Management = new ProviderManagedSecret(
                "pole/postgresql",
                Id(),
                "credentials",
                Id(),
                KubernetesSecretType.BasicAuthentication,
                ManagedSecretSharing.Shareable),
        };
        await Secrets.ReplaceOneAsync(item => item.Id == secret.Id, secret);
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        Assert.IsType<DeclarationReadBlocked>(
            (await Provider("Secret")
                .ReadLifecycleAsync(
                    new ReadLifecycleResourceRequest(secret.TenantId, Reference(secret)),
                    session,
                    default)).Value);
        await session.AbortTransactionAsync();
    }

    [Theory]
    [InlineData(GitLifecycleAction.Remove)]
    [InlineData(GitLifecycleAction.Replace)]
    public async Task Lifecycle_warnings_describe_stored_version_loss_and_new_replacement_values(
        GitLifecycleAction action)
    {
        var secret = await Seed();
        var provider = Assert.IsAssignableFrom<IDeclarativeLifecycleWarningProvider>(
            Services.GetServices<IDeclarativeResourceProvider>().Single(item => item.Kind == "Secret"));
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var request = new ReadLifecycleWarningsRequest(secret.TenantId, secret.Id, action);
        var codes = await provider.ReadLifecycleWarningsAsync(request, session, default);
        Assert.Contains("SecretStoredVersionsDeleted", codes);
        Assert.Equal(action == GitLifecycleAction.Replace, codes.Contains("ReplacementSecretValuesRequired"));
        Assert.Equal(action == GitLifecycleAction.Replace ? 2 : 1, codes.Count);
        Assert.Empty(await provider.ReadLifecycleWarningsAsync(request with { TenantId = Id() }, session, default));
        Assert.Empty(await provider.ReadLifecycleWarningsAsync(request with { ResourceId = Id() }, session, default));
        await session.CommitTransactionAsync();
        Assert.Equal(
            secret.ToBsonDocument(),
            (await Secrets.Find(item => item.Id == secret.Id).SingleAsync()).ToBsonDocument());
    }

    private async Task<Secret> Seed()
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var secret = SecretFactory.CreateMetadata(
            Id(),
            group.TenantId,
            group.Id,
            group.DefaultIsolationScopeId,
            "lifecycle-secret",
            "test",
            ["password"],
            DateTime.UtcNow);
        await Secrets.InsertOneAsync(secret);
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        Assert.IsType<RegisteredResource>(
            (await Services.GetRequiredService<IResourceRegistrar>()
                .RegisterAsync(
                    new RegisterResourceRequest(
                        secret.TenantId,
                        group.Id,
                        new ResourceRegistrationDescriptor(
                            ResourceTypeId.Create("pole/secret"),
                            secret.Id,
                            secret.Name)),
                    session)).Value);
        await session.CommitTransactionAsync();
        return secret;
    }

    private static string Id() => ObjectId.GenerateNewId().ToString();

    private static GitLifecycleResourceReference Reference(Secret secret) => new(
        new ResourceAddress("Secret", secret.Name),
        secret.Id);

    // Isolate the owning feature's transaction boundary; the GitOps suite exercises real admission.
    private sealed class ReviewedAuthority(
        string tenantId
    ) : IGitOpsMutations
    {
        public GitLifecycleAuthority Authority { get; } = new(
            tenantId,
            "source",
            "run",
            1,
            1,
            1,
            "worker",
            1,
            "preview",
            "reviewed");

        public GitLifecycleAuthority? Observed { get; private set; }

        public Task<bool> AuthorizeAsync(
            AuthorizeGitResourceMutationRequest request,
            IClientSessionHandle session,
            CancellationToken ct)
        {
            Observed = request.Lifecycle;
            return Task.FromResult(
                request.Lifecycle == Authority && request.LifecycleEffect == GitLifecycleMutationEffect.Remove);
        }

        public Task<GitOperationResult> ExecuteAsync(
            GitOperation operation,
            Func<IClientSessionHandle, CancellationToken, Task<GitOperationResult>> mutation,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<GitOperationResult?> BeginAsync(
            GitOperation operation,
            IClientSessionHandle session,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<GitOperationResult> CompleteAsync(
            CompleteGitOperationRequest request,
            IClientSessionHandle session,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<string?> ResolveAsync(
            ResolveGitResourceRequest request,
            IClientSessionHandle session,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<string?> ResolveAcceptedAsync(
            ResolveAcceptedGitReferenceRequest request,
            IClientSessionHandle session,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<PinGitReferenceOutcome> PinReferenceAsync(PinGitReferenceRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, GitResourceStatus>> StatusesAsync(
            LoadGitResourceStatusesRequest request,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<GitResourceStatus?> StatusAsync(LoadGitResourceStatusRequest request, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
