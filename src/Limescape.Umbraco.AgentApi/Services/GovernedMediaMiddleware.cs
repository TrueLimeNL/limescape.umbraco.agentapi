using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Services;

namespace Limescape.Umbraco.AgentApi.Services;

/// <summary>Runs before static files and the Blob provider; governed media are served only by signed downloads.</summary>
public sealed class GovernedMediaMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IOptions<AgentApiOptions> options, IMediaService media)
    {
        if (context.Request.Path.StartsWithSegments("/media"))
        {
            var item = media.GetMediaByPath(context.Request.Path.Value!);
            // Unknown variants (including image transforms) fail closed, as do all governed element files.
            if (item is null || item.Trashed || item.ContentType.Alias == options.Value.Content.ElementMediaTypeAlias)
            {
                context.Response.StatusCode = 404;
                return;
            }
        }
        await next(context);
    }
}
