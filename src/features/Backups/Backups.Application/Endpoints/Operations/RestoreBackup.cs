// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Backups.Application.Archives;
using Pole.Features.Backups.Application.Common;
using Pole.Features.Backups.Application.Configuration;
using Pole.Features.Backups.Application.Operations;
using Pole.Features.Backups.Contracts;
using Pole.Features.Backups.Domain;
using Pole.Features.Core.Multitenancy;
using Pole.Mediator;
using Pole.Validation;
using static Pole.Features.Backups.Application.Common.BackupArchiveHttpMappings;
using static Pole.Features.Backups.Application.Common.BackupOperationHttpMappings;
using static Pole.Features.Backups.Application.Common.BackupPolicyHttpMappings;

namespace Pole.Features.Backups.Application.Endpoints.Operations;

public sealed record RestoreBackupBody(
    string IdempotencyKey,
    string Name,
    string? BackupId,
    string? TargetTime,
    bool Confirmed,
    int? StorageGiB
);

public sealed record RestoreBackupRequest(
    [FromTenantHeader] string TenantId,
    [FromRoute] string Id,
    [FromBody] RestoreBackupBody Body
) : IMediatorRequest<RestoreBackupOutcome>, ITenantWriteOperation;

public union RestoreBackupOutcome(
    BackupOperationDto,
    BackupArchiveMissing,
    BackupCommandIdempotencyConflict,
    BackupRestoreStorageTooSmall,
    BackupRestoreCopyUnavailable,
    BackupRestoreTimeUnavailable,
    BackupRestoreTimeUnsupported,
    BackupSourceUnsupported,
    BackupCatalogCredentialUnavailable,
    BackupCatalogAccessDenied,
    BackupCatalogUnavailable,
    BackupCatalogUnreadable,
    BackupCatalogTimeout,
    BackupAdministrationRequired,
    BackupRestoreSelectionInvalid);

public sealed record BackupRestoreSelectionInvalid;

public sealed class RestoreBackupValidator : AbstractValidator<RestoreBackupRequest>
{
    public RestoreBackupValidator()
    {
        RuleFor(r => r.Id).MongoObjectId("Archive ID");
        RuleFor(r => r.Body).NotNull();
        When(
            r => r.Body is not null,
            () =>
            {
                RuleFor(r => r.Body.IdempotencyKey).NotEmpty().MaximumLength(128);
                RuleFor(r => r.Body.Name).NotEmpty().MaximumLength(63);
                RuleFor(r => r.Body.Confirmed).Equal(true);
                RuleFor(r => r.Body.StorageGiB).GreaterThan(0);
                RuleFor(r => r.Body.BackupId)
                    .Must(value => value is null || !string.IsNullOrWhiteSpace(value) && value.Length <= 128)
                    .WithMessage("Use a nonempty backup ID of at most 128 characters.");
                RuleFor(r => r.Body.BackupId)
                    .NotEmpty()
                    .When(r => r.Body.TargetTime is null)
                    .WithMessage("Select a backup ID or a recovery time.");
                RuleFor(r => r.Body.TargetTime)
                    .Must(value => value is null || BackupRestoreTime.TryTime(value, out _))
                    .WithMessage("Use an offset timestamp with at most microsecond precision.");
                RuleFor(r => r.Body.TargetTime)
                    .Null()
                    .When(r => r.Body.BackupId is not null)
                    .WithMessage("Select either a backup ID or a recovery time, not both.");
            });
    }
}

public sealed class RestoreBackupHandler(
    RequestBackupRestore commands,
    ITenantContextAccessor tenant
) : IMediatorOutcomeHandler<RestoreBackupRequest, RestoreBackupOutcome>
{
    public async Task<RestoreBackupOutcome> HandleAsync(RestoreBackupRequest request, CancellationToken ct = default)
    {
        if (tenant.Current is not { } current || (!current.IsPlatformAdmin && !current.Role.CanManageMembers()))
            return new BackupAdministrationRequired();
        BackupRestoreSelection selection;
        switch (request.Body)
        {
            case { BackupId: { } copy, TargetTime: null }:
                selection = new BackupCopySelection(copy);
                break;
            case { BackupId: null, TargetTime: { } text } when BackupRestoreTime.TryTime(text, out var instant):
                selection = new BackupTimeSelection(instant.UtcDateTime);
                break;
            default:
                return new BackupRestoreSelectionInvalid();
        }

        var answer = await commands.ExecuteAsync(
            new RestoreArchiveCommand(
                request.TenantId,
                request.Body.IdempotencyKey,
                request.Id,
                selection,
                request.Body.Name,
                request.Body.StorageGiB),
            ct);
        return answer switch
        {
            BackupOperation operation => BackupViews.From(operation),
            BackupArchiveMissing error => error,
            BackupCommandIdempotencyConflict error => error,
            BackupRestoreStorageTooSmall error => error,
            BackupRestoreCopyUnavailable error => error,
            BackupRestoreTimeUnavailable error => error,
            BackupRestoreTimeUnsupported error => error,
            BackupSourceUnsupported error => error,
            BackupCatalogCredentialUnavailable error => error,
            BackupCatalogAccessDenied error => error,
            BackupCatalogUnavailable error => error,
            BackupCatalogUnreadable error => error,
            BackupCatalogTimeout error => error,
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
                    BackupArchiveMissing error => MapArchiveMissing(error),
                    BackupCommandIdempotencyConflict error => MapCommandIdempotencyConflict(error),
                    BackupRestoreStorageTooSmall error => MapRestoreStorageTooSmall(error),
                    BackupRestoreCopyUnavailable error => MapRestoreCopyUnavailable(error),
                    BackupRestoreTimeUnavailable error => MapRestoreTimeUnavailable(error),
                    BackupRestoreTimeUnsupported error => MapRestoreTimeUnsupported(error),
                    BackupSourceUnsupported error => MapSourceUnsupported(error),
                    BackupCatalogCredentialUnavailable error => MapCatalogCredentialUnavailable(error),
                    BackupCatalogAccessDenied error => MapCatalogAccessDenied(error),
                    BackupCatalogUnavailable error => MapCatalogUnavailable(error),
                    BackupCatalogUnreadable error => MapCatalogUnreadable(error),
                    BackupCatalogTimeout error => MapCatalogTimeout(error),
                    BackupAdministrationRequired error => MapAdministrationRequired(error),
                    BackupRestoreSelectionInvalid error => MapRestoreSelectionInvalid(error),
                }))
            .WithName("Backups.RestoreBackup");
        return endpoints;
    }
}
