using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Application.Common;
using Pole.Features.Tenants.Application.Persistence.Members;
using Pole.Features.Tenants.Domain;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.Tenants.Application.Endpoints.Members;

public sealed record AddTenantMemberRequest(
    [FromRoute] string TenantId,
    [FromBody] AddTenantMemberRequestBody Body
) : IMediatorRequest<AddTenantMemberOutcome>, ITenantMemberRoleOperation
{
    public string? MemberId => null;
    public TenantRole Role => Body.Role;
}

public sealed record AddTenantMemberRequestBody(
    string UserId,
    string Email,
    TenantRole Role);

public union AddTenantMemberError(TenantMemberAdditionError);

public union AddTenantMemberOutcome(
    TenantMemberDto,
    AddTenantMemberError);

public sealed class AddTenantMemberHandler(
    PersistAddedTenantMember persistAddedTenantMember)
    : IMediatorOutcomeHandler<AddTenantMemberRequest, AddTenantMemberOutcome>
{
    public async Task<AddTenantMemberOutcome> HandleAsync(
        AddTenantMemberRequest request,
        CancellationToken cancellationToken = default)
    {
        var member = TenantFactory.CreateMember(
            request.TenantId,
            request.Body.UserId,
            request.Body.Email,
            request.Body.Role);
        var persisted = await persistAddedTenantMember(member, cancellationToken);
        return TypedResultCompositionExtensions.Match(
            persisted,
            added => new AddTenantMemberOutcome(added.ToDto()),
            error => new AddTenantMemberOutcome(new AddTenantMemberError(error)));
    }
}

public sealed class AddTenantMemberValidator : AbstractValidator<AddTenantMemberRequest>
{
    public AddTenantMemberValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.Body.UserId).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Body.Email).NotEmpty().MaximumLength(254);
        RuleFor(x => x.Body.Role).IsInEnum();
    }
}

public static class AddTenantMemberEndpoint
{
    public static IEndpointRouteBuilder MapAddTenantMemberEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/{TenantId}/members", async (
                IMediator mediator,
                IHttpProblemMapper httpProblems,
                [AsParameters] AddTenantMemberRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    TenantMemberDto member => Results.Created(
                        $"/api/v1/tenants/{member.TenantId}/members/{member.Id}",
                        member),
                    AddTenantMemberError error => httpProblems.Map(error).ToHttpResult()
                });
            })
            .WithName("AddTenantMember")
            .Produces<TenantMemberDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        return app;
    }
}
