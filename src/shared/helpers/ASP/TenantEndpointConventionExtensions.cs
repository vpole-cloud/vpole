using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Pole.Features.Core.Multitenancy;

namespace Pole.ASP.Helpers;

public static class TenantEndpointConventionExtensions
{
    /// <summary>Requires tenant context for a group and validates its endpoint overrides.</summary>
    public static TBuilder RequireTenantContext<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new TenantEndpointMetadata()).ValidateTenantContextDeclarations();

    /// <summary>Ignores the tenant header; authentication and operation authorization still apply.</summary>
    public static TBuilder WithoutTenantContext<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new TenantEndpointMetadata(ActiveTenantRequirement.None));

    /// <summary>Fails endpoint construction when a tenant request lacks required context.</summary>
    public static TBuilder ValidateTenantContextDeclarations<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.Finally(endpoint =>
        {
            var method = endpoint.Metadata.OfType<MethodInfo>().LastOrDefault();
            if (method is null)
                return;

            var requiresTenant = method.GetParameters()
                .Any(parameter => typeof(ITenantOperation).IsAssignableFrom(parameter.ParameterType));
            if (!requiresTenant)
                return;

            var metadata = endpoint.Metadata.OfType<TenantEndpointMetadata>().LastOrDefault();
            if (metadata?.ActiveTenant != ActiveTenantRequirement.Required)
                throw new InvalidOperationException(
                    $"Tenant endpoint '{endpoint.DisplayName}' must declare required active tenant context.");
        });
        return builder;
    }
}
