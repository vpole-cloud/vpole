using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Outbox.Application;
using Pole.Features.Outbox.Application.Mongo;
using Pole.Features.Tenants.Application;
using Pole.Features.Tenants.Application.Consumers;
using Pole.Features.Tenants.Application.Mongo;
using Pole.Features.Tenants.Application.Tenants;
using Pole.Features.Tenants.Domain;
using Pole.Features.Users.Application;
using Pole.Features.Users.Application.Common;
using Pole.Features.Users.Application.Endpoints;
using Pole.Features.Users.Application.Identity;
using Pole.Features.Users.Application.Mongo;
using Pole.Features.Users.Application.Registration;
using Pole.Features.Users.Domain;
using Pole.Features.Users.Events;
using Pole.Platform.Hosting;
using Pole.Server.Extensions;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Users;

[Collection(IntegrationTestCollection.Global4)]
public sealed class UserControlPlaneTests(MongoReplicaSetFixture fixture)
    : MongoIntegrationTestBase(fixture)
{
    private ServiceProvider? _services;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        var services = new ServiceCollection();
        services.AddSingleton(Fixture.Client);
        services.AddSingleton(Fixture.Database);
        services.AddSingleton<MongoRunTransaction>();
        services.AddOutboxPersistence();
        services.AddTenantsFeature();
        services.AddUsersFeature(CreateConfiguration());
        _services = services.BuildServiceProvider(validateScopes: true);
    }

    public override Task DisposeAsync()
    {
        _services?.Dispose();
        return base.DisposeAsync();
    }

    [Fact]
    public void FeatureRegistration_ResolvesTheUserCapabilityGraph()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Fixture.Client);
        services.AddSingleton(Fixture.Database);
        services.AddSingleton<MongoRunTransaction>();
        services.AddOutboxPersistence();
        services.AddUsersFeature(CreateConfiguration());

        Assert.DoesNotContain(services, descriptor =>
            descriptor.ServiceType.Name.Contains("UserDbContext", StringComparison.Ordinal));

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<EnsureUserEmailAvailable>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<PersistCreatedUser>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IIdentityProvider>());
    }

    [Fact]
    public async Task CreateUser_CreatesPoleUserAndIdempotentDefaultTenant()
    {
        var identityProvider = new FakeIdentityProvider("keycloak-user-1");
        using var scope = Services.CreateScope();
        var result = await CreateHandler(scope.ServiceProvider, identityProvider).HandleAsync(
            new CreateUserRequest(
                new CreateUserRequestBody("New.User@Example.com", "New", "User")));

        var user = result switch
        {
            UserDto createdUser => createdUser,
            UserEmailAlreadyExists => throw new Xunit.Sdk.XunitException("Unexpected email conflict."),
            UserIdentityAlreadyExists => throw new Xunit.Sdk.XunitException("Unexpected identity conflict."),
            IdentityUserCreationFailed => throw new Xunit.Sdk.XunitException("Unexpected identity failure.")
        };
        Assert.Equal("new.user@example.com", user.Email);

        var storedUser = await Fixture.DbContext.Users
            .Find(stored => stored.Id == "keycloak-user-1")
            .FirstAsync();
        Assert.Equal(1L, await Fixture.DbContext.CountAllOutboxMessagesAsync("pole.users/v1/user-created"));

        using var consumerScope = Services.CreateScope();
        var consumer = new CreateDefaultTenantOnUserCreatedConsumer(
            consumerScope.ServiceProvider.GetRequiredService<EnsureDefaultTenant>());
        var userCreated = new UserCreatedEvent(
            storedUser.Id,
            storedUser.Email,
            storedUser.FirstName,
            storedUser.LastName,
            storedUser.DefaultTenantId,
            $"{storedUser.Email} personal");
        await consumer.Handle(userCreated);
        await consumer.Handle(userCreated);

        var defaultTenant = await Fixture.DbContext.Tenants
            .Find(tenant => tenant.Id == storedUser.DefaultTenantId)
            .FirstAsync();
        Assert.Equal(
            1,
            await Fixture.DbContext.Tenants.CountDocumentsAsync(
                tenant => tenant.Id == storedUser.DefaultTenantId));
        var owner = Assert.Single(defaultTenant.Members);
        Assert.Equal("keycloak-user-1", owner.UserId);
        Assert.Equal(TenantRole.Owner, owner.Role);
        Assert.True(owner.IsDefault);
    }

    [Fact]
    public async Task CreateUser_IdentityFailureDoesNotCreatePoleRecords()
    {
        var identityProvider = new FakeIdentityProvider("keycloak-user-1")
        {
            CreateError = new IdentityUserCreationFailed()
        };

        using var scope = Services.CreateScope();
        var result = await CreateHandler(scope.ServiceProvider, identityProvider).HandleAsync(
            new CreateUserRequest(new CreateUserRequestBody("user@example.com", null, null)));

        Assert.True(
            result is IdentityUserCreationFailed);
        Assert.Equal(0, await Fixture.DbContext.Users.CountDocumentsAsync(Builders<PoleUser>.Filter.Empty));
        Assert.Equal(0, await Fixture.DbContext.Tenants.CountDocumentsAsync(Builders<Tenant>.Filter.Empty));
        Assert.Equal(0L, await Fixture.DbContext.CountAllOutboxMessagesAsync("pole.users/v1/user-created"));
    }

    [Fact]
    public async Task CreateUser_ExistingEmailDoesNotCreateAnIdentityUser()
    {
        await Fixture.DbContext.Users.InsertOneAsync(
            PoleUserFactory.Create(
                "existing-user",
                "existing@example.com",
                null,
                null,
                "tenant-1",
                DateTime.UtcNow));
        var identityProvider = new FakeIdentityProvider("keycloak-user-1");

        using var scope = Services.CreateScope();
        var result = await CreateHandler(scope.ServiceProvider, identityProvider).HandleAsync(
            new CreateUserRequest(
                new CreateUserRequestBody("Existing@Example.com", null, null)));

        Assert.True(
            result is UserEmailAlreadyExists { Email: "existing@example.com" });
        Assert.Equal(0, identityProvider.CreateCalls);
        Assert.Empty(identityProvider.DeletedUserIds);
    }

    [Fact]
    public async Task CreateUser_MongoConflictAfterIdentityCreateAttemptsCleanup()
    {
        await Fixture.DbContext.Users.InsertOneAsync(
            PoleUserFactory.Create(
                "keycloak-user-1",
                "existing@example.com",
                null,
                null,
                "tenant-1",
                DateTime.UtcNow));
        var identityProvider = new FakeIdentityProvider("keycloak-user-1");

        using var scope = Services.CreateScope();
        var result = await CreateHandler(scope.ServiceProvider, identityProvider).HandleAsync(
            new CreateUserRequest(new CreateUserRequestBody("new@example.com", null, null)));

        Assert.True(
            result is UserIdentityAlreadyExists { UserId: "keycloak-user-1" });
        Assert.Equal(["keycloak-user-1"], identityProvider.DeletedUserIds);
        Assert.Equal(1, await Fixture.DbContext.Users.CountDocumentsAsync(Builders<PoleUser>.Filter.Empty));
        Assert.Equal(0L, await Fixture.DbContext.CountAllOutboxMessagesAsync("pole.users/v1/user-created"));
    }

    [Fact]
    public async Task PersistCreatedUser_ExistingEmailAbortsWithoutWritingUserOrOutbox()
    {
        await Fixture.DbContext.Users.InsertOneAsync(PoleUserFactory.Create(
            "existing-user", "existing@example.com", null, null, "tenant-1", DateTime.UtcNow));
        using var scope = Services.CreateScope();
        var persist = scope.ServiceProvider.GetRequiredService<PersistCreatedUser>();

        var outcome = await persist(new PersistCreatedUserRequest(
            PoleUserFactory.Create("new-user", "existing@example.com", null, null, "tenant-2", DateTime.UtcNow),
            "personal"));

        Assert.True(outcome is CreateUserPersistenceError
        {
            Value: UserEmailAlreadyExists { Email: "existing@example.com" }
        });
        Assert.Equal(1, await Fixture.DbContext.Users.CountDocumentsAsync(Builders<PoleUser>.Filter.Empty));
        Assert.Equal(0L, await Fixture.DbContext.CountAllOutboxMessagesAsync("pole.users/v1/user-created"));
    }

    [Fact]
    public async Task PersistCreatedUser_OutboxFailureRollsBackUserAndPropagatesException()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Fixture.Client);
        services.AddSingleton(Fixture.Database);
        services.AddSingleton<MongoRunTransaction>();
        services.AddUsersFeature(CreateConfiguration());
        var failure = new InvalidOperationException("Injected outbox failure.");
        services.AddSingleton<Pole.Features.Outbox.Contracts.StoreOutboxWrites>(
            (_, _, _) => throw failure);
        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        var persist = scope.ServiceProvider.GetRequiredService<PersistCreatedUser>();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => persist(
            new PersistCreatedUserRequest(
                PoleUserFactory.Create("new-user", "new@example.com", null, null, "tenant-1", DateTime.UtcNow),
                "personal")));

        Assert.Same(failure, thrown);
        Assert.Equal(0, await Fixture.DbContext.Users.CountDocumentsAsync(Builders<PoleUser>.Filter.Empty));
        Assert.Equal(0L, await Fixture.DbContext.CountAllOutboxMessagesAsync("pole.users/v1/user-created"));
    }

    private static CreateUserHandler CreateHandler(
        IServiceProvider provider,
        IIdentityProvider identityProvider)
    {
        return new CreateUserHandler(
            provider.GetRequiredService<EnsureUserEmailAvailable>(),
            provider.GetRequiredService<PersistCreatedUser>(),
            identityProvider,
            NullLogger<CreateUserHandler>.Instance,
            TimeProvider.System);
    }

    private IServiceProvider Services => _services
        ?? throw new InvalidOperationException("User test services are not initialized.");

    private static IConfiguration CreateConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["KeycloakAdmin:BaseUrl"] = "https://keycloak.test",
                ["KeycloakAdmin:Realm"] = "pole",
                ["KeycloakAdmin:AdminClientId"] = "pole-admin",
                ["KeycloakAdmin:AdminClientSecret"] = "test-secret"
            })
            .Build();

    private sealed class FakeIdentityProvider(string userId) : IIdentityProvider
    {
        public IdentityUserCreationFailed? CreateError { get; init; }
        public int CreateCalls { get; private set; }
        public List<string> DeletedUserIds { get; } = [];

        public Task<CreateIdentityUserOutcome> CreateUserAsync(
            CreateIdentityUser user,
            CancellationToken cancellationToken)
        {
            CreateCalls++;
            return Task.FromResult<CreateIdentityUserOutcome>(
                CreateError is null
                    ? new IdentityUserCreated(userId)
                    : CreateError);
        }

        public Task<bool> DeleteUserAsync(
            string deletedUserId,
            CancellationToken cancellationToken)
        {
            DeletedUserIds.Add(deletedUserId);
            return Task.FromResult(true);
        }
    }
}
