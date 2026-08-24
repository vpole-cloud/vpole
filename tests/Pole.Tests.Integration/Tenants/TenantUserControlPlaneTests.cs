using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants;
using Pole.Features.Tenants.Application.Common;
using Pole.Features.Tenants.Application.Consumers;
using Pole.Features.Tenants.Application.Persistence;
using Pole.Features.Tenants.Application;
using Pole.Features.Tenants.Domain;
using Pole.Features.Tenants.Application.Endpoints;
using Pole.Features.Users.Events;
using Pole.Features.Users.Domain;
using Pole.Features.Users.Application.Endpoints;
using Pole.Features.Users.Application.Identity;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Tenants;

[Collection(IntegrationTestCollection.Name)]
public sealed class TenantUserControlPlaneTests(MongoReplicaSetFixture fixture) : IAsyncLifetime
{
    private ServiceProvider? _services;

    public async Task InitializeAsync()
    {
        await fixture.ResetDatabaseAsync();
        var services = new ServiceCollection();
        services.AddSingleton(fixture.Database);
        services.AddTenantsFeature();
        _services = services.BuildServiceProvider(validateScopes: true);
    }

    public Task DisposeAsync()
    {
        _services?.Dispose();
        return Task.CompletedTask;
    }
    
    [Fact]
    public async Task CreateTenant_UsesAuthenticatedUserAsOwner()
    {
        using var scope = Services.CreateScope();
        var handler = new CreateTenantHandler(
            scope.ServiceProvider.GetRequiredService<PersistCreatedTenant>());
        var request = new CreateTenantRequest(
            new CreateTenantRequestBody("personal"),
            new ClaimValue("user-1"),
            new ClaimValue("user@example.com"));

        var result = await handler.HandleAsync(request);

        var tenantDto = result switch
        {
            TenantDto createdTenant => createdTenant
        };
        Assert.Equal(TenantRole.Owner, tenantDto.Role);

        var tenant = await fixture.DbContext.Tenants.Find(t => t.Id == tenantDto.Id).FirstAsync();
        var member = Assert.Single(tenant.Members);
        Assert.Equal("user-1", member.UserId);
        Assert.Equal("user@example.com", member.Email);
        Assert.Equal(TenantRole.Owner, member.Role);
    }

    [Fact]
    public async Task GetTenants_ReturnsCurrentUserTenantsAndMarksDefault()
    {
        var defaultTenant = TenantFactory.Create("default");
        defaultTenant = defaultTenant with
        {
            Members = [TenantFactory.CreateMember(defaultTenant.Id, "user-1", "user@example.com", TenantRole.Owner, isDefault: true)]
        };
        var otherTenant = TenantFactory.Create("other");
        otherTenant = otherTenant with
        {
            Members = [TenantFactory.CreateMember(otherTenant.Id, "user-1", "user@example.com", TenantRole.Member)]
        };
        var hiddenTenant = TenantFactory.Create("hidden");
        hiddenTenant = hiddenTenant with
        {
            Members = [TenantFactory.CreateMember(hiddenTenant.Id, "user-2", "other@example.com", TenantRole.Owner)]
        };

        await fixture.DbContext.Tenants.InsertManyAsync([defaultTenant, otherTenant, hiddenTenant]);
        using var scope = Services.CreateScope();
        var handler = new GetTenantsHandler(
            scope.ServiceProvider.GetRequiredService<LoadTenantsForUser>());
        var result = await handler.HandleAsync(new GetTenantsRequest(new ClaimValue("user-1")));

        var tenants = result switch
        {
            IReadOnlyCollection<TenantDto> value => value
        };
        Assert.Equal(2, tenants.Count);
        Assert.True(tenants.Single(t => t.Id == defaultTenant.Id).IsDefault);
        Assert.False(tenants.Single(t => t.Id == otherTenant.Id).IsDefault);
        Assert.DoesNotContain(tenants, t => t.Id == hiddenTenant.Id);
    }

