namespace Pole.Features.Core.Multitenancy;

public interface ITenantOwned
{
    string TenantId { get; }
}
