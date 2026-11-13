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

namespace Pole.Features.Backups.Application.Endpoints.Destinations;

public sealed record ListBackupDestinationsRequest(
    [FromTenantHeader] string TenantId
) : IMediatorRequest<ListBackupDestinationsOutcome>, ITenantReadOperation;

public union ListBackupDestinationsOutcome(BackupDestinationDto[]);

public sealed class ListBackupDestinationsHandler(
    BackupReads reads
) : IMediatorOutcomeHandler<ListBackupDestinationsRequest, ListBackupDestinationsOutcome>
{
    public async Task<ListBackupDestinationsOutcome> HandleAsync(
        ListBackupDestinationsRequest request,
        CancellationToken ct = default)
    {
        return await reads.DestinationsAsync(request.TenantId, ct);
    }
}

public static class ListBackupDestinationsEndpoint
{
    public static IEndpointRouteBuilder MapListBackupDestinations(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                "/destinations",
                async (
                    IMediator mediator, [AsParameters] ListBackupDestinationsRequest request,
                    CancellationToken ct) => (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    BackupDestinationDto[] value => Results.Ok(value)
                }))
            .WithName("Backups.ListBackupDestinations");
        return endpoints;
    }
}
