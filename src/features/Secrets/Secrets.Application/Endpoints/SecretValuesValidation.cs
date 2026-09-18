using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Secrets.Domain;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.Secrets.Application.Endpoints;

internal static class SecretValuesValidation
{
    internal static bool Valid(Dictionary<string, string>? values) => values is { Count: > 0 and <= 64 } &&
        values.All(x => !string.IsNullOrWhiteSpace(x.Key) && x.Key.Length <= 128 &&
            System.Text.Encoding.UTF8.GetByteCount(x.Key) <= 128 &&
            x.Key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.') && x.Value is not null) &&
        values.Sum(x => (long)System.Text.Encoding.UTF8.GetByteCount(x.Value)) <= 65536;
}
