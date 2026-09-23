using Microsoft.AspNetCore.Http.Metadata;
using Pole.Features.Core.Multitenancy;

namespace Pole.ASP.Helpers;

/// <summary>Binds the tenant header; endpoint middleware resolves it before binding.</summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property)]
public sealed class FromTenantHeaderAttribute : Attribute, IFromHeaderMetadata
{
    public string Name => TenantHeaders.TenantId;
}
