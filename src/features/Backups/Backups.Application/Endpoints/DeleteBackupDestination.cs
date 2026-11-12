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

public sealed record DeleteBackupDestinationRequest(
    [FromTenantHeader] string TenantId,
    [FromRoute] string Id,
    [FromQuery] long ExpectedRevision
) : IMediatorRequest<DeleteBackupDestinationOutcome>, ITenantWriteOperation;

public union DeleteBackupDestinationOutcome(BackupDeletionAccepted, BackupRequestRejected);

public sealed class DeleteBackupDestinationValidator : AbstractValidator<DeleteBackupDestinationRequest>
{
    public DeleteBackupDestinationValidator()
    {
        RuleFor(r => r.Id).MongoObjectId("Resource ID");
        RuleFor(r => r.ExpectedRevision).GreaterThan(0);
    }
}

public sealed class DeleteBackupDestinationHandler(
    ManualBackupConfiguration configuration
) : IMediatorOutcomeHandler<DeleteBackupDestinationRequest, DeleteBackupDestinationOutcome>
{
    public async Task<DeleteBackupDestinationOutcome> HandleAsync(
        DeleteBackupDestinationRequest request,
        CancellationToken ct = default)
    {
        var answer = await configuration.DeleteAsync(
            request.TenantId,
            "BackupDestination",
            request.Id,
            request.ExpectedRevision,
            ct);
        return answer switch
        {
            BackupWriteResult<bool>.Saved ok => new BackupDeletionAccepted(),
            BackupWriteResult<bool>.Rejected failure => new BackupRequestRejected(failure.Code)
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
                    BackupDeletionAccepted value => Results.NoContent(),
                    BackupRequestRejected error => BackupHttpProblems.From(error)
                }))
            .WithName("Backups.DeleteBackupDestination");
        return endpoints;
    }
}
