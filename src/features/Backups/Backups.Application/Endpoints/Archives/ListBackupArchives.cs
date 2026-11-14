using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Features.Backups.Application.Archives;
using Pole.Features.Backups.Application.Configuration;
using Pole.Features.Backups.Contracts;
using Pole.Features.Backups.Domain;
using Pole.Features.Core.Multitenancy;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.Backups.Application.Endpoints.Archives;

public sealed record ListBackupArchivesRequest(
    [FromTenantHeader] string TenantId
) : IMediatorRequest<ListBackupArchivesOutcome>, ITenantReadOperation;

public union ListBackupArchivesOutcome(BackupArchiveDto[]);

public sealed class ListBackupArchivesHandler(
    BackupArchiveQueries reads
) : IMediatorOutcomeHandler<ListBackupArchivesRequest, ListBackupArchivesOutcome>
{
    public async Task<ListBackupArchivesOutcome> HandleAsync(
        ListBackupArchivesRequest request,
        CancellationToken ct = default)
    {
        return await reads.ArchivesAsync(request.TenantId, ct);
    }
}

public static class ListBackupArchivesEndpoint
{
    public static IEndpointRouteBuilder MapListBackupArchives(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                "/archives",
                async (
                    IMediator mediator, [AsParameters] ListBackupArchivesRequest request,
                    CancellationToken ct) => (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    BackupArchiveDto[] value => Results.Ok(value)
                }))
            .WithName("Backups.ListBackupArchives");
        return endpoints;
    }
}
