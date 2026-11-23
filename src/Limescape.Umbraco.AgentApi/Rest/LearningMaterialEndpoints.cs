using Limescape.Umbraco.AgentApi.Auth;
using Limescape.Umbraco.AgentApi.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Limescape.Umbraco.AgentApi.Rest;

/// <summary>
/// REST endpoints over the same <see cref="ILearningMaterialService"/> as the MCP tools, for the guided
/// screens of the Navigator app. Same token, same rights, same audit; the channel is <see cref="AgentChannels.App"/>.
/// </summary>
public static class LearningMaterialEndpoints
{
    /// <summary>Maps the endpoints under <paramref name="basePath"/>; all require a user token.</summary>
    public static IEndpointConventionBuilder MapLearningMaterialEndpoints(this IEndpointRouteBuilder endpoints, string basePath)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup(basePath).RequireAuthorization(UserTokenAuthentication.Policy);

        group.MapGet("/collections", (ILearningMaterialService service, IAgentCallerAccessor callers) =>
            Run(callers, service.ListCollections, Results.Ok));

        // ?materialType=<alias> answers for one type; an unknown alias is a 404.
        group.MapGet("/metadata-context", (string? materialType, ILearningMaterialService service, IAgentCallerAccessor callers) =>
            Run(callers, caller => service.GetMetadataContext(caller, materialType), Results.Ok));

        group.MapPost("/", (CreateMaterialRequest request, ILearningMaterialService service, IAgentCallerAccessor callers) =>
            Run(callers, caller => service.CreateMaterial(caller, request), material => Results.Created($"{basePath}/{material.Key:D}", material)));

        group.MapGet("/{key:guid}", (Guid key, ILearningMaterialService service, IAgentCallerAccessor callers) =>
            Run(callers, caller => service.GetMaterial(caller, key), Results.Ok));

        group.MapPut("/{key:guid}", (Guid key, UpdateMaterialRequest request, ILearningMaterialService service, IAgentCallerAccessor callers) =>
            Run(callers, caller => service.UpdateMaterial(caller, key, request), Results.Ok));

        group.MapPost("/{key:guid}/submit", (Guid key, SubmitMaterialRequest request, ILearningMaterialService service, IAgentCallerAccessor callers) =>
            Run(callers, caller => service.SubmitMaterial(caller, key, request), Results.Ok));

        group.MapPut("/{key:guid}/owners", (Guid key, SetOwnersRequest request, ILearningMaterialService service, IAgentCallerAccessor callers) =>
            Run(callers, caller => service.SetOwners(caller, key, request), Results.Ok));

        group.MapPost("/{key:guid}/archive", (Guid key, ArchiveMaterialRequest request, ILearningMaterialService service, IAgentCallerAccessor callers) =>
            Run(callers, caller => service.ArchiveMaterial(caller, key, request), Results.Ok));

        group.MapPost("/{key:guid}/elements", (Guid key, ElementUpload[] elements, ILearningMaterialService service, IAgentCallerAccessor callers) =>
            Run(callers, caller => service.AddElements(caller, key, elements), Results.Ok));

        // The version token of the material as last read is a query parameter: a DELETE has no body.
        group.MapDelete("/{key:guid}/elements/{elementKey:guid}", (Guid key, Guid elementKey, string? version, ILearningMaterialService service, IAgentCallerAccessor callers) =>
            Run(callers, caller => service.RemoveElement(caller, key, elementKey, version), Results.Ok));

        // Files above the inline limit: the platform's signed evidence of a verified upload session, no file bytes.
        group.MapPost("/{key:guid}/verified-uploads", (Guid key, UploadSessionRegistration session, ILearningMaterialService service, IAgentCallerAccessor callers) =>
            Run(callers, caller => service.AddElementFromSession(caller, key, session), Results.Ok));

        group.MapGet("/{key:guid}/detail-link", (Guid key, ILearningMaterialService service, IAgentCallerAccessor callers) =>
            Run(callers, caller => service.GetDetailLink(caller, key), Results.Ok));

        group.MapGet("/groups", (ILearningMaterialService service, IAgentCallerAccessor callers) =>
            Run(callers, service.ListGroups, Results.Ok));

        group.MapPut("/{key:guid}/groups/{groupKey:guid}", (Guid key, Guid groupKey, ILearningMaterialService service, IAgentCallerAccessor callers) =>
            Run(callers, caller => service.AddToGroup(caller, key, groupKey), Results.Ok));

        group.MapDelete("/{key:guid}/groups/{groupKey:guid}", (Guid key, Guid groupKey, ILearningMaterialService service, IAgentCallerAccessor callers) =>
            Run(callers, caller => service.RemoveFromGroup(caller, key, groupKey), Results.Ok));

