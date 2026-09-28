using Pole.Workloads;

namespace Pole.Features.WebServices.Application.Common;

internal static class WebServicePublicIngressPort
{
    public static bool TryResolve(
        string? protocol,
        string? targetPort,
        out NetworkPort port)
    {
        port = default;
        return string.Equals(protocol, "TCP", StringComparison.OrdinalIgnoreCase) &&
               NetworkPort.TryParse(targetPort, out port);
    }
}
