using System.Text.Json.Serialization;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitOps.Application.Persistence;
using Pole.Features.GitOps.Contracts;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.GitOps.Application.Endpoints;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SubmitSnapshotBody(
    long ExpectedFence,
    string Commit,
    ManifestFile[] Files);

public sealed record SubmitSnapshotRequest(
    [FromRoute] string Id,
    [FromBody] SubmitSnapshotBody Body,
    [FromTenantHeader] string TenantId,
    [FromClaim(Claims.UserId)] ClaimValue<string> ActorUserId,
    [FromClaim(Claims.Email)] ClaimValue<string> ActorEmail) : IMediatorRequest<SubmitSnapshotOutcome>,
    ITenantWriteOperation;

public sealed record InvalidSnapshot(IReadOnlyList<DeclarationDiagnostic> Diagnostics);

public union SubmitSnapshotOutcome(
    SnapshotSourceStatus,
    SnapshotSourceMissing,
    StaleSnapshotConflict,
    SnapshotSourceRelocationRequired,
    InvalidSnapshot);

public sealed class SubmitSnapshotValidator : AbstractValidator<SubmitSnapshotRequest>
{
    public SubmitSnapshotValidator()
    {
        RuleFor(item => item.Id).MongoObjectId("Source ID");
        RuleFor(item => item.ActorUserId.Value).NotEmpty();
        RuleFor(item => item.ActorEmail.Value).NotEmpty().EmailAddress();
        RuleFor(item => item.Body).NotNull();
        When(item => item.Body is not null, () =>
            {
                RuleFor(item => item.Body.ExpectedFence).InclusiveBetween(0, long.MaxValue - 1);
                RuleFor(item => item.Body.Commit).NotEmpty().MaximumLength(SnapshotHttpLimits.MaximumCommitLength)
                    .Must(value => value is not null && !value.Any(char.IsControl));
                RuleFor(item => item.Body.Files).NotNull()
                    .Must(files => files is { Length: <= SnapshotSchema.MaximumFiles } && files.All(file =>
                        file is not null && file.Path is not null && file.Content is not null));
            });
    }
}

public sealed class SubmitSnapshotHandler(
    ManualSnapshotSources sources,
    DeclarativePipeline pipeline) : IMediatorOutcomeHandler<SubmitSnapshotRequest, SubmitSnapshotOutcome>
{
    public async Task<SubmitSnapshotOutcome> HandleAsync(
        SubmitSnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        var source = await sources.LoadAsync(request.TenantId, request.Id, cancellationToken);

        if (source is null)
            return new SnapshotSourceMissing();

        var result = await pipeline.ApplyAsync(source.TenantId, source.Id, source.SourceUrl, request.Body.Commit,
            request.Body.ExpectedFence,
            request.ActorUserId.Value, request.ActorEmail.Value, request.Body.Files, cancellationToken);

        if (!result.Snapshot.Valid)
            return new InvalidSnapshot(result.Snapshot.Diagnostics);

        if (result.Status == SnapshotApplicationStatus.StaleExecution)
            return new StaleSnapshotConflict();

        if (result.Status == SnapshotApplicationStatus.SourceRelocationRequired)
            return new SnapshotSourceRelocationRequired();

        return await sources.StatusAsync(source, cancellationToken);
    }
}

public static class SubmitSnapshotEndpoint
{
    public static IEndpointRouteBuilder MapSubmitSnapshotEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPut("/{Id}/snapshot", async (
                IMediator mediator,
                [AsParameters] SubmitSnapshotRequest request,
                CancellationToken ct) =>
            (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    SnapshotSourceStatus source => Results.Accepted(value: source),
                    SnapshotSourceMissing error => SnapshotHttpMappings.Map(error),
                    StaleSnapshotConflict error => SnapshotHttpMappings.Map(error),
                    SnapshotSourceRelocationRequired error => SnapshotHttpMappings.Map(error),
                    InvalidSnapshot invalid => SnapshotHttpMappings.Map(invalid)
                })).WithName("SubmitManualSnapshot")
            .WithMetadata(new RequestSizeLimitAttribute(SnapshotHttpLimits.MaximumSnapshotRequestBytes));

        return app;
    }
}
