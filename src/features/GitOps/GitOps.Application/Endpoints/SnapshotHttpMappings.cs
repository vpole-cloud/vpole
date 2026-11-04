using Microsoft.AspNetCore.Http;
using Pole.ASP.Helpers;
using Pole.Features.GitOps.Application.Persistence;

namespace Pole.Features.GitOps.Application.Endpoints;

internal static class SnapshotHttpMappings
{
    public static IResult Map(StaleSnapshotConflict _) =>
        Problem(409, "gitops.stale-execution", "The snapshot execution has changed").ToHttpResult();
    public static IResult Map(SnapshotSourceRelocationRequired _) =>
        Problem(409, "gitops.source-relocation-required", "The snapshot source requires explicit relocation").ToHttpResult();
    public static IResult Map(SnapshotSourceMissing _) =>
        Problem(404, "gitops.source-not-found", "Snapshot source not found").ToHttpResult();
    public static IResult Map(InvalidSnapshot error) => new HttpProblem(
        400, PoleProblemTypeId.FromCode("gitops.invalid-snapshot"), "The snapshot is invalid", "gitops.invalid-snapshot",
        extensions: new Dictionary<string, object> { ["diagnostics"] = error.Diagnostics }).ToHttpResult();
    private static HttpProblem Problem(int status, string code, string title) =>
        new(status, PoleProblemTypeId.FromCode(code), title, code);
}
