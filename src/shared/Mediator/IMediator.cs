namespace Pole.Mediator;

public interface IMediator
{
    Task<MediatorResult<TResponse>> Send<TResponse>(
        IMediatorRequest<TResponse> request,
        CancellationToken cancellationToken = default)
        where TResponse : notnull;
}
