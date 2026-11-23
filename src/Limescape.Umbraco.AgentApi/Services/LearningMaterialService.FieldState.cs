using System.Security.Cryptography;
using System.Text;
using Limescape.Umbraco.AgentApi.Auth;
using Limescape.Umbraco.Compliance.FieldState;

namespace Limescape.Umbraco.AgentApi.Services;

// Archived fields and deprecated codes: the state operation. The rule itself (no new value in an archived
// field, no new deprecated code) is in LearningMaterialService.cs (create and update) and in the Compliance Pack's save handler.
public sealed partial class LearningMaterialService
{
    /// <inheritdoc />
    public AgentResult<MetadataFieldState> SetMetadataFieldState(AgentCaller caller, string fieldAlias, SetFieldStateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (WriteRefused(caller) is { } refused)
        {
            return refused.Cast<MetadataFieldState>();
        }

        if (ManagerRefused<MetadataFieldState>(caller, "archive a metadata field or deprecate its values") is { } notManager)
        {
            return notManager;
        }

        var alias = fieldAlias?.Trim() ?? string.Empty;
        var types = MaterialTypes();
        var properties = types.SelectMany(t => t.CompositionPropertyTypes).Where(p => string.Equals(p.Alias, alias, StringComparison.Ordinal)).ToList();
        if (properties.Count == 0)
        {
            return Invalid<MetadataFieldState>($"Unknown field '{alias}'. Use propose_metadata_context for the fields.");
        }

        if (Target(alias) == FieldTargets.System || ProtectedFields.Contains(alias, StringComparer.Ordinal))
        {
            return Invalid<MetadataFieldState>($"Field '{alias}' is needed for every material and cannot be archived or have values deprecated.");
        }

        // Codes are matched to the codes of the field (case-insensitive) and stored as the field spells them.
        var allowed = properties.SelectMany(p => AllowedValues(p) ?? []).Distinct(StringComparer.Ordinal).ToList();
        var deprecated = new List<string>();
        foreach (var raw in request.DeprecatedValues ?? [])
        {
            var code = allowed.FirstOrDefault(c => string.Equals(c, raw?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (code is null)
            {
                return Invalid<MetadataFieldState>(allowed.Count == 0
                    ? $"Field '{alias}' has no codes to deprecate."
                    : $"'{raw}' is not a code of field '{alias}'. Codes: {string.Join(", ", allowed)}.");
            }

            if (code.Length + alias.Length > KeyValueFieldStateStore.MaxKeyPayload)
            {
                return Invalid<MetadataFieldState>($"Code '{code}' is too long to be deprecated.");
            }

            if (!deprecated.Contains(code, StringComparer.Ordinal))
            {
                deprecated.Add(code);
            }
        }

        using var scope = scopeProvider.CreateCoreScope();
        var before = fieldStates.Stored().GetValueOrDefault(alias) ?? FieldStateInfo.None;
        if (!string.IsNullOrWhiteSpace(request.Version) && !string.Equals(request.Version.Trim(), StateVersion(before), StringComparison.Ordinal))
        {
            return AgentResult.Fail<MetadataFieldState>(AgentErrors.VersionConflict,
                $"The state of field '{alias}' changed after version '{request.Version.Trim()}'. Read it again and ask the user to confirm the current state.");
        }

        var next = new FieldStateInfo(request.Archived, new HashSet<string>(deprecated, StringComparer.Ordinal));
        if (before.Archived != next.Archived || !before.DeprecatedValues.SetEquals(next.DeprecatedValues))
        {
            fieldStates.Apply(alias, next);
        }

        scope.Complete();

        var byGroup = types.Any(t => fieldStates.StateOf(t, alias, new Dictionary<string, FieldStateInfo>()).Archived);
        return AgentResult.Ok(new MetadataFieldState(alias, next.Archived || byGroup, byGroup, next.DeprecatedValues.Order(StringComparer.Ordinal).ToList(), StateVersion(next)));
    }

    /// <summary>Fields every material needs: the title, the kind and link, and who may read it.</summary>
    private string[] ProtectedFields => [Model.TitleProperty, Model.KindProperty, Model.LinkUrlProperty, Model.SharingLevelProperty, Model.SharingGroupsProperty];

    private static string StateVersion(FieldStateInfo state)
    {
        var text = (state.Archived ? "1" : "0") + "|" + string.Join('|', state.DeprecatedValues.Order(StringComparer.Ordinal));
        return System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
    }
}
