// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.Functions.Runtime.Kubernetes;

namespace Pole.Features.Functions.Runtime.Activation;

public sealed record FunctionActivationCoordinatorRejected(
    string Message
);

public sealed record FunctionActivationCoordinatorUnavailable(
    string Message
);

public sealed record FunctionActivationTimedOut(
    string Message
);

public sealed record FunctionActivationScaleRejected(
    FunctionRevisionKubernetesOperationError Error
);

public union FunctionRevisionActivationError(
    FunctionActivationCoordinatorRejected,
    FunctionActivationCoordinatorUnavailable,
    FunctionActivationTimedOut,
    FunctionActivationScaleRejected);

public static class FunctionRevisionActivationErrorExtensions
{
    public static string Message(this FunctionRevisionActivationError error) => error switch
    {
        FunctionActivationCoordinatorRejected rejected => rejected.Message,
        FunctionActivationCoordinatorUnavailable unavailable => unavailable.Message,
        FunctionActivationTimedOut timedOut => timedOut.Message,
        FunctionActivationScaleRejected rejected => rejected.Error.Message(),
    };
}
