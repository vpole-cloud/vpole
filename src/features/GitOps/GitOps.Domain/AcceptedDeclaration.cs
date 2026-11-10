namespace Pole.Features.GitOps.Domain;

public sealed record AcceptedDeclaration(
    string Kind,
    string Name,
    string Hash,
    string SpecJson,
    string ResourceId
);
