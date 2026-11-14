using System.Collections.ObjectModel;
namespace Mmd.Modern.Core;

/// <summary>Format-neutral character frame. Optional passes share the surface's mesh indices and target.</summary>
public sealed class OriginalFrameScene
{
    public OriginalStyleScene Surface { get; }
    public OriginalOutlineData? Outline { get; }
    public OriginalOutlineOptions? OutlineOptions { get; }
    public OriginalFrameOptions? Options { get; }
    public SceneLightingSnapshot? Lighting { get; }
    public ViewportOverlaySnapshot? EditorOverlay {get;}
    public ReadOnlyCollection<OriginalFrameMaterialFlags> Materials { get; }
    /// <summary>Resolved render metadata only; the project persists the existing explicit model purpose.</summary>
    public bool UsesSceneLighting { get; }

    public OriginalFrameScene(OriginalStyleScene surface,OriginalOutlineData? outline=null,OriginalOutlineOptions? outlineOptions=null,
        OriginalFrameOptions? options=null,IEnumerable<OriginalFrameMaterialFlags>? materials=null,SceneLightingSnapshot? lighting=null,
        ViewportOverlaySnapshot? editorOverlay=null)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if(outline is not null && (outlineOptions is null || outline.Vertices.Count!=surface.Snapshot.VertexCount || outline.Materials.Count!=surface.Snapshot.Materials.Count))
            throw new ArgumentException("Outline requires explicit options and one vertex/material binding per surface vertex/material.");
        if(outlineOptions is not null) OriginalOutlineMath.ValidateOptions(outlineOptions);
        Surface=surface; Outline=outline; OutlineOptions=outlineOptions;
        Options=options;Lighting=lighting;EditorOverlay=editorOverlay;
        Materials=Array.AsReadOnly(materials?.ToArray()??Enumerable.Repeat(new OriginalFrameMaterialFlags(false,false,false),surface.Materials.Count).ToArray());
        if(Materials.Count!=surface.Materials.Count || Materials.Any(m=>m is null)) throw new ArgumentException("One non-null frame flag set per surface material is required.");
        UsesSceneLighting=Materials.Any(m=>m.UseSceneLighting);
        if(Materials.Any(m=>m.GiToonResponseOverride is {} response&&!Enum.IsDefined(response)))
            throw new ArgumentException("Unknown material GI toon response override.",nameof(materials));
        foreach(var flags in Materials)
        {
            if(!float.IsFinite(flags.OutlineWidthScale)||flags.OutlineWidthScale<0||flags.OutlineWidthScale>4)
                throw new ArgumentException("Outline width scale must be finite and between zero and four.",nameof(materials));
            if(flags.CharacterToonResponse is {} response) CharacterToonMath.Validate(response);
            if(flags.CharacterOutlineStyle is {} style) CharacterOutlineMath.Validate(style);
        }
        if(options is not null) OriginalFrameMath.Validate(options);
        if(options?.GroundShadow is not null && outline is null)
            throw new ArgumentException("Ground shadow requires edge geometry inputs even when outline drawing is ineligible.");
    }
}
