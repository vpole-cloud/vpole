using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Persistence;
using Pole.Features.WebServices.Application.Persistence.Mutations;
using Pole.Features.WebServices.Application.Persistence.Reads;
using Pole.Features.WebServices.Application.Persistence.Releases;
using Pole.Features.WebServices.Application.Persistence.Tokens;
using Pole.Features.WebServices.Domain;
using Pole.Mediator;
using Pole.ResultType;

using static Pole.Features.WebServices.Application.Common.WebServiceHttpMappings;

namespace Pole.Features.WebServices.Application.Endpoints;

public static class UpdateWebServiceImageTag
{
    public static IEndpointRouteBuilder MapUpdateWebServiceImageTagEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPatch("/{Id}/image-tag", async (
                IMediator mediator,
                [AsParameters] UpdateWebServiceImageTagRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    UpdateWebServiceImageTagResponse response =>
                        Results.Accepted(value: response),
                    UpdateWebServiceImageTagError error =>
                        MapError(error)
                });
            })
            .WithName("UpdateWebServiceImageTag")
            .AllowAnonymous()
            .WithMetadata(new TenantCredentialMetadata(WebServiceDeploymentTokenFactory.RawTokenPrefix))
            .Produces<UpdateWebServiceImageTagResponse>(StatusCodes.Status202Accepted);

        return app;
    }
}

public sealed record UpdateWebServiceImageTagRequest(
    [FromRoute] string Id,
    [FromBody] UpdateWebServiceImageTagRequestBody Body,
    [FromTenantHeader] string? TenantId = null,
    [FromHeader(Name = "Authorization")] string? Authorization = null,
    [FromClaim(Claims.UserId)] ClaimValue<string?> ActorUserId = default,
    [FromClaim(Claims.Email)] ClaimValue<string?> ActorEmail = default
) : IMediatorRequest<UpdateWebServiceImageTagOutcome>;

public sealed record UpdateWebServiceImageTagRequestBody(
    string Tag,
    string Source,
    string? CommitSha = null,
    string? BuildUrl = null,
    string? ImageDigest = null);

public sealed record UpdateWebServiceImageTagResponse(
    string Id,
    long ResourceRevision,
    string ReleaseId,
    string Tag,
    IReadOnlyList<PlacementSummaryDto> Deployments);

public union UpdateWebServiceImageTagOutcome(
    UpdateWebServiceImageTagResponse,
    UpdateWebServiceImageTagError);

