// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json.Serialization;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitOps.Application.Lifecycle;
using Pole.Features.GitOps.Application.Sources;
using Pole.Features.GitOps.Domain;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.GitOps.Application.Endpoints.Sources.Previews;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ConfirmLifecyclePreviewBody(
    long ExpectedRevision
);

public sealed record ConfirmLifecyclePreviewRequest(
    [FromRoute] string Id,
    [FromRoute] string PreviewId,
    [FromBody] ConfirmLifecyclePreviewBody Body,
    [FromTenantHeader] string TenantId,
    [FromClaim(Claims.UserId)] ClaimValue CurrentUserId
) : IMediatorRequest<ConfirmLifecyclePreviewOutcome>, ITenantWriteOperation;

public sealed record ConfirmLifecyclePreviewResponse(
    string RunId
);

public union ConfirmLifecyclePreviewOutcome(
    ConfirmLifecyclePreviewResponse,
    GitSourceNotFound,
    GitSourceBusy,
    GitPreviewNotFound,
    GitPreviewStale,
    GitPreviewReadFailed);

public sealed class ConfirmLifecyclePreviewValidator : AbstractValidator<ConfirmLifecyclePreviewRequest>
{
    public ConfirmLifecyclePreviewValidator()
    {
        RuleFor(item => item.Id).MongoObjectId("Source ID");
        RuleFor(item => item.PreviewId).MongoObjectId("Preview ID");
        RuleFor(item => item.Body).NotNull();
        When(item => item.Body is not null, () => RuleFor(item => item.Body.ExpectedRevision).GreaterThan(0));
    }
}

public sealed class ConfirmLifecyclePreviewHandler(
    GitLifecyclePreviews previews
) : IMediatorOutcomeHandler<ConfirmLifecyclePreviewRequest, ConfirmLifecyclePreviewOutcome>
{
    public async Task<ConfirmLifecyclePreviewOutcome> HandleAsync(
        ConfirmLifecyclePreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await previews.ConfirmAsync(
            new ConfirmLifecyclePreview(
                new GitSourceIdentity(
                    ObjectId.Parse(request.TenantId).ToString(),
                    ObjectId.Parse(request.Id).ToString()),
                ObjectId.Parse(request.PreviewId).ToString(),
                request.Body.ExpectedRevision,
                request.CurrentUserId.Value),
            cancellationToken);
        return result switch
        {
            GitLifecycleScheduled scheduled => new ConfirmLifecyclePreviewResponse(scheduled.RunId),
            GitSourceNotFound missing => missing,
            GitSourceBusy busy => busy,
            GitPreviewNotFound missing => missing,
            GitPreviewStale stale => stale,
            GitPreviewReadFailed failed => failed,
        };
    }
}

public static class ConfirmLifecyclePreviewEndpoint
{
    public static IEndpointRouteBuilder MapConfirmLifecyclePreviewEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost(
                "/{Id}/previews/{PreviewId}/confirm",
                async (
                    IMediator mediator, [AsParameters] ConfirmLifecyclePreviewRequest request,
                    CancellationToken ct) => (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    ConfirmLifecyclePreviewResponse scheduled => Results.Accepted(
                        $"/api/v1/gitops/sources/{request.Id}/runs/{scheduled.RunId}",
                        scheduled),
                    GitSourceNotFound missing => RepositoryHttpMappings.Map(missing),
                    GitSourceBusy busy => RepositoryHttpMappings.Map(busy),
                    GitPreviewNotFound missing => LifecycleHttpMappings.Map(missing),
                    GitPreviewStale stale => LifecycleHttpMappings.Map(stale),
                    GitPreviewReadFailed failed => LifecycleHttpMappings.Map(failed),
                }))
            .WithName("ConfirmGitLifecyclePreview");
        return app;
    }
}
