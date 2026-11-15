using Microsoft.Extensions.Options;

namespace Pole.Features.Tenants.Application.Invitations;

public sealed class InvitationOptions
{
    public string FrontendUrl { get; init; } = "";
}

public sealed class InvitationLinks(
    IOptions<InvitationOptions> options
)
{
    public string SetupCallback() => Frontend().AbsoluteUri.TrimEnd('/') + "/invitations";

    public string Acceptance(string token) => Frontend().AbsoluteUri.TrimEnd('/') +
        "/invitations#token=" + Uri.EscapeDataString(token);

    private Uri Frontend()
    {
        if (!Uri.TryCreate(options.Value.FrontendUrl, UriKind.Absolute, out var origin) ||
            (origin.Scheme != "http" && origin.Scheme != "https") || !string.IsNullOrEmpty(origin.UserInfo) ||
            !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment))
            throw new InvalidOperationException(
                "Invitations:FrontendUrl must be a trusted absolute HTTP(S) frontend URL.");
        return origin;
    }
}
