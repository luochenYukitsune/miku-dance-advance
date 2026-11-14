// PMM link and interpolation semantics verified against nanoem/ext/document.c
// @30acffaa29f5d2eb9e997d69418f2e4b97b5894f (MIT, hkrn).
using Mmd.Modern.Formats;
using System.Text;

namespace Mmd.Modern.Application;

/// <summary>Named track compatibility for an explicit replacement model; source order is never a fallback.</summary>
public sealed record PmmModelTrackBinding(int MatchedBones, int TotalBones, int MatchedMorphs, int TotalMorphs,
    IReadOnlyList<string> UnmatchedBones, IReadOnlyList<string> UnmatchedMorphs);

/// <summary>Converts embedded PMM motion to the existing named VMD/domain import boundary.</summary>
public static class PmmMotionAdapter
{
    public static PmmModelTrackBinding InspectBinding(PmmModel source, PmxModel model)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(model);
        var bones = PmmNameResolver.ResolveTable(source.BoneNames, model.Bones.Select(b => b.Name), "bone");
        var morphs = PmmNameResolver.ResolveTable(source.MorphNames, model.Morphs.Select(m => m.Name), "morph");
        return new(bones.Count(n => n.BoundName is not null), bones.Count, morphs.Count(n => n.BoundName is not null), morphs.Count,
            Array.AsReadOnly(bones.Where(n => n.BoundName is null).Select(n => n.DisplayName).ToArray()),
            Array.AsReadOnly(morphs.Where(n => n.BoundName is null).Select(n => n.DisplayName).ToArray()));
    }

    public static VmdMotion Convert(PmmModel source, PmxModel model)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(model);
        var diagnostics = new List<string>();
        string?[] boneNames = ResolveNames(source.BoneNames, model.Bones.Select(b => b.Name), "bone", diagnostics);
        string?[] morphNames = ResolveNames(source.MorphNames, model.Morphs.Select(m => m.Name), "morph", diagnostics);
        var bones = new List<VmdBoneKeyframe>();
        foreach (var (key, track) in ResolveTracks(source.InitialBoneKeyframes, source.BoneKeyframes, source.BoneNames.Count, "bone"))
        {
            if (boneNames[track] is not string name) continue;
            bones.Add(new() { BoneName = name, OriginalName = OriginalName(source.BoneNames[track], name), Frame = Frame(key), Translation = key.Translation, Rotation = key.Rotation,
                Interpolation = ConvertBoneInterpolation(key.Interpolation) });
        }
        var morphs = new List<VmdMorphKeyframe>();
        foreach (var (key, track) in ResolveTracks(source.InitialMorphKeyframes, source.MorphKeyframes, source.MorphNames.Count, "morph"))
            if (morphNames[track] is string name) morphs.Add(new() { MorphName = name, OriginalName = OriginalName(source.MorphNames[track], name), Frame = Frame(key), Weight = key.Weight });
        var ik = new List<VmdIkKeyframe>();
        foreach (var (key, _) in ResolveTracks([source.InitialModelKeyframe], source.ModelKeyframes, 1, "model"))
        {
            if (key.ConstraintEnabled.Count != source.ConstraintBoneIndices.Count)
                throw new InvalidDataException("PMM model keyframe constraint count differs from its constraint bone table.");
            ik.Add(new() { Frame = Frame(key), Visible = key.Visible,
                Switches = ConstraintSwitches(source, boneNames, key.ConstraintEnabled).ToArray() });
        }
        if (source.InitialBoneKeyframes.Concat(source.BoneKeyframes).Any(k => k.PhysicsDisabled))
            diagnostics.Add("PMM per-key bone physics disable flags are retained in project metadata; the current character runtime does not execute them.");
        AddMetadataDiagnostics(source, diagnostics);
        EnsureAnyMatchingTrack(source, boneNames, morphNames);
        return new() { ModelName = model.Name, BoneKeyframes = bones.AsReadOnly(), MorphKeyframes = morphs.AsReadOnly(), IkKeyframes = ik.AsReadOnly(), Diagnostics = diagnostics.AsReadOnly() };
    }

    /// <summary>Restores saved, possibly unregistered editor values without changing registered timeline keys.</summary>
    public static VmdMotion ConvertSavedPose(PmmModel source, PmxModel model)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(model);
        var diagnostics = new List<string>();
        string?[] boneNames = ResolveNames(source.BoneNames, model.Bones.Select(b => b.Name), "bone", diagnostics);
        string?[] morphNames = ResolveNames(source.MorphNames, model.Morphs.Select(m => m.Name), "morph", diagnostics);
        if (source.BoneStates.Count != boneNames.Length || source.MorphStates.Count != morphNames.Length || source.ConstraintStates.Count != source.ConstraintBoneIndices.Count)
            throw new InvalidDataException("PMM saved state counts differ from the model name/constraint tables.");
        byte[] linear = ConvertBoneInterpolation([20, 20, 107, 107, 20, 20, 107, 107, 20, 20, 107, 107, 20, 20, 107, 107]);
        var bones = source.BoneStates.Select((state, i) => (state, name: boneNames[i], raw: source.BoneNames[i])).Where(p => p.name is not null)
            .Select(p => new VmdBoneKeyframe { BoneName = p.name!, OriginalName = OriginalName(p.raw, p.name!), Frame = 0, Translation = p.state.Translation, Rotation = p.state.Rotation, Interpolation = linear }).ToArray();
        var morphs = source.MorphStates.Select((state, i) => (state, name: morphNames[i], raw: source.MorphNames[i])).Where(p => p.name is not null)
            .Select(p => new VmdMorphKeyframe { MorphName = p.name!, OriginalName = OriginalName(p.raw, p.name!), Frame = 0, Weight = p.state.Weight }).ToArray();
        var ik = new VmdIkKeyframe { Frame = 0, Visible = source.Visible,
            Switches = ConstraintSwitches(source, boneNames, source.ConstraintStates.Select(s => s.Enabled).ToArray()).ToArray() };
        diagnostics.Add("PMM saved editor pose is restored as a constant pose; registered project motion is retained separately for seek/playback.");
        if (source.BoneStates.Any(s => s.PhysicsDisabled))
            diagnostics.Add("PMM saved per-bone physics disable state is retained as metadata; it is not executed by the current character runtime.");
        AddMetadataDiagnostics(source, diagnostics);
        EnsureAnyMatchingTrack(source, boneNames, morphNames);
        return new() { ModelName = model.Name, BoneKeyframes = Array.AsReadOnly(bones), MorphKeyframes = Array.AsReadOnly(morphs), IkKeyframes = [ik], Diagnostics = diagnostics.AsReadOnly() };
    }

    /// <summary>PMM has four contiguous xyxy curves. Existing MMD playback reads each VMD redundant block.</summary>
    public static byte[] ConvertBoneInterpolation(IReadOnlyList<byte> pmm)
    {
        ArgumentNullException.ThrowIfNull(pmm);
        if (pmm.Count != 16 || pmm.Any(b => b > 127)) throw new InvalidDataException("PMM bone interpolation requires 16 control bytes in [0,127].");
        var canonical = new byte[16];
        for (int channel = 0; channel < 4; channel++)
            for (int coordinate = 0; coordinate < 4; coordinate++)
                canonical[4 * coordinate + channel] = pmm[4 * channel + coordinate];
        // VMD stores the matrix plus three progressively shifted redundant blocks.
        var result = new byte[64];
        for (int block = 0; block < 4; block++)
            for (int i = 0; i < 16 - block; i++) result[16 * block + i] = canonical[i + block];
        return result;
    }

    private static uint Frame(PmmKeyframe key)
        => key.Frame >= 0 ? (uint)key.Frame : throw new InvalidDataException($"PMM keyframe {key.RecordIndex} has a negative frame.");

    private static void AddMetadataDiagnostics(PmmModel source, List<string> diagnostics)
    {
        if (source.OutsideParentSubjectBoneIndices.Count > 0 || source.OutsideParentStates.Count > 0 || source.InitialModelKeyframe.OutsideParents.Count > 0 || source.ModelKeyframes.Any(k => k.OutsideParents.Count > 0))
            diagnostics.Add("PMM outside-parent bindings are retained in project metadata; outside-parent transforms are not executed yet.");
        diagnostics.Add("PMM model visibility is retained in the converted model/IK track and must be applied by the project scene host.");
    }

    private static void EnsureAnyMatchingTrack(PmmModel source, string?[] bones, string?[] morphs)
    {
        if (source.BoneNames.Count + source.MorphNames.Count > 0 && !bones.Concat(morphs).Any(n => n is not null))
            throw new InvalidDataException("PMM model has no bone or morph names matching this PMX. Check model path and ANSI encoding; index fallback is forbidden.");
    }

    private static IEnumerable<VmdIkSwitch> ConstraintSwitches(PmmModel source, string?[] names, IReadOnlyList<bool> enabled)
    {
        for (int i = 0; i < source.ConstraintBoneIndices.Count; i++)
        {
            int index = source.ConstraintBoneIndices[i];
            if ((uint)index >= (uint)names.Length) throw new InvalidDataException($"PMM constraint bone index {index} is outside the PMM name table.");
            if (names[index] is string name) yield return new(name, enabled[i]) { OriginalName = OriginalName(source.BoneNames[index], name) };
        }
    }

    private static VmdEncodedName? OriginalName(PmmString source, string name)
    {
        byte[] bytes = source.RawBytes.TakeWhile(b => b != 0).ToArray();
        // Longer PMM names remain in project metadata; they cannot fit a VMD name field.
        // Do not make a formerly importable PMM fail merely because it cannot be exported as VMD.
        if (bytes.Length > 20) return null;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        foreach (int page in new[] { 932, 936 })
        {
            try
            {
                var encoding = LegacyAnsiEncoding.Create(page);
                if (encoding.GetString(bytes) == name) return new(name, bytes, page);
            }
            catch (DecoderFallbackException) { }
        }
        return null;
    }

    private static string?[] ResolveNames(IReadOnlyList<PmmString> source, IEnumerable<string> target, string kind, List<string> diagnostics)
        => PmmNameResolver.ResolveTable(source, target, kind, diagnostics).Select(binding => binding.BoundName).ToArray();

    internal static IReadOnlyList<(T Key, int Track)> ResolveTracks<T>(IReadOnlyList<T> initial, IReadOnlyList<T> additional, int trackCount, string kind) where T : PmmKeyframe
    {
        if (initial.Count != trackCount) throw new InvalidDataException($"PMM {kind} initial key count differs from its name table.");
        T[] all = initial.Concat(additional).ToArray();
        var byId = new Dictionary<int, int>();
        for (int i = 0; i < all.Length; i++)
            if (all[i].RecordIndex != i || all[i].ObjectIndex < 0 || !byId.TryAdd(all[i].ObjectIndex, i))
                throw new InvalidDataException($"PMM {kind} has an invalid physical record position or duplicate keyframe ID at record {i}.");
        var owners = Enumerable.Repeat(-1, all.Length).ToArray();
        for (int track = 0; track < initial.Count; track++)
        {
            // Initial ID zero's PreviousIndex may contain a stale no-link-sentinel write.
            // Initial slot ownership and forward links are authoritative; never follow that previous value.
            int current = track;
            while (true)
            {
                if (owners[current] != -1) throw new InvalidDataException($"PMM {kind} keyframe links cycle or cross tracks at record {current}.");
                owners[current] = track;
                T key = all[current];
                _ = Frame(key);
                if (key.NextIndex == 0) break;
                int nextId = key.NextIndex;
                if (!byId.TryGetValue(nextId, out int next) || next < initial.Count || all[next].PreviousIndex != key.ObjectIndex || all[next].Frame < key.Frame)
                    throw new InvalidDataException($"PMM {kind} keyframe link ID {key.ObjectIndex}->{nextId} is invalid or inconsistent.");
                current = next;
            }
        }
        if (owners.Any(owner => owner < 0)) throw new InvalidDataException($"PMM {kind} contains unlinked additional keyframes; ownership cannot safely be inferred from record order.");
        return all.Select((key, i) => (key, owners[i])).ToArray();
    }
}
