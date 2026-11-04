using System.Text.Json;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;

namespace Pole.Features.GitOps.Application.Persistence;

internal static class SnapshotStoredStatus
{
    public const string Applying = "Applying";
    public const string Blocked = "Blocked";
    public const string ConfigurationApplied = "ConfigurationApplied";
    public const string UnsupportedStoredSchema = "UnsupportedStoredSchema";
    public const string StaleExecution = "StaleExecution";

    public static SnapshotProgress ToDomain(string status) => status switch
    {
        Applying => new ApplyingSnapshot(),
        Blocked => new BlockedSnapshot(),
        ConfigurationApplied => new AppliedSnapshot(),
        UnsupportedStoredSchema => new UnsupportedSnapshotSchema(),
        StaleExecution => new StaleSnapshotExecution(),
        _ => throw new InvalidOperationException("The stored snapshot progress is unsupported.")
    };

    public static string FromDomain(SnapshotProgress progress) => progress switch
    {
        ApplyingSnapshot => Applying,
        BlockedSnapshot => Blocked,
        AppliedSnapshot => ConfigurationApplied,
        UnsupportedSnapshotSchema => UnsupportedStoredSchema,
        StaleSnapshotExecution => StaleExecution
    };
}

internal static class GitPersistenceMappings
{
    public static ExecutionStamp ToStamp(this GitMutationContext context) =>
        new(context.TenantId, context.SourceId, context.RunId, context.Fence, context.Attempt);
    public static SnapshotExecution ToDomain(this GitSourceExecution source) => SnapshotExecutionFactory.Create(
        source.ToContext().ToStamp(), source.SourceUrl, source.AttemptedCommit, source.AppliedCommit,
        AcceptedSnapshotCodec.Read(source).Select(item => item.ToBinding()), SnapshotStoredStatus.ToDomain(source.Status));
    public static AcceptedResourceBinding ToBinding(this AcceptedDeclaration declaration) =>
        new(declaration.Kind, declaration.Name, declaration.ResourceId, declaration.Hash);
    public static AcceptedResourceBinding ToBinding(this GitOperation operation) =>
        new(operation.Declaration.Address.Kind, operation.Declaration.Address.Name, operation.ResourceId, operation.Declaration.Hash);
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
