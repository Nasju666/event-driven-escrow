using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace EscrowMarketplace.Api.Authentication;

public class ConfigureJwtBearerOptions : IPostConfigureOptions<JwtBearerOptions>
{
    public void PostConfigure(string? name, JwtBearerOptions options)
    {
        var originalOnMessageReceived = options.Events?.OnMessageReceived;

        options.Events ??= new JwtBearerEvents();

        options.Events.OnMessageReceived = async context =>
        {
            if (originalOnMessageReceived != null)
            {
                await originalOnMessageReceived(context);
            }

            var accessToken = context.Request.Query["access_token"].FirstOrDefault();
            var path = context.HttpContext.Request.Path;

            // Intercept query-string access token exclusively for SignalR Hub endpoints
            if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs/marketplace"))
            {
                context.Token = accessToken;
            }
        };
    }
}
