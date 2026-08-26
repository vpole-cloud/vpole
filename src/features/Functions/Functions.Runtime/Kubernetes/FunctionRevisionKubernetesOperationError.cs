namespace Pole.Features.Functions.Runtime.Kubernetes;

public sealed record FunctionRevisionKubernetesUnavailable(string Message);

public sealed record FunctionRevisionKubernetesOperationRejected(string Message);

public sealed record FunctionRevisionKubernetesOperationInvalid(string Message);

public union FunctionRevisionKubernetesOperationError(
    FunctionRevisionKubernetesUnavailable,
    FunctionRevisionKubernetesOperationRejected,
    FunctionRevisionKubernetesOperationInvalid);

public static class FunctionRevisionKubernetesOperationErrorExtensions
{
    public static string Message(this FunctionRevisionKubernetesOperationError error) => error switch
    {
        FunctionRevisionKubernetesUnavailable unavailable => unavailable.Message,
        FunctionRevisionKubernetesOperationRejected rejected => rejected.Message,
        FunctionRevisionKubernetesOperationInvalid invalid => invalid.Message
    };
}
