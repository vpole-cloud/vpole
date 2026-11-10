namespace Pole.AccessGateway;

public sealed class BrokerOrigin
{
    private BrokerOrigin(Uri https) => Https = https;

    public Uri Https { get; }

    public static BrokerOrigin Parse(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
            throw new ArgumentException("Broker origin must be an HTTPS origin.", nameof(value));
        return new BrokerOrigin(uri);
    }

    public Uri ApiPath(string path) => new(Https, path);

    public Uri WebSocketPath(string path)
    {
        var builder = new UriBuilder(new Uri(Https, path))
        {
            Scheme = "wss",
        };
        return builder.Uri;
    }
}
