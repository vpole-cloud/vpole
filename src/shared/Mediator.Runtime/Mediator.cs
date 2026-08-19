using Microsoft.Extensions.DependencyInjection;
using Pole.ResultType;

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
        var resultHandlerType = typeof(IMediatorHandler<,>)
            .MakeGenericType(requestType, typeof(TResponse));
        var outcomeHandlerType = typeof(IMediatorOutcomeHandler<,>)
            .MakeGenericType(requestType, typeof(TResponse));
        var resultHandler = sp.GetService(resultHandlerType);
        var outcomeHandler = sp.GetService(outcomeHandlerType);

        if (resultHandler is not null && outcomeHandler is not null)
        {
            throw new InvalidOperationException(
                $"Request '{requestType.FullName}' has both a result handler and an outcome handler.");
        }

        if (resultHandler is null && outcomeHandler is null)
        {
            throw new InvalidOperationException(
                $"No mediator handler is registered for request '{requestType.FullName}'.");
        }

        var behaviorsType = typeof(IPipelineBehavior<,>).MakeGenericType(requestType, typeof(TResponse));
        var behaviors = sp.GetServices(behaviorsType)
                            ?.Cast<dynamic>()
                            .ToArray()
                        ?? Array.Empty<dynamic>();

        RequestHandlerDelegate<TResponse> next;
        if (outcomeHandler is not null)
        {
            var handleAsyncMethod = outcomeHandlerType.GetMethod(
                    nameof(IMediatorOutcomeHandler<
                        IMediatorRequest<TResponse>,
                        TResponse>.HandleAsync))
                ?? throw new InvalidOperationException(
                    $"HandleAsync was not found on handler type '{outcomeHandlerType.FullName}'.");
            next = async () => await (Task<TResponse>)handleAsyncMethod.Invoke(
                outcomeHandler,
                [request, cancellationToken])!;
        }
        else
        {
            var handleAsyncMethod = resultHandlerType.GetMethod(
                    nameof(IMediatorHandler<
                        IMediatorRequest<TResponse>,
                        TResponse>.HandleAsync))
                ?? throw new InvalidOperationException(
                    $"HandleAsync was not found on handler type '{resultHandlerType.FullName}'.");
            next = async () => ToMediatorResult(
                await (Task<Result<TResponse>>)handleAsyncMethod.Invoke(
                    resultHandler,
                    [request, cancellationToken])!);
        }

        for (var i = behaviors.Length - 1; i >= 0; i--)
        {
            dynamic behavior = behaviors[i];
            var currentNext = next;
            next = () => behavior.Handle((dynamic)request, currentNext, cancellationToken);
        }

        return await next();
    }

    private static MediatorResult<TResponse> ToMediatorResult<TResponse>(
        Result<TResponse> result)
        where TResponse : notnull
    {
        if (result.TryGetValue(out var outcome, out var failure))
        {
            return outcome;
        }

        return new PipelineRejected(new LegacyResultRejected(failure));
    }
}
