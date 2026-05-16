using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Domain;
using Pole.Features.Tenants.Endpoints;
using Pole.Features.Users.Domain;
using Pole.Features.Users.Endpoints;
using Pole.Features.Users.Identity;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Tenants;

[Collection(IntegrationTestCollection.Name)]
public sealed class TenantUserControlPlaneTests(MongoReplicaSetFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;
    
    [Fact]
    public async Task CreateTenant_UsesAuthenticatedUserAsOwner()
    {
        var handler = new CreateTenantHandler(fixture.DbContext);
        var request = new CreateTenantRequest(
            new CreateTenantRequestBody("personal"),
            "user-1",
            "user@example.com");

        var result = await handler.HandleAsync(request);

        Assert.True(result.TryGetValue(out var tenantDto, out _));
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
            Members = [TenantFactory.CreateMember(defaultTenant.Id, "user-1", "user@example.com", TenantRole.Owner)]
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
        await fixture.DbContext.Users.InsertOneAsync(new PoleUser("user-1", "user@example.com", null, null, defaultTenant.Id, DateTime.UtcNow));

        var handler = new GetTenantsHandler(fixture.DbContext);
        var result = await handler.HandleAsync(new GetTenantsRequest("user-1"));

        Assert.True(result.TryGetValue(out var tenants, out _));
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
            fixture.DbContext,
            identityAdmin,
            NullLogger<CreateUserHandler>.Instance);

        var result = await handler.HandleAsync(new CreateUserRequest(
            new CreateUserRequestBody("New.User@Example.com", "New", "User")));

        Assert.True(result.TryGetValue(out var user, out _));
        Assert.Equal("new.user@example.com", user.Email);

        var storedUser = await fixture.DbContext.Users.Find(u => u.Id == "keycloak-user-1").FirstAsync();
        var defaultTenant = await fixture.DbContext.Tenants.Find(t => t.Id == storedUser.DefaultTenantId).FirstAsync();
        var owner = Assert.Single(defaultTenant.Members);
        Assert.Equal("keycloak-user-1", owner.UserId);
        Assert.Equal(TenantRole.Owner, owner.Role);
    }

    [Fact]
    public async Task CreateUser_KeycloakFailureDoesNotCreatePoleRecords()
    {
        var identityAdmin = new FakeIdentityProvider("keycloak-user-1")
        {
            CreateFailure = Result.ExternalService("failed", "Keycloak", "boom")
        };
        var handler = new CreateUserHandler(
            fixture.DbContext,
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
