using Mmd.Modern.Core;

namespace Mmd.Modern.Formats;

/// <summary>PMX rest-pose outline fields, with original strict-minimum shared-vertex material selection at load.</summary>
public static class PmxOriginalOutlineAdapter
{
    public static OriginalOutlineData FromRestPose(PmxModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var indices=new uint[checked(model.Faces.Count*3)];
        for(int i=0;i<model.Faces.Count;i++)
        {
            var t=model.Faces[i];
            if(t.A<0||t.B<0||t.C<0) throw new ArgumentException("Negative PMX vertex index.");
            indices[i*3]=(uint)t.A; indices[i*3+1]=(uint)t.B; indices[i*3+2]=(uint)t.C;
        }
        int[] counts=model.Materials.Select(m=>m.FaceVertexCount).ToArray();
        float[] widths=model.Materials.Select(m=>m.EdgeSize).ToArray();
        var selected=OriginalOutlineMath.SelectVertexMaterialsAtLoad(model.Vertices.Count,indices,counts,widths);
        var vertices=new OriginalOutlineVertex[model.Vertices.Count];
        for(int i=0;i<vertices.Length;i++)
        {
            var v=model.Vertices[i];
            // PMX load enables every vertex. A zero EdgeScale is not the inward/disabled runtime mode.
            vertices[i]=new(v.Position,v.Normal,OriginalOutlineVertexMode.Expand,v.EdgeScale,selected[i]);
        }
        return new(vertices,model.Materials.Select(m=>new OriginalOutlineMaterial(m.EdgeSize,m.EdgeColor,(m.DrawMode&PmxDrawModeFlags.DrawEdge)!=0)));
    }
}
