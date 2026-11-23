using System.Text.Json;
using Limescape.Umbraco.AgentApi.Auth;
using Limescape.Umbraco.Compliance.PublicationGate;
using Limescape.Umbraco.Compliance.Retention;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;

namespace Limescape.Umbraco.AgentApi.Services;

/// <summary>
/// Review and retention management from the Navigator (nobody works in Umbraco). The same rules as
/// Umbraco Workflow and the Compliance Pack's backoffice screens, checked here server-side before anything is written,
/// with the person of the token as acting user, so every route writes the same approval records and audit entries.
/// </summary>
public sealed partial class LearningMaterialService
{
    private const int MaxCommentLength = 2000;
    private const int MaxFeedbackLength = 10000;
    private const int MaxHoldReasonLength = 500;
    private const int DefaultExpiringDays = 30;
    private const int MaxExpiringDays = 3650;
    private const int DefaultListTake = 100;
    private const int MaxListTake = 500;

    /// <summary>Retention rows read per page, and the most read for one list (far above a demo, a guard for production).</summary>
    private const int RetentionPageSize = 500;
    private const int MaxRetentionRows = 20000;

    // ---- review ----

    /// <inheritdoc />
    public AgentResult<IReadOnlyList<ReviewQueueItem>> ListReviewQueue(AgentCaller caller)
    {
        if (caller.User.Roles.Count == 0)
        {
            return NoRole<IReadOnlyList<ReviewQueueItem>>();
        }

        // Only reviewers decide; for every other role the queue is empty (not forbidden: there is simply nothing to review).
        if (!caller.User.IsInRole(AgentRoles.Reviewer))
        {
            return AgentResult.Ok<IReadOnlyList<ReviewQueueItem>>([]);
        }

        return AgentResult.Ok<IReadOnlyList<ReviewQueueItem>>(AllMaterials()
            .Where(content => ReviewStatuses.Contains(Status(content), StringComparer.Ordinal)
                && !IsArchived(content)
                && !Owners(content).Contains(caller.User.PersonId, StringComparer.Ordinal))
            .OrderBy(content => content.UpdateDate)
            .Select(content => new ReviewQueueItem(
                content.Key,
                content.GetValue<string>(Model.TitleProperty) ?? content.Name ?? string.Empty,
                content.ContentType.Alias,
                contentService.GetParent(content)?.Key,
                Status(content),
                Owners(content),
                VersionOf(content),
                DateTime.SpecifyKind(content.UpdateDate, DateTimeKind.Utc),
                LinkFor(content.Key),
                ReviewOf(content)))
            .ToList());
    }

    /// <inheritdoc />
    public AgentResult<ReviewDecision> ApproveMaterial(AgentCaller caller, Guid materialKey, ApproveMaterialRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Decide(caller, materialKey, request.Version, ApprovalDecisions.Approved, comment: Trimmed(request.Comment), feedback: null);
    }

    /// <inheritdoc />
    public AgentResult<ReviewDecision> RejectMaterial(AgentCaller caller, Guid materialKey, RejectMaterialRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Decide(caller, materialKey, request.Version, ApprovalDecisions.Rejected, comment: null, feedback: Trimmed(request.Feedback));
    }

