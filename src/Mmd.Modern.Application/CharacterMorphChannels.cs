namespace Mmd.Modern.Application;

/// <summary>Stable, renderer-independent extra PMX channels from the last successful character sample.</summary>
public sealed record CharacterMorphChannels(IReadOnlyList<PmxAdditionalUvVertex> AdditionalUvs,
    IReadOnlyList<PmxMaterialTextureFactors> TextureFactors)
{
    /// <summary>Current weighted, unapplied commands. Physics-off sampling still exposes this stable snapshot.</summary>
    public IReadOnlyList<PmxRigidBodyImpulse> Impulses { get; init; } = Array.Empty<PmxRigidBodyImpulse>();
}

public sealed partial class SceneAnimationSession
{
    public CharacterMorphChannels GetMorphChannels(Guid objectId)
    {
        RequireHealthy();
        return (entries[Index(objectId)].Controller ?? throw new InvalidOperationException("Bind a motion before querying morph channels.")).MorphChannels;
    }
}
