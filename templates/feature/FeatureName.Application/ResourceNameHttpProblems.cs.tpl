using Pole.ASP.Helpers;
using Pole.Features.FeatureName.Application.Common;

namespace Pole.Features.FeatureName.Application;

public static class ResourceNameHttpProblems
{
    public static HttpProblem MapMissing(ResourceNameNotFound _) => new(
        404,
        PoleProblemTypeId.FromCode("feature-route.not-found"),
        "Resource not found",
        "feature-route.not-found");

    public static HttpProblem MapNameConflict(ResourceNameNameAlreadyExists _) => new(
        409,
        PoleProblemTypeId.FromCode("feature-route.name-conflict"),
        "Name already exists",
        "feature-route.name-conflict");

    public static HttpProblem MapRevisionConflict(ResourceNameRevisionConflict _) => new(
        409,
        PoleProblemTypeId.FromCode("feature-route.revision-conflict"),
        "Revision conflict",
        "feature-route.revision-conflict");
}
