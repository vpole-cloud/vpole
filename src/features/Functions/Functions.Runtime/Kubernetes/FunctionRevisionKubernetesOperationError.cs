// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.Functions.Runtime.Kubernetes;

public sealed record FunctionRevisionKubernetesUnavailable(
    string Message
);

public sealed record FunctionRevisionGatewaySourceUnavailable(
    string Message
);

public sealed record FunctionRevisionSecretsUnavailable(
    string Message
);

public sealed record FunctionRevisionKubernetesOperationRejected(
    string Message
);

public sealed record FunctionRevisionKubernetesOperationInvalid(
    string Message
);

public union FunctionRevisionKubernetesOperationError(
    FunctionRevisionKubernetesUnavailable,
    FunctionRevisionGatewaySourceUnavailable,
    FunctionRevisionSecretsUnavailable,
    FunctionRevisionKubernetesOperationRejected,
    FunctionRevisionKubernetesOperationInvalid);

public static class FunctionRevisionKubernetesOperationErrorExtensions
{
    public static string Message(this FunctionRevisionKubernetesOperationError error) => error switch
    {
        FunctionRevisionKubernetesUnavailable unavailable => unavailable.Message,
        FunctionRevisionGatewaySourceUnavailable unavailable => unavailable.Message,
        FunctionRevisionSecretsUnavailable unavailable => unavailable.Message,
        FunctionRevisionKubernetesOperationRejected rejected => rejected.Message,
        FunctionRevisionKubernetesOperationInvalid invalid => invalid.Message,
    };
}
