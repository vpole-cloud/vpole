using Pole.ResultType;

namespace Pole.Mediator;

public interface IMediatorRequest<TResponse> where TResponse : notnull { }

public interface IMediatorHandler<in TRequest, TResponse>
    where TRequest : IMediatorRequest<TResponse>
    where TResponse : notnull
{
    Task<Result<TResponse>> HandleAsync(TRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Handles a request whose application outcome is already represented by its own closed type.
/// The mediator wraps that outcome in <see cref="Result{T}"/> only for pipeline short-circuiting.
/// </summary>
public interface IMediatorOutcomeHandler<in TRequest, TOutcome>
    where TRequest : IMediatorRequest<TOutcome>
    where TOutcome : notnull
{
    Task<TOutcome> HandleAsync(
        TRequest request,
        CancellationToken cancellationToken = default);
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
