using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.DependencyInjection;

namespace Limescape.Umbraco.AgentApi.Mcp;

/// <summary>Registers the MCP services and endpoint through Umbraco's normal pipeline.</summary>
public sealed class McpServerComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddAgentApi(builder.Config);
        builder.AddNotificationHandler<ContentSavingNotification, Services.ImportMetadataGuardHandler>();
    }
}
