// PMX material/node mapping references nanoem/ext/physics_bullet.cc
// @30acffaa29f5d2eb9e997d69418f2e4b97b5894f (MIT); see LICENSE.Nanoem.txt.
using System.Numerics;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;

namespace Mmd.Modern.Application;

public sealed record PmxSoftBodyAdaptation(IReadOnlyList<SoftBodyDefinition> Definitions,
    IReadOnlyList<PhysicsBodyDefinition> Colliders, IReadOnlyList<Matrix4x4> RigidRestWorlds);

/// <summary>Material topology maps global PMX vertex IDs to explicit local Bullet nodes. Coordinate-identical
/// vertices are not welded. Rope node order is first appearance in the selected material's face stream.</summary>
public static class PmxSoftBodyAdapter
{
    public static PmxSoftBodyAdaptation Convert(PmxModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var result = new List<SoftBodyDefinition>();
        var indices = model.Faces.SelectMany(f => new[] { f.A, f.B, f.C }).ToArray();
        long total = model.Materials.Sum(m => (long)m.FaceVertexCount);
        if (total != indices.Length) throw new ArgumentException("PMX material ranges do not partition its triangle stream.", nameof(model));
        foreach (var soft in model.SoftBodies)
        {
            if (soft is null || soft.MaterialIndex < 0 || soft.MaterialIndex >= model.Materials.Count)
                throw new ArgumentException("Soft body requires a valid material.", nameof(model));
            int start = model.Materials.Take(soft.MaterialIndex).Sum(m => m.FaceVertexCount);
            int length = model.Materials[soft.MaterialIndex].FaceVertexCount;
            if (start < 0 || length < 0 || start > indices.Length - length || length % 3 != 0)
                throw new ArgumentException("Invalid soft-body material range.", nameof(model));
            var faces = indices.AsSpan(start, length).ToArray();
            var ids = faces.Distinct().ToArray();
            if (ids.Any(id => id < 0 || id >= model.Vertices.Count)) throw new ArgumentException("Invalid PMX vertex index.", nameof(model));
            var map = ids.Select((id, node) => (id, node)).ToDictionary(p => p.id, p => p.node);
            if (soft.Anchors.Any(a => a.RigidBodyIndex < 0 || a.RigidBodyIndex >= model.RigidBodies.Count))
                throw new ArgumentException("Soft anchor references an unknown rigid body.", nameof(model));
            var c = soft.Config; var cl = soft.ClusterConfig; var m = soft.MaterialConfig; var it = soft.Iterations;
            float Unit(float value) => float.IsFinite(value) ? Math.Clamp(value, 0, 1) : value;
            float Positive(float value) => float.IsFinite(value) ? Math.Max(value, 0) : value;
            var settings = new SoftBodySettings
            {
                Mass = soft.TotalMass, Margin = soft.CollisionMargin, Group = soft.Group, CollisionMask = soft.CollisionGroup,
                Flags = (int)soft.Flags, BendingDistance = soft.BLinkLength, ClusterCount = soft.ClusterCount, AeroModel = soft.AeroModel,
                Config = [c.Vcf, Unit(c.Dp), Positive(c.Dg), Positive(c.Lf), c.Pr, Positive(c.Vc), Unit(c.Df), Unit(c.Mt), Unit(c.Chr), Unit(c.Khr), Unit(c.Shr), Unit(c.Ahr)],
                ClusterConfig = [Unit(cl.SrhrCl), Unit(cl.SkhrCl), Unit(cl.SshrCl), Unit(cl.SrSpltCl), Unit(cl.SkSpltCl), Unit(cl.SsSpltCl)],
                Stiffness = [Unit(m.Lst), Unit(m.Ast), Unit(m.Vst)], Iterations = [it.V, it.P, it.D, it.C]
            };
            result.Add(new(soft.Name, (SoftBodyShape)soft.Type, ids, ids.Select(id => model.Vertices[id].Position),
                soft.Type == PmxSoftBodyType.TriMesh ? faces.Select(id => map[id]) : [], soft.PinVertexIndices,
                soft.Anchors.Select(a => new SoftBodyAnchor(a.RigidBodyIndex, a.VertexIndex, a.NearMode)), settings));
        }
        var rigid = PmxCharacterPhysicsAdapter.Convert(model).Definition;
        return new(Array.AsReadOnly(result.ToArray()), rigid.Bodies, rigid.BodyRestWorldTransforms);
    }
}
