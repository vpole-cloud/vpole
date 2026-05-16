using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Tests.Integration.Infrastructure;

public class FakeAuthBehaviour<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IMediatorRequest<TResponse>
    where TResponse : notnull
{
    public static readonly Dictionary<TRequest, Result<TResponse>> Mocks = new(); 
    public Task<Result<TResponse>> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (Mocks.TryGetValue(request, out var response))
        {
            return Task.FromResult(response);
        }

        return next();
    }
}