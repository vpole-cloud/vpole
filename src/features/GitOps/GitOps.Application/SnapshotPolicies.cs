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
