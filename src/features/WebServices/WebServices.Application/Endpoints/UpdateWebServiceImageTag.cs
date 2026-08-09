using System.Collections.Immutable;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Events.Abstractions;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Persistence;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Events;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.WebServices.Application.Endpoints;

public static class UpdateWebServiceImageTag
{
    public static IEndpointRouteBuilder MapUpdateWebServiceImageTagEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPatch("/{Id}/image-tag", async (IMediator mediator, [AsParameters] UpdateWebServiceImageTagRequest request, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(response => Results.Accepted(value: response));
            })
            .WithName("UpdateWebServiceImageTag")
            .AllowAnonymous()
            .Produces<UpdateWebServiceImageTagResponse>(StatusCodes.Status202Accepted);

        return app;
    }
}

public sealed record UpdateWebServiceImageTagRequest(
    [FromRoute] string Id,
    [FromBody] UpdateWebServiceImageTagRequestBody Body,
    [FromHeader(Name = TenantHeaders.TenantId)] string? TenantId = null,
    [FromHeader(Name = "Authorization")] string? Authorization = null,
    [FromClaim(Claims.UserId)] ClaimValue<string?> ActorUserId = default,
    [FromClaim(Claims.Email)] ClaimValue<string?> ActorEmail = default
) : IMediatorRequest<UpdateWebServiceImageTagResponse>;

public sealed record UpdateWebServiceImageTagRequestBody(
    string Tag,
    string Source,
    string? CommitSha = null,
    string? BuildUrl = null,
    string? ImageDigest = null);

public sealed record UpdateWebServiceImageTagResponse(
    string Id,
    int Revision,
    string ReleaseId,
    string Tag,
    PlacementSummaryDto Placement);