        // A link is identified by type, other material and direction; PUT creates it, DELETE removes it, both idempotent.
        group.MapPut("/{key:guid}/relations/{type}/{targetKey:guid}", (Guid key, string type, Guid targetKey, bool? inverse, ILearningMaterialService service, IAgentCallerAccessor callers) =>
            Run(callers, caller => service.AddRelation(caller, key, new RelationRequest { Type = type, TargetKey = targetKey, Inverse = inverse ?? false }), Results.Ok));

        group.MapDelete("/{key:guid}/relations/{type}/{targetKey:guid}", (Guid key, string type, Guid targetKey, bool? inverse, ILearningMaterialService service, IAgentCallerAccessor callers) =>
            Run(callers, caller => service.RemoveRelation(caller, key, new RelationRequest { Type = type, TargetKey = targetKey, Inverse = inverse ?? false }), Results.Ok));

        // Review and retention management from the Navigator: the same rules as Umbraco Workflow and the backoffice.
        group.MapGet("/review-queue", (ILearningMaterialService service, IAgentCallerAccessor callers) =>
            Run(callers, service.ListReviewQueue, Results.Ok));

        group.MapPost("/{key:guid}/approve", (Guid key, ApproveMaterialRequest request, ILearningMaterialService service, IAgentCallerAccessor callers) =>
            Run(callers, caller => service.ApproveMaterial(caller, key, request), Results.Ok));

        group.MapPost("/{key:guid}/reject", (Guid key, RejectMaterialRequest request, ILearningMaterialService service, IAgentCallerAccessor callers) =>
            Run(callers, caller => service.RejectMaterial(caller, key, request), Results.Ok));

        group.MapGet("/retention", (int? withinDays, string? state, int? skip, int? take, ILearningMaterialService service, IAgentCallerAccessor callers) =>
            Run(callers, caller => service.ListRetention(caller, new RetentionListRequest { WithinDays = withinDays, State = state, Skip = skip, Take = take }), Results.Ok));

        group.MapPut("/{key:guid}/retention-class", (Guid key, SetRetentionClassRequest request, ILearningMaterialService service, IAgentCallerAccessor callers) =>
            Run(callers, caller => service.SetRetentionClass(caller, key, request), Results.Ok));

        group.MapPost("/{key:guid}/legal-hold", (Guid key, LegalHoldRequest request, ILearningMaterialService service, IAgentCallerAccessor callers) =>
            Run(callers, caller => service.SetLegalHold(caller, key, request), Results.Ok));

        group.MapPost("/{key:guid}/legal-hold/clear", (Guid key, LegalHoldRequest request, ILearningMaterialService service, IAgentCallerAccessor callers) =>
            Run(callers, caller => service.ClearLegalHold(caller, key, request), Results.Ok));

        // Archive a metadata field and deprecate codes: a functional manager with the person's own token.
        group.MapPut("/metadata-fields/{alias}/state", (string alias, SetFieldStateRequest request, ILearningMaterialService service, IAgentCallerAccessor callers) =>
            Run(callers, caller => service.SetMetadataFieldState(caller, alias, request), Results.Ok));

        return group;
    }

    private static IResult Run<T>(IAgentCallerAccessor callers, Func<AgentCaller, AgentResult<T>> operation, Func<T, IResult> ok)
    {
        var caller = callers.Resolve(AgentChannels.App);
        var result = caller.IsSuccess ? operation(caller.Value!) : caller.Cast<T>();
        return result.IsSuccess ? ok(result.Value!) : Problem(result.Error!, result.Message, result.Details);
    }

    private static IResult Problem(string code, string? message, IReadOnlyDictionary<string, object?>? details = null) => Results.Problem(
        statusCode: code switch
        {
            AgentErrors.Unauthenticated => StatusCodes.Status401Unauthorized,
            "import_metadata_manager_required" or AgentErrors.Forbidden or AgentErrors.DelegatedTokenRequired or AgentErrors.ActingUserMismatch or AgentErrors.UploadSessionInvalid
                or AgentErrors.OwnMaterial => StatusCodes.Status403Forbidden,
            AgentErrors.NotFound => StatusCodes.Status404NotFound,
            AgentErrors.NotEditable or AgentErrors.NotArchivable or AgentErrors.NotReviewable or AgentErrors.VersionConflict or AgentErrors.Cancelled
                or AgentErrors.UploadSessionUsed => StatusCodes.Status409Conflict,
            AgentErrors.NotConfigured => StatusCodes.Status501NotImplemented,
            _ => StatusCodes.Status400BadRequest,
        },
        detail: message,
        extensions: new Dictionary<string, object?>(details ?? new Dictionary<string, object?>()) { ["code"] = code });
}