    /// <summary>
    /// One decision under the content write lock: a delegated token, the reviewer role, visible (else not_found), not an
    /// owner (four eyes), submitted or in review, still at the confirmed version. The Compliance Pack then writes the
    /// approval record, its audit entry and, for a rejection, status rejected; an approval is published through the
    /// publication gate in the same scope, so a refused publish leaves no approval behind.
    /// </summary>
    private AgentResult<ReviewDecision> Decide(AgentCaller caller, Guid materialKey, string? version, string decision, string? comment, string? feedback)
    {
        if (WriteRefused(caller) is { } refused)
        {
            return refused.Cast<ReviewDecision>();
        }

        // The role first: it says nothing about the material, so a non-reviewer learns nothing from the answer.
        if (!caller.User.IsInRole(AgentRoles.Reviewer))
        {
            return AgentResult.Fail<ReviewDecision>(AgentErrors.Forbidden, "Only a reviewer can approve or reject a material.");
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            return Invalid<ReviewDecision>("'version' is required: send the version of the material the reviewer confirmed.");
        }

        if (comment is { Length: > MaxCommentLength })
        {
            return Invalid<ReviewDecision>($"'comment' is at most {MaxCommentLength} characters.");
        }

        if (decision == ApprovalDecisions.Rejected)
        {
            // A rejection goes back to the owner, who has to know what to change, as in the approval API.
            if (string.IsNullOrEmpty(feedback))
            {
                return Invalid<ReviewDecision>("'feedback' is required for a rejection: tell the owner what to change.");
            }

            if (feedback.Length > MaxFeedbackLength)
            {
                return Invalid<ReviewDecision>($"'feedback' is at most {MaxFeedbackLength} characters.");
            }
        }

        using var scope = scopeProvider.CreateCoreScope();
        scope.EagerWriteLock(Constants.Locks.ContentTree);

        var content = Material(materialKey);
        if (content is null || !CanSee(caller, content))
        {
            return NotFound<ReviewDecision>();
        }

        if (Owners(content).Contains(caller.User.PersonId, StringComparer.Ordinal))
        {
            return AgentResult.Fail<ReviewDecision>(AgentErrors.OwnMaterial, "You are an owner of this material; another reviewer has to decide on it (four eyes).");
        }

        var status = Status(content);
        if (!ReviewStatuses.Contains(status, StringComparer.Ordinal) || IsArchived(content))
        {
            return AgentResult.Fail<ReviewDecision>(AgentErrors.NotReviewable, $"The material has status '{status}'; only a submitted material can be approved or rejected.");
        }

        var current = VersionOf(content);
        if (!string.Equals(version.Trim(), current, StringComparison.Ordinal))
        {
            return AgentResult.Fail<ReviewDecision>(AgentErrors.VersionConflict,
                $"The material changed after version '{version.Trim()}'; the current version is '{current}'. Read it again and ask the reviewer to confirm the current version.");
        }

        var decided = approvalService.DecideAs(content, new ApprovalDecision(decision, Comment: comment, Findings: feedback), actingUserAccessor.Current);
        var failure = decided.Status switch
        {
            ApprovalOperationStatus.Success => null,
            ApprovalOperationStatus.OwnMaterial => AgentResult.Fail<ReviewDecision>(AgentErrors.OwnMaterial, "You are an owner of this material; another reviewer has to decide on it (four eyes)."),
            ApprovalOperationStatus.RejectionRequiresFeedback => Invalid<ReviewDecision>("'feedback' is required for a rejection: tell the owner what to change."),
            ApprovalOperationStatus.ActingUserRequired => AgentResult.Fail<ReviewDecision>(AgentErrors.Forbidden, "A decision needs the reviewer's own token."),
            _ => AgentResult.Fail<ReviewDecision>(AgentErrors.Cancelled, "The content layer refused to record the decision."),
        };
        if (failure is not null)
        {
            return failure;
        }

        var record = decided.Result!;
        if (decision == ApprovalDecisions.Approved)
        {
            // Umbraco Workflow publishes when the last reviewer approves; so does this route. The publication gate checks the
            // approval record just written for exactly this draft, and sets status published.
            var published = contentService.Publish(content, ["*"]);
            if (!published.Success)
            {
                return AgentResult.Fail<ReviewDecision>(AgentErrors.Cancelled,
                    CancelMessage(published.EventMessages, $"The content layer refused to publish the material ({published.Result}); nothing was recorded."));
            }
        }

        scope.Complete();

        var after = contentService.GetById(materialKey) ?? content;
        return AgentResult.Ok(new ReviewDecision(
            after.Key,
            record.Decision,
            Status(after),
            after.Published,
            record.ReviewerId,
            record.ReviewerName,
            record.Channel,
            record.Comment,
            record.Findings,
            record.VersionHash,
            DateTime.SpecifyKind(record.DecidedUtc, DateTimeKind.Utc)));
    }

    // ---- retention ----

