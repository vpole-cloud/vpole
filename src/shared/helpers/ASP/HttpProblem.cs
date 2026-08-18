using System.Collections.ObjectModel;
using Microsoft.AspNetCore.Http;

namespace Pole.ASP.Helpers;

public sealed record HttpProblem
{
    public HttpProblem(
        int statusCode,
        string type,
        string title,
        string code,
        string? detail = null,
        IReadOnlyDictionary<string, object?>? extensions = null)
    {
        if (statusCode is < 400 or > 599)
        {
            throw new ArgumentOutOfRangeException(
                nameof(statusCode),
                statusCode,
                "An HTTP problem status code must be between 400 and 599.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        if (extensions?.ContainsKey("code") is true)
        {
            throw new ArgumentException(
                "The 'code' extension is reserved by the HTTP problem model.",
                nameof(extensions));
        }

        StatusCode = statusCode;
        Type = type;
        Title = title;
        Code = code;
        Detail = detail;
        Extensions = new ReadOnlyDictionary<string, object?>(
            extensions is null
                ? new Dictionary<string, object?>()
                : new Dictionary<string, object?>(extensions));
    }

    public int StatusCode { get; }
    public string Type { get; }
    public string Title { get; }
    public string Code { get; }
    public string? Detail { get; }
    public IReadOnlyDictionary<string, object?> Extensions { get; }

    public IResult ToHttpResult()
    {
        var extensions = new Dictionary<string, object?>(Extensions)
        {
            ["code"] = Code
        };
        return Results.Problem(
            detail: Detail,
            statusCode: StatusCode,
            title: Title,
            type: Type,
            extensions: extensions);
    }
}
