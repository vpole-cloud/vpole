using Microsoft.Extensions.DependencyInjection;

namespace Pole.Mediator;

public sealed class Mediator(IServiceProvider sp) : IMediator
{
    public async Task<MediatorResult<TResponse>> Send<TResponse>(
        IMediatorRequest<TResponse> request,
        CancellationToken cancellationToken = default)
        where TResponse : notnull
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        var requestType = request.GetType();
        var outcomeHandlerType = typeof(IMediatorOutcomeHandler<,>)
            .MakeGenericType(requestType, typeof(TResponse));
        var outcomeHandler = sp.GetService(outcomeHandlerType);

        if (outcomeHandler is null)
        {
            throw new InvalidOperationException(
                $"No mediator handler is registered for request '{requestType.FullName}'.");
        }

        var behaviorsType = typeof(IPipelineBehavior<,>).MakeGenericType(requestType, typeof(TResponse));
        var behaviors = sp.GetServices(behaviorsType)
                            ?.Cast<dynamic>()
                            .ToArray()
                        ?? Array.Empty<dynamic>();

        var handleAsyncMethod = outcomeHandlerType.GetMethod(
                nameof(IMediatorOutcomeHandler<
                    IMediatorRequest<TResponse>,
                    TResponse>.HandleAsync))
            ?? throw new InvalidOperationException(
                $"HandleAsync was not found on handler type '{outcomeHandlerType.FullName}'.");
        RequestHandlerDelegate<TResponse> next = async () =>
            await (Task<TResponse>)handleAsyncMethod.Invoke(
                outcomeHandler,
                [request, cancellationToken])!;

        for (var i = behaviors.Length - 1; i >= 0; i--)
        {
            dynamic behavior = behaviors[i];
            var currentNext = next;
            next = () => behavior.Handle((dynamic)request, currentNext, cancellationToken);
        }

        return await next();
    }
}
