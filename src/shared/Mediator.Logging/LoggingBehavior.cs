// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.Logging;

namespace Pole.Mediator;

public sealed class LoggingBehavior<TRequest, TResponse>(
    ILogger<LoggingBehavior<TRequest, TResponse>> logger
) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IMediatorRequest<TResponse>
    where TResponse : notnull
{
    public async Task<MediatorResult<TResponse>> Handle(
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
