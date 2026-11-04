namespace Pole.Features.GitOps.Contracts;

// Refer to server-owned actions. No client-supplied operations or authorization claims.
public sealed record PreviewConfirmationReference(
    string PreviewId,
    long ExpectedRevision);
