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

namespace Pole.Features.Backups.Application.Endpoints.Policies;

public sealed record DeleteBackupPolicyRequest(
    [FromTenantHeader] string TenantId,
    [FromRoute] string Id,
    [FromQuery] long ExpectedRevision
) : IMediatorRequest<DeleteBackupPolicyOutcome>, ITenantWriteOperation;

public union DeleteBackupPolicyOutcome(BackupDeletionAccepted, BackupRequestRejected);

public sealed class DeleteBackupPolicyValidator : AbstractValidator<DeleteBackupPolicyRequest>
{
    public DeleteBackupPolicyValidator()
    {
        RuleFor(r => r.Id).MongoObjectId("Resource ID");
        RuleFor(r => r.ExpectedRevision).GreaterThan(0);
    }
}

public sealed class DeleteBackupPolicyHandler(
    ManualBackupConfiguration configuration
) : IMediatorOutcomeHandler<DeleteBackupPolicyRequest, DeleteBackupPolicyOutcome>
{
    public async Task<DeleteBackupPolicyOutcome> HandleAsync(
        DeleteBackupPolicyRequest request,
        CancellationToken ct = default)
    {
        var answer = await configuration.DeleteAsync(
            request.TenantId,
            "BackupPolicy",
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

public static class DeleteBackupPolicyEndpoint
{
    public static IEndpointRouteBuilder MapDeleteBackupPolicy(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapDelete(
                "/policies/{Id}",
                async (
                    IMediator mediator, [AsParameters] DeleteBackupPolicyRequest request,
                    CancellationToken ct) => (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    BackupDeletionAccepted value => Results.NoContent(),
                    BackupRequestRejected error => BackupHttpProblems.From(error)
                }))
            .WithName("Backups.DeleteBackupPolicy");
        return endpoints;
    }
}
