using Microsoft.AspNetCore.Http;
using Pole.Mediator;

namespace Pole.ASP.Helpers;

public static class ResultExtensions
{
    public static IResult ToHttpResult<T>(
        this MediatorResult<T> result,
        Func<T, IResult>? transformer = null)
        where T : notnull =>
        result.Match(
            value => transformer is not null ? transformer(value) : Results.Ok(value),
            rejected => new MediatorRejectionHttpResult(rejected.Reason));
}
