using System.Collections.ObjectModel;
using Mmd.Modern.Core;
namespace Mmd.Modern.Formats;

/// <summary>PMX rest-material flags for the selected SM3 frame path. No animation/morph/UV2/attachment evaluation.</summary>
public static class PmxOriginalFrameAdapter
{
    public static ReadOnlyCollection<OriginalFrameMaterialFlags> FromMaterials(PmxModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return Array.AsReadOnly(model.Materials.Select(m=>new OriginalFrameMaterialFlags(
            (m.DrawMode&PmxDrawModeFlags.CastSelfShadow)!=0&&m.EdgeColor.W>0&&m.Diffuse.W!=.98f,
            (m.DrawMode&PmxDrawModeFlags.ReceiveSelfShadow)!=0,
            (m.DrawMode&PmxDrawModeFlags.GroundShadow)!=0&&m.EdgeColor.W>0,
            ShadowDoubleSided:(m.DrawMode&PmxDrawModeFlags.DrawEdge)!=0)).ToArray());
    }
    public static OriginalFrameScene FromRestPose(PmxModel model,OriginalStyleScene surface,OriginalOutlineOptions outlineOptions,OriginalFrameOptions options)
        =>new(surface,PmxOriginalOutlineAdapter.FromRestPose(model),outlineOptions,options,FromMaterials(model));
    public static ReadOnlyCollection<OriginalFrameMaterialFlags> FromEvaluatedMaterials(PmxModel model,OriginalStyleScene surface,OriginalOutlineData outline)
    {
        ArgumentNullException.ThrowIfNull(model);ArgumentNullException.ThrowIfNull(surface);ArgumentNullException.ThrowIfNull(outline);
        if(model.Materials.Count!=surface.Snapshot.Materials.Count||model.Materials.Count!=outline.Materials.Count)throw new ArgumentException("Evaluated material counts must match PMX.");
        return Array.AsReadOnly(model.Materials.Select((m,i)=>new OriginalFrameMaterialFlags(
            (m.DrawMode&PmxDrawModeFlags.CastSelfShadow)!=0&&outline.Materials[i].Color.W>0&&surface.Snapshot.Materials[i].Diffuse.W!=.98f,
            (m.DrawMode&PmxDrawModeFlags.ReceiveSelfShadow)!=0,(m.DrawMode&PmxDrawModeFlags.GroundShadow)!=0&&outline.Materials[i].Color.W>0,
            ShadowDoubleSided:(m.DrawMode&PmxDrawModeFlags.DrawEdge)!=0)).ToArray());
    }
}
