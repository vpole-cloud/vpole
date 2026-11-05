using Pole.Features.GitOps.Application.Runs;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;

namespace Pole.Features.GitOps.Application;

internal static class GitPersistenceMappings
{
    public static GitRunHandle ToHandle(this GitMutationContext context) => new(
        new(new(context.TenantId, context.SourceId),
            context.RunId,
            context.Authority.ConfigurationRevision,
            context.Authority.Generation,
            context.Fence),
        new(context.Authority.WorkerId, context.Authority.Attempt));

    public static GitMutationContext ToContext(this GitWorkClaim claim, string commit) => new(
        claim.Source.TenantId,
        claim.Source.Id,
        claim.Run.Id,
        claim.Run.Stamp.Fence,
        claim.Source.DisplayUrl,
        new(claim.Run.Stamp.ConfigurationRevision,
            claim.Run.Stamp.Generation,
            claim.Handle.Identity.WorkerId,
            claim.Handle.Identity.Attempt,
            commit));

    public static GitRunHandle ToHandle(this GitLifecycleAuthority authority) => new(
        new(new(authority.TenantId, authority.SourceId), authority.RunId, authority.ConfigurationRevision,
            authority.Generation, authority.Fence), new(authority.WorkerId, authority.Attempt));

    public static GitLifecycleAuthority ToLifecycleAuthority(this GitWorkClaim claim, string previewId,
        string actionId) => new(claim.Source.TenantId, claim.Source.Id, claim.Run.Id,
        claim.Run.Stamp.ConfigurationRevision, claim.Run.Stamp.Generation, claim.Run.Stamp.Fence,
        claim.Handle.Identity.WorkerId, claim.Handle.Identity.Attempt, previewId, actionId, claim.Run.Commit);

    public static DeclarationOwnership ToDomain(this ManagedDeclaration declaration) => new(
        declaration.TenantId,
        declaration.SourceId,
        declaration.Kind,
        declaration.Name,
        declaration.ResourceId,
        declaration.DesiredHash,
        declaration.AppliedHash,
        declaration.PendingRemoval);
}
