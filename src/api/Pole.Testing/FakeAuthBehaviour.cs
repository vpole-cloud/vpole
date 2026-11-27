// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Mediator;

namespace Pole.Testing;

public class FakeAuthBehaviour<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IMediatorRequest<TResponse>
    where TResponse : notnull
{
    public static readonly Dictionary<TRequest, MediatorResult<TResponse>> Mocks = new();

    public Task<MediatorResult<TResponse>> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (Mocks.TryGetValue(request, out var response))
            return Task.FromResult(response);

        return next();
    }
}
