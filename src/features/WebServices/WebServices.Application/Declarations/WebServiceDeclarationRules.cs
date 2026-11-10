namespace Pole.Features.WebServices.Application.Declarations;

internal static class WebServiceDeclarationRules
{
    public const string PublicExposure = "Public";
    public const string PrivateExposure = "Private";
    public const int DefaultReplicas = 1;
    public const int MaximumNameLength = 30;
    public const int MaximumImageComponentLength = 255;
    public const int MaximumImageTagLength = 100;
    public const int MaximumReplicas = 1000;
    public const int MaximumTagLength = 30;
    public const int MaximumPorts = 32;
    public const int MinimumPort = 1;
    public const int MaximumPort = 65535;
    public const int MaximumPortTextLength = 10;
    public const int MaximumPortNameLength = 15;
    public const string TcpProtocol = "TCP";
    public const string UdpProtocol = "UDP";
    public const int MaximumEnvironmentEntries = 64;
    public const int MaximumEnvironmentNameLength = 128;
    public const int MaximumProbePathLength = 200;
    public const int MaximumProbeInitialDelaySeconds = 300;
    public const int MaximumProbePeriodSeconds = 120;
    public const int MaximumProbeTimeoutSeconds = 60;
    public const int MaximumProbeThreshold = 10;
    public const int RequiredLivenessSuccessThreshold = 1;
}
