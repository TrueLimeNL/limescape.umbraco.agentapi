namespace Limescape.Umbraco.AgentApi.Services;

/// <summary>A submitted material a reviewer may decide on: what the review queue shows and the version to confirm.</summary>
/// <param name="Key">Material key.</param>
/// <param name="Title">Title of the draft under review.</param>
/// <param name="MaterialType">Material type alias.</param>
/// <param name="CollectionKey">Collection the material is in.</param>
/// <param name="Status"><c>submitted</c> or <c>inReview</c>.</param>
/// <param name="Owners">Person keys of the owners (never the reviewer).</param>
/// <param name="Version">Version token of the draft; send it unchanged with approve_material or reject_material.</param>
/// <param name="UpdatedUtc">Last change of the draft (UTC), usually the moment it was submitted.</param>
/// <param name="DetailLink">Detail page under the Navigator domain, when configured.</param>
/// <param name="Review">The latest earlier decision, for example the feedback of a previous rejection; null when none.</param>
public sealed record ReviewQueueItem(
    Guid Key,
    string Title,
    string MaterialType,
    Guid? CollectionKey,
    string Status,
    IReadOnlyList<string> Owners,
    string Version,
    DateTime UpdatedUtc,
    string? DetailLink,
    ReviewInfo? Review);

/// <summary>A confirmed approval of a submitted material.</summary>
public sealed record ApproveMaterialRequest
{
    /// <summary><see cref="ReviewQueueItem.Version"/> (or <see cref="MaterialDetail.Version"/>) the reviewer confirmed. Required.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>Optional comment of the reviewer, at most 2,000 characters.</summary>
    public string? Comment { get; init; }
}

/// <summary>A confirmed rejection of a submitted material, with the feedback the owner needs.</summary>
public sealed record RejectMaterialRequest
{
    /// <summary><see cref="ReviewQueueItem.Version"/> (or <see cref="MaterialDetail.Version"/>) the reviewer confirmed. Required.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>What the owner has to change. Required, at most 10,000 characters; stored as the findings of the decision.</summary>
    public string? Feedback { get; init; }
}

/// <summary>
/// Outcome of approve_material or reject_material: the recorded decision and what it did. A reviewer does not see the
/// material any more after a rejection, so this is not a <see cref="MaterialDetail"/>.
/// </summary>
/// <param name="MaterialKey">Material key.</param>
/// <param name="Decision"><c>approved</c> or <c>rejected</c>.</param>
/// <param name="Status">Lifecycle status after the decision: <c>published</c> after an approval, <c>rejected</c> after a rejection.</param>
/// <param name="Published">True when the material is published after the decision.</param>
/// <param name="ReviewerId">Person key of the reviewer, from the token.</param>
/// <param name="ReviewerName">Display name of the reviewer.</param>
/// <param name="Channel"><c>app</c> (REST) or <c>assistant</c> (MCP).</param>
/// <param name="Comment">Comment of an approval.</param>
/// <param name="Feedback">Feedback of a rejection.</param>
/// <param name="VersionHash">Fingerprint of the reviewed draft, as in the approval record of the publication gate.</param>
/// <param name="DecidedUtc">Moment of the decision (UTC).</param>
public sealed record ReviewDecision(
    Guid MaterialKey,
    string Decision,
    string Status,
    bool Published,
    string ReviewerId,
    string? ReviewerName,
    string? Channel,
    string? Comment,
    string? Feedback,
    string VersionHash,
    DateTime DecidedUtc);

/// <summary>Values of <see cref="MaterialRetention.State"/>.</summary>
public static class RetentionStates
{
    /// <summary>No retention class applies.</summary>
    public const string None = "none";

    /// <summary>A class applies, but its start event (for example archiving) has not happened: no end date yet.</summary>
    public const string Pending = "pending";

    /// <summary>The period runs and ends after the window of <see cref="RetentionListRequest.WithinDays"/>.</summary>
    public const string Running = "running";

    /// <summary>The period runs and ends within the window of <see cref="RetentionListRequest.WithinDays"/>.</summary>
    public const string Expiring = "expiring";

    /// <summary>The period has ended; deletion is allowed unless a legal hold applies.</summary>
    public const string Ended = "ended";

