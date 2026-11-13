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

namespace Pole.Features.Backups.Application.Endpoints.Archives;

public sealed record ListBackupCatalogRequest(
    [FromTenantHeader] string TenantId,
    [FromRoute] string Id
) : IMediatorRequest<ListBackupCatalogOutcome>, ITenantReadOperation;

public union ListBackupCatalogOutcome(BackupCatalogEntry[], BackupRequestRejected);

public sealed class ListBackupCatalogValidator : AbstractValidator<ListBackupCatalogRequest>
{
    public ListBackupCatalogValidator()
    {
        RuleFor(r => r.Id).MongoObjectId("Archive ID");
    }
}

public sealed class ListBackupCatalogHandler(
    BackupReads reads
) : IMediatorOutcomeHandler<ListBackupCatalogRequest, ListBackupCatalogOutcome>
{
    public async Task<ListBackupCatalogOutcome> HandleAsync(
        ListBackupCatalogRequest request,
        CancellationToken ct = default)
    {
        var answer = await reads.CatalogAsync(request.TenantId, request.Id, ct);
        return answer switch
        {
            BackupWriteResult<BackupCatalogEntry[]>.Saved ok => ok.Value,
            BackupWriteResult<BackupCatalogEntry[]>.Rejected failure => new BackupRequestRejected(failure.Code)
        };
    }
}

public static class ListBackupCatalogEndpoint
{
    public static IEndpointRouteBuilder MapListBackupCatalog(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                "/archives/{Id}/backups",
                async (
                    IMediator mediator, [AsParameters] ListBackupCatalogRequest request,
                    CancellationToken ct) => (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    BackupCatalogEntry[] value => Results.Ok(value),
                    BackupRequestRejected error => BackupHttpProblems.From(error)
                }))
            .WithName("Backups.ListBackupCatalog");
        return endpoints;
    }
}
