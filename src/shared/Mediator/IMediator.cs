// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Mediator;

public interface IMediator
{
    Task<MediatorResult<TResponse>> Send<TResponse>(
        IMediatorRequest<TResponse> request,
        CancellationToken cancellationToken = default)
        where TResponse : notnull;
}
