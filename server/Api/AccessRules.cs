using System.Net;
using Wic64Server.Activity;

namespace Wic64Server.Api;

public static class AccessRules
{
    /// <summary>
    /// The web UI and its API can delete files, so they only answer this computer (unless AllowRemoteAdmin is set).
    /// Every other request comes from the C64: those update when the C64 was last seen.
    /// </summary>
    public static WebApplication UseAccessRules(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<ServerOptions>();
        var activity = app.Services.GetRequiredService<ActivityLog>();

        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            var isWebUi = path.StartsWithSegments("/api") || path.StartsWithSegments("/ui")
                          || path.Value is "/" or "/index.html" or "/favicon.svg";
            var remote = context.Connection.RemoteIpAddress;

            if (isWebUi && !options.AllowRemoteAdmin && (remote is null || !IPAddress.IsLoopback(remote)))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync(
                    "The web UI only works on the computer the server runs on (start the server with --AllowRemoteAdmin=true to change that).\n");
                return;
            }

            if (!isWebUi && !path.StartsWithSegments("/push"))
                activity.Seen(remote?.ToString());
            await next();
        });

        return app;
    }
}
