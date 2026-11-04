using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Pole.Features.Functions.Domain;
using Pole.Features.Functions.Runtime.Endpoints;
using Pole.Features.Functions.Runtime.Security;
using Pole.Functions.Protocol;

namespace Pole.Features.Functions.Runtime.Agent;

public sealed record FunctionAgentProtocolResponse<T>(
    HttpStatusCode StatusCode,
    T? Value,
    FunctionProtocolError? Error)
{
    public bool IsSuccess => Value is not null && (int)StatusCode is >= 200 and < 300;
}

public interface IFunctionAgentClient
{
    Task<FunctionAgentProtocolResponse<FunctionAgentMetadataResponse>> GetMetadataAsync(
        FunctionEndpoint endpoint,
        FunctionApp app,
        FunctionRevision revision,
        CancellationToken cancellationToken);

    Task<FunctionAgentProtocolResponse<FunctionAgentStatusResponse>> GetStatusAsync(
        FunctionEndpoint endpoint,
        FunctionApp app,
        FunctionRevision revision,
        CancellationToken cancellationToken);

    Task<FunctionAgentProtocolResponse<ReserveFunctionAttemptResponse>> ReserveAsync(
        FunctionEndpoint endpoint,
        FunctionApp app,
        FunctionRevision revision,
        ReserveFunctionAttemptRequest request,
        CancellationToken cancellationToken);

    Task<FunctionAgentProtocolResponse<StartFunctionAttemptResponse>> ExecuteAsync(
        FunctionEndpoint endpoint,
        FunctionApp app,
        FunctionRevision revision,
        string attemptId,
        ExecuteFunctionAttemptRequest request,
        CancellationToken cancellationToken);

    Task<FunctionAgentProtocolResponse<FunctionAttemptStatusResponse>> ObserveAsync(
        FunctionEndpoint endpoint,
        FunctionApp app,
        FunctionRevision revision,
        string attemptId,
        int waitUpToMilliseconds,
        CancellationToken cancellationToken);
}