    [Fact]
    public async Task CreateUser_PlatformAdminCreatesPoleUserAndDefaultTenant()
    {
        var identityAdmin = new FakeIdentityProvider("keycloak-user-1");
        var handler = new CreateUserHandler(
            fixture.DbContext,
            identityAdmin,
            NullLogger<CreateUserHandler>.Instance);

        var result = await handler.HandleAsync(new CreateUserRequest(
            new CreateUserRequestBody("New.User@Example.com", "New", "User")));

        Assert.True(result.TryGetValue(out var user, out _));
        Assert.Equal("new.user@example.com", user.Email);

        var storedUser = await fixture.DbContext.Users.Find(u => u.Id == "keycloak-user-1").FirstAsync();
        Assert.Equal(1L, await fixture.DbContext.CountOutboxMessagesAsync("user-created"));

        using var scope = Services.CreateScope();
        var consumer = new CreateDefaultTenantOnUserCreatedConsumer(
            scope.ServiceProvider.GetRequiredService<EnsureDefaultTenant>());
        var created = new UserCreatedEvent(
            storedUser.Id,
            storedUser.Email,
            storedUser.FirstName,
            storedUser.LastName,
            storedUser.DefaultTenantId,
            $"{storedUser.Email} personal");
        await consumer.Handle(created);
        await consumer.Handle(created);

        var defaultTenant = await fixture.DbContext.Tenants.Find(t => t.Id == storedUser.DefaultTenantId).FirstAsync();
        Assert.Equal(
            1,
            await fixture.DbContext.Tenants.CountDocumentsAsync(
                tenant => tenant.Id == storedUser.DefaultTenantId));
        var owner = Assert.Single(defaultTenant.Members);
        Assert.Equal("keycloak-user-1", owner.UserId);
        Assert.Equal(TenantRole.Owner, owner.Role);
        Assert.True(owner.IsDefault);
    }

    [Fact]
    public async Task CreateUser_KeycloakFailureDoesNotCreatePoleRecords()
    {
        var identityAdmin = new FakeIdentityProvider("keycloak-user-1")
        {
            CreateFailure = Failures.ExternalService("failed", "Keycloak", "boom")
        };
        var handler = new CreateUserHandler(
            fixture.DbContext,
            identityAdmin,
            NullLogger<CreateUserHandler>.Instance);

        var result = await handler.HandleAsync(new CreateUserRequest(
            new CreateUserRequestBody("user@example.com", null, null)));

        Assert.False(result.TryGetValue(out _, out var failure));
        Assert.IsType<ExternalServiceFailure>(failure);
        Assert.Equal(0, await fixture.DbContext.Users.CountDocumentsAsync(Builders<PoleUser>.Filter.Empty));
        Assert.Equal(0, await fixture.DbContext.Tenants.CountDocumentsAsync(Builders<Tenant>.Filter.Empty));
    }

    [Fact]
    public async Task CreateUser_MongoFailureAfterKeycloakCreateAttemptsCleanup()
    {
        await fixture.DbContext.Users.InsertOneAsync(new PoleUser("keycloak-user-1", "existing@example.com", null, null, "tenant-1", DateTime.UtcNow));
        var identityAdmin = new FakeIdentityProvider("keycloak-user-1");
        var handler = new CreateUserHandler(
            fixture.DbContext,
            identityAdmin,
            NullLogger<CreateUserHandler>.Instance);

        var result = await handler.HandleAsync(new CreateUserRequest(
            new CreateUserRequestBody("new@example.com", null, null)));

        Assert.False(result.TryGetValue(out _, out var failure));
        Assert.IsType<ConflictFailure>(failure);
        Assert.Equal(["keycloak-user-1"], identityAdmin.DeletedUserIds);
        Assert.Equal(1, await fixture.DbContext.Users.CountDocumentsAsync(Builders<PoleUser>.Filter.Empty));
        Assert.Equal(0, await fixture.DbContext.Tenants.CountDocumentsAsync(Builders<Tenant>.Filter.Empty));
    }

