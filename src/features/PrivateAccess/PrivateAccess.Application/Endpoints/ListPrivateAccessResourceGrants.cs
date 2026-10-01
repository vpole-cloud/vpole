using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.PrivateAccess.Application.Persistence;
using Pole.Features.PrivateAccess.Contracts;
using Pole.Features.Tenants.Contracts;
using Pole.Mediator;

namespace Pole.Features.PrivateAccess.Application.Endpoints;

public sealed record ListPrivateAccessResourceGrantsRequest(
    [FromTenantHeader] string TenantId,
    [FromRoute] string Kind,
    [FromRoute] string Name,
    [FromQuery] string? DeploymentId,
    [FromQuery] string? EndpointId,
    [FromClaim(Claims.UserId)] ClaimValue<string?> SubjectUserId = default)
    : IMediatorRequest<ListPrivateAccessResourceGrantsOutcome>, ITenantReadOperation;

public sealed record PrivateAccessResourceGrantOption(
    string GrantId,
    string ResourceDeploymentId,
    string EndpointId,
    DateTime ExpiresAtUtc);

public sealed record PrivateAccessResourceGrantOptions(
    IReadOnlyList<PrivateAccessResourceGrantOption> Items);

public union ListPrivateAccessResourceGrantsOutcome(
    PrivateAccessResourceGrantOptions,
    PrivateAccessSubjectMembershipRequired);

public sealed class ListPrivateAccessResourceGrantsHandler(
    LoadTenantMember loadTenantMember,
    LoadOwnPrivateAccessGrants loadGrants,
    PrivateAccessResourceNameCatalog resources,
    TimeProvider timeProvider)
    : IMediatorOutcomeHandler<
        ListPrivateAccessResourceGrantsRequest,
        ListPrivateAccessResourceGrantsOutcome>
{
    public async Task<ListPrivateAccessResourceGrantsOutcome> HandleAsync(
        ListPrivateAccessResourceGrantsRequest request,
        CancellationToken cancellationToken = default)
    {
        var subjectId = request.SubjectUserId.Value;
        if (string.IsNullOrWhiteSpace(subjectId) ||
            await loadTenantMember(request.TenantId, subjectId,
                cancellationToken: cancellationToken) is not TenantMemberSnapshot)
            return new PrivateAccessSubjectMembershipRequired();

        var resource = await resources.ResolveAsync(
            request.TenantId, request.Kind, request.Name, cancellationToken);
        if (resource is null)
            return new PrivateAccessResourceGrantOptions([]);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var grants = await loadGrants(request.TenantId, subjectId, now, cancellationToken);
        var options = grants
            .Where(grant => grant.ResourceType == resource.ResourceType.Value &&
                grant.ResourceId == resource.ResourceId &&
                (request.DeploymentId is null ||
                    grant.ResourceDeploymentId == request.DeploymentId) &&
                (request.EndpointId is null || grant.EndpointId == request.EndpointId))
            .Select(grant => new PrivateAccessResourceGrantOption(
                grant.Id,
                grant.ResourceDeploymentId,
                grant.EndpointId,
                grant.ExpiresAtUtc))
            .ToArray();
        return new PrivateAccessResourceGrantOptions(options);
    }
}

public sealed class ListPrivateAccessResourceGrantsRequestValidator
    : AbstractValidator<ListPrivateAccessResourceGrantsRequest>
{
    public ListPrivateAccessResourceGrantsRequestValidator()
    {
        RuleFor(request => request.Kind).NotEmpty().MaximumLength(64);
        RuleFor(request => request.Name).NotEmpty().MaximumLength(200);
    }
}

public static class ListPrivateAccessResourceGrantsEndpoint
{
    public static IEndpointRouteBuilder MapListPrivateAccessResourceGrantsEndpoint(
        this IEndpointRouteBuilder app)
    {
        app.MapGet("/{Kind}/{Name}/grants", async (
                IMediator mediator,
                [AsParameters] ListPrivateAccessResourceGrantsRequest request,
                CancellationToken cancellationToken) =>
            (await mediator.Send(request, cancellationToken)).ToHttpResult(outcome => outcome switch
            {
                PrivateAccessResourceGrantOptions options => Results.Ok(options),
                PrivateAccessSubjectMembershipRequired error =>
                    PrivateAccessHttpProblemMappings.Map(error)
            }))
            .WithName("ListPrivateAccessResourceGrants")
            .Produces<PrivateAccessResourceGrantOptions>();
        return app;
    }
}
