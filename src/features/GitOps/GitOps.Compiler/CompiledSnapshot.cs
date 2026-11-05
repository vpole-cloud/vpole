using Pole.Features.GitOps.Contracts;

namespace Pole.Features.GitOps.Compiler;

public sealed record CompiledSnapshot(
    IReadOnlyList<CompiledDeclaration> Declarations,
    IReadOnlyList<DeclarationDiagnostic> Diagnostics)
{
    public bool Valid => Diagnostics.Count == 0;
}
