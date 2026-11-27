// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using FluentValidation;
using Microsoft.Extensions.Logging;
using ValidationFailure = FluentValidation.Results.ValidationFailure;

namespace Pole.Mediator;

public class ValidationBehavior<TRequest, TResponse>(
    ILogger<ValidationBehavior<TRequest, TResponse>> logger,
    IEnumerable<IValidator<TRequest>> validators
) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IMediatorRequest<TResponse>
    where TResponse : notnull
{
    public async Task<MediatorResult<TResponse>> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (!validators.Any())
            return await next();

        var context = new ValidationContext<TRequest>(request);

        ValidationFailure[] failures = validators.Select(v => v.Validate(context))
            .SelectMany(r => r.Errors)
            .Where(f => f is not null)
            .ToArray();

        if (failures.Length == 0)
            return await next();

        var errorMap = failures.GroupBy(f => f.PropertyName ?? "")
            .ToDictionary(g => g.Key, g => g.Select(f => f.ErrorMessage).Distinct().ToArray());

        logger.LogWarning("Validation failed for {RequestType}. Errors: {Errors}", typeof(TRequest).Name, errorMap);

        return new PipelineRejected(new RequestValidationRejected(errorMap));
    }
}
