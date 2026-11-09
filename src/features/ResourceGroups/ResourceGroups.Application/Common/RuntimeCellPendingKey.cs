namespace Pole.Features.ResourceGroups.Application.Common;

public static class RuntimeCellPendingKey
{
    public static string Reconciliation(string runtimeCellId, long generation) =>
        Create(runtimeCellId, generation, "reconciliation");

    public static string Deletion(string runtimeCellId, long generation) =>
        Create(runtimeCellId, generation, "deletion");

    public static string DeletionCheck(string runtimeCellId, long generation, long sequence)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 0);
        return sequence == 0
            ? Deletion(runtimeCellId, generation)
            : $"{Deletion(runtimeCellId, generation)}:check:{sequence}";
    }

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
