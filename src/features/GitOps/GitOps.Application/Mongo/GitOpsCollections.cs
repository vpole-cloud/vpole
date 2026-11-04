using Pole.Features.GitOps.Application.Lifecycle.Mongo;
using Pole.Features.GitOps.Application.Runs.Mongo;
namespace Pole.Features.GitOps.Application.Mongo;

internal static class GitOpsCollections
{
    public const string Inventory = "ManagedDeclarations";

    public const string Plans = "GitOperationPlans";

    public const string CleanupProofs = "GitLifecycleCleanupProofs";

    public const string Receipts = "GitOperationReceipts";
}
