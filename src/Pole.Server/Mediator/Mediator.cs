using Pole.Server.ResultType;

namespace Pole.Server.Mediator;

public sealed class Mediator : IMediator
{
    private readonly IServiceProvider _sp;

    public Mediator(IServiceProvider sp) => _sp = sp;

    public async Task<Result<TResponse>> Send<TResponse>(IMediatorRequest<TResponse> request, CancellationToken cancellationToken = default) where TResponse : notnull
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        var requestType = request.GetType();
        var handlerType = typeof(IMediatorHandler<,>).MakeGenericType(requestType, typeof(TResponse));
        dynamic handler = _sp.GetRequiredService(handlerType);

        var behaviorsType = typeof(IPipelineBehavior<,>).MakeGenericType(requestType, typeof(TResponse));
        var behaviors = _sp.GetServices(behaviorsType)
                            ?.Cast<dynamic>()
                            .ToArray()
                        ?? Array.Empty<dynamic>();

        // Final delegate = actual handler
        RequestHandlerDelegate<TResponse> next = () => handler.HandleAsync((dynamic)request, cancellationToken);

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