    /// <inheritdoc />
    public AgentResult<RetentionOverview> ListRetention(AgentCaller caller, RetentionListRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (ManagerRefused<RetentionOverview>(caller, "see retention periods") is { } refused)
        {
            return refused;
        }

        var withinDays = request.WithinDays ?? DefaultExpiringDays;
        var skip = request.Skip ?? 0;
        var take = request.Take ?? DefaultListTake;
        var state = request.State?.Trim();
        if (withinDays is < 1 or > MaxExpiringDays)
        {
            return Invalid<RetentionOverview>($"'withinDays' is 1 to {MaxExpiringDays}.");
        }

        if (skip < 0 || take is < 1 or > MaxListTake)
        {
            return Invalid<RetentionOverview>($"'skip' is 0 or more and 'take' is 1 to {MaxListTake}.");
        }

        if (!string.IsNullOrEmpty(state) && !RetentionStates.Listed.Contains(state, StringComparer.Ordinal))
        {
            return Invalid<RetentionOverview>($"'state' is one of {string.Join(", ", RetentionStates.Listed)}, or empty for all.");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var classes = retentionService.GetClasses();
        var names = classes.ToDictionary(c => c.Key, c => c.Name);
        var items = RetentionRows(skipTake => retentionService.GetEnded(now, skipTake.Skip, skipTake.Take))
            .Concat(RetentionRows(skipTake => retentionService.GetRunning(now, skipTake.Skip, skipTake.Take)))
            .DistinctBy(info => info.ItemKey)
            .Select(info => (Info: info, Content: Material(info.ItemKey)))
            .Where(row => row.Content is { Trashed: false })
            .Select(row => ToRetention(row.Content!, row.Info, names, now, withinDays))
            .Where(item => string.IsNullOrEmpty(state) || item.State == state)
            .OrderBy(item => item.RetentionUntil)
            .ThenBy(item => item.MaterialKey)
            .ToList();

        return AgentResult.Ok(new RetentionOverview(
            now,
            withinDays,
            classes.Select(c => new RetentionClassInfo(c.Key, c.Name, c.Description, c.Duration.Years, c.Duration.Months, c.Duration.Days, Code(c.StartEvent), Code(c.EndAction))).ToList(),
            items.Count,
            items.Skip(skip).Take(take).ToList()));
    }

    /// <inheritdoc />
    public AgentResult<MaterialRetention> SetRetentionClass(AgentCaller caller, Guid materialKey, SetRetentionClassRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (WriteRefused(caller) is { } refused)
        {
            return refused.Cast<MaterialRetention>();
        }

        if (ManagerRefused<MaterialRetention>(caller, "assign a retention class") is { } notManager)
        {
            return notManager;
        }

        if (request.ClassKey == Guid.Empty)
        {
            return Invalid<MaterialRetention>("'classKey' is required: one of the classes from list_retention.");
        }

        using var scope = scopeProvider.CreateCoreScope();
        scope.EagerWriteLock(Constants.Locks.ContentTree);
        var confirmed = ManagedMaterial(caller, materialKey, request.Version);
        if (!confirmed.IsSuccess)
        {
            return confirmed.Cast<MaterialRetention>();
        }

        if (retentionService.GetClass(request.ClassKey) is null)
        {
            return Invalid<MaterialRetention>("Unknown retention class. Use list_retention for the available classes.");
        }

        // One class per material: the new assignment first, then the old ones go. Removing them never shortens a period
        // that runs already (no administrator confirmed it), so the longer end date stays, marked heldFromEarlier.
        var previous = retentionService.GetAssignments()
            .Where(a => a.Target == RetentionAssignmentTarget.Item && a.ItemKey == materialKey)
            .ToList();
        if (previous is not [var only] || only.ClassKey != request.ClassKey)
        {
            try
            {
                if (!previous.Any(a => a.ClassKey == request.ClassKey))
                {
                    retentionService.CreateAssignment(new RetentionAssignment(0, request.ClassKey, RetentionAssignmentTarget.Item) { ItemKey = materialKey });
                }

                var kept = false;
                foreach (var assignment in previous)
                {
                    if (assignment.ClassKey == request.ClassKey && !kept)
                    {
                        kept = true;
                        continue;
                    }

                    retentionService.DeleteAssignment(assignment.Id, confirmShortening: false);
                }
            }
            catch (RetentionValidationException ex)
            {
                return Invalid<MaterialRetention>(ex.Message);
            }
        }

        scope.Complete();
        return AgentResult.Ok(RetentionOf(confirmed.Value!));
    }

    /// <inheritdoc />
    public AgentResult<MaterialRetention> SetLegalHold(AgentCaller caller, Guid materialKey, LegalHoldRequest request) =>
        ChangeLegalHold(caller, materialKey, request, hold: true);

    /// <inheritdoc />
    public AgentResult<MaterialRetention> ClearLegalHold(AgentCaller caller, Guid materialKey, LegalHoldRequest request) =>
        ChangeLegalHold(caller, materialKey, request, hold: false);

    /// <summary>
    /// The Compliance Pack's legal hold, which the backoffice keeps for administrators; here for a functional manager with
    /// the person's own token. The same row and audit entry (field <c>legalHold</c>, old and new value, the reason); a
    /// repeated call changes nothing and writes no audit entry.
    /// </summary>
    private AgentResult<MaterialRetention> ChangeLegalHold(AgentCaller caller, Guid materialKey, LegalHoldRequest request, bool hold)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (WriteRefused(caller) is { } refused)
        {
            return refused.Cast<MaterialRetention>();
        }

        if (ManagerRefused<MaterialRetention>(caller, hold ? "set a legal hold" : "lift a legal hold") is { } notManager)
        {
            return notManager;
        }

        if (request.Reason is { Length: > MaxHoldReasonLength })
        {
            return Invalid<MaterialRetention>($"'reason' is at most {MaxHoldReasonLength} characters.");
        }

        using var scope = scopeProvider.CreateCoreScope();
        scope.EagerWriteLock(Constants.Locks.ContentTree);
        var confirmed = ManagedMaterial(caller, materialKey, request.Version);
        if (!confirmed.IsSuccess)
        {
            return confirmed.Cast<MaterialRetention>();
        }

        if (deletionBlockService.SetLegalHold(materialKey, hold, Trimmed(request.Reason)) is null)
        {
            return NotFound<MaterialRetention>();
        }

        scope.Complete();
        return AgentResult.Ok(RetentionOf(confirmed.Value!));
    }

