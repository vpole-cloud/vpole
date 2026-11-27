// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Mediator;

namespace Pole.Tests.Integration.Infrastructure;

public class FakeAuthBehaviour<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IMediatorRequest<TResponse>
    where TResponse : notnull
{
    public Task<MediatorResult<TResponse>> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken) => next();
}
