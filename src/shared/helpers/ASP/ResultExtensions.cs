using Microsoft.AspNetCore.Http;
using Pole.ResultType;

namespace Pole.ASP.Helpers;

public static class ResultExtensions
{
    public static IResult ToHttpResult<T>(this Result<T> result, Func<T, IResult>? transformer = null) where T : notnull
    {
        if (result.TryGetValue(out var value, out var error))
        {
            return transformer is not null ? transformer(value) : Results.Ok(value);
        }

        return error switch
        {
            NotFoundFailure => Results.NotFound(ToErrorResponse(error)),
            ValidationFailure => Results.BadRequest(ToErrorResponse(error)),
            UnauthorizedFailure => Results.Json(ToErrorResponse(error), statusCode: StatusCodes.Status401Unauthorized),
            ForbiddenFailure => Results.Json(ToErrorResponse(error), statusCode: StatusCodes.Status403Forbidden),
            ConflictFailure => Results.Conflict(ToErrorResponse(error)),
            InternalFailure => Results.Json(ToErrorResponse(error), statusCode: StatusCodes.Status500InternalServerError),
            ExternalServiceFailure => Results.Json(ToErrorResponse(error), statusCode: StatusCodes.Status502BadGateway),
            _ => Results.Json(ToErrorResponse(error), statusCode: StatusCodes.Status500InternalServerError)
        };
    }

    private static object ToErrorResponse(Failure failure) => failure switch
    {
        NotFoundFailure { Id: not null } notFoundFailure => new
        {
            message = notFoundFailure.Message,
            id = notFoundFailure.Id
        },
        ValidationFailure validationFailure => new
        {
            message = validationFailure.Message,
            errors = validationFailure.ValidationErrors
        },
        UnauthorizedFailure unauthorizedFailure => new
        {
            message = unauthorizedFailure.Message,
            action = unauthorizedFailure.Action
        },
        ForbiddenFailure forbiddenFailure => new
        {
            message = forbiddenFailure.Message,
            action = forbiddenFailure.Action
        },
        InternalFailure { Details: not "" } internalFailure => new
        {
            message = internalFailure.Message,
            details = internalFailure.Details
        },
        ExternalServiceFailure externalServiceFailure => new
        {
            message = externalServiceFailure.Message,
            serviceName = externalServiceFailure.ServiceName,
            details = externalServiceFailure.Details
        },
        _ => new
        {
            message = failure.Message
        }
    };
}
