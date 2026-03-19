using Microsoft.Extensions.DependencyInjection;
using Pole.ResultType;

namespace Pole.Mediator;

public sealed class Mediator(IServiceProvider sp) : IMediator
{
    public async Task<Result<TResponse>> Send<TResponse>(IMediatorRequest<TResponse> request, CancellationToken cancellationToken = default) where TResponse : notnull
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        var requestType = request.GetType();
        var handlerType = typeof(IMediatorHandler<,>).MakeGenericType(requestType, typeof(TResponse));
        var handler = sp.GetRequiredService(handlerType);
        var handleAsyncMethod = handlerType.GetMethod(nameof(IMediatorHandler<IMediatorRequest<TResponse>, TResponse>.HandleAsync))
            ?? throw new InvalidOperationException($"HandleAsync was not found on handler type '{handlerType.FullName}'.");

        var behaviorsType = typeof(IPipelineBehavior<,>).MakeGenericType(requestType, typeof(TResponse));
        var behaviors = sp.GetServices(behaviorsType)
                            ?.Cast<dynamic>()
                            .ToArray()
                        ?? Array.Empty<dynamic>();

        // Final delegate = actual handler
        RequestHandlerDelegate<TResponse> next = () =>
            (Task<Result<TResponse>>)handleAsyncMethod.Invoke(handler, [request, cancellationToken])!;

        // Wrap with behaviors (last registered runs closest to handler)
        for (var i = behaviors.Length - 1; i >= 0; i--)
        {
            dynamic behavior = behaviors[i];
            var currentNext = next;
            next = () => behavior.Handle((dynamic)request, currentNext, cancellationToken);
        }

        return await next();
    }
}

public interface IMediator
{
    Task<Result<TResponse>> Send<TResponse>(
        IMediatorRequest<TResponse> request,
        CancellationToken cancellationToken = default)
        where TResponse : notnull;
}