public sealed class FunctionAgentClient(
    HttpClient httpClient,
    IFunctionAgentTokenIssuer tokenIssuer) : IFunctionAgentClient
{
    private const int MaxProtocolResponseBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = PoleFunctionProtocolJson.CreateOptions();

    public Task<FunctionAgentProtocolResponse<FunctionAgentMetadataResponse>> GetMetadataAsync(
        FunctionEndpoint endpoint,
        FunctionApp app,
        FunctionRevision revision,
        CancellationToken cancellationToken) =>
        SendAsync<FunctionAgentMetadataResponse>(
            HttpMethod.Get,
            endpoint,
            app,
            revision,
            PoleFunctionProtocol.MetadataPath,
            PoleFunctionAgentToken.Operations.Metadata,
            content: null,
            attempt: null,
            attemptId: null,
            cancellationToken);

    public Task<FunctionAgentProtocolResponse<FunctionAgentStatusResponse>> GetStatusAsync(
        FunctionEndpoint endpoint,
        FunctionApp app,
        FunctionRevision revision,
        CancellationToken cancellationToken) =>
        SendAsync<FunctionAgentStatusResponse>(
            HttpMethod.Get,
            endpoint,
            app,
            revision,
            PoleFunctionProtocol.AgentStatusPath,
            PoleFunctionAgentToken.Operations.AgentStatus,
            content: null,
            attempt: null,
            attemptId: null,
            cancellationToken);

    public Task<FunctionAgentProtocolResponse<ReserveFunctionAttemptResponse>> ReserveAsync(
        FunctionEndpoint endpoint,
        FunctionApp app,
        FunctionRevision revision,
        ReserveFunctionAttemptRequest request,
        CancellationToken cancellationToken) =>
        SendAsync<ReserveFunctionAttemptResponse>(
            HttpMethod.Post,
            endpoint,
            app,
            revision,
            PoleFunctionProtocol.ReserveAttemptPath,
            PoleFunctionAgentToken.Operations.ReserveAttempt,
            JsonContent.Create(request, options: JsonOptions),
            request.Attempt,
            request.Attempt.AttemptId,
            cancellationToken);

    public Task<FunctionAgentProtocolResponse<StartFunctionAttemptResponse>> ExecuteAsync(
        FunctionEndpoint endpoint,
        FunctionApp app,
        FunctionRevision revision,
        string attemptId,
        ExecuteFunctionAttemptRequest request,
        CancellationToken cancellationToken) =>
        SendAsync<StartFunctionAttemptResponse>(
            HttpMethod.Post,
            endpoint,
            app,
            revision,
            PoleFunctionProtocol.ExecuteAttemptPath(attemptId),
            PoleFunctionAgentToken.Operations.ExecuteAttempt,
            JsonContent.Create(request, options: JsonOptions),
            attempt: null,
            attemptId,
            cancellationToken);

    public Task<FunctionAgentProtocolResponse<FunctionAttemptStatusResponse>> ObserveAsync(
        FunctionEndpoint endpoint,
        FunctionApp app,
        FunctionRevision revision,
        string attemptId,
        int waitUpToMilliseconds,
        CancellationToken cancellationToken) =>
        SendAsync<FunctionAttemptStatusResponse>(
            HttpMethod.Get,
            endpoint,
            app,
            revision,
            $"{PoleFunctionProtocol.AttemptStatusPath(attemptId)}?waitUpToMs={Math.Max(0, waitUpToMilliseconds)}",
            PoleFunctionAgentToken.Operations.ObserveAttempt,
            content: null,
            attempt: null,
            attemptId,
            cancellationToken);

    private async Task<FunctionAgentProtocolResponse<T>> SendAsync<T>(
        HttpMethod method,
        FunctionEndpoint endpoint,
        FunctionApp app,
        FunctionRevision revision,
        string path,
        string operation,
        HttpContent? content,
        FunctionAttemptIdentity? attempt,
        string? attemptId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(endpoint.TargetUid))
            throw new InvalidOperationException("Function Agent endpoint is missing its Pod UID.");

        using var request = new HttpRequestMessage(method, BuildUri(endpoint, path)) { Content = content };
        var token = tokenIssuer.Issue(new FunctionAgentTokenDescriptor(
            app.TenantId,
            app.Id,
            revision.Id,
            endpoint.TargetUid,
            operation,
            attempt,
            attemptId));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.Content.Headers.ContentLength > MaxProtocolResponseBytes)
            throw new FunctionAgentProtocolException("Function Agent response exceeded the protocol size limit.");
        if (response.IsSuccessStatusCode)
        {
            var value = await DeserializeAsync<T>(response.Content, cancellationToken);
            if (value is null)
                throw new FunctionAgentProtocolException("Function Agent returned an empty protocol response.");
            return new FunctionAgentProtocolResponse<T>(response.StatusCode, value, null);
        }

        var emptyAuthenticationError = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new FunctionProtocolError(
                "agent_unauthorized", "Function Agent rejected authentication."),
            HttpStatusCode.Forbidden => new FunctionProtocolError(
                "agent_forbidden", "Function Agent rejected authorization."),
            _ => null
        };
        var error = await DeserializeAsync(
                        response.Content, cancellationToken, emptyAuthenticationError) ??
                    new FunctionProtocolError(
                        "invalid_agent_response", "Function Agent returned an invalid error response.");
        return new FunctionAgentProtocolResponse<T>(response.StatusCode, default, error);
    }

    private static async Task<T?> DeserializeAsync<T>(
        HttpContent content,
        CancellationToken cancellationToken,
        T? emptyResponse = default)
    {
        var responseStream = await content.ReadAsStreamAsync(cancellationToken);
        await using var boundedStream = new BoundedResponseStream(responseStream, MaxProtocolResponseBytes);
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(boundedStream, JsonOptions, cancellationToken);
        }
        catch (FunctionAgentResponseTooLargeException exception)
        {
            throw new FunctionAgentProtocolException(
                "Function Agent response exceeded the protocol size limit.",
                exception);
        }
        catch (JsonException) when (boundedStream.BytesRead == 0 && emptyResponse is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return emptyResponse;
        }
        catch (JsonException exception)
        {
            throw new FunctionAgentProtocolException("Function Agent returned invalid protocol JSON.", exception);
        }
    }

    private static Uri BuildUri(FunctionEndpoint endpoint, string path)
    {
        var separator = path.IndexOf('?');
        var builder = new UriBuilder(Uri.UriSchemeHttp, endpoint.Address, endpoint.Port)
        {
            Path = separator < 0 ? path : path[..separator],
            Query = separator < 0 ? string.Empty : path[(separator + 1)..]
        };
        return builder.Uri;
    }
}

public sealed class FunctionAgentProtocolException(string message, Exception? innerException = null)
    : Exception(message, innerException);

internal sealed class FunctionAgentResponseTooLargeException : Exception;

internal sealed class BoundedResponseStream(Stream inner, long maxBytes) : Stream
{
    private long bytesRead;

    public long BytesRead => bytesRead;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        Count(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken);
        Count(read);
        return read;
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private void Count(int read)
    {
        bytesRead += read;
        if (bytesRead > maxBytes)
            throw new FunctionAgentResponseTooLargeException();
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
