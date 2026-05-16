using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Pole.ASP.Helpers;

[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property, AllowMultiple = false)]
public class FromClaimAttribute(string claimType) : Attribute, IBindingSourceMetadata
{
    public string ClaimType { get; } = claimType;

    public BindingSource BindingSource => BindingSource.Custom;
}