using Pole.ASP.Helpers;

namespace Pole.Features.Secrets.Application;

public static class SecretHttpMappings
{
    public static HttpProblem MapGitOwned(SecretGitOwned _) => Problem(
        409,
        "secrets.git-owned",
        "Edit the desired configuration in its managing Git source");

    public static HttpProblem MapManagedByProvider(SecretManagedByProvider _) => Problem(
        409,
        "secrets.managed-by-provider",
        "Change the managed credential through its owning resource");

    public static HttpProblem MapMissing(SecretMissing _) => Problem(404, "secrets.not-found", "Secret not found");

    public static HttpProblem MapNameAlreadyExists(SecretNameAlreadyExists _) => Problem(
        409,
        "secrets.name-already-exists",
        "Secret name already exists in this tenant");

    public static HttpProblem MapReferenceRejected(SecretReferenceRejected _) => Problem(
        422,
        "secrets.reference-rejected",
        "Secret reference is unavailable or outside its scope");

    public static HttpProblem MapEncryptionFailed(SecretEncryptionFailed _) => Problem(
        503,
        "secrets.encryption-unavailable",
        "Secret encryption unavailable");

    public static HttpProblem MapVersionConflict(SecretVersionConflict _) => Problem(
        409,
        "secrets.version-conflict",
        "Secret version changed; reload before rotating");

    public static HttpProblem MapMappedKeysRequired(SecretMappedKeysRequired _) => Problem(
        409,
        "secrets.mapped-keys-required",
        "Rotation must retain keys used by bindings");

    public static HttpProblem MapBindingLimitReached(SecretBindingLimitReached _) => Problem(
        409,
        "secrets.binding-limit-reached",
        "Secret binding limit reached");

    public static HttpProblem MapConsumerAlreadyBound(SecretConsumerAlreadyBound _) => Problem(
        409,
        "secrets.consumer-already-bound",
        "Consumer already has a binding to this Secret");

    public static HttpProblem MapUnknownKeys(SecretUnknownKeys _) => Problem(
        409,
        "secrets.unknown-keys",
        "Binding references unknown Secret keys");

    public static HttpProblem MapBindingsRemain(SecretBindingsRemain _) => Problem(
        409,
        "secrets.bindings-remain",
        "Detach Secret bindings before deletion");

    private static HttpProblem Problem(
        int status,
        string code,
        string title) => new(status, PoleProblemTypeId.FromCode(code), title, code);
}
