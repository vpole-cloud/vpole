using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Backups.Application.Configuration;
using Pole.Features.Backups.Application.Destinations;
using Pole.Features.Backups.Application.Execution;
using Pole.Features.Core.Multitenancy;
using Pole.Mediator;
using Pole.Validation;
using static Pole.Features.Backups.Application.Common.BackupDestinationHttpMappings;

namespace Pole.Features.Backups.Application.Endpoints.Destinations;

public sealed record GetBackupDestinationRequest(
    [FromTenantHeader] string TenantId,
    [FromRoute] string Id
) : IMediatorRequest<GetBackupDestinationOutcome>, ITenantReadOperation;

public union GetBackupDestinationOutcome(BackupDestinationDto, BackupDestinationMissing);

public sealed class GetBackupDestinationValidator : AbstractValidator<GetBackupDestinationRequest>
{
    public GetBackupDestinationValidator() => RuleFor(r => r.Id).MongoObjectId("Resource ID");
}

public sealed class GetBackupDestinationHandler(
    BackupReads reads
) : IMediatorOutcomeHandler<GetBackupDestinationRequest, GetBackupDestinationOutcome>
{
    public async Task<GetBackupDestinationOutcome> HandleAsync(
        GetBackupDestinationRequest request,
        CancellationToken ct = default)
    {
        var item = await reads.DestinationAsync(request.TenantId, request.Id, ct);
        return item is null ? new BackupDestinationMissing() : item;
    }
}

public static class GetBackupDestinationEndpoint
{
    public static IEndpointRouteBuilder MapGetBackupDestination(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                "/destinations/{Id}",
                async (
                    IMediator mediator, [AsParameters] GetBackupDestinationRequest request,
                    CancellationToken ct) => (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    BackupDestinationDto item => Results.Ok(item),
                    BackupDestinationMissing error => MapDestinationMissing(error)
                }))
            .WithName("Backups.GetBackupDestination");
        return endpoints;
    }
}
