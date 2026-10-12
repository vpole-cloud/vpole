namespace Pole.Features.Realtime.Application;

/// <summary>Signals a missing, expired or explicitly revoked realtime session at a storage boundary.</summary>
public sealed class RealtimeSessionUnavailableException : Exception
{
    public RealtimeSessionUnavailableException() : base("The realtime session is unavailable.") { }
}
