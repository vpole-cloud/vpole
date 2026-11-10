namespace Pole.Features.GitOps.Compiler;

public sealed record AcceptedManifestDeclaration(
    string Kind,
    string Name,
    string SpecJson
);
