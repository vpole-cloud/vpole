namespace Pole.Features.GitOps.Application.Persistence;

public sealed record AcceptedDeclaration(
    string Kind,
    string Name,
    string Hash,
    string SpecJson,
    string ResourceId);
