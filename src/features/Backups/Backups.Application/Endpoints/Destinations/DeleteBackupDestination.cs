using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Backups.Application.Destinations;
using Pole.Features.Core.Multitenancy;
using Pole.Mediator;
using Pole.Validation;
using static Pole.Features.Backups.Application.Common.BackupDestinationHttpMappings;

namespace Pole.Features.Backups.Application.Endpoints.Destinations;

public sealed record DeleteBackupDestinationRequest(
    [FromTenantHeader] string TenantId,
    [FromRoute] string Id,
    [FromQuery] long ExpectedRevision
) : IMediatorRequest<DeleteBackupDestinationOutcome>, ITenantWriteOperation;

public union DeleteBackupDestinationOutcome(
    BackupDestinationDeleted,
    BackupDestinationMissing,
    BackupDestinationRevisionConflict,
    BackupDestinationInUse,
    BackupDestinationCleanupPending,
    BackupDestinationRegistrationConflict,
    BackupDestinationGitOwned);

public sealed class DeleteBackupDestinationValidator : AbstractValidator<DeleteBackupDestinationRequest>
{
    public DeleteBackupDestinationValidator()
    {
        RuleFor(r => r.Id).MongoObjectId("Resource ID");
        RuleFor(r => r.ExpectedRevision).GreaterThan(0);
    }
}

public sealed class DeleteBackupDestinationHandler(
    DeleteBackupDestination deletion
) : IMediatorOutcomeHandler<DeleteBackupDestinationRequest, DeleteBackupDestinationOutcome>
{
    public async Task<DeleteBackupDestinationOutcome> HandleAsync(
        DeleteBackupDestinationRequest request,
        CancellationToken ct = default)
    {
        var answer = await deletion.ExecuteAsync(new(request.TenantId, request.Id, request.ExpectedRevision), ct);
        return answer switch
        {
            BackupDestinationDeleted value => value,
            BackupDestinationMissing error => error,
            BackupDestinationRevisionConflict error => error,
            BackupDestinationInUse error => error,
            BackupDestinationCleanupPending error => error,
            BackupDestinationRegistrationConflict error => error,
            BackupDestinationGitOwned error => error,
        };
    }
}

public static class DeleteBackupDestinationEndpoint
{
    public static IEndpointRouteBuilder MapDeleteBackupDestination(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapDelete(
                "/destinations/{Id}",
                async (
                    IMediator mediator, [AsParameters] DeleteBackupDestinationRequest request,
                    CancellationToken ct) => (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    BackupDestinationDeleted => Results.NoContent(),
                    BackupDestinationMissing error => MapDestinationMissing(error),
                    BackupDestinationRevisionConflict error => MapDestinationRevisionConflict(error),
                    BackupDestinationInUse error => MapDestinationInUse(error),
                    BackupDestinationCleanupPending error => MapDestinationCleanupPending(error),
                    BackupDestinationRegistrationConflict error => MapDestinationRegistrationConflict(error),
                    BackupDestinationGitOwned error => MapDestinationGitOwned(error),
                }))
            .WithName("Backups.DeleteBackupDestination");
        return endpoints;
    }
}
