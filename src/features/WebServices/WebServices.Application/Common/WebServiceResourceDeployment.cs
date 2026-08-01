using Pole.Features.ResourceDeployments.Contracts;

namespace Pole.Features.WebServices.Application.Common;

public static class WebServiceResourceDeployment
{
    public static ResourceTypeId ResourceType { get; } =
        ResourceTypeId.Create("pole/web-service");
}
