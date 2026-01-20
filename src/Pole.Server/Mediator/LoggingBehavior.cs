using Pole.Server.ResultType;

namespace Pole.Server.Mediator;

public sealed class LoggingBehavior<TRequest, TResponse>(
    ILogger<LoggingBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IMediatorRequest<TResponse>
    where TResponse : notnull
{
    public async Task<Result<TResponse>> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var name = typeof(TRequest).Name;
        logger.LogDebug("Handling {Request}", name);

        try
        {
            var result = await next();
            logger.LogDebug("Handled {Request}", name);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling {Request}", name);
            throw;
        }
    }
}