// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Mediator;

/// <summary>
///     Marks a typed reason supplied by a pipeline behavior that stops request processing.
///     The marker is intentionally open so hosts and plugins can define their own reasons
///     without adding transport concerns to the Mediator contract.
/// </summary>
public interface IMediatorRejection;

public sealed record PipelineRejected
{
    public PipelineRejected(IMediatorRejection reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        Reason = reason;
    }

    public IMediatorRejection Reason { get; }
}

/// <summary>
///     Distinguishes a request-owned application outcome from a pipeline rejection.
/// </summary>
public union MediatorResult<TOutcome>(TOutcome, PipelineRejected)
    where TOutcome : notnull;
