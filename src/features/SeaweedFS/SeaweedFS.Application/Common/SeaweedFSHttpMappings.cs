using Pole.ASP.Helpers;
using Pole.Features.SeaweedFS.Application.Services;

namespace Pole.Features.SeaweedFS.Application.Common;

public static class SeaweedFSHttpMappings
{
    public static HttpProblem MapNameAlreadyExists(SeaweedFSServiceNameAlreadyExists _) => Problem(
        409,
        "name-conflict",
        "SeaweedFS name already exists in this tenant");

    public static HttpProblem Problem(
        int status,
        string code,
        string title) => new(status, PoleProblemTypeId.FromCode("seaweedfs." + code), title, "seaweedfs." + code);
}
