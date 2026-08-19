using Microsoft.AspNetCore.Http;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.ASP.Helpers;

public static class ResultExtensions
{
    public static IResult ToHttpResult<T>(
        this MediatorResult<T> result,
        Func<T, IResult>? transformer = null)
        where T : notnull =>
        result.Match(
            value => transformer is not null ? transformer(value) : Results.Ok(value),
            rejected => rejected.Reason switch
            {
                LegacyResultRejected legacy => ToHttpResult(legacy.Failure),
                var reason => new MediatorRejectionHttpResult(reason)
            });

    public static IResult ToHttpResult<T>(this Result<T> result, Func<T, IResult>? transformer = null) where T : notnull
    {
        if (result.TryGetValue(out var value, out var error))
        {
            return transformer is not null ? transformer(value) : Results.Ok(value);
        }

        return ToHttpResult(error);
    }

    private static IResult ToHttpResult(Failure error) =>
        error switch
        {
            NotFoundFailure => Results.NotFound(ToErrorResponse(error)),
            SemanticValidationFailure => Results.UnprocessableEntity(ToErrorResponse(error)),
            ValidationFailure => Results.BadRequest(ToErrorResponse(error)),
            UnauthorizedFailure => Results.Json(ToErrorResponse(error), statusCode: StatusCodes.Status401Unauthorized),
            ForbiddenFailure => Results.Json(ToErrorResponse(error), statusCode: StatusCodes.Status403Forbidden),
            ConflictFailure => Results.Conflict(ToErrorResponse(error)),
            InternalFailure => UnexpectedFailure(),
            ExternalServiceFailure => Results.Json(ToErrorResponse(error), statusCode: StatusCodes.Status502BadGateway),
            _ => UnexpectedFailure()
        };

    private static object ToErrorResponse(Failure failure) => failure switch
    {
        NotFoundFailure { Id: not null } notFoundFailure => new
        {
            message = notFoundFailure.Message,
            id = notFoundFailure.Id
        },
        FieldValidationFailure validationFailure => new
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
        ExternalServiceFailure externalServiceFailure => new
        {
            message = externalServiceFailure.Message,
            serviceName = externalServiceFailure.ServiceName
        },
        _ => new
        {
            message = failure.Message
        }
    };

    private static IResult UnexpectedFailure() => Results.Json(
        new { message = "An unexpected error occurred." },
        statusCode: StatusCodes.Status500InternalServerError);
}
