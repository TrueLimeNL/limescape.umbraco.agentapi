using Limescape.Umbraco.AgentApi.Services;
using Limescape.Umbraco.Compliance.ActingUser;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Web.Common.Authorization;

namespace Limescape.Umbraco.AgentApi.Rest;

/// <summary>
/// <c>GET {RestBasePath}/{key}/elements/{elementKey}/content</c>: the bytes of one element for a trusted
/// server-side integration such as the index sync of the platform. Outside the user-token group of
/// <see cref="LearningMaterialEndpoints"/>: the caller is the API user of the tenant with a backoffice (client credentials)
/// bearer, authorized by Umbraco's content-section policy, plus a validated <c>X-Limescape-Acting-User</c> token
/// (Compliance Pack). The bytes are streamed by the content layer; no blob URL or SAS is ever returned.
/// </summary>
public static class ElementContentEndpoint
{
    /// <summary>Route template below the REST base path.</summary>
    public const string Route = "/{key:guid}/elements/{elementKey:guid}/content";

    /// <summary>Response header with the stored SHA-256 of the element, when known.</summary>
    public const string Sha256Header = "X-Limescape-Element-Sha256";

    /// <summary>Maps the endpoint under <paramref name="basePath"/>.</summary>
    public static IEndpointConventionBuilder MapElementContentEndpoint(this IEndpointRouteBuilder endpoints, string basePath)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints.MapGet(basePath.TrimEnd('/') + Route, Handle)
            .RequireAuthorization(AuthorizationPolicies.SectionAccessContent);
    }

    // Services are explicit: a host without the Compliance Pack or the backoffice must not have them inferred as a body.
    private static IResult Handle(Guid key, Guid elementKey, HttpContext context, [FromServices] ElementContentService service,
        [FromServices] IActingUserAccessor actors, [FromServices] IBackOfficeSecurityAccessor backOffice)
    {
        context.Response.Headers.CacheControl = "no-store, private";
        context.Response.Headers.XContentTypeOptions = "nosniff";

        var token = context.Features.Get<ActingUserTokenFeature>()?.Token;
        var correlationId = actors.Current.CorrelationId;
        if (token is null)
        {
            return Refuse(StatusCodes.Status401Unauthorized, ElementContentService.ActingUserRequiredCode,
                "A valid acting-user token is required.");
        }

        if (backOffice.BackOfficeSecurity?.CurrentUser?.Kind != UserKind.Api)
        {
            return Refuse(StatusCodes.Status403Forbidden, ElementContentService.ApiUserRequiredCode,
                "Only an API user may read element content.");
        }

        var result = service.Open(key, elementKey);
        if (!result.IsSuccess)
        {
            return result.Error == ElementContentService.ConfidentialCode
                ? Refuse(StatusCodes.Status403Forbidden, result.Error, result.Message ?? string.Empty)
                : Refuse(StatusCodes.Status404NotFound, ElementContentService.NotFoundCode, result.Message ?? string.Empty);
        }

        var file = result.Value!;
        context.Response.Headers.ContentDisposition = SignedDownloadService.Disposition(file.FileName, inline: false);
        if (file.Sha256 is not null)
        {
            context.Response.Headers[Sha256Header] = file.Sha256;
        }

        return Results.Stream(file.Stream, file.ContentType);

        IResult Refuse(int status, string code, string detail)
        {
            service.LogRefused(status, code, key, elementKey, token?.Subject ?? "none", correlationId);
            return Results.Problem(statusCode: status, detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code });
        }
    }
}
