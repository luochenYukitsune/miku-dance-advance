namespace Mmd.Modern.Animation;

/// <summary>PMX loader classification and sparse layer passes; original stored-index order is retained.</summary>
internal sealed class MmdBoneSchedule
{
    internal bool[] RebuildAfterIk { get; }
    internal bool[] IsIkLink { get; }
    internal int[][] BeforePhysics { get; }
    internal int[][] AfterPhysics { get; }

    internal MmdBoneSchedule(IReadOnlyList<RigBone> bones)
    {
        // A9AB0 assigns one type per bone, in stored controller order. A later
        // controller, effector or link replaces an earlier assignment.
        const byte controllerType = 2, linkType = 4, effectorType = 6;
        var types = new byte[bones.Count];
        for (int i = 0; i < bones.Count; i++)
        {
            if (bones[i].Ik is not { } ik) continue;
            types[i] = controllerType;
            types[ik.TargetBoneIndex] = effectorType;
            foreach (var link in ik.Links) types[link.BoneIndex] = linkType;
        }
        IsIkLink = types.Select(type => type == linkType).ToArray();
        RebuildAfterIk = new bool[bones.Count];
        var layers = bones.Select(bone => bone.DeformLayer).ToArray();
        // Original PMX loader A9AB0:1398-1442 walks stored indices once, not a graph sort.
        // +264 marks append-from-IK recipients and their descendants, not IK links themselves.
        for (int i = 0; i < bones.Count; i++)
        {
            var bone = bones[i];
            int parent = bone.ParentIndex;
            if (parent >= 0 && RebuildAfterIk[parent]
                || bone.Append is { } append && IsIkLink[append.SourceBoneIndex])
                RebuildAfterIk[i] = true;
            else if (parent >= 0)
            {
                layers[i] = Math.Max(layers[i], layers[parent]);
                if ((types[parent] is linkType or effectorType) && types[i] is not (linkType or effectorType))
                {
                    if (layers[i] == int.MaxValue)
                        throw new ArgumentException("PMX effective deformation layer overflows.", nameof(bones));
                    layers[i]++;
                }
            }
        }
        // Empty numeric layers do no work. Avoid looping to hostile sparse int.MaxValue depths.
        int[][] Phase(bool after) => Enumerable.Range(0, bones.Count)
            .Where(i => layers[i] >= 0 && bones[i].AfterPhysics == after)
            .GroupBy(i => layers[i]).OrderBy(group => group.Key).Select(group => group.ToArray()).ToArray();
        BeforePhysics = Phase(false);
        AfterPhysics = Phase(true);
    }
}
