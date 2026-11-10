using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Application.ProviderOperations;
using Pole.Features.GitProviders.Application.Repositories;
using Pole.Features.GitProviders.Contracts;
using Pole.Features.GitProviders.Domain;
using Pole.Mediator;
using Pole.Validation;
using Pole.Workloads;
using static Pole.Features.GitProviders.Application.Common.GitProviderHttpMappings;

namespace Pole.Features.GitProviders.Application.Endpoints.Repositories;

public sealed record LinkProviderRepositoryRequest(
    [FromRoute] string ConnectionId,
    [FromRoute] string RepositoryId,
    [FromTenantHeader] string TenantId,
    [FromBody] LinkSourceRepositoryRequestBody Body
) : IMediatorRequest<LinkProviderRepositoryOutcome>, ITenantWriteOperation;

public union LinkProviderRepositoryOutcome(
    SourceRepositoryDto,
    GitProviderConnectionNotFound,
    SourceRepositoryRemoteNotFound,
    SourceRepositoryBranchNotFound,
    SourceRepositoryAccessDenied,
    SourceRepositoryUnavailable,
    SourceRepositoryAlreadyLinked,
    SourceRepositoryNameConflict);

public sealed class LinkProviderRepositoryHandler(
    ResolveProviderRepository resolveProviderRepository,
    PersistLinkedSourceRepository persistLinkedSourceRepository
) : IMediatorOutcomeHandler<LinkProviderRepositoryRequest, LinkProviderRepositoryOutcome>
{
    public async Task<LinkProviderRepositoryOutcome> HandleAsync(
        LinkProviderRepositoryRequest request,
        CancellationToken cancellationToken = default)
    {
        var resolved = await resolveProviderRepository(
            new ResolveProviderRepositoryRequest(
                request.TenantId,
                request.ConnectionId,
                request.RepositoryId,
                request.Body.Branch),
            cancellationToken);
        return resolved switch
        {
            ResolvedProviderRepository repository => await PersistAsync(request, repository, cancellationToken),
            GitProviderConnectionNotFound missing => missing,
            SourceRepositoryRemoteNotFound missing => missing,
            SourceRepositoryBranchNotFound missing => missing,
            SourceRepositoryAccessDenied denied => denied,
            SourceRepositoryUnavailable unavailable => unavailable,
        };
    }

    private async Task<LinkProviderRepositoryOutcome> PersistAsync(
        LinkProviderRepositoryRequest request,
        ResolvedProviderRepository resolved,
        CancellationToken cancellationToken)
    {
        var repository = SourceRepositoryFactory.Create(
            request.TenantId,
            request.Body.TechnicalName,
            request.ConnectionId,
            resolved.Repository.Id,
            resolved.Repository.FullName,
            resolved.Repository.HtmlUrl,
            resolved.Repository.IsPrivate,
            request.Body.Branch,
            SourceRepositoryFactory.DefaultOptions);
        var inserted = await persistLinkedSourceRepository(repository, cancellationToken);
        return inserted switch
        {
            SourceRepository success => success.ToDto(),
            SourceRepositoryAlreadyLinked conflict => conflict,
            SourceRepositoryNameConflict conflict => conflict,
            GitProviderConnectionNotFound missing => missing,
        };
    }
}

public sealed class LinkProviderRepositoryRequestValidator : AbstractValidator<LinkProviderRepositoryRequest>
{
    public LinkProviderRepositoryRequestValidator()
    {
        RuleFor(request => request.ConnectionId).MongoObjectId("Git provider connection ID");
        RuleFor(request => request.RepositoryId).NotEmpty();
        RuleFor(request => request.Body).NotNull();
        RuleFor(request => request.Body.TechnicalName).Must(ResourceTechnicalName.IsValid);
        RuleFor(request => request.Body.Branch).NotEmpty();
    }
}

public static class LinkProviderRepositoryEndpoint
{
    public static IEndpointRouteBuilder MapLinkProviderRepositoryEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost(
                "/{ConnectionId}/repositories/{RepositoryId}/links",
                async (
                    IMediator mediator, [AsParameters] LinkProviderRepositoryRequest request,
                    CancellationToken cancellationToken) =>
                {
                    var result = await mediator.Send(request, cancellationToken);
                    return result.ToHttpResult(outcome => outcome switch
                    {
                        SourceRepositoryDto repository => Results.Created(
                            $"/api/v1/source-repositories/{repository.Id}",
                            repository),
                        GitProviderConnectionNotFound missing => MapConnectionNotFound(missing),
                        SourceRepositoryRemoteNotFound missing => MapSourceRepositoryRemoteNotFound(missing),
                        SourceRepositoryBranchNotFound missing => MapSourceRepositoryBranchNotFound(missing),
                        SourceRepositoryAccessDenied denied => MapSourceRepositoryAccessDenied(denied),
                        SourceRepositoryUnavailable unavailable => MapSourceRepositoryUnavailable(unavailable),
                        SourceRepositoryAlreadyLinked conflict => MapSourceRepositoryAlreadyLinked(conflict),
                        SourceRepositoryNameConflict conflict => MapSourceRepositoryNameConflict(conflict),
                    });
                })
            .WithName("LinkProviderRepository")
            .Produces<SourceRepositoryDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return app;
    }
}
