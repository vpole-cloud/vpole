namespace Pole.Observability;

public sealed class TraceContextPropagationHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!request.Headers.Contains(TraceHeaders.CorrelationId))
        {
            request.Headers.TryAddWithoutValidation(TraceHeaders.CorrelationId, TraceContext.CurrentCorrelationId);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
