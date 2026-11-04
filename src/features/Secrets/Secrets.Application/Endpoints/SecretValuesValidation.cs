using System.Text;
using Pole.Features.Secrets.Contracts;

namespace Pole.Features.Secrets.Application.Endpoints;

internal static class SecretValuesValidation
{
    internal const int MaximumTotalValueBytes = 64 * 1024;
    internal static bool Valid(Dictionary<string,
        string>? values) => values is { Count: > 0 and <= SecretKeyReferenceRules.MaximumKeys } &&
        values.All(x => SecretKeyReferenceRules.ValidKey(x.Key) && x.Value is not null) &&
        values.Sum(x => (long)Encoding.UTF8.GetByteCount(x.Value)) <= MaximumTotalValueBytes;
}
