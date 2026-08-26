using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Application.Common;
using Pole.Features.Tenants.Application.Persistence;
using Pole.Features.Tenants.Application.Persistence.Tenants;
using Pole.Features.Tenants.Domain;
using Pole.Mediator;

namespace Pole.Features.Tenants.Application.Endpoints;

public static class CreateTenant
{
    public static IEndpointRouteBuilder MapCreateTenantEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/", async (IMediator mediator, [AsParameters] CreateTenantRequest request, CancellationToken cancellationToken) =>
        {
            var result = await mediator.Send(request, cancellationToken);
            return result.ToHttpResult(outcome => outcome switch
            {
                TenantDto tenant => Results.Created($"/api/v1/tenants/{tenant.Id}", tenant)
            });
        })
        .WithName("CreateTenant")
        .RequireAuthorization(Policy.Admin)
        .Produces<TenantDto>(StatusCodes.Status201Created);

        return app;
    }
}

public sealed record CreateTenantRequest(
    [FromBody] CreateTenantRequestBody Body,
    [FromClaim(Claims.UserId)] ClaimValue CurrentUserId,
    [FromClaim(Claims.Email)] ClaimValue CurrentUserEmail
    ) : IMediatorRequest<CreateTenantOutcome>;

public union CreateTenantOutcome(TenantDto);

public sealed record CreateTenantRequestBody(
    string Name);

public sealed class CreateTenantHandler(
    PersistCreatedTenant persistCreatedTenant)
    : IMediatorOutcomeHandler<CreateTenantRequest, CreateTenantOutcome>
{
    public async Task<CreateTenantOutcome> HandleAsync(
        CreateTenantRequest request,
        CancellationToken cancellationToken = default)
    {
        var tenant = TenantFactory.Create(request.Body.Name);

        var owner = TenantFactory.CreateMember(
            tenant.Id,
            request.CurrentUserId,
            request.CurrentUserEmail,
            TenantRole.Owner);
        tenant = tenant with { Members = [owner] };

        await persistCreatedTenant(tenant, cancellationToken);
        return tenant.ToDto(TenantRole.Owner);
    }
}

public sealed class CreateTenantValidator : AbstractValidator<CreateTenantRequest>
{
    public CreateTenantValidator()
    {
        RuleFor(x => x.Body.Name).NotEmpty().MaximumLength(80);
        RuleFor(x => x.CurrentUserId.Value).NotEmpty().MaximumLength(120);
        RuleFor(x => x.CurrentUserEmail.Value).NotEmpty().MaximumLength(254);
    }
}
