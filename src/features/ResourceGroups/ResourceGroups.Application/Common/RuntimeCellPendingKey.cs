namespace Pole.Features.ResourceGroups.Application.Common;

public static class RuntimeCellPendingKey
{
    public static string Reconciliation(string runtimeCellId, long generation) =>
        Create(runtimeCellId, generation, "reconciliation");

    public static string Deletion(string runtimeCellId, long generation) =>
        Create(runtimeCellId, generation, "deletion");

    private static string Create(
        string runtimeCellId,
        long generation,
        string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeCellId);
        ArgumentOutOfRangeException.ThrowIfLessThan(generation, 1);

        return $"runtime-cell:{runtimeCellId}:{generation}:{operation}";
    }
}