public sealed class UpdateWebServiceImageTagHandler(
    LoadWebServiceById loadWebServiceById,
    LoadWebServiceDeploymentTokenByHash loadDeploymentTokenByHash,
    PersistUpdatedWebServiceOutcome persistUpdatedWebService,
    PersistCurrentWebServiceRelease persistCurrentWebServiceRelease,
    TouchWebServiceDeploymentToken touchDeploymentToken,
    RunMongoOutcomeTransaction<UpdateWebServiceImageTagOutcome> runMongoTransaction,
    ITenantContextAccessor tenantContextAccessor,
    LoadResourceDeploymentSet loadResourceDeploymentSet,
    RequestResourceDeploymentSetApply requestResourceDeploymentSetApply,
    TimeProvider timeProvider)
    : IMediatorOutcomeHandler<
        UpdateWebServiceImageTagRequest,
        UpdateWebServiceImageTagOutcome>
{
    public async Task<UpdateWebServiceImageTagOutcome> HandleAsync(
        UpdateWebServiceImageTagRequest request,
        CancellationToken cancellationToken = default)
    {
        var authorizationResult = await AuthorizeAsync(request, cancellationToken);
        if (!authorizationResult.TryGetValue(out var authorization, out var authorizationError))
        {
            return Error(new WebServiceImageTagStateError(
                authorizationError));
        }

        var webService = await loadWebServiceById(
            authorization.TenantId,
            request.Id,
            cancellationToken);

        if (webService is null)
            return Error(new WebServiceImageTagStateError(
                new WebServiceNotFound(request.Id)));
        if (webService.Lifecycle is not ActiveWebServiceLifecycle)
            return Error(new WebServiceImageTagStateError(
                new WebServiceUpdateUnavailable(webService.Id)));

        var deploymentInventory = await loadResourceDeploymentSet(
            webService.TenantId,
            WebServiceResourceDeployment.ResourceType,
            webService.Id,
            cancellationToken);
        var deployments = deploymentInventory
            .Where(WebServiceDeploymentWorkflow.IsDesiredPlacement)
            .ToArray();
        if (deployments.Length == 0)
            return Error(new WebServiceImageTagStateError(
                new WebServiceHasNoActiveDeployments(webService.Id)));

        return await runMongoTransaction(async (transaction, ct) =>
        {
            var nextSpec = webService.Spec with
            {
                Image = webService.Spec.Image with
                {
                    Tag = request.Body.Tag
                }
            };
            var updatedWebService = webService.UpdateDesiredState(
                nextSpec,
                webService.Tags);
            var applied = await requestResourceDeploymentSetApply(
                new ResourceDeploymentSetApplyRequest(
                    webService.TenantId,
                    WebServiceResourceDeployment.ResourceType,
                    webService.Id,
                    updatedWebService.ActiveRevision,
                    deployments.Select(deployment =>
                        WebServiceDeploymentWorkflow.ToSetApplyItem(deployment)).ToArray()),
                transaction.Session,
                ct);
            if (applied is ResourceDeploymentMutationError applyFailure)
                return transaction.Abort(Error(
                    new UpdateWebServiceImageTagError(applyFailure)));
            var updatedDeployments = ((MutatedResourceDeployments)applied.Value).Placements;

            var persisted = await persistUpdatedWebService(
                transaction.Session, webService, updatedWebService,
                updatedWebService.ToUpdatedEvent(
                    WebServiceDeploymentWorkflow.ToEventDeployments(updatedDeployments)), ct);
            if (persisted is WebServiceUpdateConflict persistenceError)
                return transaction.Abort(Error(
                    new UpdateWebServiceImageTagError(persistenceError)));

            if (authorization.DeploymentTokenId is not null)
            {
                await touchDeploymentToken(
                    transaction.Session,
                    authorization.TenantId,
                    webService.Id,
                    authorization.DeploymentTokenId,
                    timeProvider.GetUtcNow().UtcDateTime,
                    request.Body.Source,
                    ct);
            }

            var release = WebServiceReleaseFactory.CreatePending(
                webService.TenantId,
                webService.Id,
                updatedWebService.ActiveRevision,
                WebServiceReleaseAction.ImageTagUpdate,
                authorization.ActorUserId,
                authorization.ActorEmail,
                nextSpec,
                WebServiceDeploymentWorkflow.ToReleaseDeployments(updatedDeployments),
                traceId: null,
                correlationId: null,
                new WebServiceReleaseFactory.CreateOptions(
                    MongoDB.Bson.ObjectId.GenerateNewId().ToString(),
                    timeProvider.GetUtcNow().UtcDateTime),
                new ExternalImageTagUpdateProvenance(
                    request.Body.Source,
                    request.Body.CommitSha,
                    request.Body.BuildUrl,
                    request.Body.ImageDigest,
                    webService.Spec.Image.Tag,
                    request.Body.Tag,
                    authorization.ActorType,
                    authorization.DeploymentTokenId,
                    authorization.DeploymentTokenName));
            await persistCurrentWebServiceRelease(
                transaction.Session,
                release,
                ct);
            return transaction.Commit(new UpdateWebServiceImageTagOutcome(
                new UpdateWebServiceImageTagResponse(
                    webService.Id,
                    updatedWebService.ActiveRevision,
                    release.Id,
                    request.Body.Tag,
                    updatedDeployments)));
        }, cancellationToken);
    }

    private async Task<Result<
        ImageTagUpdateAuthorization,
        WebServiceImageTagAuthorizationError>> AuthorizeAsync(
        UpdateWebServiceImageTagRequest request,
        CancellationToken cancellationToken)
    {
        var bearerToken = ExtractBearerToken(request.Authorization);
        if (WebServiceDeploymentTokenFactory.IsRawDeploymentToken(bearerToken))
        {
            return await AuthorizeDeploymentTokenAsync(
                request,
                bearerToken!,
                cancellationToken);
        }

        var tenantContext = tenantContextAccessor.Current;
        if (tenantContext is null)
            return new Err<WebServiceImageTagAuthorizationError>(
                new WebServiceImageTagUnauthorized());

        if (!tenantContext.IsPlatformAdmin && !tenantContext.Role.CanWrite())
            return new Err<WebServiceImageTagAuthorizationError>(
                new WebServiceImageTagForbidden());

        return new Ok<ImageTagUpdateAuthorization>(new ImageTagUpdateAuthorization(
            tenantContext.TenantId,
            "User",
            request.ActorUserId.Value,
            request.ActorEmail.Value,
            DeploymentTokenId: null,
            DeploymentTokenName: null));
    }

    private async Task<Result<
        ImageTagUpdateAuthorization,
        WebServiceImageTagAuthorizationError>> AuthorizeDeploymentTokenAsync(
        UpdateWebServiceImageTagRequest request,
        string rawToken,
        CancellationToken cancellationToken)
    {
        var tokenHash = WebServiceDeploymentTokenFactory.Hash(rawToken);
        var token = await loadDeploymentTokenByHash(tokenHash, cancellationToken);

        if (token is null || token.RevokedAtUtc is not null)
            return new Err<WebServiceImageTagAuthorizationError>(
                new WebServiceImageTagUnauthorized());

        if (token.WebServiceId != request.Id)
            return new Err<WebServiceImageTagAuthorizationError>(
                new WebServiceImageTagForbidden());

        if (!string.IsNullOrWhiteSpace(request.TenantId) &&
            !string.Equals(request.TenantId, token.TenantId, StringComparison.Ordinal))
        {
            return new Err<WebServiceImageTagAuthorizationError>(
                new WebServiceImageTagForbidden());
        }

        if (!token.AllowedOperations.Contains(WebServiceDeploymentTokenOperations.UpdateImageTag))
            return new Err<WebServiceImageTagAuthorizationError>(
                new WebServiceImageTagForbidden());

        return new Ok<ImageTagUpdateAuthorization>(new ImageTagUpdateAuthorization(
            token.TenantId,
            "DeploymentToken",
            ActorUserId: null,
            ActorEmail: null,
            token.Id,
            token.Name));
    }

    private static string? ExtractBearerToken(string? authorizationHeader)
    {
        const string prefix = "Bearer ";
        return authorizationHeader?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true
            ? authorizationHeader[prefix.Length..].Trim()
            : null;
    }

    private sealed record ImageTagUpdateAuthorization(
        string TenantId,
        string ActorType,
        string? ActorUserId,
        string? ActorEmail,
        string? DeploymentTokenId,
        string? DeploymentTokenName);

    private static UpdateWebServiceImageTagOutcome Error(
        UpdateWebServiceImageTagError error) =>
        new(error);
}

public sealed class UpdateWebServiceImageTagValidator : AbstractValidator<UpdateWebServiceImageTagRequest>
{
    public UpdateWebServiceImageTagValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Body.Tag)
            .NotEmpty()
            .MaximumLength(100)
            .Matches("^[A-Za-z0-9_][A-Za-z0-9_.-]*$")
            .WithMessage("Image tag must start with a letter, number, or underscore and contain only letters, numbers, underscores, periods, or dashes.");
        RuleFor(x => x.Body.Source)
            .NotEmpty()
            .MaximumLength(100);
        RuleFor(x => x.Body.CommitSha)
            .Matches("^[a-fA-F0-9]{7,64}$")
            .When(x => !string.IsNullOrWhiteSpace(x.Body.CommitSha))
            .WithMessage("Commit SHA must be a 7 to 64 character hexadecimal value.");
        RuleFor(x => x.Body.BuildUrl)
            .MaximumLength(2048)
            .Must(url => string.IsNullOrWhiteSpace(url) || Uri.TryCreate(url, UriKind.Absolute, out _))
            .WithMessage("Build URL must be an absolute URL.");
        RuleFor(x => x.Body.ImageDigest)
            .MaximumLength(255);
    }
}