    [Fact]
    public async Task TenantMemberAuthorization_MemberCannotAddMembers()
    {
        var tenant = await InsertTenantAsync(TenantRole.Member);
        var request = new AddTenantMemberRequest(
            tenant.Id,
            new AddTenantMemberRequestBody("user-2", "other@example.com", TenantRole.Member));
        var behavior = CreateTenantMemberAuthorizationBehavior<
            AddTenantMemberRequest,
            AddTenantMemberOutcome>("user-1");
        var nextCalled = false;

        var result = await behavior.Handle(request, () =>
        {
            nextCalled = true;
            return Task.FromResult<MediatorResult<AddTenantMemberOutcome>>(
                new AddTenantMemberOutcome(
                    new TenantMemberDto(
                        "member-2",
                        tenant.Id,
                        "user-2",
                        "other@example.com",
                        TenantRole.Member)));
        }, CancellationToken.None);

        var rejection = Assert.IsType<TenantRoleRequired>(
            AssertRejected(result));
        Assert.Equal(TenantRole.Admin, rejection.MinimumRole);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task TenantMemberAuthorization_AdminCanAddMembers()
    {
        var tenant = await InsertTenantAsync(TenantRole.Admin);
        var request = new AddTenantMemberRequest(
            tenant.Id,
            new AddTenantMemberRequestBody("user-2", "other@example.com", TenantRole.Member));
        var behavior = CreateTenantMemberAuthorizationBehavior<
            AddTenantMemberRequest,
            AddTenantMemberOutcome>("user-1");
        var nextCalled = false;

        var result = await behavior.Handle(request, () =>
        {
            nextCalled = true;
            return Task.FromResult<MediatorResult<AddTenantMemberOutcome>>(
                new AddTenantMemberOutcome(
                    new TenantMemberDto(
                        "member-2",
                        tenant.Id,
                        "user-2",
                        "other@example.com",
                        TenantRole.Member)));
        }, CancellationToken.None);

        var member = AssertOutcome(result);
        Assert.True(member is TenantMemberDto);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task TenantMemberAuthorization_AdminCannotGrantOwner()
    {
        var tenant = await InsertTenantAsync(TenantRole.Admin);
        var request = new AddTenantMemberRequest(
            tenant.Id,
            new AddTenantMemberRequestBody("user-2", "other@example.com", TenantRole.Owner));
        var behavior = CreateTenantMemberAuthorizationBehavior<
            AddTenantMemberRequest,
            AddTenantMemberOutcome>("user-1");

        var result = await behavior.Handle(request, () =>
            Task.FromResult<MediatorResult<AddTenantMemberOutcome>>(
                new AddTenantMemberOutcome(
                    new TenantMemberDto(
                        "member-2",
                        tenant.Id,
                        "user-2",
                        "other@example.com",
                        TenantRole.Owner))),
            CancellationToken.None);

        var rejection = Assert.IsType<TenantRoleRequired>(
            AssertRejected(result));
        Assert.Equal(TenantRole.Owner, rejection.MinimumRole);
    }

    [Fact]
    public async Task TenantMemberAuthorization_RequiresAuthentication()
    {
        var request = new GetTenantMembersRequest("tenant-1");
        var behavior = CreateTenantMemberAuthorizationBehavior<
            GetTenantMembersRequest,
            GetTenantMembersOutcome>(
            userId: null,
            isAuthenticated: false);

        var result = await behavior.Handle(
            request,
            () => Task.FromResult<MediatorResult<GetTenantMembersOutcome>>(
                new GetTenantMembersOutcome(Array.Empty<TenantMemberDto>())),
            CancellationToken.None);

        Assert.IsType<TenantAuthenticationRequired>(AssertRejected(result));
    }

    [Fact]
    public async Task TenantMemberAuthorization_RequiresAuthenticatedUserId()
    {
        var request = new GetTenantMembersRequest("tenant-1");
        var behavior = CreateTenantMemberAuthorizationBehavior<
            GetTenantMembersRequest,
            GetTenantMembersOutcome>(userId: null);

        var result = await behavior.Handle(
            request,
            () => Task.FromResult<MediatorResult<GetTenantMembersOutcome>>(
                new GetTenantMembersOutcome(Array.Empty<TenantMemberDto>())),
            CancellationToken.None);

        Assert.IsType<TenantUserIdentityRequired>(AssertRejected(result));
    }

    [Fact]
    public async Task TenantMemberAuthorization_ReturnsTypedTenantNotFound()
    {
        var tenantId = MongoDB.Bson.ObjectId.GenerateNewId().ToString();
        var request = new GetTenantMembersRequest(tenantId);
        var behavior = CreateTenantMemberAuthorizationBehavior<
            GetTenantMembersRequest,
            GetTenantMembersOutcome>("user-1");

        var result = await behavior.Handle(
            request,
            () => Task.FromResult<MediatorResult<GetTenantMembersOutcome>>(
                new GetTenantMembersOutcome(Array.Empty<TenantMemberDto>())),
            CancellationToken.None);

        var rejection = Assert.IsType<TenantNotFound>(AssertRejected(result));
        Assert.Equal(tenantId, rejection.TenantId);
    }

    [Fact]
    public async Task TenantMemberAuthorization_RequiresMembership()
    {
        var tenant = await InsertTenantAsync(TenantRole.Viewer, "other-user");
        var request = new GetTenantMembersRequest(tenant.Id);
        var behavior = CreateTenantMemberAuthorizationBehavior<
            GetTenantMembersRequest,
            GetTenantMembersOutcome>("user-1");

        var result = await behavior.Handle(
            request,
            () => Task.FromResult<MediatorResult<GetTenantMembersOutcome>>(
                new GetTenantMembersOutcome(Array.Empty<TenantMemberDto>())),
            CancellationToken.None);

        var rejection = Assert.IsType<TenantMembershipRequired>(
            AssertRejected(result));
        Assert.Equal(tenant.Id, rejection.TenantId);
    }

    [Fact]
    public async Task TenantMemberAuthorization_ReturnsTypedTargetMemberNotFound()
    {
        var tenant = await InsertTenantAsync(TenantRole.Owner);
        const string missingMemberId = "missing-member";
        var request = new DeleteTenantMemberRequest(
            tenant.Id,
            missingMemberId);
        var behavior = CreateTenantMemberAuthorizationBehavior<
            DeleteTenantMemberRequest,
            DeleteTenantMemberOutcome>("user-1");

        var result = await behavior.Handle(
            request,
            () => Task.FromResult<MediatorResult<DeleteTenantMemberOutcome>>(
                new DeleteTenantMemberOutcome(missingMemberId)),
            CancellationToken.None);

        var rejection = Assert.IsType<TenantMemberNotFound>(
            AssertRejected(result));
        Assert.Equal(tenant.Id, rejection.TenantId);
        Assert.Equal(missingMemberId, rejection.MemberId);
    }

    [Fact]
    public async Task TenantMemberAuthorization_AdminNeedsOwnerRoleToManageOwner()
    {
        var tenant = await InsertTenantAsync(TenantRole.Admin);
        var owner = TenantFactory.CreateMember(
            tenant.Id,
            "owner-user",
            "owner@example.com",
            TenantRole.Owner);
        tenant = tenant with
        {
            Members = tenant.Members.Add(owner)
        };
        await fixture.DbContext.Tenants.ReplaceOneAsync(
            stored => stored.Id == tenant.Id,
            tenant);
        var request = new DeleteTenantMemberRequest(tenant.Id, owner.Id);
        var behavior = CreateTenantMemberAuthorizationBehavior<
            DeleteTenantMemberRequest,
            DeleteTenantMemberOutcome>("user-1");

        var result = await behavior.Handle(
            request,
            () => Task.FromResult<MediatorResult<DeleteTenantMemberOutcome>>(
                new DeleteTenantMemberOutcome(owner.Id)),
            CancellationToken.None);

        var rejection = Assert.IsType<TenantRoleRequired>(
            AssertRejected(result));
        Assert.Equal(TenantRole.Owner, rejection.MinimumRole);
    }

    [Fact]
    public async Task TenantMemberAuthorization_PlatformAdminBypassesMembershipLookup()
    {
        var request = new GetTenantMembersRequest("missing-tenant");
        var behavior = CreateTenantMemberAuthorizationBehavior<
            GetTenantMembersRequest,
            GetTenantMembersOutcome>(
            userId: null,
            roles: [Roles.Admin]);

        var result = await behavior.Handle(
            request,
            () => Task.FromResult<MediatorResult<GetTenantMembersOutcome>>(
                new GetTenantMembersOutcome(Array.Empty<TenantMemberDto>())),
            CancellationToken.None);

        var outcome = AssertOutcome(result);
        var members = outcome switch
        {
            IReadOnlyCollection<TenantMemberDto> value => value,
            TenantNotFound error => throw new Xunit.Sdk.XunitException(
                $"Expected a platform-admin outcome, but tenant '{error.TenantId}' was not found.")
        };
        Assert.Empty(members);
    }

    [Fact]
    public async Task UpdateTenantMember_DoesNotDemoteLastOwner()
    {
        var tenant = await InsertTenantAsync(TenantRole.Owner);
        var owner = tenant.Members.Single();
        using var scope = Services.CreateScope();
        var handler = new UpdateTenantMemberHandler(
            scope.ServiceProvider.GetRequiredService<PersistUpdatedTenantMemberRole>());

        var result = await handler.HandleAsync(new UpdateTenantMemberRequest(
            tenant.Id,
            owner.Id,
            new UpdateTenantMemberRequestBody(TenantRole.Admin)));

        Assert.True(
            result is UpdateTenantMemberError error &&
            error is TenantMemberMutationError mutation &&
            mutation is TenantLastOwner);
    }

    [Fact]
    public async Task UpdateTenantMember_CanDemoteOwnerWhenAnotherOwnerExists()
    {
        var tenant = await InsertTenantAsync(TenantRole.Owner);
        var firstOwner = tenant.Members.Single();
        var secondOwner = TenantFactory.CreateMember(
            tenant.Id,
            "owner-2",
            "owner-2@example.com",
            TenantRole.Owner);
        tenant = tenant with { Members = tenant.Members.Add(secondOwner) };
        await fixture.DbContext.Tenants.ReplaceOneAsync(
            stored => stored.Id == tenant.Id,
            tenant);

        using var scope = Services.CreateScope();
        var handler = new UpdateTenantMemberHandler(
            scope.ServiceProvider.GetRequiredService<PersistUpdatedTenantMemberRole>());
        var result = await handler.HandleAsync(new UpdateTenantMemberRequest(
            tenant.Id,
            firstOwner.Id,
            new UpdateTenantMemberRequestBody(TenantRole.Admin)));

        Assert.True(result is TenantMemberDto { Role: TenantRole.Admin });
        var stored = await fixture.DbContext.Tenants.Find(item => item.Id == tenant.Id).SingleAsync();
        Assert.Single(stored.Members, member => member.Role == TenantRole.Owner);
    }

    [Fact]
    public async Task DeleteTenantMember_DoesNotRemoveLastOwner()
    {
        var tenant = await InsertTenantAsync(TenantRole.Owner);
        var owner = tenant.Members.Single();
        using var scope = Services.CreateScope();
        var handler = new DeleteTenantMemberHandler(
            scope.ServiceProvider.GetRequiredService<PersistRemovedTenantMember>());

        var result = await handler.HandleAsync(new DeleteTenantMemberRequest(
            tenant.Id,
            owner.Id));

        Assert.True(
            result is DeleteTenantMemberError error &&
            error is TenantMemberMutationError mutation &&
            mutation is TenantLastOwner);
    }

    [Fact]
    public async Task DeleteTenantMember_CanRemoveOwnerWhenAnotherOwnerExists()
    {
        var tenant = await InsertTenantAsync(TenantRole.Owner);
        var firstOwner = tenant.Members.Single();
        var secondOwner = TenantFactory.CreateMember(
            tenant.Id,
            "owner-2",
            "owner-2@example.com",
            TenantRole.Owner);
        tenant = tenant with { Members = tenant.Members.Add(secondOwner) };
        await fixture.DbContext.Tenants.ReplaceOneAsync(
            stored => stored.Id == tenant.Id,
            tenant);

        using var scope = Services.CreateScope();
        var handler = new DeleteTenantMemberHandler(
            scope.ServiceProvider.GetRequiredService<PersistRemovedTenantMember>());
        var result = await handler.HandleAsync(new DeleteTenantMemberRequest(
            tenant.Id,
            firstOwner.Id));

        Assert.True(result is string deletedMemberId && deletedMemberId == firstOwner.Id);
        var stored = await fixture.DbContext.Tenants.Find(item => item.Id == tenant.Id).SingleAsync();
        Assert.DoesNotContain(stored.Members, member => member.Id == firstOwner.Id);
        Assert.Single(stored.Members, member => member.Role == TenantRole.Owner);
    }

    private async Task<Tenant> InsertTenantAsync(
        TenantRole currentUserRole,
        string currentUserId = "user-1")
    {
        var tenant = TenantFactory.Create("team") with
        {
            Members = ImmutableList.Create(
                TenantFactory.CreateMember(
                    "placeholder",
                    currentUserId,
                    "user@example.com",
                    currentUserRole))
        };

        tenant = tenant with
        {
            Members = tenant.Members
                .Select(member => member with { TenantId = tenant.Id })
                .ToImmutableList()
        };

        await fixture.DbContext.Tenants.InsertOneAsync(tenant);
        return tenant;
    }

    private TenantMemberAuthorizationBehavior<TRequest, TResponse> CreateTenantMemberAuthorizationBehavior<TRequest, TResponse>(
        string? userId,
        bool isAuthenticated = true,
        params string[] roles)
        where TRequest : IMediatorRequest<TResponse>
        where TResponse : notnull
    {
        var claims = new List<Claim>();
        if (!string.IsNullOrWhiteSpace(userId))
        {
            claims.Add(new Claim(Claims.UserId, userId));
            claims.Add(new Claim(Claims.Email, "user@example.com"));
        }
        claims.AddRange(roles.Select(role => new Claim(Claims.Roles, role)));

        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    claims,
                    isAuthenticated ? "test" : null))
            }
        };

        return new TenantMemberAuthorizationBehavior<TRequest, TResponse>(
            LoadTenantAsync,
            accessor);
    }

    private async Task<Tenant?> LoadTenantAsync(
        string tenantId,
        CancellationToken cancellationToken) =>
        await fixture.DbContext.Tenants
            .Find(tenant => tenant.Id == tenantId)
            .FirstOrDefaultAsync(cancellationToken);

    private IServiceProvider Services => _services
        ?? throw new InvalidOperationException("Tenant test services are not initialized.");

    private static IMediatorRejection AssertRejected<T>(
        MediatorResult<T> result)
        where T : notnull =>
        result.Match(
            _ => throw new Xunit.Sdk.XunitException(
                "Expected a pipeline rejection, but received an outcome."),
            rejected => rejected.Reason);

    private static T AssertOutcome<T>(MediatorResult<T> result)
        where T : notnull =>
        result.Match(
            outcome => outcome,
            rejected => throw new Xunit.Sdk.XunitException(
                $"Expected an outcome, but received " +
                $"'{rejected.Reason.GetType().Name}'."));

    private static async Task<WebApplication> StartClaimBindingApp(params Claim[] claims)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        app.Use((context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
            return next(context);
        });
        app.MapPost("/claims", (ClaimBoundRequest request) =>
            new ClaimBoundResponse(request.CurrentUserId, request.CurrentUserEmail, request.Roles));
        await app.StartAsync();
        return app;
    }

    private sealed record ClaimBoundRequest(
        [FromClaim(Claims.UserId)] string CurrentUserId,
        [FromClaim(Claims.Email)] string CurrentUserEmail,
        [FromClaim(Claims.Roles)] ImmutableArray<string> Roles);

    private sealed record ClaimBoundResponse(string CurrentUserId, string CurrentUserEmail, IEnumerable<string> Roles);

    private sealed class FakeIdentityProvider(string userId) : IIdentityProvider
    {
        public Failure? CreateFailure { get; init; }
        public int CreateCalls { get; private set; }
        public List<string> DeletedUserIds { get; } = [];

        public Task<Result<string>> CreateUserAsync(CreateIdentityUser user, CancellationToken cancellationToken)
        {
            CreateCalls++;
            return Task.FromResult<Result<string>>(CreateFailure is null ? userId : CreateFailure);
        }

        public Task<bool> DeleteUserAsync(string deletedUserId, CancellationToken cancellationToken)
        {
            DeletedUserIds.Add(deletedUserId);
            return Task.FromResult(true);
        }
    }
}
