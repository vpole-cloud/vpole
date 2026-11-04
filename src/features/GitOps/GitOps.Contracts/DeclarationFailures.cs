namespace Pole.Features.GitOps.Contracts;

// Open to provider-owned typed errors; adding a provider requires no platform error enum.
public interface IDeclarationFailure;
public sealed record StaleGitExecution : IDeclarationFailure;
public sealed record GitDeclarationOwnershipConflict : IDeclarationFailure;

public static class DeclarationFailureDiagnostics
{
    // Diagnostic codes are produced only at the persistence/transport boundary. Execution branches
    // on the typed reason, never on a diagnostic string.
    public static string? Code(IDeclarationFailure? failure, IDeclarativeResourceProvider provider) => failure switch
    {
        null => null,
        StaleGitExecution => "StaleExecution",
        GitDeclarationOwnershipConflict => "OwnershipConflict",
        _ => provider.DescribeFailure(failure)
    };
}
