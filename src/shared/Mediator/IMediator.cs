using Pole.ResultType;

namespace Pole.Mediator;

public interface IMediator
{
    Task<Result<TResponse>> Send<TResponse>(
        IMediatorRequest<TResponse> request,
        CancellationToken cancellationToken = default)
        where TResponse : notnull;
}
