using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Features.Backups.Application.Configuration;
using Pole.Features.Backups.Application.Execution;
using Pole.Features.Backups.Contracts;
using Pole.Features.Backups.Domain;
using Pole.Features.Core.Multitenancy;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.Backups.Application.Endpoints;

public sealed record RestoreBackupBody(
    string IdempotencyKey,
    string Name,
    string? BackupId,
    string? TargetTime,
    bool Confirmed,
    int? StorageGiB = null
);

public sealed record RestoreBackupRequest(
    [FromTenantHeader] string TenantId,
    [FromRoute] string Id,
    [FromBody] RestoreBackupBody Body
) : IMediatorRequest<RestoreBackupOutcome>, ITenantWriteOperation;

public union RestoreBackupOutcome(BackupOperationDto, BackupRequestRejected);

public sealed class RestoreBackupValidator : AbstractValidator<RestoreBackupRequest>
{
    public RestoreBackupValidator()
    {
        RuleFor(r => r.Id).MongoObjectId("Source ID");
        RuleFor(r => r.Body).NotNull();
        When(
            r => r.Body is not null,
            () =>
            {
                RuleFor(r => r.Body.IdempotencyKey).NotEmpty().MaximumLength(128);
                RuleFor(r => r.Body.Name).NotEmpty().MaximumLength(63);
                RuleFor(r => r.Body.Confirmed).Equal(true);
                RuleFor(r => r.Body.StorageGiB).GreaterThan(0);
                RuleFor(r => r.Body)
                    .Must(b => (b.BackupId is null) != (b.TargetTime is null) &&
                        (b.BackupId is null || !string.IsNullOrWhiteSpace(b.BackupId) && b.BackupId.Length <= 128) &&
                        (b.TargetTime is null || BackupHttpProblems.TryTime(b.TargetTime, out _)));
            });
    }
}

public sealed class RestoreBackupHandler(
    BackupOperations commands,
    ITenantContextAccessor tenant
) : IMediatorOutcomeHandler<RestoreBackupRequest, RestoreBackupOutcome>
{
    public async Task<RestoreBackupOutcome> HandleAsync(RestoreBackupRequest request, CancellationToken ct = default)
    {
        if (tenant.Current is not { } current || (!current.IsPlatformAdmin && !current.Role.CanManageMembers()))
            return new BackupRequestRejected("admin-required");
        DateTime? time = null;
        if (request.Body.TargetTime is { } requested)
        {
            if (!BackupHttpProblems.TryTime(requested, out var instant))
                return new BackupRequestRejected("invalid-target-time");
            time = instant.UtcDateTime;
        }

        var answer = await commands.StartAsync(
            new(
                request.TenantId,
                request.Body.IdempotencyKey,
                request.Id,
                true,
                request.Body.Name,
                request.Body.BackupId,
                time,
                request.Body.StorageGiB),
            ct);
        return answer switch
        {
            BackupWriteResult<BackupOperation>.Saved ok => BackupViews.From(ok.Value),
            BackupWriteResult<BackupOperation>.Rejected failure => new BackupRequestRejected(failure.Code)
        };
    }
}

public static class RestoreBackupEndpoint
{
    public static IEndpointRouteBuilder MapRestoreBackup(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
                "/archives/{Id}/restores",
                async (
                    IMediator mediator, [AsParameters] RestoreBackupRequest request,
                    CancellationToken ct) => (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    BackupOperationDto value => Results.Accepted($"/api/v1/backups/operations/{value.Id}", value),
                    BackupRequestRejected error => BackupHttpProblems.From(error)
                }))
            .WithName("Backups.RestoreBackup");
        return endpoints;
    }
}
