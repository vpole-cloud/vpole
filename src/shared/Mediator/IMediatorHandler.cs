// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Mediator;

public interface IMediatorRequest<TResponse>
    where TResponse : notnull
{
}

/// <summary>
///     Handles a request whose application outcome is represented by its own closed type.
///     The mediator passes that outcome through its pipeline envelope.
/// </summary>
public interface IMediatorOutcomeHandler<in TRequest, TOutcome>
    where TRequest : IMediatorRequest<TOutcome>
    where TOutcome : notnull
{
    Task<TOutcome> HandleAsync(TRequest request, CancellationToken cancellationToken = default);
}

public delegate Task<MediatorResult<TResponse>> RequestHandlerDelegate<TResponse>()
    where TResponse : notnull;

public interface IPipelineBehavior<in TRequest, TResponse>
    where TRequest : IMediatorRequest<TResponse>
    where TResponse : notnull
{
    Task<MediatorResult<TResponse>> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken);
}
