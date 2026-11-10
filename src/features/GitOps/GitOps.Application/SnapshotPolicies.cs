using Pole.Features.GitOps.Compiler;
using Pole.Features.GitOps.Primitives;

namespace Pole.Features.GitOps.Application;

public static class SnapshotExecutionLimits
{
    public static int MaximumOperationReceipts =>
        SnapshotSchema.MaximumDeclarations * Enum.GetValues<DeclarationPhase>().Length;
}

public static class SnapshotHttpLimits
{
    public const int MaximumSnapshotRequestBytes = 2 * SnapshotSchema.MaximumContentBytes;

    public const int MaximumSourceRequestBytes = 4096;

    public const int MaximumSourceUrlLength = 512;

    public const int MaximumCommitLength = 128;
}
