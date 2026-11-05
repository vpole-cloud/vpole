using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.PrivateAccess.Contracts;
using Pole.Features.PrivateAccess.Application.Admission;
using Pole.Features.PrivateAccess.Application.Catalog;
using Pole.Features.PrivateAccess.Application.Gateway;
using Pole.Features.PrivateAccess.Application.Grants;
using Pole.Features.PrivateAccess.Application.Mongo;
using Pole.Features.PrivateAccess.Application.Sessions;
using Pole.Features.Tenants.Contracts;
using Pole.Mediator;

namespace Pole.Features.PrivateAccess.Application.Endpoints;

public sealed record ListPrivateAccessGrantsRequest(
    [FromTenantHeader] string TenantId,
    [FromClaim(Claims.UserId)] ClaimValue SubjectUserId)
    : IMediatorRequest<ListPrivateAccessGrantsOutcome>, ITenantReadOperation;

public sealed record PrivateAccessGrantList(IReadOnlyList<PrivateAccessGrantDto> Items);

public union ListPrivateAccessGrantsOutcome(
    PrivateAccessGrantList,
    PrivateAccessSubjectMembershipRequired);

public sealed class ListPrivateAccessGrantsHandler(
    LoadOwnPrivateAccessGrants loadGrants,
    LoadTenantMember loadTenantMember,
    TimeProvider timeProvider)
    : IMediatorOutcomeHandler<ListPrivateAccessGrantsRequest, ListPrivateAccessGrantsOutcome>
{
    public async Task<ListPrivateAccessGrantsOutcome> HandleAsync(
        ListPrivateAccessGrantsRequest request,
        CancellationToken cancellationToken = default)
    {
        var subjectId = request.SubjectUserId.Value;
        if (await loadTenantMember(
            new LoadTenantMemberRequest(
                TenantId: request.TenantId,
                UserId: subjectId),
            cancellationToken: cancellationToken) is not TenantMemberSnapshot)
            return new PrivateAccessSubjectMembershipRequired();

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var found = await loadGrants(request.TenantId, subjectId, now, cancellationToken);
        return new PrivateAccessGrantList(found.Select(item => item.ToDto()).ToArray());
    }
}

public sealed class ListPrivateAccessGrantsValidator : AbstractValidator<ListPrivateAccessGrantsRequest>;

public static class ListPrivateAccessGrantsEndpoint
{
    public static IEndpointRouteBuilder MapListPrivateAccessGrantsEndpoint(
        this IEndpointRouteBuilder app)
    {
        app.MapGet("/", async (
                IMediator mediator,
                [AsParameters] ListPrivateAccessGrantsRequest request,
                CancellationToken cancellationToken) =>
            (await mediator.Send(request, cancellationToken)).ToHttpResult(outcome => outcome switch
            {
                PrivateAccessGrantList list => Results.Ok(list.Items),
                PrivateAccessSubjectMembershipRequired error =>
                    PrivateAccessHttpProblemMappings.Map(error)
            }))
            .WithName("ListPrivateAccessGrants")
            .Produces<IReadOnlyList<PrivateAccessGrantDto>>(StatusCodes.Status200OK);
        return app;
    }
}
