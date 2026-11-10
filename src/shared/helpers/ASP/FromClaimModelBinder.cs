using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Pole.ASP.Helpers;

public class FromClaimBinder : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        ArgumentNullException.ThrowIfNull(bindingContext);

        // Get the container type and property
        var containerType = bindingContext.ModelMetadata.ContainerType;
        var propertyName = bindingContext.ModelMetadata.PropertyName;

        if (containerType == null || string.IsNullOrEmpty(propertyName))
            return Task.CompletedTask;

        var propertyInfo = containerType.GetProperty(propertyName);
        if (propertyInfo == null)
            return Task.CompletedTask;

        var attributes = propertyInfo.GetCustomAttributes(true);
        var fromClaimAttribute
            = attributes.FirstOrDefault(a => a.GetType() == typeof(FromClaimAttribute)) as FromClaimAttribute;

        if (fromClaimAttribute == null)
            return Task.CompletedTask;

        // Get the user claims
        var user = bindingContext.HttpContext.User;
        if (user.Identity?.IsAuthenticated != true)
        {
            bindingContext.Result = ModelBindingResult.Failed();
            return Task.CompletedTask;
        }

        // Find the specific claim value
        var claimValue = user.Claims.FirstOrDefault(c => c.Type == fromClaimAttribute.ClaimType)?.Value;

        if (string.IsNullOrEmpty(claimValue))
        {
            bindingContext.Result = ModelBindingResult.Failed();
            return Task.CompletedTask;
        }

        // Set the result
        bindingContext.Result = ModelBindingResult.Success(claimValue);
        return Task.CompletedTask;
    }
}
