namespace Pole.PrivateAccess.Prototype;

public sealed record AccessIdentity(
    string TenantId,
    string SubjectId);

public sealed record AccessRequest(
    string GrantId,
    string ResourceDeploymentId,
    string EndpointId);

public sealed record AccessBinding(
    AccessIdentity Identity,
    AccessRequest Request,
    string TargetId,
    long Revision);

public sealed record AccessAllowed(
    AccessBinding Binding,
    DateTimeOffset ExpiresAt);

public sealed record AccessDenied;
public sealed record AuthorityUnavailable;

public union AuthorizeAccessOutcome(
    AccessAllowed,
    AccessDenied,
    AuthorityUnavailable);

/// <summary>Online source of truth, called for every admission and lease renewal.</summary>
public interface IPrivateAccessAuthority
{
    ValueTask<AuthorizeAccessOutcome> AuthorizeAsync(
        AccessIdentity identity,
        AccessRequest request,
        CancellationToken cancellationToken);
}

public delegate ValueTask<AuthorizeAccessOutcome> RenewAccess(CancellationToken cancellationToken);

/// <summary>
/// A provider must enforce expiry locally even when renewal/control traffic disappears.
/// No transport-specific identity, wire message or destination address escapes this boundary.
/// </summary>
public interface IPrivateAccessTransport
{
    Task<PrivateAccessChannel> OpenAsync(
        AccessAllowed access,
        RenewAccess renew,
        CancellationToken cancellationToken);
}

public abstract class PrivateAccessChannel : IAsyncDisposable
{
    public abstract ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);
    public abstract ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken);
    public abstract ValueTask CompleteWritesAsync(CancellationToken cancellationToken);
    public abstract ValueTask DisposeAsync();
}

public sealed record AccessOpened(PrivateAccessChannel Channel);

public union OpenAccessOutcome(
    AccessOpened,
    AccessDenied,
    AuthorityUnavailable);

/// <summary>Transport-independent admission and exact binding validation.</summary>
public sealed class PrivateAccessCoordinator(
    IPrivateAccessAuthority authority,
    IPrivateAccessTransport transport)
{
    public async Task<OpenAccessOutcome> OpenAsync(
        AccessIdentity identity,
        AccessRequest request,
        CancellationToken cancellationToken)
    {
        var authorization = await authority.AuthorizeAsync(identity, request, cancellationToken)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        return authorization switch
        {
            AccessAllowed access => await OpenAllowedAsync(access, identity, request, cancellationToken),
            AccessDenied denied => denied,
            AuthorityUnavailable unavailable => unavailable
        };
    }

    private async Task<OpenAccessOutcome> OpenAllowedAsync(
        AccessAllowed access,
        AccessIdentity identity,
        AccessRequest request,
        CancellationToken cancellationToken)
    {
        if (access.ExpiresAt <= DateTimeOffset.UtcNow)
            return new AccessDenied();
        if (access.Binding.Identity != identity || access.Binding.Request != request)
            return new AccessDenied();

        var channel = await transport.OpenAsync(access, Renew, cancellationToken);
        return new AccessOpened(channel);

        async ValueTask<AuthorizeAccessOutcome> Renew(CancellationToken token)
        {
            var result = await authority.AuthorizeAsync(identity, request, token).AsTask().WaitAsync(token);
            return result switch
            {
                AccessAllowed current when current.Binding == access.Binding => current,
                AccessAllowed => new AccessDenied(),
                AccessDenied denied => denied,
                AuthorityUnavailable unavailable => unavailable
            };
        }
    }
}
