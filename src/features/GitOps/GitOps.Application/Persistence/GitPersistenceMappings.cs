using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;

namespace Pole.Features.GitOps.Application.Persistence;

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
