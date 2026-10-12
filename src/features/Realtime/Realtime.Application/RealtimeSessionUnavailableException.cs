namespace Pole.Features.Realtime.Application;

/// <summary>Adapts the framework's void ITicketStore renewal contract to a revoked-session outcome.</summary>
public sealed class RealtimeSessionUnavailableException : Exception
{
    public RealtimeSessionUnavailableException() : base("The realtime session is unavailable.") { }
}
