using System.Net;
using Pole.Mediator;

namespace Pole.ASP.Helpers;

public static class MediatorHttpProblemMappings
{
    public static IReadOnlyList<HttpProblemMapping> All { get; } =
        Array.AsReadOnly<HttpProblemMapping>(
        [
            HttpProblemMapping.For<RequestValidationRejected>(MapValidationRejected)
        ]);

    private static HttpProblem MapValidationRejected(
        RequestValidationRejected rejected) =>
        new(
            (int)HttpStatusCode.BadRequest,
            PoleProblemTypeId.FromCode("request.validation.failed"),
            "Request validation failed",
            "request.validation.failed",
            extensions: new Dictionary<string, object>
            {
                ["errors"] = rejected.Errors
            });
}
