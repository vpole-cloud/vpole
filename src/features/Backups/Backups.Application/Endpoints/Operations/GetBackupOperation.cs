using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Backups.Application.Configuration;
using Pole.Features.Backups.Application.Execution;
using Pole.Features.Core.Multitenancy;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.Backups.Application.Endpoints.Operations;

public sealed record GetBackupOperationRequest(
    [FromTenantHeader] string TenantId,
    [FromRoute] string Id
) : IMediatorRequest<GetBackupOperationOutcome>, ITenantReadOperation;

public sealed record BackupOperationMissing;

public union GetBackupOperationOutcome(BackupOperationDto, BackupOperationMissing);

public sealed class GetBackupOperationValidator : AbstractValidator<GetBackupOperationRequest>
{
    public GetBackupOperationValidator() => RuleFor(r => r.Id).MongoObjectId("Resource ID");
}

public sealed class GetBackupOperationHandler(
    BackupReads reads
) : IMediatorOutcomeHandler<GetBackupOperationRequest, GetBackupOperationOutcome>
{
    public async Task<GetBackupOperationOutcome> HandleAsync(
        GetBackupOperationRequest request,
        CancellationToken ct = default)
    {
        var item = await reads.OperationAsync(request.TenantId, request.Id, ct);
        return item is null ? new BackupOperationMissing() : item;
    }
}

public static class GetBackupOperationEndpoint
{
    public static IEndpointRouteBuilder MapGetBackupOperation(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                "/operations/{Id}",
                async (
                    IMediator mediator, [AsParameters] GetBackupOperationRequest request,
                    CancellationToken ct) => (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    BackupOperationDto item => Results.Ok(item),
                    BackupOperationMissing => BackupHttpProblems.From(new("not-found"))
                }))
            .WithName("Backups.GetBackupOperation");
        return endpoints;
    }
}
