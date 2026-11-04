using Pole.Features.GitOps.Primitives;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;

namespace Pole.Features.GitOps.Application;

public static class SnapshotSchema
{
    public const string ApiVersion = "pole.io/v1alpha1";

    public const int MaximumFiles = SnapshotInputLimits.MaximumFiles;

    public const int MaximumDocumentsPerFile = 256;

    public const int MaximumDeclarations = 256;

    public const int MaximumContentBytes = SnapshotInputLimits.MaximumContentBytes;

    public const int MaximumStoredSpecBytes = 6 * MaximumContentBytes;

    public const int MaximumYamlDepth = 24;

    public static int MaximumOperationReceipts => MaximumDeclarations * Enum.GetValues<DeclarationPhase>().Length;
}

public static class SnapshotHttpLimits
{
    public const int MaximumSnapshotRequestBytes = 2 * SnapshotSchema.MaximumContentBytes;

    public const int MaximumSourceRequestBytes = 4096;

    public const int MaximumSourceUrlLength = 512;

    public const int MaximumCommitLength = 128;
}

public static class SnapshotExecutionPolicy
{
    public const int RecoveryBatchSize = 32;

    public const int RecoveryLeaseSeconds = 300;

    public const int RetryDelaySeconds = 60;

    public static TimeSpan RecoveryLease => TimeSpan.FromSeconds(RecoveryLeaseSeconds);

    public static TimeSpan RetryDelay => TimeSpan.FromSeconds(RetryDelaySeconds);

    public static DateTime? NextAttemptAt(SnapshotProgress progress, DateTime now) => progress switch
    {
        ApplyingSnapshot => now + RecoveryLease,
        BlockedSnapshot or StaleSnapshotExecution => now + RetryDelay,
        AppliedSnapshot or UnsupportedSnapshotSchema => null
    };
}
