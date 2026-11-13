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

namespace Pole.Features.Backups.Application.Endpoints.Operations;

public sealed record ListBackupOperationsRequest(
    [FromTenantHeader] string TenantId
) : IMediatorRequest<ListBackupOperationsOutcome>, ITenantReadOperation;

public union ListBackupOperationsOutcome(BackupOperationDto[]);

public sealed class ListBackupOperationsHandler(
    BackupReads reads
) : IMediatorOutcomeHandler<ListBackupOperationsRequest, ListBackupOperationsOutcome>
{
    public async Task<ListBackupOperationsOutcome> HandleAsync(
        ListBackupOperationsRequest request,
        CancellationToken ct = default)
    {
        return await reads.OperationsAsync(request.TenantId, ct);
    }
}

public static class ListBackupOperationsEndpoint
{
    public static IEndpointRouteBuilder MapListBackupOperations(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                "/operations",
                async (
                    IMediator mediator, [AsParameters] ListBackupOperationsRequest request,
                    CancellationToken ct) => (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    BackupOperationDto[] value => Results.Ok(value)
                }))
            .WithName("Backups.ListBackupOperations");
        return endpoints;
    }
}
