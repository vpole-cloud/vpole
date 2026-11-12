using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Backups.Application.Execution;
using Pole.Features.Core.Multitenancy;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.Backups.Application.Endpoints;

public sealed record ForgetBackupArchiveRequest(
    [FromTenantHeader] string TenantId,
    [FromRoute] string Id,
    [FromQuery] bool Confirmed
) : IMediatorRequest<ForgetBackupArchiveOutcome>, ITenantWriteOperation;

public sealed record ArchiveAccessForbidden;

public union ForgetBackupArchiveOutcome(
    BackupArchiveForgotten,
    BackupArchiveMissing,
    BackupArchiveInUse,
    ArchivePlacementConflict,
    ArchiveObservationUnavailable,
    ArchiveAccessForbidden);

public sealed class ForgetBackupArchiveValidator : AbstractValidator<ForgetBackupArchiveRequest>
{
    public ForgetBackupArchiveValidator()
    {
        RuleFor(r => r.Id).MongoObjectId("Archive ID");
        RuleFor(r => r.Confirmed).Equal(true);
    }
}

public sealed class ForgetBackupArchiveHandler(
    ForgetRetiredArchive archives,
    ITenantContextAccessor tenant
) : IMediatorOutcomeHandler<ForgetBackupArchiveRequest, ForgetBackupArchiveOutcome>
{
    public async Task<ForgetBackupArchiveOutcome> HandleAsync(
        ForgetBackupArchiveRequest request,
        CancellationToken ct = default)
    {
        if (tenant.Current is not { } current || (!current.IsPlatformAdmin && !current.Role.CanManageMembers()))
            return new ArchiveAccessForbidden();
        return (await archives.ExecuteAsync(request.TenantId, request.Id, ct)) switch
        {
            BackupArchiveForgotten forgotten => forgotten,
            BackupArchiveMissing missing => missing,
            BackupArchiveInUse used => used,
            ArchiveObservationUnavailable unavailable => unavailable,
            ArchivePlacementConflict conflict => conflict
        };
    }
}

public static class ForgetBackupArchiveEndpoint
{
    public static IEndpointRouteBuilder MapForgetBackupArchive(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapDelete(
                "/archives/{Id}",
                async (
                    IMediator mediator, [AsParameters] ForgetBackupArchiveRequest request,
                    CancellationToken ct) => (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    BackupArchiveForgotten => Results.NoContent(),
                    BackupArchiveMissing => BackupHttpProblems.From(new("not-found")),
                    BackupArchiveInUse => BackupHttpProblems.From(new("archive-in-use")),
                    ArchiveObservationUnavailable => BackupHttpProblems.From(new("archive-observation-unavailable")),
                    ArchivePlacementConflict => BackupHttpProblems.From(new("placement-conflict")),
                    ArchiveAccessForbidden => BackupHttpProblems.From(new("admin-required"))
                }))
            .WithName("Backups.ForgetBackupArchive");
        return endpoints;
    }
}
