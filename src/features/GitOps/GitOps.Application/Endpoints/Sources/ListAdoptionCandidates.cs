using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitOps.Application.Inventory.Mongo;
using Pole.Features.GitOps.Application.Sources;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.GitOps.Application.Endpoints.Sources;

public sealed record AdoptionNamedResourceView(
    string Name,
    string ResourceId);

public sealed record AdoptionConsumerView(
    string Kind,
    string Name,
    string ResourceId);

public sealed record AdoptionTargetScopeView(
    string Mode,
    IReadOnlyList<string> DeploymentIds);

public sealed record AdoptionBindingView(
    AdoptionNamedResourceView Secret,
    AdoptionConsumerView Consumer,
    string Usage,
    AdoptionTargetScopeView TargetScope);

public sealed record AdoptionCandidateView(
    string Kind,
    string? Name,
    string ResourceId,
    string? OwnerSourceId,
    bool Selectable,
    string? Blocker,
    GitLifecyclePresence Presence,
    AdoptionBindingView? Binding);

public sealed record AdoptionCandidatePage(
    IReadOnlyList<AdoptionCandidateView> Items,
    int Page,
    int PageSize,
    bool HasMore);

public sealed record ListAdoptionCandidatesRequest(
    [FromRoute] string Id,
    [FromTenantHeader] string TenantId,
    [FromQuery] string Kind,
    [FromQuery] string? Name = null,
    [FromQuery] string? RelatedKind = null,
    [FromQuery] string? RelatedName = null,
    [FromQuery] string? ConsumerKind = null,
    [FromQuery] string? ConsumerName = null,
    [FromQuery] int Page = 1,
    [FromQuery] int PageSize = 50) : IMediatorRequest<ListAdoptionCandidatesOutcome>, ITenantReadOperation;

public sealed record AdoptionCandidateSearchUnsupported;

public union ListAdoptionCandidatesOutcome(
    AdoptionCandidatePage,
    GitSourceNotFound,
    AdoptionCandidateSearchUnsupported);

public sealed class ListAdoptionCandidatesValidator : AbstractValidator<ListAdoptionCandidatesRequest>
{
    public ListAdoptionCandidatesValidator()
    {
        RuleFor(item => item.Id).MongoObjectId("Source ID");
        RuleFor(item => item.Kind).NotEmpty().MaximumLength(63);
        RuleFor(item => item.Page).InclusiveBetween(1, 1000000);
        RuleFor(item => item.PageSize).InclusiveBetween(1, 100);
        RuleFor(item => item).Custom((query, context) =>
        {
            var related = query.RelatedKind is not null || query.RelatedName is not null ||
                          query.ConsumerKind is not null || query.ConsumerName is not null;
            if ((query.Name is not null) == related)
                context.AddFailure("name", "Choose a name or complete related-resource and consumer filters.");
            if (query.Name is not null && !DeclarationNames.Valid(query.Name))
                context.AddFailure("name", "A technical name is required.");
            if (related && (string.IsNullOrWhiteSpace(query.RelatedKind) || query.RelatedKind.Length > 63 ||
                            string.IsNullOrWhiteSpace(query.ConsumerKind) || query.ConsumerKind.Length > 63 ||
                            !DeclarationNames.Valid(query.RelatedName) || !DeclarationNames.Valid(query.ConsumerName)))
                context.AddFailure("relatedName",
                    "Complete valid related-resource and consumer addresses are required.");
        });
    }
}

public sealed class ListAdoptionCandidatesHandler(GitAdoptionCandidates candidates)
    : IMediatorOutcomeHandler<ListAdoptionCandidatesRequest, ListAdoptionCandidatesOutcome>
{
    public Task<ListAdoptionCandidatesOutcome> HandleAsync(ListAdoptionCandidatesRequest request,
        CancellationToken cancellationToken = default) => candidates.ReadAsync(new(
        new(ObjectId.Parse(request.TenantId).ToString(), ObjectId.Parse(request.Id).ToString()),
        request.Kind, request.Name is { } name
            ? new GitNamedCandidateSearch(name)
            : new GitRelatedCandidateSearch(new(request.RelatedKind!, request.RelatedName!),
                new(request.ConsumerKind!, request.ConsumerName!)),
        new(request.Page, request.PageSize)), cancellationToken);
}

public static class ListAdoptionCandidatesEndpoint
{
    public static IEndpointRouteBuilder MapListAdoptionCandidatesEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/{Id}/adoption-candidates", async (IMediator mediator,
                [AsParameters] ListAdoptionCandidatesRequest request, CancellationToken ct) =>
                (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    AdoptionCandidatePage page => Results.Ok(page),
                    GitSourceNotFound missing => RepositoryHttpMappings.Map(missing),
                    AdoptionCandidateSearchUnsupported unsupported => RepositoryHttpMappings.Map(unsupported)
                })).WithName("ListAdoptionCandidates");
        return app;
    }
}
