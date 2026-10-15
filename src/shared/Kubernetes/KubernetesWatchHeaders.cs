using System.Net;

namespace Pole.Features.Core.Kubernetes;

/// <summary>Observes watch transport readiness before the SDK waits for its first frame.</summary>
public sealed class KubernetesWatchHeaders : DelegatingHandler
{
    private static readonly AsyncLocal<Registration?> Current = new();

    public static Registration Begin(Action opened, CancellationToken cancellationToken)
    {
        var registration = new Registration(opened, Current.Value, cancellationToken);
        Current.Value = registration;
        return registration;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // Capture before await. Concurrent watches on a cached client have separate async flows.
        var registration = Current.Value;
        var response = await base.SendAsync(request, cancellationToken);
        if (registration is not null && response.StatusCode == HttpStatusCode.OK && IsWatch(request.RequestUri))
            registration.Capture(response);
        return response;
    }

    private static bool IsWatch(Uri? uri) => uri?.Query.TrimStart('?').Split('&').Any(parameter =>
    {
        var parts = parameter.Split('=', 2);
        return parts.Length == 2 && Uri.UnescapeDataString(parts[0]) == "watch" &&
               string.Equals(Uri.UnescapeDataString(parts[1]), "true", StringComparison.OrdinalIgnoreCase);
    }) == true;

    public sealed class Registration : IDisposable
    {
        private readonly object _gate = new();
        private readonly Action _opened;
        private readonly Registration? _previous;
        private readonly CancellationTokenRegistration _cancellation;
        private HttpResponseMessage? _response;
        private HttpContent? _originalContent;
        private bool _closed;

        internal Registration(Action opened, Registration? previous, CancellationToken cancellationToken)
        {
            _opened = opened;
            _previous = previous;
            _cancellation = cancellationToken.Register(CloseResponse);
        }

        internal void Capture(HttpResponseMessage response)
        {
            lock (_gate)
            {
                if (_closed)
                {
                    response.Dispose();
                    return;
                }

                if (_response is not null)
                    throw new InvalidOperationException("A watch registration owns one request.");
                _response = response;
                // The SDK replaces Response.Content with an internal wrapper after this callback.
                _originalContent = response.Content;
                _opened();
            }
        }

        public void CloseResponse()
        {
            HttpResponseMessage? response;
            HttpContent? original;
            lock (_gate)
            {
                if (_closed)
                    return;
                _closed = true;
                response = _response;
                original = _originalContent;
                _response = null;
                _originalContent = null;
            }

            try
            {
                original?.Dispose();
            }
            finally
            {
                try
                {
                    response?.Dispose();
                }
                finally
                {
                    response?.RequestMessage?.Dispose();
                }
            }
        }

        public void Dispose()
        {
            try
            {
                CloseResponse();
            }
            finally
            {
                _cancellation.Dispose();
                if (ReferenceEquals(Current.Value, this))
                    Current.Value = _previous;
            }
        }
    }
}
