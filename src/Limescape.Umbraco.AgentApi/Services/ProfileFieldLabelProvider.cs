using Limescape.Umbraco.Compliance.Submission;
using Microsoft.Extensions.Options;

namespace Limescape.Umbraco.AgentApi.Services;

/// <summary>
/// The labels of the tenant profile (<see cref="ContentModelOptions.Fields"/>) for the submission guard, so the
/// refusal to submit names a field with the label a person reads ("Licentie") in every route: backoffice, Management API
/// and Agent API.
/// </summary>
public sealed class ProfileFieldLabelProvider(IOptionsMonitor<AgentApiOptions> options) : IFieldLabelProvider
{
    /// <inheritdoc />
    public IReadOnlyDictionary<string, string>? GetLabels(string propertyAlias)
    {
        ArgumentNullException.ThrowIfNull(propertyAlias);
        return options.CurrentValue.Content.Fields.TryGetValue(propertyAlias, out var field) && field.Labels.Count > 0
            ? new Dictionary<string, string>(field.Labels, StringComparer.OrdinalIgnoreCase)
            : null;
    }
}
