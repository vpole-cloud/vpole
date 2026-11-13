using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Backups.Application.Archives;
using Pole.Features.Backups.Application.Common;
using Pole.Features.Core.Multitenancy;
using Pole.Mediator;
using Pole.Validation;
using static Pole.Features.Backups.Application.Common.BackupArchiveHttpMappings;

namespace Pole.Features.Backups.Application.Endpoints.Archives;

public sealed record ForgetBackupArchiveRequest(
    [FromTenantHeader] string TenantId,
    [FromRoute] string Id,
    [FromQuery] bool Confirmed
) : IMediatorRequest<ForgetBackupArchiveOutcome>, ITenantWriteOperation;

public union ForgetBackupArchiveOutcome(
    BackupArchiveForgotten,
    BackupArchiveMissing,
    BackupArchiveInUse,
    ArchivePlacementConflict,
    ArchiveObservationUnavailable,
    BackupAdministrationRequired);

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
            return new BackupAdministrationRequired();
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
                    BackupArchiveMissing error => MapArchiveMissing(error),
                    BackupArchiveInUse error => MapArchiveInUse(error),
                    ArchiveObservationUnavailable error => MapArchiveObservationUnavailable(error),
                    ArchivePlacementConflict error => MapArchivePlacementConflict(error),
                    BackupAdministrationRequired error => MapAdministrationRequired(error)
                }))
            .WithName("Backups.ForgetBackupArchive");
        return endpoints;
    }
}
