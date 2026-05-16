namespace Pole.Features.Core.Multitenancy;

public static class TenantHeaders
{
    public const string TenantId = "X-Pole-Tenant-ID";
}

public interface ITenantOperation
{
    string? TenantId { get; }
}

public interface ITenantReadOperation : ITenantOperation;

public interface ITenantWriteOperation : ITenantOperation;

public interface ITenantCreateOperation : ITenantWriteOperation;

public sealed record TenantOperationScope(
    string? TenantId);

public interface ITenantOperationScopeAccessor
{
    TenantOperationScope? Current { get; set; }
}

public sealed class TenantOperationScopeAccessor : ITenantOperationScopeAccessor
{
    private static readonly AsyncLocal<TenantOperationScope?> CurrentScope = new();

    public TenantOperationScope? Current
    {
        get => CurrentScope.Value;
        set => CurrentScope.Value = value;
    }
}
