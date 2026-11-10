namespace Pole.Features.Core.Multitenancy;

public enum ActiveTenantRequirement
{
    Required,
    Optional,
    None,
}

/// <summary>Declares whether an HTTP endpoint consumes the active tenant selection.</summary>
public sealed record TenantEndpointMetadata(
    ActiveTenantRequirement ActiveTenant = ActiveTenantRequirement.Required
);

/// <summary>
///     Allows an anonymous endpoint to validate its own bearer credential instead of selecting a tenant.
///     This only selects the authorization flow; it never authenticates the credential.
/// </summary>
public sealed record TenantCredentialMetadata(
    string BearerTokenPrefix
);
