using System.Globalization;
using Limescape.Umbraco.AgentApi.Auth;
using Limescape.Umbraco.Compliance.PublicationGate;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;

namespace Limescape.Umbraco.AgentApi.Services;

/// <summary>Protects import facts on every content save, including Management API and direct service calls.</summary>
public sealed class ImportMetadataGuardHandler(
    IGatedContentTypes gatedTypes,
    IContentService contentService,
    IHttpContextAccessor http,
    IBackOfficeSecurityAccessor backOffice,
    IOptions<AgentApiOptions> options,
    TimeProvider clock) : INotificationHandler<ContentSavingNotification>
{
    public void Handle(ContentSavingNotification notification)
    {
        foreach (var content in notification.SavedEntities.Where(c => !c.Blueprint && gatedTypes.IsGated(c)))
        {
            var date = content.GetValue(ImportMetadataRules.OriginalDate)?.ToString();
            var provenance = content.GetValue(ImportMetadataRules.Provenance)?.ToString();
            var error = ImportMetadataRules.Validate(date, provenance, clock);
            var stored = content.HasIdentity ? contentService.GetById(content.Key) : null;
            if (error is null && stored is not null && new[] { ImportMetadataRules.OriginalDate, ImportMetadataRules.Provenance }
                .Any(alias => !string.Equals(Text(content.GetValue(alias)), Text(stored.GetValue(alias)), StringComparison.Ordinal)) && !IsManager())
            {
                error = "import_metadata_manager_required: Only a functional manager may change import metadata.";
            }
            if (error is null) { continue; }
            var code = error.Split(':')[0];
            var nl = (backOffice.BackOfficeSecurity?.CurrentUser?.Language ?? CultureInfo.CurrentUICulture.Name).StartsWith("nl", StringComparison.OrdinalIgnoreCase);
            var message = nl ? code switch
            {
                "original_date_invalid" => "Vul de oorspronkelijke datum in als jjjj-mm-dd.",
                "original_date_future" => "De oorspronkelijke datum mag niet in de toekomst liggen.",
                "provenance_too_long" => "Herkomst mag maximaal 500 tekens bevatten.",
                _ => "Alleen een functioneel beheerder mag de importgegevens wijzigen."
            } : error;
            notification.CancelOperation(new EventMessage(code, message, EventMessageType.Error));
            return;
        }
    }

    private bool IsManager()
    {
        if (http.HttpContext?.User is { } principal && AgentUser.FromPrincipal(principal, options.Value.Authentication) is { } person)
        {
            return person.IsDelegated && person.IsInRole(AgentRoles.FunctionalManager);
        }
        // Backoffice access must also be mapped explicitly. An API account alone never represents a functional manager.
        var user = backOffice.BackOfficeSecurity?.CurrentUser;
        return user is not null && user.Kind != UserKind.Api && options.Value.Authentication.RoleMapping.TryGetValue(AgentRoles.FunctionalManager, out var mapped)
            && user.Groups.Any(g => mapped.Contains(g.Alias, StringComparer.Ordinal));
    }

    private static string? Text(object? value) => string.IsNullOrWhiteSpace(value?.ToString()) ? null : value.ToString()!.Trim();
}
