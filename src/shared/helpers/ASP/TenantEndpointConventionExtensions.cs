using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Pole.Features.Core.Multitenancy;

namespace Pole.ASP.Helpers;

public static class TenantEndpointConventionExtensions
{
    /// <summary>Fails endpoint construction when a tenant request lacks its local declaration.</summary>
    public static TBuilder ValidateTenantContextDeclarations<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.Finally(endpoint =>
        {
            var method = endpoint.Metadata.OfType<MethodInfo>().LastOrDefault();
            if (method is null)
                return;

            var requiresTenant = method.GetParameters().Any(parameter =>
                typeof(ITenantOperation).IsAssignableFrom(parameter.ParameterType));
            if (!requiresTenant)
                return;

            var metadata = endpoint.Metadata.OfType<TenantEndpointMetadata>().LastOrDefault();
            if (metadata?.ActiveTenant != ActiveTenantRequirement.Required)
            {
                throw new InvalidOperationException(
                    $"Tenant endpoint '{endpoint.DisplayName}' must declare required active tenant context.");
            }
        });
        return builder;
    }
}
