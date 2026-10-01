namespace Pole.Connector;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using var lifetime = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            lifetime.Cancel();
        };

        try
        {
            switch (args)
            {
                case ["enroll", "--origin", var origin, "--target", var target,
                    "--token-file", var tokenFile, "--state-dir", var stateDir]:
                    await ConnectorEnrollment.EnrollAsync(
                        BrokerOrigin.Parse(origin), target, tokenFile, stateDir, lifetime.Token);
                    return 0;
                case ["connector", "--origin", var origin, "--state-dir", var stateDir]:
                    await ConnectorSession.RunAsync(
                        BrokerOrigin.Parse(origin), stateDir, lifetime.Token);
                    return 0;
                case ["worker"]:
                    await WorkerSession.RunAsync(lifetime.Token);
                    return 0;
                default:
                    Console.Error.WriteLine(
                        "Usage: Pole.Connector enroll --origin https://host:port --target ID --token-file FILE --state-dir DIR | connector --origin https://host:port --state-dir DIR | worker");
                    return 2;
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception)
        {
            // Paths, tokens, certificates, HTTP bodies and provider errors are not diagnostics.
            Console.Error.WriteLine("Connector operation failed. Check the local configuration and broker status.");
            return 1;
        }
    }
}
