using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pole.Features.Tenants.Contracts;

namespace Pole.Features.Tenants.Application.Common;

public static class TenantContractsRegistration
{
    public static IServiceCollection AddTenantContractCapabilities(this IServiceCollection services)
    {
        services.TryAddScoped<ITenantIdentityLookup, TenantIdentityLookup>();
        return services;
    }
}
