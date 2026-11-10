using System.Text.Json;
using System.Text.Json.Serialization;
using Pole.ASP.Helpers;
using ClientJson = System.Net.Http.Json.HttpClientJsonExtensions;
using ContentJson = System.Net.Http.Json.HttpContentJsonExtensions;

namespace Pole.Tests.Integration.Http;

// Pole API callers use the same wire contract as the server. Tests for malformed
// JSON send raw StringContent so this adapter cannot mask rejected input.
internal static class PoleHttpClientJsonExtensions
{
    private static readonly JsonSerializerOptions Options = PoleHttpJson.CreateSerializerOptions();

    public static Task<T?> GetFromJsonAsync<T>(
        this HttpClient client,
        string? uri,
        CancellationToken cancellationToken = default) =>
        ClientJson.GetFromJsonAsync<T>(client, uri, Options, cancellationToken);

    public static Task<T?> GetFromJsonAsync<T>(
        this HttpClient client,
        string? uri,
        JsonSerializerOptions? options,
        CancellationToken cancellationToken = default) => ClientJson.GetFromJsonAsync<T>(
        client,
        uri,
        WithHttpEnums(options),
        cancellationToken);

    public static Task<T?>
        ReadFromJsonAsync<T>(this HttpContent content, CancellationToken cancellationToken = default) =>
        ContentJson.ReadFromJsonAsync<T>(content, Options, cancellationToken);

    public static Task<T?> ReadFromJsonAsync<T>(
        this HttpContent content,
        JsonSerializerOptions? options,
        CancellationToken cancellationToken = default) =>
        ContentJson.ReadFromJsonAsync<T>(content, WithHttpEnums(options), cancellationToken);

    public static Task<HttpResponseMessage> PostAsJsonAsync<T>(
        this HttpClient client,
        string? uri,
        T value,
        CancellationToken cancellationToken = default) =>
        ClientJson.PostAsJsonAsync(client, uri, value, Options, cancellationToken);

    public static Task<HttpResponseMessage> PostAsJsonAsync<T>(
        this HttpClient client,
        string? uri,
        T value,
        JsonSerializerOptions? options,
        CancellationToken cancellationToken = default) => ClientJson.PostAsJsonAsync(
        client,
        uri,
        value,
        WithHttpEnums(options),
        cancellationToken);

    public static Task<HttpResponseMessage> PutAsJsonAsync<T>(
        this HttpClient client,
        string? uri,
        T value,
        CancellationToken cancellationToken = default) =>
        ClientJson.PutAsJsonAsync(client, uri, value, Options, cancellationToken);

    public static Task<HttpResponseMessage> PutAsJsonAsync<T>(
        this HttpClient client,
        string? uri,
        T value,
        JsonSerializerOptions? options,
        CancellationToken cancellationToken = default) => ClientJson.PutAsJsonAsync(
        client,
        uri,
        value,
        WithHttpEnums(options),
        cancellationToken);

    public static Task<HttpResponseMessage> PatchAsJsonAsync<T>(
        this HttpClient client,
        string? uri,
        T value,
        CancellationToken cancellationToken = default) =>
        ClientJson.PatchAsJsonAsync(client, uri, value, Options, cancellationToken);

    public static Task<HttpResponseMessage> PatchAsJsonAsync<T>(
        this HttpClient client,
        string? uri,
        T value,
        JsonSerializerOptions? options,
        CancellationToken cancellationToken = default) => ClientJson.PatchAsJsonAsync(
        client,
        uri,
        value,
        WithHttpEnums(options),
        cancellationToken);

    private static JsonSerializerOptions WithHttpEnums(JsonSerializerOptions? options)
    {
        if (options is null)
            return Options;
        var result = new JsonSerializerOptions(options);
        result.Converters.Insert(0, new JsonStringEnumConverter(allowIntegerValues: false));
        return result;
    }
}
