using Limescape.Umbraco.AgentApi.Auth;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Services;

namespace Limescape.Umbraco.AgentApi.Services;

public sealed record DisplayField(string Alias, string Label, string? Value, bool TranslateValue);
public sealed record MetadataGroup(string Label, IReadOnlyList<DisplayField> Fields);
public sealed record MaterialVersion(int Id, DateTime UpdatedAt);
public sealed record MaterialRelation(string Type, Guid Key, string Title, bool IsParent);
public sealed record MaterialPage(MaterialDetail Material, IReadOnlyList<MetadataGroup> Groups,
    IReadOnlyList<MaterialVersion> Versions, IReadOnlyList<MaterialRelation> Relations)
{
    /// <summary>Anonymous count of related materials the viewer may not see, per relation type alias.</summary>
    public IReadOnlyDictionary<string, int> HiddenRelationCount => Material.HiddenRelationCount;
}

/// <summary>Presentation data stays in the package; related material visibility is checked individually.</summary>
public sealed class MaterialPageService(ILearningMaterialService materials, IContentService content, IContentTypeService types)
{
    public AgentResult<MaterialPage> GetPage(AgentCaller caller, Guid key)
    {
        var result = materials.GetMaterial(caller, key);
        if (!result.IsSuccess) { return result.Cast<MaterialPage>(); }
        var material = result.Value!;
        var item = content.GetById(key)!;
        var type = types.Get(item.ContentType.Key)!;
        var grouped = type.CompositionPropertyGroups.OrderBy(g => g.SortOrder).Select(group =>
            new MetadataGroup(group.Name ?? group.Alias, (group.PropertyTypes?.AsEnumerable() ?? []).OrderBy(p => p.SortOrder)
                .Where(p => material.Fields.ContainsKey(p.Alias))
                .Select(p => new DisplayField(p.Alias, p.Name ?? p.Alias, material.Fields[p.Alias], p.PropertyEditorAlias == Constants.PropertyEditors.Aliases.DropDownListFlexible)).ToList())).ToList();
        var included = grouped.SelectMany(g => g.Fields).Select(f => f.Alias).ToHashSet(StringComparer.Ordinal);
        var ungrouped = type.CompositionPropertyTypes.Where(p => material.Fields.ContainsKey(p.Alias) && !included.Contains(p.Alias))
            .Select(p => new DisplayField(p.Alias, p.Name ?? p.Alias, material.Fields[p.Alias], p.PropertyEditorAlias == Constants.PropertyEditors.Aliases.DropDownListFlexible)).ToList();
        if (ungrouped.Count > 0) { grouped.Add(new MetadataGroup("metadata", ungrouped)); }
        // MaterialDetail already holds the related materials this viewer may see, checked one by one.
        var visibleRelations = material.Relations.Select(r => new MaterialRelation(r.Type, r.MaterialKey, r.Title, r.Inverse)).ToList();
        // History contains only version identifiers and dates, never older field values that may have different visibility.
        var versions = content.GetVersionsSlim(item.Id, 0, int.MaxValue).Select(v => new MaterialVersion(v.VersionId, v.UpdateDate)).ToList();
        return AgentResult.Ok(new MaterialPage(material, grouped, versions, visibleRelations));
    }
}
