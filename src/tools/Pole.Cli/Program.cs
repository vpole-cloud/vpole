using System.Diagnostics;
using Pole.Cli;
using Pole.Cli.Authentication;

using var httpClient = OidcAuthenticator.CreateHttpClient();
var browser = new SystemBrowser();
var authenticator = new OidcAuthenticator(httpClient, browser);
var runner = new CliCommandRunner(httpClient, new CliTokenStore(), authenticator);
return await runner.RunAsync(args);

namespace Pole.Cli
{
    internal sealed class SystemBrowser : IOidcBrowser
    {
        public void Open(Uri uri)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true,
            };
            Process.Start(startInfo);
        }
    }
}
