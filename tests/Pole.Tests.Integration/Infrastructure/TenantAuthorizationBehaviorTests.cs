using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Mediator;
using Xunit;
using Xunit.Sdk;

namespace Pole.Tests.Integration.Infrastructure;

public sealed class TenantAuthorizationBehaviorTests
{
    [Fact]
    public async Task MissingTenantSelectionIsARequestValidationRejection()
    {
        var behavior = CreateBehavior<WriteRequest>(tenantContext: null);
        var nextCalled = false;

        var result = await behavior.Handle(
            new WriteRequest(null),
            Next,
            CancellationToken.None);

        var rejected = Assert.IsType<RequestValidationRejected>(
            AssertRejected(result));
        Assert.Contains(TenantHeaders.TenantId, rejected.Errors.Keys);
        Assert.False(nextCalled);
        return;

        Task<MediatorResult<string>> Next()
        {
            nextCalled = true;
            return Task.FromResult<MediatorResult<string>>("handled");
        }
    }

    [Fact]
    public async Task TenantWideOperationRequiresPlatformAdministrator()
    {
        var behavior = CreateBehavior<WriteRequest>(tenantContext: null);

        var result = await behavior.Handle(
            new WriteRequest("tenant-1"),
            () => Task.FromResult<MediatorResult<string>>("handled"),
            CancellationToken.None);

        Assert.IsType<TenantPlatformAdminRequired>(AssertRejected(result));
    }

    [Fact]
    public async Task WriteOperationReportsTheMinimumTenantRole()
    {
        var behavior = CreateBehavior<WriteRequest>(
            new TenantContext(
                "tenant-1",
                "Tenant 1",
                TenantRole.Viewer,
                IsPlatformAdmin: false));

        var result = await behavior.Handle(
            new WriteRequest("tenant-1"),
            () => Task.FromResult<MediatorResult<string>>("handled"),
            CancellationToken.None);

        var rejected = Assert.IsType<TenantRoleRequired>(
            AssertRejected(result));
        Assert.Equal(TenantRole.Member, rejected.MinimumRole);
    }

    [Fact]
    public async Task ViewerCanContinueAReadOperation()
    {
        var behavior = CreateBehavior<ReadRequest>(
            new TenantContext(
                "tenant-1",
                "Tenant 1",
                TenantRole.Viewer,
                IsPlatformAdmin: false));

        var result = await behavior.Handle(
            new ReadRequest("tenant-1"),
            () => Task.FromResult<MediatorResult<string>>("handled"),
            CancellationToken.None);

        Assert.Equal(
            "handled",
            result.Match(
                value => value,
                rejected => throw new XunitException(
                    $"Expected an outcome, but received '{rejected.Reason.GetType().Name}'.")));
    }

    [Fact]
    public async Task PlatformAdministratorCanContinueWithoutTenantContext()
    {
        var behavior = CreateBehavior<WriteRequest>(
            tenantContext: null,
            new Claim(Claims.Roles, Roles.Admin));

        var result = await behavior.Handle(
            new WriteRequest("tenant-1"),
            () => Task.FromResult<MediatorResult<string>>("handled"),
            CancellationToken.None);

        Assert.Equal(
            "handled",
            result.Match(
                value => value,
                rejected => throw new XunitException(
                    $"Expected an outcome, but received '{rejected.Reason.GetType().Name}'.")));
    }

    private static TenantAuthorizationBehavior<TRequest, string>
        CreateBehavior<TRequest>(
            TenantContext? tenantContext,
            params Claim[] claims)
        where TRequest : IMediatorRequest<string>
    {
        var tenantContextAccessor = new TenantContextAccessor
        {
            Current = tenantContext
        };
        var httpContextAccessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(
                    new ClaimsIdentity(claims, "test"))
            }
        };

        return new TenantAuthorizationBehavior<TRequest, string>(
            tenantContextAccessor,
            httpContextAccessor);
    }

    private static IMediatorRejection AssertRejected(
        MediatorResult<string> result) => result switch
        {
            string value => throw new XunitException(
                $"Expected a pipeline rejection, but received '{value}'."),
            PipelineRejected rejected => rejected.Reason
        };

    private sealed record WriteRequest(string? TenantId)
        : IMediatorRequest<string>, ITenantWriteOperation;

    private sealed record ReadRequest(string? TenantId)
        : IMediatorRequest<string>, ITenantReadOperation;
}
