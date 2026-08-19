using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Pole.Mediator;

namespace Pole.ASP.Helpers;

internal sealed class MediatorRejectionHttpResult(
    IMediatorRejection rejection)
    : IResult
{
    public Task ExecuteAsync(HttpContext httpContext)
    {
        var mapper = httpContext.RequestServices
            .GetRequiredService<IHttpProblemMapper>();
        return mapper.Map(rejection)
            .ToHttpResult()
            .ExecuteAsync(httpContext);
    }
}
