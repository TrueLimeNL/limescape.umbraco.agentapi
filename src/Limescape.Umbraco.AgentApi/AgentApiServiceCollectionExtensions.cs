using Limescape.Umbraco.AgentApi.Auth;
using Limescape.Umbraco.AgentApi.Mcp;
using Limescape.Umbraco.AgentApi.Rest;
using Limescape.Umbraco.AgentApi.Services;
using Limescape.Umbraco.Compliance.Submission;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using Umbraco.Cms.Web.Common.ApplicationBuilder;

namespace Limescape.Umbraco.AgentApi;

public static class AgentApiServiceCollectionExtensions
{
    /// <summary>
    /// Registers the user-token authentication, the learning-material service, the MCP tools and the Umbraco endpoint
    /// filter that maps the MCP and REST endpoints when the feature is enabled.
    /// </summary>
    public static IServiceCollection AddAgentApi(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<AgentApiOptions>()
            .Bind(configuration.GetSection(AgentApiOptions.SectionName))
            .Validate(options => AgentApiOptions.IsValidPath(options.EndpointPath) && AgentApiOptions.IsValidPath(options.RestBasePath),
                "EndpointPath and RestBasePath must be absolute literal paths with non-empty alphanumeric, hyphen or underscore segments.")
            .Validate(options => AgentApiOptions.IsValidDetailLinkTemplate(options.DetailLinkTemplate),
                "DetailLinkTemplate must be an absolute https URL (http only for loopback) containing {key}.")
            .Validate(options => options.MaxInlineElementBytes > 0, "MaxInlineElementBytes must be positive.")
            .Validate(options => options.UploadSessions.SigningSecret.Length is 0 or >= UploadSessionOptions.MinSecretLength,
                "UploadSessions:SigningSecret must be empty or at least 32 characters.")
            .Validate(options => options.UploadSessions.MaxBytes > 0 && options.UploadSessions.MaxSignatureLifetimeSeconds > 0
                && options.UploadSessions.CategoryMaxBytes.Values.All(max => max > 0),
                "UploadSessions:MaxBytes, CategoryMaxBytes and MaxSignatureLifetimeSeconds must be positive.")
            .Validate(options => options.UploadSessions.PathPrefix.EndsWith('/') && UploadSessionSignature.IsValidPath(options.UploadSessions.PathPrefix + "x"),
                "UploadSessions:PathPrefix must be a relative folder of the media file system ending in /, for example materials/.")
            .Validate(options => options.DetailPage.DownloadLifetimeSeconds is > 0 and <= 600,
                "DownloadLifetimeSeconds must be between 1 and 600.")
            .Validate(options => options.UserToken.IsValid(),
                "UserToken:Audience must be set and every UserToken:TrustedIssuers entry needs an Issuer and at least one PEM public key (EC, or RSA of 2048 bits or more).")
            .Validate(options => string.IsNullOrEmpty(options.DetailPage.BlobServiceUri) ||
                (Uri.TryCreate(options.DetailPage.BlobServiceUri, UriKind.Absolute, out var uri) && uri.Scheme == "https" && string.IsNullOrEmpty(uri.Query)),
                "BlobServiceUri must be an HTTPS account URI without credentials.")
            .ValidateOnStart();

        services.AddHttpContextAccessor();
        services.AddUserTokenAuthentication();
        services.TryAddScoped<IAgentCallerAccessor, AgentCallerAccessor>();
        services.TryAddScoped<ILearningMaterialService, LearningMaterialService>();
        // The tenant profile's field labels name the empty fields of a refused submission, on every route.
        services.AddSingleton<IFieldLabelProvider, ProfileFieldLabelProvider>();
        services.AddDataProtection();
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddScoped<MaterialPageService>();
        services.TryAddScoped<SignedDownloadService>();
        services.TryAddScoped<ElementContentService>();

        // Tools are registered explicitly, never by assembly scanning.
        services.AddMcpServer()
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .WithTools<LearningMaterialTools>();

        services.Configure<UmbracoPipelineOptions>(options =>
            options.AddFilter(new UmbracoPipelineFilter("Limescape.AgentApi")
            {
                Endpoints = app =>
                {
                    var settings = app.ApplicationServices.GetRequiredService<IOptions<AgentApiOptions>>().Value;
                    if (settings.Enabled)
                    {
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapMcp(settings.EndpointPath).RequireAuthorization(UserTokenAuthentication.Policy);
                            endpoints.MapLearningMaterialEndpoints(settings.RestBasePath);
                            // Element bytes for the index sync: API user plus acting user, not the user-token group.
                            endpoints.MapElementContentEndpoint(settings.RestBasePath);
                        });
                    }
                }
            }));

        return services;
    }
}