    /// <summary>A functional manager; null when the caller is one.</summary>
    private static AgentResult<T>? ManagerRefused<T>(AgentCaller caller, string what) =>
        caller.User.IsInRole(AgentRoles.FunctionalManager)
            ? null
            : AgentResult.Fail<T>(AgentErrors.Forbidden, $"Only a functional manager can {what}.");

    /// <summary>A material the manager sees (else not_found), still at the version the person confirmed.</summary>
    private AgentResult<IContent> ManagedMaterial(AgentCaller caller, Guid materialKey, string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return Invalid<IContent>("'version' is required: send the version of the material the user confirmed.");
        }

        var content = Material(materialKey);
        if (content is null || !CanSee(caller, content))
        {
            return NotFound<IContent>();
        }

        var current = VersionOf(content);
        return string.Equals(version.Trim(), current, StringComparison.Ordinal)
            ? AgentResult.Ok(content)
            : AgentResult.Fail<IContent>(AgentErrors.VersionConflict,
                $"The material changed after version '{version.Trim()}'; the current version is '{current}'. Read it again and ask the user to confirm the current version.");
    }

    private MaterialRetention RetentionOf(IContent content)
    {
        var info = retentionService.GetItem(content.Key);
        var names = retentionService.GetClasses().ToDictionary(c => c.Key, c => c.Name);
        return ToRetention(content, info, names, timeProvider.GetUtcNow().UtcDateTime, DefaultExpiringDays);
    }

    private MaterialRetention ToRetention(IContent content, ItemRetentionInfo? info, IReadOnlyDictionary<Guid, string> classNames, DateTime now, int withinDays)
    {
        var until = info?.RetentionUntil is { } u ? DateTime.SpecifyKind(u, DateTimeKind.Utc) : (DateTime?)null;
        var state = info is null || info.Status == RetentionStatus.None ? RetentionStates.None
            : info.OpenEnded || until is null ? RetentionStates.Pending
            : until <= now ? RetentionStates.Ended
            : until <= now.AddDays(withinDays) ? RetentionStates.Expiring
            : RetentionStates.Running;

        return new MaterialRetention
        {
            MaterialKey = content.Key,
            Title = content.GetValue<string>(Model.TitleProperty) ?? content.Name ?? string.Empty,
            Status = Status(content),
            Published = content.Published,
            Version = VersionOf(content),
            State = state,
            ClassKey = info?.GoverningClassKey,
            ClassName = info?.GoverningClassKey is { } key ? classNames.GetValueOrDefault(key) : null,
            StartDate = info?.StartDate is { } start ? DateTime.SpecifyKind(start, DateTimeKind.Utc) : null,
            RetentionUntil = until,
            EndAction = info?.EndAction is { } action ? Code(action) : null,
            HeldFromEarlier = info?.HeldFromEarlier ?? false,
            LegalHold = info?.LegalHold ?? false,
            DetailLink = LinkFor(content.Key),
        };
    }

    /// <summary>All rows of a paged retention query, up to <see cref="MaxRetentionRows"/>.</summary>
    private static IEnumerable<ItemRetentionInfo> RetentionRows(Func<(int Skip, int Take), IReadOnlyList<ItemRetentionInfo>> page)
    {
        for (var skip = 0; skip < MaxRetentionRows; skip += RetentionPageSize)
        {
            var rows = page((skip, RetentionPageSize));
            foreach (var row in rows)
            {
                yield return row;
            }

            if (rows.Count < RetentionPageSize)
            {
                yield break;
            }
        }
    }

    /// <summary>Every material that is not in the recycle bin, of every material type.</summary>
    private IEnumerable<IContent> AllMaterials()
    {
        const int pageSize = 500;
        foreach (var type in MaterialTypes())
        {
            long page = 0, total;
            do
            {
                foreach (var content in contentService.GetPagedOfType(type.Id, page, pageSize, out total, filter: null!).Where(c => !c.Trashed))
                {
                    yield return content;
                }

                page++;
            }
            while (page * pageSize < total);
        }
    }

    /// <summary>An enum value as its camelCase code, as the Compliance Pack's retention API writes it.</summary>
    private static string Code<TEnum>(TEnum value)
        where TEnum : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
