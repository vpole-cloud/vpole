using Pole.ResultType;

namespace Pole.Mediator;

public interface IMediatorRequest<TResponse> where TResponse : notnull { }

public interface IMediatorHandler<in TRequest, TResponse>
    where TRequest : IMediatorRequest<TResponse>
    where TResponse : notnull
{
    Task<Result<TResponse>> HandleAsync(TRequest request, CancellationToken cancellationToken = default);
}

public delegate Task<Result<TResponse>> RequestHandlerDelegate<TResponse>() where TResponse : notnull;

public interface IPipelineBehavior<in TRequest, TResponse>
    where TRequest : IMediatorRequest<TResponse>
    where TResponse : notnull
{
    Task<Result<TResponse>> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken);
}