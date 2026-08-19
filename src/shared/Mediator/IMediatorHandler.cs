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
/// The mediator passes that outcome through its pipeline envelope without requiring a legacy result.
/// </summary>
public interface IMediatorOutcomeHandler<in TRequest, TOutcome>
    where TRequest : IMediatorRequest<TOutcome>
    where TOutcome : notnull
{
    Task<TOutcome> HandleAsync(
        TRequest request,
        CancellationToken cancellationToken = default);
}

public delegate Task<MediatorResult<TResponse>> RequestHandlerDelegate<TResponse>() where TResponse : notnull;

public interface IPipelineBehavior<in TRequest, TResponse>
    where TRequest : IMediatorRequest<TResponse>
    where TResponse : notnull
{
    Task<MediatorResult<TResponse>> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken);
}
