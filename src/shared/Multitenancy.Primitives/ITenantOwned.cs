namespace Pole.Multitenancy.Primitives;

public interface ITenantOwned
{
    string TenantId { get; }
}
