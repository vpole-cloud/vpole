using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Application.Common;
using Pole.Features.Tenants.Domain;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.Tenants.Application.Endpoints;

public sealed record DeleteTenantMemberRequest(
    [FromRoute] string TenantId,
    [FromRoute] string MemberId
) : IMediatorRequest<string>, ITenantMemberTargetOperation;

public sealed class DeleteTenantMemberHandler(ITenantDbContext dbContext)
    : IMediatorHandler<DeleteTenantMemberRequest, string>
{
    public async Task<Result<string>> HandleAsync(
        DeleteTenantMemberRequest request,
        CancellationToken cancellationToken = default)
    {
        var ownerValidation = await TenantMemberMutationGuard.ValidateAsync(
            dbContext,
            request.TenantId,
            request.MemberId,
            nextRole: null,
            cancellationToken);
        if (ownerValidation is not null)
            return ownerValidation;

        var result = await dbContext.Tenants.UpdateOneAsync(
            t => t.Id == request.TenantId && t.Members.Any(m => m.Id == request.MemberId),
            Builders<Tenant>.Update.PullFilter(t => t.Members, m => m.Id == request.MemberId),
            cancellationToken: cancellationToken);

        return result.ModifiedCount == 0
            ? Failures.NotFound("Tenant member not found.", request.MemberId)
            : request.MemberId;
    }
}

public static class DeleteTenantMemberEndpoint
{
    public static IEndpointRouteBuilder MapDeleteTenantMemberEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapDelete("/{TenantId}/members/{MemberId}", async (IMediator mediator, [AsParameters] DeleteTenantMemberRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(_ => Results.NoContent());
            })
            .WithName("DeleteTenantMember")
            .Produces(StatusCodes.Status204NoContent);
        return app;
    }
}
