using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Features.Quotas.Application.Common;
using Pole.Features.Quotas.Domain;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.Quotas.Application.Endpoints;

public static class UpsertTenantQuotaPolicy
{
    public static IEndpointRouteBuilder MapUpsertTenantQuotaPolicyEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPut("/{tenantId}/quota", async (
                IMediator mediator,
                IHttpProblemMapper httpProblems,
                [AsParameters] UpsertTenantQuotaPolicyRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    TenantQuotaPolicyDto policy => Results.Ok(policy),
                    InvalidTenantQuotaPolicy invalid =>
                        httpProblems.Map(invalid).ToHttpResult()
                });
            })
            .WithName("UpsertTenantQuotaPolicy")
            .RequireAuthorization(Policy.Admin)
            .Produces<TenantQuotaPolicyDto>();

        return app;
    }
}

public sealed record UpsertTenantQuotaPolicyRequest(
    [FromRoute] string TenantId,
    [FromBody] UpsertTenantQuotaPolicyRequestBody Body)
    : IMediatorRequest<UpsertTenantQuotaPolicyOutcome>;

public sealed record InvalidTenantQuotaPolicy(
    IReadOnlyDictionary<string, string[]> Errors);

public union UpsertTenantQuotaPolicyOutcome(
    TenantQuotaPolicyDto,
    InvalidTenantQuotaPolicy);

public sealed class UpsertTenantQuotaPolicyHandler(
    IQuotaDbContext dbContext,
    QuotaCatalog catalog,
    TimeProvider timeProvider)
    : IMediatorOutcomeHandler<
        UpsertTenantQuotaPolicyRequest,
        UpsertTenantQuotaPolicyOutcome>
{
    public async Task<UpsertTenantQuotaPolicyOutcome> HandleAsync(
        UpsertTenantQuotaPolicyRequest request,
        CancellationToken cancellationToken = default)
    {
        var limitsResult = request.Body.ToQuotaLimits(catalog);
        if (!limitsResult.TryGetValue(out var limits, out var error))
            return error;

        var existing = await dbContext.TenantQuotaPolicies
            .Find(candidate => candidate.TenantId == request.TenantId)
            .FirstOrDefaultAsync(cancellationToken);
        var policy = QuotaDomainFactory.CreatePolicy(
            request.TenantId,
            limits,
            timeProvider.GetUtcNow().UtcDateTime,
            existing?.CreatedAtUtc);

        await dbContext.TenantQuotaPolicies.ReplaceOneAsync(
            candidate => candidate.TenantId == request.TenantId,
            policy,
            new ReplaceOptions { IsUpsert = true },
            cancellationToken);

        return policy.ToDto(policy.TenantId, catalog);
    }
}

public sealed class UpsertTenantQuotaPolicyValidator : AbstractValidator<UpsertTenantQuotaPolicyRequest>
{
    public UpsertTenantQuotaPolicyValidator()
    {
        RuleFor(request => request.Body)
            .NotNull();
    }
}
