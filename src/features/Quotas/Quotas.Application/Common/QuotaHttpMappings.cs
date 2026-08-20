using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Pole.ASP.Helpers;
using Pole.Features.Quotas.Application.Endpoints;

namespace Pole.Features.Quotas.Application.Common;

public static class QuotaHttpMappings
{
    public static IReadOnlyList<HttpProblemMapping> All { get; } =
        Array.AsReadOnly<HttpProblemMapping>(
        [
            HttpProblemMapping.For<InvalidTenantQuotaPolicy>(MapInvalidPolicy)
        ]);

    public static IServiceCollection AddQuotaHttpMappings(
        this IServiceCollection services) =>
        services.AddHttpProblemMappings(All);

    private static HttpProblem MapInvalidPolicy(
        InvalidTenantQuotaPolicy invalid) =>
        new(
            (int)HttpStatusCode.BadRequest,
            PoleProblemTypeId.FromCode("quotas.policy.invalid"),
            "Tenant quota policy is invalid",
            "quotas.policy.invalid",
            extensions: new Dictionary<string, object>
            {
                ["errors"] = invalid.Errors
            });
}
