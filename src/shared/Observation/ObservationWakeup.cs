using System.Threading.Channels;

namespace Pole.Observation;

/// <summary>Coalesced local hints; persisted interests remain authoritative across replicas.</summary>
public class ObservationWakeup
{
    private readonly Channel<bool> channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });

    public void Notify() => channel.Writer.TryWrite(true);

    public async Task WaitAsync(TimeProvider clock, CancellationToken ct)
    {
        using var turn = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var read = channel.Reader.ReadAsync(turn.Token).AsTask();
        var timer = Task.Delay(ObservationPolicy.CoordinationInterval, clock, turn.Token);
        await Task.WhenAny(read, timer);
        await turn.CancelAsync();
        try
        {
            await Task.WhenAll(read, timer);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
    }
}
