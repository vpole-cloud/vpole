using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Backups.Application.Configuration;
using Pole.Features.Backups.Application.Execution;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.Backups.Application.Endpoints;

public sealed record GetBackupPolicyRequest(
    [FromTenantHeader] string TenantId,
    [FromRoute] string Id
) : IMediatorRequest<GetBackupPolicyOutcome>, ITenantReadOperation;

public sealed record BackupPolicyMissing;

public union GetBackupPolicyOutcome(BackupPolicyDto, BackupPolicyMissing);

public sealed class GetBackupPolicyValidator : AbstractValidator<GetBackupPolicyRequest>
{
    public GetBackupPolicyValidator() => RuleFor(r => r.Id).MongoObjectId("Resource ID");
}

public sealed class GetBackupPolicyHandler(
    BackupReads reads
) : IMediatorOutcomeHandler<GetBackupPolicyRequest, GetBackupPolicyOutcome>
{
    public async Task<GetBackupPolicyOutcome> HandleAsync(
        GetBackupPolicyRequest request,
        CancellationToken ct = default)
    {
        var item = await reads.PolicyAsync(request.TenantId, request.Id, ct);
        return item is null ? new BackupPolicyMissing() : item;
    }
}

public static class GetBackupPolicyEndpoint
{
    public static IEndpointRouteBuilder MapGetBackupPolicy(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                "/policies/{Id}",
                async (
                    IMediator mediator, [AsParameters] GetBackupPolicyRequest request,
                    CancellationToken ct) => (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    BackupPolicyDto item => Results.Ok(item),
                    BackupPolicyMissing => BackupHttpProblems.From(new("not-found"))
                }))
            .WithName("Backups.GetBackupPolicy");
        return endpoints;
    }
}
