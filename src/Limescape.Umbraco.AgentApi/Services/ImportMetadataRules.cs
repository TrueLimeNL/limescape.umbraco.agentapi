using System.Globalization;

namespace Limescape.Umbraco.AgentApi.Services;

/// <summary>Optional import facts, separate from citation and the system creation date.</summary>
public static class ImportMetadataRules
{
    public const string OriginalDate = "originalDate";
    public const string Provenance = "provenance";
    public static bool IsImportField(string alias) => alias is OriginalDate or Provenance;

    /// <summary>Shared validation with stable error-code prefixes for both API transports and content notifications.</summary>
    public static string? Validate(string? originalDate, string? provenance, TimeProvider clock)
    {
        if (!string.IsNullOrWhiteSpace(originalDate))
        {
            if (!DateOnly.TryParseExact(originalDate.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                return "original_date_invalid: Use an ISO date (yyyy-MM-dd).";
            }
            if (date > DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime))
            {
                return "original_date_future: The original date cannot be in the future.";
            }
        }
        return provenance?.Trim().Length > 500 ? "provenance_too_long: Provenance is at most 500 characters." : null;
    }
}
