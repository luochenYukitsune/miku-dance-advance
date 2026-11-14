// Managed PMX 2.1 impulse sampler. Field layout follows Saba PMXFile.h
// @29b8efa8b31c8e746f9a88020fb0ad9dcdcf3332 (MIT; third_party/saba/LICENCE.txt).
using System.Collections.ObjectModel;
using System.Numerics;
using Mmd.Modern.Formats;

namespace Mmd.Modern.Application;

/// <summary>One weighted PMX impulse in the indicated rigid body's local or world space.
/// This value contains no solver state and does not apply itself to a backend.</summary>
public readonly record struct PmxRigidBodyImpulse(int RigidBodyIndex, bool Local,
    Vector3 TranslationVelocity, Vector3 RotationTorque);

/// <summary>Copies validated PMX impulse definitions and samples Group/Flip paths into immutable commands.
/// Applying them once per physics step is the responsibility of the physics integration.</summary>
public sealed class PmxImpulseMorphAdapter
{
    private const int ImpulseBudget = 1_000_000;
    private readonly PmxMorph.ImpulseOffset[][] definitions;
    private readonly PmxMorphWeights groupWeights;
    private readonly bool[] supported;
    public IReadOnlyDictionary<string, int> SupportedMorphIndices { get; }
    public IReadOnlyList<string> Diagnostics { get; }

    public PmxImpulseMorphAdapter(PmxModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.Morphs is null || model.RigidBodies is null || model.Morphs.Any(m => m is null))
            throw new ArgumentException("Impulse evaluation requires morph definitions and rigid bodies.", nameof(model));
        groupWeights = new(model);
        supported = groupWeights.Supported(type => type == PmxMorphType.Impulse);
        definitions = new PmxMorph.ImpulseOffset[model.Morphs.Count][];
        var names = new Dictionary<string, int>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var diagnostics = new List<string>();
        for (int i = 0; i < definitions.Length; i++)
        {
            var morph = model.Morphs[i];
            bool first = seen.Add(morph.Name);
            if (supported[i] && first) names.Add(morph.Name, i);
            if (supported[i] && !first)
                diagnostics.Add($"Duplicate PMX morph name '{morph.Name}': only the first PMX record can bind by name.");
            definitions[i] = morph.Type == PmxMorphType.Impulse
                ? (morph.ImpulseOffsets ?? throw new ArgumentException("Impulse offsets cannot be null.", nameof(model))).ToArray() : [];
            foreach (var offset in definitions[i])
                if ((uint)offset.RigidBodyIndex >= (uint)model.RigidBodies.Count
                    || !Finite(offset.TranslationVelocity) || !Finite(offset.RotationTorque))
                    throw new ArgumentException($"PMX impulse morph '{morph.Name}' has an invalid rigid body index or non-finite impulse.", nameof(model));
        }
        SupportedMorphIndices = new ReadOnlyDictionary<string, int>(names);
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
    }

    public IReadOnlyList<PmxRigidBodyImpulse> Evaluate(IReadOnlyDictionary<int, float> weights)
    {
        var contributions = groupWeights.Expand(weights, supported);
        var result = new List<PmxRigidBodyImpulse>();
        foreach (var contribution in contributions)
        {
            foreach (var offset in definitions[contribution.Index])
            {
                if (result.Count == ImpulseBudget)
                    throw new ArgumentException($"PMX impulse sample exceeds the {ImpulseBudget} command budget.", nameof(weights));
                var velocity = offset.TranslationVelocity * contribution.Weight;
                var torque = offset.RotationTorque * contribution.Weight;
                if (!Finite(velocity) || !Finite(torque))
                    throw new ArgumentException("PMX impulse sampling produced a non-finite impulse.", nameof(weights));
                result.Add(new(offset.RigidBodyIndex, offset.Local, velocity, torque));
            }
        }
        return Array.AsReadOnly(result.ToArray());
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
