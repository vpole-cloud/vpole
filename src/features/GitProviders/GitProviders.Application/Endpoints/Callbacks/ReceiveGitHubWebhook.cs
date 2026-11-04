using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Application.Persistence.Webhooks;
using Pole.Features.GitProviders.Domain;

namespace Pole.Features.GitProviders.Application.Endpoints.Callbacks;

public sealed record GitHubWebhookRequest(
    string DeliveryId,
    string EventType,
    string Signature,
    byte[] Body);

public enum GitHubWebhookResult { Accepted, InvalidSignature, InvalidPayload, DeliveryConflict, NotConfigured }

internal sealed record GitHubBranchPush(
    string InstallationId,
    string RepositoryId,
    string Branch,
    string HeadCommitHint);

public sealed class ReceiveGitHubWebhookHandler(
    GitHubAppOptions github,
    IOptions<GitHubWebhookOptions> options,
    GitHubWebhookPersistence persistence,
    TimeProvider clock)
{
    public async Task<GitHubWebhookResult> HandleAsync(GitHubWebhookRequest request, CancellationToken ct)
    {
        if (!github.Enabled || string.IsNullOrWhiteSpace(options.Value.Secret))
            return GitHubWebhookResult.NotConfigured;

        if (!VerifySignature(options.Value.Secret, request.Signature, request.Body))
            return GitHubWebhookResult.InvalidSignature;

        if (!Guid.TryParseExact(request.DeliveryId, "D", out var delivery) ||
            string.IsNullOrWhiteSpace(request.EventType) || request.EventType.Length > 128)
            return GitHubWebhookResult.InvalidPayload;

        GitHubBranchPush? push = null;
        if (request.EventType == "push")
        {
            try
            {
                using var document = JsonDocument.Parse(request.Body);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("ref", out var reference) ||
                    reference.ValueKind != JsonValueKind.String)
                    return GitHubWebhookResult.InvalidPayload;

                var gitRef = reference.GetString()!;
                if (gitRef.StartsWith("refs/heads/", StringComparison.Ordinal))
                {
                    if (gitRef.Length == "refs/heads/".Length || !TryId(root, "installation", out var installation) ||
                        !TryId(root, "repository", out var repository) ||
                        !root.TryGetProperty("after", out var after) ||
                        after.ValueKind != JsonValueKind.String || after.GetString() is not { Length: 40 } sha ||
                        !sha.All(char.IsAsciiHexDigit))
                        return GitHubWebhookResult.InvalidPayload;

                    push = new(installation, repository, gitRef["refs/heads/".Length..], sha.ToLowerInvariant());
                }
            }
            catch (JsonException)
            {
                return GitHubWebhookResult.InvalidPayload;
            }
        }

        var receipt = new GitHubDeliveryReceipt(delivery.ToString("D"),
            Convert.ToHexStringLower(SHA256.HashData(request.Body)),
            request.EventType,
            clock.GetUtcNow().UtcDateTime);
        return await persistence.StoreAsync(receipt, push, ct);
    }

    private static bool TryId(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var entity) || entity.ValueKind != JsonValueKind.Object ||
            !entity.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number ||
            !id.TryGetInt64(out var number) || number <= 0)
            return false;
        value = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    private static bool VerifySignature(string secret, string signature, byte[] body)
    {
        if (signature.Length != 71 || !signature.StartsWith("sha256=", StringComparison.Ordinal))
            return false;
        byte[] provided;
        try
        {
            provided = Convert.FromHexString(signature.AsSpan(7));
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }
}

internal static class ReceiveGitHubWebhook
{
    internal static void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapPost("/api/v1/integrations/github/webhook", HandleAsync)
            .AllowAnonymous().WithName("ReceiveGitHubWebhook");

    private static async Task<IResult> HandleAsync(
        HttpContext context,
        ReceiveGitHubWebhookHandler handler,
        CancellationToken ct)
    {
        if (context.Request.ContentLength > GitHubWebhookOptions.MaximumBodyBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        using var body = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await context.Request.Body.ReadAsync(buffer, ct)) != 0)
        {
            if (body.Length + read > GitHubWebhookOptions.MaximumBodyBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            await body.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        var headers = context.Request.Headers;
        var result = await handler.HandleAsync(new(headers["X-GitHub-Delivery"].ToString(),
                headers["X-GitHub-Event"].ToString(),
                headers["X-Hub-Signature-256"].ToString(),
                body.ToArray()),
            ct);
        return result switch
        {
            GitHubWebhookResult.Accepted => Results.Accepted(),
            GitHubWebhookResult.InvalidSignature => Results.Unauthorized(),
            GitHubWebhookResult.InvalidPayload => Results.BadRequest(),
            GitHubWebhookResult.DeliveryConflict => Results.Conflict(),
            GitHubWebhookResult.NotConfigured => Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
            _ => throw new ArgumentOutOfRangeException(nameof(result))
        };
    }
}