    /// <summary>States list_retention can filter on.</summary>
    public static readonly IReadOnlyList<string> Listed = [Running, Expiring, Ended];
}

/// <summary>Filter and paging of list_retention.</summary>
public sealed record RetentionListRequest
{
    /// <summary>Window in days for <see cref="RetentionStates.Expiring"/>: 1 to 3,650, default 30.</summary>
    public int? WithinDays { get; init; }

    /// <summary>Only items in this state: <c>running</c>, <c>expiring</c> or <c>ended</c>; null for all three.</summary>
    public string? State { get; init; }

    /// <summary>Items to skip, default 0.</summary>
    public int? Skip { get; init; }

    /// <summary>Items to return: 1 to 500, default 100.</summary>
    public int? Take { get; init; }
}

/// <summary>A retention class a functional manager can assign with set_retention_class.</summary>
/// <param name="Key">Class key.</param>
/// <param name="Name">Name.</param>
/// <param name="Description">Description, if any.</param>
/// <param name="Years">Duration: years.</param>
/// <param name="Months">Duration: months.</param>
/// <param name="Days">Duration: days.</param>
/// <param name="StartEvent"><c>archived</c>, <c>published</c>, <c>lastModified</c> or <c>dateProperty</c>.</param>
/// <param name="EndAction"><c>allowDeletion</c> or <c>notify</c>.</param>
public sealed record RetentionClassInfo(Guid Key, string Name, string? Description, int Years, int Months, int Days, string StartEvent, string EndAction);

/// <summary>Retention of one material: class, period, legal hold and the version to confirm a change with.</summary>
public sealed record MaterialRetention
{
    public Guid MaterialKey { get; init; }

    public string Title { get; init; } = string.Empty;

    /// <summary>Lifecycle status of the material, for example <c>archived</c>.</summary>
    public string Status { get; init; } = string.Empty;

    public bool Published { get; init; }

    /// <summary>Version token of the material; send it unchanged with set_retention_class, set_legal_hold or clear_legal_hold.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>See <see cref="RetentionStates"/>.</summary>
    public string State { get; init; } = RetentionStates.None;

    /// <summary>The class that governs the period; null when none applies.</summary>
    public Guid? ClassKey { get; init; }

    public string? ClassName { get; init; }

    /// <summary>Start of the period (UTC), for example the archive date; null while it has not started.</summary>
    public DateTime? StartDate { get; init; }

    /// <summary>End of the period (UTC); null while it has not started or when no class applies.</summary>
    public DateTime? RetentionUntil { get; init; }

    /// <summary><c>allowDeletion</c> or <c>notify</c>; what happens at the end of the period.</summary>
    public string? EndAction { get; init; }

    /// <summary>True when a later, shorter period was not confirmed by an administrator, so the earlier, longer end date is kept.</summary>
    public bool HeldFromEarlier { get; init; }

    /// <summary>Legal hold: blocks deletion whatever the period says.</summary>
    public bool LegalHold { get; init; }

    public string? DetailLink { get; init; }
}

/// <summary>Result of list_retention.</summary>
/// <param name="AsOfUtc">Moment the states were computed.</param>
/// <param name="WithinDays">Window used for <see cref="RetentionStates.Expiring"/>.</param>
/// <param name="Classes">All retention classes, by name.</param>
/// <param name="Total">Number of items that match the filter, before paging.</param>
/// <param name="Items">The page of items, earliest end date first.</param>
public sealed record RetentionOverview(DateTime AsOfUtc, int WithinDays, IReadOnlyList<RetentionClassInfo> Classes, int Total, IReadOnlyList<MaterialRetention> Items);

/// <summary>A confirmed assignment of a retention class to one material.</summary>
public sealed record SetRetentionClassRequest
{
    /// <summary><see cref="MaterialRetention.Version"/> (or <see cref="MaterialDetail.Version"/>) the person confirmed. Required.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>Key of the class, from list_retention. Required.</summary>
    public Guid ClassKey { get; init; }
}

/// <summary>A confirmed legal hold change on one material.</summary>
public sealed record LegalHoldRequest
{
    /// <summary><see cref="MaterialRetention.Version"/> (or <see cref="MaterialDetail.Version"/>) the person confirmed. Required.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>Optional reason, for example a case number; at most 500 characters, written to the audit log.</summary>
    public string? Reason { get; init; }
}
