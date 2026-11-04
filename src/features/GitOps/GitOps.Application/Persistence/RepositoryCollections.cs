namespace Pole.Features.GitOps.Application.Persistence;

internal static class RepositoryCollections
{
    public const string Sources = "GitRepositorySources";
    public const string Synchronization = "GitSynchronizationStates";
    public const string Runs = "GitReconciliationRuns";
    public const string Requests = "GitSourceRequests";
    public const string References = "GitAcceptedReferences";
    public const string CreationIndex = "UX_GitRepositorySources_Tenant_Creation";
}
