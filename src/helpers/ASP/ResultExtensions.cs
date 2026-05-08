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
            NotFoundFailure => Results.NotFound(error),
            ValidationFailure validationFailure => Results.ValidationProblem(validationFailure.ValidationErrors, error.Message),
            UnauthorizedFailure => Results.Unauthorized(),
            ConflictFailure => Results.Conflict(error),
            InternalFailure => Results.InternalServerError(error),
            ExternalServiceFailure => Results.Problem(error.ToString()),
            _ => Results.Problem(detail: error.ToString())
        };
    }
    
}