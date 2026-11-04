using System.Diagnostics.CodeAnalysis;
using Pole.Workloads;
using System.Text.Json;
using System.Text.Json.Serialization;
using MongoDB.Driver;

namespace Pole.Features.GitOps.Contracts;

public sealed record ResourceAddress(
    string Kind,
    string Name);

public enum DeclarationPhase { Prepare, Bind, Configure }

public sealed record DeclarationDependency(
    ResourceAddress Resource,
    DeclarationPhase Phase);

public sealed record DeclarationStep(
    DeclarationPhase Phase,
    IReadOnlyList<DeclarationDependency> Dependencies);

public sealed record DeclarationDiagnostic(
    string File,
    int Document,
    string Field,
    string Code);

public sealed record DeclaredReference(
    ResourceAddress Target,
    string Field,
    string? RequiredKey = null,
    bool RequiresAccess = false);

public sealed record DeclarationExclusiveRelation(
    string Relation,
    ResourceAddress Left,
    ResourceAddress Right);

public sealed record DeclarationKeyGrant(
    ResourceAddress Consumer,
    ResourceAddress Target,
    IReadOnlyList<string> Keys,
    bool AllKeys);

public sealed record DeclarationFacts(
    ResourceAddress? Scope,
    IReadOnlyList<string> Keys,
    IReadOnlyList<DeclaredReference> References,
    IReadOnlyList<DeclarationExclusiveRelation> ExclusiveRelations,
    IReadOnlyList<DeclarationKeyGrant> AccessGrants,
    bool ReferencesShareScope = true);

public sealed record CompiledDeclaration(
    ResourceAddress Address,
    JsonElement Spec,
    string Hash,
    IReadOnlyList<DeclarationStep> Steps,
    string File,
    int Document);

// This context is server-created. Neither manifests nor public resource endpoints accept it.
public sealed record GitMutationContext(
    string TenantId,
    string SourceId,
    string RunId,
    long Fence,
    string SourceUrl,
    string ActorUserId,
    string ActorEmail,
    long Attempt = 0);

public sealed record GitResourceStatus(
    string SourceId,
    string SourceUrl,
    string? Blocker,
    bool PendingRemoval,
    bool ConfigurationApplied);

public sealed record GitOperationResult(
    string ResourceId,
    IDeclarationFailure? Failure = null,
    bool Changed = false)
{
    public bool Applied => Failure is null;
}

public sealed record GitOperation(
    GitMutationContext Context,
    CompiledDeclaration Declaration,
    DeclarationPhase Phase,
    string ResourceId);

public interface IDeclarativeResourceProvider
{
    string Kind { get; }
    string DescribeFailure(IDeclarationFailure failure) => throw new InvalidOperationException("The provider must map its own typed declaration failures.");
    int MaximumNameLength => ResourceTechnicalName.MaximumLength;
    DeclarationFacts Facts(JsonElement spec) => new(null, [], [], [], []);
    JsonElement NormalizeAndValidate(JsonElement spec);
    IReadOnlyList<DeclarationStep> Steps(ResourceAddress address, JsonElement spec);
    Task<string?> ResolveAsync(
        ResolveDeclaredResourceRequest request,
        IClientSessionHandle session,
        CancellationToken ct);
    Task<GitOperationResult> ApplyAsync(GitOperation operation, CancellationToken ct);
}

// Begin, mutation and Complete must share the provider's transaction. Begin performs a real
// source fence write and claims the tenant-wide address, serializing obsolete workers and owners.
public interface IGitOpsMutations
{
    Task<GitOperationResult> ExecuteAsync(GitOperation operation,
        Func<IClientSessionHandle, CancellationToken, Task<GitOperationResult>> mutation, CancellationToken ct);
    Task<GitOperationResult?> BeginAsync(GitOperation operation, IClientSessionHandle session, CancellationToken ct);
    Task CompleteAsync(
        CompleteGitOperationRequest request,
        IClientSessionHandle session,
        CancellationToken ct);
    Task<bool> AuthorizeAsync(
        AuthorizeGitResourceMutationRequest request,
        IClientSessionHandle session,
        CancellationToken ct);
    Task<string?> ResolveAsync(
        ResolveGitResourceRequest request,
        IClientSessionHandle session,
        CancellationToken ct);
    Task<IReadOnlyDictionary<string, GitResourceStatus>> StatusesAsync(
        LoadGitResourceStatusesRequest request,
        CancellationToken ct);
    Task<GitResourceStatus?> StatusAsync(
        LoadGitResourceStatusRequest request,
        CancellationToken ct);
}

public static class DeclarationJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false
    };

    public static T Read<T>(JsonElement value) where T : class =>
        value.Deserialize<T>(Options) ?? throw new JsonException("A spec object is required.");
    public static JsonElement Write<T>(T value) => JsonSerializer.SerializeToElement(value, Options);
    public static void Require([DoesNotReturnIf(false)] bool condition, string field)
    {
        if (!condition) throw new DeclarationSchemaException(field);
    }
}

public sealed class DeclarationSchemaException(string field) : Exception
{
    public string Field { get; } = field;
}

public sealed record ResolveDeclaredResourceRequest(
    string TenantId,
    string Name);

public sealed record CompleteGitOperationRequest(
    GitOperation Operation,
    GitOperationResult Result);

public sealed record AuthorizeGitResourceMutationRequest(
    string TenantId,
    string Kind,
    string ResourceId,
    GitMutationContext? Context = null);

public sealed record ResolveGitResourceRequest(
    string TenantId,
    ResourceAddress Address);

public sealed record LoadGitResourceStatusesRequest(
    string TenantId,
    string Kind,
    IReadOnlyCollection<string> ResourceIds);

public sealed record LoadGitResourceStatusRequest(
    string TenantId,
    string Kind,
    string ResourceId);
