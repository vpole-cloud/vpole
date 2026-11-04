using System.Text.Json;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;

namespace Pole.Features.GitOps.Application.Persistence;

internal static class GitPersistenceMappings
{
    public static ExecutionStamp ToStamp(this GitMutationContext context) =>
        new(context.TenantId, context.SourceId, context.RunId, context.Fence, context.Attempt);

    public static SnapshotExecution ToDomain(this GitSourceExecution source) => SnapshotExecutionFactory.Create(
        source.ToContext().ToStamp(), source.SourceUrl, source.AttemptedCommit, source.AppliedCommit,
        AcceptedSnapshotCodec.Read(source).Select(item => item.ToBinding()), source.Status);

    public static AcceptedResourceBinding ToBinding(this AcceptedDeclaration declaration) =>
        new(declaration.Kind, declaration.Name, declaration.ResourceId, declaration.Hash);

    public static AcceptedResourceBinding ToBinding(this GitOperation operation) =>
        new(
            operation.Declaration.Address.Kind,
            operation.Declaration.Address.Name,
            operation.ResourceId,
            operation.Declaration.Hash);

    public static DeclarationOwnership ToDomain(this ManagedDeclaration declaration) => new(
        declaration.TenantId, declaration.SourceId, declaration.Kind, declaration.Name, declaration.ResourceId,
        declaration.DesiredHash, declaration.AppliedHash, declaration.PendingRemoval);
}

internal static class AcceptedSnapshotCodec
{
    public static AcceptedDeclaration[] Read(GitSourceExecution source) =>
        JsonSerializer.Deserialize<AcceptedDeclaration[]>(source.SnapshotJson) ??
        throw new InvalidOperationException("An accepted snapshot must retain its resource bindings.");

    public static string Write(IEnumerable<AcceptedDeclaration> declarations) => JsonSerializer.Serialize(declarations);
}
