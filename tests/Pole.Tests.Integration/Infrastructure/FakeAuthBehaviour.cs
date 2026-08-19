using Pole.Mediator;

namespace Pole.Tests.Integration.Infrastructure;

public class FakeAuthBehaviour<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IMediatorRequest<TResponse>
    where TResponse : notnull
{
    public static readonly Dictionary<TRequest, MediatorResult<TResponse>> Mocks = new();

    public Task<MediatorResult<TResponse>> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (Mocks.TryGetValue(request, out var response))
        {
            return Task.FromResult(response);
        }

        return next();
    }
}
