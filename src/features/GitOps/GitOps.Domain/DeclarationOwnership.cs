namespace Pole.Features.GitOps.Domain;

public sealed record DeclarationOwnership(
    string TenantId,
    string SourceId,
    string Kind,
    string Name,
    string ResourceId,
    string DesiredHash,
    string? AppliedHash,
    bool PendingRemoval)
{
    public bool CanBeMutatedBy(GitSourceIdentity source, string resourceId) =>
        TenantId == source.TenantId && SourceId == source.SourceId && ResourceId == resourceId;

    public DeclarationOwnership ObserveDesired(string hash) => this with { DesiredHash = hash, PendingRemoval = false };

    // Prune is off: removal from a snapshot retains both ownership and the physical resource.
    public DeclarationOwnership RetainPendingRemoval() => this with { PendingRemoval = true };

    public bool ConfigurationApplied(bool hasBlocker) => !PendingRemoval && !hasBlocker && DesiredHash == AppliedHash;
}

public sealed record DeclarationCompletion(
    string DesiredHash,
    bool FinalPhase,
    bool Applied)
{
    // Null means preserve the previous applied hash, including when a newer configuration blocks.
    public string? AppliedHashUpdate => FinalPhase && Applied ? DesiredHash : null;
}
