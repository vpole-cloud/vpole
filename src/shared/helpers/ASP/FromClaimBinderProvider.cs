using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Pole.ASP.Helpers;

public class FromClaimBinderProvider : IModelBinderProvider
{
    public IModelBinder? GetBinder(ModelBinderProviderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Check if this is a property that could have our attribute
        if (context.Metadata.ModelType == typeof(string) && context.Metadata.ContainerType != null &&
            !string.IsNullOrEmpty(context.Metadata.PropertyName))
        {
            // Get the property
            var propertyInfo = context.Metadata.ContainerType.GetProperty(context.Metadata.PropertyName);
            if (propertyInfo != null)
            {
                // Check if it has our attribute
                var attributes = propertyInfo.GetCustomAttributes(true);
                var hasFromClaimAttribute = attributes.Any(a => a.GetType() == typeof(FromClaimAttribute));

                if (hasFromClaimAttribute)
                    return new FromClaimBinder();
            }
        }

        return null;
    }
}