public sealed class UpdateWebServiceImageTagHandler(
    LoadWebServiceById loadWebServiceById,
    LoadWebServiceDeploymentTokenByHash loadDeploymentTokenByHash,
    PersistUpdatedWebService persistUpdatedWebService,
    PersistCurrentWebServiceRelease persistCurrentWebServiceRelease,
    TouchWebServiceDeploymentToken touchDeploymentToken,
    RunMongoTransaction<UpdateWebServiceImageTagResponse> runMongoTransaction,
    ITenantContextAccessor tenantContextAccessor,
    IResourceDeploymentComputeLookup deploymentComputeLookup,
    IResourceDeploymentMutationWriter deploymentWriter)
    : IMediatorHandler<UpdateWebServiceImageTagRequest, UpdateWebServiceImageTagResponse>
{
    public async Task<Result<UpdateWebServiceImageTagResponse>> HandleAsync(
        UpdateWebServiceImageTagRequest request,
        CancellationToken cancellationToken = default)
    {
        var authorizationResult = await AuthorizeAsync(request, cancellationToken);
        if (!authorizationResult.TryGetValue(out var authorization, out var authorizationFailure))
            return authorizationFailure;

        var webService = await loadWebServiceById(
            authorization.TenantId,
            request.Id,
            cancellationToken);

        if (webService is null)
            return Failures.NotFound("Web service not found.", request.Id);
        if (webService.Lifecycle is not ActiveWebServiceLifecycle)
            return Failures.Conflict("A deleting web service cannot be updated.");

        var computeLookupResult = await deploymentComputeLookup.GetCurrentAsync(
            webService.TenantId,
            WebServiceResourceDeployment.ResourceType,
            webService.Id,
            cancellationToken);
        if (!computeLookupResult.TryGetValue(
                out var currentCompute,
                out var computeLookupFailure))
            return computeLookupFailure;

        return await runMongoTransaction(async (session, ct) =>
        {
            var previousSpec = webService.Spec.ToSnapshot();
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
            var nextSpecSnapshot = nextSpec.ToSnapshot();
            var deploymentEventCore = EventCore.Create(webService.TenantId);

            var changes = new WebServiceUpdatedEventChanges(
                UpdateLog<WebServiceSpecSnapshot>.Of(previousSpec, nextSpecSnapshot),
                UpdateLog<ImmutableHashSet<string>>.Of(webService.Tags, webService.Tags));
            var updatedEvent = new WebServiceUpdatedEvent(
                webService.Id,
                webService.Name,
                webService.ResourceGroupId,
                webService.DnsRecord.ToSnapshot(),
                changes)
            {
                EventCore = EventCore.Create(webService.TenantId)
            };
            var replace = await persistUpdatedWebService(
                session,
                webService,
                updatedWebService,
                updatedEvent,
                ct);
            if (replace is not ISuccess)
                return (Failure)replace;

            if (authorization.DeploymentTokenId is not null)
            {
                await touchDeploymentToken(
                    session,
                    authorization.TenantId,
                    webService.Id,
                    authorization.DeploymentTokenId,
                    deploymentEventCore.OccurredOn,
                    request.Body.Source,
                    ct);
            }

            var deploymentResult = await deploymentWriter.RequestApplyAsync(
                new ResourceDeploymentMutationRequest(
                    webService.TenantId,
                    WebServiceResourceDeployment.ResourceType,
                    webService.Id,
                    currentCompute.Compute.Profile),
                session,
                ct);
            if (!deploymentResult.TryGetValue(
                    out var deployment,
                    out var deploymentFailure))
            {
                return deploymentFailure;
            }

            var release = WebServiceReleaseFactory.CreatePending(
                webService.TenantId,
                webService.Id,
                updatedWebService.ActiveRevision,
                WebServiceReleaseAction.ImageTagUpdate,
                authorization.ActorUserId,
                authorization.ActorEmail,
                nextSpec,
                deploymentEventCore.TraceId,
                deploymentEventCore.CorrelationId,
                deploymentEventCore.OccurredOn,
                new ExternalImageTagUpdateProvenance(
                    request.Body.Source,
                    request.Body.CommitSha,
                    request.Body.BuildUrl,
                    request.Body.ImageDigest,
                    webService.Spec.Image.Tag,
                    request.Body.Tag,
                    authorization.ActorType,
                    authorization.DeploymentTokenId,
                    authorization.DeploymentTokenName),
                deployment.DesiredGeneration);
            await persistCurrentWebServiceRelease(
                session,
                release,
                ct);
            return new UpdateWebServiceImageTagResponse(
                webService.Id,
                updatedWebService.ActiveRevision,
                release.Id,
                request.Body.Tag,
                deployment);
        }, cancellationToken);
    }

    private async Task<Result<ImageTagUpdateAuthorization>> AuthorizeAsync(
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
            return Failures.Unauthorized("A user access token or service deployment token is required.", "Update web service image tag");

        if (!tenantContext.IsPlatformAdmin && !tenantContext.Role.CanWrite())
            return Failures.Forbidden("Tenant member role is required.", "Update web service image tag");

        return new ImageTagUpdateAuthorization(
            tenantContext.TenantId,
            "User",
            request.ActorUserId.Value,
            request.ActorEmail.Value,
            DeploymentTokenId: null,
            DeploymentTokenName: null);
    }

    private async Task<Result<ImageTagUpdateAuthorization>> AuthorizeDeploymentTokenAsync(
        UpdateWebServiceImageTagRequest request,
        string rawToken,
        CancellationToken cancellationToken)
    {
        var tokenHash = WebServiceDeploymentTokenFactory.Hash(rawToken);
        var token = await loadDeploymentTokenByHash(tokenHash, cancellationToken);

        if (token is null || token.RevokedAtUtc is not null)
            return Failures.Unauthorized("Deployment token is invalid or has been revoked.", "Update web service image tag");

        if (token.WebServiceId != request.Id)
            return Failures.Forbidden("Deployment token is not scoped to this web service.", "Update web service image tag");

        if (!string.IsNullOrWhiteSpace(request.TenantId) &&
            !string.Equals(request.TenantId, token.TenantId, StringComparison.Ordinal))
        {
            return Failures.Forbidden("Deployment token tenant does not match the requested tenant.", "Update web service image tag");
        }

        if (!token.AllowedOperations.Contains(WebServiceDeploymentTokenOperations.UpdateImageTag))
            return Failures.Forbidden("Deployment token is not allowed to update image tags.", "Update web service image tag");

        return new ImageTagUpdateAuthorization(
            token.TenantId,
            "DeploymentToken",
            ActorUserId: null,
            ActorEmail: null,
            token.Id,
            token.Name);
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
